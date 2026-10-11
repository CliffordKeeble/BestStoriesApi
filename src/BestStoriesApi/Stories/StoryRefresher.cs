using System.Collections.Frozen;
using System.Diagnostics;
using BestStoriesApi.HackerNews;
using Microsoft.Extensions.Options;

namespace BestStoriesApi.Stories;

/// <summary>
/// Builds a fresh, fully ranked list of the best stories from Hacker News.
/// This is the only class that asks Hacker News for story details. It remembers the last good version of each
/// story in the list, so a story that fails to load keeps that version for a while instead of dropping out of the
/// ranking.
/// </summary>
public class StoryRefresher(
    IHackerNewsClient client,
    IOptions<HackerNewsOptions> options,
    ILogger<StoryRefresher> logger)
{
    /// <summary>
    /// A failed story keeps its last good version for at most this many refreshes in a row, so nothing served was
    /// fetched more than about three refresh intervals ago. After that it drops out until it loads again.
    /// </summary>
    public const int MaxRefreshesKept = 2;

    // The last good version of each story in the latest list, by id. Only the refresh loop calls this class, and
    // it runs one refresh at a time: each refresh reads this once at the start and replaces it whole at the end.
    private FrozenDictionary<int, LastGood> _lastGood = FrozenDictionary<int, LastGood>.Empty;

    public async Task<IReadOnlyList<Story>> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var ids = await client.GetBestStoryIdsAsync(cancellationToken);
        var lastGood = _lastGood;

        // Fetch the details a few at a time, so we never open hundreds of connections at once.
        // Each task writes to its own slot in the arrays, so no locking is needed.
        var found = new Story?[ids.Count];
        var timesKept = new int[ids.Count]; // 0 for a story loaded by this refresh
        var failed = 0; // threw, or came back with nothing we can show
        var kept = 0;   // failed, and kept its last good version
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
                    KeepLastGood(index, id);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One bad story must not spoil the whole refresh. Count it, and keep its last good version if there
                // is one. A timeout lands here too, whatever exception it surfaces as; only the refresh itself being
                // cancelled stops the loop.
                Interlocked.Increment(ref failed);
                logger.LogStoryFailed(ex, id);
                KeepLastGood(index, id);
            }
        });

        // Rank the WHOLE list by score, highest first. OrderBy is stable, so equal scores
        // keep the order Hacker News gave. Taking the first n happens later, per request.
        var stories = found.OfType<Story>().OrderByDescending(s => s.Score).ToList();
        var loadedNow = stories.Count - kept;
        var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogFetched(loadedNow, ids.Count, gone, failed, kept, elapsedMilliseconds);

        // Nothing loaded now means something is wrong upstream, however many old versions there are to keep.
        // Throwing keeps the old snapshot in place and leaves the last good versions as they were.
        if (loadedNow == 0)
            throw new InvalidOperationException("Hacker News returned no usable stories.");

        // Remember this refresh's stories, kept versions included, even if it turns out incomplete: each one is
        // the newest good version there is. A story that has gone, or has left the list, is forgotten.
        _lastGood = RememberByIds(ids, found, timesKept);

        // Failures are not independent: when Hacker News wobbles, many fetches fail together, and a ranking
        // of the survivors can leave out the real top stories. Past the limit the refresh is incomplete, and
        // the caller decides what to do with it. A kept version still counts as a failure here: a refresh made
        // mostly of last minute's stories is not a fresh one. A deleted or dead story is not a failure.
        var allowed = Math.Max(1, (int)Math.Floor(ids.Count * options.Value.MaxFailedFetchFraction));
        if (failed > allowed)
            throw new IncompleteRefreshException(stories, failed, ids.Count);

        return stories;

        void KeepLastGood(int index, int id)
        {
            if (!lastGood.TryGetValue(id, out var last) || last.TimesKept >= MaxRefreshesKept)
                return;

            found[index] = last.Story;
            timesKept[index] = last.TimesKept + 1;
            Interlocked.Increment(ref kept);
        }
    }

    /// <summary>Each story this refresh has, by its id. Hacker News lists an id once; if not, the last one wins.</summary>
    private static FrozenDictionary<int, LastGood> RememberByIds(IReadOnlyList<int> ids, Story?[] found, int[] timesKept)
    {
        var byId = new Dictionary<int, LastGood>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            if (found[index] is { } story)
                byId[ids[index]] = new LastGood(story, timesKept[index]);
        }

        return byId.ToFrozenDictionary();
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

    /// <summary>A story's last good version, and how many refreshes in a row it has been kept instead of loaded.</summary>
    private readonly record struct LastGood(Story Story, int TimesKept);
}
