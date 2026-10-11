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

**In one line:** callers never cause a call to Hacker News, and a request for stories never runs a serialiser. Hacker News is read once a minute in the background. The answer for every `n` is built then, once, as bytes, and a request copies a slice of those bytes to the socket.

On one core of a small cloud VM, measured against the first version with the two builds interleaved run by run:
- **Most requests per second:** `n=10` went from 24,600 to 44,100, and `n=200` from 10,600 to 24,200.
- **p99 for `n=200` at 7,500 requests/s:** 32 ms before, 3.3 ms after.
- **Hacker News:** 201 calls per refresh, once a minute, however many callers there are.

[The numbers](#performance) and [how they were measured](bench/results/2026-10-10.md) are below.

## How to run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (any 10.0 SDK; `global.json` asks for 10.0.100 or later).

```bash
dotnet run --project src/BestStoriesApi --launch-profile http
```

The API listens on `http://localhost:5028`. In the Development environment (the launch profile sets it) Swagger UI is at `http://localhost:5028/swagger`. Try it:

```bash
curl -i "http://localhost:5028/api/beststories?n=5"
```

The first request after start-up waits for the first snapshot to be built: about 200 calls to Hacker News, 10 at a time. On a laptop against the real Hacker News, the first answer came 3.6 to 3.9 s after the process started. After that, requests are answered from memory.

### Run the tests

```bash
dotnet test
```

46 tests, NUnit and Moq. Hacker News is replaced by a Moq fake, so they need no network and take a few seconds. Among them:
- the pre-built answer for every `n` is byte for byte what serialising per request produced, for awkward input: no link, non-ASCII, quotes, HTML characters;
- fifty requests at cold start cause exactly one refresh;
- a refresh in which most stories fail does not replace a good snapshot;
- a client that sends its ETag back gets 304, and HEAD gets the GET headers with no body;
- stopping the app releases anyone still waiting for the first snapshot;
- a setting that cannot work stops the app at start, naming the setting.

Each was seen to fail with the code it guards taken out, except the cold-start test, which came with the first version.

The build is warning-free under the .NET 10 recommended code-analysis rules, which `Directory.Build.props` switches on: `dotnet build -c Release -warnaserror` checks it. The few rules switched off are in the project files, each with its reason.

### Configuration

`src/BestStoriesApi/appsettings.json`, section `HackerNews`. Every value is checked at start-up, and a value that cannot work stops the app with a message that names it.

| Setting | Default | Meaning |
|---|---|---|
| `BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | The Hacker News API. Must end with `/`. |
| `RefreshInterval` | `00:01:00` | How often the snapshot is rebuilt. |
| `MaxParallelFetches` | `10` | How many story details are fetched at once during a refresh. |
| `MaxFailedFetchFraction` | `0.1` | The share of story fetches that may fail before a refresh counts as incomplete (see below). |
| `FirstSnapshotWait` | `00:00:30` | The longest a request waits for the first snapshot after start-up before it gets 503. |

Any of these can be set from the environment, for example `HackerNews__BaseAddress=http://localhost:5099/v0/`. In Visual Studio, the launch profile `against-fake-hn` runs the API against the fake Hacker News in `tools/`.

## The design

### Not overloading Hacker News

The rule that does the work: **a caller's request never causes a call to Hacker News.**

- **One background refresher.** A hosted service rebuilds the snapshot every `RefreshInterval`. There is one such service and it does one refresh at a time, so calls to Hacker News are set by the clock, not by the callers.
  - Each refresh makes one id-list call and about 200 story calls.
  - Measured: 402 calls in two minutes with no callers, at 5,000 requests/s and at 20,000 requests/s.
  - That is a floor, not a fixed figure: the standard resilience handler retries a failed call up to three times. A bad minute upstream can therefore cost up to four times as many calls. Callers cannot raise it; Hacker News's own errors can.
- **Cold start.** A request that arrives before the first refresh has finished waits for that refresh, and does not start one of its own. Fifty simultaneous requests at start-up cause one refresh (there is a test for exactly this). The wait is bounded: after `FirstSnapshotWait` the caller gets 503 with `Retry-After`.
- **Bounded fetching.** During a refresh, story details are fetched at most `MaxParallelFetches` at a time, so a refresh is a steady trickle and not a burst of 200 requests.
- **Resilient upstream calls.** Calls to Hacker News go through a named `HttpClient` with `Microsoft.Extensions.Http.Resilience`'s standard handler.
  - It sets a 10 s timeout per attempt, 3 retries with exponential back-off, a 30 s limit per call, and a circuit breaker.
  - Each call is bounded. A refresh as a whole is not: with a slow upstream, 20 rounds of 10 calls can take minutes. The previous snapshot is served throughout.
- **A failed or incomplete refresh keeps the old data.** Failures are not independent: when Hacker News wobbles, many fetches fail together, and the circuit breaker can open in the middle of a refresh. A ranking of whatever survived could then leave out the real top stories.
  - **Complete:** a refresh in which more than `MaxFailedFetchFraction` (10%) of story fetches failed counts as incomplete, with one failure always allowed. A story that is deleted or dead is not a failure.
  - **Old data kept:** an incomplete or failed refresh never replaces a good snapshot. It is logged, and the previous snapshot keeps being served.
  - **Before the first snapshot:** an incomplete list is served, because it beats nothing, and the next refresh replaces it.
  - A placeholder is never stored.

### Low latency

Once the upstream is out of the request path, what is left is the cost of building each response. The design removes most of it.

- **Serialise once per refresh, not once per request.**
  - When a refresh finishes, `StorySnapshot` writes the JSON for the whole ranked list once, `[s0,s1,...,s199]`, and records where each story ends.
  - The answer for `n` is the buffer up to the end of story `n-1`, plus `]`. A request copies that slice to the response.
  - No serialiser runs, and the copy allocates nothing. What a larger answer still costs is the copy itself: in isolation 37 ns for `n=10` and 0.95 µs for `n=200`, where serialising took 3.2 µs and 64 µs.
  - Building the snapshot takes a few hundred microseconds, once a minute.
- **One writer, many readers, no locks.** The refresher builds a complete new snapshot and publishes it by swapping a single reference.
  - A request reads that reference once and uses that snapshot to the end, so it can never see a half-built list.
  - No reader ever waits for the writer or for another reader. The snapshot is immutable once published.
  - This is the read-copy-update pattern: readers have no lock to queue on, however many there are.
- **A minimal endpoint, not an MVC controller.** The handler reads `n`, takes the current snapshot and writes bytes, with a `Content-Length`. No model binding, no action filters, no content negotiation.
  - Its own allocation is about 700 bytes a request, the same for any `n`: query parsing, headers and the flush. An empty 404 from the same server costs about 3,200 bytes; the endpoint's total is about 3,900.
- **ETag and 304.** Each answer carries a strong ETag computed from its content, and `Cache-Control: no-cache`. A client that sends the tag back in `If-None-Match` gets `304 Not Modified` with no body. HEAD gets the same headers as GET.
  - The tag depends only on the stories in that answer, so it survives refreshes in which they did not change.
  - The snapshot changes at most once a minute. A client polling faster than that gets 304 for every request but the first after a change. One polling slower rarely does, because live scores move every minute.
  - Measured with scores held still, `n=200`: about 1.65 times the responses, at about a hundredth of the bytes.

Each change was measured as a step against the one before; see below. Two parts were not measured on their own: the lock-free read path (it came with the first version), and the resilience handler.

## Performance

**Where and how it was measured.**
- **The machine:** 10 October 2026, **one core of a shared 2-vCPU cloud VM** (Xeon, .NET 10.0.12, Release).
- **The load generator:** [wrk2](https://github.com/giltene/wrk2), on the other core. It sent at a fixed rate, correcting for coordinated omission, so a stall is charged to every request that queued behind it.
- **The comparison:** the first version and this one were running side by side and measured **alternately, run by run**, so a noisy minute on the shared VM lands on both.
- **The runs:** each figure is the median of three 15-second runs, with the range of all three in brackets. A run that fell more than 2% short of its target rate was left out of the median and counted; there were two, both of this version. Throughput runs were 10 seconds, with `wrk`.
- **Everything else:** the full method, every raw output and the other measurements are in [`bench/results/2026-10-10.md`](bench/results/2026-10-10.md).

**Latency under load** (milliseconds):

| n | load | p50 before | p99 before | p50 after | p99 after |
|---:|---:|---:|---:|---:|---:|
| 10 | 2,000/s | 1.28 | 3.4 (3.2–4.4) | 1.10 | 2.7 (2.5–68.8) |
| 10 | 5,000/s | 1.40 | 4.3 (3.7–25.1) | 1.20 | 2.7 (2.7–3.1) |
| 10 | 7,500/s | 1.48 | 4.0 (3.2–4.0) | 1.23 | 3.0 (2.7–4.8) |
| 200 | 2,000/s | 1.40 | 3.0 (3.0–3.2) | 1.12 | 2.4 (2.4–6.6) |
| 200 | 5,000/s | 1.73 | 23.1 (5.3–42.2) | 1.27 | 2.7 (2.6–5.4) |
| 200 | 7,500/s | 2.14 | 32.0 (24.4–76.5) | 1.38 | 3.3 (2.9–7.0) |
| empty 404 | 2,000 to 7,500/s | | | 1.05 to 1.19 | 2.6 to 2.9 |

**Most requests per second** (closed loop, `wrk`, 64 connections):

| n | before | after |
|---:|---:|---:|
| 10 | 24,600 (22,500–25,900) | 44,100 (44,100–44,200) |
| 200 | 10,600 (10,400–11,000) | 24,200 (23,700–24,500) |

**Bytes allocated per request** (`dotnet-counters`, at 5,000/s): 13,800 (`n=10`) and 14,300 (`n=200`) before; 3,800 and 3,900 after, against 3,200 for an empty 404 from the same server.

**On a laptop** (Ryzen 5 PRO 7535U, 6 cores and 12 threads, Windows 11), measured by a second operator with this repository's load generator on the same machine as the server, on 11 October, on mains. The tables are in [`bench/results/2026-10-11-laptop.md`](bench/results/2026-10-11-laptop.md).
- **Most requests per second** (closed loop, 64 connections): `n=10` 56,400 before, 84,400 and 89,600 after; `n=200` 22,500 before, 51,600 and 38,500 after. The two after runs are 25% apart, so at `n=200` the gain is 1.7 to 2.3 times, not one figure. On 10 October, on battery: 21,100 before, 43,200–43,400 after.
- **Latency at a fixed rate:** the generator now spins on Windows' coarse timer, as designed, and 10 of the 18 rows kept their schedule. The other 8 did not, at no consistent rate, with Visual Studio and OneDrive running on the Balanced power plan; they are left out. The one rate valid in all three runs, `n=10` at 2,000/s, gave a p99 of 1.38 ms before and 0.46 and 0.41 ms after. Medians on the valid rows were 0.10 to 0.23 ms, in line with the spinning client on the VM (below).
- **The in-process benchmark** agrees with the VM: 2.6 µs → 35 ns for `n=10`, and 44.7 µs → 0.68 µs for `n=200`.

**How to read it.**
- **Most of the ~1.1 ms median is the load generator, not the server.** An empty 404 from the same server measures 1.05–1.19 ms with wrk2.
  - The same .NET client, changing only how it waits between sends, measured a median of 0.16 ms for both the endpoint and the 404 when it spun, and about 1 ms when it slept for a millisecond.
  - So the server answers in well under a millisecond at the median, and the tables above are an upper bound.
- **At light load the gain is small but real:** for `n=10` at 2,000/s, the median falls by 0.18 ms (1.28 to 1.10).
- **It grows with the size of the answer and the load.** At 7,500 requests/s the old build was at 71% of its own `n=200` maximum, and its p99 was 32 ms against 3.3 ms after.
- **The `n=10` maximum may be the generator's limit, not the endpoint's.** wrk on one core reached about the same rate for empty 304s.
- **The tail on a shared VM is noisy.** A single 70 ms run lifts a range, and the API's once-a-minute refresh falls inside about one run in four. The median of three is the figure to read, and a dedicated machine will give lower, steadier figures.
- **A first, non-interleaved run earlier the same day** showed the old build's `n=200` p99 at 7,500/s as 264 ms. It is kept in `bench/results/2026-10-10-first-run/` and superseded: its old build had less headroom on that run, and two of its rows were built from runs that fell short of their rate.

**Where each gain came from.**
- **The minimal endpoint, with source-generated JSON, still serialising per request** (first run, not interleaved): `n=10` maximum throughput went from about 20,800 to 36,400/s. The two changes were made together, so the gain is theirs jointly.
- **Serialising once per refresh** took the rest, chiefly for large answers: `n=200` went from about 10,300 to 23,400/s in the same run.

### Reproduce the figures

You need three terminals:
- **one runs a fake Hacker News**, which serves the same three endpoints with 40 ms of latency, moves scores over time and counts every call;
- **one runs the API**, pointed at the fake;
- **the third runs the load.**

```bash
dotnet run -c Release --project tools/FakeHackerNews
```

```bash
dotnet run -c Release --project src/BestStoriesApi --no-launch-profile -- --urls http://localhost:5028 --HackerNews:BaseAddress=http://localhost:5099/v0/
```

```bash
# Any OS: the load generator in this repository, no installs.
dotnet run -c Release --project tools/LoadTest -- --suite --base http://localhost:5028 --hn-stats http://localhost:5099/stats

# Linux or WSL, the instrument the tables used. Start the first version on port 5029 for a before/after.
AFTER=http://localhost:5028 BEFORE=http://localhost:5029 WRK2=~/wrk2/wrk OUT=my-raw bash bench/wrk2-suite.sh > my-results.csv
python3 bench/summarise.py my-results.csv

# The request path alone.
dotnet run -c Release --project benchmarks/BestStoriesApi.Benchmarks -- --filter '*'
```

**`tools/LoadTest` checks itself.**
- It records when each request actually left. If 1% of requests left more than 1 ms after they were due, it marks the row not valid, because the result would measure the client as much as the server.
- It measures `Thread.Sleep(1)` at start. Where the timer is coarse, as on Windows (about 15.6 ms), it spins for the last stretch before each send instead of sleeping.
- Give it cores of its own. A .NET client sharing a core with the server cannot send thousands a second on time, and it will say so.
- Its closed-loop maximum is lower than `wrk`'s, because a .NET client costs more per request than a C one, so the maximum-throughput figures come from `wrk`.

## Assumptions

1. **"Best n stories" means the best by score across the whole list.** Hacker News's best-stories list is its own ranking. The test asks for the stories "sorted by their score in a descending order", and the version of the brief dated 20 July 2023 says "the best n stories ... as determined by their score".
   - So the API ranks the whole list by score, highest first, and then takes `n`. It does not take the first `n` ids and sort those.
   - Equal scores keep the order Hacker News gave them.
2. **`n` is required and must be a whole number of at least 1.** Anything else (missing, `0`, negative, not a whole number) returns `400 Bad Request`, as `application/problem+json`.
3. **If `n` is larger than the number of stories available, you get all of them** (about 200), with status 200. That includes a number too big for a 32-bit integer.
4. **`uri` is `null` when a story has no link.** "Ask HN" posts have no `url` in Hacker News. The API says so, with `"uri": null`, and does not invent a link.
5. **`time` is ISO 8601 with a UTC offset**, for example `2019-10-12T13:43:01+00:00`. Hacker News gives Unix seconds, which are converted.
6. **`commentCount` is Hacker News's `descendants`** (all comments, including replies). It is `0` if Hacker News gives none.
7. **Deleted and dead items are left out**, as is any story whose title or author is missing.
8. **Data is normally at most one refresh old** (a minute, plus however long the refresh took).
   - While refreshes keep failing, the last good snapshot is served for as long as they fail, with an unchanged ETag. Nothing in the response says how old it is; a health check that reports the snapshot's age is the first item under next steps.
9. **If the very first refresh fails**, or has not finished within `FirstSnapshotWait`, requests get `503 Service Unavailable` with `Retry-After: 5` until a refresh succeeds.
10. **One instance.** The snapshot lives in the memory of the running process.

## What I would do next, given time

- **Say how old the data is:** a readiness check that is healthy only when a snapshot exists, a health endpoint that reports the snapshot's age, and a `Last-Modified` header on every answer.
- **Several instances.** Each instance currently builds its own snapshot, which multiplies the upstream calls by the number of instances. A shared cache (Redis, say) with one elected refresher would keep it to one set of calls. With several instances, the ETag already lets a CDN or reverse proxy in front absorb most of the traffic.
- **An incremental refresh,** fetching only the stories Hacker News lists as changed in `/v0/updates.json`, plus any new ids, would cut the 200 story calls a minute to however many changed. **I left it out on purpose.**
  - It is only correct if `updates.json` covers at least one refresh interval, and that cannot be measured without the live API. If the window is shorter, scores go stale with nothing to show it.
  - The safe form polls `updates.json` every few seconds, gathers what changed between refreshes, and does a full refresh every ten minutes as a backstop.
  - It is worth building once its saving has been measured against the real API.
- **Pre-compressed answers:** gzip or Brotli for each `n`, built with the snapshot, so compression costs nothing per request.
  - On the fake's data gzip makes the `n=200` answer 8.6 times smaller (30,240 bytes to 3,501); real titles vary more and will compress less.
  - Each encoding needs a tag of its own, since a strong ETag must not cover two different bodies.
  - It does not help over loopback, so its latency effect is not measured here.
- **Rate limiting on our own endpoint** (ASP.NET Core's built-in rate limiter), so one client cannot hog the API.
- **Metrics and tracing** (OpenTelemetry): snapshot age, refresh duration, stories skipped and failed, upstream failures, request latency histograms.
- **Tests that exercise the real HTTP client** against the fake Hacker News, to cover the retry, timeout and circuit-breaker settings. Today those are Microsoft's defaults, and are not tested here.
- **Containerise it** and add a CI build that runs `dotnet test` and a short load test that fails on a p99 regression.

## Layout

```
src/BestStoriesApi/
  Api/BestStoriesEndpoint.cs        the endpoint: validate n, take the current snapshot, write its bytes, 304, or HEAD
  Api/BestStoriesOpenApi.cs         describes n to Swagger (Development only)
  Stories/StorySnapshot.cs          the ranked stories serialised once, with each story's end offset and each answer's ETag
  Stories/StoryStore.cs             holds the current snapshot; one writer swaps it, readers never wait
  Stories/StoryRefreshService.cs    background loop: refresh now, then every interval; keeps the old snapshot on a bad refresh
  Stories/StoryRefresher.cs         fetches, counts failures, ranks the whole list
  Stories/IncompleteRefreshException.cs   a refresh in which too many stories failed, carrying what did load
  Stories/StoryJsonContext.cs       the JSON shape, generated at compile time
  Stories/StoriesLog.cs             the refresh's log messages, source-generated, each with an event id
  HackerNews/                       the upstream client, its options (checked at start-up), and its JSON shape
tests/BestStoriesApi.Tests/         NUnit + Moq
tools/FakeHackerNews/               a stand-in Hacker News for load tests: latency, moving scores, call counts
tools/LoadTest/                     open-loop load generator, coordinated-omission safe, checks its own schedule, no packages
benchmarks/BestStoriesApi.Benchmarks/   BenchmarkDotNet: the request path before and after
bench/                              the wrk2 script, the summariser, and the measurements of 10 October 2026 with raw output
```

## AI assistance

This solution was written with the help of an AI assistant (Claude, by Anthropic) and reviewed by me, Clifford Keeble.
