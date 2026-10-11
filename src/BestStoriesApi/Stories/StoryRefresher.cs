using System.Diagnostics;
using BestStoriesApi.HackerNews;
using Microsoft.Extensions.Options;

namespace BestStoriesApi.Stories;

/// <summary>
/// Builds a fresh, fully ranked list of the best stories from Hacker News.
/// This is the only class that asks Hacker News for story details.
/// </summary>
public class StoryRefresher(
    IHackerNewsClient client,
    IOptions<HackerNewsOptions> options,
    ILogger<StoryRefresher> logger)
{
    public async Task<IReadOnlyList<Story>> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var ids = await client.GetBestStoryIdsAsync(cancellationToken);

        // Fetch the details a few at a time, so we never open hundreds of connections at once.
        // Each task writes to its own slot in the array, so no locking is needed.
        var found = new Story?[ids.Count];
        var failed = 0; // threw, or came back with nothing we can show
        var gone = 0;   // deleted or dead on Hacker News, which is not a failure
        var parallelism = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxParallelFetches,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count), parallelism, async (index, ct) =>
        {
            var id = ids[index];
            try
            {
                var item = await client.GetItemAsync(id, ct);
                if (item is { Deleted: true } or { Dead: true })
                {
                    Interlocked.Increment(ref gone);
                    logger.LogStoryGone(id);
                    return;
                }

                found[index] = ToStory(item);
                if (found[index] is null)
                {
                    // Hacker News answered, but with nothing we can show: no body, or no title or author. That counts
                    // as a failure, so a refresh of mostly empty answers cannot pass itself off as complete.
                    Interlocked.Increment(ref failed);
                    logger.LogStoryUnusable(id);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One bad story must not spoil the whole refresh. Leave it out, and count it. A timeout lands here
                // too, whatever exception it surfaces as; only the refresh itself being cancelled stops the loop.
                Interlocked.Increment(ref failed);
                logger.LogStorySkipped(ex, id);
            }
        });

        // Rank the WHOLE list by score, highest first. OrderBy is stable, so equal scores
        // keep the order Hacker News gave. Taking the first n happens later, per request.
        var stories = found.OfType<Story>().OrderByDescending(s => s.Score).ToList();
        var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogFetched(stories.Count, ids.Count, gone, failed, elapsedMilliseconds);

        // Nothing usable means something is wrong upstream. Throwing keeps the old snapshot in place.
        if (stories.Count == 0)
            throw new InvalidOperationException("Hacker News returned no usable stories.");

        // Failures are not independent: when Hacker News wobbles, many fetches fail together, and a ranking
        // of the survivors can leave out the real top stories. Past the limit the refresh is incomplete, and
        // the caller decides what to do with it. A deleted or dead story is not a failure.
        var allowed = Math.Max(1, (int)Math.Floor(ids.Count * options.Value.MaxFailedFetchFraction));
        if (failed > allowed)
            throw new IncompleteRefreshException(stories, failed, ids.Count);

        return stories;
    }

    /// <summary>The story as our API shows it, or null when Hacker News gave nothing to show.</summary>
    private static Story? ToStory(HackerNewsItem? item)
    {
        if (item is null || item.Title is null || item.By is null)
            return null;

        return new Story(
            item.Title,
            item.Url,
            item.By,
            DateTimeOffset.FromUnixTimeSeconds(item.Time),
            item.Score,
            item.Descendants);
    }
}
