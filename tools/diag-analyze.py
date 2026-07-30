#!/usr/bin/env python3
"""
diag-analyze.py — line up a #311 abort across all three instrumentation layers.

WHY (twig#311, task #41)

The boundary trace proved the stall is not inside a test body: on the captured
repro every START had an END, and the host then sat alive with nothing executing
for ~289 s. That leaves one question, and it is the one that picks the fix:

    at the moment dispatch stops, is the RUNNER waiting on the HOST,
    or is the HOST waiting on the RUNNER?

`--diag` answers it, because vstest writes TWO logs -- the runner
(vstest.console.dll) and the test host (testhost.dll) -- both timestamped. This
script finds the LAST activity on each side and reports the silence gap, so the
answer is read off a table instead of eyeballed from a 500k-line log.

Reading the verdict:

  * runner spoke last, host silent  -> host stopped responding (host-side stall)
  * host spoke last, runner silent  -> runner stopped dispatching (runner-side stall)
  * both silent from the same instant -> the seam itself (socket/scheduling)

USAGE
    tools/diag-analyze.py artifacts/diag-hunt/diag-3.log
    tools/diag-analyze.py artifacts/diag-hunt/diag-3.log --trace artifacts/diag-hunt/trace-3.tsv
"""
from __future__ import annotations

import argparse
import datetime as dt
import glob
import os
import re
import sys

# TpTrace lines look like:
#   TpTrace Information: 0 : <pid>, <tid>, 2026/07/30, 11:54:53.359, <ticks>, vstest.console.dll, <message>
LINE = re.compile(
    r"TpTrace\s+\w+:\s*\d+\s*:\s*(?P<pid>\d+),\s*(?P<tid>\d+),\s*"
    r"(?P<date>\d{4}/\d{2}/\d{2}),\s*(?P<time>\d{2}:\d{2}:\d{2}\.\d+),\s*"
    r"(?P<ticks>\d+),\s*(?P<src>[\w.]+),\s*(?P<msg>.*)$"
)


def parse(path):
    """Return (timestamp, source, message) for every parseable trace line."""
    out = []
    with open(path, errors="ignore") as fh:
        for raw in fh:
            m = LINE.search(raw)
            if not m:
                continue
            try:
                ts = dt.datetime.strptime(
                    f"{m.group('date')} {m.group('time')}", "%Y/%m/%d %H:%M:%S.%f"
                )
            except ValueError:
                continue
            out.append((ts, m.group("src"), m.group("msg").strip()))
    return out


def summarise(label, rows, tail=6):
    if not rows:
        print(f"  {label:<18} (no parseable lines)")
        return None
    first, last = rows[0][0], rows[-1][0]
    print(f"  {label:<18} lines={len(rows):<7} first={first:%H:%M:%S.%f} last={last:%H:%M:%S.%f}")
    return last


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("diag", help="the --diag runner log (sidecar host log is found automatically)")
    ap.add_argument("--trace", help="boundary trace TSV from TWIG_TEST_TRACE")
    ap.add_argument("--tail", type=int, default=8, help="how many trailing messages to show per side")
    a = ap.parse_args()

    if not os.path.exists(a.diag):
        print(f"error: {a.diag} not found", file=sys.stderr)
        return 2

    runner = parse(a.diag)
    hosts = sorted(glob.glob(a.diag + ".host.*.log")) or sorted(
        glob.glob(os.path.splitext(a.diag)[0] + ".host.*.log")
    )
    host = []
    for h in hosts:
        host.extend(parse(h))
    host.sort(key=lambda r: r[0])

    print("═" * 72)
    print("TWIG-DIAG layer summary")
    print("═" * 72)
    r_last = summarise("runner", runner)
    h_last = summarise("host", host)

    t_last = None
    if a.trace and os.path.exists(a.trace):
        rows = [l.split("\t") for l in open(a.trace).read().splitlines() if l]
        if rows:
            starts = sum(1 for r in rows if r[1] == "START")
            ends = sum(1 for r in rows if r[1] == "END")
            t_first = dt.datetime.fromisoformat(rows[0][0]).replace(tzinfo=None)
            t_last = dt.datetime.fromisoformat(rows[-1][0]).replace(tzinfo=None)
            print(
                f"  {'boundary trace':<18} tests={starts:<7} "
                f"first={t_first:%H:%M:%S.%f} last={t_last:%H:%M:%S.%f}"
            )
            print(f"  {'':18} STARTs={starts} ENDs={ends} "
                  f"=> in-flight at abort: {'NONE' if starts == ends else starts - ends}")

    # 🔴 The last line on each side is written during SHUTDOWN, which the timeout
    # triggers on both sides at once -- so comparing final lines always looks
    # synchronised and proves nothing. The informative moment is the START of the
    # silence: the biggest gap between consecutive messages on each side.
    def longest_gap(rows):
        if len(rows) < 2:
            return None
        best = (dt.timedelta(0), None, None)
        for i in range(1, len(rows)):
            d = rows[i][0] - rows[i - 1][0]
            if d > best[0]:
                best = (d, rows[i - 1], rows[i])
            
        return best

    print()
    print("═" * 72)
    print("TWIG-DIAG verdict — where the SILENCE starts")
    print("═" * 72)

    rg, hg = longest_gap(runner), longest_gap(host)
    for label, g in (("runner", rg), ("host", hg)):
        if g and g[1]:
            print(f"  {label:<7} longest silence: {g[0].total_seconds():7.1f}s "
                  f"starting {g[1][0]:%H:%M:%S.%f}")
            print(f"  {'':7} last words before it: {g[1][2][:110]}")
    print()

    if rg and hg and rg[1] and hg[1]:
        r_start, h_start = rg[1][0], hg[1][0]
        skew = (r_start - h_start).total_seconds()
        print(f"  runner fell silent at : {r_start:%H:%M:%S.%f}")
        print(f"  host   fell silent at : {h_start:%H:%M:%S.%f}")
        print(f"  runner minus host     : {skew:+.3f}s")
        print()
        if abs(skew) < 1.0:
            print("  => BOTH sides went quiet together. Points at the seam itself")
            print("     (socket / process scheduling), not one side hanging.")
        elif skew > 0:
            print("  => The HOST fell silent FIRST; the runner kept talking after.")
            print("     Consistent with a host-side stall: the runner is waiting on a")
            print("     host that stopped responding.")
        else:
            print("  => The RUNNER fell silent FIRST; the host kept talking after.")
            print("     Consistent with a runner-side stall: the host is idle because")
            print("     nothing is dispatching more work to it.")
        print()
        print("  NOTE: this is where to start reading, not a conclusion. Open both")
        print("  logs at those timestamps and confirm what the last exchange was.")

    if t_last and h_last:
        print()
        print(f"  last test END        : {t_last:%H:%M:%S.%f}")
        print(f"  host quiet AFTER last test for : {(h_last - t_last).total_seconds():.1f}s")
        print("     (large value = the host stayed alive and chatty with no tests running)")

    for label, rows in (("RUNNER", runner), ("HOST", host)):
        print()
        print(f"── last {a.tail} {label} messages ──")
        for ts, _src, msg in rows[-a.tail:]:
            print(f"  {ts:%H:%M:%S.%f}  {msg[:150]}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
