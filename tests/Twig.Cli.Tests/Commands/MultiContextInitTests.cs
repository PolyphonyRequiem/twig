using NSubstitute;
using Shouldly;
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
/// Integration tests for multi-context database isolation (ITEM-140).
/// Verifies that initializing with different org/project combos preserves each context's DB.
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

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

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

    [Fact]
    public async Task Init_OrgA_ThenOrgB_PreservesOrgADatabase()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            // Init with Org A
            var pathsA = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmdA = new InitCommand(_iterationService, pathsA, _formatterFactory, _hintEngine);
            var resultA = await cmdA.ExecuteAsync("OrgA", "ProjectA");
            resultA.ShouldBe(0);

            // Verify Org A's DB exists at nested path
            var orgADbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgA", "ProjectA");
            File.Exists(orgADbPath).ShouldBeTrue("Org A DB should exist");

            // Insert test data into Org A's DB
            using (var storeA = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = storeA.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO context (key, value) VALUES ('test_marker', 'org_a_data');";
                cmd.ExecuteNonQuery();
            }

            // Init with Org B (force since .twig/ already exists)
            var pathsB = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmdB = new InitCommand(_iterationService, pathsB, _formatterFactory, _hintEngine);
            var resultB = await cmdB.ExecuteAsync("OrgB", "ProjectB", force: true);
            resultB.ShouldBe(0);

            // Verify Org B's DB exists
            var orgBDbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgB", "ProjectB");
            File.Exists(orgBDbPath).ShouldBeTrue("Org B DB should exist");

            // Verify Org A's DB is STILL intact
            File.Exists(orgADbPath).ShouldBeTrue("Org A DB should still exist after Org B init");

            // Verify Org A's data is still there
            using (var storeA2 = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = storeA2.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM context WHERE key = 'test_marker';";
                var value = cmd.ExecuteScalar() as string;
                value.ShouldBe("org_a_data", "Org A's data should be preserved");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_SameOrg_DifferentProjects_CreatesSeparateDBs()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            // Init with Org/ProjectA
            var paths1 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd1 = new InitCommand(_iterationService, paths1, _formatterFactory, _hintEngine);
            var result1 = await cmd1.ExecuteAsync("contoso", "MyProject");
            result1.ShouldBe(0);

            // Init with same Org/different Project
            var paths2 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd2 = new InitCommand(_iterationService, paths2, _formatterFactory, _hintEngine);
            var result2 = await cmd2.ExecuteAsync("contoso", "BackendService", force: true);
            result2.ShouldBe(0);

            // Both DBs should exist in separate directories
            var osDbPath = TwigPaths.GetContextDbPath(_twigDir, "contoso", "MyProject");
            var cvDbPath = TwigPaths.GetContextDbPath(_twigDir, "contoso", "BackendService");

            File.Exists(osDbPath).ShouldBeTrue("MyProject DB should exist");
            File.Exists(cvDbPath).ShouldBeTrue("BackendService DB should exist");

            // Directories should be under the same org folder
            Path.GetDirectoryName(Path.GetDirectoryName(osDbPath)).ShouldBe(
                Path.GetDirectoryName(Path.GetDirectoryName(cvDbPath)),
                "Both projects should be under same org directory");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Force_DeletesOnlyCurrentContextDb()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            // Init with Org A
            var pathsA = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmdA = new InitCommand(_iterationService, pathsA, _formatterFactory, _hintEngine);
            await cmdA.ExecuteAsync("OrgA", "ProjectA");

            // Insert data into Org A
            var orgADbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgA", "ProjectA");
            using (var store = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = store.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO context (key, value) VALUES ('marker', 'preserved');";
                cmd.ExecuteNonQuery();
            }

            // Init Org B
            var pathsB = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmdB = new InitCommand(_iterationService, pathsB, _formatterFactory, _hintEngine);
            await cmdB.ExecuteAsync("OrgB", "ProjectB", force: true);

            // Force re-init Org B — should only delete Org B's DB
            var pathsB2 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmdB2 = new InitCommand(_iterationService, pathsB2, _formatterFactory, _hintEngine);
            await cmdB2.ExecuteAsync("OrgB", "ProjectB", force: true);

            // Org A's data should be intact
            File.Exists(orgADbPath).ShouldBeTrue("Org A DB file should still exist");
            using (var store = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = store.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM context WHERE key = 'marker';";
                var value = cmd.ExecuteScalar() as string;
                value.ShouldBe("preserved", "Org A data should survive Org B's --force reinit");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Force_ReInitOrgA_DoesNotDeleteOrgBDatabase()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            // Init Org A, insert data
            var paths1 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd1 = new InitCommand(_iterationService, paths1, _formatterFactory, _hintEngine);
            await cmd1.ExecuteAsync("OrgA", "ProjA");

            var orgADbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgA", "ProjA");
            using (var store = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = store.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO context (key, value) VALUES ('active_id', '42');";
                cmd.ExecuteNonQuery();
            }

            // Switch to Org B
            var paths2 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd2 = new InitCommand(_iterationService, paths2, _formatterFactory, _hintEngine);
            await cmd2.ExecuteAsync("OrgB", "ProjB", force: true);

            // Force re-init Org A — this will delete Org A's DB, but Org B's must survive
            var paths3 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd3 = new InitCommand(_iterationService, paths3, _formatterFactory, _hintEngine);
            await cmd3.ExecuteAsync("OrgA", "ProjA", force: true);

            // Org B's DB should be untouched
            var orgBDbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgB", "ProjB");
            File.Exists(orgBDbPath).ShouldBeTrue("Org B DB should still exist after force re-init of Org A");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task SwitchBackToOrgA_WithoutForce_PreservesOrgAData()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            // Init Org A, insert data
            var paths1 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd1 = new InitCommand(_iterationService, paths1, _formatterFactory, _hintEngine);
            await cmd1.ExecuteAsync("OrgA", "ProjA");

            var orgADbPath = TwigPaths.GetContextDbPath(_twigDir, "OrgA", "ProjA");
            using (var store = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = store.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO context (key, value) VALUES ('active_id', '42');";
                cmd.ExecuteNonQuery();
            }

            // Switch to Org B (force needed because .twig/ exists)
            var paths2 = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd2 = new InitCommand(_iterationService, paths2, _formatterFactory, _hintEngine);
            await cmd2.ExecuteAsync("OrgB", "ProjB", force: true);

            // Read Org A's DB directly (no re-init) — data should be intact
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Exists(orgADbPath).ShouldBeTrue("Org A DB should still exist");
            using (var storeA = new SqliteCacheStore($"Data Source={orgADbPath}"))
            {
                var conn = storeA.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM context WHERE key = 'active_id';";
                var value = cmd.ExecuteScalar() as string;
                value.ShouldBe("42", "Org A's data should be intact after switching to Org B");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_CreatesNestedDirectoryStructure()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var paths = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd = new InitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            await cmd.ExecuteAsync("dangreen-msft", "Twig");

            // Verify nested directory was created
            var contextDir = Path.Combine(_twigDir, "dangreen-msft", "Twig");
            Directory.Exists(contextDir).ShouldBeTrue();
            File.Exists(Path.Combine(contextDir, "twig.db")).ShouldBeTrue();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_UpdatesConfigWithOrgAndProject()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        try
        {
            var configPath = Path.Combine(_twigDir, "config");
            var paths = new TwigPaths(_twigDir, configPath, Path.Combine(_twigDir, "twig.db"), startDir: _testDir);
            var cmd = new InitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            await cmd.ExecuteAsync("myorg", "myproj");

            var config = await TwigConfiguration.LoadAsync(configPath);
            config.Organization.ShouldBe("myorg");
            config.Project.ShouldBe("myproj");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
