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
#     tools/run-tests.sh --pre-push      # the four suites, THEN CI's own commands
#     tools/run-tests.sh --selftest      # prove the guards can fail AND pass
#
# EXIT CODE: 0 only if every suite is a genuine, unaborted pass.
#
# A USAGE ERROR IS ALSO A VERDICT (AB#352). An unknown option or suite name
# exits 2 AND prints `TWIG-VERDICT OVERALL: FAILED (... — nothing ran)` to
# stdout, so the grep AGENTS.md mandates cannot come back empty — empty output
# contains no FAILED and therefore reads as a pass.
#
# WHY --pre-push EXISTS (AB#248, closing AB#246's admission)
#
# The four suites are NECESSARY BUT NOT SUFFICIENT. CI runs SIX assemblies
# unfiltered and compiles the whole solution (including tests/Twig.Benchmarks,
# which is IsTestProject=false — CI builds it and never tests it). AGENTS.md
# used to ask a human to run CI's three commands by hand and "read the exit
# code" — the exact judgement call this script exists to abolish. `--pre-push`
# runs them, reconciles them with the SAME three signals, and folds the result
# into TWIG-VERDICT OVERALL.
#
# The specific false green it must catch: if the solution-wide BUILD is skipped
# and `dotnet test --no-build` runs anyway, vstest tests whatever assemblies
# happen to be on disk, prints clean `Passed!` lines for them, and reports the
# missing one only as a single line near the TOP of the log —
#
#     The argument .../Twig.Tui.Tests.dll is invalid. Please use the /help option
#
# — which contains neither "error" nor "fail", so it survives the documented
# grep recipe and has scrolled off screen by the time the run finishes. Only the
# non-zero exit code catches it. That is why the reconciler below leads with the
# exit code and never trusts the summary line, and why an explicit
# invalid-argument marker is a signal in its own right.
# ============================================================================
set -uo pipefail

# ----------------------------------------------------------------------------
# fatal <reason> — the ONLY supported way for this script to die early. (AB#352)
#
# Every early `exit` used to leave stdout verdict-free, which turns the grep
# AGENTS.md mandates in bold —
#
#     tools/run-tests.sh --typo | grep TWIG-VERDICT
#
# — into EMPTY output. Empty contains no "FAILED", so a caller asking "did
# anything fail?" the documented way sees nothing wrong: the same
# looks-green-while-saying-nothing class the whole script exists to abolish,
# reintroduced by the script itself.
#
# So a usage error is reported in the SAME verdict vocabulary as a test failure
# —`TWIG-VERDICT OVERALL: FAILED (<reason>)` — rather than in a second, invented
# one. Two properties are load-bearing:
#
#   * it goes to STDOUT, because the documented grep only sees stdout;
#   * the reason ends in "nothing ran", so a reader does not go hunting for a
#     broken test that does not exist.
#
# The human-facing diagnostic still goes to stderr, unchanged.
#
# NOTE: `--help` deliberately does NOT route through here. It exits 0 having
# done exactly what was asked, so there is no failure to report; emitting a
# FAILED verdict for a successful help request would be a false RED, which
# corrodes the verdict's meaning just as surely as a false green.
# ----------------------------------------------------------------------------
fatal() {
  echo "run-tests: $1" >&2
  echo "TWIG-VERDICT OVERALL: FAILED ($1 — nothing ran)"
  exit 2
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || fatal "cannot cd to repo root '$REPO_ROOT'"

LOG_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/test-logs}"
mkdir -p "$LOG_DIR" || fatal "cannot create log directory '$LOG_DIR'"

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

PRE_PUSH=0
SELFTEST=0
ARGS=()
for arg in "$@"; do
  case "$arg" in
    --pre-push) PRE_PUSH=1 ;;
    --selftest) SELFTEST=1 ;;
    -h|--help)
      # Sentinel-delimited, not hardcoded line numbers: the header comment moves
      # every time this file is edited, and a `sed -n 'N,Mp'` help text that
      # silently garbles itself is its own small false green.
      sed -n '/^# USAGE$/,/^# EXIT CODE/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    -*)
      fatal "unknown option '$arg'"
      ;;
    # An array, not a space-joined string: an unquoted re-split would also
    # GLOB-expand, so `run-tests.sh 'C*'` would match paths in the repo root
    # before the suite lookup ever saw it.
    *) ARGS+=("$arg") ;;
  esac
