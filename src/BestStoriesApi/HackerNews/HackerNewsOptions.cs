namespace BestStoriesApi.HackerNews;

/// <summary>Settings from the "HackerNews" section of appsettings.json.</summary>
public class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    /// <summary>Base address of the Hacker News API. Must end with a slash.</summary>
    public string BaseAddress { get; set; } = "https://hacker-news.firebaseio.com/v0/";

    /// <summary>How often the snapshot of best stories is rebuilt.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many story details are fetched at the same time during a refresh.</summary>
    public int MaxParallelFetches { get; set; } = 10;

    /// <summary>
    /// The share of story fetches that may fail before a refresh counts as incomplete (one failure is always
    /// allowed). An incomplete refresh never replaces a good snapshot.
    /// </summary>
    public double MaxFailedFetchFraction { get; set; } = 0.1;

    /// <summary>
    /// The longest a request waits for the first snapshot after start-up before it gets 503. Without a limit, a
    /// slow first refresh would hold every early caller until the caller gave up.
    /// </summary>
    public TimeSpan FirstSnapshotWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Until there is a first snapshot, a failed refresh is tried again after this long, not after a whole
    /// RefreshInterval, then after twice as long each time, up to RefreshInterval. A 503 sent before the first
    /// snapshot carries it as Retry-After. Once there is a snapshot, refreshes follow RefreshInterval.
    /// </summary>
    public TimeSpan FirstSnapshotRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
