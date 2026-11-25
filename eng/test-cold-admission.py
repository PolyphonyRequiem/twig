"""Negative admission for a candidate without a qualified installed selection."""
import argparse
import os
from pathlib import Path
import sys
import subprocess
import tempfile


def verify(cli, server):
    with tempfile.TemporaryDirectory(prefix="twig-cold-admission-") as directory:
        root = Path(directory)
        env = dict(os.environ, TWIG_USER_HOME=str(root / "home"),
                   TWIG_SERVER_INSTALL_ROOT=str(root / "install"))
        env.pop("TWIG_OWNER_FIXTURE_ID", None)
        env.pop("TWIG_TELEMETRY_ENDPOINT", None)
        for image, args in (
            (server, ["--server-command-worker"]),
            (server, ["--commands-only", "--data-dir", str(root / "data")]),
            (server, ["--data-dir", str(root / "data")]),
            (server, ["--ensure-running"]),
            (server, ["--stop"]),
            (cli, ["server", "start"]),
            (cli, ["bench", "create", "must-not-exist", "-o", "json"]),
        ):
            proc = subprocess.run([image, *args], input=b"", capture_output=True,
                                  cwd=root, env=env, timeout=5)
            assert proc.returncode == 2 and not proc.stdout and b"cold cutover" in proc.stderr, (
                args, proc.returncode, proc.stdout, proc.stderr)
            assert not list(root.iterdir()), "Refusal must precede selection, store and lease creation"
        tools = Path(__file__).resolve().parent
        if os.name == "nt":
            operators = [
                ["pwsh", "-NoProfile", "-File", tools / "server-install.ps1", "-InstallRoot", root / "install",
                 "-PrivateDataDir", root / "data", "-RegisterStartup", "-StartupDirectory", root / "startup"],
                ["pwsh", "-NoProfile", "-File", tools / "server-launch.ps1", "-InstallRoot", root / "install", "-EnsureRunning"],
            ]
        else:
            operators = [[sys.executable, tools / "server-linux.py", "install", root / "package", root / "data"]]
        for command in operators:
            result = subprocess.run(command, cwd=root, env=env, capture_output=True, timeout=10)
            assert result.returncode and b"cold cutover" in result.stderr, result.stderr
            assert not list(root.iterdir()), "The unqualified operator must not stage or promote anything"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    args = parser.parse_args()
    verify(args.cli.resolve(strict=True), args.server.resolve(strict=True))
    print("PASS: unqualified CLI, lifecycle, direct server and worker entries refuse before effects.")
