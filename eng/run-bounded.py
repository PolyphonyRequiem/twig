#!/usr/bin/env python3
"""Run one owned command with a hard safety timeout and an optional timing warning.

Usage: python eng/run-bounded.py [--warn-after SECONDS] [--timeout SECONDS] -- <command...>
       python eng/run-bounded.py --selftest
"""
import argparse
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import time
import unittest
from unittest import mock


def load_runner():
    # Share the existing cross-platform owned-tree cleanup; do not invent another killer.
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('ci_runner', Path(__file__).with_name('run-ci-tests.py'))
    runner = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = runner
    spec.loader.exec_module(runner)
    return runner


def _stop_owned_tree(process, runner):
    """Return True only when the shared cleanup confirmed the owned tree stopped."""
    try:
        runner.stop_session(process, False)
        return True
    except (runner.RunFailure, OSError, subprocess.SubprocessError) as error:
        # Expected cleanup failures leave survivors unknown: report, never claim success.
        # Programming defects are not caught here and still propagate.
        root = process.poll()  # non-blocking: shared cleanup already spent its own bounded root wait
        root_state = 'root still running' if root is None else f'root exited {root}'
        cause = f'; cause={error.__cause__!r}' if error.__cause__ is not None else ''
        print(f'BOUNDED-CLEANUP: tree termination unconfirmed; {root_state}; survivors unknown; '
              f'{type(error).__name__}: {error}{cause}', file=sys.stderr, flush=True)
        return False


def run_bounded(command, timeout, runner, popen=subprocess.Popen, warn_after=None, clock=time.monotonic):
    # No Popen context manager: its __exit__ makes an untimed wait, which must not follow failed cleanup.
    started = clock()
    process = popen(command, start_new_session=os.name != 'nt')
    try:
        return process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        stopped = _stop_owned_tree(process, runner)
        tree = 'owned process tree stopped' if stopped else 'owned process tree termination unconfirmed'
        print(f'BOUNDED-VERDICT: FAILED (deadline {timeout}s; {tree})', file=sys.stderr, flush=True)
        return 124
    except KeyboardInterrupt:
        stopped = _stop_owned_tree(process, runner)
        tree = 'owned process tree stopped' if stopped else 'owned process tree termination unconfirmed'
        print(f'BOUNDED-VERDICT: INTERRUPTED ({tree})', file=sys.stderr, flush=True)
        return 130
    finally:
        if warn_after is not None:
            elapsed = clock() - started
            if elapsed > warn_after:
                print(f'::warning::BOUNDED-TIMING: elapsed {elapsed:.2f}s exceeded target {warn_after}s; '
                      'command outcome unchanged.', file=sys.stderr, flush=True)


class _FakeProcess:
    """Synthetic owned root. An untimed wait on a still-running root is the unbounded reap this
    wrapper must never make; on an already-exited root it returns at once, as a real Popen does."""

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
            if self.poll_result is not None:
                return self.poll_result
            self.untimed_waits += 1
            raise AssertionError('unbounded wait on a still-running owned root')
        if isinstance(self.wait_outcome, BaseException):
            raise self.wait_outcome
        return self.wait_outcome

    def poll(self):
        return self.poll_result


