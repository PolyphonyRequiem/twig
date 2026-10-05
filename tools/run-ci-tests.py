#!/usr/bin/env python3
"""Run already-built CI assemblies serially, with bounded CLI sessions and exact coverage.

Usage: python tools/run-ci-tests.py [--selftest]
The local run-tests.sh BinaryLauncher exclusion is deliberately NOT used here.
"""

import argparse
from collections import Counter
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent
ASSEMBLIES = ("Cli", "Domain", "Infrastructure", "RenderTree", "Tui")
HOST_CLASSES = {
    "Twig.Cli.Tests.Commands.ConnectionBindingTransitionConsumerTests",
    "Twig.Cli.Tests.Commands.ConnectionMigrateCommandTests",
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
    require(host_methods == expected_host and bool(host_methods), "HostInventory trait does not match both inventory classes")
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


def stop_session(process, elevated):
    # Kill only the tree launched by this runner; never leave a timed-out host beside the next partition.
    if os.name == "nt":
        result = subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
        require(result.returncode == 0 or process.poll() is not None, "could not stop timed-out test process tree")
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
            with subprocess.Popen(command, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT) as process:
                try:
                    exit_code = process.wait(timeout=SESSION_TIMEOUT / 1000)
                except subprocess.TimeoutExpired:
                    stop_session(process, elevated)
                    stream.write("\nTest Run Aborted: runner process deadline of 300000ms exceeded.\n")
                    exit_code = 1
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selftest", action="store_true", help="check coverage and false-green guards with synthetic data only")
    args = parser.parse_args()
    if args.selftest:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RunnerSelfTests))
        return int(not result.wasSuccessful())
    try:
        return Runner().run()
    except (RunFailure, OSError, ET.ParseError, ValueError) as error:
        print(f"TWIG-VERDICT OVERALL: FAILED ({error})")
        return 1


if __name__ == "__main__":
    sys.exit(main())
