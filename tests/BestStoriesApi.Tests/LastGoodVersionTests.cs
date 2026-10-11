using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>A story that fails to load keeps the last good version a refresh found, for a while, instead of dropping out.</summary>
public class LastGoodVersionTests
{
    private static StoryRefresher CreateRefresher(Mock<IHackerNewsClient> client) =>
        new(client.Object,
            Options.Create(new HackerNewsOptions { MaxParallelFetches = 4 }),
            NullLogger<StoryRefresher>.Instance);

    private static void MakeStoryFail(Mock<IHackerNewsClient> client, int id, string failure)
    {
        var setup = client.Setup(c => c.GetItemAsync(id, It.IsAny<CancellationToken>()));
        if (failure == "throws")
            setup.ThrowsAsync(new HttpRequestException("Hacker News is down"));
        else if (failure == "times out")
            setup.ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        else
            setup.ReturnsAsync((HackerNewsItem?)null); // Hacker News answered, with nothing in it
    }

    [TestCase("throws")]
    [TestCase("times out")]
    [TestCase("answers with nothing")]
    public async Task A_story_that_fails_keeps_its_last_good_version_for_two_refreshes_then_drops_out(string failure)
    {
        var client = TestData.FakeHackerNews((1, TestData.Item("steady", 10)), (2, TestData.Item("top story", 99)));
        var refresher = CreateRefresher(client);
        await refresher.BuildSnapshotAsync(CancellationToken.None);

        // From now on the top story fails, while the other one goes on loading with a new score each time.
        MakeStoryFail(client, 2, failure);
        var refreshes = new List<IReadOnlyList<Story>>();
        for (var score = 11; score <= 13; score++)
        {
            client.Setup(c => c.GetItemAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(TestData.Item("steady", score));
            refreshes.Add(await refresher.BuildSnapshotAsync(CancellationToken.None));
        }

        // Kept for StoryRefresher.MaxRefreshesKept (two) refreshes in a row, then gone until it loads again.
        Assert.That(refreshes[0].Select(s => (s.Title, s.Score)), Is.EqualTo(new[] { ("top story", 99), ("steady", 11) }));
        Assert.That(refreshes[1].Select(s => (s.Title, s.Score)), Is.EqualTo(new[] { ("top story", 99), ("steady", 12) }));
        Assert.That(refreshes[2].Select(s => (s.Title, s.Score)), Is.EqualTo(new[] { ("steady", 13) }));
    }

    [Test]
    public async Task If_no_story_loads_now_the_refresh_fails_even_though_every_story_has_a_version_to_keep()
    {
        // A refresh that loaded nothing is Hacker News failing, and it must say so as a failure, not pass for an
        // incomplete refresh made entirely of old versions.
        var client = TestData.FakeHackerNews((1, TestData.Item("one", 10)), (2, TestData.Item("two", 20)));
        var refresher = CreateRefresher(client);
        await refresher.BuildSnapshotAsync(CancellationToken.None);
        client.Setup(c => c.GetItemAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Hacker News is down"));

        Assert.ThrowsAsync<InvalidOperationException>(() => refresher.BuildSnapshotAsync(CancellationToken.None));
    }

    [Test]
    public async Task A_story_that_has_gone_is_not_brought_back_by_a_later_failure()
    {
        var client = TestData.FakeHackerNews((1, TestData.Item("steady", 10)), (2, TestData.Item("flagged", 99)));
        var refresher = CreateRefresher(client);
        await refresher.BuildSnapshotAsync(CancellationToken.None);

        var flagged = TestData.Item("flagged", 99);
        flagged.Dead = true;
        client.Setup(c => c.GetItemAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(flagged);
        await refresher.BuildSnapshotAsync(CancellationToken.None); // story 2 is dead now, so it is gone
        MakeStoryFail(client, 2, "throws");
        var stories = await refresher.BuildSnapshotAsync(CancellationToken.None);

        Assert.That(stories.Select(s => s.Title), Is.EqualTo(new[] { "steady" }));
    }

    [Test]
    public async Task A_kept_version_still_counts_as_a_failure_so_190_kept_of_200_make_the_refresh_incomplete()
    {
        // A refresh made mostly of the last refresh's stories is not a fresh one, so it must not replace the
        // snapshot being served, however complete it looks.
        var client = TestData.FakeHackerNews(Enumerable.Range(1, 200).Select(id => (id, TestData.Item($"story {id}", id))).ToArray());
        var refresher = CreateRefresher(client);
        await refresher.BuildSnapshotAsync(CancellationToken.None);
        client.Setup(c => c.GetItemAsync(It.Is<int>(id => id > 10), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Hacker News is down"));

        var ex = Assert.ThrowsAsync<IncompleteRefreshException>(() => refresher.BuildSnapshotAsync(CancellationToken.None));

        Assert.That(ex!.Failed, Is.EqualTo(190));
        Assert.That(ex.Partial, Has.Count.EqualTo(200)); // all of them: 10 loaded now, 190 kept from before
    }
}
