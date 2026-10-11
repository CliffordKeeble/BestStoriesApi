using Microsoft.Extensions.Options;

namespace BestStoriesApi.HackerNews;

public static class HackerNewsOptionsRegistration
{
    /// <summary>
    /// Reads the HackerNews settings and checks them when the app starts, so a bad value stops it with a message
    /// instead of failing every refresh.
    /// </summary>
    public static OptionsBuilder<HackerNewsOptions> AddHackerNewsOptions(this IServiceCollection services, IConfiguration configuration) =>
        services.AddOptions<HackerNewsOptions>()
            .Bind(configuration.GetSection(HackerNewsOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseAddress, UriKind.Absolute, out _) && o.BaseAddress.EndsWith('/'),
                "HackerNews:BaseAddress must be an absolute URL ending with '/'.")
            .Validate(o => o.RefreshInterval > TimeSpan.Zero, "HackerNews:RefreshInterval must be more than zero.")
            .Validate(o => o.MaxParallelFetches >= 1, "HackerNews:MaxParallelFetches must be at least 1.")
            .Validate(o => o.MaxFailedFetchFraction is >= 0 and <= 1, "HackerNews:MaxFailedFetchFraction must be between 0 and 1.")
            .Validate(o => o.FirstSnapshotWait > TimeSpan.Zero, "HackerNews:FirstSnapshotWait must be more than zero.")
            .Validate(o => o.FirstSnapshotRetryInterval > TimeSpan.Zero, "HackerNews:FirstSnapshotRetryInterval must be more than zero.")
            .ValidateOnStart();
}