done
set -- ${ARGS[@]+"${ARGS[@]}"}
SUITES="${*:-$ALL_SUITES}"

# Validate every suite name BEFORE running any of them. (AB#352)
#
# 🔴 This check used to live inside the run loop, which made `run-tests.sh Cli
# Domian` run the Cli suite for ~70 s and only THEN die on the typo — throwing
# away Cli's verdict line, and making any "nothing ran" wording a lie. Hoisting
# it here restores the property the message claims: a usage error costs nothing
# and runs nothing.
for suite in $SUITES; do
  [ -n "$(suite_project "$suite")" ] || fatal "unknown suite '$suite' (known: $ALL_SUITES)"
done

OVERALL=0
VERDICT_LINES=""

# ----------------------------------------------------------------------------
# reconcile <log> <exit_code> [strict]
#
# The single reconciliation rule, shared by the per-suite runs and by the
# solution-wide pre-push run so the two verdicts cannot drift apart. Sets the
# globals `verdict` and `reason`.
#
# 🔴 The exit code is checked FIRST and is decisive. Every other signal here is
# a defence in depth: the failure modes this script exists to catch (an aborted
# session, a vacuous filter, a missing assembly under --no-build) all print a
# clean-looking `Passed! - Failed: 0, ...` summary line, and the missing-assembly
# case additionally emits its only complaint near the TOP of the log in a line
# containing neither "error" nor "fail". Never derive a pass from the summary.
#
# `strict` (optional, "1") adds one guard used only by the wide run: a run that
# produced NO summary line at all is a failure rather than a `PASSED (0 tests)`.
# It is opt-in so the four-suite path's output stays byte-identical.
# ----------------------------------------------------------------------------
reconcile() {
  local log="$1" exit_code="$2" strict="${3:-0}"
  local aborted=0 no_tests=0 bad_arg=0 passed_count assemblies

  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$log" && aborted=1

  # A filtered-to-empty run exits 0 while running nothing.
  grep -q 'No test matches the given testcase filter' "$log" && no_tests=1

  # The missing-assembly trap: `dotnet test --no-build` with a stale or absent
  # build output tests the assemblies that ARE on disk and greens out, naming
  # the missing one only here. Measured: neither "error" nor "fail" appears, so
  # the documented grep recipe returns only the false-green `Passed!` lines.
  #
  # Anchored to vstest's actual shape — an argument that is a path to a .dll —
  # so a test printing the words "argument ... is invalid" in its own output
  # cannot turn a genuine pass into a FAILED. This guard runs on the four-suite
  # path too, where such a collision would be a regression rather than a catch.
  #
  # ⚠️ vstest localizes this message, so under a non-English
  # DOTNET_CLI_UI_LANGUAGE the marker vanishes and only the exit code catches
  # the trap. The exit code is the primary signal precisely because of that;
  # this marker is defence in depth, not a replacement for it.
  grep -qE 'The argument .*\.dll is invalid\.' "$log" && bad_arg=1

  # One `dotnet test` invocation over the whole solution prints ONE summary line
  # PER ASSEMBLY, so `tail -1` would report the last assembly's count as if it
  # were the run's. Sum them, and count them. A single-project invocation prints
  # exactly one line, so this is identical to `tail -1` on the four-suite path.
  assemblies="$(grep -cE 'Passed: +[0-9]+' "$log")"
  passed_count="$(grep -oE 'Passed: +[0-9]+' "$log" | grep -oE '[0-9]+' | awk '{s+=$1} END {print s+0}')"
  passed_count="${passed_count:-0}"

  if [ "$exit_code" -ne 0 ]; then
    verdict="FAILED"
    reason="process exit code $exit_code"
  elif [ "$aborted" -eq 1 ]; then
    # Defensive: an abort should always exit non-zero, but the whole point of this
    # script is not to trust one signal.
    verdict="FAILED"
    reason="run aborted (exit code was 0 — do not trust it)"
  elif [ "$bad_arg" -eq 1 ]; then
    verdict="FAILED"
    reason="vstest rejected an assembly argument — the run was narrower than it looks"
  elif [ "$no_tests" -eq 1 ]; then
    verdict="FAILED"
    reason="filter matched no tests — a vacuous run is not a pass"
  elif [ "$strict" = "1" ] && [ "$assemblies" -eq 0 ]; then
    verdict="FAILED"
    reason="no test summary in the log — a count-free run is not a pass"
  else
    verdict="PASSED"
    reason="$passed_count tests"
    # Only the wide run is multi-assembly; saying so makes a silently-narrowed
    # run visible in the verdict line itself rather than only in the log.
    if [ "$assemblies" -gt 1 ]; then
      reason="$reason across $assemblies assemblies"
    fi
  fi

  # Return 0 unconditionally. The verdict is carried in `$verdict`, never in this
  # function's exit status — a trailing `[ ... ] && ...` above would otherwise
  # leak a 1 on the PASSED path, and under `pipefail` a caller that ever tested
  # `reconcile ... || ...` would read a pass as a failure.
  return 0
}

