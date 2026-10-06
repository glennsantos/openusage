using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Cursor;

/// Maps the connect-RPC `GetCurrentPeriodUsage` response (plus credit grants, Stripe balance and Grok Bot status)
/// into metric lines. Mirrors Swift `CursorUsageMapper`.
public static class CursorUsageMapper
{
    public const string NoActiveSubscription = "No active Cursor subscription.";
    public const string LimitMissing = "Total usage limit missing from API response.";
    public const string RequestBasedUnavailable = "Cursor request-based usage data unavailable. Try again later.";

    /// Billing-cycle reset and length from epoch-ms `billingCycleStart` / `billingCycleEnd`.
    public static (DateTimeOffset? ResetsAt, long PeriodMs) Cycle(JsonObject usage)
    {
        var start = Parse.Number(usage["billingCycleStart"]);
        var end = Parse.Number(usage["billingCycleEnd"]);
        DateTimeOffset? resetsAt = end is { } e ? DateTimeOffset.FromUnixTimeMilliseconds((long)e) : null;
        var period = start is { } s && end is { } en && en > s ? (long)(en - s) : MetricPeriod.MonthMs;
        return (resetsAt, period);
    }

    public static double? Spent(JsonNode? planUsage)
    {
        if (Parse.Number(planUsage?["totalSpend"]) is { } spend) return spend;
        return Parse.Number(planUsage?["limit"]) is { } limit ? limit - (Parse.Number(planUsage?["remaining"]) ?? limit) : null;
    }

    public static bool HasModelPools(JsonNode? planUsage)
    {
        if (Parse.Number(planUsage?["autoPercentUsed"]) is not { } auto || auto < 0) return false;
        if (Parse.Number(planUsage?["apiPercentUsed"]) is not { } api || api < 0) return false;
        return auto > 0 || api > 0 || Spent(planUsage) == 0;
    }

    public static bool IsTeamByShape(JsonObject usage)
    {
        var spend = usage["spendLimitUsage"];
        return string.Equals(Parse.String(spend?["limitType"]), "team", StringComparison.OrdinalIgnoreCase)
            || Parse.Number(spend?["pooledLimit"]) > 0;
    }

    /// True when the plan-usage block can't be shown and the cookie-auth usage-summary endpoint should be used instead.
    public static bool ShouldUseSummaryFallback(JsonObject usage, string? plan)
    {
        if (Parse.Bool(usage["enabled"]) == false) return false;
        var planUsage = usage["planUsage"];
        var hasLimit = Parse.Number(planUsage?["limit"]) != null;
        var hasTotal = Parse.Number(planUsage?["totalPercentUsed"]) != null;
        var pools = HasModelPools(planUsage);
        var unusable = planUsage == null || (!hasLimit && !pools);
        var normalized = plan?.Trim().ToLowerInvariant() ?? "";
        if (unusable && normalized is "enterprise" or "team") return true;
        if (unusable && !hasTotal && normalized.Length == 0) return true;
        // Swift tries `/api/usage` alone here before failing with "limit missing"; the summary path covers that endpoint too.
        if (planUsage != null && !hasLimit && !hasTotal && !pools) return true;
        return IsTeamByShape(usage) && !hasLimit && !pools;
    }

