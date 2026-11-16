"""Independently verify source/project absence; compiled PE regressions prove the binary boundary."""
from pathlib import Path
import argparse
import json
import re
import tempfile
import unittest

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


def inspect(root):
    if not (root / "Twig.slnx").is_file() or not (root / "src" / "Twig.Core" / "Twig.Core.csproj").is_file():
        raise ValueError("current solution and Core project must exist")
    failures = []
    sources = {}
    for path in (root / "src").rglob("*"):
        if any(part in {"bin", "obj"} for part in path.relative_to(root).parts) or not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if path.suffix == ".cs":
            text = path.read_text(encoding="utf-8-sig")
            sources[relative] = text
            if "Twig.Domain" in text:
                failures.append(relative)
        if relative.startswith("src/Twig.Domain/"):
            failures.append(relative)
        if path.name.startswith("PublicAPI.") and "Twig.Domain" in path.read_text(encoding="utf-8-sig"):
            failures.append(relative)
    if not sources or not any(name.startswith("src/Twig.Core/") for name in sources):
        raise ValueError("current production/Core source inventory must not be empty")
    for path in root.rglob("*"):
        if any(part in {"bin", "obj", ".git", "artifacts"} for part in path.relative_to(root).parts) or not path.is_file():
            continue
        if path.suffix in {".csproj", ".props", ".targets", ".slnx"}:
            if any(token in path.read_text(encoding="utf-8-sig") for token in ("Twig.Domain", "Twig.TestKit")):
                failures.append(path.relative_to(root).as_posix())
    for name in ("ci.yml", "release.yml"):
        path = root / ".github" / "workflows" / name
        if "src/Twig.Domain/Twig.Domain.csproj" in path.read_text(encoding="utf-8"):
            failures.append(path.relative_to(root).as_posix())
    return {"failures": sorted(set(failures)), "production": source_metrics(sources)}


class DomainCutTests(unittest.TestCase):
    def test_scanner_rejects_empty_roots_and_detects_a_real_source_reference(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(ValueError, "must exist"):
                inspect(root)
            (root / "Twig.slnx").write_text("<Solution />")
            core = root / "src" / "Twig.Core"
            core.mkdir(parents=True)
            (core / "Twig.Core.csproj").write_text("<Project />")
            with self.assertRaisesRegex(ValueError, "must not be empty"):
                inspect(root)
            workflows = root / ".github" / "workflows"
            workflows.mkdir(parents=True)
            for name in ("ci.yml", "release.yml"):
                (workflows / name).write_text("jobs: {}")
            source = core / "Example.cs"
            source.write_text("public sealed class Example {}")
            self.assertEqual(inspect(root)["failures"], [])
            source.write_text("using Twig.Domain; public sealed class Example {}")
            self.assertEqual(inspect(root)["failures"], ["src/Twig.Core/Example.cs"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()
    if args.selftest:
        result = unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(DomainCutTests))
        return int(not result.wasSuccessful())
    report = inspect(ROOT)
    print(json.dumps(report, indent=2, sort_keys=True))
    return int(bool(report["failures"]))


if __name__ == "__main__":
    raise SystemExit(main())
