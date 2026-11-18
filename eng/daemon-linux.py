"""Linux x64 release installer and owner-only headless lifecycle operator."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import stat
import struct
import subprocess
import sys
import tempfile
import time
import uuid


def require(condition, message):
    if not condition:
        raise ValueError(message)


def private_dir(path, create=False):
    path = Path(path)
    require(path.is_absolute() and path != Path("/") and ".." not in path.parts,
            "An absolute, dedicated directory is required.")
    for entry in [*reversed(path.parents), path]:
        if create and not entry.exists():
            entry.mkdir(mode=0o700, exist_ok=True)
        st = entry.lstat()
        require(stat.S_ISDIR(st.st_mode) and st.st_uid in (0, os.geteuid())
                and (not st.st_mode & 0o022 or (st.st_uid == 0 and st.st_mode & stat.S_ISVTX)),
                f"Unsafe or linked directory: {entry}")
    require(st.st_uid == os.geteuid() and stat.S_IMODE(st.st_mode) == 0o700,
            f"Directory must be owned by this user and mode 0700: {path}")
    return path


def regular(path):
    st = path.lstat()
    require(stat.S_ISREG(st.st_mode) and st.st_nlink == 1 and not st.st_mode & 0o022
            and st.st_uid in (0, os.geteuid()), f"Unsafe file: {path}")
    return path


def small(path, limit):
    with regular(path).open("rb") as f:
        result = f.read(limit + 1)
    require(len(result) <= limit, f"Oversize record: {path}")
    return result


def digest(path):
    h = hashlib.sha256()
    with regular(path).open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def inventory(package):
    files = {}
    for path in package.rglob("*"):
        rel = path.relative_to(package).as_posix()
        if path.is_dir() and not path.is_symlink():
            require("wwwroot" not in path.parts, "Bundled Canvas assets are not supported.")
        elif rel != "runtime.json":
            files[rel] = digest(path)
    require({"Bench.Server", "Bench.Server.dll", "libcoreclr.so", "libhostfxr.so",
             "Twig.Providers.Native.dll", "Twig.Store.Sqlite.dll"} <= files.keys(),
            "Incomplete Linux x64 daemon package.")
    require(os.access(package / "Bench.Server", os.X_OK), "Bench.Server is not executable.")
    return files


def version(value):
    require(isinstance(value, str) and re.fullmatch("[0-9a-f]{40}", value), "Invalid daemon source revision.")
    return value


def manifest(revision, files):
    return dict(application="twig-daemon", schema=1, rid="linux-x64", protocol="twig-command-v1",
                version=version(revision), files=files)


def verify(package, revision, expected):
    raw = small(package / "runtime.json", 4 * 1024 * 1024)
    require(hashlib.sha256(raw).hexdigest() == expected, "Selected daemon manifest hash mismatch.")
    require(json.loads(raw) == manifest(revision, inventory(package)), "Daemon runtime inventory mismatch.")
    return package / "Bench.Server"


def selected(root):
    record = json.loads(small(root / "current.json", 16384))
    require(record["application"] == "twig-daemon" and record["schema"] == 1
            and record["rid"] == "linux-x64", "Unsupported or foreign daemon selection.")
    runtime = private_dir(root / "versions" / version(record["version"]))
    data = private_dir(record["privateDataDir"])
    separate(root, data)
    return verify(runtime, record["version"], record["manifestHash"]), data


def separate(root, data):
    require(root != data and root not in data.parents and data not in root.parents,
            "Install root and private data must be separate directories.")


def locked(file):
    if file.exists() or file.is_symlink():
        regular(file)
    fd = os.open(file, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    return os.fdopen(fd, "rb+")


def idle(data):
    with locked(data / "daemon.lock") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        except BlockingIOError:
            return False


def contact(executable, data, stop=False):
    with socket.socket(socket.AF_UNIX) as peer:
        peer.settimeout(2)
        try:
            peer.connect("\0twig-control-v1-" + hashlib.sha256(os.fsencode(data)).hexdigest().upper())
        except ConnectionRefusedError:
            return False
        pid, uid, _ = struct.unpack("iII", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        require(uid == os.geteuid() and os.path.samefile(f"/proc/{pid}/exe", executable),
                "Control endpoint does not belong to the selected owner and executable.")
        identity = str(uuid.UUID(small(data / "daemon.id", 64).decode("ascii")))
        with peer.makefile("rb") as response:
            require(response.readline(100) == f"TWIG-CONTROL/1 {identity}\n".encode(),
                    "Daemon did not prove the selected protocol and durable identity.")
            if stop:
                peer.sendall(b"S")
                require(response.readline(16) == b"OK\n", "Stop outcome uncertain; not retried.")
        return True


def lifecycle(action, executable, data):
    live = contact(executable, data, stop=action == "stop")
    if live and action != "stop":
        return "running"
    if not live:
        require(idle(data), "The daemon lease is held but its identity is unavailable; refusing to launch or stop.")
        if action != "start":
            return "stopped"
        # No console/parent pipes or terminal session are retained by a background daemon.
        child = subprocess.Popen([executable, "--commands-only", "--data-dir", data],
                                 cwd=executable.parent, start_new_session=True,
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if action == "start":
            require(child.poll() is None, f"Selected daemon exited before readiness (exit {child.returncode}).")
            if contact(executable, data):
                return "running"
        elif idle(data):
            return "stopped"
        time.sleep(0.05)
    raise TimeoutError(f"Daemon {action} outcome uncertain; not retried.")


def install(root, package, data):
    separate(root, data)
    entries = {entry.name for entry in root.iterdir()} - {".install.lock"}
    if entries:
        require(entries == {"versions", "current.json"}, "Refusing a foreign or incomplete install root.")
        old_exe, old_data = selected(root)
        require(not contact(old_exe, old_data) and idle(old_data), "Stop the selected daemon before upgrading.")
    require(idle(data), "Stop the daemon using this data directory before installing.")
    require(package.is_dir() and not package.is_symlink(), "An extracted daemon runtime is required.")
    raw = small(package / "runtime.json", 4 * 1024 * 1024)
    revision = version(json.loads(raw)["version"])
    expected = hashlib.sha256(raw).hexdigest()
    versions = root / "versions"
    dest = versions / revision
    require(not dest.exists(), "Immutable daemon version already installed.")
    with tempfile.TemporaryDirectory(dir=root) as temp:
        staged = Path(temp) / "runtime"
        shutil.copytree(package, staged, symlinks=True)
        staged.chmod(0o700)
        verify(staged, revision, expected)
        private_dir(versions, create=True)
        record = dict(application="twig-daemon", schema=1, rid="linux-x64", version=revision,
                      manifestHash=expected, privateDataDir=str(data))
        pending = Path(temp) / "current.json"
        pending.write_text(json.dumps(record), encoding="utf-8")
        staged.rename(dest)
        os.replace(pending, root / "current.json")
    print(f"Selected daemon {revision}. Run: twig daemon start")


def main(args):
    require(platform.system() == "Linux" and platform.machine() == "x86_64", "Linux x64 is required.")
    os.umask(0o077)
    if len(args) == 3 and args[0] == "manifest":
        package = Path(args[1])
        with (package / "runtime.json").open("x", encoding="utf-8") as f:
            json.dump(manifest(args[2], inventory(package)), f, sort_keys=True)
        return
    installing = len(args) == 3 and args[0] == "install"
    require(installing or (len(args) == 1 and args[0] in ("start", "status", "stop")),
            "Usage: daemon-linux.py install <extracted-runtime> <private-data-directory>")
    local = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local/share")
    root = Path(os.environ.get("TWIG_DAEMON_INSTALL_ROOT", local / "Twig/Daemon"))
    if not installing and not (root / "current.json").exists():
        raise ValueError("No selected daemon. Verify and extract twig-daemon-linux-x64.tar.gz, then run its daemon-linux.py install.")
    root = private_dir(root, create=installing)
    with locked(root / ".install.lock") as lock:
        deadline = time.monotonic() + 15
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                require(time.monotonic() < deadline, "Another daemon lifecycle operation is still in progress.")
                time.sleep(0.05)
        if installing:
            install(root, Path(args[1]), private_dir(args[2], create=True))
        else:
            print(lifecycle(args[0], *selected(root)))


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except (OSError, ValueError, KeyError, TypeError) as error:
        sys.exit(f"Twig daemon: {error}")
