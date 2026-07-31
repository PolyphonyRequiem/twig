#!/usr/bin/env bash
# ============================================================================
# dispatch-analyze.sh — turn a dispatch-watch.sh capture into a verdict about
# WHICH SIDE of the runner/host boundary stalled (twig#311, ADO #42).
#
#   tools/repro-311/dispatch-analyze.sh artifacts/test-logs/dispatch-3
#
# HOW TO READ IT
#
# 🔴 Do NOT compare the last log line on each side. The session timeout tears
# both processes down within ~20 ms of each other, so the final lines always
# look synchronised. That trap already wasted effort on the --diag probe.
#
# The decisive evidence is the socket pair between vstest.console (runner) and
# testhost (host):
#
#   runner Send-Q > 0, or host Recv-Q > 0
#       Bytes are on the wire and the host is not consuming them.
#       -> HOST-side stall. The host was asked for work and never answered.
#
#   both queues 0, host blocked in a receive, runner not in a send
#       Nothing was ever sent. -> RUNNER-side stall: the runner stopped
#       requesting work while the host sat waiting for a request.
#
#   both queues 0 and BOTH sides blocked in a receive
#       A lost-wakeup / missed-message deadlock: each is waiting for the other.
#       Distinguish from the previous case with bytes_sent/bytes_received —
#       compare the runner's bytes_sent against the host's bytes_received.
#
# The managed stacks disambiguate: look for the host's xunit/CrossPlatEngine
# dispatch thread and the runner's message-loop thread.
# ============================================================================
set -uo pipefail

DIR="${1:-}"
[ -d "$DIR" ] || { echo "usage: $0 <capture-dir>" >&2; exit 2; }
[ -f "$DIR/trip.txt" ] || { echo "no trip.txt in $DIR — the watcher never tripped." >&2; exit 2; }

echo "════════════════════════════════════════════"
cat "$DIR/trip.txt"
echo

echo "──── dispatch trace shape ────"
if [ -s "$DIR/trace.tsv" ]; then
  awk -F'\t' '
    $2=="START"{open[$3]=$1; order[++n]=$3; if(!f)f=$1; l=$1}
    $2=="END"{delete open[$3]; l=$1}
    END{
      printf "  boundaries: %d tests\n", n
      printf "  first START: %s\n  last line  : %s\n", f, l
      c=0; for(t in open){printf "  IN FLIGHT: %s\n", t; c++}
      if(c==0) printf "  IN FLIGHT: none — every START had an END (matches all 6 known captures).\n"
    }' "$DIR/trace.tsv"
else
  echo "  trace empty — instrumentation did not run."
fi
echo

echo "──── socket queues (the decisive evidence) ────"
for f in "$DIR"/*.snap*.sock; do
  [ -f "$f" ] || continue
  side="$(basename "$f" | cut -d. -f1)"
  # Send-Q and Recv-Q are ss's columns 2 and 3 on the ESTAB line.
  awk -v s="$side" '/ESTAB/{printf "  %-16s Recv-Q=%-8s Send-Q=%-8s %s -> %s\n", s, $2, $3, $4, $5}' "$f"
  grep -o 'bytes_sent:[0-9]*\|bytes_received:[0-9]*\|lastsnd:[0-9]*\|lastrcv:[0-9]*' "$f" \
    | tr '\n' ' ' | sed "s/^/      $side: /;s/$/\n/"
done
echo
echo "  Reminder: lastsnd/lastrcv are MILLISECONDS since that side last sent/received."
echo "  Expect roughly the dispatch gap (>=45s, i.e. 45000+), NOT hundreds of thousands:"
echo "  the watcher fires at the 45s gap, not at the 300s abort, so these counters are"
echo "  young BY CONSTRUCTION. A capture with lastsnd/lastrcv in the tens of thousands"
echo "  is normal and must NOT be rejected on that basis (verified: ADO #43 capture,"
echo "  a true 311 abort, showed 44698-88290 ms across its three snapshots)."
echo
echo "  The load-bearing check is not the magnitude but that the counters are FROZEN"
echo "  across snapshots while both queues stay 0 — and that runner bytes_sent =="
echo "  host bytes_received (and vice versa), which proves nothing is stranded on the"
echo "  wire and the stall is a mutual lost wakeup rather than one side's fault."
echo

echo "──── blocked threads ────"
for f in "$DIR"/*.snap*.proc; do
  [ -f "$f" ] || continue
  side="$(basename "$f" | cut -d. -f1)"
  echo "  [$side] $(grep -m1 '^State:' "$f")  $(grep -m1 '^Threads:' "$f")"
  # D = uninterruptible sleep: a genuinely stuck kernel wait, always notable.
  awk -F'\t' '$2=="D"{printf "    UNINTERRUPTIBLE tid=%s wchan=%s comm=%s\n",$1,$3,$4}' "$f"
done
echo

echo "──── dispatch-relevant managed frames ────"
for f in "$DIR"/*.snap*.stack; do
  [ -f "$f" ] || continue
  side="$(basename "$f" | cut -d. -f1)"
  echo "  [$side] $(grep -c '^Thread' "$f") threads; $(grep -m1 'dotnet-stack-exit' "$f")"
  grep -E 'CommunicationManager|SocketCommunication|ReceiveMessage|SendMessage|RunTestsWithSources|DefaultEngineInvoker|MessageLoop|WaitForRequest' "$f" \
    | sed 's/^/    /' | sort -u | head -20
done

echo
echo "════════════════════════════════════════════"
echo "TWIG-DISPATCH ANALYSIS COMPLETE — apply the decision rule in this file's header."
