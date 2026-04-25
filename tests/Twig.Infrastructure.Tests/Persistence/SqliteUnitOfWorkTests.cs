using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqliteUnitOfWork: commit persists, rollback discards.
/// </summary>
public class SqliteUnitOfWorkTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteUnitOfWork _unitOfWork;

    public SqliteUnitOfWorkTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _unitOfWork = new SqliteUnitOfWork(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task BeginAndCommit_PersistsData()
    {
        var tx = await _unitOfWork.BeginAsync();

        // Insert data using the underlying transaction
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = ((SqliteUnitOfWork.SqliteTransactionWrapper)tx).Transaction;
        cmd.CommandText = "INSERT INTO context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", "test_key");
        cmd.Parameters.AddWithValue("@value", "test_value");
        cmd.ExecuteNonQuery();

        await _unitOfWork.CommitAsync(tx);
        await tx.DisposeAsync();

        // Verify data persisted
        using var verifyCmd = conn.CreateCommand();
        verifyCmd.CommandText = "SELECT value FROM context WHERE key = @key;";
        verifyCmd.Parameters.AddWithValue("@key", "test_key");
        var result = verifyCmd.ExecuteScalar() as string;
        result.ShouldBe("test_value");
    }

    [Fact]
    public async Task BeginAndRollback_DiscardsData()
    {
        var tx = await _unitOfWork.BeginAsync();

        // Insert data using the underlying transaction
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = ((SqliteUnitOfWork.SqliteTransactionWrapper)tx).Transaction;
        cmd.CommandText = "INSERT INTO context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", "rollback_key");
        cmd.Parameters.AddWithValue("@value", "rollback_value");
        cmd.ExecuteNonQuery();

        await _unitOfWork.RollbackAsync(tx);
        await tx.DisposeAsync();

        // Verify data was NOT persisted
        using var verifyCmd = conn.CreateCommand();
        verifyCmd.CommandText = "SELECT value FROM context WHERE key = @key;";
        verifyCmd.Parameters.AddWithValue("@key", "rollback_key");
        var result = verifyCmd.ExecuteScalar();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task CommitAsync_Throws_WhenWrongTransactionType()
    {
        var fakeTx = new FakeTransaction();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _unitOfWork.CommitAsync(fakeTx));
    }

    [Fact]
    public async Task RollbackAsync_Throws_WhenWrongTransactionType()
    {
        var fakeTx = new FakeTransaction();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _unitOfWork.RollbackAsync(fakeTx));
    }

    private sealed class FakeTransaction : Twig.Domain.Interfaces.ITransaction
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
