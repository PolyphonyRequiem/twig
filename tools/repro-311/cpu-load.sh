#!/bin/bash
# Load generator for twig#311 repro hunting.
# Saturates CPU so the nested `dotnet build` inside BuildFixture's constructor
# contends with the outer build/test host — the condition both observed failure
# clusters are consistent with.
#
# 🔴 ORPHANED SPINNERS. Earlier this script left its children running: they are
# `bash -c 'while :; do :; done'` processes that do NOT match `cpu-load.sh`, so
# `pkill -f cpu-load.sh` killed the parent and left the box pinned at load ~20
# indefinitely. Verified 2026-07-30; it silently poisoned a baseline run an hour
# later. It now traps INT/TERM/EXIT and kills its own process group.
#
# To be certain the box is clean before trusting any measurement:
#     pkill -9 -f 'SECONDS=0; while'; cut -d' ' -f1-3 /proc/loadavg
set -u
DURATION="${1:-3600}"
WORKERS="${2:-16}"

pids=()
cleanup() {
  trap - INT TERM EXIT
  # 🔴 Killing the `timeout` PIDs is NOT enough when `timeout` wraps the
  # spinner: each spawns the loop as its OWN child, and `pkill -P $$` only
  # reaches direct children — verified, it left 4 of 4 spinners alive. The fix
  # is to not use `timeout` at all: each worker below is a single bash process
  # that enforces its own deadline, so it IS our direct child and dies with us.
  for p in "${pids[@]:-}"; do
    [ -n "$p" ] && kill -9 "$p" 2>/dev/null
  done
  pkill -9 -P $$ 2>/dev/null
}
trap cleanup INT TERM EXIT

for i in $(seq 1 "$WORKERS"); do
  # Self-deadlining spinner, no `timeout` wrapper — see cleanup() above.
  # SECONDS is bash's own elapsed-time counter, so this costs no subprocesses.
  bash -c 'SECONDS=0; while [ "$SECONDS" -lt '"$DURATION"' ]; do :; done' &
  pids+=("$!")
done
echo "load: $WORKERS spinners for ${DURATION}s (pid $$) — Ctrl-C or SIGTERM reaps them"
wait
