using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Regression cover for #272. A file-backed <see cref="SqliteCacheStore"/> derives its durable
/// store as a <c>pending.db</c> SIBLING of the mirror file (see
/// <see cref="SqliteCacheStore.DeriveDurableDataSource"/>). Two stores whose mirror files live in
/// the SAME directory therefore share one durable file — even though their mirror paths are
/// unique GUIDs.
/// </summary>
public class DurableStoreIsolationTests
{
    [Fact]
    public void DeriveDurableDataSource_TwoUniqueMirrorsInSameDirectory_CollideOnOnePendingFile()
    {
        // This is the whole defect, in one assertion. Unique mirror names are NOT sufficient
        // isolation, because the durable sibling is named by DIRECTORY, not by mirror file.
        var dir = Path.GetTempPath();
        var mirrorA = Path.Combine(dir, $"twig_test_{Guid.NewGuid():N}.db");
        var mirrorB = Path.Combine(dir, $"twig_test_{Guid.NewGuid():N}.db");

        mirrorA.ShouldNotBe(mirrorB);

        SqliteCacheStore.DeriveDurableDataSource(mirrorA)
            .ShouldBe(SqliteCacheStore.DeriveDurableDataSource(mirrorB));
    }

    [Fact]
    public void ConcurrentStoresInSeparateDirectories_DoNotShareDurableState()
    {
        // Behavioural regression cover for #272. Two stores created the way the repository
        // tests create them must NOT see each other's durable rows. Before the fix both
        // mirrors sat in the temp root, so both attached the same /tmp/pending.db and this
        // assertion fails; with per-test directories they are properly isolated.
        var dirA = Path.Combine(Path.GetTempPath(), $"twig_iso_a_{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"twig_iso_b_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        try
        {
            using var storeA = new SqliteCacheStore($"Data Source={Path.Combine(dirA, "twig.db")}");
            using var storeB = new SqliteCacheStore($"Data Source={Path.Combine(dirB, "twig.db")}");

            using (var write = storeA.GetConnection().CreateCommand())
            {
                write.CommandText =
                    $"CREATE TABLE {SqliteCacheStore.DurableSchema}.iso_probe(v TEXT);"
                    + $"INSERT INTO {SqliteCacheStore.DurableSchema}.iso_probe(v) VALUES('from-A');";
                write.ExecuteNonQuery();
            }

            // B must not see A's durable table at all.
            using var read = storeB.GetConnection().CreateCommand();
            read.CommandText =
                "SELECT count(*) FROM pending.sqlite_master WHERE type='table' AND name='iso_probe';";
            Convert.ToInt32(read.ExecuteScalar()).ShouldBe(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dirA, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(dirB, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DeriveDurableDataSource_MirrorsInDistinctDirectories_DoNotCollide()
    {
        // The fix shape: give each store its own directory and the durable siblings separate.
        var root = Path.GetTempPath();
        var mirrorA = Path.Combine(root, $"twig_a_{Guid.NewGuid():N}", "twig.db");
        var mirrorB = Path.Combine(root, $"twig_b_{Guid.NewGuid():N}", "twig.db");

        SqliteCacheStore.DeriveDurableDataSource(mirrorA)
            .ShouldNotBe(SqliteCacheStore.DeriveDurableDataSource(mirrorB));
    }

    [Fact]
    public void SharedDurableFile_SecondStoreRebuildingSchema_DisruptsFirstStoresDurableTables()
    {
        // The mechanism behind the intermittent ObjectDisposedException: two live stores hold
        // the SAME attached pending.db. Tearing one down (or rebuilding its schema) mutates a
        // file the other still has attached and is writing through.
        var dir = Path.Combine(Path.GetTempPath(), $"twig_share_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var pendingPath = Path.Combine(dir, "pending.db");

        try
        {
            using var storeA = new SqliteCacheStore($"Data Source={Path.Combine(dir, "a.db")}");
            using var storeB = new SqliteCacheStore($"Data Source={Path.Combine(dir, "b.db")}");

            // Both resolved the same durable file despite distinct mirrors.
            SqliteCacheStore.DeriveDurableDataSource(Path.Combine(dir, "a.db"))
                .ShouldBe(pendingPath);
            SqliteCacheStore.DeriveDurableDataSource(Path.Combine(dir, "b.db"))
                .ShouldBe(pendingPath);

            // Assert the sharing is real at the SQLite level, not just at the path level:
            // a row written through A's attached durable schema is visible through B's.
            using (var write = storeA.GetConnection().CreateCommand())
            {
                write.CommandText =
                    $"CREATE TABLE IF NOT EXISTS {SqliteCacheStore.DurableSchema}.share_probe(v TEXT);"
                    + $"INSERT INTO {SqliteCacheStore.DurableSchema}.share_probe(v) VALUES('from-A');";
                write.ExecuteNonQuery();
            }

            using var read = storeB.GetConnection().CreateCommand();
            read.CommandText = $"SELECT v FROM {SqliteCacheStore.DurableSchema}.share_probe;";
            read.ExecuteScalar().ShouldBe("from-A");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
