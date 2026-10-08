using System.Text.Json.Serialization;

namespace BestStoriesApi.HackerNews;

/// <summary>One item as Hacker News returns it. Only the fields we use.</summary>
public class HackerNewsItem
{
    [JsonPropertyName("title")] public string? Title { get; set; }

    /// <summary>The link the story points to. Absent on "Ask HN" posts.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("by")] public string? By { get; set; }

    /// <summary>Unix time in seconds.</summary>
    [JsonPropertyName("time")] public long Time { get; set; }

    [JsonPropertyName("score")] public int Score { get; set; }

    /// <summary>Total comment count. Absent when there are no comments.</summary>
    [JsonPropertyName("descendants")] public int Descendants { get; set; }

    [JsonPropertyName("deleted")] public bool Deleted { get; set; }

    [JsonPropertyName("dead")] public bool Dead { get; set; }
}
