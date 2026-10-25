#!/usr/bin/env python3
"""Run already-built CI assemblies serially, with bounded CLI sessions and exact coverage.

Usage: python tools/run-ci-tests.py [--selftest | --selftest-windows-cleanup]
The local run-tests.sh BinaryLauncher exclusion is deliberately NOT used here.
"""

import argparse
from collections import Counter
from dataclasses import dataclass
import ctypes
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent
ASSEMBLIES = ("Cli", "Domain", "Infrastructure", "RenderTree", "Tui")
HOST_CLASSES = {
    "Twig.Cli.Tests.Commands.ConnectionBindingTransitionConsumerTests",
    "Twig.Cli.Tests.Commands.ConnectionMigrateCommandTests",
    "Twig.Cli.Tests.Commands.SeedLifecycleIntegrationTests",
    "Twig.Cli.Tests.Browser.BrowserApplicationScenarioTests",
    "Twig.Cli.Tests.Browser.BrowserBenchModuleTests",
    "Twig.Cli.Tests.Browser.BrowserEditingModuleTests",
    "Twig.Cli.Tests.Browser.BrowserReadModuleTests",
    "Twig.Cli.Tests.Browser.BrowserReviewModuleTests",
}
MAX_CLI_CASES = 200
MAX_HOST_CASES = 10
MAX_FILTER_LENGTH = 6000  # Leave room in Windows' command-line limit for SDK/artifact paths.
SESSION_TIMEOUT = 300000
BAD_OUTPUT = re.compile(
    r"Test Run Aborted|Aborting test run|test host process crashed|"
    r"No test matches the given testcase filter|No test is available|"
    r"The argument .*\.dll is invalid\.|Discovery of tests .* failed|"
    r"Exception occurred while test discoverer was loading tests",
    re.IGNORECASE,
)
SUMMARY = re.compile(
    r"(?:Passed!|Failed!|Skipped!)\s*-\s*Failed:\s*(\d+),\s*"
    r"Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)"
)
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
WINDOWS_TREE_STARTUP_TIMEOUT = 10
WINDOWS_TREE_OUTER_TIMEOUT = 45

WINDOWS_TREE_WORKER = r"""
import ctypes
from ctypes import wintypes
import os
import subprocess
import sys
import time

class FILETIME(ctypes.Structure):
    _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.GetCurrentProcess.restype = wintypes.HANDLE

def creation_time(handle):
    kernel.GetProcessTimes.argtypes = [
        wintypes.HANDLE, ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
    ]
    created = FILETIME()
    exited = FILETIME()
    kernel_seconds = FILETIME()
    user_seconds = FILETIME()
    if not kernel.GetProcessTimes(
        handle, ctypes.byref(created), ctypes.byref(exited),
        ctypes.byref(kernel_seconds), ctypes.byref(user_seconds),
    ):
        raise ctypes.WinError(ctypes.get_last_error())
    return (created.high << 32) | created.low

creation = creation_time(kernel.GetCurrentProcess())
role = sys.argv[1]

if role == "root":
    print(f"READY root {os.getpid()} {creation}", flush=True)
    child = None
    for command in sys.stdin:
        command = command.strip()
        if command == "spawn-child":
            child = subprocess.Popen(
                [sys.executable, "-c", sys.argv[2], "child", sys.argv[2]],
                stdin=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            print(f"CHILD {child.pid} {creation_time(child._handle)}", flush=True)
        elif command == "announce-child" and child is not None:
            child.stdin.write("announce\n")
            child.stdin.flush()
        elif command == "spawn-grandchild" and child is not None:
            child.stdin.write("spawn-grandchild\n")
            child.stdin.flush()
        elif command == "announce-grandchild" and child is not None:
            child.stdin.write("announce-grandchild\n")
            child.stdin.flush()
elif role == "child":
    grandchild = None
    for command in sys.stdin:
        command = command.strip()
        if command == "announce":
            print(f"READY child {os.getpid()} {creation}", flush=True)
        elif command == "spawn-grandchild":
            grandchild = subprocess.Popen(
                [sys.executable, "-c", sys.argv[2], "grandchild", sys.argv[2]],
                stdin=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            print(f"CHILD {grandchild.pid} {creation_time(grandchild._handle)}", flush=True)
        elif command == "announce-grandchild" and grandchild is not None:
            grandchild.stdin.write("announce\n")
            grandchild.stdin.flush()
elif role == "grandchild":
    for command in sys.stdin:
        if command.strip() == "announce":
            print(f"READY grandchild {os.getpid()} {creation}", flush=True)
            break
while True:
    time.sleep(0.1)
"""

WINDOWS_SENTINEL_WORKER = r"""
import ctypes
from ctypes import wintypes
import os
import sys

class FILETIME(ctypes.Structure):
    _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.GetCurrentProcess.restype = wintypes.HANDLE
kernel.GetProcessTimes.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
    ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
]
created = FILETIME()
exited = FILETIME()
kernel_seconds = FILETIME()
user_seconds = FILETIME()
if not kernel.GetProcessTimes(
    kernel.GetCurrentProcess(), ctypes.byref(created), ctypes.byref(exited),
    ctypes.byref(kernel_seconds), ctypes.byref(user_seconds),
):
    raise ctypes.WinError(ctypes.get_last_error())
creation = (created.high << 32) | created.low
print(f"READY sentinel {os.getpid()} {creation}", flush=True)
for challenge in sys.stdin:
    print(f"PONG {challenge.strip()} {os.getpid()} {creation}", flush=True)
"""


