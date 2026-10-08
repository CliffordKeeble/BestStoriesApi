using BestStoriesApi.HackerNews;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>
/// Starts the real app in memory, with the Hacker News client swapped for a Moq fake.
/// The refresh service, store, controller and JSON output are all the real ones.
/// </summary>
public class BestStoriesApiFactory(Mock<IHackerNewsClient> fakeHackerNews, TimeSpan? refreshInterval = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHackerNewsClient>();
            services.AddSingleton(fakeHackerNews.Object);

            // A long default, so a test sees exactly one refresh unless it asks for more.
            services.Configure<HackerNewsOptions>(o => o.RefreshInterval = refreshInterval ?? TimeSpan.FromHours(1));
        });
    }
}
