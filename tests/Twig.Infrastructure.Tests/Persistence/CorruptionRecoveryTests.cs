using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for corruption recovery (FM-008): garbage DB file detection,
/// SqliteCacheStore wraps open errors.
/// </summary>
public class CorruptionRecoveryTests
{
    [Fact]
    public void SqliteCacheStore_CorruptDatabase_ThrowsDescriptiveMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "twig.db");

        try
        {
            // Write garbage data to simulate corruption
            File.WriteAllBytes(dbPath, [0xFF, 0xFE, 0xDD, 0xCC, 0xBB, 0xAA, 0x00, 0x01]);

            // SqliteCacheStore wraps corruption in InvalidOperationException to preserve inner exception (I-003)
            var ex = Should.Throw<InvalidOperationException>(() =>
            {
                using var store = new SqliteCacheStore($"Data Source={dbPath}");
            });

            ex.Message.ShouldContain("corrupt");
            // I-003: Verify the original exception chain is preserved
            ex.InnerException.ShouldNotBeNull("Original SqliteException should be preserved as InnerException");
            ex.InnerException.ShouldBeOfType<SqliteException>();
        }
        finally
        {
            // Clear SQLite connection pool to release file handles
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void SqliteCacheStore_ValidDatabase_OpensSuccessfully()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        store.ShouldNotBeNull();
        store.GetConnection().State.ShouldBe(System.Data.ConnectionState.Open);
    }
}
