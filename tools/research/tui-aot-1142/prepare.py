"""Prepare an instrumented NativeAOT source snapshot without touching production outputs.

Coordinator runs this only after the accepted start gate. No build, network, store,
credential, tracker, installed binary, or Go source operations are performed here.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
COMMIT = "39e10719870437e011567155c328aee03b80c835"
INPUTS = ["src/Twig", "src/Twig.Domain", "src/Twig.Infrastructure", "src/Twig.RenderTree",
          "Directory.Build.props", "Directory.Packages.props", "global.json",
          ".github/skills/twig", "docs/commands"]
PROBE = "Twig.Infrastructure.Diagnostics.ProfileProbe"


def replace_once(text, old, new, label):
    if text.count(old) != 1:
        raise RuntimeError(f"Pinned source anchor drift: {label} count={text.count(old)}")
    return text.replace(old, new, 1)


def instrument(snapshot):
    path = snapshot / "src/Twig/Program.cs"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, "    .ConfigureServices(services =>\n    {", "    .ConfigureServices(services =>\n    {\n        var profileComposition = " + PROBE + ".Start();", "composition entry")
    text = replace_once(text, "        services.AddTwigCommands();", "        services.AddTwigCommands();\n        " + PROBE + '.End("composition", profileComposition);', "composition exit")
    text = replace_once(text, "SelfUpdater.CleanupOldBinary();", "// Research snapshot: installed binary cleanup intentionally omitted.", "cleanup exclusion")
    text = replace_once(text, "CompanionStartup.RunFirstRunCheck();", "// Research snapshot: companion installation/download intentionally omitted.", "installer exclusion")
    text = replace_once(text, "\napp.Run(args);\n\n/// <summary>\n/// Detects the git remote URL", "\ntry { app.Run(args); }\nfinally { " + PROBE + ".Flush(); }\n\n/// <summary>\n/// Detects the git remote URL", "flush after native command")
    old = "        => await services.GetRequiredService<BenchDetailCommand>().ExecuteAsync(id: id, width: width, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, sync: sync, ct: ct);"
    new = '''    {
        if (id is not (1141 or 1142 or 1143) || width is not (45 or 80 or 120) || sync || output != "json")
            throw new ArgumentException("Research allows only approved cached items/widths, JSON, and no sync.");
        if (expectBench != "1" || expectBinding != "bind-e6bf6f457d49d26cc09c542d" || expectIdentity != "id-5d7fbf575b88e492bc95c948")
            throw new ArgumentException("Research requires the coordinator's exact captured refusal guards.");
        var resolutionStarted = Twig.Infrastructure.Diagnostics.ProfileProbe.Start();
        var command = services.GetRequiredService<BenchDetailCommand>();
        Twig.Infrastructure.Diagnostics.ProfileProbe.End("leaf-service-resolution", resolutionStarted);
        var count = Twig.Infrastructure.Diagnostics.ProfileProbe.Repetitions();
        for (var index = 0; index < count; index++)
        {
            Twig.Infrastructure.Diagnostics.ProfileProbe.SetIteration(index);
            var operationStarted = Twig.Infrastructure.Diagnostics.ProfileProbe.Start();
            var exit = await command.ExecuteAsync(id: id, width: width, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, sync: false, ct: ct);
            Twig.Infrastructure.Diagnostics.ProfileProbe.End("detail-operation-with-disposal", operationStarted);
            if (exit != 0) return exit;
            // ExecuteAsync's using scopes have disposed before this idle interval.
            if (index + 1 < count) await Task.Delay(25, ct);
        }
        return 0;
    }'''
    text = replace_once(text, old, new, "same-command reuse handler")
    # Reject mutation commands at the process entrance, before SQLite/DI.
    prefix = '''// Isolated research executable: no mutation or arbitrary command route is supported.
if (!(args.Length >= 2 && ((args[0] == "bench" && args[1] == "detail") || (args[0] == "connection" && args[1] == "status")))
    && !(args.Length == 1 && args[0] is "--version" or "--help"))
{
    Console.Error.WriteLine("Research executable supports cached Bench detail and connection status only.");
    return;
}
if (Environment.GetEnvironmentVariable("TWIG_USER_HOME") != "C:/Users/dangreen/.twig-tui-epic-tracking"
    || !string.Equals(Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath("C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic"), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Research requires the approved workspace and metadata home.");

'''
    text = replace_once(text, "// AB#350: the generated argument binder", prefix + "// AB#350: the generated argument binder", "restricted entrance")
    path.write_text(text, encoding="utf-8", newline="\n")

    path = snapshot / "src/Twig/Commands/BenchDetailCommand.cs"
    text = path.read_text(encoding="utf-8")
    probes = [
        ("            using var admission =", "            var profileAdmission = " + PROBE + ".Start();\n            using var admission =", "initial start"),
        ("            var bench = await resolver.ResolveStoredAsync(ct, expectBench);", "            var bench = await resolver.ResolveStoredAsync(ct, expectBench);\n            " + PROBE + '.End("initial-admission", profileAdmission);', "initial end"),
        ("            var item = await repository.GetByIdAsync(id, ct)", "            var profileReads = " + PROBE + ".Start();\n            var item = await repository.GetByIdAsync(id, ct)", "reads start"),
        ("            var changes = await pendingChanges.GetChangesAsync(item.Id, ct);", "            var changes = await pendingChanges.GetChangesAsync(item.Id, ct);\n            " + PROBE + '.End("data-reads", profileReads);\n            var profileLayout = ' + PROBE + ".Start();", "reads end/layout start"),
        ("            console.Write(view);", "            console.Write(view);\n            " + PROBE + '.End("layout-and-native-serialization", profileLayout);', "layout end"),
        ("            await EnsureCaptureAsync();\n\n            async Task EnsureCaptureAsync()", "            var profileFinal = " + PROBE + ".Start();\n            await EnsureCaptureAsync();\n            " + PROBE + '.End("final-admission", profileFinal);\n\n            async Task EnsureCaptureAsync()', "final check"),
        ('            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")\n            {', '            var profileOutput = ' + PROBE + '.Start();\n            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")\n            {', "output start"),
        ("            exit = 0;", "            " + PROBE + '.End("envelope-output", profileOutput);\n            exit = 0;', "output end"),
    ]
    for old, new, label in probes:
        text = replace_once(text, old, new, label)
    dimensions = '''            Twig.Infrastructure.Diagnostics.ProfileProbe.Dimension("title-chars", item.Title.Length);
            Twig.Infrastructure.Diagnostics.ProfileProbe.Dimension("field-count", item.Fields.Count);
            long htmlChars = item.Fields.TryGetValue("System.Description", out var htmlDescription) ? htmlDescription?.Length ?? 0 : 0;
            foreach (var definition in definitions)
                if (definition.DataType.Equals("html", StringComparison.OrdinalIgnoreCase)
                    && !definition.ReferenceName.Equals("System.Description", StringComparison.OrdinalIgnoreCase)
                    && item.Fields.TryGetValue(definition.ReferenceName, out var htmlValue)) htmlChars += htmlValue?.Length ?? 0;
            Twig.Infrastructure.Diagnostics.ProfileProbe.Dimension("native-html-chars", htmlChars);
'''
    # Dimensions outside timed layout/reads; no field content leaves the numeric log.
    text = replace_once(text, "            // All yielding reads and rendering finish", dimensions + "            // All yielding reads and rendering finish", "dimension observation")
    path.write_text(text, encoding="utf-8", newline="\n")

    path = snapshot / "src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, "        using var operation = Auth.ConnectionOperationGate.Acquire(Path.GetDirectoryName(paths.TwigDir)!);", "        var profileOpen = " + PROBE + ".Start();\n        var profileInstance = " + PROBE + ".NewInstance();\n        using var operation = Auth.ConnectionOperationGate.Acquire(Path.GetDirectoryName(paths.TwigDir)!);", "store open entry")
    old = "        return new SqliteCacheStore(new SqliteConnectionStringBuilder { DataSource = paths.DbPath, Pooling = false }.ToString(), admission, false);"
    new = "        var store = new SqliteCacheStore(new SqliteConnectionStringBuilder { DataSource = paths.DbPath, Pooling = false }.ToString(), admission, false);\n        " + PROBE + '.End("store-open-total-nested", profileOpen, profileInstance);\n        return store;'
    text = replace_once(text, old, new, "store open exit")
    text = replace_once(text, "        _admission = admission;", "        var profileInitialize = " + PROBE + ".Start();\n        _admission = admission;", "store constructor entry")
    text = replace_once(text, '                    throw new InvalidOperationException("mirror-generation-mismatch: native generation does not own the mirror. Resume its original migration before reading data.");\n            }', '                    throw new InvalidOperationException("mirror-generation-mismatch: native generation does not own the mirror. Resume its original migration before reading data.");\n            }\n            ' + PROBE + '.End("store-initialize-subset", profileInitialize);', "store constructor exit")
    path.write_text(text, encoding="utf-8", newline="\n")
    (snapshot / "src/Twig.Infrastructure/ProfileProbe1142.cs").write_text((ROOT / "ProfileProbe.cs").read_text(encoding="utf-8"), encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gate", required=True, type=Path)
    args = parser.parse_args()
    gate = json.loads(args.gate.read_text(encoding="utf-8"))
    states = {row["id"]: row["requestedFields"]["System.State"]["value"] for row in gate.get("readbacks", [])}
    if gate.get("status") != "START_GATE_OPEN" or states.get(1142) != "Doing" or states.get(1143) != "Doing":
        raise RuntimeError("Accepted native start gate with both Doing readbacks is required")
    destination = ROOT / "generated/source"
    if destination.exists():
        raise RuntimeError("Snapshot already exists; preserve it or use a fresh owned namespace instead of overwriting")
    archive = subprocess.run(["git", "archive", "--format=tar", COMMIT, *INPUTS], cwd=REPO, capture_output=True, check=True, timeout=60).stdout
    destination.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        source.extractall(destination, filter="data")
    hashes = {p.relative_to(destination).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in destination.rglob("*") if p.is_file()}
    instrument(destination)
    changed = [p.relative_to(destination).as_posix() for p in destination.rglob("*") if p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest() != hashes.get(p.relative_to(destination).as_posix())]
    manifest = {"sourceCommit": COMMIT, "gate": str(args.gate.resolve()), "inputs": INPUTS, "changedSnapshotFiles": sorted(changed), "originalSourceHashes": hashes,
                "startupExclusions": ["SelfUpdater.CleanupOldBinary", "CompanionStartup.RunFirstRunCheck"], "productionWrites": False}
    (ROOT / "generated/snapshot-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {destination}; changed only {len(changed)} snapshot files. No build or native execution performed.")


if __name__ == "__main__":
    main()
