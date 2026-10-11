using BestStoriesApi.Api;
using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Settings are checked when the app starts, so a bad value stops it with a message instead of failing every refresh.
builder.Services.AddHackerNewsOptions(builder.Configuration);

// A named HttpClient for Hacker News. The standard resilience handler adds retries, a timeout per attempt and an
// overall timeout per call, so no single call can hold a refresh for long. A refresh as a whole is not bounded.
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
builder.Services.AddSingleton(TimeProvider.System);

// Readiness says whether there is a snapshot to serve and how old it is. Neither health endpoint calls Hacker News.
builder.Services.AddHealthChecks().AddCheck<SnapshotReadinessCheck>("snapshot", tags: ["ready"]);

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
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false, // the process answers, which is all liveness asks
    ResponseWriter = HealthResponseWriter.WriteAsync,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();

// Lets the integration tests start the app in memory.
public partial class Program;
