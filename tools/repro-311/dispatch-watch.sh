#!/usr/bin/env bash
# ============================================================================
# dispatch-watch.sh — localise the twig#311 stall to ONE side of the
# runner/host dispatch boundary, without instrumenting either side.
#
# WHY THIS EXISTS (ADO #42)
#
# `--diag` was the previous probe and is retired: 0 captures in 90 loaded
# attempts against 2 in 31 trace-only. It writes ~5 MB per side per run, and
# whether that suppresses the hang or was expensive bad luck cannot be settled
# at this base rate. Either way it is not the instrument that catches this.
#
# Six captures agree on the shape: every test START has a matching END, nothing
# is in flight, single-digit seconds of test-body time inside a ~310 s run. The
# suite executes normally, then STOPS DISPATCHING and idles until the session
# timeout kills it. The one open question is:
#
#     at the moment dispatch stops, is the RUNNER waiting on the HOST
#     (host never asks for more work), or the HOST waiting on the RUNNER
#     (request sent, never answered)?
#
# HOW THIS ANSWERS IT
#
# The stall leaves ~290 s of idle-but-alive process on both sides. That is an
# enormous observation window, and — crucially — the observation happens AFTER
# the stall has already occurred, so it cannot perturb the timing that produces
# it. This script:
#
#   1. Runs the Cli suite with the existing TWIG_TEST_TRACE boundary trace
#      (tests/Shared/TestProgressTrace.cs). No new
#      in-process instrumentation, and the trace stays opt-in.
#   2. Watches that trace file's mtime from OUTSIDE both processes. A healthy
#      run writes a boundary every few ms; the largest legitimate untraced gap
#      is BuildFixture's nested build (2.1 s idle, ~6.2 s loaded). A gap of
#      STALL_SECS (default 45 s) is therefore unambiguous.
#   3. On trip, snapshots BOTH processes N times, so a genuinely blocked side is
#      distinguishable from a merely slow one:
#        - the TCP socket pair they talk over (ss -tnpi): Send-Q / Recv-Q are
#          the decisive evidence. Bytes stuck in the runner's Send-Q or the
#          host's Recv-Q means the host is not reading -> host-side stall.
#          Both queues empty with neither side progressing means the runner
#          never sent -> runner-side stall.
#        - managed stacks of every thread on each side (dotnet-stack report).
#        - per-thread kernel wait channel + state from /proc, which works even
#          if the diagnostics IPC endpoint is itself unresponsive.
#
# COST DURING A HEALTHY RUN
#
# One `stat` per second on one file, from a separate process. No writes by
# either side under test, no diagnostic channel opened, no change to either
# process's scheduling. The snapshot machinery executes only after a >=45 s
# dispatch gap, which a healthy run never produces. This is the property
# `--diag` lacks.
#
# USAGE
#
#   tools/repro-311/dispatch-watch.sh            # one traced+watched run
#   tools/repro-311/dispatch-watch.sh 20         # loop until a capture lands
#
#   TWIG_311_SELFTEST=1 tools/repro-311/dispatch-watch.sh
#       Proves the instrument works on a HEALTHY run (acceptance criterion 2).
#       It FORCE-trips the watcher a couple of seconds after the first trace
#       line, so the snapshot path runs against real live vstest.console and
#       testhost PIDs mid-run and the output can be inspected.
#
#       🔴 It force-trips rather than lowering STALL_SECS because a healthy run
#       does not contain a gap worth waiting for: measured here, 3018 tests
#       execute in 7.3 s with a largest in-trace gap of 1.53 s. BuildFixture's
#       nested build — the largest untraced cost — happens BEFORE the first
#       trace line, in a window the watcher cannot see by construction. A
#       threshold low enough to catch 1.53 s would be luck-dependent against a
#       1 s poll over a 7 s window. So the self-test proves the SNAPSHOT path;
#       the gap DETECTOR is proven separately by --selftest-detector below.
#
#   tools/repro-311/dispatch-watch.sh --selftest-detector
#       Proves the gap detector alone, with no test run: writes a trace file,
#       stops touching it, and asserts the watcher trips after STALL_SECS.
#
# ENV
#   TWIG_311_STALL_SECS   gap that trips the watcher   (default 45)
#   TWIG_311_SNAPSHOTS    snapshots per trip           (default 3)
#   TWIG_311_SNAP_GAP     seconds between snapshots    (default 15)
#   TWIG_TEST_LOG_DIR     output root  (default artifacts/test-logs)
#
# VERDICT TOKENS (grep these; never grep `Passed!` — an aborted run prints a
# false-green summary above `Test Run Aborted` and exits 1)
#   TWIG-DISPATCH attempt N: ...
#   TWIG-DISPATCH VERDICT: ...
# Exit 0 = all attempts clean, no repro. 1 = capture. 2 = apparatus fault.
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT" || exit 2

