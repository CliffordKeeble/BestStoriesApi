// The cost of building one response, in process, without the network or the server around it.
//
//   dotnet run -c Release --project benchmarks/BestStoriesApi.Benchmarks
//
// Before: what the controller of 4ee5907 did on every request, serialise the first n stories.
// After:  what the endpoint does now, copy the first part of a buffer built once per refresh.
// Both write into the same reusable buffer, so the comparison is the work, not the destination.

using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BestStoriesApi.Stories;

BenchmarkSwitcher.FromAssembly(typeof(RequestPath).Assembly).Run(args);

[MemoryDiagnoser]
public class RequestPath
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);
    private readonly ArrayBufferWriter<byte> _output = new(64 * 1024);
    private Story[] _stories = [];
    private StorySnapshot _snapshot = null!;

    [Params(10, 200)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20261010);
        var now = DateTimeOffset.UtcNow;
        _stories = Enumerable.Range(0, 200)
            .Select(i => new Story($"Story number {i} with a title of ordinary length", $"https://example.com/story/{i}",
                $"user{i % 37}", now.AddMinutes(-10 * i), random.Next(50, 2500), random.Next(0, 900)))
            .OrderByDescending(s => s.Score)
            .ToArray();
        _snapshot = StorySnapshot.Create(_stories);
    }

    [Benchmark(Baseline = true, Description = "serialise per request (before)")]
    public int SerialisePerRequest()
    {
        _output.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_output);
        JsonSerializer.Serialize(writer, _stories.Take(N), WebDefaults);
        return _output.WrittenCount;
    }

    [Benchmark(Description = "copy a pre-built slice (after)")]
    public int CopyPrebuiltSlice()
    {
        _output.ResetWrittenCount();
        _output.Write(_snapshot.Head(N).Span);
        _output.Write(StorySnapshot.Tail.Span);
        return _output.WrittenCount;
    }
}

[MemoryDiagnoser]
public class SnapshotBuild
{
    private Story[] _stories = [];

    [GlobalSetup]
    public void Setup()
    {
        var now = DateTimeOffset.UtcNow;
        _stories = Enumerable.Range(0, 200)
            .Select(i => new Story($"Story number {i} with a title of ordinary length", $"https://example.com/story/{i}",
                $"user{i % 37}", now.AddMinutes(-10 * i), 2500 - i, i))
            .ToArray();
    }

    [Benchmark(Description = "build a 200-story snapshot (once per refresh)")]
    public StorySnapshot Build() => StorySnapshot.Create(_stories);
}
