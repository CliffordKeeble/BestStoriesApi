using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BestStoriesApi.HackerNews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BestStoriesApi.Tests;

/// <summary>
/// The real Hacker News client and its whole HTTP pipeline, with only the network replaced: the addresses it asks
/// for and how it reads Hacker News's JSON. Every other test fakes the client itself.
/// </summary>
public class HackerNewsHttpTests
{
    [Test]
    public async Task The_spec_example_story_comes_back_as_the_spec_shows_it_from_the_spec_addresses()
    {
        // The story in the spec, as Hacker News serves it: its own field names, Unix time, and fields we do not use.
        var hackerNews = new StubHackerNews(new Dictionary<string, string>
        {
            ["https://hacker-news.firebaseio.com/v0/beststories.json"] = "[21233041]",
            ["https://hacker-news.firebaseio.com/v0/item/21233041.json"] =
                """{"by":"ismaildonmez","descendants":572,"id":21233041,"kids":[21233229,21233577],"score":1716,"time":1570887781,"title":"A uBlock Origin update was rejected from the Chrome Web Store","type":"story","url":"https://github.com/uBlockOrigin/uBlock-issues/issues/745"}""",
        });
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddHttpClient(HackerNewsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => hackerNews)));
        using var http = app.CreateClient();

        var answer = JsonNode.Parse(await http.GetStringAsync("/api/beststories?n=1"));

        var asTheSpecShowsIt = JsonNode.Parse(
            """[{"title":"A uBlock Origin update was rejected from the Chrome Web Store","uri":"https://github.com/uBlockOrigin/uBlock-issues/issues/745","postedBy":"ismaildonmez","time":"2019-10-12T13:43:01+00:00","score":1716,"commentCount":572}]""");
        Assert.That(JsonNode.DeepEquals(answer, asTheSpecShowsIt), Is.True, answer?.ToJsonString());
        Assert.That(hackerNews.Requested, Is.EquivalentTo(new[]
        {
            "https://hacker-news.firebaseio.com/v0/beststories.json",
            "https://hacker-news.firebaseio.com/v0/item/21233041.json",
        }));
    }
}

/// <summary>Answers the addresses it was given, records every address asked for, and gives 404 for anything else.</summary>
internal sealed class StubHackerNews(IReadOnlyDictionary<string, string> answers) : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _requested = new();

    public IReadOnlyList<string> Requested => [.. _requested];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!.ToString();
        _requested.Enqueue(address);
        return Task.FromResult(answers.TryGetValue(address, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
