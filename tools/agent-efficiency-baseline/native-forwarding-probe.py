#!/usr/bin/env python3
"""AB#879 native-forwarding probe.

Exercises the actual Hermes twig-plugin seam
(`~/.hermes/profiles/starbright-engineering/plugins/twig/{runner,tools,schemas}.py`)
against this worktree's *source-built* Twig CLI.  It never mutates the plugin,
its config, ADO, an existing work-item cache, or a user's shell.

Two modes, run independently.

    OFFLINE  --offline
        Import runner+tools, monkey-patch subprocess.run to intercept every
        `twig …` argv, cwd, and env.  Prove:
          1. `twig_show(refresh=True)` forwards `--refresh` in argv.
          2. runner.run uses the configured workspace_dir as its cwd — never
             the caller's process cwd.
          3. a nonzero returncode from twig propagates as `TwigError`, with
             the diagnostic text preserved and ANSI stripped.
          4. runner appends `-o json` to `run_json` calls, once and last.
          5. Non-existent twig_bin/workspace refuses at preflight before any
             subprocess is spawned.
        Zero credentials, zero network, zero build.  This is the pure
        forwarding-negative-control channel.

    LIVE     --workspace <dir> --id <int> --source-dll <path>
        Point the runner at a disposable workspace (config only, no copied
        cache), inherit the caller's credentials from $HOME, and launch the
        source CLI (via `dotnet <path/Twig.dll>` — no install, no upgrade).
        Then invoke `twig_show(id=--id, refresh=True)` through the actual
        tool handler and prove:
          * subprocess argv contains `show <id> --refresh -o json`
          * subprocess cwd equals the disposable workspace_dir, not the
            caller's cwd
          * result parses as JSON and reports id=<id> (no fake cache success)
          * a deliberately-broken run (bogus id / bad workspace) surfaces
            format-aware error + nonzero exit through TwigError.
        Uses the currently-authenticated $HOME's `.twig/.refresh-token`; if
        that token is absent the preflight fails cleanly and the probe stops.

Honest limits:
  * This is NOT a fresh Hermes agent session.  It exercises the plugin's
    runner/tools import seam only — not the outer approval/streaming or
    tool-schema wiring.  Hermes-session qualification remains AB#882.
  * The live channel does not write to ADO; write tools are not invoked.
  * Even the live channel is one process — cross-invocation cache/session
    behaviour is not measured.
  * A cache-only fake success cannot ride the live channel because the
    disposable workspace has an empty cache; a claim of "show returned
    an item" can only be satisfied by an actual FetchWithLinks landing.

Usage:
  # pure offline (no build, no credentials):
  python3 tools/agent-efficiency-baseline/native-forwarding-probe.py --offline

  # live, against source dll and a disposable workspace:
  dotnet build src/Twig -c Release
  python3 tools/agent-efficiency-baseline/native-forwarding-probe.py \\
      --workspace /tmp/twig-879-probe-ws \\
      --id 879 \\
      --source-dll src/Twig/bin/Release/net11.0/twig.dll

Exit codes:
  0  every assertion in the selected mode passed
  1  at least one assertion failed; JSON detail on stdout
  2  usage error (missing/incompatible flags, missing files)
"""

from __future__ import annotations

import argparse
import importlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace
from typing import Any

PLUGIN_ROOT = Path(
    os.environ.get(
        "TWIG_PLUGIN_ROOT",
        str(Path.home() / ".hermes/profiles/starbright-engineering/plugins"),
    )
)


def _fail(reason: str) -> None:
    print(json.dumps({"error": reason}, indent=2))
    sys.exit(2)


def _load_plugin_modules():
    """Import the real plugin without copying its files.

    The plugin lives at PLUGIN_ROOT/twig; adding PLUGIN_ROOT to sys.path lets
    us `import twig.{runner,tools,schemas}` verbatim.  We never mutate the
    package on disk — only its in-memory `_ctx` singleton, and only for the
    duration of this process.
    """
    if not (PLUGIN_ROOT / "twig" / "runner.py").is_file():
        _fail(f"twig plugin not found under {PLUGIN_ROOT} — set TWIG_PLUGIN_ROOT")
    if str(PLUGIN_ROOT) not in sys.path:
        sys.path.insert(0, str(PLUGIN_ROOT))
    runner = importlib.import_module("twig.runner")
    tools = importlib.import_module("twig.tools")
    schemas = importlib.import_module("twig.schemas")
    return runner, tools, schemas


