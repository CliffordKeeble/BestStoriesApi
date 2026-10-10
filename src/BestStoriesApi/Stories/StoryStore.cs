namespace BestStoriesApi.Stories;

/// <summary>
/// Holds the current snapshot in memory. Requests read from here and nowhere else.
///
/// One writer (the refresh service) and any number of readers. The writer builds a whole new
/// <see cref="StorySnapshot"/> and publishes it by swapping one reference; readers take the reference once and
/// use that snapshot for the whole request. No reader ever waits for the writer, and none can see a half-built
/// snapshot.
/// </summary>
public class StoryStore
{
    // Completed once the first refresh has finished, whether it worked or not.
    private readonly TaskCompletionSource _firstAttemptDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile StorySnapshot? _snapshot;

    /// <summary>The snapshot now, or null before the first successful refresh. Never waits.</summary>
    public StorySnapshot? Current => _snapshot;

    /// <summary>
    /// The current snapshot, or null if the first refresh failed and nothing is available yet.
    /// A caller arriving before the first refresh finishes waits here for that one refresh.
    /// </summary>
    public async Task<StorySnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        await _firstAttemptDone.Task.WaitAsync(cancellationToken);
        return _snapshot;
    }

    public void Publish(StorySnapshot snapshot)
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
