#!/usr/bin/env bash
# AB#524 splitting experiment, CORRECTED SHAPE.
#
# v1 of this script ran Twig.Tui.Tests STANDALONE and got 14/14 clean at 3-4s.
# That is not the shape the AB#390 capture came from and cannot reproduce it:
# the capture is CI's WIDE invocation (6 assemblies in parallel processes,
# 101-118s per attempt under load). Twig.Tui.Tests alone is 85/85 in ~0.5-4s,
# and its module cctor is then raced by far fewer concurrent collections.
#
# This runs the WIDE invocation with the session timeout REMOVED, which is the
# actual splitting experiment:
#     completes late  => STARVATION (the reflection walk is merely slow)
#     never completes => DEADLOCK
#
# The removed timeout is a DIAGNOSTIC runsettings, never a repo change.
set -uo pipefail
cd "$(dirname "$0")/../.."
REPO="$PWD"

unset DOTNET_ROOT
export PATH=/home/polyphonyrequiem/.dotnet-p5:$PATH

ATTEMPTS="${1:-14}"
HARD_CAP="${TWIG_524_CAP:-1500}"   # seconds before we call it "never completes"
OUT="$REPO/artifacts/ab524-wide"
mkdir -p "$OUT"
LEDGER="$OUT/ledger.tsv"
[ -f "$LEDGER" ] || printf 'attempt\twall_s\texit\tloadavg\tstarted\tended\tinflight\tverdict\n' > "$LEDGER"

echo "=== load validity (preview.5-correct; csc.dll is STALE) ==="
printf '  VBCSCompiler=%s build-load=%s cpu-load=%s loadavg=%s cores=%s\n' \
  "$(ps -eo args --no-headers | grep -c '[V]BCSCompiler')" \
  "$(ps -eo args --no-headers | grep -c '[b]uild-load.sh')" \
  "$(ps -eo args --no-headers | grep -c '[c]pu-load.sh')" \
  "$(cut -d' ' -f1 /proc/loadavg)" "$(nproc)"
echo

for i in $(seq 1 "$ATTEMPTS"); do
  TRACE="$OUT/trace-$i"; rm -rf "$TRACE"; mkdir -p "$TRACE"
  LOG="$OUT/attempt-$i.log"
  load_at_start=$(cut -d' ' -f1 /proc/loadavg)

  start=$(date +%s)
  TWIG_TEST_TRACE="$TRACE" timeout "$HARD_CAP" \
    dotnet test --no-build --nologo \
      --settings tools/repro-311/ab524-no-timeout.runsettings \
      > "$LOG" 2>&1
  rc=$?
  wall=$(( $(date +%s) - start ))

  started=0; ended=0
  for f in "$TRACE"/*.tsv; do
    [ -e "$f" ] || continue
    started=$(( started + $(grep -c $'\tSTART\t' "$f" || true) ))
    ended=$((   ended   + $(grep -c $'\tEND\t'   "$f" || true) ))
  done
  inflight=$(( started - ended ))

  # Verdicts kept DISTINCT: an apparatus fault must never bank as "clean".
  if [ "$rc" -eq 124 ]; then
    verdict="NEVER_COMPLETED_at_${HARD_CAP}s__DEADLOCK_ARM"
  elif [ "$started" -eq 0 ]; then
    verdict="APPARATUS_FAULT_empty_trace"
  elif [ "$inflight" -ne 0 ]; then
    verdict="INFLIGHT_${inflight}__STALL_COMPLETED_LATE"
  elif [ "$rc" -eq 0 ]; then
    verdict="CLEAN"
  else
    verdict="FAILED_exit_${rc}"
  fi

  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$i" "$wall" "$rc" "$load_at_start" "$started" "$ended" "$inflight" "$verdict" >> "$LEDGER"
  printf 'attempt %2d: wall=%4ds exit=%-4s load=%-6s start/end=%s/%s inflight=%s  %s\n' \
    "$i" "$wall" "$rc" "$load_at_start" "$started" "$ended" "$inflight" "$verdict"

  case "$verdict" in
    NEVER_COMPLETED*|INFLIGHT*)
      echo "  >>> THIS IS THE DECIDING ARM. log=$LOG trace=$TRACE"
      echo "  >>> wall=${wall}s against the 300s timeout the real runs die at."
      ;;
  esac
done

echo
echo "=== ledger ==="
cat "$LEDGER"
echo
echo "READ: wall_s > 300 with a completion  => STARVATION (would have finished, timeout cut it)"
echo "READ: NEVER_COMPLETED at ${HARD_CAP}s => DEADLOCK"