class _FakeRunner:
    def __init__(self, run_failure, failure=None):
        # The one RunFailure class the tests raise; a second module load would be a distinct class.
        self.RunFailure = run_failure
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

    def invoke(self, process, runner, warn_after=None, elapsed=0):
        stderr = io.StringIO()
        ticks = iter((0, elapsed))
        with contextlib.redirect_stderr(stderr):
            code = run_bounded(['owned'], 7, runner, popen=lambda *args, **kwargs: process,
                               warn_after=warn_after, clock=lambda: next(ticks))
        return code, stderr.getvalue()

    def test_slow_success_warns_without_cleanup_or_failure(self):
        runner = _FakeRunner(self.RunFailure)
        code, stderr = self.invoke(_FakeProcess(0, poll_result=0), runner, warn_after=3, elapsed=4)
        self.assertEqual(0, code)
        self.assertIn('::warning::BOUNDED-TIMING: elapsed 4.00s exceeded target 3s', stderr)
        self.assertEqual([], runner.calls)

    def test_slow_failure_warns_and_preserves_child_exit(self):
        runner = _FakeRunner(self.RunFailure)
        code, stderr = self.invoke(_FakeProcess(5, poll_result=5), runner, warn_after=3, elapsed=4)
        self.assertEqual(5, code)
        self.assertIn('::warning::BOUNDED-TIMING:', stderr)
        self.assertEqual([], runner.calls)

    def test_at_or_below_target_does_not_warn(self):
        for elapsed in (2, 3):
            with self.subTest(elapsed=elapsed):
                code, stderr = self.invoke(_FakeProcess(0, poll_result=0),
                                           _FakeRunner(self.RunFailure), warn_after=3, elapsed=elapsed)
                self.assertEqual(0, code)
                self.assertEqual('', stderr)

    def test_smoke_supervisor_warning_preserves_worker_outcome(self):
        spec = importlib.util.spec_from_file_location('smoke_supervisor', Path(__file__).with_name('verify-build.py'))
        smoke = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(smoke)
        for elapsed, exit_code in ((61, 0), (61, 5), (30, 0)):
            with self.subTest(elapsed=elapsed, exit_code=exit_code):
                process = mock.MagicMock()
                process.communicate.return_value = ('worker output', 'worker error')
                process.returncode = exit_code
                ticks = iter((0, elapsed))
                stderr = io.StringIO()
                trace = mock.Mock()
                with mock.patch.object(smoke.subprocess, 'Popen') as popen, \
                        contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
                    popen.return_value = process
                    result = smoke.run_worker(['owned'], 300, trace, clock=lambda: next(ticks))
                self.assertEqual(exit_code, result)
                self.assertEqual(elapsed > 60, '::warning::SMOKE TIMING:' in stderr.getvalue())
                self.assertIn('worker error', stderr.getvalue())
                process.communicate.assert_called_once_with(timeout=300)
                self.assertEqual([mock.call('supervisor', 'start'), mock.call('supervisor', 'end', exit_code)],
                                 trace.write.call_args_list)

    def test_smoke_cleanup_failure_never_enters_context_or_waits_without_bound(self):
        spec = importlib.util.spec_from_file_location('smoke_cleanup', Path(__file__).with_name('verify-build.py'))
        smoke = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(smoke)
        process = mock.MagicMock()
        process.communicate.side_effect = subprocess.TimeoutExpired(['owned'], 300)
        process.poll.return_value = None
        runner = _FakeRunner(self.RunFailure, self.RunFailure('synthetic cleanup failure'))
        runner_spec = mock.Mock()
        runner_spec.name = 'ci_runner'
        ticks = iter((0, 301))
        stderr = io.StringIO()
        with mock.patch.object(smoke.subprocess, 'Popen', return_value=process), \
                mock.patch.object(smoke.importlib.util, 'spec_from_file_location', return_value=runner_spec), \
                mock.patch.object(smoke.importlib.util, 'module_from_spec', return_value=runner), \
                mock.patch.dict(smoke.sys.modules), \
                contextlib.redirect_stderr(stderr):
            code = smoke.run_worker(['owned'], 300, mock.Mock(), clock=lambda: next(ticks))
        self.assertEqual(1, code)
        self.assertEqual([(process.pid, False)], runner.calls)
        self.assertIn('tree termination unconfirmed; root still running; survivors unknown', stderr.getvalue())
        self.assertNotIn('owned process tree stopped', stderr.getvalue())
        process.__enter__.assert_not_called()
        process.__exit__.assert_not_called()
        process.wait.assert_not_called()

    def test_deadline_cleanup_failure_keeps_124_and_reports_unconfirmed_tree(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=None)
        runner = _FakeRunner(self.RunFailure, self.RunFailure('taskkill returned 1; wrapper did not exit within 10 seconds; '
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
        runner = _FakeRunner(self.RunFailure, self.RunFailure('taskkill returned 128; wrapper exited; tree termination unconfirmed; '
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
        runner = _FakeRunner(self.RunFailure, self.RunFailure('could not reap timed-out test process tree'))
        code, stderr = self.invoke(process, runner)
        self.assertEqual(130, code)
        self.assertIn('BOUNDED-VERDICT: INTERRUPTED (owned process tree termination unconfirmed)', stderr)
        self.assertIn('BOUNDED-CLEANUP: tree termination unconfirmed', stderr)
        self.assertIn('could not reap timed-out test process tree', stderr)
        self.assertNotIn('owned process tree stopped', stderr)
        self.assertEqual(0, process.untimed_waits)

    def test_deadline_taskkill_oserror_keeps_124_and_preserves_cause(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=None)
        failure = self.RunFailure('taskkill could not run; wrapper exited; tree termination unconfirmed')
        failure.__cause__ = FileNotFoundError('synthetic taskkill missing')
        code, stderr = self.invoke(process, _FakeRunner(self.RunFailure, failure))
        self.assertEqual(124, code)
        self.assertIn('RunFailure: taskkill could not run', stderr)
        self.assertIn("cause=FileNotFoundError('synthetic taskkill missing')", stderr)
        self.assertIn('owned process tree termination unconfirmed', stderr)

    def test_cleanup_programming_defect_is_not_masked(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=None)
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr), self.assertRaises(TypeError):
            run_bounded(['owned'], 7, _FakeRunner(self.RunFailure, TypeError('synthetic defect')),
                        popen=lambda *args, **kwargs: process)
        self.assertNotIn('BOUNDED-VERDICT', stderr.getvalue())
        self.assertEqual(0, process.untimed_waits)

    def test_deadline_cleanup_success_reports_stopped_tree(self):
        process = _FakeProcess(subprocess.TimeoutExpired(['owned'], 7), poll_result=-9)
        runner = _FakeRunner(self.RunFailure)
        code, stderr = self.invoke(process, runner)
        self.assertEqual(124, code)
        self.assertIn('BOUNDED-VERDICT: FAILED (deadline 7s; owned process tree stopped)', stderr)
        self.assertNotIn('unconfirmed', stderr)
        self.assertNotIn('BOUNDED-CLEANUP', stderr)

    def test_interrupt_cleanup_success_reports_stopped_tree(self):
        code, stderr = self.invoke(_FakeProcess(KeyboardInterrupt(), poll_result=-9), _FakeRunner(self.RunFailure))
        self.assertEqual(130, code)
        self.assertIn('BOUNDED-VERDICT: INTERRUPTED (owned process tree stopped)', stderr)
        self.assertNotIn('unconfirmed', stderr)

    def test_completed_command_preserves_native_exit_without_cleanup(self):
        runner = _FakeRunner(self.RunFailure)
        code, stderr = self.invoke(_FakeProcess(3, poll_result=3), runner)
        self.assertEqual(3, code)
        self.assertEqual([], runner.calls)
        self.assertNotIn('BOUNDED-VERDICT', stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--timeout', type=int, default=60, choices=range(1, 601),
                        metavar='SECONDS', help='hard safety timeout, 1-600 seconds (default: 60)')
    parser.add_argument('--warn-after', type=int, choices=range(1, 601), metavar='SECONDS',
                        help='elapsed-time warning target; never changes the command exit code')
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
    return run_bounded(command, args.timeout, load_runner(), warn_after=args.warn_after)


if __name__ == '__main__':
    sys.exit(main())
