using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed class CodexProvider : IProviderRuntime
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const string RefreshUrl = "https://auth.openai.com/oauth/token";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    private readonly HttpClient _http;
    private readonly CodexAuthStore _store;

    public CodexProvider(HttpClient http, CodexAuthStore? store = null)
    {
        _http = http;
        _store = store ?? new CodexAuthStore();
    }

    public ProviderInfo Provider { get; } = new("codex", "Codex",
        "https://status.openai.com/", "https://chatgpt.com/codex/settings/usage");

    public IReadOnlyList<WidgetDescriptor> Widgets { get; } = new WidgetDescriptor[]
    {
        new("codex.session", "Session", true, true),
        new("codex.weekly", "Weekly", true, true),
        new("codex.spark", "Spark", true, false),
        new("codex.sparkWeekly", "Spark Weekly", true, false),
        new("codex.credits", "Credits", true, false),
        new("codex.rateLimitResets", "Rate Limit Resets", true, false),
    };

    public bool HasLocalCredentials() => _store.LoadCandidates().Any(c => c.AccessToken != null);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            ProviderException? fallbackError = null;
            foreach (var candidate in _store.LoadCandidates())
            {
                try
                {
                    return await ProbeAsync(candidate, ct).ConfigureAwait(false);
                }
                catch (ProviderException e) when (e.AllowsAuthFallback)
                {
                    Log.Warn($"codex: {e.Message} ({candidate.Path}); trying next login");
                    fallbackError = e;
                }
            }
            throw fallbackError ?? new ProviderException(CodexErrors.NotLoggedIn);
        }
        catch (ProviderException e)
        {
            Log.Warn($"codex: {e.Message}");
            return ProviderSnapshot.Failed(Provider, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("codex: auth file access failed", e);
            return ProviderSnapshot.Failed(Provider, "Couldn't read Codex credentials. See log for details.");
        }
    }

    private async Task<ProviderSnapshot> ProbeAsync(CodexCredential credential, CancellationToken ct)
    {
        if (credential.AccessToken == null)
            throw new ProviderException(credential.ApiKey != null ? CodexErrors.ApiKeyOnly : CodexErrors.NotLoggedIn);

        var now = DateTimeOffset.Now;
        if (credential.NeedsRefresh(now))
        {
            // The CLI may already have rotated the token on disk; adopt it before spending a refresh.
            if (_store.Load(credential.Path) is { AccessToken: not null } live) credential = live;
            if (credential.NeedsRefresh(now) && credential.RefreshToken != null)
                credential = await RotateAsync(credential, ct).ConfigureAwait(false);
        }

        var current = credential;
        var result = await Http.WithAuthRetryAsync(
            current.AccessToken!,
            token => Http.SendAsync(_http, Request(UsageUrl, token, current.AccountId, codexHeaders: false),
                TimeSpan.FromSeconds(10), ct),
            async () =>
            {
                if (current.RefreshToken == null) return null;
                current = await RotateAsync(current, ct).ConfigureAwait(false);
                return current.AccessToken;
            },
            CodexErrors.TokenExpired).ConfigureAwait(false);

        if (!result.IsSuccess) throw new ProviderException(Http.RequestFailed(result.Code));
        var body = Parse.Object(result.Body) ?? throw new ProviderException(Http.InvalidResponse);

        var headers = new CodexHeaderFallbacks(
            HeaderNumber(result.Headers, "x-codex-primary-used-percent"),
            HeaderNumber(result.Headers, "x-codex-secondary-used-percent"),
            HeaderNumber(result.Headers, "x-codex-credits-balance"));
        var resetCredits = await FetchResetCreditsAsync(current, ct).ConfigureAwait(false);
        var lines = CodexUsageMapper.Map(body, headers, resetCredits, now);
        return new ProviderSnapshot(Provider, CodexUsageMapper.FormatPlan(Parse.String(body["plan_type"])), lines, now);
    }

    /// Best-effort: any failure falls back to the count embedded in the usage body.
    private async Task<JsonObject?> FetchResetCreditsAsync(CodexCredential credential, CancellationToken ct)
    {
        try
        {
            var result = await Http.SendAsync(_http, Request(ResetCreditsUrl, credential.AccessToken!, credential.AccountId, codexHeaders: true),
                TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return result.IsSuccess ? Parse.Object(result.Body) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"codex: reset-credits fetch failed: {e.Message}");
            return null;
        }
    }

    private static HttpRequestMessage Request(string url, string token, string? accountId, bool codexHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "OpenUsage");
        if (!string.IsNullOrEmpty(accountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        if (codexHeaders)
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");
        }
        return request;
    }

    private static double? HeaderNumber(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) &&
        double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private async Task<CodexCredential> RotateAsync(CodexCredential credential, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = credential.RefreshToken!,
            }),
        };
        HttpResult result;
        try { result = await Http.SendAsync(_http, request, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException(Http.ConnectionFailed);
        }

        var body = Parse.Object(result.Body);
        if (result.Code is 400 or 401)
        {
            var error = body?["error"];
            var code = Parse.String(error) ?? Parse.String(error?["code"]) ?? Parse.String(error?["error"]) ?? Parse.String(body?["code"]);
            throw code switch
            {
                "refresh_token_expired" => new ProviderException(CodexErrors.SessionExpired, allowsAuthFallback: true),
                "refresh_token_reused" => new ProviderException(CodexErrors.TokenConflict, allowsAuthFallback: true),
                "refresh_token_invalidated" => new ProviderException(CodexErrors.TokenRevoked, allowsAuthFallback: true),
                _ => new ProviderException(Http.RequestFailed(result.Code)),
            };
        }
        if (!result.IsSuccess) throw new ProviderException(Http.RequestFailed(result.Code));
        var access = Parse.String(body?["access_token"])
            ?? throw new ProviderException(CodexErrors.TokenExpired, allowsAuthFallback: true);
        var refresh = Parse.String(body?["refresh_token"]);
        var idToken = Parse.String(body?["id_token"]);

        try
        {
            _store.SaveRotated(credential, access, refresh, idToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("codex: couldn't persist refreshed token; using it for this session only", e);
        }
        return credential with
        {
            AccessToken = access,
            RefreshToken = refresh ?? credential.RefreshToken,
            IdToken = idToken ?? credential.IdToken,
            LastRefresh = DateTimeOffset.UtcNow,
        };
    }
}
