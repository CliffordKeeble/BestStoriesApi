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
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("first", 10)),
            (2, TestData.Item("second", 10)),
            (3, TestData.Item("third", 10)));

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "first", "second", "third" }));
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
    public async Task Deleted_and_missing_items_are_skipped()
    {
        var deleted = TestData.Item("deleted", 99);
        deleted.Deleted = true;
        var client = TestData.FakeHackerNews(
            (1, TestData.Item("kept", 10)),
            (2, deleted),
            (3, TestData.Item("missing", 50)));
        client.Setup(c => c.GetItemAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync((HackerNewsItem?)null);

        var snapshot = await CreateRefresher(client).BuildSnapshotAsync(CancellationToken.None);

        Assert.That(snapshot.Select(s => s.Title), Is.EqualTo(new[] { "kept" }));
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
            .Returns(async (int id, CancellationToken _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref highestSeen, now);
                await Task.Delay(10);
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
