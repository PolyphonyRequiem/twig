#!/usr/bin/env bash
# ============================================================================
# run-tests.sh — the only supported way to run twig's test suites.
#
# WHY THIS EXISTS (twig#311, and #257 before it)
#
# `dotnet test` prints a summary line that is NOT a verdict. When a run aborts —
# vstest session timeout, test-host crash — it still prints:
#
#     Aborting test run: test run timeout of 300000 milliseconds exceeded.
#     Passed!  - Failed: 0, Passed: 1237, Skipped: 0, Total: 1237, Duration: 8 s
#     Test Run Aborted.
#
# ...and exits 1. The documented grep `grep -E "Passed!|Failed!"` matches that
# `Passed!` line and reports success. A TRX report does not rescue you either:
# its counters only describe the portion completed before the host died, so an
# aborted run still shows aborted="0" notExecuted="0".
#
# That false green already cost this repo one bogus issue report (#257, closed
# as invalid). Guidance in AGENTS.md telling humans to "remember the exit code"
# demonstrably does not hold — so this script removes the judgement call: it
# reconciles the exit code, the abort markers, and the reported test total, and
# prints a single unambiguous verdict line that CANNOT grep as a pass unless the
# run really passed.
#
# USAGE
#     tools/run-tests.sh                 # all four suites, serially
#     tools/run-tests.sh Cli             # one suite by short name
#     tools/run-tests.sh Cli Domain      # several
#
# EXIT CODE: 0 only if every suite is a genuine, unaborted pass.
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

LOG_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/test-logs}"
mkdir -p "$LOG_DIR"

# BinaryLauncherTests spawns a child binary that cannot resolve the SQLite native
# lib under a user-local SDK, killing the host mid-run. Environmental, passes in CI.
CLI_FILTER='FullyQualifiedName!~BinaryLauncher'

suite_project() {
  case "$1" in
    Cli)            echo "tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj" ;;
    Infrastructure) echo "tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj" ;;
    Mcp)            echo "tests/Twig.Mcp.Tests/Twig.Mcp.Tests.csproj" ;;
    Domain)         echo "tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj" ;;
    *)              echo "" ;;
  esac
}

ALL_SUITES="Cli Infrastructure Mcp Domain"
SUITES="${*:-$ALL_SUITES}"

OVERALL=0
VERDICT_LINES=""

for suite in $SUITES; do
  project="$(suite_project "$suite")"
  if [ -z "$project" ]; then
    echo "run-tests: unknown suite '$suite' (known: $ALL_SUITES)" >&2
    exit 2
  fi

  log="$LOG_DIR/$suite.log"
  echo "──> $suite"

  # `dotnet test` takes exactly one project per invocation, and two concurrent
  # runs collide over shared build output (bogus SQLitePCL DllNotFoundException),
  # so this loop is deliberately serial.
  # VSTest writes its progress indicator with carriage returns, so redirecting
  # straight to a file leaves the final
  # `Passed! - Failed: 0, Passed: N, ...` summary partially OVERWRITTEN — under
  # the .NET 11 preview SDK the log kept only a `... 1870, Skipped: 0, Total:
  # 1870` fragment with no `Passed:` token. The run still exits 0, so the
  # verdict below read `PASSED (0 tests)`: precisely the count-free false green
  # this script exists to eliminate. `-tl:off` does NOT fix it (the terminal
  # logger is not the writer). Translating CR to LF preserves every line.
  # `PIPESTATUS[0]` is required — `$?` would report tr's status, not the test's.
  if [ "$suite" = "Cli" ]; then
    dotnet test "$project" --nologo --filter "$CLI_FILTER" 2>&1 | tr '\r' '\n' > "$log"
  else
    dotnet test "$project" --nologo 2>&1 | tr '\r' '\n' > "$log"
  fi
  exit_code=${PIPESTATUS[0]}

  # ---- Reconcile three independent signals; any disagreement is a FAIL. ----
  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$log" && aborted=1

  # A filtered-to-empty run exits 0 while running nothing.
  no_tests=0
  grep -q 'No test matches the given testcase filter' "$log" && no_tests=1

  passed_count="$(grep -oE 'Passed: +[0-9]+' "$log" | tail -1 | grep -oE '[0-9]+')"
  passed_count="${passed_count:-0}"

  if [ "$exit_code" -ne 0 ]; then
    verdict="FAILED"
    reason="process exit code $exit_code"
  elif [ "$aborted" -eq 1 ]; then
    # Defensive: an abort should always exit non-zero, but the whole point of this
    # script is not to trust one signal.
    verdict="FAILED"
    reason="run aborted (exit code was 0 — do not trust it)"
  elif [ "$no_tests" -eq 1 ]; then
    verdict="FAILED"
    reason="filter matched no tests — a vacuous run is not a pass"
  else
    verdict="PASSED"
    reason="$passed_count tests"
  fi

  [ "$verdict" = "FAILED" ] && OVERALL=1

  # NOTE: the verdict deliberately does NOT contain the string "Passed!". Anyone
  # grepping this output for a pass must match TWIG-VERDICT, which is computed
  # from the exit code and abort markers rather than from vstest's summary prose.
  line="TWIG-VERDICT $suite: $verdict ($reason) [log: $log]"
  VERDICT_LINES="$VERDICT_LINES$line"$'\n'
  echo "$line"

  if [ "$verdict" = "FAILED" ]; then
    grep -E '\[FAIL\]|error CS|Test Run Aborted|Aborting test run' "$log" | head -20
  fi
done

echo
echo "════════════════════════════════════════════"
printf '%s' "$VERDICT_LINES"
if [ "$OVERALL" -eq 0 ]; then
  echo "TWIG-VERDICT OVERALL: PASSED"
else
  echo "TWIG-VERDICT OVERALL: FAILED"
fi
exit "$OVERALL"
