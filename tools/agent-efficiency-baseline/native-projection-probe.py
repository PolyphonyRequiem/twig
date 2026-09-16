#!/usr/bin/env python3
"""AB#880 native-projection probe.

Sits on top of the AB#879 native-forwarding-probe: reuses its plugin-loading,
subprocess-interception, and disposable-workspace helpers, and layers on the
AB#880 projection-specific assertions (opt-in `--fields` / `--sections`
forwarding on `show`, `show-batch`, and `process description`).

The probe exercises the *staged patched copy* under
`880-evidence/consumer/plugin-root/twig/`. It never mutates the running
plugin, ADO, or the caller's shell. Deployment of the qualified artifact is
AB#882.

Two modes, run independently.

    OFFLINE  --offline
        Monkey-patch subprocess.run inside the runner and assert:
          1. `twig_show(fields=…, sections=…)` forwards `--fields <csv>` and
             `--sections <csv>` verbatim, with trailing `-o json`.
          2. `twig_show(refresh=True, fields=…, sections=…)` forwards all
             three flags together.
          3. `twig_show(tree=True, fields=…)` REFUSES at the wrapper with a
             readable error; no subprocess spawned. (`--tree` never consumes
             `--fields`/`--sections`; silent drop would be worse than refuse.)
          4. Legacy `twig_show(id)` argv is preserved when neither flag set.
          5. `twig_show_batch(ids, fields, sections)` calls
             `show-batch <csv-ids>` with the same projection flags; ids
             deduplicated in order; no fabricated `--refresh` (the CLI has
             no such flag on `show-batch`).
          6. `twig_process_description(type, sections, fields)` calls
             `process description <ref> --sections <csv> --fields <csv>
             -o json`.
          7. `twig_process_description(sections=…)` without a type refuses
             at the wrapper; no subprocess spawned.
          8. `twig_process_description(type=…)` alone stays on the legacy
             full-descriptor path.
          9. Nonzero exit propagates as `TwigError` → `{error: …}` JSON with
             ANSI stripped.
         10. Schemas expose the projection shape; `show_batch` does NOT
             advertise a `refresh` property.
         11. `show_batch` rejects non-integer ids without spawning.

    LIVE     --workspace <dir> --id <int> --source-dll <path> [--type <ref>]
        Point the runner at a disposable workspace (config-only, no copied
        cache), inherit `$HOME/.twig/.refresh-token` from the caller, and
        launch the source CLI as `dotnet <path/Twig.dll>`. Assertions
        (read-only; never invokes a write tool):
          * `twig_show(id, refresh=True, fields=…, sections=…)` → argv
            contains `show <id> --refresh --fields <csv> --sections <csv>
            -o json`, cwd equals the disposable workspace, response JSON
            reports `contractVersion` (compact projection contract).
          * `twig_show(id, refresh=True)` → legacy full JSON (no
            `contractVersion`, `fields` a full map). Compact vs legacy
            response character counts are recorded for the fact checklist.
          * `twig_process_description(type=<ref>, sections="requirements")`
            → argv contains `process description <ref> --sections
            requirements -o json`, response JSON reports
            `contractVersion="process-description-compact/1"`.
          * `twig_process_description(type=<ref>)` alone → legacy
            byte-stable full descriptor (no `contractVersion`).
        `--type` defaults to `Hyperbright.Feature` (matching AB#880 target
        item 880's type); pass another reference name to override.

Usage:
  # pure offline (no build, no credentials):
  python3 tools/agent-efficiency-baseline/native-projection-probe.py --offline

  # live, against source dll and disposable workspace (read-only):
  python3 tools/agent-efficiency-baseline/native-projection-probe.py \\
      --workspace /tmp/twig-880-probe-ws \\
      --id 880 \\
      --type Hyperbright.Feature \\
      --source-dll <path>/twig.dll

Environment:
  TWIG_PLUGIN_ROOT  Directory containing the `twig/` package to load. When
                    unset the probe defaults to the AB#880 staged patched
                    copy under `880-evidence/consumer/plugin-root/` (worktree
                    is auto-detected from this file's path).

Exit codes:
  0  every assertion in the selected mode passed
  1  at least one assertion failed
  2  usage error
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

_THIS = Path(__file__).resolve()
_HARNESS = _THIS.with_name("native-forwarding-probe.py")
_WORKTREE = _THIS.parents[2]
_DEFAULT_PATCHED_ROOT = (
    _WORKTREE / "880-evidence" / "consumer" / "plugin-root"
)
_ALT_PATCHED_ROOT = (
    _WORKTREE.parent.parent / "880-evidence" / "consumer" / "plugin-root"
)


def _resolve_plugin_root() -> Path:
    env = os.environ.get("TWIG_PLUGIN_ROOT")
    if env:
        return Path(env)
    if _DEFAULT_PATCHED_ROOT.is_dir():
        return _DEFAULT_PATCHED_ROOT
    return _ALT_PATCHED_ROOT


def _import_harness():
    """Import AB#879's probe module for its reusable helpers.

    The harness reads `TWIG_PLUGIN_ROOT` at module-import time, so we set it
    to the AB#880 staged patched root *before* importing.
    """
    plugin_root = _resolve_plugin_root()
    os.environ["TWIG_PLUGIN_ROOT"] = str(plugin_root)
    if not _HARNESS.is_file():
        print(json.dumps({
            "error": f"AB#879 harness not found next to this probe: {_HARNESS}"
        }, indent=2))
        sys.exit(2)
    spec = importlib.util.spec_from_file_location(
        "ab879_probe_harness", _HARNESS,
    )
    if spec is None or spec.loader is None:
        print(json.dumps({
            "error": f"failed to load harness spec from {_HARNESS}"
        }, indent=2))
        sys.exit(2)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if module.PLUGIN_ROOT != plugin_root:
        # Harness ignored our env; refuse rather than silently exercise the
        # running plugin.
        print(json.dumps({
            "error": (
                f"harness resolved PLUGIN_ROOT={module.PLUGIN_ROOT} but AB#880 "
                f"needs {plugin_root}; the staged patched copy would be "
                "bypassed."
            )
        }, indent=2))
        sys.exit(2)
    return module, plugin_root


def _trailing(argv: list[str], flag: str) -> str | None:
    for i, tok in enumerate(argv):
        if tok == flag and i + 1 < len(argv):
            return argv[i + 1]
    return None


def _argv_ok_trailing(argv: list[str]) -> bool:
    return argv[-2:] == ["-o", "json"]


# ── OFFLINE mode ────────────────────────────────────────────────────────


def run_offline(harness) -> int:
    runner, tools, schemas = harness._load_plugin_modules()
    results: list[dict[str, Any]] = []
    passed = True

    with tempfile.TemporaryDirectory(prefix="twig-880-offline-") as td:
        ws = Path(td) / "workspace"
        home = harness._write_stub_workspace(ws)
        settings = {
            "workspace_dir": str(ws),
            "twig_home": str(home),
            "twig_bin": str(home / ".twig" / "bin" / "twig"),
        }
        rebound = harness._rebound
        intercept = harness._intercept_subprocess

        # 1: show forwards --fields + --sections after id, trailing -o json
        with rebound(runner, settings), intercept(
            returncode=0, stdout='{"contractVersion": 1, "id": 880}'
        ) as calls:
            tools.show({
                "work_item_id": 880,
                "fields": "System.Title,System.State,Custom.WayfinderExecutionMode",
                "sections": "links,parent",
            })
        argv = calls[0].argv if calls else []
        ok = (
            len(calls) == 1
            and argv[1:3] == ["show", "880"]
            and _trailing(argv, "--fields")
                == "System.Title,System.State,Custom.WayfinderExecutionMode"
            and _trailing(argv, "--sections") == "links,parent"
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.show.projection-flags-forwarded",
            "ok": ok,
            "argv": argv,
        })

        # 2: refresh + projection all forward together
        with rebound(runner, settings), intercept(
            returncode=0, stdout='{"contractVersion": 1}'
        ) as calls:
            tools.show({
                "work_item_id": 880,
                "refresh": True,
                "fields": "System.State",
                "sections": "parent",
            })
        argv = calls[0].argv if calls else []
        ok = (
            argv[1:3] == ["show", "880"]
            and "--refresh" in argv
            and _trailing(argv, "--fields") == "System.State"
            and _trailing(argv, "--sections") == "parent"
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.show.refresh-and-projection-both-forward",
            "ok": ok,
            "argv": argv,
        })

        # 3: tree + projection REFUSES at wrapper; no subprocess spawned
        with rebound(runner, settings), intercept(
            returncode=0, stdout=""
        ) as calls:
            surfaced = tools.show({
                "work_item_id": 880,
                "tree": True,
                "fields": "System.State",
            })
        no_subprocess = len(calls) == 0
        parsed = json.loads(surfaced) if isinstance(surfaced, str) else {}
        err = (parsed.get("error") or "") if isinstance(parsed, dict) else ""
        ok = (
            no_subprocess
            and "tree is incompatible with fields/sections" in err
        )
        passed &= ok
        results.append({
            "id": "offline.show.tree-plus-projection-refuses",
            "ok": ok,
            "expected": "no subprocess, wrapper returns JSON error",
            "surfaced": surfaced,
        })

        # 4: legacy show path preserved when neither flag set
        with rebound(runner, settings), intercept(
            returncode=0, stdout='{"id": 880}'
        ) as calls:
            tools.show({"work_item_id": 880})
        argv = calls[0].argv if calls else []
        ok = (
            argv[1:3] == ["show", "880"]
            and "--fields" not in argv
            and "--sections" not in argv
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.show.legacy-preserved-when-no-projection",
            "ok": ok,
            "argv": argv,
        })

        # 5: show_batch csv ids + projection; no fabricated --refresh
        with rebound(runner, settings), intercept(
            returncode=0, stdout='{"contractVersion": 1, "items": []}'
        ) as calls:
            tools.show_batch({
                "ids": [880, 877, 880, 878],  # dupes deduplicated in order
                "fields": "System.Title",
                "sections": "links",
            })
        argv = calls[0].argv if calls else []
        ok = (
            argv[1:3] == ["show-batch", "880,877,878"]
            and "--refresh" not in argv
            and _trailing(argv, "--fields") == "System.Title"
            and _trailing(argv, "--sections") == "links"
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.show-batch.projection-forwarded-no-refresh",
            "ok": ok,
            "argv": argv,
        })

        # 6: process description with type + sections + fields
        with rebound(runner, settings), intercept(
            returncode=0,
            stdout='{"contractVersion": "process-description-compact/1"}',
        ) as calls:
            tools.process_description({
                "work_item_type": "Niflheim.Bug",
                "sections": "fields,requirements",
                "fields": "System.State,Custom.FalsificationCriteria",
            })
        argv = calls[0].argv if calls else []
        ok = (
            argv[1:4] == ["process", "description", "Niflheim.Bug"]
            and _trailing(argv, "--sections") == "fields,requirements"
            and _trailing(argv, "--fields")
                == "System.State,Custom.FalsificationCriteria"
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.process-description.projection-flags-forwarded",
            "ok": ok,
            "argv": argv,
        })

        # 7: process description without type but with sections refuses
        with rebound(runner, settings), intercept(
            returncode=0, stdout=""
        ) as calls:
            surfaced = tools.process_description({"sections": "fields"})
        no_subprocess = len(calls) == 0
        parsed = json.loads(surfaced) if isinstance(surfaced, str) else {}
        ok = (
            no_subprocess
            and isinstance(parsed, dict)
            and "work_item_type is required" in (parsed.get("error") or "")
        )
        passed &= ok
        results.append({
            "id": "offline.process-description.missing-type-refuses",
            "ok": ok,
            "surfaced": surfaced,
        })

        # 8: process description with only type stays on legacy path
        with rebound(runner, settings), intercept(
            returncode=0, stdout='{"types": []}'
        ) as calls:
            tools.process_description({"work_item_type": "Niflheim.Task"})
        argv = calls[0].argv if calls else []
        ok = (
            argv[1:4] == ["process", "description", "Niflheim.Task"]
            and "--sections" not in argv
            and "--fields" not in argv
            and _argv_ok_trailing(argv)
        )
        passed &= ok
        results.append({
            "id": "offline.process-description.legacy-preserved-when-no-projection",
            "ok": ok,
            "argv": argv,
        })

        # 9: nonzero exit -> TwigError -> {error} JSON, ANSI stripped
        with rebound(runner, settings), intercept(
            returncode=7, stdout="",
            stderr="\x1b[31munknown section: bogus\x1b[0m",
        ):
            wrapper_surfaced = tools.show({
                "work_item_id": 880,
                "sections": "bogus",
            })
        parsed = (
            json.loads(wrapper_surfaced)
            if isinstance(wrapper_surfaced, str) else {}
        )
        err = parsed.get("error") or ""
        ok = (
            isinstance(parsed, dict)
            and "unknown section: bogus" in err
            and "\x1b[" not in err
        )
        passed &= ok
        results.append({
            "id": "offline.show.error-propagates-and-ansi-stripped",
            "ok": ok,
            "surfaced": wrapper_surfaced,
        })

        # 10: schemas expose projection shape; show_batch has NO refresh
        show_props = schemas.SHOW["parameters"]["properties"]
        show_batch_props = schemas.SHOW_BATCH["parameters"]["properties"]
        pd_props = schemas.PROCESS_DESCRIPTION["parameters"]["properties"]
        schema_ok = (
            "fields" in show_props
            and "sections" in show_props
            and "ids" in show_batch_props
            and schemas.SHOW_BATCH["parameters"]["required"] == ["ids"]
            and "refresh" not in show_batch_props
            and "sections" in pd_props
            and "fields" in pd_props
        )
        passed &= schema_ok
        results.append({
            "id": "offline.schemas.projection-shape-exposed",
            "ok": schema_ok,
            "show_props": sorted(show_props.keys()),
            "show_batch_props": sorted(show_batch_props.keys()),
            "process_description_props": sorted(pd_props.keys()),
        })

        # 11: show_batch rejects non-integer id without spawning
        with rebound(runner, settings), intercept(
            returncode=0, stdout=""
        ) as calls:
            surfaced = tools.show_batch({"ids": [880, "nope"]})
        parsed = json.loads(surfaced) if isinstance(surfaced, str) else {}
        ok = (
            len(calls) == 0
            and isinstance(parsed, dict)
            and "must be an integer" in (parsed.get("error") or "")
        )
        passed &= ok
        results.append({
            "id": "offline.show-batch.non-integer-refuses",
            "ok": ok,
            "surfaced": surfaced,
        })

    report = {
        "mode": "offline",
        "plugin_root": str(harness.PLUGIN_ROOT),
        "harness": str(_HARNESS),
        "passed": passed,
        "assertions": results,
        "honest_limits": [
            "not a fresh Hermes session",
            "no build, no ADO calls, no credentials used",
            "subprocess.run is monkey-patched; the twig binary is never executed",
            "exercises the staged patched copy — install remains AB#882 / parent-owned",
        ],
    }
    print(json.dumps(report, indent=2))
    return 0 if passed else 1


# ── LIVE mode ───────────────────────────────────────────────────────────


def run_live(harness, workspace: Path, work_item_id: int,
             source_dll: Path, type_ref: str) -> int:
    import shutil
    runner, tools, _ = harness._load_plugin_modules()
    results: list[dict[str, Any]] = []
    passed = True

    if not source_dll.is_file():
        harness._fail(f"--source-dll not found: {source_dll}")
    if not shutil.which("dotnet"):
        harness._fail("dotnet not on PATH; run under a shell that can reach the SDK")

    caller_ctx = harness._read_caller_org_project()
    if caller_ctx is None:
        harness._fail(
            "no twig.json in current directory; run from a workspace whose "
            "organization/project matches the credential you want the probe "
            "to reuse (read-only)."
        )
    org, project = caller_ctx

    caller_home = Path(os.environ.get("HOME", str(Path.home())))
    if not (caller_home / ".twig" / ".refresh-token").is_file():
        harness._fail(
            f"$HOME/.twig/.refresh-token missing under {caller_home}; run "
            "'twig auth login' in the caller's profile, then retry."
        )

    if workspace.exists() and any(workspace.iterdir()):
        harness._fail(f"--workspace must be a fresh empty directory: {workspace}")
    harness._write_disposable_workspace(workspace, org, project)
    launcher = harness._make_dotnet_launcher(workspace, source_dll)

    settings = {
        "workspace_dir": str(workspace),
        "twig_home": str(caller_home),
        "twig_bin": str(launcher),
    }

    real_run = subprocess.run

    def _sniff(bucket: list[Any]):
        def _run(argv, cwd=None, env=None, **kw):
            bucket.append(harness._RecordedCall(
                argv, str(cwd) if cwd else None, env or {},
            ))
            return real_run(argv, cwd=cwd, env=env, **kw)
        return _run

    def _call(fn, args, bucket):
        with harness._rebound(runner, settings):
            subprocess.run = _sniff(bucket)  # type: ignore[assignment]
            try:
                return fn(args)
            finally:
                subprocess.run = real_run  # type: ignore[assignment]

    def _parse(payload):
        try:
            return json.loads(payload) if isinstance(payload, str) else payload
        except (TypeError, json.JSONDecodeError):
            return None

    def _chars(x: Any) -> int:
        if isinstance(x, str):
            return len(x)
        try:
            return len(json.dumps(x, ensure_ascii=False))
        except (TypeError, ValueError):
            return -1

    # ── show, projection ─────────────────────────────────────
    calls1: list[Any] = []
    payload1 = _call(tools.show, {
        "work_item_id": work_item_id,
        "refresh": True,
        "fields": "System.Title,System.State",
        "sections": "parent",
    }, calls1)
    argv = calls1[0].argv if calls1 else []
    parsed = _parse(payload1)
    argv_ok = (
        argv[1:2] == ["show"]
        and str(work_item_id) in argv
        and "--refresh" in argv
        and _trailing(argv, "--fields") == "System.Title,System.State"
        and _trailing(argv, "--sections") == "parent"
        and _argv_ok_trailing(argv)
    )
    cwd_ok = calls1[0].cwd == str(workspace) if calls1 else False
    envelope_ok = isinstance(parsed, dict) and "contractVersion" in parsed
    ok = argv_ok and cwd_ok and envelope_ok
    passed &= ok
    results.append({
        "id": "live.show.projection-envelope-observed",
        "ok": ok,
        "argv_ok": argv_ok, "cwd_ok": cwd_ok, "envelope_ok": envelope_ok,
        "argv": argv,
        "cwd": calls1[0].cwd if calls1 else None,
        "payload_keys": sorted((parsed or {}).keys()) if isinstance(parsed, dict) else None,
    })

    # ── show, legacy full ───────────────────────────────────
    calls2: list[Any] = []
    payload2 = _call(tools.show, {
        "work_item_id": work_item_id, "refresh": True,
    }, calls2)
    argv2 = calls2[0].argv if calls2 else []
    parsed2 = _parse(payload2)
    argv2_ok = (
        argv2[1:2] == ["show"]
        and "--fields" not in argv2
        and "--sections" not in argv2
        and _argv_ok_trailing(argv2)
    )
    legacy_ok = (
        isinstance(parsed2, dict)
        and "fields" in parsed2
        and isinstance(parsed2.get("fields"), dict)
        and "contractVersion" not in parsed2
    )
    ok = argv2_ok and legacy_ok
    passed &= ok
    results.append({
        "id": "live.show.legacy-full-preserved-when-no-projection",
        "ok": ok,
        "argv_ok": argv2_ok, "legacy_ok": legacy_ok,
        "argv": argv2,
        "payload_keys": sorted((parsed2 or {}).keys()) if isinstance(parsed2, dict) else None,
    })

    # ── process description, compact envelope ────────────────
    calls3: list[Any] = []
    payload3 = _call(tools.process_description, {
        "work_item_type": type_ref,
        "sections": "requirements",
    }, calls3)
    argv3 = calls3[0].argv if calls3 else []
    parsed3 = _parse(payload3)
    argv3_ok = (
        argv3[1:4] == ["process", "description", type_ref]
        and _trailing(argv3, "--sections") == "requirements"
        and _argv_ok_trailing(argv3)
    )
    envelope3_ok = (
        isinstance(parsed3, dict)
        and parsed3.get("contractVersion") == "process-description-compact/1"
    )
    ok = argv3_ok and envelope3_ok
    passed &= ok
    results.append({
        "id": "live.process-description.compact-envelope-observed",
        "ok": ok,
        "argv_ok": argv3_ok, "envelope_ok": envelope3_ok,
        "argv": argv3,
        "contract_version": (parsed3 or {}).get("contractVersion") if isinstance(parsed3, dict) else None,
    })

    # ── process description, legacy full descriptor ──────────
    calls4: list[Any] = []
    payload4 = _call(tools.process_description, {
        "work_item_type": type_ref,
    }, calls4)
    argv4 = calls4[0].argv if calls4 else []
    parsed4 = _parse(payload4)
    argv4_ok = (
        argv4[1:4] == ["process", "description", type_ref]
        and "--sections" not in argv4
        and "--fields" not in argv4
        and _argv_ok_trailing(argv4)
    )
    legacy4_ok = (
        isinstance(parsed4, dict)
        and "contractVersion" not in parsed4
    )
    ok = argv4_ok and legacy4_ok
    passed &= ok
    results.append({
        "id": "live.process-description.legacy-full-preserved-when-no-projection",
        "ok": ok,
        "argv_ok": argv4_ok, "legacy_ok": legacy4_ok,
        "argv": argv4,
        "payload_keys": sorted((parsed4 or {}).keys()) if isinstance(parsed4, dict) else None,
    })

    # ── size comparison, informational ───────────────────────
    results.append({
        "id": "live.response-size-observed",
        "ok": True,
        "show_compact_chars": _chars(payload1),
        "show_legacy_chars": _chars(payload2),
        "process_description_compact_chars": _chars(payload3),
        "process_description_legacy_chars": _chars(payload4),
        "note": (
            "informational; the compact response should be strictly smaller "
            "than the legacy full payload for the same input. Parent's fact "
            "checklist owns the ≥50% target."
        ),
    })

    report = {
        "mode": "live",
        "plugin_root": str(harness.PLUGIN_ROOT),
        "harness": str(_HARNESS),
        "workspace_dir": str(workspace),
        "twig_home": str(caller_home),
        "twig_launcher": str(launcher),
        "source_dll": str(source_dll),
        "work_item_id": work_item_id,
        "type_ref": type_ref,
        "passed": passed,
        "assertions": results,
        "honest_limits": [
            "not a fresh Hermes agent session",
            "read-only: only twig_show and twig_process_description are invoked",
            "single-process; cross-session behaviour is not measured",
            "credential is reused from $HOME/.twig, never copied",
            "exercises the staged patched copy — install remains AB#882 / parent-owned",
        ],
    }
    print(json.dumps(report, indent=2))
    return 0 if passed else 1


# ── entry point ─────────────────────────────────────────────────────────


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="native-projection-probe",
        description="AB#880 Hermes twig-plugin projection probe.",
    )
    parser.add_argument("--offline", action="store_true",
                        help="run the pure offline forwarding assertions.")
    parser.add_argument("--workspace", type=Path,
                        help="fresh disposable workspace_dir for live mode.")
    parser.add_argument("--id", dest="work_item_id", type=int,
                        help="work item id to twig_show in live mode.")
    parser.add_argument("--source-dll", dest="source_dll", type=Path,
                        help="path to a built src/Twig/bin/*/net11.0/twig.dll.")
    parser.add_argument("--type", dest="type_ref",
                        default="Hyperbright.Feature",
                        help=("work item type reference for the live "
                              "process description exercise (defaults to "
                              "Hyperbright.Feature, matching AB#880's "
                              "target item 880)."))
    args = parser.parse_args(argv)
    if args.offline and any([args.workspace, args.work_item_id, args.source_dll]):
        parser.error("--offline is exclusive with live-mode flags.")
    if not args.offline:
        missing = [n for n, v in (("--workspace", args.workspace),
                                  ("--id", args.work_item_id),
                                  ("--source-dll", args.source_dll)) if v is None]
        if missing:
            parser.error(f"missing required live-mode flag(s): {', '.join(missing)}. "
                         "Use --offline for the pure forwarding assertions.")
    return args


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(list(sys.argv[1:] if argv is None else argv))
    harness, _ = _import_harness()
    if args.offline:
        return run_offline(harness)
    return run_live(
        harness, args.workspace, args.work_item_id,
        args.source_dll, args.type_ref,
    )


if __name__ == "__main__":
    sys.exit(main())
