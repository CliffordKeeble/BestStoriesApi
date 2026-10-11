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
}
