using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for Busy Timeout via PRAGMA busy_timeout in SqliteCacheStore (ITEM-139).
/// Verifies that the PRAGMA is applied when the store opens a connection.
/// </summary>
public class BusyTimeoutTests : IDisposable
{
    private readonly string _testDir;

    public BusyTimeoutTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-busy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
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
    public void SqliteCacheStore_SetsBusyTimeout_Via_Pragma()
    {
        var dbPath = Path.Combine(_testDir, "busy.db");
        var connStr = $"Data Source={dbPath}";

        using var store = new SqliteCacheStore(connStr);
        var conn = store.GetConnection();

        // PRAGMA busy_timeout returns the timeout in milliseconds
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeout = Convert.ToInt32(cmd.ExecuteScalar());

        timeout.ShouldBe(5000, "SqliteCacheStore should set busy_timeout=5000 via PRAGMA");
    }

    [Fact]
    public void SqliteCacheStore_InMemory_SetsBusyTimeout()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeout = Convert.ToInt32(cmd.ExecuteScalar());

        timeout.ShouldBe(5000, "Even in-memory DBs should have busy_timeout=5000");
    }
}
