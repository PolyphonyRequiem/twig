#!/usr/bin/env bash
# ============================================================================
# perturbation-ab.sh — does --diag SUPPRESS the #311 hang?
#
# WHY (twig#311, task #41)
#
# The hang reproduced twice with the boundary trace alone (1-in-11 under load),
# then did NOT reproduce in 70 attempts once `--diag` was added. That is either
# bad luck or a real observer effect: --diag writes ~5 MB per side per run, which
# is not free on a contended box. If the instrument suppresses the bug, every
# future diag hunt is wasted time -- so this question gates the next probe.
#
# THE DESIGN
#
# Alternate diag-ON and diag-OFF attempts within ONE run, rather than running two
# separate campaigns. Machine conditions drift (thermal, page cache, other load),
# and two campaigns hours apart are confounded by that drift. Interleaving means
# both arms see the same conditions, so a hit-rate difference is attributable to
# the variable under test.
#
# Both arms are otherwise IDENTICAL -- same filter, same boundary trace, same
# everything. Only --diag differs.
#
# USAGE
#     tools/repro-311/perturbation-ab.sh 40      # 40 attempts, alternating
#
# Start the load generators FIRST (this script does not, so you control lifetime):
#     tools/repro-311/cpu-load.sh 7200 16 &
#     tools/repro-311/build-load.sh 7200 &
#     tools/repro-311/build-load.sh 7200 &
#
# 🔴 VERIFY THE LOAD IS REAL before trusting a null result: `pgrep -cf csc.dll`
# must be non-zero while this runs. A no-op build loop applies no contention and
# already invalidated one 30-attempt cycle (see AGENTS.md).
#
# READING THE RESULT
#
# This reports hit rates per arm. With the observed ~1-in-11 base rate, ~20
# attempts per arm is enough to notice a strong suppression effect but NOT enough
# to prove absence -- 0/20 vs 2/20 is suggestive, not conclusive. The script says
# so in its own output rather than letting the reader over-read it.
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT" || exit 2

ATTEMPTS="${1:-40}"
OUT_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/perturbation-ab}"
mkdir -p "$OUT_DIR"

on_runs=0;  on_hits=0
off_runs=0; off_hits=0
CAPTURES=""

for i in $(seq 1 "$ATTEMPTS"); do
  # Odd attempts = diag OFF, even = diag ON. Alternating rather than blocked, so
  # any monotonic drift in machine state hits both arms evenly.
  if [ $((i % 2)) -eq 1 ]; then arm="OFF"; else arm="ON"; fi

  trace="$OUT_DIR/trace-$i-$arm.tsv"
  console="$OUT_DIR/console-$i-$arm.log"
  diag="$OUT_DIR/diag-$i.log"
  rm -f "$trace" "$console" "$diag" "$diag".* 2>/dev/null

  printf '──> attempt %d/%d [diag %s] ' "$i" "$ATTEMPTS" "$arm"
  start=$(date +%s)

  if [ "$arm" = "ON" ]; then
    TWIG_TEST_TRACE="$trace" \
      dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo \
        --filter 'FullyQualifiedName!~BinaryLauncher' \
        --diag "$diag" 2>&1 | tr '\r' '\n' > "$console"
  else
    TWIG_TEST_TRACE="$trace" \
      dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo \
        --filter 'FullyQualifiedName!~BinaryLauncher' 2>&1 | tr '\r' '\n' > "$console"
  fi
  exit_code=${PIPESTATUS[0]}
  elapsed=$(( $(date +%s) - start ))

  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$console" && aborted=1
  # A real [FAIL] is NOT this bug (see diag-hunt.sh) -- count it separately so a
  # broken stressor can't masquerade as a hit.
  failures=0
  grep -qE '\[FAIL\]' "$console" && failures=1

  if [ "$arm" = "ON" ]; then on_runs=$((on_runs + 1)); else off_runs=$((off_runs + 1)); fi

  if [ "$failures" -eq 1 ]; then
    echo "REAL TEST FAILURES — not the #311 timeout. Stopping."
    grep -E '\[FAIL\]' "$console" | head -5
    echo "  console: $console"
    exit 2
  fi

  if [ "$aborted" -eq 1 ]; then
    echo "HANG CAPTURED (${elapsed}s)"
    if [ "$arm" = "ON" ]; then on_hits=$((on_hits + 1)); else off_hits=$((off_hits + 1)); fi
    CAPTURES="$CAPTURES  attempt $i [diag $arm]: $console"$'\n'
    # Keep the artifacts for a hit; they are the evidence.
  else
    echo "clean (${elapsed}s)"
    rm -f "$trace" "$console" "$diag" "$diag".* 2>/dev/null
  fi
done

pct() { [ "$2" -eq 0 ] && echo "n/a" || awk "BEGIN{printf \"%.0f%%\", $1*100/$2}"; }

echo
echo "════════════════════════════════════════════"
echo "TWIG-AB RESULT"
echo "════════════════════════════════════════════"
echo "  diag OFF : $off_hits hit(s) / $off_runs runs  ($(pct $off_hits $off_runs))"
echo "  diag ON  : $on_hits hit(s) / $on_runs runs  ($(pct $on_hits $on_runs))"
echo
[ -n "$CAPTURES" ] && { echo "  captures:"; printf '%s' "$CAPTURES"; echo; }

if [ "$off_hits" -eq 0 ] && [ "$on_hits" -eq 0 ]; then
  echo "  VERDICT: no hits in EITHER arm. This says nothing about --diag; it says"
  echo "  the load did not reproduce the bug at all. Check the load was real"
  echo "  (pgrep -cf csc.dll) before drawing any conclusion."
elif [ "$on_hits" -eq 0 ] && [ "$off_hits" -gt 0 ]; then
  echo "  VERDICT: hits ONLY with diag off. Consistent with --diag suppressing the"
  echo "  hang (observer effect). Treat as suggestive, not proven -- at a ~1-in-11"
  echo "  base rate this arm size cannot establish absence. Prefer a lighter probe."
elif [ "$on_hits" -gt 0 ]; then
  echo "  VERDICT: the hang reproduces WITH --diag enabled. No suppression effect;"
  echo "  the earlier 70-attempt null was luck. Analyse the capture:"
  echo "    tools/diag-analyze.py <diag log> --trace <trace>"
fi
echo "════════════════════════════════════════════"

[ $((on_hits + off_hits)) -gt 0 ] && exit 1
exit 0
