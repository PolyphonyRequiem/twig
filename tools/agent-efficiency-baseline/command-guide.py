#!/usr/bin/env python3
"""Select existing command-index metadata and executable help before tool delivery.

No persistent cache: consumers may reuse the result only while executableSha256,
indexSha256 and contractVersion match. The executable supplies syntax; the index
supplies mutation classification and the full documentation route.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys


def guide(command, executable, source_dll=None, workspace=None):
    root = Path(__file__).resolve().parents[2]
    index_path = root / "docs/commands/index.json"
    index_bytes = index_path.read_bytes()
    rows = [row for row in json.loads(index_bytes) if row["command"] == command]
    if len(rows) != 1:
        raise ValueError(f"Unknown or ambiguous command '{command}'. Select an exact command from docs/commands/index.json; no catalog was executed.")
    workspace_root = Path(workspace or Path.cwd()).resolve(strict=True)
    config_bytes = (workspace_root / "twig.json").read_bytes()
    config = json.loads(config_bytes)
    connection = {key: config.get(key) for key in ("organization", "project")}
    if not all(isinstance(value, str) and value.strip() for value in connection.values()):
        raise ValueError("Workspace twig.json must bind organization and project.")
    resolved = Path(source_dll).resolve(strict=True) if source_dll else Path(shutil.which(executable) or executable).resolve(strict=True)
    argv = ["dotnet", str(resolved)] if source_dll else [str(resolved)]
    before = hashlib.sha256(resolved.read_bytes()).hexdigest()
    results = []
    for arguments in (["--version"], command.split() + ["--help"]):
        result = subprocess.run(argv + arguments, cwd=workspace_root, text=True, capture_output=True, timeout=30)
        if result.returncode:
            raise RuntimeError(json.dumps({"exitCode": result.returncode, "stdout": result.stdout, "stderr": result.stderr}))
        results.append(result)
    version, help_result = results
    if hashlib.sha256(resolved.read_bytes()).hexdigest() != before:
        raise RuntimeError("Executable changed during guidance capture; discard this result.")
    if (workspace_root / "twig.json").read_bytes() != config_bytes:
        raise RuntimeError("Workspace binding changed during guidance capture; discard this result.")
    return {"contractVersion": 1, "executable": str(resolved), "executableSha256": before,
            "version": version.stdout.strip(), "indexSha256": hashlib.sha256(index_bytes).hexdigest(),
            "workspace": str(workspace_root), "connection": connection,
            "command": rows[0], "help": help_result.stdout, "stderr": help_result.stderr,
            "fullRead": str(root / "docs/commands" / rows[0]["path"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--executable", help="Qualified Twig executable, never a version-only provenance claim")
    source.add_argument("--source-dll", help="Qualified source-built twig.dll; runs via dotnet")
    parser.add_argument("--workspace", required=True, help="Explicit Twig workspace root containing twig.json")
    parser.add_argument("command", nargs="+", help="Exact canonical command, such as process description")
    args = parser.parse_args()
    try:
        result = guide(" ".join(args.command), args.executable, args.source_dll, args.workspace)
        print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
