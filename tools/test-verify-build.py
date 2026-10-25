"""Deterministic subprocess tests for optional smoke boundary diagnostics."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("smoke", Path(__file__).with_name("verify-build.py"))
smoke = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(smoke)


class SmokeTraceTests(unittest.TestCase):
    def test_partial_worker_trace_survives_supervisor_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / "trace.jsonl"
            source = (
                "import importlib.util,time;"
                f"s=importlib.util.spec_from_file_location('v',{str(Path(smoke.__file__))!r});"
                "v=importlib.util.module_from_spec(s);s.loader.exec_module(v);"
                f"v.SmokeTrace(v.Path({str(trace)!r})).write('version','start');"
                "time.sleep(60)"
            )
            result = smoke.run_worker([sys.executable, "-c", source], 1, smoke.SmokeTrace(trace))
            self.assertEqual(result, 1)
            self.assertTrue(trace.is_file(), "partial boundaries must survive a killed worker")
            rows = [smoke.json.loads(line) for line in trace.read_text().splitlines()]
            self.assertIn(("version", "start"), [(r["phase"], r["event"]) for r in rows])
            self.assertEqual(rows[-1]["event"], "timeout")

    def test_success_and_native_failure_without_trace_preserve_exit(self):
        for code in (0, 7):
            with self.subTest(code=code):
                result = smoke.run_worker(
                    [sys.executable, "-c", f"print('worker-output');raise SystemExit({code})"],
                    5, smoke.SmokeTrace(None))
                self.assertEqual(result, code)

    def test_trace_schema_has_only_allowlisted_labels_and_numbers(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.jsonl"
            trace = smoke.SmokeTrace(path)
            trace.write("preview", "end", 4)
            row = smoke.json.loads(path.read_text())
            self.assertEqual(set(row), {"phase", "event", "elapsedSeconds", "exitCode"})
            with self.assertRaises(ValueError):
                trace.write("secret-command-argument", "start")


if __name__ == "__main__":
    unittest.main()