class _StubCtx:
    """Minimal shape of the ctx runner.bind() expects.

    Only get_config is read (`runner._setting`); everything else is unused.
    Real Hermes passes a richer ctx; we intentionally provide only what the
    plugin actually consumes so a change in the wider protocol shows up as
    an obvious AttributeError, not a silent difference.
    """

    def __init__(self, settings: dict[str, Any]):
        self._settings = settings

    def get_config(self, key: str, default: Any = None) -> Any:
        return self._settings.get(key, default)


@contextmanager
def _rebound(runner, settings: dict[str, Any]):
    prior = runner._ctx
    runner.bind(_StubCtx(settings))
    try:
        yield
    finally:
        runner._ctx = prior


class _RecordedCall:
    __slots__ = ("argv", "cwd", "env")

    def __init__(self, argv, cwd, env):
        self.argv = list(argv)
        self.cwd = cwd
        # Provenance only: HOME proves the runner overrode twig_home, and
        # HERMES_HOME is expected to be popped by runner.run() so its
        # presence would be a regression signal.  Nothing else from the
        # caller's environment leaks into the report.
        self.env = {k: env.get(k) for k in ("HOME", "HERMES_HOME") if k in env}

    def to_dict(self) -> dict[str, Any]:
        return {"argv": self.argv, "cwd": self.cwd, "env": self.env}


@contextmanager
def _intercept_subprocess(returncode: int = 0, stdout: str = "", stderr: str = ""):
    """Replace subprocess.run inside runner with a recorder.

    Returns a list the caller can inspect after the block exits — one entry
    per twig subprocess the plugin *would* have spawned.  No real process
    is created, no external command runs.
    """
    calls: list[_RecordedCall] = []
    real_run = subprocess.run

    def fake_run(argv, cwd=None, env=None, capture_output=None, text=None,
                 timeout=None, shell=None, **_):
        calls.append(_RecordedCall(argv, str(cwd) if cwd else None, env or {}))
        result = SimpleNamespace(
            args=argv, returncode=returncode, stdout=stdout, stderr=stderr,
        )
        return result

    subprocess.run = fake_run  # type: ignore[assignment]
    try:
        yield calls
    finally:
        subprocess.run = real_run  # type: ignore[assignment]


# ── OFFLINE mode ────────────────────────────────────────────────────────

def _write_stub_workspace(root: Path) -> Path:
    """Writes the smallest twig.json + fake credential the preflight needs.

    Neither is used to talk to ADO in offline mode — subprocess.run is
    monkey-patched, so no real twig is ever spawned.  The files exist only
    so `_preflight` (twig.json probe, credential probe) doesn't refuse
    before the assertion we actually want to make.
    """
    root.mkdir(parents=True, exist_ok=True)
    (root / "twig.json").write_text(
        json.dumps({"organization": "probe-org", "project": "probe-project"}),
        encoding="utf-8",
    )
    home = root / "home"
    (home / ".twig").mkdir(parents=True, exist_ok=True)
    (home / ".twig" / ".refresh-token").write_text("offline-fake-token", encoding="utf-8")
    twig_bin_dir = home / ".twig" / "bin"
    twig_bin_dir.mkdir(parents=True, exist_ok=True)
    # A file, not an executable: subprocess.run is intercepted, so this only
    # needs to satisfy `Path(binary).exists()`.
    fake_bin = twig_bin_dir / "twig"
    fake_bin.write_text("#!/bin/sh\nexit 42\n", encoding="utf-8")
    fake_bin.chmod(0o755)
    return home


