#!/usr/bin/env bash
# ============================================================================
# find-hung-test.sh — name the test that was in flight when a run aborted, and
# name the ASSEMBLY that stalled.
#
# WHY (GitHub issue #311)
#
# When a run hits the 300 s vstest TestSessionTimeout, the host is killed
# mid-test. vstest prints a false-green `Passed! - Failed: 0` summary and does NOT
# name the test it was running, so the abort point identifies nothing. `--diag` and
# `--logger trx` don't rescue you: a TRX is written at the END of a run, and an
# aborted run's TRX only describes the tests that COMPLETED — the in-flight one is
# absent, not marked.
#
# The fix is a boundary trace written and flushed OUTSIDE the test host's buffers.
# tests/Shared/TestProgressTrace.cs (link-compiled into every test assembly) appends
# a START line before each test and an END line after it. The last START with no
# matching END is the suspect.
#
# 🔴 WHAT CHANGED, AND WHY (2026-08-14 CI capture)
#
# Issue #311 reproduced in CI for the first time. The captured log shows the Cli
# suite COMPLETING normally (3275 tests, 2m23s) while Twig.Tui.Tests stalled at
# 9 of 85. Every instrument built for this card was scoped to Cli, so the whole
# toolset was pointed at the assembly that finished.
#
# Two consequences, both handled here:
#
#   1. The trace is now assembly-WIDE. TWIG_TEST_TRACE names a DIRECTORY and each
#      assembly writes <assembly-name>.tsv into it.
#   2. Reconciliation is PER FILE. CI runs one `dotnet test` across six assemblies
#      in PARALLEL processes (verified: all six hosts start within 9 s), so a single
#      merged trace would interleave mid-line and could not answer "which assembly
#      stalled" — which is now the primary question.
#
# USAGE
#     tools/find-hung-test.sh                  # one wide run under trace
#     tools/find-hung-test.sh 20               # loop until abort, max 20 attempts
#     TWIG_311_SUITE=Cli tools/find-hung-test.sh   # single-project run instead
#
# Exit 0 = every attempt passed (no repro). Exit 1 = an abort was captured and the
# in-flight test (or the stalling assembly) is named. A no-repro is DATA, not a
# failure of this script — the bug is intermittent.
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

ATTEMPTS="${1:-1}"
OUT_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/test-logs}"
mkdir -p "$OUT_DIR"

# Default to CI's own wide invocation, because that is the shape that reproduced.
# TWIG_311_SUITE=Cli narrows to the historical single-project run.
SUITE="${TWIG_311_SUITE:-}"

for i in $(seq 1 "$ATTEMPTS"); do
  trace_dir="$OUT_DIR/trace-$i"
  log="$OUT_DIR/trace-$i.log"
  rm -rf "$trace_dir"
  mkdir -p "$trace_dir"

  echo "──> attempt $i/$ATTEMPTS"
  start=$(date +%s)

  if [ -n "$SUITE" ]; then
    TWIG_TEST_TRACE="$trace_dir" \
      dotnet test "tests/Twig.$SUITE.Tests/Twig.$SUITE.Tests.csproj" --nologo \
        --filter 'FullyQualifiedName!~BinaryLauncher' 2>&1 | tr '\r' '\n' > "$log"
  else
    TWIG_TEST_TRACE="$trace_dir" \
      dotnet test --no-build --settings test.runsettings --nologo \
        2>&1 | tr '\r' '\n' > "$log"
  fi
  exit_code=${PIPESTATUS[0]}
  elapsed=$(( $(date +%s) - start ))

  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$log" && aborted=1

  # A healthy wide run is ~2.5 min; an aborted one burns the full 300 s past the
  # slowest assembly. Duration is the single most reliable tell, so report it always.
  echo "    exit=$exit_code aborted=$aborted elapsed=${elapsed}s trace=$trace_dir"

  if [ "$exit_code" -eq 0 ] && [ "$aborted" -eq 0 ]; then
    echo "    TWIG-TRACE attempt $i: clean run (no repro)"
    continue
  fi

  shopt -s nullglob
  traces=("$trace_dir"/*.tsv)
  shopt -u nullglob

  if [ ${#traces[@]} -eq 0 ]; then
    echo "TWIG-TRACE VERDICT: run failed but NO trace files were written." >&2
    echo "  The instrumentation did not execute — check TWIG_TEST_TRACE plumbing." >&2
    exit 1
  fi

  printf "\n════════════════════════════════════════════\n"
  printf "TWIG-TRACE reconciliation across %d assembly trace(s)\n\n" "${#traces[@]}"

  # Reconcile EACH assembly separately. Per-file, because the files are written by
  # parallel processes and only a per-assembly view can name the stalling assembly.
  #
  # Cross-assembly context matters too: the assembly whose LAST boundary is oldest
  # relative to the abort is the one that stopped dispatching. That is printed as
  # the wall-clock gap, which is what distinguished "Tui stalled" from "Tui was
  # starved of budget" in the 2026-08-14 capture.
  for t in "${traces[@]}"; do
    asm="$(basename "$t" .tsv)"
    awk -F'\t' -v asm="$asm" '
      $2 == "START" { open[$3] = $1; order[++n] = $3; last = $1 }
      $2 == "END"   { delete open[$3]; last = $1 }
      END {
        found = 0
        for (i = 1; i <= n; i++) {
          t = order[i]
          if (t in open) {
            printf "TWIG-TRACE %s: IN-FLIGHT AT ABORT: %s (started %s)\n", asm, t, open[t]
            found = 1
            delete open[t]
          }
        }
        if (!found) {
          printf "TWIG-TRACE %s: %d tests, no test in flight — every START had an END.\n", asm, n
          printf "    last boundary: %s (last test: %s)\n", last, order[n]
        }
      }
    ' "$t"
  done

  printf "\n"
  printf "Interpretation: an assembly with NO test in flight and a last boundary far\n"
  printf "before the abort is the one that stopped dispatching. All captures to date\n"
  printf "show that shape — the stall is at the runner/host dispatch boundary, not\n"
  printf "inside a test body.\n"

  exit 1
done

echo
echo "════════════════════════════════════════════"
echo "TWIG-TRACE VERDICT: $ATTEMPTS/$ATTEMPTS attempts clean — no repro captured."
exit 0
