using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BestStoriesApi.Tests;

public class StoryRefresherTests
{
    private static StoryRefresher CreateRefresher(Mock<IHackerNewsClient> client, int maxParallelFetches = 4) =>
        new(client.Object,
            Options.Create(new HackerNewsOptions { MaxParallelFetches = maxParallelFetches }),
            NullLogger<StoryRefresher>.Instance);

    [Test]
    public async Task Ranks_the_whole_list_by_score_highest_first()
    {
        // The highest scores are the LAST ids, so a "take n ids, then sort" would get this wrong.
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("low", 10)),
            (2, TestData.Item("middle", 50)),
            (3, TestData.Item("high", 90)),
            (4, TestData.Item("highest", 99)));

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "highest", "high", "middle", "low" }));
    }

    [Test]
    public async Task Equal_scores_keep_the_order_Hacker_News_gave()
    {
        // More than 16 of them: an unstable sort keeps the order of a short list anyway, so with three this test
        // could not fail.
        var titles = Enumerable.Range(1, 24).Select(i => $"story {i}").ToArray();
        var client = TestData.FakeHackerNews(titles.Select((title, i) => (i + 1, TestData.Item(title, 10))).ToArray());

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(titles));
    }

    [Test]
    public async Task A_story_that_fails_to_load_is_skipped_and_the_rest_are_kept()
    {
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("good one", 10)),
            (2, TestData.Item("never loaded", 99)),
            (3, TestData.Item("good two", 20)));
        client.Setup(c => c.GetItemAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "good two", "good one" }));
    }

    [Test]
    public async Task Deleted_dead_and_empty_items_are_left_out()
    {
        var deleted = TestData.Item("deleted", 99);
        deleted.Deleted = true;
        var dead = TestData.Item("dead", 98);
        dead.Dead = true;
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("kept", 10)),
            (2, deleted),
            (3, dead),
            (4, TestData.Item("empty", 50)));
        client.Setup(c => c.GetItemAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync((HackerNewsItem?)null);

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "kept" }));
    }

    [Test]
    public async Task A_story_that_times_out_is_one_failure_and_the_refresh_goes_on()
    {
        // HttpClient reports its own timeout as a TaskCanceledException, which is an OperationCanceledException.
        // Only the refresh itself being cancelled may stop the refresh.
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("good one", 10)),
            (2, TestData.Item("timed out", 99)),
            (3, TestData.Item("good two", 20)));
        client.Setup(c => c.GetItemAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "good two", "good one" }));
    }

    [Test]
    public void If_no_story_can_be_loaded_the_refresh_fails()
    {
        var client = TestData.FakeHackerNews((1, TestData.Item("a", 1)), (2, TestData.Item("b", 2)));
        client.Setup(c => c.GetItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None));
    }

    [Test]
    public async Task Never_fetches_more_stories_at_once_than_the_configured_limit()
    {
        const int limit = 3;
        var stories = Enumerable.Range(1, 30).Select(id => (id, TestData.Item($"story {id}", id))).ToArray();
        var client = TestData.FakeHackerNews(stories);

        var running = 0;
        var highestSeen = 0;
        client.Setup(c => c.GetItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int id, CancellationToken ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref highestSeen, now);
                await Task.Delay(10, ct);
                Interlocked.Decrement(ref running);
                return TestData.Item($"story {id}", id);
            });

        var snapshot = await CreateRefresher(client, limit).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot, Has.Count.EqualTo(30));
        Assert.That(highestSeen, Is.InRange(1, limit));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }
}

/// <summary>A refresh in which many stories fail must not pass itself off as complete.</summary>
public class IncompleteRefreshTests
{
    private static StoryRefresher CreateRefresher(Mock<IHackerNewsClient> client) =>
        new(client.Object,
            Options.Create(new HackerNewsOptions { MaxParallelFetches = 10 }),
            NullLogger<StoryRefresher>.Instance);

