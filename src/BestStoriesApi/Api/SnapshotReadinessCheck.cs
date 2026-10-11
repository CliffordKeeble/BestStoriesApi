using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BestStoriesApi.Api;

/// <summary>
/// Ready means there is a snapshot to serve, so a request never has to wait for Hacker News. Reads only the
/// store, never Hacker News.
/// <list type="bullet">
///   <item>Unhealthy (503) before the first snapshot.</item>
///   <item>Healthy (200) while the snapshot is fresh.</item>
///   <item>Degraded (200) once it is older than three refresh intervals, which means refreshes are failing or are
///         taking far longer than they should. It is still served: an old list beats no list.</item>
/// </list>
/// The answer says how many stories are being served and how old they are.
/// </summary>
public sealed class SnapshotReadinessCheck(StoryStore store, IOptions<HackerNewsOptions> options, TimeProvider clock) : IHealthCheck
{
    private const int StaleAfterRefreshIntervals = 3;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = store.Current;
        if (snapshot is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("No snapshot yet: the first refresh has not finished, or has not worked"));

        var age = clock.GetUtcNow() - snapshot.BuiltAt;
        var data = new Dictionary<string, object>
        {
            ["stories"] = snapshot.Count,
            ["builtAt"] = snapshot.BuiltAt,
            ["ageSeconds"] = (long)age.TotalSeconds,
        };

        return Task.FromResult(age > options.Value.RefreshInterval * StaleAfterRefreshIntervals
            ? HealthCheckResult.Degraded("Serving a snapshot older than three refresh intervals: refreshes are failing or slow", data: data)
            : HealthCheckResult.Healthy("Serving the latest snapshot", data));
    }
}
