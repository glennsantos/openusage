namespace OpenUsage.Core.Models;

public enum MetricKind { Percent, Dollars, Count }

public sealed record MetricValue(double Number, MetricKind Kind, string? Label = null);

/// How a progress line renders its used/limit values. Mirrors the Swift `ProgressFormat`.
public abstract record ProgressFormat
{
    public sealed record Percent : ProgressFormat;
    public sealed record Dollars : ProgressFormat;
    public sealed record Count(string Suffix) : ProgressFormat;

    public static readonly ProgressFormat PercentFormat = new Percent();
    public static readonly ProgressFormat DollarsFormat = new Dollars();
}

/// Normalized usage row a provider emits. Mirrors the Swift `MetricLine` enum.
public abstract record MetricLine(string Label)
{
    public sealed record Text(string Label, string Value, string? ColorHex = null) : MetricLine(Label);

    public sealed record Values(
        string Label,
        IReadOnlyList<MetricValue> Items,
        IReadOnlyList<DateTimeOffset>? ExpiriesAt = null) : MetricLine(Label);

    public sealed record Progress(
        string Label,
        double Used,
        double Limit,
        ProgressFormat Format,
        DateTimeOffset? ResetsAt = null,
        long? PeriodDurationMs = null) : MetricLine(Label);

    public sealed record Badge(string Label, string BadgeText, string? ColorHex = null) : MetricLine(Label);
}

public static class MetricPeriod
{
    public const long HourMs = 3_600_000;
    public const long DayMs = 24 * HourMs;
    public const long SessionMs = 5 * HourMs;
    public const long WeekMs = 7 * DayMs;
    public const long MonthMs = 30 * DayMs;
}
