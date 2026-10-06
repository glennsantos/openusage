using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers.Cursor;

namespace OpenUsage.Core.Tests;

public class CursorTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    [Fact]
    public void MapsIndividualPlan()
    {
        var usage = Json("""
        {"billingCycleStart": "1790000000000", "billingCycleEnd": 1792592000000,
         "planUsage": {"limit": 2000, "totalSpend": 500, "autoPercentUsed": 10, "apiPercentUsed": 20},
         "spendLimitUsage": {"individualLimit": 10000, "individualUsed": 2550}}
        """);

        var lines = CursorUsageMapper.Map(usage, "pro", null, 0);

        Assert.Equal(new[] { "Total usage", "Cursor Models", "Other Models", "On-demand" }, lines.Select(l => l.Label));
        var total = (MetricLine.Progress)lines[0];
        Assert.Equal(25, total.Used);
        Assert.Equal(2_592_000_000, total.PeriodDurationMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792592000000), total.ResetsAt);
        var onDemand = (MetricLine.Progress)lines[3];
        Assert.Equal((25.5, 100d), (onDemand.Used, onDemand.Limit));
    }

    [Fact]
    public void TeamWithoutPoolsShowsDollars()
    {
        var usage = Json("""{"planUsage": {"limit": 5000, "remaining": 3000}, "spendLimitUsage": {"limitType": "team"}}""");
        var total = (MetricLine.Progress)CursorUsageMapper.Map(usage, null, null, 0)[0];
        Assert.Equal((20d, 50d), (total.Used, total.Limit));
        Assert.IsType<ProgressFormat.Dollars>(total.Format);
    }

    [Fact]
    public void CreditsCombineGrantsAndStripeBalance()
    {
        var usage = Json("""{"planUsage": {"totalPercentUsed": 5}}""");
        var grants = Json("""{"hasCreditGrants": true, "totalCents": 1000, "usedCents": 250}""");
        var stripe = CursorUsageMapper.StripeCreditCents(Json("""{"customerBalance": -500}"""));

        var credits = (MetricLine.Values)CursorUsageMapper.Map(usage, "pro", grants, stripe)[0];

        Assert.Equal("Credits", credits.Label);
        Assert.Equal(12.5, credits.Items[0].Number);
    }

    [Fact]
    public void DisabledUsageMeansNoSubscription()
    {
        var e = Assert.Throws<ProviderException>(() => CursorUsageMapper.Map(Json("""{"enabled": false}"""), null, null, 0));
        Assert.Equal(CursorUsageMapper.NoActiveSubscription, e.Message);
    }

    [Theory]
    [InlineData("""{"spendLimitUsage": {}}""", "enterprise", true)]
    [InlineData("""{"planUsage": {"limit": 2000}}""", "pro", false)]
    [InlineData("""{"planUsage": {}}""", "pro", true)]
    [InlineData("""{"enabled": false}""", "team", false)]
    public void DecidesSummaryFallback(string usage, string plan, bool expected) =>
        Assert.Equal(expected, CursorUsageMapper.ShouldUseSummaryFallback(Json(usage), plan));

    [Fact]
    public void SummaryMapsRequestBasedPlan()
    {
        var requests = Json("""{"gpt-4": {"maxRequestUsage": 500, "numRequests": 120}, "startOfMonth": "2026-10-01T00:00:00.000Z"}""");
        var lines = CursorSummaryMapper.Map(null, requests);
        Assert.Equal(new[] { "Total usage", "Requests" }, lines.Select(l => l.Label));
        var total = (MetricLine.Progress)lines[0];
        Assert.Equal((120d, 500d), (total.Used, total.Limit));
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T00:00:00Z"), total.ResetsAt);
    }

    [Fact]
    public void SummaryMapsTeamPoolAndOnDemand()
    {
        var summary = Json("""
        {"billingCycleStart": "2026-10-01 00:00:00 UTC", "billingCycleEnd": "2026-11-01T00:00:00Z", "limitType": "team",
         "teamUsage": {"pooled": {"limit": 10000, "used": 4000}, "onDemand": {"enabled": true, "used": 700}}}
        """);
        var lines = CursorSummaryMapper.Map(summary, null);
        Assert.Equal(new[] { "Total usage", "On-demand" }, lines.Select(l => l.Label));
        Assert.Equal(40, ((MetricLine.Progress)lines[0]).Used);
        Assert.Equal(7, ((MetricLine.Values)lines[1]).Items[0].Number);
    }

    [Fact]
    public void GrokBotSkipsPooledEnterpriseAllowance()
    {
        Assert.Null(CursorUsageMapper.MapGrokBot(Json("""{"usesPooledEnterpriseAllowance": true, "usagePercent": 5}""")));
        var line = (MetricLine.Progress)CursorUsageMapper.MapGrokBot(Json("""{"usagePercent": 140}"""))!;
        Assert.Equal(100, line.Used);
    }

    [Fact]
    public void SessionCookieUsesUserIdFromJwtSub()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"auth0|user_abc","exp":1}""")).TrimEnd('=');
        var token = $"h.{payload}.s";
        var session = CursorAuth.Session(token)!.Value;
        Assert.Equal("user_abc", session.UserId);
        Assert.Equal($"user_abc%3A%3A{token}", session.Cookie);
        Assert.True(CursorAuth.NeedsRefresh(token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AuthStoreReadsAndWritesStateDb()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "state.vscdb");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                INSERT INTO ItemTable VALUES ('cursorAuth/accessToken', ' a1 '), ('cursorAuth/refreshToken', 'r1');
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new CursorAuthStore(path);

        Assert.Equal(new CursorAuth("a1", "r1"), store.Load());
        store.SaveAccessToken("a2");
        Assert.Equal("a2", store.Load()!.AccessToken);
    }
}
