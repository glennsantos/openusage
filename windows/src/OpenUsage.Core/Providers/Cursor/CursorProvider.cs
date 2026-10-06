using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed class CursorProvider : IProviderRuntime
{
    private const string Api = "https://api2.cursor.sh/aiserver.v1.DashboardService/";
    private const string RefreshUrl = "https://api2.cursor.sh/oauth/token";
    private const string ClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
    private const string NotLoggedIn = "Not logged in. Sign in via Cursor app or run `agent login`.";
    private const string SessionExpired = "Session expired. Sign in via Cursor app or run `agent login`.";
    private const string TokenExpired = "Token expired. Sign in via Cursor app or run `agent login`.";

    private readonly HttpClient _http;
    private readonly CursorAuthStore _store;

    public CursorProvider(HttpClient http, CursorAuthStore? store = null)
    {
        _http = http;
        _store = store ?? new CursorAuthStore();
    }

    public ProviderInfo Provider { get; } = new("cursor", "Cursor",
        "https://status.cursor.com/", "https://www.cursor.com/dashboard");

    public IReadOnlyList<WidgetDescriptor> Widgets { get; } = new WidgetDescriptor[]
    {
        new("cursor.usage", "Total usage", true, false),
        new("cursor.auto", "Cursor Models", true, true),
        new("cursor.api", "Other Models", true, true),
        new("cursor.grokBot", "Grok Bot usage", true, false),
        new("cursor.onDemand", "On-demand", true, false),
        new("cursor.requests", "Requests", false, false),
        new("cursor.credits", "Credits", false, false),
    };

    public bool HasLocalCredentials()
    {
        try { return _store.Load() != null; }
        catch (SqliteException e)
        {
            Log.Warn($"cursor: credential probe failed: {e.Message}");
            return false;
        }
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            return await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        catch (ProviderException e)
        {
            Log.Warn($"cursor: {e.Message}");
            return ProviderSnapshot.Failed(Provider, e.Message);
        }
        catch (SqliteException e)
        {
            Log.Error("cursor: reading state.vscdb failed", e);
            return ProviderSnapshot.Failed(Provider, "Couldn't read Cursor's login database. See log for details.");
        }
    }

    private async Task<ProviderSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        var auth = _store.Load() ?? throw new ProviderException(NotLoggedIn);
        var token = auth.AccessToken;
        var now = DateTimeOffset.Now;
        if (CursorAuth.NeedsRefresh(token, now))
        {
            try { token = await RotateAsync(auth.RefreshToken, ct).ConfigureAwait(false) ?? token; }
            catch (ProviderException) when (token != null) { /* keep the old token; the usage call decides */ }
        }
        if (token == null) throw new ProviderException(NotLoggedIn);

        var result = await Http.WithAuthRetryAsync(
            token,
            t => { token = t; return Http.SendAsync(_http, Rpc("GetCurrentPeriodUsage", t), TimeSpan.FromSeconds(10), ct); },
            () => RotateAsync(auth.RefreshToken, ct),
            TokenExpired).ConfigureAwait(false);
        if (!result.IsSuccess) throw new ProviderException(Http.RequestFailed(result.Code));
        var usage = Parse.Object(result.Body) ?? throw new ProviderException(Http.InvalidResponse);

        var planName = Parse.String((await OptionalRpcAsync("GetPlanInfo", token, ct))?["planInfo"]?["planName"]);
        var grokBot = CursorUsageMapper.MapGrokBot(await OptionalRpcAsync("GetSandUsageStatus", token, ct));

        List<MetricLine> lines;
        string? plan;
        if (CursorUsageMapper.ShouldUseSummaryFallback(usage, planName))
        {
            var session = CursorAuth.Session(token);
            var summary = await OptionalCookieAsync("https://cursor.com/api/usage-summary", session?.Cookie, ct);
            var requests = session is { } s
                ? await OptionalCookieAsync($"https://cursor.com/api/usage?user={Uri.EscapeDataString(s.UserId)}", s.Cookie, ct)
                : null;
            lines = CursorSummaryMapper.Map(summary, requests);
            plan = CursorSummaryMapper.Plan(planName, summary);
        }
        else
        {
            var grants = ValidGrants(await OptionalRpcAsync("GetCreditGrantsBalance", token, ct));
            var stripe = await OptionalCookieAsync("https://cursor.com/api/auth/stripe", CursorAuth.Session(token)?.Cookie, ct);
            lines = CursorUsageMapper.Map(usage, planName, grants, CursorUsageMapper.StripeCreditCents(stripe));
            plan = CursorUsageMapper.FormatPlan(planName);
        }
        if (grokBot != null) lines.Add(grokBot);
        return new ProviderSnapshot(Provider, plan, lines, now);
    }

    private static JsonObject? ValidGrants(JsonObject? grants)
    {
        if (grants == null || Parse.Bool(grants["hasCreditGrants"]) is not { } has) return null;
        if (has && !(Parse.Number(grants["totalCents"]) > 0 && Parse.Number(grants["usedCents"]) >= 0)) return null;
        return grants;
    }

    private static HttpRequestMessage Rpc(string method, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Api + method)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        return request;
    }

    /// Optional endpoints never fail the refresh: errors are logged and the data is skipped.
    private async Task<JsonObject?> OptionalRpcAsync(string method, string token, CancellationToken ct) =>
        await OptionalAsync(method, () => Rpc(method, token), ct).ConfigureAwait(false);

    private async Task<JsonObject?> OptionalCookieAsync(string url, string? cookie, CancellationToken ct)
    {
        if (cookie == null) return null;
        return await OptionalAsync(url, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookie}");
            return request;
        }, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject?> OptionalAsync(string name, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        try
        {
            var result = await Http.SendAsync(_http, build(), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (result.IsSuccess && Parse.Object(result.Body) is { } body) return body;
            Log.Warn($"cursor: optional {name} returned HTTP {result.Code}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"cursor: optional {name} failed: {e.Message}");
        }
        return null;
    }

    /// Returns the new access token, or null when no refresh is possible. Cursor doesn't rotate the refresh token.
    private async Task<string?> RotateAsync(string? refreshToken, CancellationToken ct)
    {
        if (refreshToken == null) return null;
        var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl)
        {
            Content = JsonContent.Create(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
            }),
        };
        HttpResult result;
        try { result = await Http.SendAsync(_http, request, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"cursor: token refresh failed: {e.Message}");
            return null;
        }

        var body = Parse.Object(result.Body);
        var logout = Parse.Bool(body?["shouldLogout"]) == true;
        if (result.Code is 400 or 401) throw new ProviderException(logout ? SessionExpired : TokenExpired);
        if (!result.IsSuccess || body == null) return null;
        if (logout) throw new ProviderException(SessionExpired);
        if (Parse.String(body["access_token"]) is not { } access) return null;

        try { _store.SaveAccessToken(access); }
        catch (SqliteException e) { Log.Error("cursor: couldn't persist refreshed token; using it for this session only", e); }
        return access;
    }
}
