using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Claude;

namespace OpenUsage.Core.Tests;

public class ClaudeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");

    [Fact]
    public void MapsSessionWeeklyFableExtraAndResets()
    {
        var body = JsonNode.Parse("""
        {
          "five_hour": {"utilization": 42, "resets_at": "2026-10-06T14:00:00Z"},
          "seven_day": {"utilization": "18.5", "resets_at": 1791720000},
          "limits": [{"kind": "weekly_scoped", "scope": {"model": {"display_name": "Fable"}}, "percent": 7}],
          "extra_usage": {"is_enabled": true, "used_credits": 1250, "monthly_limit": 5000},
          "cedar_ember": {"eligible": true, "grants": [
            {"resets_left": 2, "ends_at": "2026-10-10T00:00:00Z"},
            {"resets_left": 1, "ends_at": "2026-10-01T00:00:00Z"}
          ]}
        }
        """)!.AsObject();

        var lines = ClaudeUsageMapper.Map(body, Now);

        var session = Assert.IsType<MetricLine.Progress>(lines[0]);
        Assert.Equal(("Session", 42d, MetricPeriod.SessionMs), (session.Label, session.Used, session.PeriodDurationMs!.Value));
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T14:00:00Z"), session.ResetsAt);
        var weekly = Assert.IsType<MetricLine.Progress>(lines[1]);
        Assert.Equal(18.5, weekly.Used);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791720000), weekly.ResetsAt);
        Assert.Equal("Fable", lines[2].Label);
        var extra = Assert.IsType<MetricLine.Progress>(lines[3]);
        Assert.Equal((12.5, 50d), (extra.Used, extra.Limit));
        var resets = Assert.IsType<MetricLine.Values>(lines[4]);
        Assert.Equal(2, resets.Items[0].Number); // the expired grant is skipped
        Assert.Equal(2, resets.ExpiriesAt!.Count);
    }

    [Fact]
    public void ExtraUsageWithoutLimitIsAValuesLine()
    {
        var body = JsonNode.Parse("""{"extra_usage": {"is_enabled": true, "used_credits": 300}}""")!.AsObject();
        var line = Assert.IsType<MetricLine.Values>(Assert.Single(ClaudeUsageMapper.Map(body, Now)));
        Assert.Equal(3, line.Items[0].Number);
    }

    [Fact]
    public void IneligibleResetGrantsShowZero()
    {
        var body = JsonNode.Parse("""{"cedar_ember": {"eligible": false, "ineligible_reason": "surface"}}""")!.AsObject();
        var line = Assert.IsType<MetricLine.Values>(Assert.Single(ClaudeUsageMapper.Map(body, Now)));
        Assert.Equal(0, line.Items[0].Number);
    }

    [Theory]
    [InlineData("max", "default_claude_max_5x", "Max 5x")]
    [InlineData("PRO", null, "Pro")]
    [InlineData(" ", null, null)]
    public void FormatsPlan(string subscription, string? tier, string? expected) =>
        Assert.Equal(expected, ClaudeUsageMapper.FormatPlan(subscription, tier));

    [Fact]
    public void RetryAfterDefaultsToFiveMinutes()
    {
        Assert.Equal(300, ClaudeUsageMapper.RetryAfterSeconds(null, Now));
        Assert.Equal(90, ClaudeUsageMapper.RetryAfterSeconds(new RetryConditionHeaderValue(TimeSpan.FromSeconds(90)), Now));
        Assert.Equal(120, ClaudeUsageMapper.RetryAfterSeconds(new RetryConditionHeaderValue(Now.AddMinutes(2)), Now));
    }

    [Fact]
    public void AuthStoreReadsFileAndPreservesOtherFieldsOnSave()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, ".credentials.json");
        File.WriteAllText(path, """
        {"mcpOAuth": {"keep": true},
         "claudeAiOauth": {"accessToken": "a1", "refreshToken": "r1", "expiresAt": 1000, "scopes": ["user:inference"]}}
        """);
        var store = new ClaudeAuthStore(dir);

        var loaded = store.Load()!;
        Assert.False(loaded.HasProfileScope);
        Assert.True(loaded.NeedsRefresh(Now));

        store.SaveRotated(loaded, loaded with { AccessToken = "a2", RefreshToken = "r2", ExpiresAtMs = 5000 });
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("a2", (string?)saved["claudeAiOauth"]!["accessToken"]);
        Assert.Equal(5000, (long)saved["claudeAiOauth"]!["expiresAt"]!);
        Assert.True((bool)saved["mcpOAuth"]!["keep"]!);
    }

    [Fact]
    public void AuthStoreRefusesToOverwriteAChangedLogin()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, ".credentials.json");
        File.WriteAllText(path, """{"claudeAiOauth": {"accessToken": "a1", "refreshToken": "r1"}}""");
        var store = new ClaudeAuthStore(dir);
        var loaded = store.Load()!;
        File.WriteAllText(path, """{"claudeAiOauth": {"accessToken": "other", "refreshToken": "r9"}}""");

        Assert.Throws<ProviderException>(() => store.SaveRotated(loaded, loaded with { AccessToken = "a2" }));
        Assert.Contains("other", File.ReadAllText(path));
    }

    [Fact]
    public async Task RateLimitServesLastGoodDataWithNote()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), """{"claudeAiOauth": {"accessToken": "a1"}}""");
        var handler = new StubHandler(
            (HttpStatusCode.OK, """{"five_hour": {"utilization": 10}}"""),
            (HttpStatusCode.TooManyRequests, "{}"));
        var provider = new ClaudeProvider(new HttpClient(handler), new ClaudeAuthStore(dir));

        var first = await provider.RefreshAsync();
        var second = await provider.RefreshAsync();

        Assert.Null(first.Error);
        Assert.Equal("Session", second.Lines[0].Label);
        Assert.Equal("Note", second.Lines[^1].Label);
        Assert.Contains("Updates blocked by Anthropic", second.Warning);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("oauth-2025-04-20", handler.Requests[0].Headers.GetValues("anthropic-beta").Single());
    }
}
