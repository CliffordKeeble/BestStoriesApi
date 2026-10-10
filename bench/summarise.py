#!/usr/bin/env python3
"""Summarise bench/wrk2-suite.sh output: the median of the VALID runs, with the spread of all of them.

    python3 bench/summarise.py results.csv

A run is valid when it achieved at least 98% of its target rate (closed-loop runs are always valid). Rejected runs
are counted in the table, never silently dropped: their raw output is still in the folder the CSV names.
"""
import csv
import statistics
import sys
from collections import defaultdict


def main(path: str) -> None:
    groups = defaultdict(list)
    for row in csv.DictReader(open(path, encoding="utf-8")):
        groups[(row["mode"], row["n"], int(row["rate"]), row["build"])].append(row)

    def stats(rows, key):
        valid = [float(r[key]) for r in rows if r["valid"] == "1" and r[key]]
        every = [float(r[key]) for r in rows if r[key]]
        if not valid:
            return None, every
        return statistics.median(valid), every

    def cell(rows, key, digits):
        median, every = stats(rows, key)
        if median is None:
            return "no valid run"
        spread = f"{min(every):.{digits}f}–{max(every):.{digits}f}" if len(every) > 1 else ""
        return f"{median:.{digits}f} ({spread})" if spread else f"{median:.{digits}f}"

    def rejected(rows):
        n = sum(1 for r in rows if r["valid"] != "1")
        return f"{n} of {len(rows)}" if n else "0"

    print("Latency, open loop: median of the valid runs in ms, with the range of every run in brackets.\n")
    print("| n | load | build | runs rejected | p50 | p99 | p99.9 |")
    print("|---:|---:|---|---:|---:|---:|---:|")
    for (mode, n, rate, build), rows in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1] == "-", int(kv[0][1]) if kv[0][1] != "-" else 0, kv[0][2], kv[0][3])):
        if mode != "open":
            continue
        label = "empty 404 (floor)" if build == "floor" else build
        print(f"| {n} | {rate:,}/s | {label} | {rejected(rows)} | {cell(rows, 'p50_ms', 2)} | {cell(rows, 'p99_ms', 2)} | {cell(rows, 'p999_ms', 2)} |")

    print("\nMost requests per second, closed loop (wrk, 64 connections): median, with the range.\n")
    print("| n | build | requests/s | p50 ms | p99 ms |")
    print("|---:|---|---:|---:|---:|")
    for (mode, n, rate, build), rows in sorted(groups.items()):
        if mode != "closed":
            continue
        print(f"| {n} | {build} | {cell(rows, 'achieved', 0)} | {cell(rows, 'p50_ms', 2)} | {cell(rows, 'p99_ms', 2)} |")


if __name__ == "__main__":
    main(sys.argv[1])
