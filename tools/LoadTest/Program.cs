// Load test for the best-stories API. No packages: it needs only the .NET SDK, so a reviewer can run it.
//
// OPEN LOOP (the default) sends requests on a fixed schedule, whatever the server is doing, and times each
// one from the moment it was DUE, not from the moment it was sent. A server that stalls therefore pays for
// every request that queued behind the stall. A tool that waits for a reply before sending the next request
// (a closed loop) quietly sends fewer requests while the server is slow and under-reports the tail; this is
// "coordinated omission" (Gil Tene). Open loop is how the latency percentiles in docs/performance.md are measured.
//
// CLOSED LOOP (--mode closed) keeps C requests in flight as fast as the server answers. It measures the most
// requests per second the server can take, not latency under a stated load.
//
// IT CHECKS ITSELF. Each request's real send time is recorded. If 1% of requests left more than 1 ms after they
// were due, the client could not keep the schedule and the row is marked not valid. The worst send delay is
// reported beside it. Give it cores of its own: a .NET client sharing one core with a server cannot send
// thousands a second on time. How it waits between sends is chosen at start (see Pacing): Windows' default
// timer tick is about 15.6 ms, too coarse to sleep on.
//
//   dotnet run -c Release --project tools/LoadTest -- --url "http://localhost:5028/api/beststories?n=10" --rate 5000
//   dotnet run -c Release --project tools/LoadTest -- --suite --base http://localhost:5028 --hn-stats http://localhost:5099/stats

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

var options = Options.Parse(args);
Pacing.Choose(options.Wait);

// Start with enough pool threads that the client never waits for the pool to grow mid-run: thread
// injection is slow by design, and a client stall would be timed as server latency.
ThreadPool.SetMinThreads(Math.Max(64, Environment.ProcessorCount * 8), Math.Max(64, Environment.ProcessorCount * 8));
using var client = Http.NewClient(options.Connections);

if (options.Suite)
{
    await Suite.RunAsync(client, options);
    return 0;
}

if (options.Url is null)
{
    Console.Error.WriteLine("--url is required unless --suite is given. See the comment at the top of Program.cs.");
    return 2;
}

var etag = options.UseEtag ? await Http.FetchEtagAsync(client, options.Url) : null;
var result = options.Mode == "closed"
    ? await Runner.ClosedLoopAsync(client, options.Url, options.Concurrency, options.Warmup, options.Duration, etag)
    : await Runner.OpenLoopAsync(client, options.Url, options.Rate, options.Warmup, options.Duration, etag);
Console.WriteLine(Machine.Describe());
Console.WriteLine(Report.Header());
Console.WriteLine(Report.Row(options.Label ?? options.Url, result));
return result.Valid ? 0 : 1;

sealed record Options(
    string? Url, string Mode, int Rate, int Concurrency, int Connections, TimeSpan Warmup, TimeSpan Duration,
    bool Suite, string? Base, string? HnStats, bool UseEtag, string? Label, int[] SuiteRates, int[] SuiteNs, string Wait)
{
    public static Options Parse(string[] args)
    {
        string? Get(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Has(string name) => args.Contains(name);
        int[] Ints(string? s, int[] fallback) => s is null ? fallback : s.Split(',').Select(int.Parse).ToArray();
        return new Options(
            Url: Get("--url"),
            Mode: Get("--mode") ?? "open",
            Rate: int.Parse(Get("--rate") ?? "1000"),
            Concurrency: int.Parse(Get("--concurrency") ?? "64"),
            Connections: int.Parse(Get("--connections") ?? "256"),
            Warmup: TimeSpan.FromSeconds(double.Parse(Get("--warmup") ?? "5")),
            Duration: TimeSpan.FromSeconds(double.Parse(Get("--duration") ?? "20")),
            Suite: Has("--suite"),
            Base: Get("--base") ?? "http://localhost:5028",
            HnStats: Get("--hn-stats"),
            UseEtag: Has("--etag"),
            Label: Get("--label"),
            SuiteRates: Ints(Get("--rates"), [2000, 5000, 7500]),
            SuiteNs: Ints(Get("--ns"), [10, 200]),
            Wait: Get("--wait") ?? "auto");
    }
}

sealed record Result(
    string Mode, int TargetRate, long Sent, long Ok, long NotModified, long Failed, double Seconds, long BytesPerResponse,
    double P50, double P90, double P99, double P999, double Max, double SendLagP99Ms, double SendLagMaxMs, string? Warning)
{
    public double Achieved => Sent / Seconds;
    public bool Valid => Warning is null;
}

static class Http
{
    public static HttpClient NewClient(int connections) => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = connections,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string?> FetchEtagAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return response.Headers.ETag?.ToString();
    }

    /// <summary>One request, its body read to the end. Returns the status and the body length.</summary>
    public static async Task<(HttpStatusCode Status, long Bytes)> SendAsync(HttpClient client, string url, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0) total += read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (response.StatusCode, total);
    }
}