    public static List<MetricLine> Map(JsonObject usage, string? plan, JsonObject? creditGrants, double stripeCreditCents)
    {
        var planUsage = usage["planUsage"] as JsonObject;
        if (Parse.Bool(usage["enabled"]) == false || planUsage == null) throw new ProviderException(NoActiveSubscription);

        var limit = Parse.Number(planUsage["limit"]);
        var totalPercent = Parse.Number(planUsage["totalPercentUsed"]);
        var pools = HasModelPools(planUsage);
        if (limit == null && totalPercent == null && !pools) throw new ProviderException(LimitMissing);

        var lines = new List<MetricLine>();
        AddCredits(lines, creditGrants, stripeCreditCents);

        var (resetsAt, period) = Cycle(usage);
        var isTeam = string.Equals(plan?.Trim(), "team", StringComparison.OrdinalIgnoreCase) || IsTeamByShape(usage);
        var usedCents = Spent(planUsage) ?? 0;
        if (isTeam && pools)
        {
            if (totalPercent is { } t)
                lines.Add(new MetricLine.Progress("Total usage", t, 100, ProgressFormat.PercentFormat, resetsAt, period));
        }
        else if (isTeam)
        {
            if (limit is not { } l) throw new ProviderException(RequestBasedUnavailable);
            lines.Add(new MetricLine.Progress("Total usage", usedCents / 100, l / 100, ProgressFormat.DollarsFormat, resetsAt, period));
        }
        else
        {
            var computed = limit is > 0 ? usedCents / limit.Value * 100 : 0;
            lines.Add(new MetricLine.Progress("Total usage", totalPercent ?? computed, 100, ProgressFormat.PercentFormat, resetsAt, period));
        }

        if (Parse.Number(planUsage["autoPercentUsed"]) is { } auto)
            lines.Add(new MetricLine.Progress("Cursor Models", auto, 100, ProgressFormat.PercentFormat, resetsAt, period));
        if (Parse.Number(planUsage["apiPercentUsed"]) is { } api)
            lines.Add(new MetricLine.Progress("Other Models", api, 100, ProgressFormat.PercentFormat, resetsAt, period));

        AddOnDemand(lines, usage["spendLimitUsage"]);
        return lines;
    }

    private static void AddCredits(List<MetricLine> lines, JsonObject? grants, double stripeCreditCents)
    {
        var valid = Parse.Bool(grants?["hasCreditGrants"]) == true
            && Parse.Number(grants?["totalCents"]) > 0
            && Parse.Number(grants?["usedCents"]) >= 0;
        var grantTotal = valid ? Parse.Number(grants!["totalCents"])!.Value : 0;
        var grantUsed = valid ? Parse.Number(grants!["usedCents"])!.Value : 0;
        var combined = grantTotal + stripeCreditCents;
        if (combined <= 0) return;
        var remaining = Math.Max(0, combined - grantUsed);
        lines.Add(new MetricLine.Values("Credits", new[] { new MetricValue(Math.Round(remaining) / 100, MetricKind.Dollars) }));
    }

    private static void AddOnDemand(List<MetricLine> lines, JsonNode? spend)
    {
        if (spend == null) return;
        var limit = Parse.Number(spend["individualLimit"]) ?? Parse.Number(spend["pooledLimit"]) ?? 0;
        var remaining = Parse.Number(spend["individualRemaining"]) ?? Parse.Number(spend["pooledRemaining"]) ?? 0;
        var reported = new[] { "individualUsed", "pooledUsed", "totalSpend" }.Select(k => Parse.Number(spend[k])).ToList();
        var spent = reported.FirstOrDefault(v => v > 0)
            ?? (limit - remaining > 0 ? limit - remaining : reported.FirstOrDefault(v => v != null) ?? 0);

        if (limit > 0)
            lines.Add(new MetricLine.Progress("On-demand", spent / 100, limit / 100, ProgressFormat.DollarsFormat));
        else if (spent > 0)
            lines.Add(new MetricLine.Values("On-demand", new[] { new MetricValue(spent / 100, MetricKind.Dollars) }));
    }

    /// Stripe `customerBalance` is negative when the customer holds credit.
    public static double StripeCreditCents(JsonObject? stripe) =>
        Parse.Number(stripe?["customerBalance"]) is { } b && b < 0 ? Math.Abs(b) : 0;

    public static MetricLine? MapGrokBot(JsonObject? status)
    {
        if (status == null
            || Parse.Bool(status["usesPooledEnterpriseAllowance"]) == true
            || Parse.Bool(status["hasNonZeroIncludedLimit"]) == false
            || Parse.Bool(status["includedLimitZero"]) == true
            || Parse.Number(status["usagePercent"]) is not { } percent || percent < 0)
            return null;
        var reset = Parse.IsoDate(Parse.String(status["nextResetTimestampUtc"]));
        var start = Parse.IsoDate(Parse.String(status["currentPeriodStart"]));
        var period = reset is { } r && start is { } s && r > s ? (long)(r - s).TotalMilliseconds : MetricPeriod.WeekMs;
        return new MetricLine.Progress("Grok Bot usage", Math.Clamp(percent, 0, 100), 100, ProgressFormat.PercentFormat, reset, period);
    }

    public static string? FormatPlan(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Parse.TitleCase(name.Trim(), ' ');
}
