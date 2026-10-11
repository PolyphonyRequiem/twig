"""Summarize actual numeric Go browser key-to-ready-stdout evidence.

Does not drive gestures, fabricate samples, open credentials or execute product.
Coordinator supplies probe JSONL from real isolated Herdr/ConPTY browser runs.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics

ROOT = Path(__file__).resolve().parent


def stats(values):
    ordered = sorted(values)
    return {"count": len(values), "minimumMs": ordered[0], "medianMs": statistics.median(ordered),
            "maximumMs": ordered[-1], "p95NearestRankMs": ordered[math.ceil(len(ordered) * 0.95) - 1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probes", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "generated/go-gesture-summary.json")
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to((ROOT / "generated").resolve()) or output.exists():
        raise RuntimeError("Preserve evidence: output must be a fresh owned generated path")
    gestures, frame_states, sources = [], [], []
    required_stages = {"input-reader-to-dispatch", "native-pre-authority-command", "native-detail-command-and-streams",
                       "go-parse-and-origin-control-validation", "native-post-authority-command", "response-event-queue",
                       "go-consumer-detail-layout", "ready-frame-layout-and-stdout-write"}
    for source in args.probes:
        source = source.resolve()
        if not source.is_relative_to((ROOT / "generated").resolve()):
            raise RuntimeError("Read only owned generated numeric probes")
        sources.append({"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
        for line in source.read_text(encoding="utf-8").splitlines():
            row = json.loads(line)
            if row.get("kind") == "browser-frame-state":
                frame_states.append(row)
                continue
            if row.get("itemId") not in (1141, 1142, 1143):
                raise RuntimeError("Gesture evidence outside approved cached corpus")
            if row.get("outcome") == "ready-frame-written" and not required_stages <= row.get("stagesMs", {}).keys():
                raise RuntimeError("Missing real gesture pipeline stage; cannot label complete")
            gestures.append(row)
    ready = [row for row in gestures if row.get("outcome") == "ready-frame-written"]
    if not ready:
        raise RuntimeError("No real gesture produced an accepted detail ready frame")
    groups = []
    for key in sorted(set((row["itemId"], row["columns"], row["action"]) for row in ready)):
        rows = [row for row in ready if (row["itemId"], row["columns"], row["action"]) == key]
        groups.append({"itemId": key[0], "columns": key[1], "action": key[2],
                       "gestureToReadyFrame": stats([row["gestureToReadyFrameMs"] for row in rows]),
                       "stages": {stage: stats([row["stagesMs"][stage] for row in rows]) for stage in sorted(required_stages)}})
    result = {"sourceCommit": "0d3c1e7f7f3b6aa3cb75c0770348f961660d3bca", "sourceProbes": sources,
              "observedGestureCount": len(gestures), "readyFrameCount": len(ready), "groups": groups,
              "observations": gestures, "browserFrameStates": frame_states,
              "limits": ["Starts at actual parsed key ingress, not externally delivered write/input injection timestamp.",
                         "Ends at actual first admitted ready detail frame stdout write completion into ConPTY, not emulator consume/composite or physical photons.",
                         "All existing native pre/detail/post checks and Go consumer generation/origin/control validation remain.",
                         "Small warm convenience sample under background host load is descriptive, not approved latency qualification.",
                         "Local numeric log output after frame is excluded from gesture end clock but can affect later scheduling.",
                         "Native timing evidence from earlier387-row experiment remains separately preserved, not overwritten."]}
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Summarized{len(ready)} actual ready frames from{len(gestures)} gestures at{output}")


if __name__ == "__main__":
    main()