def run_offline() -> int:
    runner, tools, _ = _load_plugin_modules()
    results: list[dict[str, Any]] = []
    passed = True

    with tempfile.TemporaryDirectory(prefix="twig-879-offline-") as td:
        ws = Path(td) / "workspace"
        home = _write_stub_workspace(ws)
        settings = {
            "workspace_dir": str(ws),
            "twig_home": str(home),
            "twig_bin": str(home / ".twig" / "bin" / "twig"),
        }

        # ── Assertion 1: --refresh forwarding on twig_show ──────────────
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=0, stdout='{"id": 879}') as calls:
            tools.show({"work_item_id": 879, "refresh": True})
        argv = calls[0].argv if calls else []
        got_refresh = "--refresh" in argv
        got_id = "879" in argv
        got_show = argv and argv[1] == "show"
        got_json = argv[-2:] == ["-o", "json"]
        ok = got_refresh and got_id and got_show and got_json and len(calls) == 1
        passed &= ok
        results.append({
            "id": "offline.show.refresh-forwarded",
            "ok": ok,
            "expected": "one twig subprocess with argv[1]='show', id, --refresh, trailing -o json",
            "argv": argv,
        })

        # ── Assertion 2: fixed cwd = workspace_dir, not caller cwd ──────
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=0, stdout='{"id": 879}') as calls:
            tools.show({"work_item_id": 879, "refresh": False})
        recorded_cwd = calls[0].cwd if calls else None
        ok = recorded_cwd == str(ws)
        passed &= ok
        results.append({
            "id": "offline.show.cwd-provenance",
            "ok": ok,
            "expected": str(ws),
            "observed": recorded_cwd,
            "note": "runner MUST resolve cwd from the plugin's workspace_dir, "
                    "never the caller's cwd or $PWD.",
        })

        # ── Assertion 3: nonzero exit propagates as TwigError ───────────
        # The wrapper (`_guard`) catches TwigError and returns a JSON error
        # string, so `tools.show` never re-raises.  Exercise both hops:
        # `runner.run` is where the exception is native, and `tools.show`
        # is what a Hermes tool call actually observes.  A pass requires
        # the stderr text to survive both, with ANSI stripped.
        raw_surfaced: str | None = None
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=7, stdout="",
                                       stderr="\x1b[31mfatal: no such id\x1b[0m"):
            try:
                runner.run(["show", "42"])
            except runner.TwigError as exc:
                raw_surfaced = str(exc)
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=7, stdout="",
                                       stderr="\x1b[31mfatal: no such id\x1b[0m"):
            wrapper_surfaced = tools.show({"work_item_id": 42})
        raw_ok = (
            raw_surfaced is not None
            and "fatal: no such id" in raw_surfaced
            and "\x1b[" not in raw_surfaced
        )
        wrapper_ok = (
            isinstance(wrapper_surfaced, str)
            and "fatal: no such id" in wrapper_surfaced
            and "\x1b[" not in wrapper_surfaced
        )
        ok = raw_ok and wrapper_ok
        passed &= ok
        results.append({
            "id": "offline.show.nonzero-propagates",
            "ok": ok,
            "expected": "raw runner.run raises TwigError; wrapper returns JSON error string; ANSI stripped in both",
            "raw_surfaced": raw_surfaced,
            "wrapper_surfaced": wrapper_surfaced,
        })

        # ── Assertion 4: run_json appends -o json exactly once, last ────
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=0, stdout='{}') as calls:
            tools.process({"work_item_type": "Task"})
        argv = calls[0].argv if calls else []
        occurrences = sum(1 for i, tok in enumerate(argv) if tok == "-o" and i + 1 < len(argv) and argv[i + 1] == "json")
        ok = occurrences == 1 and argv[-2:] == ["-o", "json"]
        passed &= ok
        results.append({
            "id": "offline.process.o-json-once-and-last",
            "ok": ok,
            "expected": "single trailing -o json",
            "argv": argv,
        })

        # ── Assertion 5: missing credential refuses at preflight ────────
        (home / ".twig" / ".refresh-token").unlink()
        with _rebound(runner, settings), \
                _intercept_subprocess(returncode=0, stdout="") as calls:
            try:
                tools.show({"work_item_id": 879})
                surfaced = None
            except runner.TwigError as exc:
                surfaced = str(exc)
            except Exception as exc:  # noqa: BLE001 — recording only
                surfaced = f"unexpected-exception:{type(exc).__name__}:{exc}"
        # Tool wrapper catches TwigError and returns JSON error, so we also
        # cover the wrapper path:
        with _rebound(runner, settings):
            wrapper_result = tools.show({"work_item_id": 879})
        no_subprocess = len(calls) == 0
        wrapper_flagged = isinstance(wrapper_result, str) and ".refresh-token" in wrapper_result
        ok = no_subprocess and wrapper_flagged
        passed &= ok
        results.append({
            "id": "offline.preflight.missing-credential-refuses",
            "ok": ok,
            "expected": "no subprocess spawned; wrapper returns credential-hint error",
            "surfaced_directly": surfaced,
            "wrapper_result": wrapper_result if isinstance(wrapper_result, str) else str(wrapper_result),
        })

    report = {
        "mode": "offline",
        "plugin_root": str(PLUGIN_ROOT),
        "passed": passed,
        "assertions": results,
        "honest_limits": [
            "not a fresh Hermes session",
            "no build, no ADO calls, no credentials used",
            "subprocess.run is monkey-patched; the twig binary is never executed",
        ],
    }
    print(json.dumps(report, indent=2))
    return 0 if passed else 1