# ----------------------------------------------------------------------------
# --selftest: prove the guards can FAIL and PASS, without paying for a build.
#
# Ten arms in two groups. Arms 1-6 drive reconcile() in-process over synthetic
# logs; arms 7-10 (AB#352) re-invoke this script as a subprocess to cover the
# usage-error exits, which are a control-flow property no in-process call can
# observe.
#
# Both groups carry negative AND positive arms, deliberately. Asserting only
# that a guard rejects bad input cannot distinguish a working guard from one
# that always fails.
# ----------------------------------------------------------------------------
selftest() {
  local tmp rc=0
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN

  # Arm 1 — the missing-assembly trap. Exactly the shape AGENTS.md measured:
  # the complaint near the top, clean green lines at the tail, and only the
  # exit code dissenting.
  cat >"$tmp/missing.log" <<'EOF'
The argument /repo/tests/Twig.Tui.Tests/bin/Debug/net11.0/Twig.Tui.Tests.dll is invalid. Please use the /help option to check the list of valid arguments.
Passed!  - Failed: 0, Passed: 3193, Skipped: 0, Total: 3193, Duration: 71 s
Passed!  - Failed: 0, Passed: 1487, Skipped: 0, Total: 1487, Duration: 9 s
EOF
  reconcile "$tmp/missing.log" 1
  if [ "$verdict" = "FAILED" ]; then
    echo "selftest 1/10 PASS: missing assembly under --no-build reconciles as FAILED ($reason)"
  else
    echo "selftest 1/10 FAIL: missing assembly reconciled as $verdict — the trap is not caught" >&2
    rc=1
  fi

  # Arm 1b — the same log with a ZERO exit code. The exit code is the only
  # signal AGENTS.md says catches this, so if it is ever lost the marker must
  # still hold the line.
  reconcile "$tmp/missing.log" 0
  if [ "$verdict" = "FAILED" ]; then
    echo "selftest 2/10 PASS: invalid-argument marker holds even at exit 0 ($reason)"
  else
    echo "selftest 2/10 FAIL: invalid-argument marker not independently checked" >&2
    rc=1
  fi

  # Arm 2 — the aborted-session false green. Exits non-zero AND is marked.
  cat >"$tmp/aborted.log" <<'EOF'
Aborting test run: test run timeout of 300000 milliseconds exceeded.
Passed!  - Failed: 0, Passed: 1092, Skipped: 0, Total: 1092, Duration: 7 s
Test Run Aborted.
EOF
  reconcile "$tmp/aborted.log" 0
  if [ "$verdict" = "FAILED" ]; then
    echo "selftest 3/10 PASS: aborted run reconciles as FAILED ($reason)"
  else
    echo "selftest 3/10 FAIL: aborted run reconciled as $verdict" >&2
    rc=1
  fi

  # Arm 3 — the positive arm. A genuine clean multi-assembly run must reconcile
  # PASSED, with every assembly's count SUMMED. `tail -1` would report 67 here,
  # silently discarding five sixths of the run.
  cat >"$tmp/clean.log" <<'EOF'
Passed!  - Failed: 0, Passed: 81, Skipped: 0, Total: 81, Duration: 2 s - Twig.RenderTree.Tests.dll
Passed!  - Failed: 0, Passed: 67, Skipped: 0, Total: 67, Duration: 2 s - Twig.Tui.Tests.dll
EOF
  reconcile "$tmp/clean.log" 0 1
  if [ "$verdict" = "PASSED" ] && [ "$reason" = "148 tests across 2 assemblies" ]; then
    echo "selftest 4/10 PASS: multi-assembly counts are summed, not last-wins ($reason)"
  else
    echo "selftest 4/10 FAIL: clean run reconciled as $verdict ($reason); expected 148 across 2" >&2
    rc=1
  fi

  # Arm 3b — the single-assembly path must be unchanged: one summary line, no
  # "across N assemblies" suffix. This is what keeps bare run-tests.sh output
  # byte-identical to before this flag existed.
  cat >"$tmp/single.log" <<'EOF'
Passed!  - Failed: 0, Passed: 3198, Skipped: 0, Total: 3198, Duration: 1 m 12 s
EOF
  reconcile "$tmp/single.log" 0
  if [ "$verdict" = "PASSED" ] && [ "$reason" = "3198 tests" ]; then
    echo "selftest 5/10 PASS: single-assembly verdict text unchanged ($reason)"
  else
    echo "selftest 5/10 FAIL: single-assembly reason is '$reason', expected '3198 tests'" >&2
    rc=1
  fi

  # Arm 4 — a run with no summary line at all. Under `strict` this is a failure,
  # not the count-free `PASSED (0 tests)` false green that the CR-overwrite bug
  # once produced.
  : >"$tmp/empty.log"
  reconcile "$tmp/empty.log" 0 1
  if [ "$verdict" = "FAILED" ]; then
    echo "selftest 6/10 PASS: count-free wide run reconciles as FAILED ($reason)"
  else
    echo "selftest 6/10 FAIL: count-free wide run reconciled as $verdict" >&2
    rc=1
  fi

  # ------------------------------------------------------------------------
  # Arms 7-9 — the usage-error paths. (AB#352)
  #
  # These are the only arms that re-invoke THIS SCRIPT as a subprocess, and
  # they have to: the defect was never in reconcile(), it was in control flow
  # that exited before any verdict was printed. An in-process check of a helper
  # cannot observe "the process died with verdict-free stdout", so testing the
  # unit here would reproduce the original mistake one level down.
  #
  # 🔴 Each arm greps STDOUT ONLY (2>/dev/null), reproducing the documented
  # recipe exactly. The old code did print a diagnostic — on stderr, where the
  # documented grep never sees it. An arm that captured 2>&1 would pass against
  # the unfixed script and prove nothing.
  #
  # The exit code is asserted too, but it is NOT the load-bearing assertion:
  # the unfixed script already exited 2. Only the stdout verdict is new.
  #
  # 🔴 The `! grep '──>'` assertion is load-bearing for arm 9 and was ADDED
  # after mutation testing. Without it, moving the suite-name validation back
  # INSIDE the run loop — which makes `Cli Domian` run Cli for ~70 s before
  # dying, discarding Cli's verdict and making "nothing ran" a lie — left the
  # arm GREEN. `──>` is the per-suite banner, so its absence is direct evidence
  # that no suite was started, rather than an inference from the wording.
  # ------------------------------------------------------------------------
  local self="${BASH_SOURCE[0]}" out code n=7
  local desc argv expect_frag
  local -a argv_words
  while IFS='|' read -r argv expect_frag desc; do
    [ -n "$argv" ] || continue
    # Split deliberately: arm 9 passes TWO arguments. An explicit `read -ra`
    # into an array says so, rather than relying on an unquoted expansion that
    # would also glob-expand against the repo root.
    read -ra argv_words <<<"$argv"
    out="$(TWIG_TEST_LOG_DIR="$tmp/logs" bash "$self" "${argv_words[@]}" 2>/dev/null)"
    code=$?
    if [ "$code" -ne 0 ] \
       && printf '%s' "$out" | grep -q 'TWIG-VERDICT OVERALL: FAILED' \
       && printf '%s' "$out" | grep -qF "$expect_frag" \
       && printf '%s' "$out" | grep -q 'nothing ran' \
       && ! printf '%s' "$out" | grep -q '──>'; then
      echo "selftest $n/10 PASS: $desc"
    else
      echo "selftest $n/10 FAIL: $desc — exit $code, stdout: ${out:-<empty>}" >&2
      rc=1
    fi
    n=$((n + 1))
  done <<'EOF'
--typo|unknown option '--typo'|unknown option emits a FAILED verdict on stdout
Domian|unknown suite 'Domian'|unknown suite emits a FAILED verdict on stdout
Cli Domian|unknown suite 'Domian'|a typo beside a valid suite is caught before anything runs
EOF

  # Arm 10 — the guard must not be always-FAILED. `--help` succeeds, so it must
  # print NO failure verdict at all. Without this arm, a `fatal` wired into the
  # help path (or into the top of main) would satisfy arms 7-9 while making the
  # script useless, and nothing would notice.
  out="$(bash "$self" --help 2>/dev/null)"
  code=$?
  if [ "$code" -eq 0 ] \
     && ! printf '%s' "$out" | grep -q 'TWIG-VERDICT' \
     && printf '%s' "$out" | grep -q 'pre-push'; then
    echo "selftest 10/10 PASS: --help succeeds without emitting a failure verdict"
  else
    echo "selftest 10/10 FAIL: --help exit $code / verdict-bearing output — the usage guard is always-FAILED" >&2
    rc=1
  fi

  if [ "$rc" -eq 0 ]; then
    echo "TWIG-VERDICT SELFTEST: PASSED"
  else
    echo "TWIG-VERDICT SELFTEST: FAILED"
  fi
  return "$rc"
}

