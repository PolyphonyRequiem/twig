#!/usr/bin/env bash
# ============================================================================
# diag-hunt.sh — capture a #311 abort with BOTH instrumentation layers on.
#
# WHY (twig#311, task #41)
#
# The boundary trace (tools/find-hung-test.sh) proved the stall is NOT inside a
# test body: on the captured repro all 1218 STARTs had a matching END, and the
# host then sat alive with nothing executing for ~289 s until the 300 s session
# timeout killed it.
#
# The boundary trace can only see INSIDE test bodies. `--diag` sees the layer
# above: the runner<->host handshake and the work-dispatch messages. Running both
# at once on the SAME abort is what lets you line up "last test completed" against
# "last message exchanged" and answer the one question that picks the fix:
#
#     at the moment dispatch stops, is the RUNNER waiting on the HOST,
#     or is the HOST waiting on the RUNNER?
#
# USAGE
#     tools/diag-hunt.sh            # one traced+diagnosed run
#     tools/diag-hunt.sh 25         # loop until an abort is captured
#
# Expect roughly a 1-in-11 hit rate under tools/repro-311 load. Start the load
# generators FIRST; this script does not start them for you, because you want to
# control how long they live.
#
# Exit 0 = every attempt passed (no repro). Exit 1 = an abort was captured; the
# artifact paths are printed for analysis.
#
# Diag logs are large and land under artifacts/ (gitignored). Each attempt's logs
# are removed when that attempt passes, so a long loop does not fill the disk.
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

ATTEMPTS="${1:-1}"
OUT_DIR="${TWIG_TEST_LOG_DIR:-$REPO_ROOT/artifacts/diag-hunt}"
mkdir -p "$OUT_DIR"

for i in $(seq 1 "$ATTEMPTS"); do
  trace="$OUT_DIR/trace-$i.tsv"
  diag="$OUT_DIR/diag-$i.log"
  console="$OUT_DIR/console-$i.log"
  rm -f "$trace" "$diag" "$console" "$diag".* 2>/dev/null

  echo "──> attempt $i/$ATTEMPTS"
  start=$(date +%s)

  # --diag writes several sidecar files next to the named one (host, datacollector).
  TWIG_TEST_TRACE="$trace" \
    dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo \
      --filter 'FullyQualifiedName!~BinaryLauncher' \
      --diag "$diag" 2>&1 | tr '\r' '\n' > "$console"
  exit_code=${PIPESTATUS[0]}
  elapsed=$(( $(date +%s) - start ))

  aborted=0
  grep -qE 'Test Run Aborted|Aborting test run|test host process crashed' "$console" && aborted=1

  echo "    exit=$exit_code aborted=$aborted elapsed=${elapsed}s"

  if [ "$exit_code" -eq 0 ] && [ "$aborted" -eq 0 ]; then
    echo "    TWIG-DIAG attempt $i: clean run (no repro) — discarding logs"
    rm -f "$trace" "$console" "$diag" "$diag".* 2>/dev/null
    continue
  fi

  echo
  echo "════════════════════════════════════════════"
  echo "TWIG-DIAG REPRO CAPTURED on attempt $i"
  echo "  console : $console"
  echo "  trace   : $trace"
  echo "  diag    : $diag (plus sidecars: $(ls "$diag".* 2>/dev/null | tr '\n' ' '))"
  echo

  # Boundary-trace side: was anything in flight?
  awk -F'\t' '
    $2 == "START" { open[$3] = $1; order[++n] = $3 }
    $2 == "END"   { delete open[$3] }
    END {
      printf "TWIG-DIAG tests recorded: %d\n", n
      found = 0
      for (i = 1; i <= n; i++) if (order[i] in open) {
        printf "TWIG-DIAG IN-FLIGHT AT ABORT: %s (started %s)\n", order[i], open[order[i]]
        found = 1; delete open[order[i]]
      }
      if (!found) {
        printf "TWIG-DIAG IN-FLIGHT AT ABORT: none — every START had an END.\n"
        printf "TWIG-DIAG last test to complete: %s\n", order[n]
        printf "TWIG-DIAG last test END timestamp: (see tail of trace)\n"
      }
    }
  ' "$trace"

  echo
  echo "── last boundary-trace line (when tests stopped) ──"
  tail -1 "$trace"
  echo
  echo "── tail of runner diag (what the runner did next) ──"
  tail -30 "$diag" 2>/dev/null
  echo
  echo "Analyse with: tools/diag-analyze.sh $i"
  exit 1
done

echo
echo "════════════════════════════════════════════"
echo "TWIG-DIAG VERDICT: $ATTEMPTS/$ATTEMPTS attempts clean — no repro captured."
exit 0
