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

    /// <summary>
    /// Whatever state the refresh loop reached, nobody may go on waiting for a first snapshot that will never
    /// come: they are released and get 503. This covers a stop that lands before the loop has even started,
    /// which the loop itself cannot see.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        store.RefreshFailed();
    }

    private async Task RefreshOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stories = await refresher.BuildSnapshotAsync(stoppingToken);
            var snapshot = StorySnapshot.Create(stories); // serialise once here, not once per request
            store.Publish(snapshot);
            logger.LogRefreshed(snapshot.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is shutting down. Release anyone still waiting for the first snapshot; they get 503.
            store.RefreshFailed();
        }
        catch (IncompleteRefreshException ex) when (store.Current is null)
        {
            // Nothing to fall back on yet: an incomplete list is better than none, and the next refresh replaces it.
            store.Publish(StorySnapshot.Create(ex.Partial));
            logger.LogFirstRefreshIncomplete(ex, ex.Failed, ex.Requested, ex.Partial.Count);
        }
        catch (IncompleteRefreshException ex)
        {
            // A good snapshot is already being served. A ranking of the survivors would be worse, so keep it.
            logger.LogRefreshIncomplete(ex, ex.Failed, ex.Requested);
            store.RefreshFailed();
        }
        catch (Exception ex)
        {
            // Keep serving the previous snapshot and try again on the next tick.
            logger.LogRefreshFailed(ex);
            store.RefreshFailed();
        }
    }
}
