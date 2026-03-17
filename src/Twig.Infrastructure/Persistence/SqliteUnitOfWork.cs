using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IUnitOfWork"/>.
/// Wraps <see cref="SqliteTransaction"/> in an <see cref="ITransaction"/> token.
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteCacheStore _store;

    public SqliteUnitOfWork(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<ITransaction> BeginAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        var sqlTx = conn.BeginTransaction();
        return Task.FromResult<ITransaction>(new SqliteTransactionWrapper(sqlTx));
    }

    public Task CommitAsync(ITransaction tx, CancellationToken ct = default)
    {
        var wrapper = tx as SqliteTransactionWrapper
            ?? throw new InvalidOperationException("Expected SqliteTransactionWrapper.");
        wrapper.Transaction.Commit();
        return Task.CompletedTask;
    }

    public Task RollbackAsync(ITransaction tx, CancellationToken ct = default)
    {
        var wrapper = tx as SqliteTransactionWrapper
            ?? throw new InvalidOperationException("Expected SqliteTransactionWrapper.");
        wrapper.Transaction.Rollback();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a <see cref="SqliteTransaction"/> as an <see cref="ITransaction"/> token.
    /// </summary>
    internal sealed class SqliteTransactionWrapper : ITransaction
    {
        public SqliteTransaction Transaction { get; }

        public SqliteTransactionWrapper(SqliteTransaction transaction)
        {
            Transaction = transaction;
        }

        public ValueTask DisposeAsync()
        {
            Transaction.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
