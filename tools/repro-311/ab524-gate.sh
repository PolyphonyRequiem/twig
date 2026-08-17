#!/usr/bin/env bash
# AB#524: repo gate. NOT run concurrently with the hunt -- two dotnet test
# processes collide over shared build output (bogus SQLitePCL DllNotFoundException),
# and the hunt is itself a dotnet test loop. Waits for the hunt to finish first.
set -u
export HOME=/home/polyphonyrequiem
unset DOTNET_ROOT
export PATH=/home/polyphonyrequiem/.dotnet-p5:$PATH
cd /home/polyphonyrequiem/repos/twig-524

while pgrep -f '[a]b524-split-wide' >/dev/null; do
  echo "waiting for hunt to finish... $(date +%H:%M:%S)"
  sleep 60
done
echo "hunt done; stopping stressors before the gate so it is not measured under load"
pkill -f '[c]pu-load.sh'   || true
pkill -f '[b]uild-load.sh' || true
sleep 5

echo "=== tools/run-tests.sh --pre-push ==="
LOG=/tmp/ab524-gate.log
tools/run-tests.sh --pre-push > "$LOG" 2>&1
rc=$?
tail -30 "$LOG"
echo "=== EVERY verdict line (never grep for Passed!) ==="
grep TWIG-VERDICT "$LOG" || echo "NO VERDICT LINE -- absence of a verdict IS a FAILED"
echo "=== gate exit code: $rc ==="
