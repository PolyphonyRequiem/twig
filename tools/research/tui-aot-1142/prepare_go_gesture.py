"""Archive pinned actual Go browser and add numeric gesture/frame probes only.

Coordinator owns execution. Read-only source repo; no credential/store/network/
tracker/stable install mutation. Existing native authority and detail behavior stay.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parent
GO_REPO = Path("C:/Users/dangreen/projects/twig-herdr")
COMMIT = "0d3c1e7f7f3b6aa3cb75c0770348f961660d3bca"


def replace_once(text, old, new, label):
    if text.count(old) != 1:
        raise RuntimeError(f"Pinned Go source anchor drift: {label}, count={text.count(old)}")
    return text.replace(old, new, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gate", required=True, type=Path)
    parser.add_argument("--native-bundle", type=Path, default=Path("C:/Users/dangreen/.local/bin/twig-bench-native"))
    args = parser.parse_args()
    gate = json.loads(args.gate.read_text(encoding="utf-8"))
    states = {row["id"]: row["requestedFields"]["System.State"]["value"] for row in gate.get("readbacks", [])}
    if gate.get("status") != "START_GATE_OPEN" or states.get(1142) != "Doing" or states.get(1143) != "Doing":
        raise RuntimeError("Verified Doing start gate required")
    snapshot = ROOT / "generated/go-gesture-source"
    binary = ROOT / "generated/go-gesture-bin"
    if snapshot.exists() or binary.exists():
        raise RuntimeError("Preserve existing generated evidence; use fresh owned experiment namespace")
    archive = subprocess.run(["git", "archive", "--format=tar", COMMIT], cwd=GO_REPO, capture_output=True, check=True, timeout=60).stdout
    snapshot.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        source.extractall(snapshot, filter="data")
    hashes = {p.relative_to(snapshot).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in snapshot.rglob("*") if p.is_file()}

    path = snapshot / "internal/panel/run.go"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, "type runtime struct {", "type runtime struct {\n\tprofile1142 *profile1142State", "runtime probe state")
    text = replace_once(text, "type event struct {", "type event struct {\n\tprofile1142 *profile1142Ingress", "input event timestamp")
    text = replace_once(text, "\trt := newRuntime(cfg)\n\treturn rt.run(requests)", "\trt := newRuntime(cfg)\n\tif err := profile1142Initialize(rt); err != nil { return err }\n\tdefer profile1142Close(rt)\n\treturn rt.run(requests)", "probe initialization/disposal")
    text = replace_once(text, "\t\t\tcase evKey:\n\t\t\t\trt.handleKey(ev.key)", "\t\t\tcase evKey:\n\t\t\t\tprofile1142Begin(rt, ev.profile1142)\n\t\t\t\trt.handleKey(ev.key)", "actual event dispatch start")
    text = replace_once(text, "\t\t\tfor _, wrapped := range flattenEvent(ev) {\n\t\t\t\trt.emit(wrapped)", "\t\t\tfor _, wrapped := range flattenEvent(ev) {\n\t\t\t\twrapped.profile1142 = profile1142Receive(wrapped)\n\t\t\t\trt.emit(wrapped)", "actual parsed input receipt")
    path.write_text(text, encoding="utf-8", newline="\n")

    path = snapshot / "internal/panel/detail.go"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, "\tctx, cancel := context.WithTimeout(rt.ctx, 30*time.Second)", "\tprofileGesture := profile1142Issue(rt, gen)\n\tctx, cancel := context.WithTimeout(rt.ctx, 30*time.Second)", "bind request generation")
    text = replace_once(text, "\t\terr := rt.semanticAuthority(ctx, binding)", "\t\tprofilePre := time.Now()\n\t\terr := rt.semanticAuthority(ctx, binding)\n\t\tprofile1142Stage(profileGesture, \"native-pre-authority-command\", profilePre)", "native pre admission")
    text = replace_once(text, "\t\t\toutput, err = nativeOutput(ctx, rt.nativePath, rt.cfg.Cwd, semanticArgs(args, binding)...)\n\t\t\tif err == nil {\n\t\t\t\tdocument, err = parseDetail(output, binding, benchID, id)\n\t\t\t}", "\t\t\tprofileCommand := time.Now()\n\t\t\toutput, err = nativeOutput(ctx, rt.nativePath, rt.cfg.Cwd, semanticArgs(args, binding)...)\n\t\t\tprofile1142Stage(profileGesture, \"native-detail-command-and-streams\", profileCommand)\n\t\t\tif err == nil {\n\t\t\t\tprofileParse := time.Now()\n\t\t\t\tdocument, err = parseDetail(output, binding, benchID, id)\n\t\t\t\tprofile1142Stage(profileGesture, \"go-parse-and-origin-control-validation\", profileParse)\n\t\t\t}", "actual native detail/parse")
    text = replace_once(text, "\t\tadmissionErr := rt.postSemanticAdmission(binding)\n\t\trt.emit(event{kind: evDetailLoaded", "\t\tprofilePost := time.Now()\n\t\tadmissionErr := rt.postSemanticAdmission(binding)\n\t\tprofile1142Stage(profileGesture, \"native-post-authority-command\", profilePost)\n\t\tprofile1142Queued(profileGesture)\n\t\trt.emit(event{kind: evDetailLoaded", "post and response queue")
    text = replace_once(text, "\tdetail.document = ev.detail\n\tdetail.cols = 0\n\trt.layoutDetail(rt.contentCols())\n\trt.draw()", "\tprofileConsumer := profile1142Consumer(rt, ev.token)\n\tprofileLayout := time.Now()\n\tdetail.document = ev.detail\n\tdetail.cols = 0\n\trt.layoutDetail(rt.contentCols())\n\tprofile1142Stage(profileConsumer, \"go-consumer-detail-layout\", profileLayout)\n\trt.draw()", "real accepted consumer layout")
    path.write_text(text, encoding="utf-8", newline="\n")

    path = snapshot / "internal/panel/render.go"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, '\t"strings"', '\t"strings"\n\t"time"', "render clock import")
    text = replace_once(text, "func (rt *runtime) draw() {", "func (rt *runtime) draw() {\n\tprofileFrame := time.Now()", "frame rendering start")
    text = replace_once(text, '\t\t_, _ = os.Stdout.WriteString(labelSGR.ReplaceAllString(out.String(), ""))\n\t\treturn', '\t\tprofileBytes, profileError := os.Stdout.WriteString(labelSGR.ReplaceAllString(out.String(), ""))\n\t\tprofile1142Paint(rt, profileFrame, profileBytes, profileError)\n\t\treturn', "plain frame write completion")
    text = replace_once(text, "\t_, _ = os.Stdout.WriteString(out.String())\n}", "\tprofileBytes, profileError := os.Stdout.WriteString(out.String())\n\tprofile1142Paint(rt, profileFrame, profileBytes, profileError)\n}", "color frame write completion")
    path.write_text(text, encoding="utf-8", newline="\n")
    (snapshot / "internal/panel/profile1142_gesture.go").write_text((ROOT / "profile1142_gesture.go").read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
    changed = [p.relative_to(snapshot).as_posix() for p in snapshot.rglob("*") if p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest() != hashes.get(p.relative_to(snapshot).as_posix())]

    # Code/native-library payloads only, never workspace data, registry or credentials.
    payloads = {}
    for name in ("twig-bench-native.exe", "e_sqlite3.dll"):
        original = args.native_bundle.resolve() / name
        if not original.is_file():
            raise RuntimeError(f"Existing native code payload missing: {name}")
        target = binary / "twig-bench-native" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        payloads[name] = {"source": str(original), "sha256": hashlib.sha256(target.read_bytes()).hexdigest()}
    manifest = {"sourceCommit": COMMIT, "readOnlySourceRepository": str(GO_REPO), "changedSnapshotFiles": sorted(changed),
        "originalSourceHashes": hashes, "nativeCodeOnlyPayloads": payloads,
        "probeStart": "Real ultraviolet parsed key receipt before event-queue emit", "probeEnd": "First accepted ready detail draw completes stdout write into ConPTY, not physical display", "nativeAuthorityLogicChanged": False}
    (ROOT / "generated/go-gesture-snapshot-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared{snapshot};{len(changed)} isolated files changed; native code payloads copied to{binary}. No Go build/runtime executed.")


if __name__ == "__main__":
    main()
