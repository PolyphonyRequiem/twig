using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Store.Sqlite;

public sealed class SqliteStore : IStore
{
    private const int ApplicationId = 0x54574353; // TWCS, not a legacy Twig cache.
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    private SqliteStore(string path) => _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        DefaultTimeout = 5,
    }.ToString();

    public static async Task<Result<SqliteStore>> CreateAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path))
            return Result<SqliteStore>.Fail(ErrorCode.InvalidInput, "An explicit absolute database file path is required.");
        ct.ThrowIfCancellationRequested();
        try
        {
            // Reserve a new file without ever opening an existing workspace for initialization.
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var store = new SqliteStore(path);
            await using var db = new SqliteConnection(store._connectionString);
            await db.OpenAsync(ct);
            await Execute(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", ct);
            using var transaction = db.BeginTransaction(deferred: false);
            await Execute(db, transaction, $"""
                PRAGMA application_id={ApplicationId};
                PRAGMA user_version={SchemaVersion};
                CREATE TABLE core_snapshots (
                    connection TEXT PRIMARY KEY NOT NULL,
                    version INTEGER NOT NULL CHECK(version > 0),
                    document TEXT NOT NULL
                ) STRICT;
                """, ct);
            transaction.Commit();
            return Result<SqliteStore>.Ok(store);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return Result<SqliteStore>.Fail(ErrorCode.StorageUnavailable,
                $"Cannot initialize a new clean-core database. Existing files are never replaced; failed new files are retained for inspection. {ex.Message}");
        }
    }

    public static async Task<Result<SqliteStore>> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path))
            return Result<SqliteStore>.Fail(ErrorCode.InvalidInput, "An explicit absolute database file path is required.");
        ct.ThrowIfCancellationRequested();
        try
        {
            var store = new SqliteStore(path);
            await using var db = new SqliteConnection(store._connectionString);
            await db.OpenAsync(ct);
            await CheckFormat(db, null, ct);
            return Result<SqliteStore>.Ok(store);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return Result<SqliteStore>.Fail(ErrorCode.StorageUnavailable, $"Cannot open clean-core database: {ex.Message}");
        }
    }

    public async Task<Result<StoreSnapshot>> ReadAsync(Connection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var db = new SqliteConnection(_connectionString);
            await db.OpenAsync(ct);
            using var transaction = db.BeginTransaction(deferred: true);
            await CheckFormat(db, transaction, ct);
            var snapshot = await Read(db, transaction, connection, ct);
            transaction.Commit();
            return Result<StoreSnapshot>.Ok(snapshot);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return Result<StoreSnapshot>.Fail(ErrorCode.StorageUnavailable, $"Cannot read clean-core snapshot: {ex.Message}");
        }
    }

    public async Task<Result<long>> CommitAsync(Connection connection, long expectedVersion, StoreSnapshot next,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(next);
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var db = new SqliteConnection(_connectionString);
            await db.OpenAsync(ct);
            await Execute(db, null, "PRAGMA synchronous=FULL;", ct);
            // IMMEDIATE serializes acquisition before reading the current CAS, not after it.
            using var transaction = db.BeginTransaction(deferred: false);
            await CheckFormat(db, transaction, ct);
            var before = await Read(db, transaction, connection, ct);
            if (before.Version != expectedVersion)
                return Result<long>.Fail(ErrorCode.Conflict, "The connection's store version changed; no data was committed.");
            var errors = Core.Core.ValidateCommit(before, next);
            if (!errors.IsEmpty)
                return Result<long>.Fail(new Error(errors[0].Code, "Snapshot violates the clean-core commit contract.")
                { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
            if (next.Proposals.Any(p => p.Connection != connection))
                return Result<long>.Fail(ErrorCode.Conflict, "Retained intent must belong to this exact connection binding.");
            var document = Persistence.Serialize(next);
            _ = Persistence.Deserialize(document);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO core_snapshots(connection, version, document) VALUES($connection, $version, $document)
                ON CONFLICT(connection) DO UPDATE SET version=excluded.version, document=excluded.document;
                """;
            command.Parameters.AddWithValue("$connection", Persistence.ConnectionText(connection));
            command.Parameters.AddWithValue("$version", next.Version);
            command.Parameters.AddWithValue("$document", document);
            await command.ExecuteNonQueryAsync(ct);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<long>.Ok(next.Version);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return Result<long>.Fail(ErrorCode.Conflict, $"SQLite writer acquisition was busy; no replay is authorized. {ex.Message}");
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return Result<long>.Fail(ErrorCode.StorageUnavailable, $"Cannot commit clean-core snapshot; transaction rolled back. {ex.Message}");
        }
    }

    private static async Task<StoreSnapshot> Read(SqliteConnection db, SqliteTransaction transaction,
        Connection connection, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, document FROM core_snapshots WHERE connection=$connection;";
        command.Parameters.AddWithValue("$connection", Persistence.ConnectionText(connection));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new StoreSnapshot(0);
        var snapshot = Persistence.Deserialize(reader.GetString(1));
        if (snapshot.Version != reader.GetInt64(0) || snapshot.Proposals.Any(p => p.Connection != connection))
            throw new InvalidDataException("Snapshot version or proposal connection does not match its stored partition.");
        return snapshot;
    }

    private static async Task CheckFormat(SqliteConnection db, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id;";
        var application = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await command.ExecuteScalarAsync(ct);
        if (application != ApplicationId || version != SchemaVersion || mode != "wal")
            throw new InvalidDataException("Not a supported clean-core schema version 1 / WAL database. No migration or reinitialization was attempted.");
        command.CommandText = "SELECT connection, version, document FROM core_snapshots LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync(ct);
    }

    private static async Task Execute(SqliteConnection db, SqliteTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool IsStorageFailure(Exception ex) => ex is SqliteException or IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException;
}
