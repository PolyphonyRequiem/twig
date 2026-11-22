#!/usr/bin/env python3
"""Focused offline regression for the built CLI, owner-only pipe and isolated server workers."""
import json
import os
import argparse
from pathlib import Path
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "src/Twig/bin/Debug/net11.0/twig.dll"
SERVER = ROOT / "src/Twig.Server/bin/Debug/net11.0/Twig.Server.dll"


def verify(base, cli_dir=None, server_dir=None):
    extension = ".exe" if os.name == "nt" else ""
    client = [str(cli_dir / ("twig" + extension))] if cli_dir else ["dotnet", str(CLI)]
    host = [str(server_dir / ("Twig.Server" + extension))] if server_dir else ["dotnet", str(SERVER)]
    install, data, home, work = (base / name for name in ("install", "private", "home", "work"))
    install.mkdir(mode=0o700)
    work.mkdir()
    (install / "current.json").write_text(json.dumps({"privateDataDir": str(data)}), encoding="utf-8")
    env = dict(os.environ, TWIG_SERVER_INSTALL_ROOT=str(install), TWIG_USER_HOME=str(home))
    env.pop("TWIG_TELEMETRY_ENDPOINT", None)

    def cli(*args, expected=0, cwd=work, metadata=home, body=None):
        run = subprocess.run([*client, *args], cwd=cwd,
                             env=dict(env, TWIG_USER_HOME=str(metadata)), input=body,
                             capture_output=True, encoding="utf-8", timeout=10)
        if run.returncode != expected:
            raise RuntimeError(f"{args}: expected {expected}, got {run.returncode}: {run.stderr}")
        return run.stdout if expected == 0 else run.stderr

    assert "server start" in cli("--help")
    for action in (None, "start", "status", "stop", "endpoint"):
        command = ("server",) if action is None else ("server", action)
        for flag in ("-h", "--help"):
            assert "Usage: twig server <start|status|stop|endpoint>" in cli(*command, flag)
    assert "Usage: twig server" in cli("server", expected=2)
    assert "Usage: twig server" in json.loads(
        cli("server", "unknown", "-o", "json", expected=2))["error"]
    for action in ("start", "status", "stop", "endpoint"):
        error = json.loads(cli("server", action, "-o", "json", expected=1))["error"]
        assert "Usage:" not in error and "No command was dispatched" not in error, error
    assert not data.exists() and not home.exists() and not (work / ".twig").exists()

    unavailable = json.loads(
        cli("bench", "create", "unreachable", "-o", "json", expected=1))["error"]
    assert "No command was dispatched" in unavailable
    assert "twig server status" in unavailable and "twig server start" in unavailable
    assert not home.exists(), "Missing server must not open the native store"
    for args in (("init", "--provider", "twig", "--database", "qualification"), ("auth", "identities"),
                 ("connection", "status")):
        assert "No command was dispatched" in json.loads(cli(*args, "-o", "json", expected=1))["error"]
    assert not home.exists() and not (work / ".twig").exists()
    (install / "current.json").write_text('{"privateDataDir":"relative"}', encoding="utf-8")
    assert "No command was dispatched" in json.loads(cli("bench", "list", "-o", "json", expected=1))["error"]
    (install / "current.json").write_text(json.dumps({"privateDataDir": str(data)}), encoding="utf-8")
    with subprocess.Popen([*host, "--commands-only", "--data-dir", str(data)],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env) as server:
        try:
            assert server.stdout.readline().strip() == "COMMANDS_READY"
            cli("auth", "identities", "-o", "json")
            assert "No PAT provided" in json.loads(cli("auth", "pat", "--identity", "test",
                "--org", "qualification", "--stdin", "-o", "json", expected=1, body="\n"))["error"]
            created = json.loads(cli("bench", "create", "Boundary", "-o", "json"))
            assert created["id"] in cli("bench", "list", "-o", "ids")
            assert "metadata home" in json.loads(cli("bench", "list", "-o", "json",
                                                   metadata=base / "other", expected=2))["error"]
            subprocess.run(["git", "init", "--quiet"], cwd=work, check=True)
            cli("init", "--provider", "twig", "--database", "qualification", "-o", "json")
            nested = work / "nested"
            nested.mkdir()
            cli("process", "description", "--out", "schema.json", "-o", "json", cwd=nested)
            assert json.loads((nested / "schema.json").read_text(encoding="utf-8"))["scope"]
            seed = json.loads(cli("seed", "new", "--type", "Twig.Item", "--title", "Seed",
                                  "--no-parent", "-o", "json", cwd=nested))
            assert seed["localId"] in cli("show", "--local-id", seed["localId"], "-o", "json", cwd=nested)
            update = json.loads(cli("update", "title", "--id", str(seed["id"]), "--stdin", "-o", "json",
                                    body="Quoted \"title\" through stdin\n", cwd=nested))
            assert update["valueDisplay"] == 'Quoted "title" through stdin' and update["journalStatus"] == "Draft"
            assert "explicit --id" in json.loads(cli("update", "Twig.Title", "bad", "-o", "json",
                                                     expected=2))["error"]
        finally:
            if server.poll() is None:
                try:
                    server.communicate("stop\n", timeout=5)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()
                    raise
        assert server.returncode == 0
    print("PASSED: unavailable server, shared Bench, metadata isolation, nested workspace, seed, targeting")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-dir", type=Path, help="Verify self-contained published Linux or Windows apphosts")
    parser.add_argument("--cli-dir", type=Path, help="Independently published CLI directory")
    parser.add_argument("--server-dir", type=Path, help="Independently published server runtime directory")
    args = parser.parse_args()
    if args.app_dir and (args.cli_dir or args.server_dir):
        parser.error("--app-dir cannot be combined with separate distribution directories")
    if bool(args.cli_dir) != bool(args.server_dir):
        parser.error("--cli-dir and --server-dir must be supplied together")
    cli_dir = args.cli_dir or args.app_dir
    server_dir = args.server_dir or args.app_dir
    if args.cli_dir:
        assert cli_dir.resolve() != server_dir.resolve(), "CLI and server must be independently distributed"
        assert not (server_dir / ("twig.exe" if os.name == "nt" else "twig")).exists()
        assert not (cli_dir / ("Twig.Server.exe" if os.name == "nt" else "Twig.Server")).exists()
    with tempfile.TemporaryDirectory(prefix="twig-command-test-", dir=os.getenv("LOCALAPPDATA")
                                     if os.name == "nt" else None) as directory:
        verify(Path(directory), cli_dir.resolve() if cli_dir else None, server_dir.resolve() if server_dir else None)
