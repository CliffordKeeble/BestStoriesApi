# Performance

The figures summarised at the top of the [README](../README.md) are here in full, with where and how they were measured, how to read them, where each gain came from, and how to reproduce them. The commands run from the repository root.

**Where and how it was measured.**
- **The machine:** 10 October 2026, **one core of a shared 2-vCPU cloud VM** (Xeon, .NET 10.0.12, Release).
- **The load generator:** [wrk2](https://github.com/giltene/wrk2), on the other core. It sent at a fixed rate, correcting for coordinated omission, so a stall is charged to every request that queued behind it.
- **The comparison:** the first version and this one were running side by side and measured **alternately, run by run**, so a noisy minute on the shared VM lands on both.
- **The runs:** each figure is the median of three 15-second runs, with the range of all three in brackets. A run that fell more than 2% short of its target rate was left out of the median and counted; there were two, both of this version. Throughput runs were 10 seconds, with `wrk`.
- **Everything else:** the full method, every raw output and the other measurements are in [`bench/results/2026-10-10.md`](../bench/results/2026-10-10.md).

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

**On a laptop** (Ryzen 5 PRO 7535U, 6 cores and 12 threads, Windows 11), measured by a second operator with this repository's load generator on the same machine as the server, on 11 October, on mains. The tables are in [`bench/results/2026-10-11-laptop.md`](../bench/results/2026-10-11-laptop.md).
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

## Reproduce the figures

You need three terminals, and a fourth for a before/after:
- **one runs a fake Hacker News**, which serves the two endpoints the API uses with 40 ms of latency, moves scores over time and counts every call;
- **one runs the API**, pointed at the fake;
- **for a before/after, one runs the first version**, also pointed at the fake;
- **the last runs the load.**

```bash
dotnet run -c Release --project tools/FakeHackerNews
```

```bash
dotnet run -c Release --project src/BestStoriesApi --no-launch-profile -- --urls http://localhost:5028 --HackerNews:BaseAddress=http://localhost:5099/v0/
```

```bash
# The first version is commit 4ee5907. Run this from the repository root, so that the root's global.json chooses the SDK.
git worktree add ../first-version 4ee5907
dotnet run -c Release --project ../first-version/src/BestStoriesApi --no-launch-profile -- --urls http://localhost:5029 --HackerNews:BaseAddress=http://localhost:5099/v0/
```

```bash
# Any OS: the load generator in this repository, no installs.
dotnet run -c Release --project tools/LoadTest -- --suite --base http://localhost:5028 --hn-stats http://localhost:5099/stats

# Linux or WSL, the instrument the tables used, with the first version on port 5029 for the before.
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
