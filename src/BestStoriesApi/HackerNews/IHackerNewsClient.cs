namespace BestStoriesApi.HackerNews;

public interface IHackerNewsClient
{
    /// <summary>The ids of the best stories, in the order Hacker News gives them.</summary>
    Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>One item by id, or null if Hacker News has nothing for that id.</summary>
    Task<HackerNewsItem?> GetItemAsync(int id, CancellationToken cancellationToken);
}
