using Microsoft.Data.Sqlite;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

public sealed record CursorAuth(string? AccessToken, string? RefreshToken)
{
    /// True when the token is missing, has no readable `exp`, or expires within five minutes.
    public static bool NeedsRefresh(string? token, DateTimeOffset now) =>
        Parse.Number(Parse.JwtPayload(token)?["exp"]) is not { } exp || exp - now.ToUnixTimeSeconds() <= 300;

    /// `WorkosCursorSessionToken` cookie value: "<userId>%3A%3A<accessToken>", where userId is the part of the JWT
    /// `sub` after "|" (e.g. "auth0|user_abc" → "user_abc").
    public static (string UserId, string Cookie)? Session(string? token)
    {
        if (token == null || Parse.String(Parse.JwtPayload(token)?["sub"]) is not { } sub) return null;
        var parts = sub.Split('|');
        var userId = parts.Length >= 2 ? parts[1] : parts[0];
        return userId.Length == 0 ? null : (userId, $"{userId}%3A%3A{token}");
    }
}

/// Reads Cursor's login from the editor's `state.vscdb` (a SQLite key/value store shared with VS Code).
public sealed class CursorAuthStore
{
    private const string AccessKey = "cursorAuth/accessToken";
    private const string RefreshKey = "cursorAuth/refreshToken";

    private readonly string _dbPath;

    public CursorAuthStore(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");
    }

    public CursorAuth? Load()
    {
        if (!File.Exists(_dbPath)) return null;
        using var connection = Open(SqliteOpenMode.ReadOnly);
        var access = Read(connection, AccessKey);
        var refresh = Read(connection, RefreshKey);
        return access == null && refresh == null ? null : new CursorAuth(access, refresh);
    }

    public void SaveAccessToken(string token)
    {
        using var connection = Open(SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", AccessKey);
        command.Parameters.AddWithValue("$value", token);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        // Cursor keeps the database open (WAL mode); a short busy timeout rides out its writes.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? Read(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        var value = (command.ExecuteScalar() as string)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
