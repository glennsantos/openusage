using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public sealed class ClaudeProvider : IProviderRuntime
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1";
    private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string RefreshScopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    // The usage endpoint only returns the reset-grants block to clients it recognizes as Claude Code.
    private const string UserAgent = "claude-cli/2.1.280 (external, cli)";
    private const string TokenExpired = "Token expired. Run `claude` to log in again.";
    private const string SessionExpired = "Session expired. Run `claude` to log in again.";
    private const string MissingScope = "Re-login for live usage. Run `claude` and sign in again to restore session and weekly limits.";

    private readonly HttpClient _http;
    private readonly ClaudeAuthStore _store;
    private DateTimeOffset? _rateLimitedUntil;
    private (IReadOnlyList<MetricLine> Lines, string? Plan)? _lastGood;

    public ClaudeProvider(HttpClient http, ClaudeAuthStore? store = null)
    {
        _http = http;
        _store = store ?? new ClaudeAuthStore();
    }

    public ProviderInfo Provider { get; } = new("claude", "Claude",
        "https://status.anthropic.com/", "https://claude.ai/settings/usage");

    public IReadOnlyList<WidgetDescriptor> Widgets { get; } = new WidgetDescriptor[]
    {
        new("claude.session", "Session", true, true),
        new("claude.weekly", "Weekly", true, true),
        new("claude.sonnet", "Sonnet", false, false),
        new("claude.fable", "Fable", true, false),
        new("claude.extra", "Extra usage spent", true, false),
        new("claude.rateLimitResets", "Rate Limit Resets", true, false),
    };

    public bool HasLocalCredentials() => _store.Load() != null;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            return await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        catch (ProviderException e)
        {
            Log.Warn($"claude: {e.Message}");
            return ProviderSnapshot.Failed(Provider, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("claude: credential file access failed", e);
            return ProviderSnapshot.Failed(Provider, "Couldn't read Claude credentials. See log for details.");
        }
    }

    private async Task<ProviderSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        var credential = _store.Load() ?? throw new ProviderException(ClaudeAuthStore.NotLoggedIn);
        var plan = ClaudeUsageMapper.FormatPlan(credential.SubscriptionType, credential.RateLimitTier);
        if (!credential.HasProfileScope)
            return new ProviderSnapshot(Provider, plan, Array.Empty<MetricLine>(), DateTimeOffset.Now, Error: MissingScope);

        var now = DateTimeOffset.Now;
        if (_rateLimitedUntil is { } until && now < until) return RateLimitedSnapshot(plan, until, now);

        if (credential.NeedsRefresh(now) && credential.RefreshToken != null)
            credential = await RotateAsync(credential, ct).ConfigureAwait(false);

        var current = credential;
        var result = await Http.WithAuthRetryAsync(
            current.AccessToken,
            token => Http.SendAsync(_http, UsageRequest(token), TimeSpan.FromSeconds(10), ct),
            async () =>
            {
                if (current.RefreshToken == null) return null;
                current = await RotateAsync(current, ct).ConfigureAwait(false);
                return current.AccessToken;
            },
            TokenExpired).ConfigureAwait(false);

        if (result.Code == 429)
        {
            var retryAfter = ClaudeUsageMapper.RetryAfterSeconds(result.Headers.RetryAfter, now);
            _rateLimitedUntil = now.AddSeconds(retryAfter);
            Log.Warn($"claude: rate limited for {retryAfter}s");
            return RateLimitedSnapshot(plan, _rateLimitedUntil.Value, now);
        }
        if (!result.IsSuccess) throw new ProviderException(Http.RequestFailed(result.Code));
        var body = Parse.Object(result.Body) ?? throw new ProviderException(Http.InvalidResponse);

        _rateLimitedUntil = null;
        var lines = ClaudeUsageMapper.Map(body, now);
        _lastGood = (lines, plan);
        return new ProviderSnapshot(Provider, plan, lines, now);
    }

    private ProviderSnapshot RateLimitedSnapshot(string? plan, DateTimeOffset until, DateTimeOffset now)
    {
        var retry = ClaudeUsageMapper.RetryText(until, now);
        var warning = $"Updates blocked by Anthropic. Be patient — manual refreshes will make it worse. Retrying in {retry}.";
        if (_lastGood is { } good)
        {
            var lines = good.Lines.Append(new MetricLine.Text("Note", $"Live usage rate limited - retry in {retry}")).ToList();
            return new ProviderSnapshot(Provider, good.Plan ?? plan, lines, now, warning);
        }
        return new ProviderSnapshot(Provider, plan,
            new MetricLine[] { new MetricLine.Badge("Status", $"Rate limited, retry in {retry}", "#F59E0B") }, now, warning);
    }

    private static HttpRequestMessage UsageRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private async Task<ClaudeCredential> RotateAsync(ClaudeCredential credential, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl)
        {
            Content = JsonContent.Create(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.RefreshToken!,
                ["client_id"] = ClientId,
                ["scope"] = RefreshScopes,
            }),
        };
        HttpResult result;
        try { result = await Http.SendAsync(_http, request, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException(Http.ConnectionFailed);
        }

        var body = Parse.Object(result.Body);
        if (result.Code is 400 or 401 &&
            (Parse.String(body?["error"]) == "invalid_grant" || Parse.String(body?["error_description"]) == "invalid_grant"))
            throw new ProviderException(SessionExpired, allowsAuthFallback: true);
        if (!result.IsSuccess) throw new ProviderException(Http.RequestFailed(result.Code));
        var access = Parse.String(body?["access_token"]) ?? throw new ProviderException(Http.InvalidResponse);

        var expiresIn = Parse.Number(body?["expires_in"]);
        var rotated = credential with
        {
            AccessToken = access,
            RefreshToken = Parse.String(body?["refresh_token"]) ?? credential.RefreshToken,
            ExpiresAtMs = expiresIn is { } s ? DateTimeOffset.Now.ToUnixTimeMilliseconds() + (long)(s * 1000) : credential.ExpiresAtMs,
        };
        try
        {
            _store.SaveRotated(credential, rotated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("claude: couldn't persist refreshed token; using it for this session only", e);
        }
        return rotated;
    }
}
