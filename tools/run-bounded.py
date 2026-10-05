#!/usr/bin/env python3
"""Run one owned command with an external deadline; reap its process tree on timeout."""
import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--timeout', type=int, default=60, choices=range(1, 601),
                        metavar='SECONDS', help='wall-clock deadline, 1-600 seconds (default: 60)')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command
    if command and command[0] == '--':
        command = command[1:]
    if not command:
        parser.error('provide a command after --')

    # Share the existing cross-platform owned-tree cleanup; do not invent another killer.
    spec = importlib.util.spec_from_file_location('ci_runner', Path(__file__).with_name('run-ci-tests.py'))
    runner = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = runner
    spec.loader.exec_module(runner)
    with subprocess.Popen(command, start_new_session=os.name != 'nt') as process:
        try:
            return process.wait(timeout=args.timeout)
        except subprocess.TimeoutExpired:
            runner.stop_session(process, False)
            print(f'BOUNDED-VERDICT: FAILED (deadline {args.timeout}s; owned process tree stopped)',
                  file=sys.stderr, flush=True)
            return 124
        except KeyboardInterrupt:
            runner.stop_session(process, False)
            print('BOUNDED-VERDICT: INTERRUPTED (owned process tree stopped)', file=sys.stderr, flush=True)
            return 130


if __name__ == '__main__':
    sys.exit(main())