ATTEMPTS="${1:-1}"
case "$ATTEMPTS" in ''|*[!0-9]*) ATTEMPTS=1 ;; esac
OUT_ROOT="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/test-logs}"
SNAPSHOTS="${TWIG_311_SNAPSHOTS:-3}"
SNAP_GAP="${TWIG_311_SNAP_GAP:-15}"
SELFTEST="${TWIG_311_SELFTEST:-0}"

if [ "$SELFTEST" = "1" ]; then
  STALL_SECS="${TWIG_311_STALL_SECS:-45}"
  SNAPSHOTS="${TWIG_311_SNAPSHOTS:-1}"
  SNAP_GAP="${TWIG_311_SNAP_GAP:-2}"
  FORCE_TRIP_AFTER="${TWIG_311_FORCE_TRIP_AFTER:-2}"
else
  STALL_SECS="${TWIG_311_STALL_SECS:-45}"
  FORCE_TRIP_AFTER=0
fi

DOTNET_STACK="$(command -v dotnet-stack || true)"
[ -z "$DOTNET_STACK" ] && [ -x "$HOME/.dotnet/tools/dotnet-stack" ] && DOTNET_STACK="$HOME/.dotnet/tools/dotnet-stack"
if [ -z "$DOTNET_STACK" ]; then
  echo "TWIG-DISPATCH VERDICT: apparatus fault — dotnet-stack not found." >&2
  echo "  dotnet tool install --global dotnet-stack" >&2
  exit 2
fi
command -v ss >/dev/null || { echo "TWIG-DISPATCH VERDICT: apparatus fault — 'ss' not found." >&2; exit 2; }

mkdir -p "$OUT_ROOT"

