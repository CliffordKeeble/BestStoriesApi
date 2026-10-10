```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 2 logical and 2 physical cores
.NET SDK 10.0.112
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                           | N   | Mean         | Error        | StdDev       | Median       | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------------- |---- |-------------:|-------------:|-------------:|-------------:|------:|--------:|-------:|----------:|------------:|
| **&#39;serialise per request (before)&#39;** | **10**  |  **3,231.13 ns** |    **97.551 ns** |   **278.320 ns** |  **3,134.02 ns** |  **1.01** |    **0.12** | **0.0229** |     **496 B** |        **1.00** |
| &#39;copy a pre-built slice (after)&#39; | 10  |     37.05 ns |     1.501 ns |     4.401 ns |     35.50 ns |  0.01 |    0.00 |      - |         - |        0.00 |
|                                  |     |              |              |              |              |       |         |        |           |             |
| **&#39;serialise per request (before)&#39;** | **200** | **63,892.69 ns** | **1,964.907 ns** | **5,542.055 ns** | **62,014.89 ns** |  **1.01** |    **0.12** |      **-** |     **496 B** |        **1.00** |
| &#39;copy a pre-built slice (after)&#39; | 200 |    951.90 ns |    18.800 ns |    20.896 ns |    948.50 ns |  0.02 |    0.00 |      - |         - |        0.00 |