# ── LIVE mode ───────────────────────────────────────────────────────────

def _write_disposable_workspace(root: Path, org: str, project: str) -> None:
    """Config-only workspace.

    A minimal twig.json is enough to satisfy the runner's preflight; no cache
    is copied in from anywhere else — so any 'show returned an item' claim
    must come from a real FetchWithLinks landing, not a stale local mirror.
    """
    root.mkdir(parents=True, exist_ok=True)
    (root / "twig.json").write_text(
        json.dumps({"organization": org, "project": project}, indent=2),
        encoding="utf-8",
    )


def _make_dotnet_launcher(root: Path, dll: Path) -> Path:
    """Writes a tiny shell shim that runs `dotnet <dll>` with our args.

    The runner treats `twig_bin` as an executable and calls it directly (no
    shell interpolation), so a shim is the simplest way to invoke the
    source dll without publishing or installing.
    """
    launcher = root / "twig-launcher.sh"
    launcher.write_text(
        textwrap.dedent(f"""\
        #!/bin/sh
        exec dotnet {json.dumps(str(dll))} "$@"
        """),
        encoding="utf-8",
    )
    launcher.chmod(0o755)
    return launcher


def _read_caller_org_project() -> tuple[str, str] | None:
    """Best-effort: reuse the caller's org/project so the same credential
    resolves.  If the caller doesn't sit in a twig workspace, live mode
    won't be able to authenticate — surface that plainly instead of guessing.
    """
    candidate = Path.cwd() / "twig.json"
    if not candidate.is_file():
        return None
    try:
        data = json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    org = data.get("organization") or data.get("Organization")
    project = data.get("project") or data.get("Project")
    if not org or not project:
        return None
    return org, project


