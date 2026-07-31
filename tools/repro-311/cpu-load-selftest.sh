#!/bin/bash
# Verifies cpu-load.sh reaps its own spinners on SIGTERM (twig ADO #42).
set -u
cd "$(dirname "$0")"
before=$(pgrep -cf 'SECONDS=0; while')
setsid bash ./cpu-load.sh 120 4 >/dev/null 2>&1 &
lp=$!
sleep 4
during=$(pgrep -cf 'SECONDS=0; while')
kill -TERM -"$(ps -o pgid= -p $lp | tr -d ' ')" 2>/dev/null || kill -TERM "$lp"
sleep 4
after=$(pgrep -cf 'SECONDS=0; while')
echo "spinners before=$before during=$during after=$after"
if [ "$during" -ge $(( before + 4 )) ] && [ "$after" -le "$before" ]; then
  echo "CPULOAD-SELFTEST: PASSED — spinners started and were reaped."
  exit 0
fi
echo "CPULOAD-SELFTEST: FAILED" >&2
exit 1
