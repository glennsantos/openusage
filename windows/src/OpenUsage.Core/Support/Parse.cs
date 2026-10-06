using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenUsage.Core.Support;

/// Lenient JSON readers shared by provider mappers. Mirrors Swift `ProviderParse`.
public static class Parse
{
    /// JSON number or numeric string; booleans and non-finite values are rejected.
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out _)) return null;
        if (value.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (value.TryGetValue<string>(out var s) &&
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed))
            return parsed;
        return null;
    }

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// Trimmed non-empty string, else null.
    public static string? String(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    public static JsonObject? Object(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// Parses JSON, falling back to hex-encoded JSON (optional "0x" prefix) as stored by some credential stores.
    public static JsonObject? ObjectWithHexFallback(string text)
    {
        var direct = Object(text);
        if (direct != null) return direct;
        var hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return null;
        try { return Object(Encoding.UTF8.GetString(Convert.FromHexString(hex))); }
        catch (FormatException) { return null; }
    }

    /// Decodes a JWT payload without verifying the signature.
    public static JsonObject? JwtPayload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return Object(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
        catch (FormatException) { return null; }
    }

    /// ISO-8601 string (lenient: space for "T", " UTC" suffix), or epoch seconds / milliseconds.
    public static DateTimeOffset? Date(JsonNode? node)
    {
        if (Number(node) is { } n)
            return n < 1e10 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(n * 1000)) : DateTimeOffset.FromUnixTimeMilliseconds((long)n);
        return IsoDate(String(node));
    }

    public static DateTimeOffset? IsoDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.EndsWith(" UTC", StringComparison.Ordinal)) s = s[..^4] + "Z";
        s = s.Replace(' ', 'T');
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// "pro_lite" -> "Pro Lite".
    public static string TitleCase(string value, char separator = '_') =>
        string.Join(' ', value.Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