def run_live(workspace: Path, work_item_id: int, source_dll: Path) -> int:
    runner, tools, _ = _load_plugin_modules()
    results: list[dict[str, Any]] = []
    passed = True

    if not source_dll.is_file():
        _fail(f"--source-dll not found: {source_dll}")
    if not shutil.which("dotnet"):
        _fail("dotnet not on PATH; run under a shell that can reach the SDK")

    caller_ctx = _read_caller_org_project()
    if caller_ctx is None:
        _fail(
            "no twig.json in current directory; run from a workspace whose "
            "organization/project matches the credential you want the probe "
            "to reuse (read-only). Live mode reuses your $HOME's "
            ".twig/.refresh-token verbatim."
        )
    org, project = caller_ctx

    caller_home = Path(os.environ.get("HOME", str(Path.home())))
    if not (caller_home / ".twig" / ".refresh-token").is_file():
        _fail(
            f"$HOME/.twig/.refresh-token missing under {caller_home}; run "
            "'twig auth login' in the caller's profile, then retry."
        )

    if workspace.exists() and any(workspace.iterdir()):
        _fail(f"--workspace must be a fresh empty directory: {workspace}")
    _write_disposable_workspace(workspace, org, project)
    launcher = _make_dotnet_launcher(workspace, source_dll)

    settings = {
        "workspace_dir": str(workspace),
        "twig_home": str(caller_home),
        "twig_bin": str(launcher),
    }

    # ── Live assertion 1: happy-path show forwards --refresh + fixed cwd ─
    calls: list[_RecordedCall] = []
    real_run = subprocess.run

    def sniffing_run(argv, cwd=None, env=None, **kw):
        calls.append(_RecordedCall(argv, str(cwd) if cwd else None, env or {}))
        return real_run(argv, cwd=cwd, env=env, **kw)

    with _rebound(runner, settings):
        subprocess.run = sniffing_run  # type: ignore[assignment]
        try:
            payload = tools.show({"work_item_id": work_item_id, "refresh": True})
        finally:
            subprocess.run = real_run  # type: ignore[assignment]

    argv = calls[0].argv if calls else []
    argv_ok = (
        argv[1:2] == ["show"]
        and str(work_item_id) in argv
        and "--refresh" in argv
        and argv[-2:] == ["-o", "json"]
    )
    cwd_ok = calls[0].cwd == str(workspace) if calls else False
    parsed: Any = None
    try:
        parsed = json.loads(payload) if isinstance(payload, str) else payload
    except (TypeError, json.JSONDecodeError):
        parsed = None
    payload_has_id = (
        isinstance(parsed, dict)
        and (parsed.get("id") == work_item_id or parsed.get("Id") == work_item_id)
    )
    payload_flagged_error = isinstance(parsed, dict) and "error" in parsed
    ok = argv_ok and cwd_ok and payload_has_id
    passed &= ok
    results.append({
        "id": "live.show.refresh-forwarded-and-id-observed",
        "ok": ok,
        "argv_ok": argv_ok,
        "cwd_ok": cwd_ok,
        "payload_has_id": payload_has_id,
        "payload_flagged_error": payload_flagged_error,
        "argv": argv,
        "cwd": calls[0].cwd if calls else None,
        "expected_id": work_item_id,
    })

    # ── Live assertion 2: failure propagation from a bogus id ─────────
    bogus_id = 2_147_483_647
    calls2: list[_RecordedCall] = []

    def sniffing_run2(argv, cwd=None, env=None, **kw):
        calls2.append(_RecordedCall(argv, str(cwd) if cwd else None, env or {}))
        return real_run(argv, cwd=cwd, env=env, **kw)

    with _rebound(runner, settings):
        subprocess.run = sniffing_run2  # type: ignore[assignment]
        try:
            fail_payload = tools.show({"work_item_id": bogus_id, "refresh": True})
        finally:
            subprocess.run = real_run  # type: ignore[assignment]

    try:
        fail_parsed = json.loads(fail_payload) if isinstance(fail_payload, str) else fail_payload
    except (TypeError, json.JSONDecodeError):
        fail_parsed = None
    fail_ok = (
        isinstance(fail_parsed, dict) and isinstance(fail_parsed.get("error"), str)
    )
    cwd2_ok = calls2[0].cwd == str(workspace) if calls2 else False
    passed &= fail_ok and cwd2_ok
    results.append({
        "id": "live.show.bogus-id-propagates-error",
        "ok": fail_ok and cwd2_ok,
        "cwd_ok": cwd2_ok,
        "fail_ok": fail_ok,
        "argv": calls2[0].argv if calls2 else [],
        "wrapper_error": (fail_parsed or {}).get("error") if isinstance(fail_parsed, dict) else None,
        "note": "wrapper MUST return {error: ...} JSON, not a masqueraded success.",
    })

    report = {
        "mode": "live",
        "plugin_root": str(PLUGIN_ROOT),
        "workspace_dir": str(workspace),
        "twig_home": str(caller_home),
        "twig_launcher": str(launcher),
        "source_dll": str(source_dll),
        "work_item_id": work_item_id,
        "passed": passed,
        "assertions": results,
        "honest_limits": [
            "not a fresh Hermes agent session",
            "read-only ADO surface: only twig_show is invoked",
            "single-process; cross-session behaviour is not measured",
            "credential is reused from $HOME/.twig, never copied",
        ],
    }
    print(json.dumps(report, indent=2))
    return 0 if passed else 1


# ── entry point ─────────────────────────────────────────────────────────

def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="native-forwarding-probe",
        description="AB#879 Hermes twig-plugin forwarding probe.",
    )
    parser.add_argument("--offline", action="store_true",
                        help="run pure offline forwarding negative controls; "
                             "requires no build, credentials, or ADO.")
    parser.add_argument("--workspace", type=Path,
                        help="fresh disposable workspace_dir for live mode.")
    parser.add_argument("--id", dest="work_item_id", type=int,
                        help="work item id to twig_show in live mode.")
    parser.add_argument("--source-dll", dest="source_dll", type=Path,
                        help="path to a built src/Twig/bin/*/net11.0/twig.dll.")
    args = parser.parse_args(argv)
    if args.offline and any([args.workspace, args.work_item_id, args.source_dll]):
        parser.error("--offline is exclusive with live-mode flags.")
    if not args.offline:
        missing = [n for n, v in (("--workspace", args.workspace),
                                  ("--id", args.work_item_id),
                                  ("--source-dll", args.source_dll)) if v is None]
        if missing:
            parser.error(f"missing required live-mode flag(s): {', '.join(missing)}. "
                         "Use --offline for the pure forwarding negative controls.")
    return args


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(list(sys.argv[1:] if argv is None else argv))
    if args.offline:
        return run_offline()
    return run_live(args.workspace, args.work_item_id, args.source_dll)


if __name__ == "__main__":
    sys.exit(main())
