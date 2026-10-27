#!/usr/bin/env python3
"""Run a 60-second bounded dotnet regression and verify every filter selector.

Usage: python tools/verify-trx.py -- dotnet test ... --filter ... --logger "trx;LogFileName=..." --results-directory ...
       python tools/verify-trx.py --selftest

The existing command supplies the filter and TRX location; no separate test census
is maintained. Supported filters use FullyQualifiedName or DisplayName, = or ~,
and | / & without parentheses. Unsupported expressions fail before execution.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET


NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def parse_filter(selector):
    groups = []
    for group in selector.split("|"):
        terms = []
        for term in group.split("&"):
            match = re.fullmatch(r"(FullyQualifiedName|DisplayName)([=~])([^()!&|=~]+)", term)
            require(match is not None, f"unsupported selector: {term!r}")
            terms.append(match.groups())
        groups.append(terms)
    return groups


def verify(root, selector):
    groups = parse_filter(selector)
    results = root.findall(".//t:UnitTestResult", NS)
    definitions = root.findall(".//t:UnitTest", NS)
    summary = root.find("t:ResultSummary", NS)
    require(summary is not None and summary.get("outcome") == "Completed", "run did not complete")
    require(bool(results), "TRX contains no executed results")
    require(all(r.get("outcome") == "Passed" for r in results), "every result must be Passed")
    executions = [r.get("executionId") for r in results]
    require(all(executions) and len(set(executions)) == len(results), "missing or duplicate execution IDs")
    ids = [d.get("id") for d in definitions]
    require(all(ids) and len(set(ids)) == len(definitions), "missing or duplicate definition IDs")
    require(set(ids) == {r.get("testId") for r in results}, "definitions and executed results differ")
    counters = summary.find("t:Counters", NS)
    require(counters is not None, "missing run counters")
    for key in ("total", "executed", "passed"):
        require(counters.get(key) == str(len(results)), f"{key} counter disagrees with executed results")
    for key in ("failed", "error", "timeout", "aborted", "notExecuted", "passedButRunAborted"):
        require(counters.get(key) == "0", f"{key} counter is missing or nonzero")
    methods = {}
    for definition in definitions:
        method = definition.find("t:TestMethod", NS)
        require(method is not None and method.get("className") and method.get("name"),
                "definition lacks class/method identity")
        methods[definition.get("id")] = f'{method.get("className")}.{method.get("name")}'

    covered = set()
    unexpected = []
    for result in results:
        values = {"FullyQualifiedName": methods[result.get("testId")], "DisplayName": result.get("testName", "")}
        matched = {
            index for index, terms in enumerate(groups)
            if all(values[key].casefold() == value.casefold() if operator == "="
                   else value.casefold() in values[key].casefold()
                   for key, operator, value in terms)
        }
        if not matched:
            unexpected.append(result.get("testName"))
        covered.update(matched)
    missing = [selector.split("|")[index] for index in range(len(groups)) if index not in covered]
    require(not missing, f"selectors matched no executed cases: {', '.join(missing)}")
    require(not unexpected, f"unexpected executed tests: {', '.join(unexpected)}")
    return len(results)


def option(command, name, prefix):
    values = [command[i + 1] for i, arg in enumerate(command[:-1]) if arg == name]
    values += [arg[len(prefix):] for arg in command if arg.startswith(prefix)]
    require(len(values) == 1, f"provide exactly one {name} or {prefix} option")
    return values[0]


def run(command):
    require(command[:2] in (["dotnet", "test"], ["dotnet", "vstest"]), "expected dotnet test or dotnet vstest")
    selector = option(command, "--filter", "/TestCaseFilter:")
    parse_filter(selector)
    logger = option(command, "--logger", "/Logger:")
    match = re.fullmatch(r"trx;LogFileName=([^/\\]+\.trx)", logger)
    require(match is not None, "expected trx;LogFileName=<name>.trx logger")
    path = Path(option(command, "--results-directory", "/ResultsDirectory:")) / match.group(1)
    path.unlink(missing_ok=True)
    code = subprocess.call([sys.executable, str(Path(__file__).with_name("run-bounded.py")),
                            "--timeout", "60", "--", *command])
    if code:
        return code
    count = verify(ET.parse(path).getroot(), selector)
    print(f"TRX-VERDICT: PASSED ({count} executed cases; every selector covered)", flush=True)
    return 0


class VerifyTrxTests(unittest.TestCase):
    def fixture(self):
        root = ET.Element(f"{{{NS['t']}}}TestRun")
        definitions = ET.SubElement(root, f"{{{NS['t']}}}TestDefinitions")
        results = ET.SubElement(root, f"{{{NS['t']}}}Results")
        for index, (method, display) in enumerate((("First", "First(one)"), ("First", "First(two)"),
                                                  ("Second", "Second"))):
            definition = ET.SubElement(definitions, f"{{{NS['t']}}}UnitTest", id=str(index))
            ET.SubElement(definition, f"{{{NS['t']}}}TestMethod", className="Tests.Sample", name=method)
            ET.SubElement(results, f"{{{NS['t']}}}UnitTestResult", testId=str(index),
                          executionId=f"execution-{index}", testName=display, outcome="Passed")
        summary = ET.SubElement(root, f"{{{NS['t']}}}ResultSummary", outcome="Completed")
        ET.SubElement(summary, f"{{{NS['t']}}}Counters", total="3", executed="3", passed="3",
                      failed="0", error="0", timeout="0", aborted="0", notExecuted="0",
                      passedButRunAborted="0")
        return root

    def test_positive_class_methods_and_theory_display_selectors(self):
        root = self.fixture()
        for selector in ("FullyQualifiedName~Tests.Sample",
                         "FullyQualifiedName=Tests.Sample.First|FullyQualifiedName~Sample.Second",
                         "FullyQualifiedName~First&DisplayName~one|FullyQualifiedName~First&DisplayName~two|FullyQualifiedName~Second"):
            with self.subTest(selector=selector):
                self.assertEqual(3, verify(root, selector))

    def test_renamed_or_missing_selector_is_not_masked_by_other_matches(self):
        for missing in ("FullyQualifiedName~Tests.Renamed",
                        "FullyQualifiedName=Tests.Sample.RenamedMethod",
                        "FullyQualifiedName~Sample&DisplayName~Missing"):
            with self.subTest(selector=missing):
                with self.assertRaisesRegex(ValueError, "selectors matched no executed cases") as error:
                    verify(self.fixture(), f"FullyQualifiedName~Sample|{missing}")
                self.assertIn(missing, str(error.exception))

    def test_failed_result_is_rejected_even_with_green_counters(self):
        root = self.fixture()
        root.find(".//t:UnitTestResult", NS).set("outcome", "Failed")
        with self.assertRaisesRegex(ValueError, "every result must be Passed"):
            verify(root, "FullyQualifiedName~Sample")

    def test_incomplete_duplicate_missing_and_inconsistent_evidence_is_rejected(self):
        mutations = [
            (".//t:ResultSummary", "outcome", "Aborted", "run did not complete"),
            (".//t:UnitTestResult", "executionId", "execution-1", "execution IDs"),
            (".//t:UnitTestResult", "testId", "absent", "definitions and executed"),
            (".//t:Counters", "total", "4", "total counter"),
            (".//t:Counters", "aborted", "1", "aborted counter"),
            (".//t:Counters", "notExecuted", "1", "notExecuted counter"),
        ]
        for path, key, value, message in mutations:
            root = self.fixture()
            root.find(path, NS).set(key, value)
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, message):
                verify(root, "FullyQualifiedName~Sample")

    def test_empty_and_unsupported_filters_are_rejected(self):
        for selector in ("", "FullyQualifiedName!~Sample", "(FullyQualifiedName~Sample)"):
            with self.subTest(selector=selector), self.assertRaisesRegex(ValueError, "unsupported selector"):
                parse_filter(selector)

    def test_empty_run_is_not_a_pass(self):
        root = self.fixture()
        root.find("t:Results", NS).clear()
        with self.assertRaisesRegex(ValueError, "no executed results"):
            verify(root, "FullyQualifiedName~Sample")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.selftest:
        require(not args.command, "--selftest takes no command")
        result = unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(VerifyTrxTests))
        return 0 if result.wasSuccessful() else 1
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    try:
        return run(command)
    except (ValueError, OSError, ET.ParseError) as error:
        print(f"TRX-VERDICT: FAILED ({error})", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
