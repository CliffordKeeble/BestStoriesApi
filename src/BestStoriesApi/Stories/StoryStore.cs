namespace BestStoriesApi.Stories;

/// <summary>
/// Holds the current ranked snapshot in memory. Requests read from here and nowhere else.
/// </summary>
public class StoryStore
{
    // Completed once the first refresh has finished, whether it worked or not.
    private readonly TaskCompletionSource _firstAttemptDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Swapping a reference is atomic, so readers always see a whole snapshot, never a half-built one.
    private volatile IReadOnlyList<Story>? _snapshot;

    /// <summary>
    /// The current snapshot, or null if the first refresh failed and nothing is available yet.
    /// A caller arriving before the first refresh finishes waits here for that one refresh.
    /// </summary>
    public async Task<IReadOnlyList<Story>?> GetAsync(CancellationToken cancellationToken)
    {
        await _firstAttemptDone.Task.WaitAsync(cancellationToken);
        return _snapshot;
    }

    public void Publish(IReadOnlyList<Story> snapshot)
    {
        _snapshot = snapshot;
        _firstAttemptDone.TrySetResult();
    }

    /// <summary>
    /// Called when a refresh fails. Releases anyone waiting on the first refresh.
    /// An existing snapshot is left alone, so it keeps being served.
    /// </summary>
    public void RefreshFailed() => _firstAttemptDone.TrySetResult();
}
