using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Browser;

// Opt-in scratch fixture export for out-of-process consumers (the extension's execFile integration test).
// Inert unless TWIG_EXPORT_NATIVE_FIXTURE names an empty or absent directory; never touches a real workspace or the network.
//   TWIG_EXPORT_NATIVE_FIXTURE  target root (receives home/, checkout/, manifest.json)
//   TWIG_NATIVE_EXE_DIR         directory containing the built twig executable
//   TWIG_NATIVE_SHA             source commit the binary was built from (recorded verbatim; not verified against the binary)
// Run (bounded, no twig process may be running): dotnet test tests/Twig.Cli.Tests --no-build --filter "FullyQualifiedName~NativeFixtureExportTests"
// Consumers must run twig with cwd=workspacePath, env.TWIG_USER_HOME, and a PATH that still contains git:
// checkout identity is discovered by shelling to `git`, so a PATH of only the exe directory is refused as migration-fingerprint-mismatch.
[Trait("Category", "HostInventory")]
public sealed class NativeFixtureExportTests
{
    internal const string RootVariable = "TWIG_EXPORT_NATIVE_FIXTURE";
    internal const int MemberId = 42;
    internal const int NonMemberId = 43;
    internal const string BenchName = "Alpha";

    [HostMigrationFact]
    public async Task ExportsASealedScratchFixtureWhenRequested()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root)) return;
        var exeDir = Required("TWIG_NATIVE_EXE_DIR");
        var sha = Required("TWIG_NATIVE_SHA");
        var exeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
        var exe = Path.Combine(exeDir, exeName);
        File.Exists(exe).ShouldBeTrue($"{exeName} not found in {exeDir}");
        root = Path.GetFullPath(root);
        (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()).ShouldBeFalse("export root must be empty");

        string benchId, currentId;
        await using (var fixture = new NativeBrowserFixture(root))
        {
            await fixture.InitializeAsync();
            var runtime = await fixture.CreateRuntimeAsync();
            var repository = runtime.GetRequiredService<IWorkItemRepository>();
            await repository.SaveAsync(NativeBrowserFixture.Item(MemberId, "Member"));
            await repository.SaveAsync(NativeBrowserFixture.Item(NonMemberId, "Outsider"));
            var benches = runtime.GetRequiredService<IBenchRepository>();
            var named = (await benches.CreateAsync(BenchName))!;
            await benches.AddSelectorAsync(named.Id, BenchSelector.ForItem(MemberId));
            var other = (await benches.CreateAsync("Other"))!;
            await benches.SetCurrentAsync(other.Id);
            benchId = named.Id.ToString();
            currentId = (await benches.GetCurrentAsync())!.Id.ToString();
            currentId.ShouldNotBe(benchId);
            var manifestJson = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(manifestJson, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("nativeSha", sha);
                w.WriteStartObject("binary");
                w.WriteString("file", exe);
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant());
                w.WriteEndObject();
                w.WriteString("workspacePath", fixture.Paths.StartDir);
                w.WriteString("home", fixture.Home);
                w.WriteBoolean("gitRequiredOnPath", true);
                w.WriteString("bench", BenchName);
                w.WriteString("benchId", benchId);
                w.WriteString("currentBenchIdAtExport", currentId);
                w.WriteNumber("memberId", MemberId);
                w.WriteNumber("nonMemberId", NonMemberId);
                w.WriteStartArray("currentBenchProbeArgs");
                foreach (var arg in new[] { "bench", "list", "-o", "json" }) w.WriteStringValue(arg);
                w.WriteEndArray();
                w.WriteStartObject("env");
                w.WriteString("TWIG_USER_HOME", fixture.Home);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "manifest.json"), manifestJson.ToArray());
        }
        SqliteConnection.ClearAllPools();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{name} is required with {RootVariable}.");
}
