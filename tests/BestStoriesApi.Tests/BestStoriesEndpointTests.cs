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
