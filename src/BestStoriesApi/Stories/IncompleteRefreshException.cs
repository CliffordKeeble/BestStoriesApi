namespace BestStoriesApi.Stories;

/// <summary>
/// A refresh in which too many stories failed to load (more than
/// <see cref="HackerNews.HackerNewsOptions.MaxFailedFetchFraction"/> of them). Its ranking is of whatever
/// survived, so its "top n" may leave out the real top stories. It carries what did load, so the caller
/// can decide: keep the previous snapshot if there is one, or serve this if there is nothing else.
/// </summary>
public sealed class IncompleteRefreshException(IReadOnlyList<Story> partial, int failed, int requested)
    : Exception($"{failed} of {requested} stories could not be loaded")
{
    public IReadOnlyList<Story> Partial { get; } = partial;
    public int Failed { get; } = failed;
    public int Requested { get; } = requested;
}
