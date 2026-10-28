"""Offline platform suspension regressions; no network or installed Twig needed."""
import os
import contextlib
import importlib.util
import io
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
BASH = r"C:\Program Files\Git\bin\bash.exe" if os.name == "nt" else shutil.which("bash")


class PlatformSupportTests(unittest.TestCase):
    def test_linux_installer_still_installs_exact_native_aliases(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "release.tar.gz"
            image = b"#!/usr/bin/env bash\necho 1.0.0\n"
            with tarfile.open(archive, "w:gz") as payload:
                for name in ("twig", "twig-tui", "twig-bench-tui"):
                    info = tarfile.TarInfo(name)
                    info.size, info.mode = len(image), 0o755
                    payload.addfile(info, io.BytesIO(image))
            script = """
uname() { if [ "$1" = "-s" ]; then echo Linux; else echo x86_64; fi; }
curl() {
    while [ "$#" -gt 0 ]; do
        if [ "$1" = "-o" ]; then cp "$TEST_ARCHIVE" "$2"; return; fi
        shift
    done
    echo '{"tag_name":"v1.0.0","assets":[{"browser_download_url":"https://example.invalid/twig-linux-x64.tar.gz"}]}'
}
export -f uname curl
source ./install.sh
"""
            result = subprocess.run([BASH, "-c", script], cwd=ROOT,
                                    env={**os.environ, "HOME": temporary, "TEST_ARCHIVE": archive.as_posix()},
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("twig installed successfully!", result.stdout)
            for name in ("twig", "twig-tui", "twig-bench-tui"):
                self.assertEqual((root / ".twig" / "bin" / name).read_bytes(), image)

    def test_candidate_tool_refuses_macos_before_arguments_or_files(self):
        spec = importlib.util.spec_from_file_location("candidate", ROOT / "tools" / "tui-candidate.py")
        candidate = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(candidate)
        self.assertEqual(set(candidate.RIDS), {"win-x64", "linux-x64"})
        with patch.object(candidate.platform, "system", return_value="Darwin"), \
                patch.object(candidate, "build", side_effect=AssertionError("unexpected build")), \
                contextlib.redirect_stderr(io.StringIO()) as stderr:
            self.assertEqual(candidate.main(), 1)
            self.assertIn("macOS support is temporarily suspended", stderr.getvalue())

    def test_macos_installer_refuses_both_architectures_before_network_or_writes(self):
        for arch in ("arm64", "x86_64"):
            with self.subTest(arch=arch), tempfile.TemporaryDirectory() as temporary:
                # Exported functions stub only the external OS/network boundaries.
                script = """
uname() { if [ "$1" = "-s" ]; then echo Darwin; else echo "$TEST_ARCH"; fi; }
curl() { echo unexpected-network >&2; return 99; }
export -f uname curl
source ./install.sh
"""
                result = subprocess.run([BASH, "-c", script], cwd=ROOT,
                                        env={**os.environ, "HOME": temporary, "TEST_ARCH": arch},
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("macOS support is temporarily suspended", result.stderr)
                self.assertNotIn("unexpected-network", result.stderr)
                self.assertFalse((Path(temporary) / ".twig").exists())

    def test_active_workflows_never_qualify_or_publish_macos(self):
        for name in ("ci.yml", "release.yml", "tui-candidate.yml"):
            with self.subTest(workflow=name):
                text = (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")
                self.assertNotIn("os: macos-", text)
                self.assertNotIn("rid: osx-", text)
                self.assertNotIn("./artifacts/twig-osx-", text)
                self.assertIn("rid: win-x64", text)
                self.assertIn("rid: linux-x64", text)


if __name__ == "__main__":
    unittest.main()
