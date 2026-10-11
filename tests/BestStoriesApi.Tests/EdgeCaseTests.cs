using System.Net;
using System.Text.Json;
using BestStoriesApi.HackerNews;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace BestStoriesApi.Tests;

/// <summary>HEAD, a very large n, a slow first refresh, and settings that cannot work.</summary>
public class EdgeCaseTests
{
    private static Mock<IHackerNewsClient> ThreeStories() => TestData.FakeHackerNews(
        (1, TestData.Item("one", 30)), (2, TestData.Item("two", 20)), (3, TestData.Item("three", 10)));

    [Test]
    public async Task HEAD_gets_the_GET_headers_and_no_body()
    {
        await using var app = new BestStoriesApiFactory(ThreeStories());
        using var http = app.CreateClient();
        var get = await http.GetAsync("/api/beststories?n=2");

        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/beststories?n=2"));

        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(head.Headers.ETag, Is.EqualTo(get.Headers.ETag));
        Assert.That(head.Content.Headers.ContentLength, Is.EqualTo((await get.Content.ReadAsByteArrayAsync()).Length));
        Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
    }

    [TestCase("2147483648")]
    [TestCase("99999999999999999999999999")]
    public async Task An_n_too_big_for_an_int_returns_everything(string n)
    {
        await using var app = new BestStoriesApiFactory(ThreeStories());
        using var http = app.CreateClient();

        var response = await http.GetAsync($"/api/beststories?n={n}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(3));
    }

    [TestCase("-99999999999999999999999999")]
    [TestCase("1e3")]
    public async Task A_huge_negative_or_a_non_integer_n_is_still_a_400(string n)
    {
        await using var app = new BestStoriesApiFactory(ThreeStories());
        using var http = app.CreateClient();

        Assert.That((await http.GetAsync($"/api/beststories?n={n}")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase(7000, 7)] // not the default of 5, so a fixed Retry-After cannot pass
    [TestCase(1500, 2)] // rounded up: coming back early would only meet another 503
    [TestCase(50, 1)]   // Retry-After is in whole seconds, and at least one
    public async Task A_caller_waits_for_the_first_snapshot_no_longer_than_the_limit_then_gets_503_and_when_to_try_again(
        int retryIntervalMilliseconds, int retryAfterSeconds)
    {
        var fake = new Mock<IHackerNewsClient>();
        fake.Setup(c => c.GetBestStoryIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (IReadOnlyList<int>)[]; });
        await using var app = new BestStoriesApiFactory(fake, configure: o =>
        {
            o.FirstSnapshotWait = TimeSpan.FromMilliseconds(300);
            o.FirstSnapshotRetryInterval = TimeSpan.FromMilliseconds(retryIntervalMilliseconds);
        });
        using var http = app.CreateClient();

        var response = await http.GetAsync("/api/beststories?n=1").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(retryAfterSeconds)));
    }

    [TestCase("MaxParallelFetches")]
    [TestCase("RefreshInterval")]
    [TestCase("BaseAddress")]
    [TestCase("MaxFailedFetchFraction")]
    [TestCase("FirstSnapshotWait")]
    [TestCase("FirstSnapshotRetryInterval")]
    public void A_setting_that_cannot_work_stops_the_app_at_start(string setting)
    {
        Action<HackerNewsOptions> bad = setting switch
        {
            "MaxParallelFetches" => o => o.MaxParallelFetches = 0,
            "RefreshInterval" => o => o.RefreshInterval = TimeSpan.Zero,
            "BaseAddress" => o => o.BaseAddress = "not a url",
            "FirstSnapshotWait" => o => o.FirstSnapshotWait = TimeSpan.Zero,
            "FirstSnapshotRetryInterval" => o => o.FirstSnapshotRetryInterval = TimeSpan.Zero,
            _ => o => o.MaxFailedFetchFraction = 1.5,
        };
        var services = new ServiceCollection();
        services.AddHackerNewsOptions(new ConfigurationBuilder().Build());
        services.Configure(bad);
        using var provider = services.BuildServiceProvider();

        // The host runs this validator as it starts, before anything else, so the test asks it directly. Starting the
        // whole app only to watch it fail is a race: WebApplicationFactory sometimes finds the failed app already
        // disposed, and reports that instead of why it failed.
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.That(ex!.Message, Does.Contain(setting));
    }
}
