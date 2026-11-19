#!/usr/bin/env python3
"""Verify unchanged legacy fresh opens refuse the current schema; no provider transport."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

PROBE = r'''
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
var assemblyPath = Path.GetFullPath(args[1]);
AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
    var path = Path.Combine(Path.GetDirectoryName(assemblyPath)!, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
var assembly = Assembly.LoadFrom(assemblyPath);
var type = assembly.GetType("Twig.Infrastructure.Persistence.SqliteSystemWorktreeRegistry", true)!;
using var registry = (IDisposable)Activator.CreateInstance(type, args[2], TimeProvider.System)!;
var create = args[0] is "create" or "cached";
var method = type.GetMethod(create ? "UpsertConnectionAsync" : "FindWorktreeAsync")!;
object[] input = create ? ["fixture", "org", "project", null!, CancellationToken.None]
    : ["fixture-root", CancellationToken.None];
var task = (Task)method.Invoke(registry, input)!;
await task;
var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
var success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
void CheckTarget() {
    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + args[2] + ";Pooling=False");
    db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT version FROM layout_meta WHERE id=1;";
    if (Convert.ToInt32(command.ExecuteScalar()) != int.Parse(args[3])) throw new Exception("Compatibility target version differs from the actual migrated database.");
}
if (create) {
    if (!success) throw new Exception("Current registry initialization failed.");
    if (args[0] != "cached") CheckTarget();
    if (args[0] == "cached") {
        Console.WriteLine("CACHED-READY");
        if (Console.ReadLine() != "continue") throw new Exception("Actual separate-process migration was not confirmed.");
        CheckTarget();
        var read = (Task)method.Invoke(registry, input)!;
        await read;
        var cached = read.GetType().GetProperty("Result")!.GetValue(read)!;
        if (!(bool)cached.GetType().GetProperty("IsSuccess")!.GetValue(cached)!)
            throw new Exception("Cached legacy compatibility limit was not reproduced.");
        Console.WriteLine("COMPILED LEGACY CACHED-OPEN: still accepts after actual migration; quiescence is required.");
    }
} else {
    CheckTarget();
    if (success) throw new Exception("Compiled legacy reader accepted current schema" + args[3] + ".");
    var error = result.GetType().GetProperty("Error")!.GetValue(result)!;
    var message = (string)error.GetType().GetProperty("Message")!.GetValue(error)!;
    if (!message.Contains("system-store-schema-mismatch")) throw new Exception(message);
    Console.WriteLine("COMPILED LEGACY FRESH-OPEN: schema" + args[3] + " refused; no provider HTTP.");
}
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--legacy-assembly", required=True)
    parser.add_argument("--current-assembly", required=True)
    parser.add_argument("--legacy-version", type=int, choices=(10, 11), default=10)
    parser.add_argument("--current-version", type=int, default=12)
    args = parser.parse_args()
    bounded = [sys.executable, str(Path(__file__).with_name("run-bounded.py"))]
    with tempfile.TemporaryDirectory(prefix="twig-registry-compat-") as directory:
        root = Path(directory)
        (root / "Probe.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<Nullable>enable</Nullable></PropertyGroup><ItemGroup>'
            '<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.6" />'
            '<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.14" />'
            '</ItemGroup></Project>', encoding="utf-8")
        (root / "Program.cs").write_text(PROBE, encoding="utf-8")
        subprocess.run(bounded + ["--timeout", "120", "--", "dotnet", "build",
                                  str(root / "Probe.csproj"), "-m:1", "--verbosity", "minimal"], check=True)
        probe = ["dotnet", str(root / "bin" / "Debug" / "net11.0" / "Probe.dll")]
        database = str(root / "system.db")
        for mode, assembly in (("create", args.current_assembly), ("reject", args.legacy_assembly)):
            subprocess.run(bounded + ["--timeout", "60", "--", *probe, mode,
                                      str(Path(assembly).resolve()), database, str(args.current_version)], check=True)
        cached_database = str(root / "cached-system.db")
        cached = subprocess.Popen(bounded + ["--timeout", "60", "--", *probe, "cached",
                                  str(Path(args.legacy_assembly).resolve()), cached_database, str(args.current_version)],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            line = cached.stdout.readline()
            if line.strip() != "CACHED-READY":
                raise RuntimeError("Legacy cached process did not initialize: " + line)
            subprocess.run(bounded + ["--timeout", "60", "--", *probe, "create",
                                      str(Path(args.current_assembly).resolve()), cached_database, str(args.current_version)], check=True)
            output, _ = cached.communicate("continue\n", timeout=60)
            print(output, end="", flush=True)
            if cached.returncode: raise RuntimeError("Cached-handle probe failed.")
        finally:
            if cached.poll() is None:
                if not cached.stdin.closed: cached.stdin.close()
                cached.wait(timeout=60)  # The existing bounded wrapper owns tree cleanup.
    print(f"REGISTRY-COMPAT-VERDICT: PASSED; v{args.legacy_version} fresh opens refuse {args.current_version}; cached processes require quiescence.", flush=True)


if __name__ == "__main__":
    main()