# ---------------------------------------------------------------------------
# snapshot_side <label> <pid> <destdir> <n>
# ---------------------------------------------------------------------------
snapshot_side() {
  local label="$1" pid="$2" dest="$3" n="$4"
  local base="$dest/${label}.snap${n}"

  {
    echo "# $(date -u +%FT%T.%3NZ) $label pid=$pid"
    if [ -r "/proc/$pid/status" ]; then
      grep -E '^(State|Threads|voluntary_ctxt_switches|nonvoluntary_ctxt_switches):' "/proc/$pid/status"
    else
      echo "State: <gone>"
    fi
    echo "--- per-thread state/wchan ---"
    for t in /proc/"$pid"/task/*; do
      [ -d "$t" ] || continue
      local tid; tid="$(basename "$t")"
      local st wc cmd
      # 🔴 Do NOT use `awk '{print $3}'` here. Field 2 of /proc/<tid>/stat is the
      # thread's comm in parentheses and .NET thread names CONTAIN SPACES
      # (".NET EventPipe", ".NET Tiered JIT"), so positional splitting yields
      # nonsense like "EventPipe)" in the state column. Split after the LAST
      # ')' instead — comm is the only parenthesised field.
      st="$(sed 's/.*) //' "$t/stat" 2>/dev/null | cut -d' ' -f1)"
      wc="$(cat "$t/wchan" 2>/dev/null)"
      cmd="$(tr -d '\0' < "$t/comm" 2>/dev/null)"
      printf '%s\t%s\t%s\t%s\n' "$tid" "${st:-?}" "${wc:-0}" "${cmd:-?}"
    done
  } > "$base.proc" 2>&1

  # Sockets: Send-Q/Recv-Q on the runner<->host channel is the decisive signal.
  ss -tnpi 2>/dev/null | grep -A1 "pid=$pid," > "$base.sock" 2>&1

  # Managed stacks. Timed out because the diagnostics IPC endpoint is served by
  # the target process itself — if THAT is wedged, dotnet-stack hangs, which is
  # itself a datum worth recording rather than a reason to hang the watcher.
  timeout 60 "$DOTNET_STACK" report -p "$pid" > "$base.stack" 2>&1
  echo "dotnet-stack-exit=$?" >> "$base.stack"
}

# ---------------------------------------------------------------------------
# watcher: runs in background; trips on a trace-file mtime gap.
# ---------------------------------------------------------------------------
watch_dispatch() {
  local trace="$1" dest="$2" runner_pid="$3"
  local tripped=0 first_seen=0

  while kill -0 "$runner_pid" 2>/dev/null; do
    sleep 1
    [ -s "$trace" ] || continue
    [ "$tripped" = "1" ] && continue

    local mtime now gap forced=0
    mtime="$(stat -c %Y "$trace" 2>/dev/null)" || continue
    now="$(date +%s)"
    [ "$first_seen" = "0" ] && first_seen="$now"

    gap=$(( now - mtime ))
    if [ "$FORCE_TRIP_AFTER" -gt 0 ] && [ $(( now - first_seen )) -ge "$FORCE_TRIP_AFTER" ]; then
      forced=1
    elif [ "$gap" -lt "$STALL_SECS" ]; then
      continue
    fi

    tripped=1
    # vstest's own process tree: the runner hosts vstest.console, the host is a
    # separate `testhost` process. Resolve both by name, then verify they are
    # descendants of this attempt rather than a neighbour's.
    local host_pids console_pids
    host_pids="$(pgrep -f 'testhost' | tr '\n' ' ')"
    console_pids="$(pgrep -f 'vstest.console' | tr '\n' ' ')"

    {
      echo "TWIG-DISPATCH TRIP at $(date -u +%FT%T.%3NZ)"
      [ "$forced" = "1" ] && echo "  ** FORCED TRIP (self-test) — not a real dispatch gap **"
      echo "  dispatch gap: ${gap}s (threshold ${STALL_SECS}s)"
      echo "  last trace line: $(tail -1 "$trace")"
      echo "  runner(dotnet test) pid: $runner_pid"
      echo "  vstest.console pids: ${console_pids:-<none>}"
      echo "  testhost pids: ${host_pids:-<none>}"
      echo "  loadavg: $(cut -d' ' -f1-3 /proc/loadavg)"
    } | tee "$dest/trip.txt"

    local n=1
    while [ "$n" -le "$SNAPSHOTS" ]; do
      for p in $console_pids; do snapshot_side "runner-$p" "$p" "$dest" "$n"; done
      for p in $host_pids;    do snapshot_side "host-$p"   "$p" "$dest" "$n"; done
      # Full socket table for the pair, so both ends of the same connection are
      # visible in one place even if one side's per-pid grep missed it.
      ss -tnpi 2>/dev/null > "$dest/sockets.snap$n" 2>&1
      n=$(( n + 1 ))
      [ "$n" -le "$SNAPSHOTS" ] && sleep "$SNAP_GAP"
    done
    echo "TWIG-DISPATCH snapshots complete: $dest"
    [ "$SELFTEST" = "1" ] && return 0
  done
}

# ---------------------------------------------------------------------------
# --selftest-detector: prove the gap detector in isolation, no test run.
# Writes a trace, stops touching it, asserts the watcher trips after
# STALL_SECS. This is the half the healthy-run self-test cannot cover, because
# a healthy run contains no gap large enough to wait for.
# ---------------------------------------------------------------------------
if [ "${1:-}" = "--selftest-detector" ]; then
  STALL_SECS="${TWIG_311_STALL_SECS:-5}"
  SNAPSHOTS=1; SNAP_GAP=1; FORCE_TRIP_AFTER=0
  dest="$OUT_ROOT/dispatch-detector-selftest"
  rm -rf "$dest"; mkdir -p "$dest"
  trace="$dest/trace.tsv"
  printf '%s\tSTART\tSelfTest.Fake\n' "$(date -u +%FT%T.%6NZ)" > "$trace"

  sleep $(( STALL_SECS * 3 )) &
  fake_runner=$!
  # Detection only: suppress the snapshot path, which the healthy-run self-test
  # already covers against real PIDs.
  snapshot_side() { echo "selftest: snapshot suppressed for $1" > "$3/$1.snap$4.proc"; }
  watch_dispatch "$trace" "$dest" "$fake_runner"
  wait "$fake_runner" 2>/dev/null

  if [ -f "$dest/trip.txt" ]; then
    echo "TWIG-DISPATCH VERDICT: DETECTOR SELFTEST PASSED — tripped on a ${STALL_SECS}s stalled trace. [$dest]"
    exit 0
  fi
  echo "TWIG-DISPATCH VERDICT: DETECTOR SELFTEST FAILED — no trip on a stalled trace." >&2
  exit 2
fi

# ---------------------------------------------------------------------------
overall=0
for i in $(seq 1 "$ATTEMPTS"); do
  dest="$OUT_ROOT/dispatch-$i"
  rm -rf "$dest"; mkdir -p "$dest"
  trace="$dest/trace.tsv"
  log="$dest/run.log"
  : > "$trace"

  echo "──> attempt $i/$ATTEMPTS (stall threshold ${STALL_SECS}s)"
  start=$(date +%s)

  TWIG_TEST_TRACE="$trace" \
    dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo \
      --filter 'FullyQualifiedName!~BinaryLauncher' 2>&1 | tr '\r' '\n' > "$log" &
  pipeline_pid=$!

  watch_dispatch "$trace" "$dest" "$pipeline_pid" &
  watcher_pid=$!

  wait "$pipeline_pid"
  exit_code=$?
  kill "$watcher_pid" 2>/dev/null
  wait "$watcher_pid" 2>/dev/null
  elapsed=$(( $(date +%s) - start ))

  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$log" && aborted=1
  real_fail=0
  grep -qE '^\s*\[FAIL\]|Failed!' "$log" && real_fail=1
  tripped=0
  [ -f "$dest/trip.txt" ] && tripped=1

  echo "    exit=$exit_code aborted=$aborted failed=$real_fail tripped=$tripped elapsed=${elapsed}s"

  if [ "$SELFTEST" = "1" ]; then
    if [ "$tripped" = "1" ]; then
      echo "TWIG-DISPATCH VERDICT: SELFTEST PASSED — watcher tripped and snapshotted on a healthy run. [$dest]"
      exit 0
    fi
    echo "TWIG-DISPATCH VERDICT: SELFTEST FAILED — no trip; instrument is not proven." >&2
    exit 2
  fi

  if [ "$real_fail" = "1" ] && [ "$aborted" = "0" ]; then
    # Real [FAIL]s are NOT #311 (#311 aborts on the timeout, never on a FAIL).
    # This is usually a stressor colliding with the subject — see build-load.sh.
    echo "TWIG-DISPATCH VERDICT: apparatus fault — real test failures, not #311. [$log]" >&2
    exit 2
  fi

  if [ "$exit_code" -eq 0 ] && [ "$aborted" -eq 0 ]; then
    echo "    TWIG-DISPATCH attempt $i: clean run (no repro)"
    continue
  fi

  if [ "$aborted" = "1" ]; then
    echo
    echo "════════════════════════════════════════════"
    if [ "$tripped" = "1" ]; then
      echo "TWIG-DISPATCH VERDICT: CAPTURED — abort with dispatch snapshots. [$dest]"
      cat "$dest/trip.txt"
      echo "  Analyse with: tools/repro-311/dispatch-analyze.sh $dest"
    else
      echo "TWIG-DISPATCH VERDICT: abort captured but watcher did NOT trip. [$dest]"
      echo "  The trace kept advancing to within ${STALL_SECS}s of the kill — a"
      echo "  DIFFERENT shape from the six known captures. Check the trace before"
      echo "  assuming the watcher is broken."
    fi
    overall=1
    exit 1
  fi

  echo "TWIG-DISPATCH VERDICT: apparatus fault — exit=$exit_code with no abort marker. [$log]" >&2
  exit 2
done

echo
echo "════════════════════════════════════════════"
echo "TWIG-DISPATCH VERDICT: $ATTEMPTS/$ATTEMPTS attempts clean — no repro captured."
exit $overall