class RunFailure(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise RunFailure(message)


def check_process(output, exit_code):
    # The native exit code is authoritative; clean-looking summaries are not verdicts.
    require(exit_code == 0, f"process exit code {exit_code}")
    marker = BAD_OUTPUT.search(output)
    require(marker is None, f"aborted, empty or invalid session: {marker.group(0) if marker else ''}")


def method_name(display_name):
    # This repo uses xUnit's default fully-qualified display names; fail closed on drift.
    name = display_name.split("(", 1)[0]
    require(re.fullmatch(r"Twig\.[\w.+]+", name) is not None, f"unsupported test display name: {display_name!r}")
    return name


def display_cases(output, assembly):
    require("The following Tests are available:" in output, "discovery header missing")
    prefix = f"Twig.{assembly}.Tests."
    cases = Counter(line.strip() for line in output.splitlines() if line.startswith("    " + prefix))
    require(bool(cases), f"empty discovery for {assembly}")
    for name in cases:
        method_name(name)
    return cases


def eligible_cases(cases, methods):
    discovered = {method_name(name) for name in cases}
    require(bool(methods), "filtered discovery is empty")
    require(methods <= discovered, f"filtered discovery absent from display discovery: {sorted(methods - discovered)}")
    return Counter({name: count for name, count in cases.items() if method_name(name) in methods})


@dataclass(frozen=True)
class Partition:
    name: str
    filter: str
    cases: Counter
    host_inventory: bool = False


def partition_cli(cases, host_methods, base_filter):
    methods = {method_name(name) for name in cases}
    require(host_methods <= methods, "HostInventory discovery includes ineligible methods")
    expected_host = {method for method in methods if method.rsplit(".", 1)[0] in HOST_CLASSES}
    require(host_methods == expected_host and bool(host_methods), "HostInventory trait does not match the declared inventory classes")
    require({method.rsplit(".", 1)[0] for method in host_methods} == HOST_CLASSES, "an inventory class is missing")
    groups = {}
    for name, count in cases.items():
        method = method_name(name)
        groups.setdefault(method.rsplit(".", 1)[0], {}).setdefault(method, Counter())[name] = count

    partitions = []
    for host in (False, True):
        batch = Counter()
        selectors = []

        def flush():
            nonlocal batch, selectors
            if batch:
                category = "Category=HostInventory" if host else "Category!=HostInventory"
                expression = f"({base_filter})&{category}&({'|'.join(selectors)})"
                require(len(expression) <= MAX_FILTER_LENGTH, "CLI filter exceeds command-line bound")
                partitions.append(Partition(f"Cli-{'host' if host else 'part'}-{len(partitions) + 1:03d}",
                                            expression, batch, host))
                batch = Counter()
                selectors = []

        for class_name, class_methods in sorted(groups.items()):
            if (class_name in HOST_CLASSES) != host:
                continue
            limit = MAX_HOST_CASES if host else MAX_CLI_CASES
            class_rows = Counter()
            for rows in class_methods.values():
                class_rows.update(rows)
            # Whole-class selectors keep normal partitions compact. Only oversized classes
            # need exact method selectors, and theories remain indivisible discovery units.
            if sum(class_rows.values()) <= limit:
                units = [(f"FullyQualifiedName~{class_name}.", class_rows)]
            else:
                units = [(f"FullyQualifiedName={method}", rows) for method, rows in sorted(class_methods.items())]
            for selector, rows in units:
                require(sum(rows.values()) <= limit, f"one theory exceeds partition bound: {selector}")
                filter_length = sum(len(item) + 1 for item in selectors) + len(selector) + len(base_filter) + 50
                if batch and (sum(batch.values()) + sum(rows.values()) > limit or filter_length > MAX_FILTER_LENGTH):
                    flush()
                batch.update(rows)
                selectors.append(selector)
            if host:
                flush()  # Inventory classes never overlap, even within one session.
        flush()

    reconcile_coverage(cases, [part.cases for part in partitions])
    return partitions


def reconcile_coverage(expected, completed):
    actual = Counter()
    for cases in completed:
        actual.update(cases)
    require(actual == expected, f"coverage mismatch: missing={dict(expected - actual)}, extra={dict(actual - expected)}")


def reconcile_result(output, exit_code, trx, expected):
    check_process(output, exit_code)
    require(bool(expected), "empty partition")
    summaries = SUMMARY.findall(output)
    require(len(summaries) == 1, "expected exactly one assembly summary")
    failed, passed, skipped, total = map(int, summaries[0])
    require(failed == 0 and total == sum(expected.values()) and passed + skipped == total,
            f"summary incomplete: expected={sum(expected.values())}, actual={total}, failed={failed}")
    root = ET.parse(trx).getroot()
    summary = root.find("t:ResultSummary", NS)
    require(summary is not None and summary.get("outcome") in {"Completed", "Passed"}, "TRX run did not complete")
    counters = summary.find("t:Counters", NS)
    require(counters is not None and int(counters.get("total", "-1")) == total, "TRX total differs from discovery")
    for key in ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "disconnected", "inProgress", "pending"):
        require(int(counters.get(key, "0")) == 0, f"TRX contains {key}")
    results = root.findall("t:Results/t:UnitTestResult", NS)
    actual = Counter(result.get("testName") for result in results)
    reconcile_coverage(expected, [actual])
    require(len({result.get("executionId") for result in results}) == len(results), "duplicate TRX execution")
    require(len({result.get("testId") for result in results}) == len(results), "duplicate TRX test identity")
    outcomes = Counter(result.get("outcome") for result in results)
    require(set(outcomes) <= {"Passed", "NotExecuted"}, f"unsuccessful TRX outcomes: {dict(outcomes)}")
    require(outcomes["Passed"] == passed and outcomes["NotExecuted"] == skipped, "summary and TRX outcomes disagree")
    require(int(counters.get("passed", "-1")) == passed, "TRX passed counter and results disagree")
    # VSTest's xUnit adapter leaves notExecuted=0 for explicitly skipped rows.
    # Exact row coverage and the native skipped summary above remain authoritative.
    require(int(counters.get("notExecuted", "0")) in {0, skipped}, "TRX skipped counter and results disagree")
    for result in results:
        if result.get("outcome") == "NotExecuted":
            print(f"TWIG-SKIPPED {result.get('testName')}")
    return actual, passed, skipped


def _cleanup_output(value):
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


