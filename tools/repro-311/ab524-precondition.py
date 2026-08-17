#!/usr/bin/env python3
"""AB#524 mechanism probe — DETERMINISTIC, does not depend on the 1-in-12 stall.

Claim under test: Twig.Tui.Tests races the Terminal.Gui module cctor from
MULTIPLE parallel xunit collections, whereas Twig.Cli.Tests cannot because it
disables parallelization.

This does not try to reproduce the deadlock (that needs an unlucky interleaving).
It establishes the PRECONDITION the deadlock requires: >1 collection able to
trigger the Terminal.Gui module initializer concurrently.

Reports, per test assembly:
  - whether DisableTestParallelization is set
  - how many test classes (= parallel collections by default)
  - how many of them reference Terminal.Gui types
"""
import pathlib, re, sys

REPO = pathlib.Path("/home/polyphonyrequiem/repos/twig-524")
TESTS = REPO / "tests"

def analyse(d: pathlib.Path):
    cs = sorted(p for p in d.glob("*.cs"))
    disabled = False
    for p in list(cs) + sorted(d.glob("**/*.cs")):
        try:
            t = p.read_text(errors="replace")
        except Exception:
            continue
        if "DisableTestParallelization = true" in t or "DisableTestParallelization=true" in t:
            disabled = True
            break
    classes, tg_classes = [], []
    for p in sorted(d.glob("**/*.cs")):
        t = p.read_text(errors="replace")
        found = re.findall(r"^public (?:sealed )?class (\w+)", t, re.M)
        if not found:
            continue
        # only count classes that actually hold tests
        if "[Fact]" not in t and "[Theory]" not in t:
            continue
        classes += found
        # A class can trigger the Terminal.Gui module cctor DIRECTLY (it names a
        # Terminal.Gui type) or TRANSITIVELY (it touches Twig.Tui types, which
        # are compiled against Terminal.Gui). Counting only direct references
        # undercounts: PendingChangeStoreSinkTests references only Twig.Tui.Views
        # yet was one of the four tests in flight in the AB#390 capture.
        direct = bool(re.search(r"\bTerminal\.Gui\b", t))
        transitive = bool(re.search(r"\bTwig\.Tui\b", t))
        if direct or transitive:
            tg_classes += [(c, "direct" if direct else "transitive") for c in found]
    return disabled, classes, tg_classes

print(f"{'assembly':<34}{'parallel?':<12}{'test classes':<15}{'touch Terminal.Gui'}")
print("-" * 90)
rows = []
for d in sorted(TESTS.iterdir()):
    if not d.is_dir() or not (d / f"{d.name}.csproj").exists():
        continue
    disabled, classes, tg = analyse(d)
    par = "DISABLED" if disabled else "ENABLED"
    print(f"{d.name:<34}{par:<12}{len(classes):<15}{len(tg)}")
    rows.append((d.name, disabled, len(classes), len(tg), tg))

print()
print("PRECONDITION for the concurrent-cctor deadlock:")
print("  parallelization ENABLED  AND  >1 test class touching Terminal.Gui")
print()
at_risk = [r for r in rows if not r[1] and r[3] > 1]
for name, disabled, nc, ntg, tg in rows:
    verdict = "AT RISK" if (not disabled and ntg > 1) else "not at risk"
    print(f"  {name:<34}{verdict}")
    if not disabled and ntg > 1:
        print(f"      {ntg} concurrent collections can trigger the module cctor:")
        for c, how in tg:
            print(f"        - {c}  ({how})")

print()
if at_risk:
    print(f"RESULT: {len(at_risk)} assembly(ies) meet the precondition.")
    sys.exit(0)
print("RESULT: no assembly meets the precondition.")
