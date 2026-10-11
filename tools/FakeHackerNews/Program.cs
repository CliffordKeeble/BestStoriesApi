// A stand-in for the Hacker News API, for load tests. It serves the two endpoints the API uses,
// counts every call, and adds a delay to each one so a refresh takes about as long as it would
// against the real thing. Scores move over time, the way they do on Hacker News, so one refresh
// can rank the stories differently from the last.
//
//   dotnet run -c Release --project tools/FakeHackerNews
//
// Options (environment variables or --Name=value on the command line):
//   Fake__Port            5099
//   Fake__Stories         200    how many ids beststories.json returns
//   Fake__LatencyMs       40     delay added to every call, in milliseconds
//   Fake__ChangesPerMinute 120   how many best-story scores change per minute
//
// GET  /stats        what has been called, as JSON
// POST /stats/reset  zero the counters

using System.Text.Json;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var port = builder.Configuration.GetValue("Fake:Port", 5099);
var storyCount = builder.Configuration.GetValue("Fake:Stories", 200);
var latency = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Fake:LatencyMs", 40));
var changesPerMinute = builder.Configuration.GetValue("Fake:ChangesPerMinute", 120);

builder.WebHost.UseUrls($"http://localhost:{port}");

var world = new FakeWorld(storyCount);
var app = builder.Build();

// Score changes run on their own clock, independent of who is calling.
if (changesPerMinute > 0)
{
    var period = TimeSpan.FromMinutes(1) / changesPerMinute;
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            world.ChangeOneScore();
    });
}

app.MapGet("/v0/beststories.json", async () =>
{
    world.Count("beststories");
    await Task.Delay(latency);
    return Results.Json(world.BestIds());
});

app.MapGet("/v0/item/{id:int}.json", async (int id) =>
{
    world.Count("item");
    await Task.Delay(latency);
    var item = world.Item(id);
    return item is null ? Results.Text("null", "application/json") : Results.Json(item); // Hacker News answers null too
});

app.MapGet("/stats", () => Results.Json(world.Stats()));
app.MapPost("/stats/reset", () => { world.ResetCounts(); return Results.NoContent(); });

Console.WriteLine($"Fake Hacker News on http://localhost:{port}/v0/  stories={storyCount} latency={latency.TotalMilliseconds}ms changes/min={changesPerMinute}");
app.Run();

/// <summary>The fake's data: a fixed set of stories whose scores drift, and the calls made against it.</summary>
sealed class FakeWorld
{
    private const int FirstId = 45_000_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Dictionary<string, object?>> _items = new();
    private readonly List<int> _best = new();
    private readonly Random _random = new(20261010);
    private readonly Dictionary<string, long> _calls = new() { ["beststories"] = 0, ["item"] = 0 };
    private DateTimeOffset _countingSince = DateTimeOffset.UtcNow;

    public FakeWorld(int storyCount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < storyCount; i++)
        {
            var id = FirstId + i;
            _best.Add(id);
            var askHn = i % 17 == 5;
            var item = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["type"] = "story",
                ["by"] = $"user{i % 37}",
                ["time"] = now - 600L * i,
                ["title"] = askHn ? $"Ask HN: question number {i}" : $"Story number {i}",
                ["score"] = _random.Next(50, 2500),
                ["descendants"] = _random.Next(0, 900),
            };
            if (!askHn) item["url"] = $"https://example.com/story/{i}"; // an Ask HN post has no url at all
            _items[id] = item;
        }
    }

    public void Count(string endpoint)
    {
        lock (_gate) _calls[endpoint]++;
    }

    public List<int> BestIds()
    {
        lock (_gate) return [.. _best];
    }

    public Dictionary<string, object?>? Item(int id)
    {
        lock (_gate) return _items.TryGetValue(id, out var item) ? new Dictionary<string, object?>(item) : null;
    }

    public void ChangeOneScore()
    {
        lock (_gate)
        {
            var id = _best[_random.Next(_best.Count)];
            var item = _items[id];
            item["score"] = (int)item["score"]! + _random.Next(1, 25);
            item["descendants"] = (int)item["descendants"]! + _random.Next(0, 4);
        }
    }

    public object Stats()
    {
        lock (_gate)
        {
            var seconds = (DateTimeOffset.UtcNow - _countingSince).TotalSeconds;
            var total = _calls.Values.Sum();
            return new
            {
                since = _countingSince,
                seconds = Math.Round(seconds, 1),
                beststories = _calls["beststories"],
                item = _calls["item"],
                total,
                perMinute = seconds > 0 ? Math.Round(total * 60 / seconds, 1) : 0,
            };
        }
    }

    public void ResetCounts()
    {
        lock (_gate)
        {
            foreach (var key in _calls.Keys.ToList()) _calls[key] = 0;
            _countingSince = DateTimeOffset.UtcNow;
        }
    }
}
