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

## How to run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins 10.0.401 or a later 10.0 feature band).

```bash
dotnet run --project src/BestStoriesApi --launch-profile http
```

The API listens on `http://localhost:5028`. In the Development environment (the launch profile sets it) Swagger UI is at `http://localhost:5028/swagger`. Try it:

```bash
curl "http://localhost:5028/api/beststories?n=5"
```

The first request after start-up waits a second or two while the first snapshot is built. After that, requests are answered from memory.

### Run the tests

```bash
dotnet test
```

The tests use NUnit and Moq. Hacker News is replaced by a Moq fake, so they need no network and take a few seconds.

### Configuration

`src/BestStoriesApi/appsettings.json`, section `HackerNews`:

| Setting | Default | Meaning |
|---|---|---|
| `BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | The Hacker News API. |
| `RefreshInterval` | `00:01:00` | How often the snapshot is rebuilt. |
| `MaxParallelFetches` | `10` | How many story details are fetched at once during a refresh. |

## How the load requirement is met

The requirement is to serve large numbers of requests without overloading Hacker News. The rule that does the work: **a caller's request never causes a call to Hacker News.**

- **One snapshot in memory.** The API holds a single ranked list of all the best stories (around 200). Every request reads that list and takes the first `n`. Cost per request: a slice of an in-memory list.
- **One background refresher.** A hosted service rebuilds the snapshot every `RefreshInterval`. There is one such service, and it does one refresh at a time, so the number of calls to Hacker News is set by the clock (about 200 story fetches a minute at the default) and not by how many callers there are. 10 callers or 10,000 callers make the same number of upstream calls.
- **Cold start.** A request that arrives before the first refresh has finished waits for that refresh. It does not start one of its own. Fifty simultaneous requests at start-up cause one refresh (there is a test for exactly this).
- **Bounded fetching.** During a refresh, story details are fetched at most `MaxParallelFetches` at a time, so a refresh is a steady trickle and not a burst of 200 requests.
- **Resilient upstream calls.** Calls to Hacker News go through a named `HttpClient` with `Microsoft.Extensions.Http.Resilience`'s standard handler: timeouts, retries with back-off, and a circuit breaker. A slow or flaky upstream cannot hang a refresh.
- **Failure keeps the old data.** If a refresh fails, the previous snapshot keeps being served and the failure is logged. A single story that cannot be loaded is skipped (and logged). A placeholder is never stored.

## Assumptions

1. **"Best n stories" means the best by score across the whole list.** Hacker News's best-stories list is its own ranking. The test asks for the stories "sorted by their score in a descending order", and the version of the brief dated 20 July 2023 says "the best n stories ... as determined by their score". So the API ranks the whole list by score, highest first, and then takes `n`. It does not take the first `n` ids and sort those. Equal scores keep the order Hacker News gave them.
2. **`n` is required and must be a whole number of at least 1.** Anything else (missing, `0`, negative, not a number) returns `400 Bad Request`.
3. **If `n` is larger than the number of stories available, you get all of them** (about 200), with status 200. It is not an error.
4. **`uri` is `null` when a story has no link.** "Ask HN" posts have no `url` in Hacker News. The API says so, with `"uri": null`, and does not invent a link.
5. **`time` is ISO 8601 with a UTC offset**, for example `2019-10-12T13:43:01+00:00`. Hacker News gives Unix seconds, which are converted.
6. **`commentCount` is Hacker News's `descendants`** (all comments, including replies). It is `0` if Hacker News gives none.
7. **Deleted and dead items are left out**, as is any story whose title or author is missing.
8. **Data can be up to one refresh interval old** (a minute by default). A story's score on this API can lag Hacker News by that much.
9. **If the very first refresh fails**, there is nothing to serve, so requests get `503 Service Unavailable` until a later refresh succeeds.
10. **One instance.** The snapshot lives in the memory of the running process.

## What I would do next, given time

- **Several instances:** each instance would currently build its own snapshot, which multiplies the upstream calls by the number of instances. A shared cache (Redis, say) with one elected refresher would keep it to one set of calls.
- **Rate limiting on our own endpoint** (ASP.NET Core's built-in rate limiter), so one client cannot hog the API.
- **Health checks:** a readiness check that is healthy only when a snapshot exists, and one that reports the age of the snapshot.
- **Metrics and tracing** (OpenTelemetry): snapshot age, refresh duration, number of stories skipped, upstream failures.
- **Conditional and incremental refresh:** only re-fetch stories whose details are likely to have changed, rather than all of them every minute.
- **Response caching headers** (`Cache-Control`, ETag) so clients and a CDN can cache responses too.
- **Tests that exercise the real HTTP client** against a local fake server, to cover the retry and timeout settings. Today those settings are Microsoft's defaults and are not tested here.
- **Containerise it** and add a CI build that runs `dotnet test`.

## Layout

```
src/BestStoriesApi/
  Controllers/BestStoriesController.cs   the endpoint: validate n, read the snapshot, take n
  Stories/StoryStore.cs                  holds the current snapshot; callers wait here at cold start
  Stories/StoryRefreshService.cs         background loop: refresh now, then every interval
  Stories/StoryRefresher.cs              fetches, skips failures, ranks the whole list
  HackerNews/                            the upstream client, its options, and its JSON shape
tests/BestStoriesApi.Tests/              NUnit + Moq
```

## AI assistance

This solution was written with the help of an AI assistant (Claude, by Anthropic) and reviewed by me, Clifford Keeble.
