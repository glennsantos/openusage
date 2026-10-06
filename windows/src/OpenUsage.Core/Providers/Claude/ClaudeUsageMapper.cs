using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Claude;

/// Maps `GET /api/oauth/usage` into metric lines. Mirrors Swift `ClaudeUsageMapper`.
public static class ClaudeUsageMapper
{
    public static List<MetricLine> Map(JsonObject body, DateTimeOffset now)
    {
        var lines = new List<MetricLine>();
        AddWindow(lines, "Session", body["five_hour"], MetricPeriod.SessionMs);
        AddWindow(lines, "Weekly", body["seven_day"], MetricPeriod.WeekMs);
        AddWindow(lines, "Sonnet", body["seven_day_sonnet"], MetricPeriod.WeekMs);
        AddScopedWeekly(lines, body["limits"], "Fable");
        AddExtraUsage(lines, body["extra_usage"]);
        AddResetGrants(lines, body["cedar_ember"], now);
        return lines;
    }

    private static void AddWindow(List<MetricLine> lines, string label, JsonNode? window, long periodMs)
    {
        if (Parse.Number(window?["utilization"]) is not { } used) return;
        lines.Add(new MetricLine.Progress(label, used, 100, ProgressFormat.PercentFormat,
            Parse.Date(window?["resets_at"]), periodMs));
    }

    private static void AddScopedWeekly(List<MetricLine> lines, JsonNode? limits, string model)
    {
        if (limits is not JsonArray arr) return;
        var match = arr.OfType<JsonObject>().FirstOrDefault(l =>
            Parse.String(l["kind"]) == "weekly_scoped" &&
            Parse.String(l["scope"]?["model"]?["display_name"]) == model);
        if (match == null || Parse.Number(match["percent"]) is not { } used) return;
        lines.Add(new MetricLine.Progress(model, used, 100, ProgressFormat.PercentFormat,
            Parse.Date(match["resets_at"]), MetricPeriod.WeekMs));
    }

    private static void AddExtraUsage(List<MetricLine> lines, JsonNode? extra)
    {
        if (Parse.Bool(extra?["is_enabled"]) != true || Parse.Number(extra?["used_credits"]) is not { } usedCents) return;
        var used = Math.Round(usedCents) / 100;
        if (Parse.Number(extra?["monthly_limit"]) is { } limitCents && limitCents > 0)
            lines.Add(new MetricLine.Progress("Extra usage spent", used, Math.Round(limitCents) / 100, ProgressFormat.DollarsFormat));
        else if (used > 0)
            lines.Add(new MetricLine.Values("Extra usage spent", new[] { new MetricValue(used, MetricKind.Dollars) }));
    }

    private static void AddResetGrants(List<MetricLine> lines, JsonNode? block, DateTimeOffset now)
    {
        if (block is not JsonObject cedar) return;
        var count = 0;
        var expiries = new List<DateTimeOffset>();
        if (Parse.Bool(cedar["eligible"]) == true && cedar["grants"] is JsonArray grants)
        {
            foreach (var grant in grants.OfType<JsonObject>())
            {
                if (Parse.Number(grant["resets_left"]) is not { } left || left < 1) continue;
                if (Parse.Date(grant["ends_at"]) is not { } ends || ends <= now) continue;
                var n = (int)Math.Floor(left);
                count += n;
                expiries.AddRange(Enumerable.Repeat(ends, n));
            }
        }
        expiries.Sort();
        lines.Add(new MetricLine.Values("Rate Limit Resets",
            new[] { new MetricValue(count, MetricKind.Count, "available") }, expiries));
    }

    /// "max" + "default_claude_max_5x" -> "Max 5x".
    public static string? FormatPlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType)) return null;
        var s = subscriptionType.Trim();
        var plan = char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
        if (rateLimitTier != null && Regex.Match(rateLimitTier, @"\d+x") is { Success: true } m)
            plan += " " + m.Value;
        return plan;
    }

    /// Seconds to wait after a 429: `Retry-After` as seconds or an HTTP date, else five minutes.
    public static double RetryAfterSeconds(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta?.TotalSeconds
        ?? (header?.Date is { } date ? Math.Max(0, Math.Ceiling((date - now).TotalSeconds)) : 300);

    public static string RetryText(DateTimeOffset until, DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((until - now).TotalMinutes);
        return minutes <= 0 ? "now" : $"~{minutes}m";
    }
}
