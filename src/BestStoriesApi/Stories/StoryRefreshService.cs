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
        var settings = options.Value;
        logger.LogStarting(settings.BaseAddress, settings.RefreshInterval, settings.MaxParallelFetches,
            settings.MaxFailedFetchFraction, settings.FirstSnapshotWait, settings.FirstSnapshotRetryInterval);

        await RefreshOnceAsync(stoppingToken); // straight away at startup

        // Until there is something to serve, try again soon: a minute of 503s after Hacker News is back is a minute
        // too long. The wait doubles after each failed try, up to RefreshInterval, so a Hacker News that keeps failing
        // is soon asked no more often than usual. Once there is a snapshot, a failed refresh just keeps it.
        var wait = Shorter(settings.FirstSnapshotRetryInterval, settings.RefreshInterval);
        while (store.Current is null && !stoppingToken.IsCancellationRequested)
        {
            logger.LogFirstSnapshotRetry(wait);
            await Task.Delay(wait, stoppingToken);
            await RefreshOnceAsync(stoppingToken);
            wait = Shorter(wait * 2, settings.RefreshInterval);
        }

        using var timer = new PeriodicTimer(settings.RefreshInterval);
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
        logger.LogRefreshStarted();
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
        catch (Exception ex) when (store.Current is null)
        {
            // Nothing to serve yet, so callers get 503. The loop above tries again soon.
            logger.LogFirstRefreshFailed(ex);
            store.RefreshFailed();
        }
        catch (Exception ex)
        {
            // Keep serving the previous snapshot and try again on the next tick.
            logger.LogRefreshFailed(ex);
            store.RefreshFailed();
        }
    }

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;
}