def _stop_windows_session(process):
    command = ["taskkill", "/PID", str(process.pid), "/T", "/F"]
    result = None
    taskkill_error = None
    try:
        result = subprocess.run(command, capture_output=True, check=False, timeout=10)
    except (OSError, subprocess.TimeoutExpired) as error:
        taskkill_error = error

    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired as error:
        if taskkill_error is not None:
            taskkill_status = (
                "taskkill timed out"
                if isinstance(taskkill_error, subprocess.TimeoutExpired)
                else f"taskkill could not run: {taskkill_error}"
            )
            stdout = _cleanup_output(getattr(taskkill_error, "stdout", None))
            stderr = _cleanup_output(getattr(taskkill_error, "stderr", None))
        else:
            taskkill_status = f"taskkill returned {result.returncode}"
            stdout = _cleanup_output(result.stdout)
            stderr = _cleanup_output(result.stderr)
        raise RunFailure(
            f"{taskkill_status}; wrapper did not exit within 10 seconds; "
            f"tree termination unconfirmed; taskkill stdout={stdout!r}; taskkill stderr={stderr!r}"
        ) from error

    if taskkill_error is not None:
        taskkill_status = (
            "taskkill timed out"
            if isinstance(taskkill_error, subprocess.TimeoutExpired)
            else f"taskkill could not run: {taskkill_error}"
        )
        stdout = _cleanup_output(getattr(taskkill_error, "stdout", None))
        stderr = _cleanup_output(getattr(taskkill_error, "stderr", None))
        raise RunFailure(
            f"{taskkill_status}; wrapper exited; tree termination unconfirmed; "
            f"taskkill stdout={stdout!r}; taskkill stderr={stderr!r}"
        ) from taskkill_error

    if result.returncode != 0:
        raise RunFailure(
            f"taskkill returned {result.returncode}; wrapper exited; tree termination unconfirmed; "
            f"taskkill stdout={_cleanup_output(result.stdout)!r}; "
            f"taskkill stderr={_cleanup_output(result.stderr)!r}"
        )


def stop_session(process, elevated):
    # Kill only the tree launched by this runner; never leave a timed-out host beside the next partition.
    if os.name == "nt":
        _stop_windows_session(process)
        return
    else:
        prefix = ["sudo", "-n"] if elevated else []
        # Freeze parents before looking for children, so the captured tree cannot keep spawning.
        pending = [process.pid]
        stopped = []
        while pending:
            subprocess.run([*prefix, "/bin/kill", "-STOP", "--", *map(str, pending)], capture_output=True, check=False)
            stopped.extend(pending)
            snapshot = subprocess.run(["ps", "-e", "-o", "pid=,ppid="], capture_output=True, text=True, check=True)
            parents = {int(pid): int(parent) for pid, parent in (line.split() for line in snapshot.stdout.splitlines())}
            pending = [pid for pid, parent in parents.items() if parent in stopped and pid not in stopped]
        subprocess.run([*prefix, "/bin/kill", "-KILL", "--", *map(str, reversed(stopped))], capture_output=True, check=False)
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired as error:
        raise RunFailure("could not reap timed-out test process tree") from error