if [ "$SELFTEST" -eq 1 ]; then
  selftest
  exit $?
fi

for suite in $SUITES; do
  project="$(suite_project "$suite")"

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
  reconcile "$log" "$exit_code"

  [ "$verdict" = "FAILED" ] && OVERALL=1

  # NOTE: the verdict deliberately does NOT contain the string "Passed!". Anyone
  # grepping this output for a pass must match TWIG-VERDICT, which is computed
  # from the exit code and abort markers rather than from vstest's summary prose.
  line="TWIG-VERDICT $suite: $verdict ($reason) [log: $log]"
  VERDICT_LINES="$VERDICT_LINES$line"$'\n'
  echo "$line"

  if [ "$verdict" = "FAILED" ]; then
    grep -E '\[FAIL\]|error CS|Test Run Aborted|Aborting test run|is invalid\.' "$log" | head -20
  fi
done

if [ "$PRE_PUSH" -eq 1 ]; then
  # --------------------------------------------------------------------------
  # CI's own three commands, in CI's own order (.github/workflows/ci.yml).
  #
  # 🔴 Chained with && on purpose. If the build fails and `dotnet test
  # --no-build` runs anyway, you get a green-looking run of whatever assemblies
  # happen to still be on disk — the exact trap reconcile() guards. Chaining
  # means a build failure never reaches the test step at all, and the
  # invalid-argument marker is the second line of defence for the case where a
  # stale output directory survives a successful build.
  #
  # 🔴 Serial with respect to the four suites above. Two concurrent `dotnet
  # test` processes collide over shared build output and produce a bogus
  # SQLitePCL DllNotFoundException. That is why this runs AFTER the loop rather
  # than beside it, despite the wide run being the cheaper of the two.
  # --------------------------------------------------------------------------
  wide_log="$LOG_DIR/SolutionWide.log"
  echo
  echo "──> SolutionWide (CI's commands: restore → build → test)"

  {
    dotnet restore \
      && dotnet build --no-restore \
      && dotnet test --no-build --settings test.runsettings
  } 2>&1 | tr '\r' '\n' > "$wide_log"
  wide_exit=${PIPESTATUS[0]}

  reconcile "$wide_log" "$wide_exit" 1

  # A compile failure never reaches vstest, so it leaves no abort marker and no
  # summary line — only the exit code and `error CS`. Say so plainly rather than
  # reporting a bare exit code.
  if [ "$verdict" = "FAILED" ] && grep -q 'error CS' "$wide_log"; then
    reason="solution-wide build failed (error CS) — the test step never ran"
  fi

  [ "$verdict" = "FAILED" ] && OVERALL=1

  line="TWIG-VERDICT SolutionWide: $verdict ($reason) [log: $wide_log]"
  VERDICT_LINES="$VERDICT_LINES$line"$'\n'
  echo "$line"

  if [ "$verdict" = "FAILED" ]; then
    grep -E '\[FAIL\]|error CS|Test Run Aborted|Aborting test run|is invalid\.' "$wide_log" | head -20
  fi
fi

echo
echo "════════════════════════════════════════════"
printf '%s' "$VERDICT_LINES"
if [ "$OVERALL" -eq 0 ]; then
  echo "TWIG-VERDICT OVERALL: PASSED"
else
  echo "TWIG-VERDICT OVERALL: FAILED"
fi
exit "$OVERALL"
