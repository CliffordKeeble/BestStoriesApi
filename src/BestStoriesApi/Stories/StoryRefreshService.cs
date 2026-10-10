using BestStoriesApi.HackerNews;
using Microsoft.Extensions.Options;

namespace BestStoriesApi.Stories;

/// <summary>
/// Runs for the life of the app and rebuilds the snapshot on a fixed interval. There is
/// exactly one of these, so there is one refresh at a time however many callers arrive.
/// </summary>
public class StoryRefreshService(
    StoryRefresher refresher,
    StoryStore store,
    IOptions<HackerNewsOptions> options,
    ILogger<StoryRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshOnceAsync(stoppingToken); // straight away at startup, then on every tick

        using var timer = new PeriodicTimer(options.Value.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshOnceAsync(stoppingToken);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stories = await refresher.BuildSnapshotAsync(stoppingToken);
            var snapshot = StorySnapshot.Create(stories); // serialise once here, not once per request
            store.Publish(snapshot);
            logger.LogInformation("Refreshed best stories: {Count} stories", snapshot.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is shutting down. Nothing to report.
        }
        catch (Exception ex)
        {
            // Keep serving the previous snapshot and try again on the next tick.
            logger.LogError(ex, "Refresh failed; continuing to serve the previous snapshot");
            store.RefreshFailed();
        }
    }
}