class Runner:
    def __init__(self):
        settings = ROOT / "test.runsettings"
        config = ET.parse(settings).getroot().find("RunConfiguration")
        require(config is not None and config.findtext("TestSessionTimeout") == str(SESSION_TIMEOUT), "the existing 300000ms watchdog must be preserved")
        self.base_filter = config.findtext("TestCaseFilter")
        require(bool(self.base_filter), "CI eligibility filter is missing")
        self.settings = settings
        executable = shutil.which("dotnet")
        require(executable is not None, "dotnet is missing from PATH")
        self.dotnet = str(Path(executable).resolve())
        log_root = ROOT / "artifacts" / "test-logs" / "ci"
        log_root.mkdir(parents=True, exist_ok=True)
        self.logs = Path(tempfile.mkdtemp(prefix="run-", dir=log_root))
        self.trace = Path(os.environ.get("TWIG_TEST_TRACE", ROOT / "artifacts" / "trace-311")).resolve()
        self.env = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en-US")

    def invoke(self, dll, options, name, elevated=False):
        command = [self.dotnet, "vstest", str(dll), f"/Settings:{self.settings}", *options]
        env = dict(self.env)
        trace = self.trace / self.logs.name / name
        trace.mkdir(parents=True, exist_ok=True)
        env["TWIG_TEST_TRACE"] = str(trace)
        elevated = elevated and sys.platform.startswith("linux")
        if elevated:
            # /proc/*/fd inspection needs root on the hosted Linux runner. No product bypass.
            # Pin the same SDK and child-process PATH; do not depend on sudo's secure_path.
            env["DOTNET_ROOT"] = str(Path(self.dotnet).parent)
            env["DOTNET_CLI_HOME"] = str(self.logs / "host-sdk-home")
            keep = ("PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_CLI_HOME",
                    "DOTNET_ROLL_FORWARD", "DOTNET_CLI_UI_LANGUAGE", "DOTNET_CLI_TELEMETRY_OPTOUT",
                    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "LD_LIBRARY_PATH", "TWIG_TEST_TRACE")
            command = ["sudo", "-n", "env", *(f"{key}={env[key]}" for key in keep if key in env), *command]
        print(f"TWIG-SESSION {name}", flush=True)
        log = self.logs / f"{name}.log"
        with log.open("w", encoding="utf-8") as stream:
            # VSTest keeps its unchanged watchdog; the outer deadline also bounds discovery/startup.
            started = time.monotonic()
            with subprocess.Popen(command, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT) as process:
                try:
                    exit_code = process.wait(timeout=SESSION_TIMEOUT / 1000)
                except subprocess.TimeoutExpired:
                    stop_session(process, elevated)
                    stream.write("\nTest Run Aborted: runner process deadline of 300000ms exceeded.\n")
                    exit_code = 1
        elapsed = time.monotonic() - started
        print(f"TWIG-TIMING {name}: {elapsed:.3f}s wall", flush=True)
        output = log.read_text(encoding="utf-8", errors="replace").replace("\r", "\n")
        print(output, end="" if output.endswith("\n") else "\n", flush=True)
        return output, exit_code

    def discover_methods(self, dll, filter_expression, name):
        target = self.logs / f"{name}.txt"
        output, exit_code = self.invoke(dll, ["/ListFullyQualifiedTests", f"/ListTestsTargetPath:{target}",
                                            f"/TestCaseFilter:{filter_expression}"], name)
        check_process(output, exit_code)
        require(target.is_file(), f"filtered discovery file missing: {target}")
        methods = set(target.read_text(encoding="utf-8-sig").splitlines())
        require(bool(methods) and all(re.fullmatch(r"Twig\.[\w.+]+", method) for method in methods), "invalid or empty filtered discovery")
        return methods

    def run(self):
        # Read the solution's supported test-project list, not every DLL on disk (MCP is withdrawn).
        solution = ET.parse(ROOT / "Twig.slnx").getroot()
        supported = set()
        for project in solution.iter("Project"):
            path = project.get("Path")
            if path.startswith("tests/") and ET.parse(ROOT / path).getroot().findtext("PropertyGroup/IsTestProject") == "true":
                supported.add(Path(path).stem)
        require(supported == {f"Twig.{suite}.Tests" for suite in ASSEMBLIES}, f"supported assembly inventory changed: {sorted(supported)}")
        framework = ET.parse(ROOT / "Directory.Build.props").getroot().findtext("PropertyGroup/TargetFramework")
        require(bool(framework), "test target framework missing")
        failed = False
        totals = Counter()
        for assembly in ASSEMBLIES:
            try:
                dll = ROOT / "tests" / f"Twig.{assembly}.Tests" / "bin" / "Debug" / framework / f"Twig.{assembly}.Tests.dll"
                require(dll.is_file(), f"built assembly missing: {dll}")
                output, exit_code = self.invoke(dll, ["/ListTests"], f"{assembly}-discovery")
                check_process(output, exit_code)
                all_cases = display_cases(output, assembly)
                methods = self.discover_methods(dll, self.base_filter, f"{assembly}-eligible")
                cases = eligible_cases(all_cases, methods)
                if assembly == "Cli":
                    host_methods = self.discover_methods(dll, f"({self.base_filter})&Category=HostInventory", "Cli-host-discovery")
                    partitions = partition_cli(cases, host_methods, self.base_filter)
                else:
                    partitions = [Partition(assembly, self.base_filter, cases)]
                manifest = [{"name": part.name, "filter": part.filter, "expected": sum(part.cases.values()),
                             "hostInventory": part.host_inventory, "cases": dict(sorted(part.cases.items()))} for part in partitions]
                (self.logs / f"{assembly}-plan.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
                print(f"TWIG-DISCOVERY {assembly}: {sum(cases.values())} eligible, {sum(all_cases.values()) - sum(cases.values())} excluded by runsettings, {len(partitions)} sessions")
                completed = []
                for part in partitions:
                    try:
                        results = self.logs / part.name
                        results.mkdir()
                        output, exit_code = self.invoke(dll, [f"/TestCaseFilter:{part.filter}", f"/ResultsDirectory:{results}",
                                                            "/Logger:trx;LogFileName=results.trx"], part.name, part.host_inventory)
                        actual, passed, skipped = reconcile_result(output, exit_code, results / "results.trx", part.cases)
                        completed.append(actual)
                        totals.update(passed=passed, skipped=skipped)
                        print(f"TWIG-VERDICT {part.name}: PASSED ({passed} passed, {skipped} skipped; {sum(part.cases.values())} expected)")
                    except (RunFailure, OSError, ET.ParseError, ValueError) as error:
                        failed = True
                        print(f"TWIG-VERDICT {part.name}: FAILED ({error})")
                reconcile_coverage(cases, completed)
                print(f"TWIG-COVERAGE {assembly}: {sum(cases.values())}/{sum(cases.values())} exactly once")
            except (RunFailure, OSError, ET.ParseError, ValueError) as error:
                failed = True
                print(f"TWIG-VERDICT {assembly}: FAILED ({error})")
        print(f"TWIG-VERDICT OVERALL: {'FAILED' if failed else 'PASSED'} ({totals['passed']} passed, {totals['skipped']} skipped) [logs: {self.logs}]")
        return int(failed)


class RunnerSelfTests(unittest.TestCase):
    """Synthetic runner-contract checks only: no SDK, build, discovery or test host."""
    def test_windows_taskkill_failure_reaps_wrapper_without_claiming_tree_stopped(self):
        process = Mock(pid=4321)
        process.poll.return_value = None
        process.wait.return_value = 0
        self.assertIsNone(process.poll())
        taskkill = subprocess.CompletedProcess(
            ["taskkill"], 1, "synthetic taskkill stdout", "synthetic taskkill diagnostic")

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", return_value=taskkill) as run:
                with self.assertRaisesRegex(RunFailure, "tree termination unconfirmed") as failure:
                    stop_session(process, False)

        process.wait.assert_called_once_with(timeout=10)
        self.assertIn("taskkill returned 1", str(failure.exception))
        self.assertIn("synthetic taskkill stdout", str(failure.exception))
        self.assertIn("synthetic taskkill diagnostic", str(failure.exception))
        run.assert_called_once_with(
            ["taskkill", "/PID", "4321", "/T", "/F"],
            capture_output=True,
            check=False,
            timeout=10,
        )

    def test_windows_taskkill_timeout_preserves_diagnostics_and_bounds_wrapper_reap(self):
        process = Mock(pid=4322)
        process.wait.return_value = 0
        timeout = subprocess.TimeoutExpired(
            ["taskkill"], 10, output="partial taskkill stdout", stderr="partial taskkill stderr")

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", side_effect=timeout) as run:
                with self.assertRaisesRegex(RunFailure, "taskkill timed out") as failure:
                    stop_session(process, False)

        process.wait.assert_called_once_with(timeout=10)
        self.assertIn("partial taskkill stdout", str(failure.exception))
        self.assertIn("partial taskkill stderr", str(failure.exception))
        self.assertIn("tree termination unconfirmed", str(failure.exception))
        run.assert_called_once_with(
            ["taskkill", "/PID", "4322", "/T", "/F"],
            capture_output=True,
            check=False,
            timeout=10,
        )

    def test_windows_taskkill_failure_reports_unreaped_wrapper_honestly(self):
        process = Mock(pid=4323)
        process.wait.side_effect = subprocess.TimeoutExpired(["test-runner"], 10)
        taskkill = subprocess.CompletedProcess(
            ["taskkill"], 1, "taskkill stdout", "taskkill stderr")

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", return_value=taskkill):
                with self.assertRaisesRegex(RunFailure, "wrapper did not exit") as failure:
                    stop_session(process, False)

        process.wait.assert_called_once_with(timeout=10)
        self.assertIn("tree termination unconfirmed", str(failure.exception))
        self.assertIn("taskkill stdout", str(failure.exception))
        self.assertIn("taskkill stderr", str(failure.exception))

    def test_windows_taskkill_success_keeps_bounded_wrapper_wait(self):
        process = Mock(pid=4324)
        process.wait.return_value = 0
        taskkill = subprocess.CompletedProcess(["taskkill"], 0, "SUCCESS", "")

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", return_value=taskkill) as run:
                stop_session(process, False)

        run.assert_called_once_with(
            ["taskkill", "/PID", "4324", "/T", "/F"],
            capture_output=True,
            check=False,
            timeout=10,
        )
        process.wait.assert_called_once_with(timeout=10)

    def test_windows_taskkill_success_does_not_claim_tree_stopped_when_wrapper_wait_times_out(self):
        process = Mock(pid=4325)
        process.wait.side_effect = subprocess.TimeoutExpired(["test-runner"], 10)
        taskkill = subprocess.CompletedProcess(["taskkill"], 0, "SUCCESS", "")

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", return_value=taskkill):
                with self.assertRaises(RunFailure) as failure:
                    stop_session(process, False)

        process.wait.assert_called_once_with(timeout=10)
        self.assertNotIn("tree stopped", str(failure.exception))

    def test_windows_taskkill_oserror_reaps_wrapper_and_reports_unconfirmed_tree(self):
        process = Mock(pid=4326)
        process.wait.return_value = 0

        with patch.object(sys.modules[__name__], "os", SimpleNamespace(name="nt")):
            with patch.object(subprocess, "run", side_effect=FileNotFoundError("taskkill missing")) as run:
                with self.assertRaisesRegex(RunFailure, "tree termination unconfirmed") as failure:
                    stop_session(process, False)

        process.wait.assert_called_once_with(timeout=10)
        self.assertIn("taskkill could not run", str(failure.exception))
        self.assertIn("taskkill missing", str(failure.exception))
        run.assert_called_once_with(
            ["taskkill", "/PID", "4326", "/T", "/F"],
            capture_output=True,
            check=False,
            timeout=10,
        )

    def test_partition_coverage_is_complete_disjoint_and_bounded(self):
        cases = Counter({f"Twig.Cli.Tests.Commands.Ordinary{index // 15}.Case{index}": 1 for index in range(421)})
        cases.update({f"Twig.Cli.Tests.Commands.Oversized.Case{index}": 1 for index in range(250)})
        cases.update({f"{name}.Migrate{method}(row: {row})": 1 for name in HOST_CLASSES for method in range(4) for row in range(3)})
        host_methods = {f"{name}.Migrate{method}" for name in HOST_CLASSES for method in range(4)}
        partitions = partition_cli(cases, host_methods, "Category!=Interactive&Category!=Integration")
        reconcile_coverage(cases, [part.cases for part in partitions])
        self.assertTrue(all(0 < sum(part.cases.values()) <= MAX_CLI_CASES for part in partitions))
        inventory = [part for part in partitions if part.host_inventory]
        self.assertEqual(inventory, partitions[-len(inventory):])
        self.assertTrue(all(0 < sum(part.cases.values()) <= MAX_HOST_CASES for part in inventory))
        self.assertEqual(partitions, partition_cli(Counter(dict(reversed(list(cases.items())))), host_methods,
                                                 "Category!=Interactive&Category!=Integration"))
        with self.assertRaises(RunFailure):
            partition_cli(cases, set(), "Category!=Interactive")
        with self.assertRaises(RunFailure):
            reconcile_coverage(cases, [part.cases for part in partitions[:-1]])
        with self.assertRaises(RunFailure):
            reconcile_coverage(cases, [part.cases for part in partitions] + [partitions[0].cases])
        oversized = [part for part in partitions if any(".Oversized." in name for name in part.cases)]
        self.assertGreater(len(oversized), 1)

    def test_discovery_retains_rows_skips_and_ci_binary_launcher(self):
        output = "The following Tests are available:\n    Twig.Cli.Tests.BinaryLauncherTests.Run\n    Twig.Cli.Tests.Commands.Host.Migrate\n    Twig.Cli.Tests.Commands.Rows.Run(value: 1)\n    Twig.Cli.Tests.Commands.Rows.Run(value: 2)\n    Twig.Cli.Tests.Commands.Interactive.Run\n"
        cases = display_cases(output, "Cli")
        eligible = eligible_cases(cases, {"Twig.Cli.Tests.BinaryLauncherTests.Run", "Twig.Cli.Tests.Commands.Host.Migrate", "Twig.Cli.Tests.Commands.Rows.Run"})
        self.assertEqual(sum(eligible.values()), 4)
        self.assertIn("Twig.Cli.Tests.BinaryLauncherTests.Run", eligible)
        with self.assertRaises(RunFailure):
            eligible_cases(cases, {"Twig.Cli.Tests.Commands.Missing.Run"})
        with self.assertRaises(RunFailure):
            display_cases("The following Tests are available:\n", "Cli")

    def test_abort_and_native_exit_are_authoritative(self):
        check_process("Passed!", 0)
        for output, exit_code in (("Passed!", 1), ("Passed!\nTest Run Aborted.", 0),
                                  ("Passed!\nAborting test run: test run timeout of 300000 milliseconds exceeded.", 0),
                                  ("No test matches the given testcase filter", 0)):
            with self.subTest(output=output, exit_code=exit_code), self.assertRaises(RunFailure):
                check_process(output, exit_code)

    def test_trx_requires_exact_rows_and_honest_skips(self):
        expected = Counter({"Twig.Cli.Tests.Commands.Rows.Run(value: 1)": 1, "Twig.Cli.Tests.Commands.Host.Migrate": 1})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.trx"
            root = ET.Element("TestRun", xmlns=NS["t"])
            results = ET.SubElement(root, "Results")
            for index, (name, outcome) in enumerate(zip(expected, ("Passed", "NotExecuted"))):
                ET.SubElement(results, "UnitTestResult", testName=name, executionId=str(index), testId=str(index), outcome=outcome)
            summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
            counters = ET.SubElement(summary, "Counters", total="2", passed="1", notExecuted="1")
            ET.ElementTree(root).write(path, encoding="utf-8")
            output = "Passed! - Failed: 0, Passed: 1, Skipped: 1, Total: 2"
            self.assertEqual(reconcile_result(output, 0, path, expected), (expected, 1, 1))
            counters.set("notExecuted", "0")
            ET.ElementTree(root).write(path, encoding="utf-8")
            self.assertEqual(reconcile_result(output, 0, path, expected), (expected, 1, 1))
            with self.assertRaises(RunFailure):
                reconcile_result("Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1", 0, path, expected)
            results[0].set("testName", "Twig.Cli.Tests.Commands.Rows.Run(value: 2)")
            ET.ElementTree(root).write(path, encoding="utf-8")
            with self.assertRaises(RunFailure):
                reconcile_result(output, 0, path, expected)
            results[0].set("testName", next(iter(expected)))
            counters.set("aborted", "1")
            ET.ElementTree(root).write(path, encoding="utf-8")
            with self.assertRaises(RunFailure):
                reconcile_result(output, 0, path, expected)


class WindowsCleanupProcessTreeSelfTest(unittest.TestCase):
    """Opt-in Windows process-tree control; ordinary --selftest remains synthetic-only."""

    @unittest.skipUnless(os.name == "nt", "real process-tree control requires Windows")
    def test_taskkill_stops_owned_tree_and_preserves_challenged_sentinel(self):
        api = _WindowsProcessApi()
        cleanup_deadline = time.monotonic() + WINDOWS_TREE_OUTER_TIMEOUT
        startup_deadline = time.monotonic() + WINDOWS_TREE_STARTUP_TIMEOUT
        root = None
        root_handle = None
        sentinel = None
        owned = []
        process_roles = {}
        sentinel_handle = None
        readers = []
        primary_error = None
        cleanup_errors = []
        taskkill_attempted = False
        child_spawn_requested = False
        grandchild_spawn_requested = False
        child_handle = None
        grandchild_handle = None
        child_identity = None
        grandchild_identity = None
        final_handle_states = []

        try:
            root = subprocess.Popen(
                [sys.executable, "-c", WINDOWS_TREE_WORKER, "root", WINDOWS_TREE_WORKER],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            root_handle = api.open_from_popen(root)
            owned.append(root_handle)
            process_roles[root_handle.pid] = "root"
            root_lines = queue.Queue()
            root_reader = threading.Thread(target=_pump_lines, args=(root.stdout, root_lines), daemon=True)
            root_reader.start()
            readers.append((root_reader, root.stdout))
            root_record = _read_worker_record(root_lines, "root", startup_deadline)
            require(root_record[1] == root_handle.pid and root_record[2] == root_handle.creation_time,
                    "root READY identity did not match the immediately retained Popen handle")
            _fixture_evidence("identity", f"role={root_record[0]} pid={root_record[1]} creation={root_record[2]}")

            child_spawn_requested = True
            root.stdin.write("spawn-child\n")
            root.stdin.flush()
            child_pid, child_creation = _read_child_pid(root_lines, startup_deadline)
            child_identity = (child_pid, child_creation)
            child_handle = api.open_verified(child_pid, child_creation)
            owned.append(child_handle)
            process_roles[child_handle.pid] = "child"
            root.stdin.write("announce-child\n")
            root.stdin.flush()
            child_record = _read_worker_record(root_lines, "child", startup_deadline)
            require(child_record[1:] == (child_handle.pid, child_handle.creation_time),
                    "child READY identity did not match its immediately retained parent-reported handle")
            _fixture_evidence("identity", f"role={child_record[0]} pid={child_record[1]} creation={child_record[2]}")

            grandchild_spawn_requested = True
            root.stdin.write("spawn-grandchild\n")
            root.stdin.flush()
            grandchild_pid, grandchild_creation = _read_child_pid(root_lines, startup_deadline)
            grandchild_identity = (grandchild_pid, grandchild_creation)
            grandchild_handle = api.open_verified(grandchild_pid, grandchild_creation)
            owned.append(grandchild_handle)
            process_roles[grandchild_handle.pid] = "grandchild"
            root.stdin.write("announce-grandchild\n")
            root.stdin.flush()
            grandchild_record = _read_worker_record(root_lines, "grandchild", startup_deadline)
            require(grandchild_record[1:] == (grandchild_handle.pid, grandchild_handle.creation_time),
                    "grandchild READY identity did not match its immediately retained parent-reported handle")
            _fixture_evidence(
                "identity",
                f"role={grandchild_record[0]} pid={grandchild_record[1]} creation={grandchild_record[2]}",
            )

            sentinel = subprocess.Popen(
                [sys.executable, "-c", WINDOWS_SENTINEL_WORKER],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            sentinel_handle = api.open_from_popen(sentinel)
            process_roles[sentinel_handle.pid] = "sentinel"
            sentinel_lines = queue.Queue()
            sentinel_reader = threading.Thread(target=_pump_lines, args=(sentinel.stdout, sentinel_lines), daemon=True)
            sentinel_reader.start()
            readers.append((sentinel_reader, sentinel.stdout))
            sentinel_record = _read_worker_record(sentinel_lines, "sentinel", startup_deadline)
            require(sentinel_record[1:] == (sentinel_handle.pid, sentinel_handle.creation_time),
                    "sentinel READY identity did not match the immediately retained Popen handle")
            _fixture_evidence(
                "identity",
                f"role={sentinel_record[0]} pid={sentinel_record[1]} creation={sentinel_record[2]}",
            )

            pids = [record[1] for record in (*[root_record, child_record, grandchild_record], sentinel_record)]
            require(len(set(pids)) == 4, f"fixture PIDs are not distinct: {pids}")
            require(
                all(api.is_running(handle) for handle in owned) and api.is_running(sentinel_handle),
                "not all four identity-validated process handles were alive immediately before taskkill",
            )
            pre_taskkill = [(process_roles[handle.pid], handle, api.is_running(handle)) for handle in [*owned, sentinel_handle]]
            _fixture_evidence(
                "pre-taskkill",
                *(f"role={role} pid={handle.pid} creation={handle.creation_time} alive={alive}"
                  for role, handle, alive in pre_taskkill),
            )
            require(time.monotonic() + 20 < cleanup_deadline, "fixture startup left insufficient bounded cleanup time")

            taskkill_attempted = True
            _stop_with_evidence(root, "primary")
            for handle in owned:
                require(api.wait(handle, _remaining_ms(cleanup_deadline)),
                        f"owned PID {handle.pid} did not exit before the outer deadline")
            _fixture_evidence(
                "post-taskkill",
                *(f"role={process_roles[handle.pid]} pid={handle.pid} creation={handle.creation_time} signaled={not api.is_running(handle)}"
                  for handle in owned),
            )
            require(api.is_running(sentinel_handle), "unrelated sentinel exited during taskkill")

            challenge = f"challenge-{time.monotonic_ns()}"
            sentinel.stdin.write(challenge + "\n")
            sentinel.stdin.flush()
            response = _read_line(sentinel_lines, cleanup_deadline)
            expected_response = f"PONG {challenge} {sentinel_record[1]} {sentinel_record[2]}"
            require(response == expected_response, f"sentinel challenge response mismatch: {response!r}")
            require(api.is_running(sentinel_handle), "sentinel exited after answering the post-cleanup challenge")
            _fixture_evidence("sentinel-challenge", response)
        except Exception as error:
            primary_error = error
        finally:
            if child_spawn_requested and child_handle is None:
                cleanup_errors.append("child was requested but its creation-identity-validated handle was not retained")
            if grandchild_spawn_requested and grandchild_handle is None:
                cleanup_errors.append("grandchild was requested but its creation-identity-validated handle was not retained")
            if root is not None and not taskkill_attempted and child_spawn_requested and root_handle is not None:
                try:
                    if root.poll() is None and time.monotonic() + 20 < cleanup_deadline:
                        _stop_with_evidence(root, "incomplete-tree-cleanup")
                    elif root.poll() is None:
                        cleanup_errors.append("could not safely bound taskkill cleanup for an incompletely identified tree")
                    else:
                        cleanup_errors.append("root exited before the incomplete process tree was fully confirmed")
                except Exception as error:
                    cleanup_errors.append(f"could not stop incompletely identified tree: {error}")
            elif root is not None and root_handle is None and root.poll() is None:
                try:
                    root.terminate()
                    root.wait(timeout=min(2, max(0.001, cleanup_deadline - time.monotonic())))
                except Exception as error:
                    cleanup_errors.append(f"could not terminate exact Popen root handle PID {root.pid}: {error}")

            if sentinel is not None and sentinel_handle is None and sentinel.poll() is None:
                try:
                    sentinel.terminate()
                    sentinel.wait(timeout=min(2, max(0.001, cleanup_deadline - time.monotonic())))
                except Exception as error:
                    cleanup_errors.append(f"could not terminate exact Popen sentinel handle PID {sentinel.pid}: {error}")

            for handle in owned:
                try:
                    if api.is_running(handle):
                        api.terminate(handle)
                except Exception as error:
                    cleanup_errors.append(f"could not terminate owned PID {handle.pid} by retained handle: {error}")
            if sentinel_handle is not None:
                try:
                    if api.is_running(sentinel_handle):
                        api.terminate(sentinel_handle)
                except Exception as error:
                    cleanup_errors.append(f"could not terminate sentinel PID {sentinel_handle.pid} by retained handle: {error}")

            for handle in [*owned, *([sentinel_handle] if sentinel_handle is not None else [])]:
                try:
                    running = api.is_running(handle)
                except Exception as error:
                    cleanup_errors.append(f"could not verify termination for PID {handle.pid}: {error}")
                    final_handle_states.append((process_roles[handle.pid], handle.pid, handle.creation_time, None))
                    continue
                if running:
                    remaining = _remaining_ms(cleanup_deadline)
                    try:
                        stopped = remaining > 0 and api.wait(handle, min(2000, remaining))
                    except Exception as error:
                        cleanup_errors.append(f"could not wait for PID {handle.pid}: {error}")
                    else:
                        if not stopped:
                            cleanup_errors.append(f"process termination unconfirmed for PID {handle.pid}")
                try:
                    final_handle_states.append(
                        (process_roles[handle.pid], handle.pid, handle.creation_time, api.is_running(handle))
                    )
                except Exception as error:
                    cleanup_errors.append(f"could not verify final state for PID {handle.pid}: {error}")
                    final_handle_states.append((process_roles[handle.pid], handle.pid, handle.creation_time, None))

            for process in (root, sentinel):
                if process is not None:
                    remaining = cleanup_deadline - time.monotonic()
                    if remaining > 0:
                        try:
                            process.wait(timeout=min(2, remaining))
                        except (subprocess.TimeoutExpired, OSError) as error:
                            cleanup_errors.append(f"could not reap fixture wrapper PID {process.pid}: {error}")
                    elif process.poll() is None:
                        cleanup_errors.append(f"fixture wrapper PID {process.pid} remained alive at the outer deadline")
                    if process.stdin is not None:
                        try:
                            process.stdin.close()
                        except OSError as error:
                            cleanup_errors.append(f"could not close fixture input pipe for PID {process.pid}: {error}")
                    if process is root and root_handle is None or process is sentinel and sentinel_handle is None:
                        role = "root" if process is root else "sentinel"
                        try:
                            creation = api._creation_time(process._handle)
                        except Exception:
                            creation = None
                        final_handle_states.append((role, process.pid, creation, process.poll() is None))
            for reader, _ in readers:
                reader.join(timeout=min(0.2, max(0, cleanup_deadline - time.monotonic())))
                if reader.is_alive():
                    cleanup_errors.append("fixture output reader did not stop within 200ms")
            for reader, stream in readers:
                if not reader.is_alive():
                    try:
                        stream.close()
                    except OSError as error:
                        cleanup_errors.append(f"could not close fixture output pipe: {error}")
            for handle in [*owned, *([sentinel_handle] if sentinel_handle is not None else [])]:
                try:
                    api.close(handle)
                except OSError as error:
                    cleanup_errors.append(f"could not close process handle for PID {handle.pid}: {error}")

            if child_spawn_requested and child_handle is None:
                final_handle_states.append(
                    ("child", child_identity[0] if child_identity else None,
                     child_identity[1] if child_identity else None, None)
                )
            if grandchild_spawn_requested and grandchild_handle is None:
                final_handle_states.append(
                    ("grandchild", grandchild_identity[0] if grandchild_identity else None,
                     grandchild_identity[1] if grandchild_identity else None, None)
                )
            _fixture_evidence(
                "final-cleanup",
                *(
                    f"role={role} pid={pid if pid is not None else 'unrecorded'} "
                    f"creation={creation if creation is not None else 'unrecorded'} "
                    f"state={'unknown' if state is None else ('alive' if state else 'signaled')}"
                    for role, pid, creation, state in final_handle_states
                ),
            )

        if primary_error is not None or cleanup_errors:
            details = []
            if primary_error is not None:
                details.append(f"primary failure: {primary_error}")
            if cleanup_errors:
                details.append("cleanup failures: " + " | ".join(cleanup_errors))
            raise RunFailure("; ".join(details)) from primary_error


class _WindowsProcessApi:
    PROCESS_TERMINATE = 0x0001
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    SYNCHRONIZE = 0x00100000
    WAIT_OBJECT_0 = 0
    WAIT_TIMEOUT = 258

    def __init__(self):
        from ctypes import wintypes

        class FILETIME(ctypes.Structure):
            _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

        self.FILETIME = FILETIME
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.GetProcessTimes.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
        ]
        self.kernel.GetProcessTimes.restype = wintypes.BOOL
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.WaitForSingleObject.restype = wintypes.DWORD
        self.kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.kernel.TerminateProcess.restype = wintypes.BOOL
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL

    def open_verified(self, pid, expected_creation):
        handle = self.kernel.OpenProcess(
            self.PROCESS_TERMINATE | self.PROCESS_QUERY_LIMITED_INFORMATION | self.SYNCHRONIZE,
            False,
            pid,
        )
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            created = self.FILETIME()
            exited = self.FILETIME()
            kernel_seconds = self.FILETIME()
            user_seconds = self.FILETIME()
            if not self.kernel.GetProcessTimes(
                handle,
                ctypes.byref(created),
                ctypes.byref(exited),
                ctypes.byref(kernel_seconds),
                ctypes.byref(user_seconds),
            ):
                raise ctypes.WinError(ctypes.get_last_error())
            actual_creation = (created.high << 32) | created.low
            require(actual_creation == expected_creation, f"PID {pid} creation identity changed")
            return _WindowsProcessHandle(pid, actual_creation, handle)
        except Exception:
            self.kernel.CloseHandle(handle)
            raise

    def open_from_popen(self, process):
        creation = self._creation_time(process._handle)
        return self.open_verified(process.pid, creation)

    def _creation_time(self, handle):
        created = self.FILETIME()
        exited = self.FILETIME()
        kernel_seconds = self.FILETIME()
        user_seconds = self.FILETIME()
        if not self.kernel.GetProcessTimes(
            handle,
            ctypes.byref(created),
            ctypes.byref(exited),
            ctypes.byref(kernel_seconds),
            ctypes.byref(user_seconds),
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return (created.high << 32) | created.low

    def is_running(self, handle):
        result = self.kernel.WaitForSingleObject(handle.handle, 0)
        if result == self.WAIT_TIMEOUT:
            return True
        if result == self.WAIT_OBJECT_0:
            return False
        raise ctypes.WinError(ctypes.get_last_error())

    def wait(self, handle, timeout_ms):
        result = self.kernel.WaitForSingleObject(handle.handle, timeout_ms)
        if result == self.WAIT_OBJECT_0:
            return True
        if result == self.WAIT_TIMEOUT:
            return False
        raise ctypes.WinError(ctypes.get_last_error())

    def terminate(self, handle):
        if not self.kernel.TerminateProcess(handle.handle, 1) and self.is_running(handle):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self, handle):
        if not self.kernel.CloseHandle(handle.handle):
            raise ctypes.WinError(ctypes.get_last_error())


@dataclass(frozen=True)
class _WindowsProcessHandle:
    pid: int
    creation_time: int
    handle: object


def _pump_lines(stream, destination):
    for line in stream:
        destination.put(line.rstrip("\r\n"))


def _read_line(lines, deadline):
    remaining = deadline - time.monotonic()
    require(remaining > 0, "fixture event exceeded its bounded deadline")
    try:
        return lines.get(timeout=remaining)
    except queue.Empty as error:
        raise RunFailure("fixture event exceeded its bounded deadline") from error


def _read_worker_record(lines, expected_role, deadline):
    line = _read_line(lines, deadline)
    parts = line.split()
    require(
        len(parts) == 4 and parts[0] == "READY" and parts[1] == expected_role,
        f"unexpected fixture output while waiting for {expected_role}: {line!r}",
    )
    try:
        return parts[1], int(parts[2]), int(parts[3])
    except ValueError as error:
        raise RunFailure(f"invalid fixture process identity: {line!r}") from error


def _read_child_pid(lines, deadline):
    line = _read_line(lines, deadline)
    parts = line.split()
    require(len(parts) == 3 and parts[0] == "CHILD",
            f"parent did not report child PID and creation identity before readiness: {line!r}")
    try:
        return int(parts[1]), int(parts[2])
    except ValueError as error:
        raise RunFailure(f"invalid child PID report: {line!r}") from error


def _remaining_ms(deadline):
    return max(0, int((deadline - time.monotonic()) * 1000))


def _fixture_evidence(phase, *details):
    suffix = " ".join(details)
    print(f"TWIG-CLEANUP-EVIDENCE {phase}" + (f" {suffix}" if suffix else ""), flush=True)


def _stop_with_evidence(process, phase):
    run = subprocess.run

    def recorded_run(command, *args, **kwargs):
        if command and Path(str(command[0])).name.casefold() == "taskkill":
            try:
                result = run(command, *args, **kwargs)
            except subprocess.TimeoutExpired as error:
                _fixture_evidence(
                    "taskkill",
                    f"phase={phase}",
                    "result=timeout",
                    f"stdout={_cleanup_output(error.stdout)!r}",
                    f"stderr={_cleanup_output(error.stderr)!r}",
                )
                raise
            except OSError as error:
                _fixture_evidence("taskkill", f"phase={phase}", f"result=oserror:{error!r}")
                raise
            _fixture_evidence(
                "taskkill",
                f"phase={phase}",
                f"returncode={result.returncode}",
                f"stdout={_cleanup_output(result.stdout)!r}",
                f"stderr={_cleanup_output(result.stderr)!r}",
            )
            return result
        return run(command, *args, **kwargs)

    with patch.object(subprocess, "run", side_effect=recorded_run):
        return stop_session(process, False)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selftest", action="store_true", help="check coverage and false-green guards with synthetic data only")
    parser.add_argument("--selftest-windows-cleanup", action="store_true", help="opt-in real Windows process-tree cleanup control")
    args = parser.parse_args()
    if args.selftest and args.selftest_windows_cleanup:
        parser.error("--selftest and --selftest-windows-cleanup are separate selectors")
    if args.selftest:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RunnerSelfTests))
        return int(not result.wasSuccessful())
    if args.selftest_windows_cleanup:
        if os.name != "nt":
            parser.error("--selftest-windows-cleanup requires Windows")
        result = unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(WindowsCleanupProcessTreeSelfTest)
        )
        return int(not result.wasSuccessful())
    try:
        return Runner().run()
    except (RunFailure, OSError, ET.ParseError, ValueError) as error:
        print(f"TWIG-VERDICT OVERALL: FAILED ({error})")
        return 1


if __name__ == "__main__":
    sys.exit(main())