static class Runner
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    public static async Task<Result> OpenLoopAsync(HttpClient client, string url, int rate, TimeSpan warmup, TimeSpan duration, string? etag)
    {
        var warmupCount = (long)(rate * warmup.TotalSeconds);
        var total = warmupCount + (long)(rate * duration.TotalSeconds);
        var period = (double)Stopwatch.Frequency / rate;
        var latency = new long[total];
        var status = new int[total];
        var bytes = new long[total];
        var sendLag = new long[total];
        var inFlight = new CountdownEvent(1);

        // The dispatcher sends everything that has fallen due, then waits for the next one (see Pacing). Nothing
        // here can flatter the result: every request is timed from its own due time, so a late send counts
        // against the server. How late each send actually started is recorded too, so the report can say how
        // much of the latency is the client's own.
        var start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10; // begin 100 ms from now
        var dispatcher = new Thread(() =>
        {
            long i = 0;
            while (i < total)
            {
                var now = Stopwatch.GetTimestamp();
                for (long due; i < total && (due = start + (long)(i * period)) <= now; i++)
                {
                    inFlight.AddCount();
                    var index = i;
                    var dueAt = due;
                    ThreadPool.UnsafeQueueUserWorkItem(async _ =>
                    {
                        sendLag[index] = Stopwatch.GetTimestamp() - dueAt;
                        try
                        {
                            var (code, length) = await Http.SendAsync(client, url, etag);
                            status[index] = (int)code;
                            bytes[index] = length;
                        }
                        catch
                        {
                            status[index] = -1;
                        }
                        finally
                        {
                            latency[index] = Stopwatch.GetTimestamp() - dueAt;
                            inFlight.Signal();
                        }
                    }, null);
                }
                var wait = start + (long)(i * period) - Stopwatch.GetTimestamp();
                if (wait > 0) Pacing.WaitUpTo(wait);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Highest, Name = "dispatcher" };

        dispatcher.Start();
        dispatcher.Join();
        inFlight.Signal();
        await Task.Run(() => inFlight.Wait(TimeSpan.FromSeconds(60)));

        // Valid when 99% of requests left within 1 ms of their due time: then the client's own delay is small
        // beside what is being measured, up to p99. A rarer, longer delay (the client thread preempted once) is
        // still charged to the result above p99, and the worst one is reported beside it.
        var lags = sendLag.AsSpan((int)warmupCount).ToArray();
        Array.Sort(lags);
        var lagP99 = lags.Length == 0 ? 0 : lags[Math.Max(0, (int)Math.Ceiling(0.99 * lags.Length) - 1)] / TicksPerMs;
        var lagMax = lags.Length == 0 ? 0 : lags[^1] / TicksPerMs;
        var warning = lagP99 > 1.0
            ? $"1% of requests left more than {lagP99:F1} ms after they were due: this client could not keep the schedule at {rate}/s, so the result measures the client as much as the server and is not valid"
            : null;
        return Summarise("open", rate, latency.AsSpan((int)warmupCount), status.AsSpan((int)warmupCount), bytes.AsSpan((int)warmupCount), duration.TotalSeconds, lagP99, lagMax, warning);
    }

    public static async Task<Result> ClosedLoopAsync(HttpClient client, string url, int concurrency, TimeSpan warmup, TimeSpan duration, string? etag)
    {
        var samples = new List<(long Latency, int Status, long Bytes)>[concurrency];
        var measureFrom = Stopwatch.GetTimestamp() + (long)(warmup.TotalSeconds * Stopwatch.Frequency);
        var stopAt = measureFrom + (long)(duration.TotalSeconds * Stopwatch.Frequency);

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
        {
            var mine = samples[w] = new List<(long, int, long)>(200_000);
            while (true)
            {
                var sent = Stopwatch.GetTimestamp();
                if (sent >= stopAt) break;
                int code; long length = 0;
                try { (var c, length) = await Http.SendAsync(client, url, etag); code = (int)c; }
                catch { code = -1; }
                if (sent >= measureFrom) mine.Add((Stopwatch.GetTimestamp() - sent, code, length));
            }
        })));

        var all = samples.SelectMany(s => s).ToArray();
        return Summarise("closed", 0, all.Select(s => s.Latency).ToArray(), all.Select(s => s.Status).ToArray(), all.Select(s => s.Bytes).ToArray(), duration.TotalSeconds, 0, 0, null);
    }

    private static Result Summarise(string mode, int rate, Span<long> latency, Span<int> status, Span<long> bytes, double seconds, double lagP99, double lagMax, string? warning)
    {
        long ok = 0, notModified = 0, failed = 0, size = 0;
        for (var i = 0; i < status.Length; i++)
        {
            switch (status[i])
            {
                case 200: ok++; size = bytes[i]; break;
                case 304: notModified++; break;
                default: failed++; break;
            }
        }
        var sorted = latency.ToArray();
        Array.Sort(sorted);
        double At(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)] / TicksPerMs;
        if (failed > 0) warning ??= $"{failed} request(s) failed or returned an unexpected status";
        return new Result(mode, rate, sorted.Length, ok, notModified, failed, seconds, size,
            At(0.50), At(0.90), At(0.99), At(0.999), sorted.Length == 0 ? double.NaN : sorted[^1] / TicksPerMs, lagP99, lagMax, warning);
    }
}

