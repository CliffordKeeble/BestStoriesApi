namespace BestStoriesApi.Tests;

/// <summary>
/// A clock that stands still until a test moves it. Only the time of day is faked: timers, such as the refresh
/// loop's, still run on real time.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}
