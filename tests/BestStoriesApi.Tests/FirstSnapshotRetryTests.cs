using System.Collections.Concurrent;
using System.Diagnostics;
using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>How the refresh loop tries again while there is no snapshot to serve.</summary>
public class FirstSnapshotRetryTests
{
    [Test]
    public async Task While_Hacker_News_keeps_failing_the_wait_between_tries_doubles()
    {
        // Three tries fail and the fourth works. Each try is timed as it starts.
        var tryStarts = new ConcurrentQueue<long>();
        var fourthTry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = TestData.FakeHackerNews((1, TestData.Item("back", 5)));
        client.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                tryStarts.Enqueue(Stopwatch.GetTimestamp());
                if (tryStarts.Count < 4)
                    return Task.FromException<IReadOnlyList<int>>(new HttpRequestException("Hacker News is still down"));
                fourthTry.TrySetResult();
                return Task.FromResult<IReadOnlyList<int>>([1]);
            });
        var options = Options.Create(new HackerNewsOptions { FirstSnapshotRetryInterval = TimeSpan.FromMilliseconds(100) });
        var store = new StoryStore();
        var service = new StoryRefreshService(
            new StoryRefresher(client.Object, options, NullLogger<StoryRefresher>.Instance),
            store, options, NullLogger<StoryRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await fourthTry.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        // The waits are 100, 200 and 400 ms. Only lower bounds are checked, so a slow machine cannot fail the test;
        // each allows 20 ms, because a timer on Windows can end up to one 15.6 ms clock tick early.
        var starts = tryStarts.ToArray();
        var gaps = starts.Zip(starts.Skip(1), (earlier, later) => Stopwatch.GetElapsedTime(earlier, later).TotalMilliseconds).ToArray();
        Assert.That(gaps, Has.Length.EqualTo(3));
        Assert.That(gaps[0], Is.GreaterThanOrEqualTo(80));
        Assert.That(gaps[1], Is.GreaterThanOrEqualTo(180));
        Assert.That(gaps[2], Is.GreaterThanOrEqualTo(380));
    }
}
