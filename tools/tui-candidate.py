#!/usr/bin/env python3
"""Build, seal, inspect and switch only explicitly isolated native TUI candidates."""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import uuid

REPO = Path(__file__).resolve().parent.parent
MANIFEST = "tui-candidate.json"
ROOT_MARKER = ".twig-tui-candidate-root.json"
RIDS = {"win-x64": ("Windows", "twig.exe", "e_sqlite3.dll"),
        "linux-x64": ("Linux", "twig", "libe_sqlite3.so"),
        "osx-arm64": ("Darwin", "twig", "libe_sqlite3.dylib")}


def run(argv, cwd=None):
    result = subprocess.run([str(x) for x in argv], cwd=cwd, text=True,
                            encoding="utf-8", errors="replace", capture_output=True, timeout=1800)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip() or f"Command failed: {argv[0]}")
    return result.stdout.strip()


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def commit_value(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("Specify the complete lowercase source commit, not a branch or short hash.")
    return value


def source_pin(commit):
    if run(["git", "rev-parse", "HEAD"], REPO) != commit:
        raise ValueError("The checked-out source does not match the requested commit.")
    if run(["git", "status", "--porcelain", "--untracked-files=normal"], REPO):
        raise ValueError("Commit or preserve outstanding source work before building a source-pinned candidate.")


def compatibility():
    cache = (REPO / "src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs").read_text(encoding="utf-8")
    registry = (REPO / "src/Twig.Infrastructure/Persistence/SqliteSystemWorktreeRegistry.cs").read_text(encoding="utf-8")
    def constant(text, name):
        found = re.search(r"\bconst int " + name + r"\s*=\s*(\d+)\s*;", text)
        if not found:
            raise ValueError(f"The native {name} compatibility constant is unavailable.")
        return int(found.group(1))
    return {"mirrorSchema": constant(cache, "SchemaVersion"),
            "durableSchema": constant(cache, "DurableSchemaVersion"),
            "registrySchema": constant(registry, "SchemaVersion"),
            "controlProtocol": 2, "herdrProtocol": 22}


def write_json(path, value):
    content = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    temporary = path.with_name(path.name + ".new-" + uuid.uuid4().hex)
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def payload_files(root):
    files = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError("Candidate payloads cannot contain links or junctions.")
        if path.is_file() and path.name != MANIFEST:
            files[path.relative_to(root).as_posix()] = digest(path)
    return files


def signature(root, rid, commit):
    if rid != "win-x64":
        return {"mode": "source-pinned-checksums", "limit": "No Windows signature is claimed on this platform."}
    if platform.system() != "Windows":
        raise ValueError("Verify the Windows public-trust publisher/profile/timestamp on Windows before installation.")
    result = run(["pwsh", "-NoProfile", "-File", REPO / "tools/verify-tui-candidate.ps1",
                  "-Executable", root / "bin" / "twig.exe", "-SourceCommit", commit])
    return json.loads(result)


def build(args):
    source_pin(args.commit)
    system, _, _ = RIDS[args.rid]
    machine = platform.machine().lower()
    if platform.system() != system or (args.rid == "osx-arm64" and machine not in ("arm64", "aarch64")):
        raise ValueError("NativeAOT must be built on the selected platform; this tool does not claim cross-OS publication.")
    target = args.directory.resolve()
    if target.exists():
        raise ValueError("Build output must be a new isolated directory; previous evidence is never overwritten.")
    target.mkdir(parents=True)
    argv = ["dotnet", "publish", REPO / "src/Twig/Twig.csproj", "-c", "Release", "-r", args.rid,
            "--self-contained", "true", "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:TrimMode=full",
            f"-p:MinVerVersionOverride=0.0.0-tui-candidate+{args.commit}",
            "-p:IncludeSourceRevisionInInformationalVersion=false", "-o", target / "bin"]
    output = run(argv, REPO)
    source_pin(args.commit)
    host = REPO / "integrations/herdr-aot"
    shutil.copy2(host / "herdr-plugin.toml", target / "herdr-plugin.toml")
    shutil.copytree(host / "scripts", target / "scripts")
    write_json(target / "build-receipt.json", {"sourceCommit": args.commit, "rid": args.rid,
               "sdk": run(["dotnet", "--version"], REPO), "publishArguments": [str(x) for x in argv],
               "compatibility": compatibility(), "result": output,
               "status": "Built only: sign the exact Windows image with the approved profile, then seal. No installation occurred."})
    return {"built": str(target), "sourceCommit": args.commit, "sealed": False, "installed": False}


def seal(args):
    source_pin(args.commit)
    root = args.directory.resolve(strict=True)
    receipt = json.loads((root / "build-receipt.json").read_text(encoding="utf-8"))
    if receipt["sourceCommit"] != args.commit or receipt["rid"] != args.rid or receipt["compatibility"] != compatibility():
        raise ValueError("Build receipt/source/store contract mismatch; do not relabel a prior payload.")
    for name in RIDS[args.rid][1:]:
        if not (root / "bin" / name).is_file():
            raise ValueError(f"Missing native dependency: {name}")
    trust = signature(root, args.rid, args.commit)
    files = payload_files(root)
    manifest = {"version": 1, "repository": "PolyphonyRequiem/twig", "sourceCommit": args.commit,
                "rid": args.rid, "compatibility": receipt["compatibility"], "files": files, "trust": trust,
                "nativeAot": True, "trimMode": "full", "defaultReplacement": False}
    write_json(root / MANIFEST, manifest)
    manifest_sha = digest(root / MANIFEST)
    return {"manifest": inspect(root, args.commit, args.rid, manifest_sha), "manifestSha256": manifest_sha,
            "trustSource": "Retain this independently trusted seal receipt; never take the expected digest from the downloaded payload itself."}


def inspect(root, expected_commit, expected_rid, expected_manifest_sha):
    if root.is_symlink() or (hasattr(root, "is_junction") and root.is_junction()):
        raise ValueError("A candidate root cannot be a link or junction.")
    root = root.resolve(strict=True)
    if not isinstance(expected_manifest_sha, str) or not re.fullmatch(r"[0-9a-f]{64}", expected_manifest_sha):
        raise ValueError("An independently trusted complete manifest SHA-256 is required.")
    if digest(root / MANIFEST) != expected_manifest_sha:
        raise ValueError("Candidate manifest differs from the trusted receipt; no payload was launched or installed.")
    manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8"))
    if manifest.get("version") != 1 or manifest.get("repository") != "PolyphonyRequiem/twig":
        raise ValueError("Unsupported candidate artifact contract.")
    if manifest.get("sourceCommit") != expected_commit or manifest.get("rid") != expected_rid:
        raise ValueError("Candidate source/platform differs from the explicit requested artifact.")
    if manifest.get("nativeAot") is not True or manifest.get("trimMode") != "full" or manifest.get("defaultReplacement") is not False:
        raise ValueError("Candidate is not a full-trim opt-in NativeAOT artifact.")
    formats = manifest.get("compatibility")
    required_formats = {"mirrorSchema", "durableSchema", "registrySchema", "controlProtocol", "herdrProtocol"}
    if not isinstance(formats, dict) or set(formats) != required_formats or any(type(value) is not int or value < 1 for value in formats.values()):
        raise ValueError("Candidate store/control compatibility evidence is unknown or malformed.")
    files = manifest.get("files")
    if not isinstance(files, dict) or not files:
        raise ValueError("Candidate checksum inventory is missing.")
    for name, sha in files.items():
        relative = PurePosixPath(name)
        if not isinstance(sha, str) or relative.is_absolute() or ".." in relative.parts or "\\" in name or ":" in name or relative.as_posix() != name or not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise ValueError("Candidate inventory contains an unsafe path or checksum.")
    if payload_files(root) != files:
        raise ValueError("Candidate checksum/path inventory changed; no executable was launched.")
    if "herdr-plugin.toml" not in files:
        raise ValueError("The native Herdr manifest is missing from the qualified payload.")
    for name in RIDS[expected_rid][1:]:
        if f"bin/{name}" not in files:
            raise ValueError(f"Missing native dependency: {name}")
    verified_trust = signature(root, expected_rid, expected_commit)
    if expected_rid == "win-x64" and verified_trust != manifest.get("trust"):
        raise ValueError("Candidate trusted signature evidence changed.")
    return manifest


def isolated_root(path):
    root = path.absolute()
    if root.is_symlink() or (hasattr(root, "is_junction") and root.is_junction()):
        raise ValueError("An isolated installation root cannot be a link or junction.")
    root = root.resolve()
    home = Path.home()
    protected = [home / ".twig", home / ".local/bin", REPO / ".local", REPO / ".twig",
                 home / "AppData/Roaming/herdr"]
    if any(root == p.resolve() or p.resolve() in root.parents for p in protected):
        raise ValueError("Candidate installation cannot modify normal Twig/shims, workspace data or registered Herdr paths.")
    existed = root.exists()
    if existed and not (root / ROOT_MARKER).is_file():
        raise ValueError("Existing installation root is not owned by this isolated candidate tool.")
    if platform.system() == "Windows":
        run(["pwsh", "-NoProfile", "-File", REPO / "tools/secure-tui-candidate-root.ps1", "-Root", root])
    else:
        root.mkdir(parents=True, mode=0o700, exist_ok=True)
        status = root.stat()
        if status.st_uid != os.geteuid() or status.st_mode & 0o077:
            raise ValueError("Candidate root must be current-user-owned and private (0700).")
    if existed:
        if json.loads((root / ROOT_MARKER).read_text(encoding="utf-8")).get("kind") != "isolated-twig-tui-candidates/1":
            raise ValueError("Existing installation root has an incompatible ownership marker.")
    else:
        write_json(root / ROOT_MARKER, {"kind": "isolated-twig-tui-candidates/1"})
    return root


@contextlib.contextmanager
def installation_lock(root):
    # The OS releases this lock on crash/kill. Keep its inode/file identity so a
    # concurrent opener can never lock a replaced sentinel while another runs.
    descriptor = os.open(root / ".switch.lock", os.O_CREAT | os.O_RDWR, 0o600)
    try:
        if os.fstat(descriptor).st_size == 0:
            os.write(descriptor, b"\0")
        os.lseek(descriptor, 0, os.SEEK_SET)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


def switch(args, restoring=False):
    # Trust/checksums are established before any installation root is created.
    source = args.directory.absolute()
    manifest = inspect(source, args.commit, args.rid, args.manifest_sha256)
    root = isolated_root(args.root)
    with installation_lock(root):
        pointer = root / "current.json"
        current = json.loads(pointer.read_text(encoding="utf-8")) if pointer.exists() else None
        if current:
            name = current.get("directory", "")
            if not re.fullmatch(r"[0-9a-f]{40}-(win-x64|linux-x64|osx-arm64)", name):
                raise ValueError("Existing candidate selection is invalid; no prior payload is overwritten.")
            previous = inspect(root / name, commit_value(current["sourceCommit"]), current["rid"], current["manifestSha256"])
            if current["rid"] != args.rid:
                raise ValueError("Recovery cannot switch to an executable for another platform.")
            if previous["compatibility"] != manifest["compatibility"]:
                raise ValueError("Store/control contract differs: no downgrade or migration is permitted. Retain the compatible payload.")
        name = args.commit + "-" + args.rid
        destination = root / name
        if restoring and not destination.is_dir():
            raise ValueError("Recovery requires an already retained compatible candidate payload.")
        if destination.exists():
            if inspect(destination, args.commit, args.rid, args.manifest_sha256) != manifest:
                raise ValueError("The retained candidate differs; it is never replaced in place.")
        else:
            stage = root / (".stage-" + uuid.uuid4().hex)
            try:
                shutil.copytree(source, stage)
                inspect(stage, args.commit, args.rid, args.manifest_sha256)
                os.rename(stage, destination)
            finally:
                if stage.exists():
                    shutil.rmtree(stage)
        write_json(pointer, {"sourceCommit": args.commit, "rid": args.rid, "directory": name,
                            "manifestSha256": args.manifest_sha256,
                            "previous": current, "dataMutation": False})
    executable = destination / "bin" / RIDS[args.rid][1]
    return {"selected": str(destination), "executable": str(executable), "mode": "restore" if restoring else "install",
            "stableProductsChanged": False, "pendingOrJournalOpened": False,
            "launch": [str(executable), "bench", "browser", "--cwd", "<explicit workspace>"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["build", "seal", "inspect", "install", "restore"])
    parser.add_argument("--directory", required=True, type=Path, help="Fresh build output or exact retained candidate artifact directory")
    parser.add_argument("--commit", required=True, type=commit_value, help="Exact independently selected source commit")
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--root", type=Path, help="Explicit isolated candidate-only installation root; never normal Twig/Herdr directories")
    parser.add_argument("--manifest-sha256", help="Exact manifest digest from an independently trusted seal/build receipt, not from the payload")
    args = parser.parse_args()
    if args.operation in ("install", "restore") and args.root is None:
        parser.error("install/restore requires --root; there is no default replacement path")
    if args.operation in ("inspect", "install", "restore") and args.manifest_sha256 is None:
        parser.error("inspect/install/restore requires independently trusted --manifest-sha256")
    try:
        result = (build(args) if args.operation == "build" else seal(args) if args.operation == "seal" else
                  inspect(args.directory, args.commit, args.rid, args.manifest_sha256) if args.operation == "inspect" else
                  switch(args, args.operation == "restore"))
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, subprocess.SubprocessError) as error:
        print(json.dumps({"error": str(error), "operation": args.operation, "noDefaultReplacement": True}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
