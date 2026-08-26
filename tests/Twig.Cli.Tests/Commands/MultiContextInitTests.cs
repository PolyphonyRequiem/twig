using Shouldly;
using NSubstitute;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#736 T1 §4.2.4 contract: exactly one SQLite file per worktree at
/// <c>.twig/cache/twig.db</c>, regardless of the currently-bound
/// org/project. Switching org or project inside a worktree is a
/// destructive reinitialization — twig does NOT silently re-interpret an
/// existing cache under a new binding, does NOT keep parallel per-context
/// caches, and does NOT migrate pre-T1 nested (<c>.twig/{org}/{project}/twig.db</c>)
/// or flat (<c>.twig/twig.db</c>) legacy databases. Any such residue is
/// left untouched; a fresh cache is written at the canonical path and the
/// operator is expected to switch worktrees or explicitly reinit.
/// </summary>
[Collection("NonParallel")]
public class MultiContextInitTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;
    private readonly IIterationService _iterationService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public MultiContextInitTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-multicontext-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _twigDir = Path.Combine(_testDir, ".twig");

        _iterationService = Substitute.For<IIterationService>();
        _iterationService.DetectTemplateNameAsync(Arg.Any<CancellationToken>())
            .Returns("Agile");
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>
            {
                new("Bug", "CC293D", "icon_insect"),
                new("Task", "F2CB1D", "icon_clipboard"),
            });

        InitCommandTestFixture.InitTempWorktree(_testDir);
        (_systemRegistry, _profileRegistry) = InitCommandTestFixture.CreateSeams(_testDir);

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private readonly Twig.Domain.Interfaces.ISystemWorktreeRegistry _systemRegistry;
    private readonly Twig.Domain.Services.Attachment.IProfileRegistrySource _profileRegistry;

    private InitCommand CreateInitCommand(
        IIterationService iterationService,
        TwigPaths paths,
        OutputFormatterFactory formatterFactory,
        HintEngine hintEngine,
        IGlobalProfileStore? globalProfileStore = null,
        IConsoleInput? consoleInput = null,
        ITelemetryClient? telemetryClient = null)
        => InitCommandTestFixture.CreateInitCommand(
            _systemRegistry, _profileRegistry,
            iterationService, paths, formatterFactory, hintEngine,
            globalProfileStore, consoleInput, telemetryClient);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private TwigPaths PathsForTest() =>
        TwigPaths.ForContext(_twigDir, org: "unused", project: "unused", startDir: _testDir);

    private static string CanonicalDbPath(string twigDir) =>
        Path.Combine(twigDir, "cache", "twig.db");

    // ── one canonical cache path ────────────────────────────────────

    [Fact]
    public async Task Init_WritesTheSingleCanonicalCacheDb()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            // The one path §4.2.4 pins — regardless of the org/project passed to init.
            var canonical = CanonicalDbPath(_twigDir);
            File.Exists(canonical).ShouldBeTrue("Cache DB must live at .twig/cache/twig.db");
            paths.DbPath.ShouldBe(canonical);

            // No nested org/project scoping directory is created.
            Directory.Exists(Path.Combine(_twigDir, "OrgA")).ShouldBeFalse(
                "T1 clean cutover forbids the pre-T1 nested layout.");
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    [Fact]
    public async Task Init_DifferentOrgProject_ResolvesToTheSameCacheDbPath()
    {
        // The path is opaque to org/project. Different bindings, same file.
        var canonical = CanonicalDbPath(_twigDir);
        TwigPaths.GetContextDbPath(_twigDir, "OrgA", "ProjectA").ShouldBe(canonical);
        TwigPaths.GetContextDbPath(_twigDir, "OrgB", "ProjectB").ShouldBe(canonical);

        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths1 = PathsForTest();
            var cmd1 = CreateInitCommand(_iterationService, paths1, _formatterFactory, _hintEngine);
            (await cmd1.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            paths1.DbPath.ShouldBe(canonical);

            var paths2 = PathsForTest();
            paths2.DbPath.ShouldBe(canonical);
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    // ── switching bindings requires explicit reinit ─────────────────

    [Fact]
    public async Task Reinit_WithoutForce_IsRefused_WhenCacheAlreadyExists()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            // Switching to a new binding cannot silently reuse the existing cache —
            // the second init MUST refuse without --force, so the operator is
            // forced to reinitialize (or move to a fresh worktree) explicitly.
            var second = CreateInitCommand(_iterationService, PathsForTest(), _formatterFactory, _hintEngine);
            (await second.ExecuteAsync("OrgB", "ProjectB")).ShouldBe(1);

            // The cache file is still there — unchanged.
            File.Exists(CanonicalDbPath(_twigDir)).ShouldBeTrue();
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    [Fact]
    public async Task Reinit_WithForce_DiscardsCache_ForCleanRebind()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            // Write a distinctive marker into the OrgA cache.
            var canonical = CanonicalDbPath(_twigDir);
            using (var store = new SqliteCacheStore($"Data Source={canonical}"))
            {
                var conn = store.GetConnection();
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO context (key, value) VALUES ('org_a_marker', 'org_a_value');";
                insert.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Force reinit under new binding — same canonical path, but the
            // old data MUST NOT resurface under the new binding.
            var second = CreateInitCommand(_iterationService, PathsForTest(), _formatterFactory, _hintEngine);
            (await second.ExecuteAsync("OrgB", "ProjectB", force: true)).ShouldBe(0);

            File.Exists(canonical).ShouldBeTrue();
            using (var store = new SqliteCacheStore($"Data Source={canonical}"))
            {
                var conn = store.GetConnection();
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT value FROM context WHERE key = 'org_a_marker';";
                probe.ExecuteScalar().ShouldBeNull(
                    "--force must discard the previous cache — no OrgA data may resurface under OrgB.");
            }
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    // ── fail-closed against pre-T1 legacy layouts ───────────────────

    [Fact]
    public async Task Init_LeavesPreT1FlatLegacyDbUntouched_AndWritesFreshCache()
    {
        // AB#736 §9 explicitly forbids in-band migration. A stray legacy
        // .twig/twig.db from a pre-T1 install must NOT be silently moved,
        // read, or reinterpreted; the new run writes .twig/cache/twig.db
        // fresh and the legacy file remains exactly where it was.
        Directory.CreateDirectory(_twigDir);
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        File.WriteAllText(legacyDbPath, "legacy-residue-not-a-real-db");
        var legacyBytes = File.ReadAllBytes(legacyDbPath);

        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            File.Exists(CanonicalDbPath(_twigDir)).ShouldBeTrue("Fresh cache DB must exist.");
            File.Exists(legacyDbPath).ShouldBeTrue(
                "Legacy .twig/twig.db must remain — the T1 clean cutover forbids in-band migration.");
            File.ReadAllBytes(legacyDbPath).ShouldBe(legacyBytes,
                "Legacy DB contents must be untouched.");
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    [Fact]
    public async Task Init_LeavesPreT1NestedLegacyLayoutUntouched()
    {
        // Pre-T1 residue: .twig/{org}/{project}/twig.db written by an
        // earlier binary. It must NOT be recognized as the cache, and the
        // fresh init MUST NOT delete or rewrite it.
        var nestedDir = Path.Combine(_twigDir, "OrgA", "ProjectA");
        Directory.CreateDirectory(nestedDir);
        var nestedDbPath = Path.Combine(nestedDir, "twig.db");
        File.WriteAllText(nestedDbPath, "nested-legacy-residue-not-a-real-db");
        var nestedBytes = File.ReadAllBytes(nestedDbPath);

        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("OrgA", "ProjectA")).ShouldBe(0);

            paths.DbPath.ShouldBe(CanonicalDbPath(_twigDir),
                "TwigPaths.DbPath is opaque to org/project — the nested layout is gone.");
            File.Exists(CanonicalDbPath(_twigDir)).ShouldBeTrue();
            File.Exists(nestedDbPath).ShouldBeTrue(
                "Nested legacy DB must remain untouched — no in-band migration.");
            File.ReadAllBytes(nestedDbPath).ShouldBe(nestedBytes);
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }

    // ── config persistence still works ──────────────────────────────

    [Fact]
    public async Task Init_UpdatesConfigWithOrgAndProject()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = PathsForTest();
            var cmd = CreateInitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            (await cmd.ExecuteAsync("myorg", "myproj")).ShouldBe(0);

            var config = await TwigConfiguration.LoadSplitAsync(paths);
            config.Organization.ShouldBe("myorg");
            config.Project.ShouldBe("myproj");
        }
        finally { Directory.SetCurrentDirectory(originalCwd); }
    }
}
