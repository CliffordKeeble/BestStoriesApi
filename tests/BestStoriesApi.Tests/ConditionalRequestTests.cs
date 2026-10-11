using System.Net;
using System.Net.Http.Headers;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>
/// Caching: max-age says how long a copy stays current, and ETag with If-None-Match lets a client that already
/// holds the answer get 304 and no body.
/// </summary>
public class ConditionalRequestTests
{
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static BestStoriesApiFactory App() => new(TestData.FakeHackerNews(
        (1, TestData.Item("one", 30)), (2, TestData.Item("two", 20)), (3, TestData.Item("three", 10))));

    [Test]
    public async Task A_200_carries_an_ETag_a_public_max_age_and_a_Content_Length()
    {
        await using var app = App();
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.ETag, Is.Not.Null);
        Assert.That(response.Headers.CacheControl?.Public, Is.True);
        // The factory refreshes hourly, and this snapshot has only just been built.
        Assert.That(response.Headers.CacheControl?.MaxAge, Is.InRange(TimeSpan.FromMinutes(59), TimeSpan.FromHours(1)));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo((await response.Content.ReadAsByteArrayAsync()).Length));
    }

    [Test]
    public async Task Max_age_counts_down_to_when_the_next_refresh_starts_and_a_304_carries_it_too()
    {
        var clock = new ManualClock(MondayMorning);
        var fake = TestData.FakeHackerNews((1, TestData.Item("one", 30)), (2, TestData.Item("two", 20)));
        // This refresh takes 30 seconds by the test's clock. The next one starts an hour after this one finished.
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { clock.Advance(TimeSpan.FromSeconds(30)); return (IReadOnlyList<int>)[1, 2]; });
        await using var app = new BestStoriesApiFactory(fake, clock: clock);
        using var http = app.CreateClient();

        var first = await http.GetAsync("/api/beststories?n=2"); // waits for the first snapshot
        clock.Advance(TimeSpan.FromMinutes(20) + TimeSpan.FromMilliseconds(500));
        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/beststories?n=2");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var revalidated = await http.SendAsync(again);
        clock.Advance(TimeSpan.FromHours(2)); // the next refresh is late, or failed: the timer runs on real time
        var overdue = await http.GetAsync("/api/beststories?n=2");

        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromHours(1)));
        Assert.That(revalidated.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(revalidated.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(3600 - 1200 - 1))); // rounded down
        Assert.That(overdue.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(overdue.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task Sending_the_tag_back_gets_304_with_no_body_and_the_same_tag()
    {
        await using var app = App();
        using var http = app.CreateClient();
        var first = await http.GetAsync("/api/beststories?n=2");

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/beststories?n=2");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await http.SendAsync(again);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(await second.Content.ReadAsByteArrayAsync(), Is.Empty);
        Assert.That(second.Headers.ETag, Is.EqualTo(first.Headers.ETag));
    }

    [Test]
    public async Task A_weak_form_of_the_tag_and_a_star_also_match()
    {
        await using var app = App();
        using var http = app.CreateClient();
        var tag = (await http.GetAsync("/api/beststories?n=1")).Headers.ETag!.Tag;

        foreach (var header in new[] { $"W/{tag}", $"\"other\", {tag}", "*" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/beststories?n=1");
            request.Headers.TryAddWithoutValidation("If-None-Match", header);
            Assert.That((await http.SendAsync(request)).StatusCode, Is.EqualTo(HttpStatusCode.NotModified), header);
        }
    }

    [Test]
    public async Task Another_n_or_a_stale_tag_gets_the_full_answer()
    {
        await using var app = App();
        using var http = app.CreateClient();
        var tagForTwo = (await http.GetAsync("/api/beststories?n=2")).Headers.ETag!;

        using var otherN = new HttpRequestMessage(HttpMethod.Get, "/api/beststories?n=3");
        otherN.Headers.IfNoneMatch.Add(tagForTwo);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/beststories?n=2");
        stale.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"not-the-tag\""));

        Assert.That((await http.SendAsync(otherN)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await http.SendAsync(stale)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
