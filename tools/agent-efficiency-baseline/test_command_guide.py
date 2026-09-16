"""Safety boundaries for the source-index guidance adapter (no installed Twig)."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("command_guide", Path(__file__).with_name("command-guide.py"))
guide = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guide)


class CommandGuideTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.workspace = Path(self.temporary.name)
        (self.workspace / "twig.json").write_text('{"organization":"fixture.invalid","project":"Fixture"}')

    def test_unknown_command_never_executes_or_browses_catalog(self):
        with patch.object(guide.subprocess, "run") as execute:
            with self.assertRaises(ValueError):
                guide.guide("not-a-command", "/missing/twig", workspace=self.workspace)
            execute.assert_not_called()

    def test_executable_failure_preserves_exit_and_both_streams(self):
        with tempfile.NamedTemporaryFile() as executable:
            failure = subprocess.CompletedProcess([], 9, "partial result", '{"error":"denied"}')
            with patch.object(guide.subprocess, "run", return_value=failure):
                with self.assertRaisesRegex(RuntimeError, '"exitCode": 9') as result:
                    guide.guide("show", executable.name, workspace=self.workspace)
                self.assertIn("partial result", str(result.exception))
                self.assertIn("denied", str(result.exception))

    def test_changed_executable_cannot_be_reused_as_qualified_guidance(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "twig"
            executable.write_bytes(b"before")
            def execute(*args, **kwargs):
                executable.write_bytes(b"after")
                return subprocess.CompletedProcess([], 0, "help", "")
            with patch.object(guide.subprocess, "run", side_effect=execute):
                with self.assertRaisesRegex(RuntimeError, "Executable changed"):
                    guide.guide("show", str(executable), workspace=self.workspace)


if __name__ == "__main__":
    unittest.main()