    /// <summary>200 stories with ids 1..200 and score = id; the ids listed fail to load.</summary>
    private static Mock<IHackerNewsClient> TwoHundredWhereTheseFail(IEnumerable<int> failing)
    {
        var client = TestData.FakeHackerNews(Enumerable.Range(1, 200).Select(id => (id, TestData.Item($"story {id}", id))).ToArray());
        foreach (var id in failing)
            client.Setup(c => c.GetItemAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        return client;
    }

    [Test]
    public void When_190_of_200_fail_the_refresh_is_incomplete_and_carries_the_10_that_loaded()
    {
        var client = TwoHundredWhereTheseFail(Enumerable.Range(11, 190));

        var ex = Assert.ThrowsAsync<IncompleteRefreshException>(() => CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None));

        Assert.That(ex!.Failed, Is.EqualTo(190));
        Assert.That(ex.Requested, Is.EqualTo(200));
        Assert.That(ex.Partial.Select(s => s.Score), Is.EqualTo(Enumerable.Range(1, 10).Reverse()));
    }

    [Test]
    public async Task Up_to_the_limit_is_complete_and_one_more_is_not()
    {
        var atLimit = TwoHundredWhereTheseFail(Enumerable.Range(1, 20));        // 10% of 200
        var overLimit = TwoHundredWhereTheseFail(Enumerable.Range(1, 21));

        Assert.That(await CreateRefresher(atLimit).BuildSnapshotAsync(CancellationToken.None), Has.Count.EqualTo(180));
        Assert.ThrowsAsync<IncompleteRefreshException>(() => CreateRefresher(overLimit).BuildSnapshotAsync(CancellationToken.None));
    }

    [TestCase("no body")]
    [TestCase("no title")]
    [TestCase("no author")]
    public void An_answer_with_nothing_to_show_is_a_failure_so_190_of_them_make_the_refresh_incomplete(string emptiness)
    {
        // Hacker News can answer 200 with nothing usable. Counted as failures, 190 of 200 cannot pass for a complete
        // refresh and replace a good snapshot with ten stories.
        var client = TestData.FakeHackerNews(Enumerable.Range(1, 200).Select(id =>
        {
            var item = TestData.Item($"story {id}", id);
            if (id > 10 && emptiness == "no title") item.Title = null;
            if (id > 10 && emptiness == "no author") item.By = null;
            return (id, item);
        }).ToArray());
        if (emptiness == "no body")
            client.Setup(c => c.GetItemAsync(It.Is<int>(id => id > 10), It.IsAny<CancellationToken>())).ReturnsAsync((HackerNewsItem?)null);

        var ex = Assert.ThrowsAsync<IncompleteRefreshException>(() => CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None));

        Assert.That(ex!.Failed, Is.EqualTo(190));
        Assert.That(ex.Partial, Has.Count.EqualTo(10));
    }

    [Test]
    public async Task Deleted_stories_are_not_failures()
    {
        var client = TestData.FakeHackerNews(Enumerable.Range(1, 200).Select(id =>
        {
            var item = TestData.Item($"story {id}", id);
            item.Deleted = id > 50;
            return (id, item);
        }).ToArray());

        Assert.That(await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None), Has.Count.EqualTo(50));
    }
}

public class ShutdownTests
{
    private static (StoryRefreshService Service, StoryStore Store, Task Entered) Build()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IHackerNewsClient>();
        client.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return (IReadOnlyList<int>)[];
            });
        var options = Options.Create(new HackerNewsOptions());
        var store = new StoryStore();
        var service = new StoryRefreshService(
            new StoryRefresher(client.Object, options, NullLogger<StoryRefresher>.Instance),
            store, options, NullLogger<StoryRefreshService>.Instance);
        return (service, store, entered.Task);
    }

    [Test]
    public async Task Stopping_during_the_first_refresh_releases_whoever_is_waiting_for_it()
    {
        var (service, store, entered) = Build();
        await service.StartAsync(CancellationToken.None);
        var waiting = store.GetAsync(CancellationToken.None);
        await entered.WaitAsync(TimeSpan.FromSeconds(5)); // the first refresh is under way

        await service.StopAsync(CancellationToken.None);

        Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);
    }

    [Test]
    public async Task Stopping_a_refresh_loop_that_never_started_releases_whoever_is_waiting_for_it()
    {
        var (service, store, _) = Build();
        var waiting = store.GetAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None); // never started, so only StopAsync can release them

        Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);
    }
}
