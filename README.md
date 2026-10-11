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

The numbers, how they were measured and how to reproduce them are in [`docs/performance.md`](docs/performance.md).

## How to run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (any 10.0 SDK; `global.json` asks for 10.0.100 or later).

```bash
dotnet run --project src/BestStoriesApi --launch-profile http
```

The API listens on `http://localhost:5028`. In the Development environment (the launch profile sets it) Swagger UI is at `http://localhost:5028/swagger`. Try it:

```bash
curl -i "http://localhost:5028/api/beststories?n=5"
```

It needs internet access to `hacker-news.firebaseio.com`. The first request after start-up waits for the first snapshot to be built: about 200 calls to Hacker News, 10 at a time. On a laptop against the real Hacker News, the first answer came 3.6 to 3.9 s after the process started. After that, requests are answered from memory.

The console shows the settings in force at start, then for each refresh what was fetched and how long it took, and what is now being served. Against the fake Hacker News in `tools/`:

```
info: BestStoriesApi.Stories.StoryRefresher[8]
      Fetched 200 of 200 best stories in 899 ms: 0 deleted or dead, 0 failed
info: BestStoriesApi.Stories.StoryRefreshService[1]
      Refreshed best stories: 200 stories
```

### Run the tests

```bash
dotnet test
```

57 tests, NUnit and Moq. None needs a network, and together they take a few seconds. Most replace the Hacker News client with a Moq fake; one keeps the real client and its whole HTTP pipeline, and replaces only the network beneath it. Among them:
- the real client asks for exactly the two addresses the brief names, and turns the brief's example story into the brief's example answer;
- the pre-built answer for every `n` is byte for byte what serialising per request produced, for awkward input: no link, non-ASCII, quotes, HTML characters;
- fifty requests at cold start cause exactly one refresh;
- a refresh in which most stories fail does not replace a good snapshot, and a story that times out or comes back empty counts as failed;
- a failed first refresh is tried again within seconds, then less and less often while Hacker News keeps failing;
- a client that sends its ETag back gets 304, and HEAD gets the GET headers with no body;
- stopping the app releases anyone still waiting for the first snapshot, even when the refresh loop never started;
- a setting that cannot work stops the app at start, naming the setting.

Each was seen to fail with the code it guards taken out, except the cold-start test, which came with the first version.

The build is warning-free under the .NET 10 recommended code-analysis rules, which `Directory.Build.props` switches on: `dotnet build -c Release -warnaserror` checks it. The few rules switched off are in the project files, each with its reason.

### Configuration

Settings are read from the `HackerNews` section of the configuration. Their defaults are in `src/BestStoriesApi/HackerNews/HackerNewsOptions.cs`, and `appsettings.json` repeats the first three so they are easy to find and change. Every value is checked at start-up, and a value that cannot work stops the app with a message that names it.

| Setting | Default | Meaning |
|---|---|---|
| `BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | The Hacker News API. Must end with `/`. |
| `RefreshInterval` | `00:01:00` | How often the snapshot is rebuilt. |
| `MaxParallelFetches` | `10` | How many story details are fetched at once during a refresh. |
| `MaxFailedFetchFraction` | `0.1` | The share of story fetches that may fail before a refresh counts as incomplete (see below). |
| `FirstSnapshotWait` | `00:00:30` | The longest a request waits for the first snapshot after start-up before it gets 503. |
| `FirstSnapshotRetryInterval` | `00:00:05` | How soon a failed refresh is tried again while there is no snapshot yet; each later try waits twice as long, up to `RefreshInterval`. A 503 before the first snapshot carries it as `Retry-After`. |

Any of these can be set from the environment, for example `HackerNews__BaseAddress=http://localhost:5099/v0/`. In Visual Studio, the launch profile `against-fake-hn` runs the API against the fake Hacker News in `tools/`.

### Logging

- **Information,** the default: the settings at start, then the two lines per refresh shown above. A story skipped, a refresh that is incomplete or fails, and a retry or timeout on a call to Hacker News are Warnings or Errors. Each of the app's own messages has an event id.
- **Debug, for the app only:** `--Logging:LogLevel:BestStoriesApi=Debug` adds when each refresh starts, each story left out as deleted or dead, and each 503 sent before the first snapshot. The `http` launch profile runs in Development, which has it on.
- **The framework's lines for every call are off.** `HttpClient` writes four Information lines for every call to Hacker News and the resilience handler (Polly) one more: about 1,000 lines a refresh, which buried the app's two. `appsettings.json` sets both to Warning, so retries, timeouts and the circuit breaker opening still show. `--Logging:LogLevel:Polly=Information` brings the line per call back.
- **The messages are source-generated** (`[LoggerMessage]`). A message whose level is off is never formatted, so the Debug messages can stay in a Release build at almost no cost.

## The design

### Not overloading Hacker News

The rule that does the work: **a caller's request never causes a call to Hacker News.**

- **One background refresher.** A hosted service rebuilds the snapshot every `RefreshInterval`. There is one such service and it does one refresh at a time, so calls to Hacker News are set by the clock, not by the callers.
  - Each refresh makes one id-list call and about 200 story calls.
  - Measured: 402 calls in two minutes with no callers, at 5,000 requests/s and at 20,000 requests/s.
  - That is a floor, not a fixed figure: the standard resilience handler retries a failed call up to three times. A bad minute upstream can therefore cost up to four times as many calls. Callers cannot raise it; Hacker News's own errors can.
