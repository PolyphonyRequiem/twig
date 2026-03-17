using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for LegacyDbMigrator: migrates flat .twig/twig.db → nested .twig/{org}/{project}/twig.db (ITEM-139).
/// </summary>
public class LegacyDbMigratorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;

    public LegacyDbMigratorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-migration-test-{Guid.NewGuid():N}");
        _twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(_twigDir);
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
    public void MigrateIfNeeded_MovesLegacyDb_ToContextPath()
    {
        // Arrange: create a legacy flat DB
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var expectedContextPath = TwigPaths.GetContextDbPath(_twigDir, "myorg", "myproj");

        // Act
        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        // Assert
        File.Exists(legacyDbPath).ShouldBeFalse("Legacy DB should be moved, not copied");
        File.Exists(expectedContextPath).ShouldBeTrue("DB should exist at context path");
    }

    [Fact]
    public void MigrateIfNeeded_MovesWalAndShmFiles()
    {
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);
        File.WriteAllText(legacyDbPath + "-wal", "wal-data");
        File.WriteAllText(legacyDbPath + "-shm", "shm-data");

        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var expectedContextPath = TwigPaths.GetContextDbPath(_twigDir, "myorg", "myproj");

        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        File.Exists(legacyDbPath + "-wal").ShouldBeFalse();
        File.Exists(legacyDbPath + "-shm").ShouldBeFalse();
        File.Exists(expectedContextPath + "-wal").ShouldBeTrue();
        File.Exists(expectedContextPath + "-shm").ShouldBeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothing_WhenLegacyDbMissing()
    {
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };

        // Act — should not throw
        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        var contextPath = TwigPaths.GetContextDbPath(_twigDir, "myorg", "myproj");
        File.Exists(contextPath).ShouldBeFalse("No DB should be created");
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothing_WhenContextDbAlreadyExists()
    {
        // Arrange: both legacy and context DBs exist
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var contextPath = TwigPaths.GetContextDbPath(_twigDir, "myorg", "myproj");
        Directory.CreateDirectory(Path.GetDirectoryName(contextPath)!);
        CreateDummyDb(contextPath);

        // Act
        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        // Assert: legacy DB should still exist (not moved because context already has one)
        File.Exists(legacyDbPath).ShouldBeTrue("Legacy DB should NOT be moved when context DB exists");
        File.Exists(contextPath).ShouldBeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothing_WhenOrganizationIsEmpty()
    {
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "", Project = "myproj" };

        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        File.Exists(legacyDbPath).ShouldBeTrue("Legacy DB should remain when org is empty");
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothing_WhenProjectIsEmpty()
    {
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "myorg", Project = "" };

        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        File.Exists(legacyDbPath).ShouldBeTrue("Legacy DB should remain when project is empty");
    }

    [Fact]
    public void MigrateIfNeeded_CreatesNestedDirectories()
    {
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "deeporg", Project = "deepproj" };
        var contextDir = Path.GetDirectoryName(TwigPaths.GetContextDbPath(_twigDir, "deeporg", "deepproj"))!;

        Directory.Exists(contextDir).ShouldBeFalse("Context dir should not exist before migration");

        LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

        Directory.Exists(contextDir).ShouldBeTrue("Migration should create the nested directories");
    }

    [Fact]
    public void MigrateIfNeeded_DoesNotCrash_WhenMigrationFails()
    {
        // Arrange: create a legacy DB
        var legacyDbPath = Path.Combine(_twigDir, "twig.db");
        CreateDummyDb(legacyDbPath);

        var config = new TwigConfiguration { Organization = "failorg", Project = "failproj" };

        // Create a *file* at the org directory path so Directory.CreateDirectory fails
        var orgDirPath = Path.Combine(_twigDir, "failorg");
        File.WriteAllText(orgDirPath, "blocker");

        // Capture stderr
        var originalStderr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            // Act — should NOT throw
            LegacyDbMigrator.MigrateIfNeeded(_twigDir, config);

            // Assert: legacy DB should still exist (move failed)
            File.Exists(legacyDbPath).ShouldBeTrue("Legacy DB should remain when migration fails");

            // Assert: warning was written to stderr
            var stderrOutput = sw.ToString();
            stderrOutput.ShouldContain("warning: legacy DB migration failed:");
            stderrOutput.ShouldContain("twig init --force");
        }
        finally
        {
            Console.SetError(originalStderr);
        }
    }

    private static void CreateDummyDb(string path)
    {
        // Create a minimal SQLite DB file
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65 }); // "SQLite" header fragment
    }
}
