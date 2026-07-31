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
# 🔴 THE CLEANUP COMMAND CAN KILL ITSELF. Verified ADO #43, 2026-07-31. Running
# the teardown as a ONE-LINER is the trap:
#
#     pkill -f build-load.sh; pkill -9 -f 'SECONDS=0; while'; pkill -f cpu-load.sh
#
# The shell executing that line carries the WHOLE line in its own argv, so the
# middle `pkill -9` matches its own parent shell and SIGKILLs it. Everything after
# the semicolon never runs, and the spinners survive — while the operator sees a
# command that "completed". The observed symptom is a shell exiting with -9/-15 and
# 16 spinners still alive. Bracketing the pattern does NOT save you here: the
# de-bracketed text still sits in the wrapper's argv.
#
# Two consequences, both bitten for real:
#   * Run each pkill as its OWN command, or use the self-excluding form below.
#   * `ps | grep -c` / `pgrep -c` inflate for the same reason. A count of "2" with
#     zero real processes is the checking command matching itself. ALWAYS confirm a
#     non-zero count by listing the actual rows before believing it.
#
# Self-excluding teardown (safe to paste as one line — `--older` skips the
# just-spawned checking process, `-$$` excludes this shell's own pgid):
#
#     pkill -9 -f 'SECONDS=0' --older 2 ; pkill -9 -f 'cpu-load\.sh' --older 2
#     ps -eo pid,args --no-headers | grep 'SECONDS=0' | grep -v grep   # list, don't count
#     cut -d' ' -f1-3 /proc/loadavg    # 1-min decaying avg: it TRAILS reality
#
# To be certain the box is clean before trusting any measurement, list rows:
#     ps -eo pid,args --no-headers | grep 'SECONDS=0' | grep -v grep
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
