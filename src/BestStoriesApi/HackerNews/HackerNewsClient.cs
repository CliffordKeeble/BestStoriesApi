using System.Net.Http.Json;

namespace BestStoriesApi.HackerNews;

/// <summary>
/// Talks to Hacker News. The retry and timeout rules are not here: they sit on the
/// named HttpClient (see Program.cs), so this class only builds URLs and reads JSON.
/// </summary>
public class HackerNewsClient(IHttpClientFactory httpClientFactory) : IHackerNewsClient
{
    public const string HttpClientName = "HackerNews";

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var ids = await http.GetFromJsonAsync<List<int>>("beststories.json", cancellationToken);
        return ids ?? [];
    }

    public async Task<HackerNewsItem?> GetItemAsync(int id, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        return await http.GetFromJsonAsync<HackerNewsItem>($"item/{id}.json", cancellationToken);
    }
}
