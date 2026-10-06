using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

/// Usage-response headers the mapper falls back to when the body omits a value.
public sealed record CodexHeaderFallbacks(double? PrimaryPercent, double? SecondaryPercent, double? CreditsBalance);

/// Maps `GET /backend-api/wham/usage` into metric lines. Mirrors Swift `CodexUsageMapper`.
public static class CodexUsageMapper
{
    public const double CreditUsdRate = 0.04;

    private sealed record Window(JsonNode? Node, double? Percent, long? DurationMs);

    public static List<MetricLine> Map(JsonObject body, CodexHeaderFallbacks headers, JsonObject? resetCredits, DateTimeOffset now)
    {
        var lines = new List<MetricLine>();
        var rateLimit = body["rate_limit"];
        AddWindows(lines, rateLimit, "Session", "Weekly", headers.PrimaryPercent, headers.SecondaryPercent, now);

        var spark = (body["additional_rate_limits"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e =>
            (Parse.String(e["limit_name"]) ?? "").Contains("spark", StringComparison.OrdinalIgnoreCase) ||
            (Parse.String(e["metered_feature"]) ?? "").Contains("spark", StringComparison.OrdinalIgnoreCase));
        if (spark != null) AddWindows(lines, spark["rate_limit"], "Spark", "Spark Weekly", null, null, now);

        AddResetCredits(lines, resetCredits, body["rate_limit_reset_credits"] as JsonObject);
        AddCredits(lines, body["credits"], headers.CreditsBalance);
        return lines;
    }

    /// Classifies primary/secondary windows by duration (5h → session, 7d → weekly), falling back to slot position.
    private static void AddWindows(List<MetricLine> lines, JsonNode? rateLimit, string sessionLabel, string weeklyLabel,
        double? primaryHeader, double? secondaryHeader, DateTimeOffset now)
    {
        var primary = ReadWindow(rateLimit?["primary_window"], primaryHeader);
        var secondary = ReadWindow(rateLimit?["secondary_window"], secondaryHeader);
        var slots = new[] { primary, secondary };

        Window? session = slots.FirstOrDefault(w => w?.DurationMs == MetricPeriod.SessionMs);
        Window? weekly = slots.FirstOrDefault(w => w?.DurationMs == MetricPeriod.WeekMs);
        bool Unclassified(Window? w) => w != null && w.DurationMs != MetricPeriod.SessionMs && w.DurationMs != MetricPeriod.WeekMs;
        session ??= Unclassified(primary) ? primary : null;
        weekly ??= Unclassified(secondary) ? secondary : null;

        AddWindow(lines, sessionLabel, session, MetricPeriod.SessionMs, now);
        AddWindow(lines, weeklyLabel, weekly, MetricPeriod.WeekMs, now);
    }

    private static Window? ReadWindow(JsonNode? node, double? headerPercent)
    {
        if (node == null && headerPercent == null) return null;
        var seconds = Parse.Number(node?["limit_window_seconds"]);
        return new Window(node, Parse.Number(node?["used_percent"]) ?? headerPercent, seconds is { } s ? (long)(s * 1000) : null);
    }

    private static void AddWindow(List<MetricLine> lines, string label, Window? window, long defaultPeriodMs, DateTimeOffset now)
    {
        if (window?.Percent is not { } used) return;
        DateTimeOffset? resetsAt = Parse.Number(window.Node?["reset_at"]) is { } at
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(at * 1000))
            : Parse.Number(window.Node?["reset_after_seconds"]) is { } after ? now.AddSeconds(after) : null;
        lines.Add(new MetricLine.Progress(label, used, 100, ProgressFormat.PercentFormat, resetsAt,
            window.DurationMs ?? defaultPeriodMs));
    }

    private static void AddResetCredits(List<MetricLine> lines, JsonObject? preferred, JsonObject? fromBody)
    {
        var source = Parse.Number(preferred?["available_count"]) != null ? preferred : fromBody;
        if (Parse.Number(source?["available_count"]) is not { } count || count < 0) return;
        var expiries = (source!["credits"] as JsonArray)?.OfType<JsonObject>()
            .Where(c => Parse.String(c["status"]) is null or "available")
            .Select(c => Parse.Date(c["expires_at"]))
            .OfType<DateTimeOffset>()
            .Order()
            .ToList() ?? new List<DateTimeOffset>();
        lines.Add(new MetricLine.Values("Rate Limit Resets",
            new[] { new MetricValue(Math.Floor(count), MetricKind.Count, "available") }, expiries));
    }

    private static void AddCredits(List<MetricLine> lines, JsonNode? credits, double? headerBalance)
    {
        var balance = Parse.Number(credits?["balance"])
            ?? (Parse.Bool(credits?["has_credits"]) == false ? 0 : headerBalance);
        if (balance is not { } b) return;
        var count = Math.Max(0, Math.Floor(b));
        lines.Add(new MetricLine.Values("Credits", new[]
        {
            new MetricValue(count * CreditUsdRate, MetricKind.Dollars),
            new MetricValue(count, MetricKind.Count, "credits"),
        }));
    }

    public static string? FormatPlan(string? planType)
    {
        var plan = planType?.Trim();
        if (string.IsNullOrEmpty(plan)) return null;
        return plan.ToLowerInvariant() switch
        {
            "prolite" => "Pro 100",
            "pro" => "Pro 200",
            "promax" => "Pro 500",
            "self_serve_business_prolite" => "Business Premium",
            _ => Parse.TitleCase(plan),
        };
    }
}
