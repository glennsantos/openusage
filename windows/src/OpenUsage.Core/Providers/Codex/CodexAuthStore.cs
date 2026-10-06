using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

public sealed record CodexCredential(
    string Path,
    string? AccessToken,
    string? RefreshToken,
    string? IdToken,
    string? AccountId,
    string? ApiKey,
    DateTimeOffset? LastRefresh)
{
    /// Refresh within five minutes of JWT `exp`; without `exp`, after eight days since `last_refresh`.
    public bool NeedsRefresh(DateTimeOffset now)
    {
        if (Parse.Number(Parse.JwtPayload(AccessToken)?["exp"]) is { } exp)
            return exp - now.ToUnixTimeSeconds() <= 300;
        return LastRefresh is { } last && now - last > TimeSpan.FromDays(8);
    }
}

/// Reads `auth.json` from each Codex home. Codex CLI on Windows keeps its login in this file.
public sealed class CodexAuthStore
{
    private readonly IReadOnlyList<string> _homes;

    public CodexAuthStore(IReadOnlyList<string>? homes = null)
    {
        _homes = homes ?? (Http.Env("CODEX_HOME") is { } home
            ? new[] { home }
            : new[] { System.IO.Path.Combine(Http.Home, ".config", "codex"), System.IO.Path.Combine(Http.Home, ".codex") });
    }

    /// Every home's credential that carries an access token or API key, in search order, de-duplicated by path.
    public List<CodexCredential> LoadCandidates() =>
        _homes
            .Select(h => System.IO.Path.GetFullPath(System.IO.Path.Combine(h.TrimEnd('/', '\\'), "auth.json")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .OfType<CodexCredential>()
            .Where(c => c.AccessToken != null || c.ApiKey != null)
            .ToList();

    public CodexCredential? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return Decode(path, Parse.ObjectWithHexFallback(File.ReadAllText(path)));
    }

    public static CodexCredential? Decode(string path, JsonObject? doc)
    {
        if (doc == null) return null;
        var tokens = doc["tokens"];
        return new CodexCredential(
            path,
            Parse.String(tokens?["access_token"]),
            Parse.String(tokens?["refresh_token"]),
            Parse.String(tokens?["id_token"]),
            Parse.String(tokens?["account_id"]),
            Parse.String(doc["OPENAI_API_KEY"]),
            Parse.IsoDate(Parse.String(doc["last_refresh"])));
    }

    /// Writes rotated tokens only if the file still holds the credential we loaded (the CLI may have rotated it first).
    public void SaveRotated(CodexCredential loaded, string access, string? refresh, string? idToken)
    {
        var doc = Parse.ObjectWithHexFallback(File.ReadAllText(loaded.Path));
        if (Decode(loaded.Path, doc) != loaded) throw new ProviderException(CodexErrors.TokenConflict, allowsAuthFallback: true);

        if (doc!["tokens"] is not JsonObject tokens) doc["tokens"] = tokens = new JsonObject();
        tokens["access_token"] = access;
        if (refresh != null) tokens["refresh_token"] = refresh;
        if (idToken != null) tokens["id_token"] = idToken;
        doc["last_refresh"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var tmp = loaded.Path + ".openusage.tmp";
        File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, loaded.Path, overwrite: true);
    }
}

public static class CodexErrors
{
    public const string NotLoggedIn = "Not logged in. Run `codex` to authenticate.";
    public const string SessionExpired = "Session expired. Run `codex` to log in again.";
    public const string TokenConflict = "Token conflict. Run `codex` to log in again.";
    public const string TokenRevoked = "Token revoked. Run `codex` to log in again.";
    public const string TokenExpired = "Token expired. Run `codex` to log in again.";
    public const string ApiKeyOnly = "Usage not available for API key.";
}
