using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

public sealed record ClaudeCredential(
    string AccessToken,
    string? RefreshToken,
    long? ExpiresAtMs,
    string? SubscriptionType,
    string? RateLimitTier,
    IReadOnlyList<string> Scopes)
{
    public const string ProfileScope = "user:profile";

    /// Within five minutes of expiry. A credential without `expiresAt` never needs a proactive refresh.
    public bool NeedsRefresh(DateTimeOffset now) =>
        ExpiresAtMs is { } exp && exp - now.ToUnixTimeMilliseconds() <= 300_000;

    public bool HasProfileScope => Scopes.Count == 0 || Scopes.Contains(ProfileScope);
}

/// Reads and writes Claude Code's `.credentials.json`. On Windows (and Linux) Claude Code keeps its OAuth login in
/// this file rather than an OS keychain, so it is the only stored source the Windows port needs.
public sealed class ClaudeAuthStore
{
    public const string NotLoggedIn = "Not logged in. Run `claude` to authenticate.";
    public const string LoginChanged = "Claude login changed during refresh. Refresh again.";

    private readonly string _credentialsPath;

    public ClaudeAuthStore(string? configDir = null)
    {
        var dir = configDir ?? Http.Env("CLAUDE_CONFIG_DIR") ?? Path.Combine(Http.Home, ".claude");
        _credentialsPath = Path.Combine(dir, ".credentials.json");
    }

    public string CredentialsPath => _credentialsPath;

    public ClaudeCredential? Load() => Decode(ReadDocument());

    public static ClaudeCredential? Decode(JsonObject? document)
    {
        if (document?["claudeAiOauth"] is not JsonObject oauth) return null;
        var access = Parse.String(oauth["accessToken"]);
        if (access == null) return null;
        var scopes = oauth["scopes"] is JsonArray arr
            ? arr.Select(Parse.String).OfType<string>().ToList()
            : new List<string>();
        return new ClaudeCredential(
            access,
            Parse.String(oauth["refreshToken"]),
            Parse.Number(oauth["expiresAt"]) is { } exp ? (long)exp : null,
            Parse.String(oauth["subscriptionType"]),
            Parse.String(oauth["rateLimitTier"]),
            scopes);
    }

    /// Writes the rotated tokens into the freshly read document, leaving every other field (MCP logins, etc.) untouched.
    /// Refuses to write if the stored login changed since `loaded` was read.
    public void SaveRotated(ClaudeCredential loaded, ClaudeCredential rotated)
    {
        var document = ReadDocument();
        var current = Decode(document);
        if (document == null || current == null ||
            current.AccessToken != loaded.AccessToken || current.RefreshToken != loaded.RefreshToken)
            throw new ProviderException(LoginChanged);

        var oauth = (JsonObject)document["claudeAiOauth"]!;
        oauth["accessToken"] = rotated.AccessToken;
        if (rotated.RefreshToken != null) oauth["refreshToken"] = rotated.RefreshToken;
        if (rotated.ExpiresAtMs != null) oauth["expiresAt"] = rotated.ExpiresAtMs;

        var tmp = _credentialsPath + ".openusage.tmp";
        File.WriteAllText(tmp, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _credentialsPath, overwrite: true);
    }

    private JsonObject? ReadDocument()
    {
        if (!File.Exists(_credentialsPath)) return null;
        return Parse.ObjectWithHexFallback(File.ReadAllText(_credentialsPath));
    }
}
