using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Browser;

// Opt-in scratch fixture export for out-of-process consumers (the extension's execFile integration test).
// Inert unless TWIG_EXPORT_NATIVE_FIXTURE names an empty or absent directory; never touches a real workspace or the network.
//   TWIG_EXPORT_NATIVE_FIXTURE  target root (receives home/, checkout/, manifest.json)
//   TWIG_NATIVE_EXE_DIR         directory containing the built twig executable
//   TWIG_NATIVE_SHA             source commit the binary was built from
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
        var exe = Directory.EnumerateFiles(exeDir, "twig*").FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals("twig", StringComparison.OrdinalIgnoreCase)
            && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).ShouldNotBeNull($"no twig executable in {exeDir}");
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
            var manifest = new Dictionary<string, object>
            {
                ["nativeSha"] = sha,
                ["binary"] = new Dictionary<string, string> { ["file"] = exe, ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant() },
                ["workspacePath"] = fixture.Paths.StartDir,
                ["home"] = fixture.Home,
                ["bench"] = BenchName,
                ["benchId"] = benchId,
                ["currentBenchIdAtExport"] = currentId,
                ["memberId"] = MemberId,
                ["nonMemberId"] = NonMemberId,
                ["currentBenchProbeArgs"] = new[] { "bench", "list", "-o", "json" },
                ["env"] = new Dictionary<string, string> { ["TWIG_USER_HOME"] = fixture.Home },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        SqliteConnection.ClearAllPools();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{name} is required with {RootVariable}.");
}
