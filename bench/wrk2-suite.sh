#!/usr/bin/env bash
# The latency and throughput figures in the README, re-run on your machine (Linux or WSL).
#
# Needs wrk2 (https://github.com/giltene/wrk2, built as "wrk"; give its path in WRK2) and wrk (apt install wrk).
# Start the fake Hacker News and the API first, in Release, ideally on separate cores:
#
#   taskset -c 0 dotnet run -c Release --project tools/FakeHackerNews &
#   ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5028 \
#   HackerNews__BaseAddress=http://localhost:5099/v0/ taskset -c 1 dotnet run -c Release --project src/BestStoriesApi &
#   WRK2=~/wrk2/wrk LOADCPU=0 bash bench/wrk2-suite.sh > my-results.csv
#
# wrk2 sends at a fixed rate and corrects for coordinated omission, so the percentiles hold under load.
# Each scenario runs three times; the README reports the median and the spread.
set -euo pipefail
WRK2=${WRK2:-wrk2}; BASE=${BASE:-http://localhost:5028}; RUNS=${RUNS:-3}
pin() { if [ -n "${LOADCPU:-}" ]; then taskset -c "$LOADCPU" "$@"; else "$@"; fi; }
ms() { awk -v v="$1" 'BEGIN{ if (v ~ /us$/) {sub(/us$/,"",v); print v/1000} else if (v ~ /ms$/) {sub(/ms$/,"",v); print v} else if (v ~ /m$/) {sub(/m$/,"",v); print v*60000} else {sub(/s$/,"",v); print v*1000} }'; }
echo "n,mode,rate,run,achieved,p50_ms,p90_ms,p99_ms,p999_ms,max_ms,non2xx"
for n in 10 200; do
  url="$BASE/api/beststories?n=$n"
  pin "$WRK2" -t1 -c64 -d8s -R2000 "$url" > /dev/null   # warm-up
  for rate in 2000 5000 7500; do for run in $(seq 1 "$RUNS"); do
    o=$(pin "$WRK2" -t1 -c64 -d15s -R"$rate" --latency "$url")
    p() { echo "$o" | awk -v k="$1" '$1==k {print $2; exit}'; }
    echo "$n,open,$rate,$run,$(echo "$o" | awk '/Requests\/sec/ {print $2}'),$(ms "$(p 50.000%)"),$(ms "$(p 90.000%)"),$(ms "$(p 99.000%)"),$(ms "$(p 99.900%)"),$(ms "$(p 100.000%)"),$(echo "$o" | awk '/Non-2xx/ {print $NF}' | sed 's/^$/0/')"
  done; done
  for run in $(seq 1 "$RUNS"); do
    o=$(pin wrk -t1 -c64 -d10s --latency "$url")
    echo "$n,closed,0,$run,$(echo "$o" | awk '/Requests\/sec/ {print $2}'),$(ms "$(echo "$o" | awk '$1=="50%" {print $2}')"),,$(ms "$(echo "$o" | awk '$1=="99%" {print $2}')"),,,"
  done
done