- **Cold start.** A request that arrives before the first refresh has finished waits for that refresh, and does not start one of its own. Fifty simultaneous requests at start-up cause one refresh (there is a test for exactly this). The wait is bounded: after `FirstSnapshotWait` the caller gets 503 with `Retry-After`.
  - If the first refresh fails, it is tried again after `FirstSnapshotRetryInterval` (5 s), not a minute later, so the API answers soon after Hacker News comes back. Each further failure doubles the wait, up to `RefreshInterval`, so a Hacker News that keeps failing is soon asked no more often than usual.
- **Bounded fetching.** During a refresh, story details are fetched at most `MaxParallelFetches` at a time, so a refresh is a steady trickle and not a burst of 200 requests.
- **Resilient upstream calls.** Calls to Hacker News go through a named `HttpClient` with `Microsoft.Extensions.Http.Resilience`'s standard handler.
  - It sets a 10 s timeout per attempt, 3 retries with exponential back-off, a 30 s limit per call, and a circuit breaker.
  - Each call is bounded. A refresh as a whole is not: with a slow upstream, 20 rounds of 10 calls can take minutes. The previous snapshot is served throughout.
- **A failed or incomplete refresh keeps the old data.** Failures are not independent: when Hacker News wobbles, many fetches fail together, and the circuit breaker can open in the middle of a refresh. A ranking of whatever survived could then leave out the real top stories.
  - **Complete:** a refresh in which more than `MaxFailedFetchFraction` (10%) of story fetches failed counts as incomplete, with one failure always allowed. A story that times out, or comes back with no body, title or author, is a failure. A story that is deleted or dead is not.
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

Each change was measured as a step against the one before; see [`docs/performance.md`](docs/performance.md). Two parts were not measured on their own: the lock-free read path (it came with the first version), and the resilience handler.

## Performance

The figures at the top of this page are from [`docs/performance.md`](docs/performance.md), which has them in full: latency under load and maximum throughput against the first version on a cloud VM, a rerun on a laptop, how to read them, where each gain came from, and how to reproduce them with the kit in this repository.

## Assumptions

1. **"Best n stories" means the best by score across the whole list.** Hacker News's best-stories list is its own ranking. The brief asks for "the best n stories from the Hacker News API, as determined by their score", returned "in descending order of score".
   - So the API ranks the whole list by score, highest first, and then takes `n`. It does not take the first `n` ids and sort those.
   - Equal scores keep the order Hacker News gave them.
2. **`n` is required and must be a whole number of at least 1.** Anything else (missing, `0`, negative, not a whole number) returns `400 Bad Request`, as `application/problem+json`.
3. **If `n` is larger than the number of stories available, you get all of them** (about 200), with status 200. That includes a number too big for a 32-bit integer.
4. **`uri` is `null` when a story has no link.** "Ask HN" posts have no `url` in Hacker News. The API says so, with `"uri": null`, and does not invent a link.
5. **`time` is ISO 8601 with a UTC offset**, for example `2019-10-12T13:43:01+00:00`. Hacker News gives Unix seconds, which are converted.
6. **`commentCount` is Hacker News's `descendants`** (all comments, including replies). It is `0` if Hacker News gives none.
7. **Deleted and dead items are left out**, as is any story whose title or author is missing. A missing title or author counts as a failed fetch; a deleted or dead story does not.
8. **Data is normally at most one refresh old** (a minute, plus however long the refresh took).
   - While refreshes keep failing, the last good snapshot is served for as long as they fail, with an unchanged ETag. Nothing in the response says how old it is; a health check that reports the snapshot's age is the first item under next steps.
9. **If the very first refresh fails**, or has not finished within `FirstSnapshotWait`, requests get `503 Service Unavailable` until a refresh succeeds. `Retry-After` is `FirstSnapshotRetryInterval` (5 s), the soonest a failed first refresh is tried again.
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
- **Tests of the retry, timeout and circuit-breaker settings.** One test runs the real client and its whole pipeline with only the network replaced, but on the happy path. The settings themselves are Microsoft's defaults, and are not tested here.
- **A fake clock** (`TimeProvider`) for the refresh loop, so the few tests that wait on short real intervals would run on simulated time instead.
- **Containerise it** and add a CI build that runs `dotnet test` and a short load test that fails on a p99 regression.

## Layout

```
src/BestStoriesApi/
  Api/BestStoriesEndpoint.cs        the endpoint: validate n, take the current snapshot, write its bytes, 304, or HEAD
  Api/BestStoriesOpenApi.cs         describes n to Swagger (Development only)
  Api/ApiLog.cs                     the endpoint's one log message: a 503 sent before the first snapshot (Debug)
  Stories/StorySnapshot.cs          the ranked stories serialised once, with each story's end offset and each answer's ETag
  Stories/StoryStore.cs             holds the current snapshot; one writer swaps it, readers never wait
  Stories/StoryRefreshService.cs    background loop: refresh now, retry soon until there is a snapshot, then every interval; keeps the old snapshot on a bad refresh
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
docs/performance.md                 the measurements in full, how to read them, and how to reproduce them
```

## AI assistance

This solution was written with the help of an AI assistant (Claude, by Anthropic) and reviewed by me, Clifford Keeble.
