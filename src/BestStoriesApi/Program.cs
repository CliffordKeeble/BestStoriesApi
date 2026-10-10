using BestStoriesApi.Api;
using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HackerNewsOptions>(builder.Configuration.GetSection(HackerNewsOptions.SectionName));

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
