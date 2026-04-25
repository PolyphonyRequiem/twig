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
        _store.ActiveTransaction = sqlTx;
        return Task.FromResult<ITransaction>(new SqliteTransactionWrapper(sqlTx, _store));
    }

    public Task CommitAsync(ITransaction tx, CancellationToken ct = default)
    {
        var wrapper = tx as SqliteTransactionWrapper
            ?? throw new InvalidOperationException("Expected SqliteTransactionWrapper.");
        wrapper.Transaction.Commit();
        _store.ActiveTransaction = null;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(ITransaction tx, CancellationToken ct = default)
    {
        var wrapper = tx as SqliteTransactionWrapper
            ?? throw new InvalidOperationException("Expected SqliteTransactionWrapper.");
        wrapper.Transaction.Rollback();
        _store.ActiveTransaction = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a <see cref="SqliteTransaction"/> as an <see cref="ITransaction"/> token.
    /// Clears the ambient transaction on the store when disposed.
    /// </summary>
    internal sealed class SqliteTransactionWrapper : ITransaction
    {
        public SqliteTransaction Transaction { get; }
        private readonly SqliteCacheStore _store;

        public SqliteTransactionWrapper(SqliteTransaction transaction, SqliteCacheStore store)
        {
            Transaction = transaction;
            _store = store;
        }

        public ValueTask DisposeAsync()
        {
            _store.ActiveTransaction = null;
            Transaction.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
