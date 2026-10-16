"""Measure approved cached native paths; coordinator runs after isolated publish.

Real commands only, no credentials/database shortcuts/network refresh. Evidence
contains dimensions/timings, not item text, HTML, credentials or auth responses.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parent
WORKSPACE = Path("C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic")
HOME = "C:/Users/dangreen/.twig-tui-epic-tracking"
BENCH = "1"
BINDING = "bind-e6bf6f457d49d26cc09c542d"
IDENTITY = "id-5d7fbf575b88e492bc95c948"
ITEMS = (1141, 1142, 1143)
WIDTHS = (45, 80, 120)
SGR = re.compile(r"\x1b\[[0-9;:]*m")


def objects(text):
    decoder = json.JSONDecoder()
    offset = 0
    result = []
    while offset < len(text):
        while offset < len(text) and text[offset].isspace():
            offset += 1
        if offset == len(text):
            break
        value, offset = decoder.raw_decode(text, offset)
        result.append(value)
    return result


def detail_document(value):
    # Native JSON renderer emits a single record as an object, not an inferred ANSI ID.
    if not isinstance(value, dict):
        raise RuntimeError("Native detail envelope is not an object")
    if value.get("workItemId") not in ITEMS or str(value.get("benchId")) != BENCH or value.get("bindingId") != BINDING or value.get("identityId") != IDENTITY or value.get("version") != 1:
        raise RuntimeError("Native detail returned a changed or unsupported captured origin")
    ansi = value.get("ansi")
    if not isinstance(ansi, str) or not isinstance(value.get("title"), str):
        raise RuntimeError("Native detail is missing ANSI/title")
    plain = SGR.sub("", ansi).replace("\r\n", "\n")
    if "\x1b" in plain or any(ord(ch) < 32 and ch not in "\n\t\r" for ch in plain):
        raise RuntimeError("Native detail contains unsupported terminal controls")
    return {"itemId": value["workItemId"], "ansiBytes": len(ansi.encode("utf-8")), "plainChars": len(plain),
            "physicalRows": len(plain.splitlines()), "titleChars": len(value["title"])}


def status_origin(value):
    if not isinstance(value, dict) or value.get("bindingId") != BINDING or value.get("identityId") != IDENTITY:
        raise RuntimeError("Connection status changed; stop and explicitly reconnect outside experiment")
    if Path(value.get("worktreeRoot", "")).resolve() != WORKSPACE.resolve():
        raise RuntimeError("Connection status returned another workspace")
    # Compare the whole returned status, not just opaque IDs; never persist its content.
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def summary(values):
    ordered = sorted(values)
    return {"count": len(ordered), "minimumMs": ordered[0], "medianMs": statistics.median(ordered),
            "p95NearestRankMs": ordered[math.ceil(0.95 * len(ordered)) - 1], "maximumMs": ordered[-1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gate", required=True, type=Path)
    parser.add_argument("--native", type=Path, default=ROOT / "generated/publish/win-x64/twig.exe")
    parser.add_argument("--samples", type=int, default=20, help="Warm samples per approved item/width; first results excluded")
    parser.add_argument("--output", type=Path, default=ROOT / "generated/results")
    args = parser.parse_args()
    gate = json.loads(args.gate.read_text(encoding="utf-8"))
    states = {row["id"]: row["requestedFields"]["System.State"]["value"] for row in gate.get("readbacks", [])}
    if gate.get("status") != "START_GATE_OPEN" or states.get(1142) != "Doing" or states.get(1143) != "Doing":
        raise RuntimeError("Verified start gate required")
    if not 1 <= args.samples <= 100:
        parser.error("--samples must be1..100")
    native = args.native.resolve()
    if not native.is_relative_to((ROOT / "generated").resolve()) or not native.is_file():
        raise RuntimeError("Run only the owned generated native executable, never a stable install")
    output = args.output.resolve()
    if not output.is_relative_to((ROOT / "generated").resolve()) or output.exists():
        raise RuntimeError("Use a fresh owned generated result directory; never overwrite evidence")
    output.mkdir(parents=True)
    rows = []
    sequence = 0
    env = dict(os.environ, TWIG_USER_HOME=HOME, TWIG_TELEMETRY_ENDPOINT="", NO_COLOR="1")
    for name in ("TWIG_1142_REPEAT", "TWIG_1142_PROBE", "TWIG_1142_PROBE_OUTPUT"):
        env.pop(name, None)

    def run(argv, *, repetitions=1, instrument=True):
        nonlocal sequence
        sequence += 1
        capture = output / f"probe-{sequence:04d}.json"
        child_env = dict(env, TWIG_1142_REPEAT=str(repetitions))
        if instrument:
            child_env.update(TWIG_1142_PROBE="1", TWIG_1142_PROBE_OUTPUT=str(capture))
        start = time.perf_counter_ns()
        completed = subprocess.run([str(native), *argv], cwd=WORKSPACE, env=child_env,
                                   capture_output=True, encoding="utf-8", errors="strict", timeout=180)
        elapsed = (time.perf_counter_ns() - start) / 1_000_000
        if completed.returncode:
            # Do not persist raw stderr or stdout, which might contain work/account content.
            (output / "stopped.json").write_text(json.dumps({"sequence": sequence, "exitCode": completed.returncode,
                "wallMs": elapsed, "reason": "Native refusal; series stopped without retry/retarget/reconnect. Raw content not persisted."}, indent=2), encoding="utf-8")
            raise RuntimeError(f"Native command refused at sequence{sequence}, exit{completed.returncode}; no retry")
        values = objects(completed.stdout)
        probes = json.loads(capture.read_text(encoding="utf-8"))["observations"] if instrument else []
        return values, elapsed, probes

    captured_status = None

    def status():
        nonlocal captured_status
        values, wall, probes = run(["connection", "status", "-o", "json"])
        if len(values) != 1:
            raise RuntimeError("Connection status returned unexpected envelope count")
        current = status_origin(values[0])
        if captured_status is not None and current != captured_status:
            raise RuntimeError("Full connection status changed; stop without reconnect")
        captured_status = current
        return wall, probes

    for item in ITEMS:
        for width in WIDTHS:
            detail_args = ["bench", "detail", str(item), "--width", str(width), "-o", "json",
                           "--expect-bench", BENCH, "--expect-binding", BINDING, "--expect-identity", IDENTITY]
            # First cached read captures process-first (not OS-cold); then warm one-shots.
            for sample in range(args.samples + 1):
                bracket_start = time.perf_counter_ns()
                pre_wall, pre_probe = status()
                values, native_wall, probes = run(detail_args)
                if len(values) != 1:
                    raise RuntimeError("One-shot detail returned unexpected envelope count")
                dimension_start = time.perf_counter_ns()
                dimensions = detail_document(values[0])
                decode_guard_ms = (time.perf_counter_ns() - dimension_start) / 1_000_000
                post_wall, post_probe = status()
                rows.append({"mode": "process-first-os-cache-uncontrolled" if sample == 0 else "warm-one-shot",
                    "sample": sample, "width": width, **dimensions, "nativeWallMs": native_wall,
                    "preStatusWallMs": pre_wall, "postStatusWallMs": post_wall,
                    "dimensionsAndGuardMs": decode_guard_ms,
                    "bracketedWallMs": (time.perf_counter_ns() - bracket_start) / 1_000_000,
                    "stages": probes, "statusStages": {"pre": pre_probe, "post": post_probe}})
            # Control differs only by disabled numeric probes, same startup exclusions.
            pre_wall, _ = status()
            values, control_wall, _ = run(detail_args, instrument=False)
            if len(values) != 1:
                raise RuntimeError("Control detail returned unexpected envelope count")
            post_wall, _ = status()
            rows.append({"mode": "warm-one-shot-probes-disabled-control", "sample": 0, "width": width,
                         **detail_document(values[0]), "nativeWallMs": control_wall,
                         "preStatusWallMs": pre_wall, "postStatusWallMs": post_wall})
            # Same actual command/DI/store, fresh native admission/final checks every call.
            pre_wall, _ = status()
            values, reused_process_wall, probes = run(detail_args, repetitions=args.samples + 1)
            post_wall, _ = status()
            if len(values) != args.samples + 1:
                raise RuntimeError("Reuse detail returned unexpected envelope count")
            for sample, value in enumerate(values):
                iterations = [row for row in probes if row["iteration"] == sample]
                operation = [row["milliseconds"] for row in iterations if row["name"] == "detail-operation-with-disposal"]
                if len(operation) != 1:
                    raise RuntimeError("Missing real reuse operation timing")
                rows.append({"mode": "reuse-process-first" if sample == 0 else "warm-reused-state",
                    "sample": sample, "width": width, **detail_document(value), "operationMs": operation[0],
                    "reusedProcessWallMs": reused_process_wall, "preStatusWallMs": pre_wall,
                    "postStatusWallMs": post_wall, "stages": iterations})

    groups = {}
    for item in ITEMS:
        for width in WIDTHS:
            for mode, key in (("warm-one-shot", "nativeWallMs"), ("warm-reused-state", "operationMs")):
                selected = [row[key] for row in rows if row["itemId"] == item and row["width"] == width and row["mode"] == mode]
                groups[f"{item}/{width}/{mode}"] = summary(selected)
    evidence = {"capturedUtc": datetime.now(timezone.utc).isoformat(), "sourceCommit": "39e10719870437e011567155c328aee03b80c835",
        "executable": str(native), "executableSha256": hashlib.sha256(native.read_bytes()).hexdigest(),
        "cwd": str(WORKSPACE), "metadataHome": HOME, "warmSamplesPerCell": args.samples,
        "widths": WIDTHS, "corpus": ITEMS, "observations": rows, "summaries": groups,
        "limits": ["Process-first is not OS-cold; no cache eviction or credential reset.",
                   "Startup installer/old-binary cleanup intentionally absent in snapshot.",
                   "Store total/constructor are nested in composition/service resolution; never sum nested rows.",
                   "Reuse operation includes lease disposal but excludes composition/process startup and25ms idle gaps.",
                   "One-shot native wall versus reuse inner operation is an overhead comparison, not isolated cause proof.",
                   "Reuse emission is pipe-captured; no Go terminal event queue/layout/paint measured.",
                   "Bracketed wall includes real status before/after but is not input-to-painted-frame.",
                   "Only approved cached Epic/frontier corpus; not broad production qualification.",
                   "Twenty descriptive warm samples do not approve any latency target."]}
    (output / "results.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Measured{len(rows)} numeric rows; preserved results at{output / 'results.json'}. No sync, tracker or installed changes requested.")


if __name__ == "__main__":
    main()
