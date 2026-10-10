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
}
