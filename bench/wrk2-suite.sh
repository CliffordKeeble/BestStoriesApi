#!/usr/bin/env bash
# The latency and throughput tables in docs/performance.md, re-run on your machine (Linux or WSL).
#
# Needs wrk2 (https://github.com/giltene/wrk2, built as "wrk"; give its path in WRK2) and wrk (apt install wrk).
# Start the fake Hacker News and the API (and, for a before/after comparison, the first version on port 5029, as
# docs/performance.md shows):
#
#   taskset -c 0 dotnet run -c Release --project tools/FakeHackerNews &
#   ASPNETCORE_URLS=http://localhost:5028 HackerNews__BaseAddress=http://localhost:5099/v0/ \
#     taskset -c 1 dotnet run -c Release --project src/BestStoriesApi --no-launch-profile &
#   AFTER=http://localhost:5028 BEFORE=http://localhost:5029 WRK2=~/wrk2/wrk LOADCPU=0 OUT=my-raw \
#     bash bench/wrk2-suite.sh > my-results.csv
#   python3 bench/summarise.py my-results.csv
#
# - wrk2 sends at a fixed rate and corrects for coordinated omission: each request is timed from when it was due.
# - The builds are INTERLEAVED, run by run (before, after, before, after...), so a noisy minute on a shared
#   machine lands on both rather than on one.
# - A run that achieved less than 98% of its target rate is kept, and marked valid=0: it measured a client or a
#   machine that could not keep up, and the summary leaves it out of the median. Every run's full output is kept
#   in $OUT, named in the CSV.
# - The floor is an empty 404 from the AFTER server, at each rate, in the same rotation.
set -euo pipefail
WRK2=${WRK2:-wrk2}; AFTER=${AFTER:-http://localhost:5028}; BEFORE=${BEFORE:-}; RUNS=${RUNS:-3}; OUT=${OUT:-bench-raw}
RATES=${RATES:-"2000 5000 7500"}; NS=${NS:-"10 200"}
mkdir -p "$OUT"
pin() { if [ -n "${LOADCPU:-}" ]; then taskset -c "$LOADCPU" "$@"; else "$@"; fi; }
ms() { awk -v v="$1" 'BEGIN{ if (v=="") {print ""} else if (v ~ /us$/) {sub(/us$/,"",v); print v/1000} else if (v ~ /ms$/) {sub(/ms$/,"",v); print v} else if (v ~ /m$/) {sub(/m$/,"",v); print v*60000} else {sub(/s$/,"",v); print v*1000} }'; }
builds() { if [ -n "$BEFORE" ]; then echo "before after"; else echo "after"; fi; }
base() { if [ "$1" = before ]; then echo "$BEFORE"; else echo "$AFTER"; fi; }

open_run() { # build n rate run path label
  local file="$OUT/open_$6_r$3_run$4.txt"
  pin "$WRK2" -t1 -c64 -d15s -R"$3" --latency "$(base "$1")$5" > "$file"
  local o; o=$(cat "$file")
  p() { echo "$o" | awk -v k="$1" '$1==k {print $2; exit}'; }
  local ach; ach=$(echo "$o" | awk '/Requests\/sec/ {print $2}')
  local valid; valid=$(awk -v a="$ach" -v r="$3" 'BEGIN{print (a >= 0.98*r) ? 1 : 0}')
  local bad; bad=$(echo "$o" | awk '/Non-2xx/ {print $NF}')
  echo "$1,$2,open,$3,$4,$ach,$valid,$(ms "$(p 50.000%)"),$(ms "$(p 90.000%)"),$(ms "$(p 99.000%)"),$(ms "$(p 99.900%)"),$(ms "$(p 100.000%)"),${bad:-0},$file"
}

echo "build,n,mode,rate,run,achieved,valid,p50_ms,p90_ms,p99_ms,p999_ms,max_ms,non2xx,raw"
for b in $(builds); do for n in $NS; do pin "$WRK2" -t1 -c64 -d8s -R2000 "$(base "$b")/api/beststories?n=$n" > /dev/null; done; done   # warm-up
for n in $NS; do
  for rate in $RATES; do
    for run in $(seq 1 "$RUNS"); do
      for b in $(builds); do open_run "$b" "$n" "$rate" "$run" "/api/beststories?n=$n" "${b}_n$n"; done
      if [ "$n" = "$(echo $NS | awk '{print $1}')" ]; then open_run floor - "$rate" "$run" "/nothing-here" "floor404"; fi
    done
  done
  for run in $(seq 1 "$RUNS"); do
    for b in $(builds); do
      file="$OUT/closed_${b}_n${n}_run${run}.txt"
      pin wrk -t1 -c64 -d10s --latency "$(base "$b")/api/beststories?n=$n" > "$file"
      o=$(cat "$file")
      echo "$b,$n,closed,0,$run,$(echo "$o" | awk '/Requests\/sec/ {print $2}'),1,$(ms "$(echo "$o" | awk '$1=="50%" {print $2}')"),,$(ms "$(echo "$o" | awk '$1=="99%" {print $2}')"),,,$(echo "$o" | awk '/Non-2xx/ {print $NF}' | sed 's/^$/0/'),$file"
    done
  done
done
