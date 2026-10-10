```

BenchmarkDotNet v0.15.4, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 2 logical and 2 physical cores
.NET SDK 10.0.112
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                                          | Mean     | Error   | StdDev   | Gen0   | Allocated |
|------------------------------------------------ |---------:|--------:|---------:|-------:|----------:|
| &#39;build a 200-story snapshot (once per refresh)&#39; | 362.0 μs | 9.65 μs | 27.99 μs | 6.3477 | 109.39 KB |
