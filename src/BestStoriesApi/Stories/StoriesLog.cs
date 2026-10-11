namespace BestStoriesApi.Stories;

/// <summary>
/// Every log message the refresh writes, in one place. Source-generated: a message whose level is switched
/// off costs nothing, and each carries an event id an operator can filter on.
/// </summary>
internal static partial class StoriesLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Refreshed best stories: {Count} stories")]
    public static partial void LogRefreshed(this ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "First refresh incomplete ({Failed} of {Requested} stories failed); serving the {Count} that loaded")]
    public static partial void LogFirstRefreshIncomplete(this ILogger logger, Exception exception, int failed, int requested, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Refresh incomplete ({Failed} of {Requested} stories failed); continuing to serve the previous snapshot")]
    public static partial void LogRefreshIncomplete(this ILogger logger, Exception exception, int failed, int requested);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Refresh failed; continuing to serve the previous snapshot")]
    public static partial void LogRefreshFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Skipping story {StoryId}: it could not be loaded")]
    public static partial void LogStorySkipped(this ILogger logger, Exception exception, int storyId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Refreshing from {BaseAddress} every {RefreshInterval}, {MaxParallelFetches} fetches at a time; a refresh is " +
                  "incomplete when more than {MaxFailedFetchFraction} of its fetches fail; callers wait up to " +
                  "{FirstSnapshotWait} for the first snapshot; a failed first refresh is tried again after {FirstSnapshotRetryInterval}, " +
                  "then twice as long each time, up to the refresh interval")]
    public static partial void LogStarting(this ILogger logger, string baseAddress, TimeSpan refreshInterval, int maxParallelFetches,
        double maxFailedFetchFraction, TimeSpan firstSnapshotWait, TimeSpan firstSnapshotRetryInterval);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Refresh started")]
    public static partial void LogRefreshStarted(this ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Fetched {Loaded} of {Requested} best stories in {ElapsedMilliseconds} ms: {Gone} deleted or dead, {Failed} failed")]
    public static partial void LogFetched(this ILogger logger, int loaded, int requested, int gone, int failed, long elapsedMilliseconds);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Story {StoryId} is deleted or dead on Hacker News, so it is left out")]
    public static partial void LogStoryGone(this ILogger logger, int storyId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Skipping story {StoryId}: Hacker News answered with nothing to show (no body, or no title or author)")]
    public static partial void LogStoryUnusable(this ILogger logger, int storyId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "Refresh failed and there is nothing to serve yet; callers get 503 until a refresh works")]
    public static partial void LogFirstRefreshFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "No snapshot yet; trying again in {Wait}")]
    public static partial void LogFirstSnapshotRetry(this ILogger logger, TimeSpan wait);
}
