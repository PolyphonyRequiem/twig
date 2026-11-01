"""Independently verify source/project absence; compiled PE regressions prove the binary boundary."""
from pathlib import Path
import argparse
import io
import json
import re
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parent.parent
DECLARATION = re.compile(r"\b(class|interface|enum|struct|record)\s+(?:struct\s+)?([A-Z]\w*)")


def source_metrics(sources):
    projects = {}
    for name, text in sources.items():
        project = Path(name).parts[1]
        metric = projects.setdefault(project, {"files": 0, "lines": 0, "types": 0, "ports": 0})
        metric["files"] += 1
        metric["lines"] += len(text.splitlines())
        # Source declarations, not generated/compiled type counts; apply the same heuristic to both snapshots.
        code = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"', "", text)
        declarations = DECLARATION.findall(code)
        metric["types"] += len(declarations)
        metric["ports"] += sum(kind == "interface" for kind, _ in declarations)
    return projects


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", help="Optional Git revision for same-method production source metrics")
    args = parser.parse_args()
    failures = []
    sources = {}
    for path in (ROOT / "src").rglob("*"):
        if any(part in {"bin", "obj"} for part in path.relative_to(ROOT).parts) or not path.is_file():
            continue
        relative = path.relative_to(ROOT).as_posix()
        if path.suffix == ".cs":
            text = path.read_text(encoding="utf-8-sig")
            sources[relative] = text
            if "Twig.Domain" in text:
                failures.append(relative)
        if relative.startswith("src/Twig.Domain/"):
            failures.append(relative)
        if path.name.startswith("PublicAPI.") and "Twig.Domain" in path.read_text(encoding="utf-8-sig"):
            failures.append(relative)
    for path in ROOT.rglob("*"):
        if any(part in {"bin", "obj", ".git", "artifacts"} for part in path.relative_to(ROOT).parts) or not path.is_file():
            continue
        if path.suffix in {".csproj", ".props", ".targets", ".slnx"}:
            if any(token in path.read_text(encoding="utf-8-sig") for token in ("Twig.Domain", "Twig.TestKit")):
                failures.append(path.relative_to(ROOT).as_posix())
    for name in ("ci.yml", "release.yml"):
        path = ROOT / ".github" / "workflows" / name
        if "src/Twig.Domain/Twig.Domain.csproj" in path.read_text(encoding="utf-8"):
            failures.append(path.relative_to(ROOT).as_posix())
    report = {"failures": sorted(set(failures)), "production": source_metrics(sources)}
    if args.baseline:
        archive = subprocess.check_output(["git", "archive", "--format=tar", args.baseline, "src"], cwd=ROOT)
        with tarfile.open(fileobj=io.BytesIO(archive)) as tree:
            baseline = {member.name: tree.extractfile(member).read().decode("utf-8-sig")
                        for member in tree.getmembers() if member.isfile() and member.name.endswith(".cs")}
        report["baseline"] = source_metrics(baseline)
    print(json.dumps(report, indent=2, sort_keys=True))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
