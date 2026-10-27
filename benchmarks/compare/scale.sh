#!/usr/bin/env bash
# How the Ume LLM gateway and eneo scale, against the same fake LLM. See README.md in this folder.
#
#   1. Load:      concurrent clients LOAD_VUS on fixed resources (4 application cores each).
#   2. Resources: application cores CORES (eneo: one gunicorn worker per core). Each core count starts on a fresh
#      stack and is measured at PEAK_FACTORS × cores clients; the report uses the best throughput (the peak), so each
#      platform is judged at the load that suits it rather than at one that overloads the smaller configurations.
#
#   ./scale.sh
#   CORES="1 2 4" LOAD_VUS="1 16 128" DURATION=30s ./scale.sh
#   PLATFORMS="ume eneo" LOAD_VUS="" ./scale.sh      # resources only
#
# CPU sets are the shared layout in lib.sh (application from 8, CORES counts its hardware threads, i.e. cloud-style
# vCPUs), so neither the harness nor the database limits the application. 6 vCPUs borrow one thread each from the fake
# LLM and Postgres (checked 2026-10-27: the 4-vCPU peak on that smaller harness was the same within run-to-run noise). Results go to results/scale/ and
# results/scale-report.md.
set -euo pipefail
cd "$(dirname "$0")"

PLATFORMS="${PLATFORMS:-fake ume eneo}"
LOAD_VUS="${LOAD_VUS-1 2 4 8 16 32 64 128 256}" # empty skips the load sweep
CORES="${CORES:-1 2 4 6}"
PEAK_FACTORS="${PEAK_FACTORS:-2 4 8 32 64}"
STREAM="${STREAM:-true}"
DURATION="${DURATION:-20s}"
WARMUP="${WARMUP:-10s}"
source ./lib.sh

for platform in $PLATFORMS; do
  if [[ -n "${LOAD_VUS// }" ]]; then
    log "$platform: load scaling (4 hardware threads)"
    export APP_CPUS="8-11" NUM_WORKERS=4
    start_platform "$platform"
    warm_up "$platform"
    for vus in $LOAD_VUS; do
      measure "$platform" "$STREAM" "$vus" "$DURATION" "scale/load-$platform-vus_$vus"
    done
  fi

  [[ $platform == fake ]] && continue # fixed resources; its load curve shows the harness ceiling

  for cores in $CORES; do
    log "$platform: $cores hardware thread(s), fresh stack"
    export APP_CPUS="8-$((7 + cores))" NUM_WORKERS="$cores"
    # 6 vCPUs borrow one thread from the fake LLM (3) and one from Postgres (12); both use well under what remains.
    [[ $cores == 6 ]] && export APP_CPUS="3,8-12" FAKE_CPUS="0-2" PG_CPUS="13"
    start_platform "$platform"
    warm_up "$platform"
    for factor in $PEAK_FACTORS; do
      vus=$((factor * cores))
      measure "$platform" "$STREAM" "$vus" "$DURATION" "scale/cores-$platform-cores_$cores-vus_$vus"
    done
  done
done

stop_all
log "Report"
python3 scale_report.py results/scale | tee results/scale-report.md
