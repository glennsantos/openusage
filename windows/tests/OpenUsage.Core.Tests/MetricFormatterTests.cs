using OpenUsage.Core.Models;
using OpenUsage.Core.Presentation;

namespace OpenUsage.Core.Tests;

public class MetricFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");

    [Fact]
    public void FormatsProgressValues()
    {
        Assert.Equal("42%", MetricFormatter.ProgressValue(new MetricLine.Progress("S", 42.4, 100, ProgressFormat.PercentFormat)));
        Assert.Equal("$12.50 / $20.00", MetricFormatter.ProgressValue(new MetricLine.Progress("D", 12.5, 20, ProgressFormat.DollarsFormat)));
        Assert.Equal("120 / 500 requests",
            MetricFormatter.ProgressValue(new MetricLine.Progress("R", 120, 500, new ProgressFormat.Count("requests"))));
    }

    [Fact]
    public void FormatsValues()
    {
        var line = new MetricLine.Values("Credits", new[]
        {
            new MetricValue(32.84, MetricKind.Dollars),
            new MetricValue(821, MetricKind.Count, "credits"),
        });
        Assert.Equal("$32.84 · 821 credits", MetricFormatter.Values(line));
    }

    [Theory]
    [InlineData(-5, "Resets now")]
    [InlineData(0.5, "Resets in 1m")]
    [InlineData(133, "Resets in 2h 13m")]
    [InlineData(4560, "Resets in 3d 4h")]
    public void FormatsResetCountdown(double minutes, string expected) =>
        Assert.Equal(expected, MetricFormatter.ResetsIn(Now.AddMinutes(minutes), Now));
}
