namespace BestStoriesApi.Stories;

/// <summary>A story as our API returns it. Property names become camelCase in the JSON.</summary>
public record Story(
    string Title,
    string? Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);
