# BestStoriesApi

An ASP.NET Core (.NET 10) REST API that returns the best `n` stories from Hacker News, highest score first.

```
GET /api/beststories?n=10
```

```json
[
  {
    "title": "Margaret Hamilton has died",
    "uri": "https://news.mit.edu/2026/margaret-hamilton-computing-pioneer-dies-1007",
    "postedBy": "muglug",
    "time": "2026-10-07T21:16:18+00:00",
    "score": 1737,
    "commentCount": 182
  }
]
```

**In one line:** callers never cause a call to Hacker News, and a request never runs a serialiser. Hacker News is read once a minute in the background; the answer for every `n` is built then, once, as bytes. A request copies a slice of those bytes to the socket. On one core of a small cloud VM that is **42,800 requests/s for `n=10` and 23,400/s for `n=200`**, and **p99 of 3.5 ms for `n=200` at 7,500 requests/s, where it was 264 ms**. Hacker News sees 201 calls a minute whatever the load. [The numbers](#performance) and [how they were measured](bench/results/2026-10-10.md) are below.

## How to run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (any 10.0 SDK; `global.json` asks for 10.0.100 or later).

```bash
dotnet run --project src/BestStoriesApi --launch-profile http
```

The API listens on `http://localhost:5028`. In the Development environment (the launch profile sets it) Swagger UI is at `http://localhost:5028/swagger`. Try it:

```bash
curl -i "http://localhost:5028/api/beststories?n=5"
```

The first request after start-up waits a second or two while the first snapshot is built. After that, requests are answered from memory.

### Run the tests

```bash
dotnet test
```

29 tests, NUnit and Moq. Hacker News is replaced by a Moq fake, so they need no network and take a few seconds. Among them: the pre-built answer for every `n` is byte for byte what serialising per request produced, for awkward input (no link, non-ASCII, quotes, HTML characters); fifty requests at cold start cause exactly one refresh; a client that sends its ETag back gets 304.

### Configuration

`src/BestStoriesApi/appsettings.json`, section `HackerNews`:

| Setting | Default | Meaning |
|---|---|---|
| `BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | The Hacker News API. |
| `RefreshInterval` | `00:01:00` | How often the snapshot is rebuilt. |
| `MaxParallelFetches` | `10` | How many story details are fetched at once during a refresh. |

Any of these can be set from the environment, for example `HackerNews__BaseAddress=http://localhost:5099/v0/`.

## The design

### Not overloading Hacker News

The rule that does the work: **a caller's request never causes a call to Hacker News.**

- **One background refresher.** A hosted service rebuilds the snapshot every `RefreshInterval`. There is one such service, and it does one refresh at a time, so the number of calls to Hacker News is set by the clock (201 a minute at the default: one id list and 200 stories) and not by the number of callers. Measured: the same 201 a minute with no callers, at 5,000 requests/s and at 20,000 requests/s.
- **Cold start.** A request that arrives before the first refresh has finished waits for that refresh. It does not start one of its own. Fifty simultaneous requests at start-up cause one refresh (there is a test for exactly this).
- **Bounded fetching.** During a refresh, story details are fetched at most `MaxParallelFetches` at a time, so a refresh is a steady trickle and not a burst of 200 requests.
- **Resilient upstream calls.** Calls to Hacker News go through a named `HttpClient` with `Microsoft.Extensions.Http.Resilience`'s standard handler: timeouts, retries with back-off, and a circuit breaker. A slow or flaky upstream cannot hang a refresh.
- **Failure keeps the old data.** If a refresh fails, the previous snapshot keeps being served and the failure is logged. A single story that cannot be loaded is skipped (and logged). A placeholder is never stored.

### Low latency

Once the upstream is out of the request path, what is left is the cost of building each response. The design removes it.

- **Serialise once per refresh, not once per request.** When a refresh finishes, `StorySnapshot` writes the JSON for the whole ranked list once, `[s0,s1,...,s199]`, and records where each story ends. The answer for `n` is the buffer up to the end of story `n-1`, plus `]`. A request copies that slice to the response: no serialiser, no allocation of its own, and the same cost whether `n` is 1 or 200. Building the snapshot takes about 185 µs, once a minute.
- **One writer, many readers, no locks.** The refresher builds a complete new snapshot and publishes it by swapping a single reference. A request reads that reference once and uses that snapshot to the end, so it can never see a half-built list, and no reader ever waits for the writer or for another reader. The snapshot is immutable once published. This is the read-copy-update pattern: readers have no lock to queue on, however many there are.
- **A minimal endpoint, not an MVC controller.** The handler reads `n`, takes the current snapshot and writes bytes, with a `Content-Length`. No model binding, no action filters, no content negotiation.
- **ETag and 304.** Each answer carries a strong ETag computed from its content, and `Cache-Control: no-cache`. A client that sends the tag back in `If-None-Match` gets `304 Not Modified` with no body. The tag depends only on the stories in that answer, so it survives refreshes in which they did not change. For `n=200` that is twice the throughput and a hundredth of the bytes.

Every one of these was kept because it moved a number; see below.

## Performance

Measured on 10 October 2026 on **one core of a shared 2-vCPU cloud VM** (Xeon @ 2.1 GHz, .NET 10.0.12, Release). The load generator, [wrk2](https://github.com/giltene/wrk2), was on the other core and sent at a fixed rate, correcting for coordinated omission, so a stall is charged to every request that queued behind it. Each figure is the median of three 15-second runs, with the spread in brackets. The full method, the raw runs and the other measurements are in [`bench/results/2026-10-10.md`](bench/results/2026-10-10.md).

**Latency under load** (milliseconds):

| n | load | p50 before | p99 before | p50 after | p99 after |
|---:|---:|---:|---:|---:|---:|
| 10 | 2,000/s | 1.25 | 3.0 (2.7–3.4) | 1.13 | 3.0 (2.7–3.8) |
| 10 | 5,000/s | 1.40 | 3.7 (3.6–4.1) | 1.21 | 4.0 (2.9–6.7) |
| 10 | 7,500/s | 1.61 | 8.5 (6.4–10.4) | 1.31 | 4.4 (4.1–4.8) |
| 200 | 2,000/s | 8.81 | 18.9 (9.6–24.9) | 1.18 | 4.8 (2.8–9.0) |
| 200 | 5,000/s | 3.03 | 42.8 (21.6–61.6) | 1.37 | 5.8 (5.5–6.3) |
| 200 | 7,500/s | 190 | 264 (119–531) | 1.46 | 3.5 (3.5–9.0) |

**Most requests per second** (closed loop, `wrk`, 64 connections):

| n | before | after |
|---:|---:|---:|
| 10 | 20,800 | 42,800 |
| 200 | 8,750 | 23,400 |

**Bytes allocated per request** (`dotnet-counters`, at 5,000/s): 14,100 (`n=10`) and 16,400 (`n=200`) before; 3,800 for both after, against 3,200 for an empty 404 from the same server.

**How to read it.**

- **For small answers at light load nothing changed, and nothing could.** An empty 404 from the same server has a median of 1.04–1.12 ms on this machine: that floor is the VM and the loopback, and both builds sit on it.
- **The change pays as the load or the answer grows.** At 7,500 requests/s for `n=200` the old build was at 90% of what it could serve, and its p99 was a quarter of a second.
- **The tail on a shared VM is noisy**, which is why the spread is shown. A dedicated machine will give lower and steadier figures; the commands below reproduce them.

**Where each gain came from.**

- **Replacing the controller with a minimal endpoint, still serialising per request:** maximum throughput for `n=10` went from 20,800 to 36,400/s, and p99 at 7,500/s halved. The raw runs are in `bench/results/2026-10-10-step-a-minimal-endpoint.csv`.
- **Serialising once per refresh took the rest:** `n=200` went from 10,300 to 23,400/s, and its p99 at 7,500/s from 88 ms to 3.5 ms.
- **In isolation** ([BenchmarkDotNet](benchmarks/BestStoriesApi.Benchmarks)), building one `n=200` response fell from 48 µs to 0.9 µs, and one `n=10` response from 2.1 µs to 30 ns.

### Reproduce the figures

You need three terminals. One runs a fake Hacker News, which serves the same three endpoints with 40 ms of latency, moves scores over time and counts every call. One runs the API pointed at it. The third runs the load.

```bash
dotnet run -c Release --project tools/FakeHackerNews
```

```bash
dotnet run -c Release --project src/BestStoriesApi --no-launch-profile --urls http://localhost:5028 --HackerNews:BaseAddress=http://localhost:5099/v0/
```

```bash
# Any OS: the load generator in this repository, no installs. Open loop, timed from each request's due time.
dotnet run -c Release --project tools/LoadTest -- --suite --base http://localhost:5028 --hn-stats http://localhost:5099/stats

# Linux or WSL, the instrument the table above used: wrk2 and wrk.
WRK2=~/wrk2/wrk bash bench/wrk2-suite.sh

# The request path alone.
dotnet run -c Release --project benchmarks/BestStoriesApi.Benchmarks -- --filter '*'
```

**`tools/LoadTest` checks itself.** It refuses a result if it fell more than 10 ms behind its own sending schedule, because then it was measuring itself. On the VM above it agreed with wrk2 at the median and at p99 where it kept up. Its closed-loop maximum is lower than `wrk`'s, because a .NET client on one core costs more than a C one. So the maximum-throughput figures above come from `wrk`.

## Assumptions

1. **"Best n stories" means the best by score across the whole list.** Hacker News's best-stories list is its own ranking. The test asks for the stories "sorted by their score in a descending order", and the version of the brief dated 20 July 2023 says "the best n stories ... as determined by their score". So the API ranks the whole list by score, highest first, and then takes `n`. It does not take the first `n` ids and sort those. Equal scores keep the order Hacker News gave them.
2. **`n` is required and must be a whole number of at least 1.** Anything else (missing, `0`, negative, not a number) returns `400 Bad Request`, as `application/problem+json`.
3. **If `n` is larger than the number of stories available, you get all of them** (about 200), with status 200. It is not an error.
4. **`uri` is `null` when a story has no link.** "Ask HN" posts have no `url` in Hacker News. The API says so, with `"uri": null`, and does not invent a link.
5. **`time` is ISO 8601 with a UTC offset**, for example `2019-10-12T13:43:01+00:00`. Hacker News gives Unix seconds, which are converted.
6. **`commentCount` is Hacker News's `descendants`** (all comments, including replies). It is `0` if Hacker News gives none.
7. **Deleted and dead items are left out**, as is any story whose title or author is missing.
8. **Data can be up to one refresh interval old** (a minute by default). A story's score on this API can lag Hacker News by that much. A client that wants to know whether anything changed can send its ETag back.
9. **If the very first refresh fails**, there is nothing to serve, so requests get `503 Service Unavailable` with `Retry-After: 5` until a later refresh succeeds.
10. **One instance.** The snapshot lives in the memory of the running process.

## What I would do next, given time

- **Several instances.** Each instance currently builds its own snapshot, which multiplies the upstream calls by the number of instances. A shared cache (Redis, say) with one elected refresher would keep it to one set of calls. With several instances, the ETag already lets a CDN or reverse proxy in front absorb most of the traffic.
- **An incremental refresh,** fetching only the stories Hacker News lists as changed in `/v0/updates.json`, plus any new ids, would cut the 200 story calls a minute to however many changed. **I left it out on purpose.** It is only correct if `updates.json` covers at least one refresh interval, and that cannot be measured without the live API: if the window is shorter, scores go stale with nothing to show it. The safe form polls `updates.json` every few seconds, gathers what changed between refreshes, and does a full refresh every ten minutes as a backstop. It is worth building once its saving has been measured against the real API.
- **Pre-compressed answers.** gzip or Brotli for each `n`, built with the snapshot, so compression costs nothing per request. On the fake's data gzip makes the `n=200` answer 8.6 times smaller (30,240 bytes to 3,501); real titles vary more and will compress less. It does not help over loopback, so its latency effect is not measured here.
- **Rate limiting on our own endpoint** (ASP.NET Core's built-in rate limiter), so one client cannot hog the API.
- **Health checks:** a readiness check that is healthy only when a snapshot exists, and one that reports the snapshot's age.
- **Metrics and tracing** (OpenTelemetry): snapshot age, refresh duration, number of stories skipped, upstream failures, request latency histograms.
- **Tests that exercise the real HTTP client** against the fake Hacker News, to cover the retry and timeout settings. Today those settings are Microsoft's defaults and are not tested here.
- **Containerise it** and add a CI build that runs `dotnet test` and a short load test that fails on a p99 regression.

## Layout

```
src/BestStoriesApi/
  Api/BestStoriesEndpoint.cs        the endpoint: validate n, take the current snapshot, write its bytes, or 304
  Stories/StorySnapshot.cs          the ranked stories serialised once, with each story's end offset and each answer's ETag
  Stories/StoryStore.cs             holds the current snapshot; one writer swaps it, readers never wait
  Stories/StoryRefreshService.cs    background loop: refresh now, then every interval
  Stories/StoryRefresher.cs         fetches, skips failures, ranks the whole list
  Stories/StoryJsonContext.cs       the JSON shape, generated at compile time
  HackerNews/                       the upstream client, its options, and its JSON shape
tests/BestStoriesApi.Tests/         NUnit + Moq
tools/FakeHackerNews/               a stand-in Hacker News for load tests: latency, moving scores, call counts
tools/LoadTest/                     open-loop load generator, coordinated-omission safe, no packages
benchmarks/BestStoriesApi.Benchmarks/   BenchmarkDotNet: the request path before and after
bench/                              the wrk2 script and the measurements of 10 October 2026
```

## AI assistance

This solution was written with the help of an AI assistant (Claude, by Anthropic) and reviewed by me, Clifford Keeble.