static class Report
{
    public static string Header() =>
        "| scenario | mode | target/s | achieved/s | ok | 304 | failed | bytes | p50 ms | p90 ms | p99 ms | p99.9 ms | max ms | send lag p99 / max ms |\n" +
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|";

    public static string Row(string label, Result r) =>
        $"| {label} | {r.Mode} | {(r.TargetRate > 0 ? r.TargetRate.ToString() : "-")} | {r.Achieved:F0} | {r.Ok} | {r.NotModified} | {r.Failed} | {r.BytesPerResponse} | " +
        $"{r.P50:F3} | {r.P90:F3} | {r.P99:F3} | {r.P999:F3} | {r.Max:F1} | {(r.Mode == "open" ? $"{r.SendLagP99Ms:F2} / {r.SendLagMaxMs:F1}" : "-")} |" + (r.Valid ? "" : $" ⚠ {r.Warning}");
}

static class Machine
{
    public static string Describe()
    {
        var cpu = RuntimeInformation.ProcessArchitecture.ToString();
        if (File.Exists("/proc/cpuinfo"))
            cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? cpu;
        else if (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") is { } id)
            cpu = id;
        return $"machine: {RuntimeInformation.OSDescription}; {Environment.ProcessorCount} logical CPUs; {cpu}; .NET {Environment.Version}; " +
               $"load generator and server on the same machine unless stated; {Pacing.Describe()}";
    }
}

static class Suite
{
    public static async Task RunAsync(HttpClient client, Options o)
    {
        var baseUrl = o.Base!.TrimEnd('/');
        Console.WriteLine(Machine.Describe());
        Console.WriteLine($"suite: open loop at {string.Join(", ", o.SuiteRates)}/s and a closed loop of {o.Concurrency}; n = {string.Join(", ", o.SuiteNs)}; " +
                          $"{o.Warmup.TotalSeconds:F0} s warm-up then {o.Duration.TotalSeconds:F0} s measured per scenario");
        await WaitForServerAsync(client, $"{baseUrl}/api/beststories?n=1");
        var (hnBefore, refreshesBefore) = await HnCallsAsync(client, o.HnStats);
        var started = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine(Report.Header());
        foreach (var n in o.SuiteNs)
        {
            var url = $"{baseUrl}/api/beststories?n={n}";
            foreach (var rate in o.SuiteRates)
                Console.WriteLine(Report.Row($"n={n}", await Runner.OpenLoopAsync(client, url, rate, o.Warmup, o.Duration, null)));
            Console.WriteLine(Report.Row($"n={n}", await Runner.ClosedLoopAsync(client, url, o.Concurrency, o.Warmup, o.Duration, null)));
        }

        if (o.HnStats is not null)
        {
            var (hnAfter, refreshesAfter) = await HnCallsAsync(client, o.HnStats);
            var refreshes = refreshesAfter - refreshesBefore;
            Console.WriteLine();
            Console.WriteLine($"Hacker News calls during the suite ({started.Elapsed.TotalMinutes:F1} min): {hnAfter - hnBefore} in {refreshes} refresh(es), " +
                              $"{(refreshes > 0 ? (hnAfter - hnBefore) / refreshes : 0)} per refresh, whatever the request rate above.");
        }
    }

