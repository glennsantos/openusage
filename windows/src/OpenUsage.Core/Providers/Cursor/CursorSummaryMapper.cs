using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

/// Fallback mapping for team, enterprise and request-based plans, from the cookie-auth `cursor.com/api/usage-summary`
/// and `cursor.com/api/usage` endpoints. Mirrors Swift `CursorUsageSummaryMapper`.
public static class CursorSummaryMapper
{
    public static List<MetricLine> Map(JsonObject? summary, JsonObject? requests)
    {
        var (resetsAt, period) = Cycle(summary, requests);
        var lines = new List<MetricLine>();

        var gpt4 = requests?["gpt-4"];
        if (Parse.Number(gpt4?["maxRequestUsage"]) is { } max && max > 0)
        {
            var used = Math.Max(0, Parse.Number(gpt4?["numRequests"]) ?? Parse.Number(gpt4?["numRequestsTotal"]) ?? 0);
            var format = new ProgressFormat.Count("requests");
            lines.Add(new MetricLine.Progress("Total usage", used, max, format, resetsAt, period));
            lines.Add(new MetricLine.Progress("Requests", used, max, format, resetsAt, period));
        }
        else
        {
            AddSummaryTotal(lines, summary, resetsAt, period);
        }

        var plan = summary?["individualUsage"]?["plan"];
        if (Parse.Number(plan?["autoPercentUsed"]) is { } auto)
            lines.Add(new MetricLine.Progress("Cursor Models", auto, 100, ProgressFormat.PercentFormat, resetsAt, period));
        if (Parse.Number(plan?["apiPercentUsed"]) is { } api)
            lines.Add(new MetricLine.Progress("Other Models", api, 100, ProgressFormat.PercentFormat, resetsAt, period));

        var onDemand = summary?["individualUsage"]?["onDemand"] ?? summary?["teamUsage"]?["onDemand"];
        if (onDemand != null && Parse.Bool(onDemand["enabled"]) != false)
        {
            if (Meter(onDemand) is { } m)
                lines.Add(new MetricLine.Progress("On-demand", m.Used / 100, m.Limit / 100, ProgressFormat.DollarsFormat));
            else if (Parse.Number(onDemand["used"]) is { } spent && spent > 0)
                lines.Add(new MetricLine.Values("On-demand", new[] { new MetricValue(spent / 100, MetricKind.Dollars) }));
        }

        if (lines.Count == 0) throw new ProviderException(CursorUsageMapper.RequestBasedUnavailable);
        return lines;
    }

    private static void AddSummaryTotal(List<MetricLine> lines, JsonObject? summary, DateTimeOffset? resetsAt, long period)
    {
        var pooled = summary?["teamUsage"]?["pooled"];
        var isTeam = string.Equals(Parse.String(summary?["limitType"]), "team", StringComparison.OrdinalIgnoreCase);
        if (isTeam && Meter(pooled) is { } team)
            lines.Add(Dollars(team, resetsAt, period));
        else if (Parse.Number(summary?["individualUsage"]?["plan"]?["totalPercentUsed"]) is { } percent)
            lines.Add(new MetricLine.Progress("Total usage", percent, 100, ProgressFormat.PercentFormat, resetsAt, period));
        else if (Meter(summary?["individualUsage"]?["overall"]) is { } overall)
            lines.Add(Dollars(overall, resetsAt, period));
        else if (Meter(pooled) is { } pool)
            lines.Add(Dollars(pool, resetsAt, period));
    }

    private static MetricLine Dollars((double Used, double Limit) m, DateTimeOffset? resetsAt, long period) =>
        new MetricLine.Progress("Total usage", m.Used / 100, m.Limit / 100, ProgressFormat.DollarsFormat, resetsAt, period);

    /// A bucket `{enabled, limit, used, remaining}` in cents is a meter when enabled with a positive limit.
    private static (double Used, double Limit)? Meter(JsonNode? bucket)
    {
        if (bucket == null || Parse.Bool(bucket["enabled"]) == false) return null;
        if (Parse.Number(bucket["limit"]) is not { } limit || limit <= 0) return null;
        var used = Parse.Number(bucket["used"]) is { } u && u > 0
            ? u
            : Math.Max(0, limit - (Parse.Number(bucket["remaining"]) ?? limit));
        return (used, limit);
    }

    private static (DateTimeOffset? ResetsAt, long PeriodMs) Cycle(JsonObject? summary, JsonObject? requests)
    {
        var start = Parse.IsoDate(Parse.String(summary?["billingCycleStart"]));
        var end = Parse.IsoDate(Parse.String(summary?["billingCycleEnd"]));
        if (start is { } s && end is { } e && e > s) return (e, (long)(e - s).TotalMilliseconds);
        var month = Parse.IsoDate(Parse.String(requests?["startOfMonth"]));
        return (month?.AddMilliseconds(MetricPeriod.MonthMs), MetricPeriod.MonthMs);
    }

    public static string? Plan(string? planName, JsonObject? summary) =>
        CursorUsageMapper.FormatPlan(planName) ?? CursorUsageMapper.FormatPlan(Parse.String(summary?["membershipType"]));
}
