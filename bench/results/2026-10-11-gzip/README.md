# Pre-compressed answers, 11 October 2026: raw outputs

The figures in [`docs/performance.md`](../../../docs/performance.md), "Pre-compressed answers", come from these runs. The method is described there.

Each file is one 10-second `wrk -t1 -c64 --latency` run, named `<build>_n<n>_<encoding>_r<round>.txt`. `summary.json` in each folder holds the medians, the server's and the client's CPU use in each run, and the answer sizes.

- **`before`**: commit 8af6fc4, before max-age and gzip.
- **`after`**: this change. `plain` sent no `Accept-Encoding`; `gzip` sent `Accept-Encoding: gzip`.
- **`middleware`**: this change's max-age, without pre-compressed answers, with ASP.NET Core's response compression middleware (gzip, at its default level, Fastest) compressing every answer per request.

`real-stories` used 218 real stories (6 to 11 October 2026, more than 100 points, from the Hacker News search API) served by a small stand-in; `fake-stories` used `tools/FakeHackerNews`.
