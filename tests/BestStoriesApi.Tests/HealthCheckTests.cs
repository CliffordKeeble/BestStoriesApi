using System.Net;
using System.Text.Json.Nodes;
using BestStoriesApi.HackerNews;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>Liveness, and readiness: whether there is a snapshot to serve, how many stories it holds and how old it is.</summary>
public class HealthCheckTests
{
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(HttpStatusCode Status, JsonNode Body)> GetAsync(HttpClient http, string path)
    {
        var response = await http.GetAsync(path);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Test]
    public async Task Before_the_first_snapshot_ready_is_503_and_live_is_200()
    {
        var fake = new Mock<IHackerNewsClient>();
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (IReadOnlyList<int>)[]; });
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var ready = await GetAsync(http, "/health/ready");
        var live = await GetAsync(http, "/health/live");

        Assert.That(ready.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((string?)ready.Body["status"], Is.EqualTo("Unhealthy"));
        Assert.That(live.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((string?)live.Body["status"], Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task Ready_says_how_many_stories_and_how_old_and_turns_degraded_after_exactly_three_refresh_intervals()
    {
        var clock = new ManualClock(MondayMorning);
        var fake = TestData.FakeHackerNews((1, TestData.Item("one", 30)), (2, TestData.Item("two", 20)), (3, TestData.Item("three", 10)));
        // The factory refreshes hourly, and the refresh timer runs on real time, so this first snapshot is the only one.
        await using var app = new BestStoriesApiFactory(fake, clock: clock);
        using var http = app.CreateClient();
        await http.GetAsync("/api/beststories?n=1"); // waits for the first snapshot

        clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = await GetAsync(http, "/health/ready");
        clock.Advance(TimeSpan.FromHours(3) - TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        var justFresh = await GetAsync(http, "/health/ready"); // one second short of three hourly intervals
        clock.Advance(TimeSpan.FromSeconds(2));
        var stale = await GetAsync(http, "/health/ready");     // one second past

        Assert.That(fresh.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((string?)fresh.Body["status"], Is.EqualTo("Healthy"));
        Assert.That((int?)fresh.Body["stories"], Is.EqualTo(3));
        Assert.That((DateTimeOffset?)fresh.Body["builtAt"], Is.EqualTo(MondayMorning));
        Assert.That((long?)fresh.Body["ageSeconds"], Is.EqualTo(120));
        Assert.That((string?)justFresh.Body["status"], Is.EqualTo("Healthy"));
        Assert.That(stale.Status, Is.EqualTo(HttpStatusCode.OK)); // still being served: an old list beats none
        Assert.That((string?)stale.Body["status"], Is.EqualTo("Degraded"));
        Assert.That((long?)stale.Body["ageSeconds"], Is.EqualTo(3 * 3600 + 1));
    }
}