    private static async Task WaitForServerAsync(HttpClient client, string url)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"{url} did not answer 200 within a minute");
    }

    /// <summary>Calls the fake Hacker News has had in total, and how many refreshes (one id-list call each).</summary>
    private static async Task<(long Calls, long Refreshes)> HnCallsAsync(HttpClient client, string? statsUrl)
    {
        if (statsUrl is null) return (0, 0);
        using var doc = JsonDocument.Parse(await client.GetStringAsync(statsUrl));
        return (doc.RootElement.GetProperty("total").GetInt64(), doc.RootElement.GetProperty("beststories").GetInt64());
    }
}

/// <summary>
/// How the dispatcher waits for the next request to fall due. Thread.Sleep(1) sleeps for the operating system's
/// timer tick: about 1 ms on Linux, but about 15.6 ms on Windows unless something has raised the timer
/// resolution. A 15 ms sleep cannot keep a schedule of thousands a second, and the generator then (rightly)
/// rejects its own result. So the tick is measured here, at start: where it is fine the dispatcher sleeps,
/// and where it is coarse it spins for the last stretch before each send, yielding the CPU to any other
/// thread that is ready, and sleeps only when the next send is further off than a whole tick.
/// </summary>
static class Pacing
{
    private static long _sleepTicks;
    private static bool _spin;

    public static double SleepMs => _sleepTicks * 1000.0 / Stopwatch.Frequency;

    // LOADTEST_SIMULATE_TICK_MS makes every sleep last that long, to try the pacing on a machine whose own tick is
    // fine: 16 reproduces Windows' default. It changes nothing else.
    private static readonly int SimulatedTickMs =
        int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_SIMULATE_TICK_MS"), out var ms) && ms > 0 ? ms : 0;

    private static void Sleep() => Thread.Sleep(SimulatedTickMs > 0 ? SimulatedTickMs : 1);

    public static void Choose(string mode)
    {
        var samples = new long[15];
        for (var i = 0; i < samples.Length; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            Sleep();
            samples[i] = Stopwatch.GetTimestamp() - t0;
        }
        Array.Sort(samples);
        _sleepTicks = samples[(int)(samples.Length * 0.8)];
        _spin = mode switch
        {
            "spin" => true,
            "sleep" => false,
            _ => SleepMs > 2.0,
        };
    }

    /// <summary>Wait, at most, until <paramref name="ticks"/> from now; the caller checks again.</summary>
    public static void WaitUpTo(long ticks)
    {
        if (!_spin || ticks > _sleepTicks * 3 / 2) Sleep();
        else Thread.Yield();
    }

    public static string Describe() =>
        $"Thread.Sleep(1) takes {SleepMs:F1} ms here{(SimulatedTickMs > 0 ? $" (SIMULATED tick of {SimulatedTickMs} ms)" : "")}, so the dispatcher {(_spin ? "spins (yielding) for the last stretch before each send" : "sleeps between sends")}";
}
