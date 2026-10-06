using System.Net;
using System.Text.Json.Nodes;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Codex;

namespace OpenUsage.Core.Tests;

public class CodexTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    private static readonly CodexHeaderFallbacks NoHeaders = new(null, null, null);

    [Fact]
    public void ClassifiesWindowsByDurationEvenWhenWeeklyIsPrimary()
    {
        var body = JsonNode.Parse("""
        {"rate_limit": {
          "primary_window": {"used_percent": 40, "limit_window_seconds": 604800, "reset_after_seconds": 60},
          "secondary_window": {"used_percent": 12, "limit_window_seconds": 18000}
        }}
        """)!.AsObject();

        var lines = CodexUsageMapper.Map(body, NoHeaders, null, Now);

        var session = Assert.IsType<MetricLine.Progress>(lines[0]);
        var weekly = Assert.IsType<MetricLine.Progress>(lines[1]);
        Assert.Equal(("Session", 12d), (session.Label, session.Used));
        Assert.Equal(("Weekly", 40d), (weekly.Label, weekly.Used));
        Assert.Equal(Now.AddSeconds(60), weekly.ResetsAt);
    }

    [Fact]
    public void FallsBackToHeadersAndSlotPosition()
    {
        var lines = CodexUsageMapper.Map(new JsonObject(), new CodexHeaderFallbacks(5, 25, null), null, Now);
        Assert.Equal(new[] { "Session", "Weekly" }, lines.Select(l => l.Label));
        Assert.Equal(MetricPeriod.WeekMs, ((MetricLine.Progress)lines[1]).PeriodDurationMs);
    }

    [Fact]
    public void MapsSparkResetsAndCredits()
    {
        var body = JsonNode.Parse("""
        {"additional_rate_limits": [
            "junk",
            {"limit_name": "GPT-5.3-Codex-Spark", "rate_limit": {"primary_window": {"used_percent": 3}}}
         ],
         "rate_limit_reset_credits": {"available_count": 1},
         "credits": {"balance": "821.5"}}
        """)!.AsObject();
        var endpoint = JsonNode.Parse("""
        {"available_count": 2, "credits": [
            {"status": "available", "expires_at": "2026-11-01T00:00:00Z"},
            {"status": "used", "expires_at": "2026-10-20T00:00:00Z"},
            {"expires_at": 1791000000}
        ]}
        """)!.AsObject();

        var lines = CodexUsageMapper.Map(body, NoHeaders, endpoint, Now);

        Assert.Equal(new[] { "Spark", "Rate Limit Resets", "Credits" }, lines.Select(l => l.Label));
        var resets = (MetricLine.Values)lines[1];
        Assert.Equal(2, resets.Items[0].Number);
        Assert.Equal(2, resets.ExpiriesAt!.Count);
        Assert.True(resets.ExpiriesAt[0] < resets.ExpiriesAt[1]);
        var credits = (MetricLine.Values)lines[2];
        Assert.Equal(821 * 0.04, credits.Items[0].Number, 6);
        Assert.Equal(821, credits.Items[1].Number);
    }

    [Theory]
    [InlineData("prolite", "Pro 100")]
    [InlineData("pro", "Pro 200")]
    [InlineData("plus", "Plus")]
    [InlineData("self_serve_business", "Self Serve Business")]
    [InlineData("", null)]
    public void FormatsPlan(string raw, string? expected) => Assert.Equal(expected, CodexUsageMapper.FormatPlan(raw));

    [Fact]
    public async Task ApiKeyOnlyLoginExplainsUsageIsUnavailable()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(home, "auth.json"), """{"OPENAI_API_KEY": "sk-test"}""");
        var provider = new CodexProvider(new HttpClient(new StubHandler()), new CodexAuthStore(new[] { home }));

        var snapshot = await provider.RefreshAsync();

        Assert.Equal(CodexErrors.ApiKeyOnly, snapshot.Error);
        Assert.False(provider.HasLocalCredentials());
    }

    [Fact]
    public async Task RefreshesOn401AndPersistsRotatedTokens()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, """{"tokens": {"access_token": "old", "refresh_token": "r1", "account_id": "acct"}, "extra": 1}""");
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.OK, """{"access_token": "new", "refresh_token": "r2"}"""),
            (HttpStatusCode.OK, """{"plan_type": "plus", "rate_limit": {"primary_window": {"used_percent": 9}}}"""),
            (HttpStatusCode.NotFound, "{}"));
        var provider = new CodexProvider(new HttpClient(handler), new CodexAuthStore(new[] { home }));

        var snapshot = await provider.RefreshAsync();

        Assert.Null(snapshot.Error);
        Assert.Equal("Plus", snapshot.Plan);
        Assert.Equal("Bearer new", handler.Requests[2].Headers.Authorization!.ToString());
        Assert.Equal("acct", handler.Requests[2].Headers.GetValues("ChatGPT-Account-Id").Single());
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("new", (string?)saved["tokens"]!["access_token"]);
        Assert.Equal("r2", (string?)saved["tokens"]!["refresh_token"]);
        Assert.Equal(1, (int)saved["extra"]!);
    }
}
