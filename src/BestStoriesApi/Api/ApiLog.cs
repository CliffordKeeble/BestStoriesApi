namespace BestStoriesApi.Api;

/// <summary>
/// The endpoint's log messages. Debug only, because they are per request: off by default, and source-generated, so
/// while they are off they cost nothing.
/// </summary>
internal static partial class ApiLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
        Message = "Answered 503: no snapshot to serve yet (gave up waiting for the first refresh: {TimedOut})")]
    public static partial void LogNoSnapshotYet(this ILogger logger, bool timedOut);
}
