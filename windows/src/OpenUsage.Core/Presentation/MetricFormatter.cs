using System.Globalization;
using OpenUsage.Core.Models;

namespace OpenUsage.Core.Presentation;

/// Display strings for metric lines, kept out of the UI layer so they can be unit tested.
public static class MetricFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Dollars(double value) => value.ToString("$#,0.00", Culture);

    public static string Number(double value) =>
        value == Math.Floor(value) ? value.ToString("#,0", Culture) : value.ToString("#,0.#", Culture);

    /// "42%", "$12.50 / $20.00", "120 / 500 requests".
    public static string ProgressValue(MetricLine.Progress line) => line.Format switch
    {
        ProgressFormat.Dollars => $"{Dollars(line.Used)} / {Dollars(line.Limit)}",
        ProgressFormat.Count c => $"{Number(line.Used)} / {Number(line.Limit)} {c.Suffix}",
        _ => $"{Math.Round(Fraction(line) * 100):0}%",
    };

    public static double Fraction(MetricLine.Progress line) =>
        line.Limit <= 0 ? 0 : Math.Clamp(line.Used / line.Limit, 0, 1);

    /// "$32.84 · 821 credits", "2 available".
    public static string Values(MetricLine.Values line) =>
        string.Join(" · ", line.Items.Select(v =>
        {
            var number = v.Kind switch
            {
                MetricKind.Dollars => Dollars(v.Number),
                MetricKind.Percent => $"{Math.Round(v.Number):0}%",
                _ => Number(v.Number),
            };
            return v.Label == null ? number : $"{number} {v.Label}";
        }));

    /// "Resets in 2h 13m", "Resets in 3d 4h", "Resets now".
    public static string? ResetsIn(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } at) return null;
        var span = at - now;
        if (span <= TimeSpan.Zero) return "Resets now";
        if (span.TotalDays >= 1) return $"Resets in {(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"Resets in {(int)span.TotalHours}h {span.Minutes}m";
        return $"Resets in {Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    /// Compact tray-tooltip value for a pinned line, e.g. "42%".
    public static string Compact(MetricLine line) => line switch
    {
        MetricLine.Progress { Format: ProgressFormat.Percent } p => $"{Math.Round(p.Used):0}%",
        MetricLine.Progress p => ProgressValue(p),
        MetricLine.Values v => Values(v),
        MetricLine.Text t => t.Value,
        MetricLine.Badge b => b.BadgeText,
        _ => "",
    };
}
