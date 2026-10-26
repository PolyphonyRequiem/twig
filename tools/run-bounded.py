#!/usr/bin/env python3
"""Run one owned command with an external deadline; reap its process tree on timeout.

Usage: python tools/run-bounded.py [--timeout SECONDS] -- <command...>
       python tools/run-bounded.py --selftest
"""
import argparse
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import unittest


def load_runner():
    # Share the existing cross-platform owned-tree cleanup; do not invent another killer.
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('ci_runner', Path(__file__).with_name('run-ci-tests.py'))
    runner = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = runner
    spec.loader.exec_module(runner)
    return runner


def run_bounded(command, timeout, runner, popen=subprocess.Popen):
    with popen(command, start_new_session=os.name != 'nt') as process:
        try:
            return process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            runner.stop_session(process, False)
            print(f'BOUNDED-VERDICT: FAILED (deadline {timeout}s; owned process tree stopped)',
                  file=sys.stderr, flush=True)
            return 124
        except KeyboardInterrupt:
            runner.stop_session(process, False)
            print('BOUNDED-VERDICT: INTERRUPTED (owned process tree stopped)', file=sys.stderr, flush=True)
            return 130


class _FakeProcess:
    """Synthetic owned root; an untimed wait is the unbounded reap this wrapper must never make."""

    def __init__(self, wait_outcome, poll_result=None):
        self.pid = 4321
        self.wait_outcome = wait_outcome
        self.poll_result = poll_result
        self.untimed_waits = 0

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.wait()  # mirrors subprocess.Popen.__exit__
        return False

    def wait(self, timeout=None):
        if timeout is None:
            self.untimed_waits += 1
            raise AssertionError('unbounded wait on the owned root')
        if isinstance(self.wait_outcome, BaseException):
            raise self.wait_outcome
        return self.wait_outcome

    def poll(self):
        return self.poll_result


class _FakeRunner:
    def __init__(self, failure=None):
        self.failure = failure
        self.calls = []

    def stop_session(self, process, elevated):
        self.calls.append((process.pid, elevated))
        if self.failure is not None:
            raise self.failure


class RunBoundedSelfTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.RunFailure = load_runner().RunFailure

    def invoke(self, process, runner):
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            code = run_bounded(['owned'], 7, runner, popen=lambda *args, **kwargs: process)
        return code, stderr.getvalue()

    def test_deadline_cleanup_failure_keeps_124_and_reports_unconfirmed_tree(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=None)
        runner = _FakeRunner(self.RunFailure('taskkill returned 1; wrapper did not exit within 10 seconds; '
                                             "tree termination unconfirmed; taskkill stderr='synthetic'"))
        code, stderr = self.invoke(process, runner)
        self.assertEqual(124, code)
        self.assertEqual([(4321, False)], runner.calls)
        self.assertIn('BOUNDED-VERDICT: FAILED (deadline 7s; owned process tree termination unconfirmed)', stderr)
        self.assertIn('BOUNDED-CLEANUP: tree termination unconfirmed', stderr)
        self.assertIn('root still running', stderr)
        self.assertIn('survivors unknown', stderr)
        self.assertIn("taskkill stderr='synthetic'", stderr)
        self.assertNotIn('owned process tree stopped', stderr)
        self.assertNotIn('Traceback', stderr)
        self.assertEqual(0, process.untimed_waits)

    def test_deadline_cleanup_failure_after_root_already_exited_keeps_124(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=1)
        runner = _FakeRunner(self.RunFailure('taskkill returned 128; wrapper exited; tree termination unconfirmed; '
                                             "taskkill stderr='ERROR: The process \"4321\" not found.'"))
        code, stderr = self.invoke(process, runner)
        self.assertEqual(124, code)
        self.assertIn('BOUNDED-VERDICT: FAILED (deadline 7s; owned process tree termination unconfirmed)', stderr)
        self.assertIn('root exited 1', stderr)
        self.assertIn('survivors unknown', stderr)
        self.assertIn('not found', stderr)
        self.assertNotIn('owned process tree stopped', stderr)
        self.assertEqual(0, process.untimed_waits)

    def test_interrupt_cleanup_failure_keeps_130_and_reports_unconfirmed_tree(self):
        process = _FakeProcess(KeyboardInterrupt(), poll_result=None)
        runner = _FakeRunner(self.RunFailure('could not reap timed-out test process tree'))
        code, stderr = self.invoke(process, runner)
        self.assertEqual(130, code)
        self.assertIn('BOUNDED-VERDICT: INTERRUPTED (owned process tree termination unconfirmed)', stderr)
        self.assertIn('BOUNDED-CLEANUP: tree termination unconfirmed', stderr)
        self.assertIn('could not reap timed-out test process tree', stderr)
        self.assertNotIn('owned process tree stopped', stderr)
        self.assertEqual(0, process.untimed_waits)

    def test_deadline_cleanup_success_reports_stopped_tree(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=-9)
        runner = _FakeRunner()
        code, stderr = self.invoke(process, runner)
        self.assertEqual(124, code)
        self.assertIn('BOUNDED-VERDICT: FAILED (deadline 7s; owned process tree stopped)', stderr)
        self.assertNotIn('unconfirmed', stderr)
        self.assertNotIn('BOUNDED-CLEANUP', stderr)

    def test_interrupt_cleanup_success_reports_stopped_tree(self):
        code, stderr = self.invoke(_FakeProcess(KeyboardInterrupt(), poll_result=-9), _FakeRunner())
        self.assertEqual(130, code)
        self.assertIn('BOUNDED-VERDICT: INTERRUPTED (owned process tree stopped)', stderr)
        self.assertNotIn('unconfirmed', stderr)

    def test_completed_command_preserves_native_exit_without_cleanup(self):
        runner = _FakeRunner()
        code, stderr = self.invoke(_FakeProcess(3, poll_result=3), runner)
        self.assertEqual(3, code)
        self.assertEqual([], runner.calls)
        self.assertNotIn('BOUNDED-VERDICT', stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--timeout', type=int, default=60, choices=range(1, 601),
                        metavar='SECONDS', help='wall-clock deadline, 1-600 seconds (default: 60)')
    parser.add_argument('--selftest', action='store_true', help='check cleanup verdicts with synthetic processes only')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command
    if command and command[0] == '--':
        command = command[1:]
    if args.selftest:
        if command:
            parser.error('--selftest takes no command')
        result = unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(RunBoundedSelfTests))
        return 0 if result.wasSuccessful() else 1
    if not command:
        parser.error('provide a command after --')
    return run_bounded(command, args.timeout, load_runner())


if __name__ == '__main__':
    sys.exit(main())
