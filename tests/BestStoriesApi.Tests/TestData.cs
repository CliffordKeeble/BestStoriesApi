using BestStoriesApi.HackerNews;
using Moq;

namespace BestStoriesApi.Tests;

internal static class TestData
{
    /// <summary>2019-10-12T13:43:01Z as Unix seconds, the example time in the brief.</summary>
    public const long ExampleUnixTime = 1570887781;

    public static HackerNewsItem Item(string title, int score, string? url = "https://example.com/story") => new()
    {
        Title = title,
        Url = url,
        By = "someone",
        Time = ExampleUnixTime,
        Score = score,
        Descendants = 3
    };

    /// <summary>Makes a fake Hacker News that knows these stories. Ids are listed in the order given.</summary>
    public static Mock<IHackerNewsClient> FakeHackerNews(params (int Id, HackerNewsItem Item)[] stories)
    {
        var client = new Mock<IHackerNewsClient>();

        client.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stories.Select(s => s.Id).ToList());

        foreach (var (id, item) in stories)
        {
            client.Setup(c => c.GetItemAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        }

        return client;
    }
}
