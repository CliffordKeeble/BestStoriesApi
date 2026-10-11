using System.Net;
using System.Text.Json;
using BestStoriesApi.HackerNews;
using Moq;

namespace BestStoriesApi.Tests;

public class BestStoriesEndpointTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Test]
    public async Task Returns_the_top_n_of_the_whole_list_not_the_first_n_ids()
    {
        var fake = TestData.FakeHackerNews(
            (1, TestData.Item("low", 10)),
            (2, TestData.Item("middle", 50)),
            (3, TestData.Item("high", 90)),
            (4, TestData.Item("highest", 99)));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var stories = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        Assert.That(stories.Select(s => s.GetProperty("title").GetString()), Is.EqualTo(new[] { "highest", "high" }));
    }

    [Test]
    public async Task A_second_request_makes_no_call_to_Hacker_News()
    {
        var fake = TestData.FakeHackerNews((1, TestData.Item("a", 1)), (2, TestData.Item("b", 2)));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        await http.GetAsync("/api/beststories?n=2");
        var callsAfterFirst = fake.Invocations.Count;
        await http.GetAsync("/api/beststories?n=2");
        await http.GetAsync("/api/beststories?n=1");

        Assert.That(fake.Invocations.Count, Is.EqualTo(callsAfterFirst));
        fake.Verify(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Many_requests_at_cold_start_cause_exactly_one_refresh()
    {
        // Hold the first refresh open, so every request arrives while it is still running.
        var release = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = TestData.FakeHackerNews((1, TestData.Item("a", 1)), (2, TestData.Item("b", 2)));
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>())).Returns(release.Task);
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var requests = Enumerable.Range(0, 50).Select(_ => http.GetAsync("/api/beststories?n=2")).ToList();
        await Task.Delay(200);
        Assert.That(requests.Any(r => r.IsCompleted), Is.False, "requests should be waiting for the refresh");

        release.SetResult(new[] { 1, 2 });
        var responses = await Task.WhenAll(requests).WaitAsync(Patience);

        Assert.That(responses.Select(r => r.StatusCode), Is.All.EqualTo(HttpStatusCode.OK));
        fake.Verify(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
        fake.Verify(c => c.GetItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task A_failed_refresh_keeps_serving_the_old_snapshot()
    {
        var thirdCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fake = TestData.FakeHackerNews((1, TestData.Item("kept", 5)));
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1) return Task.FromResult<IReadOnlyList<int>>(new[] { 1 });
                if (call == 3) thirdCall.TrySetResult();
                throw new HttpRequestException("Hacker News is down");
            });

        // Refresh every 20 ms. Call 1 works; calls 2 and later fail.
        await using var app = new BestStoriesApiFactory(fake, TimeSpan.FromMilliseconds(20));
        using var http = app.CreateClient();
        await http.GetAsync("/api/beststories?n=1"); // waits for the first refresh
        await thirdCall.Task.WaitAsync(Patience);    // the third attempt starts only after the second one failed

        var response = await http.GetAsync("/api/beststories?n=1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var stories = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        Assert.That(stories.Single().GetProperty("title").GetString(), Is.EqualTo("kept"));
    }

    [Test]
    public async Task Returns_503_if_the_very_first_refresh_fails()
    {
        var fake = new Mock<IHackerNewsClient>();
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=5");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task A_failed_first_refresh_is_tried_again_soon_not_after_the_refresh_interval()
    {
        var calls = 0;
        var fake = TestData.FakeHackerNews((1, TestData.Item("back", 5)));
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<IReadOnlyList<int>>(new HttpRequestException("Hacker News is down at start-up"))
                : Task.FromResult<IReadOnlyList<int>>(new[] { 1 }));

        // An hour between refreshes, and 50 ms between tries until there is a first snapshot.
        await using var app = new BestStoriesApiFactory(fake, configure: o => o.FirstSnapshotRetryInterval = TimeSpan.FromMilliseconds(50));
        using var http = app.CreateClient();

        var status = HttpStatusCode.ServiceUnavailable;
        for (var deadline = DateTime.UtcNow + Patience; status != HttpStatusCode.OK && DateTime.UtcNow < deadline; await Task.Delay(20))
            status = (await http.GetAsync("/api/beststories?n=1")).StatusCode;

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(calls, Is.EqualTo(2));
    }

    [TestCase("/api/beststories")]
    [TestCase("/api/beststories?n=")]
    [TestCase("/api/beststories?n=0")]
    [TestCase("/api/beststories?n=-3")]
    [TestCase("/api/beststories?n=abc")]
    [TestCase("/api/beststories?n=2.5")]
    public async Task A_missing_or_invalid_n_is_a_400(string url)
    {
        var fake = TestData.FakeHackerNews((1, TestData.Item("a", 1)));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var response = await http.GetAsync(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task An_n_larger_than_the_list_returns_everything_there_is()
    {
        var fake = TestData.FakeHackerNews((1, TestData.Item("a", 1)), (2, TestData.Item("b", 2)));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=500");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadJsonAsync(response)).GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Each_story_has_the_agreed_shape_and_the_time_is_ISO_8601_with_an_offset()
    {
        var fake = TestData.FakeHackerNews((1, TestData.Item("A title", 1716, "https://example.com/a")));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var story = (await ReadJsonAsync(await http.GetAsync("/api/beststories?n=1")))[0];

        Assert.That(story.GetProperty("time").GetString(), Is.EqualTo("2019-10-12T13:43:01+00:00"));
        Assert.That(story.GetProperty("title").GetString(), Is.EqualTo("A title"));
        Assert.That(story.GetProperty("uri").GetString(), Is.EqualTo("https://example.com/a"));
        Assert.That(story.GetProperty("postedBy").GetString(), Is.EqualTo("someone"));
        Assert.That(story.GetProperty("score").GetInt32(), Is.EqualTo(1716));
        Assert.That(story.GetProperty("commentCount").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public async Task A_story_with_no_url_has_a_null_uri()
    {
        var fake = TestData.FakeHackerNews((1, TestData.Item("Ask HN: anything?", 5, url: null)));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var story = (await ReadJsonAsync(await http.GetAsync("/api/beststories?n=1")))[0];

        Assert.That(story.GetProperty("uri").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }
}

/// <summary>What the API serves when a refresh is incomplete.</summary>
public class IncompleteRefreshEndpointTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Test]
    public async Task An_incomplete_refresh_does_not_replace_a_good_snapshot()
    {
        var fourthCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failing = false;
        var fake = TestData.FakeHackerNews(Enumerable.Range(1, 20).Select(id => (id, TestData.Item($"story {id}", id))).ToArray());
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 4) fourthCall.TrySetResult();
                return Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, 20).ToList());
            });
        fake.Setup(c => c.GetItemAsync(It.Is<int>(id => id > 1), It.IsAny<CancellationToken>()))
            .Returns((int id, CancellationToken _) => Volatile.Read(ref failing)
                ? Task.FromException<HackerNewsItem?>(new HttpRequestException("down"))
                : Task.FromResult<HackerNewsItem?>(TestData.Item($"story {id}", id)));

        // Refresh every 20 ms. The first refresh loads all 20; after that 19 of 20 fail on every refresh.
        await using var app = new BestStoriesApiFactory(fake, TimeSpan.FromMilliseconds(20));
        using var http = app.CreateClient();
        await http.GetAsync("/api/beststories?n=1");
        Volatile.Write(ref failing, true);
        await fourthCall.Task.WaitAsync(Patience); // refreshes run one at a time, so the third, all failing, has finished

        using var document = JsonDocument.Parse(await http.GetStringAsync("/api/beststories?n=100"));

        Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(20));
    }

    [Test]
    public async Task With_nothing_to_fall_back_on_an_incomplete_first_refresh_is_served()
    {
        var fake = TestData.FakeHackerNews(Enumerable.Range(1, 20).Select(id => (id, TestData.Item($"story {id}", id))).ToArray());
        fake.Setup(c => c.GetItemAsync(It.Is<int>(id => id > 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        await using var app = new BestStoriesApiFactory(fake);
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=100");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(1));
    }
}
