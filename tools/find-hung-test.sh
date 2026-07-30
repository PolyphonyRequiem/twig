#!/usr/bin/env bash
# ============================================================================
# find-hung-test.sh — name the test that was in flight when a run aborted.
#
# WHY (twig#311)
#
# When the Cli suite hits the 300 s vstest TestSessionTimeout, the host is killed
# mid-test. vstest prints a false-green `Passed! - Failed: 0` summary and does NOT
# name the test it was running, so the abort point (observed at 2377 / 2834 / 737
# tests) identifies nothing. `--diag` and `--logger trx` don't rescue you: a TRX is
# written at the END of a run, and an aborted run's TRX only describes the tests
# that COMPLETED — the in-flight one is absent, not marked.
#
# The fix is a boundary trace written and flushed OUTSIDE the test host's buffers.
# TestSupport/TestProgressTrace.cs appends a START line before each test and an END
# line after it. The last START with no matching END is the suspect.
#
# USAGE
#     tools/find-hung-test.sh                  # run Cli once under trace
#     tools/find-hung-test.sh 20               # loop until abort, max 20 attempts
#
# Exit 0 = every attempt passed (no repro). Exit 1 = an abort was captured and the
# in-flight test is named. A no-repro is DATA, not a failure of this script — the
# bug is intermittent (3 aborts in 9 runs observed).
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

ATTEMPTS="${1:-1}"
OUT_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/test-logs}"
mkdir -p "$OUT_DIR"

for i in $(seq 1 "$ATTEMPTS"); do
  trace="$OUT_DIR/cli-trace-$i.tsv"
  log="$OUT_DIR/cli-trace-$i.log"
  rm -f "$trace"

  echo "──> attempt $i/$ATTEMPTS"
  start=$(date +%s)
  TWIG_TEST_TRACE="$trace" \
    dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo \
      --filter 'FullyQualifiedName!~BinaryLauncher' 2>&1 | tr '\r' '\n' > "$log"
  exit_code=${PIPESTATUS[0]}
  elapsed=$(( $(date +%s) - start ))

  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$log" && aborted=1

  # A healthy run is ~2 min; an aborted one burns the full 300 s. Duration is the
  # single most reliable tell (issue #311), so report it every time.
  echo "    exit=$exit_code aborted=$aborted elapsed=${elapsed}s trace=$trace"

  if [ "$exit_code" -eq 0 ] && [ "$aborted" -eq 0 ]; then
    echo "    TWIG-TRACE attempt $i: clean run (no repro)"
    continue
  fi

  if [ ! -s "$trace" ]; then
    echo "TWIG-TRACE VERDICT: run failed but trace file is empty or missing." >&2
    echo "  The instrumentation did not execute — check TWIG_TEST_TRACE plumbing." >&2
    exit 1
  fi

  # Reconcile: walk the trace, tracking tests that STARTed and never ENDed.
  # Parallelization is disabled assembly-wide, so at most one should be open —
  # but the reconciliation does not assume that, and prints all of them.
  awk -F'\t' '
    $2 == "START" { open[$3] = $1; order[++n] = $3 }
    $2 == "END"   { delete open[$3] }
    END {
      printf "\n════════════════════════════════════════════\n"
      printf "TWIG-TRACE tests recorded: %d\n", n
      found = 0
      for (i = 1; i <= n; i++) {
        t = order[i]
        if (t in open) {
          printf "TWIG-TRACE IN-FLIGHT AT ABORT: %s (started %s)\n", t, open[t]
          found = 1
          delete open[t]
        }
      }
      if (!found) {
        printf "TWIG-TRACE IN-FLIGHT AT ABORT: none — every START had an END.\n"
        printf "  The stall is OUTSIDE a test body: collection/fixture teardown,\n"
        printf "  assembly cleanup, or the host failing to exit after the last test.\n"
        printf "  Last test to complete: %s\n", order[n]
      }
    }
  ' "$trace"

  exit 1
done

echo
echo "════════════════════════════════════════════"
echo "TWIG-TRACE VERDICT: $ATTEMPTS/$ATTEMPTS attempts clean — no repro captured."
exit 0
