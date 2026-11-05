#!/usr/bin/env python3
"""Verify compiled schema-v10 fresh opens refuse v11; no provider transport."""
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
if (create) {
    if (!success) throw new Exception("Current registry initialization failed.");
    if (args[0] == "cached") {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + args[2] + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE layout_meta SET version=11 WHERE id=1;";
        command.ExecuteNonQuery();
        var read = (Task)method.Invoke(registry, input)!;
        await read;
        var cached = read.GetType().GetProperty("Result")!.GetValue(read)!;
        if (!(bool)cached.GetType().GetProperty("IsSuccess")!.GetValue(cached)!)
            throw new Exception("Cached v10 compatibility limit was not reproduced.");
        Console.WriteLine("COMPILED V10 CACHED-OPEN: still accepts existing connection; quiescence is required.");
    }
} else {
    if (success) throw new Exception("Compiled v10 reader accepted schema11.");
    var error = result.GetType().GetProperty("Error")!.GetValue(result)!;
    var message = (string)error.GetType().GetProperty("Message")!.GetValue(error)!;
    if (!message.Contains("system-store-schema-mismatch")) throw new Exception(message);
    Console.WriteLine("COMPILED V10 FRESH-OPEN: schema11 refused; no provider HTTP.");
}
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--legacy-assembly", required=True)
    parser.add_argument("--current-assembly", required=True)
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
                                      str(Path(assembly).resolve()), database], check=True)
        subprocess.run(bounded + ["--timeout", "60", "--", *probe, "cached",
                                  str(Path(args.legacy_assembly).resolve()), str(root / "cached-system.db")], check=True)
    print("REGISTRY-COMPAT-VERDICT: PASSED; cached v10 processes still require quiescence.", flush=True)


if __name__ == "__main__":
    main()
