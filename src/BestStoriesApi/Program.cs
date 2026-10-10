using BestStoriesApi.Api;
using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Settings are checked when the app starts, so a bad value stops it with a message instead of failing every refresh.
builder.Services.AddOptions<HackerNewsOptions>()
    .Bind(builder.Configuration.GetSection(HackerNewsOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.BaseAddress, UriKind.Absolute, out _) && o.BaseAddress.EndsWith('/'),
        "HackerNews:BaseAddress must be an absolute URL ending with '/'.")
    .Validate(o => o.RefreshInterval > TimeSpan.Zero, "HackerNews:RefreshInterval must be more than zero.")
    .Validate(o => o.MaxParallelFetches >= 1, "HackerNews:MaxParallelFetches must be at least 1.")
    .Validate(o => o.MaxFailedFetchFraction is >= 0 and <= 1, "HackerNews:MaxFailedFetchFraction must be between 0 and 1.")
    .Validate(o => o.FirstSnapshotWait > TimeSpan.Zero, "HackerNews:FirstSnapshotWait must be more than zero.")
    .ValidateOnStart();

// A named HttpClient for Hacker News. The standard resilience handler adds retries,
// a timeout per attempt and an overall timeout, so a slow or flaky upstream cannot hang a refresh.
builder.Services
    .AddHttpClient(HackerNewsClient.HttpClientName, (serviceProvider, http) =>
    {
        http.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value.BaseAddress);
    })
    .AddStandardResilienceHandler();

builder.Services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
builder.Services.AddSingleton<StoryRefresher>();
builder.Services.AddSingleton<StoryStore>();
builder.Services.AddHostedService<StoryRefreshService>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.OperationFilter<BestStoriesOpenApi>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapBestStories();

app.Run();

// Lets the integration tests start the app in memory.
public partial class Program;
