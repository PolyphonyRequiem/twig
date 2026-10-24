using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite <see cref="ILocalWorkItemStore"/> over the durable store's <c>local_work_items</c> and
/// <c>external_identities</c> tables (Epic #1168, durable schema v13).
/// </summary>
/// <remarks>
/// <para>
/// Both tables live in the attached <c>pending</c> schema, which a mirror rebuild never drops.
/// Every write joins <see cref="SqliteCacheStore.ActiveTransaction"/> when a unit of work is
/// open, so it commits or rolls back with the proposal journal.
/// </para>
/// <para>
/// The <see cref="IExternalIdentityMap"/> columns are exactly the components of Epic #1167's
/// <see cref="ExternalWorkItemIdentity"/> plus its opaque <see cref="ProviderRevisionToken"/>.
/// </para>
/// </remarks>
internal sealed class SqliteLocalWorkItemRepository(SqliteCacheStore store) : ILocalWorkItemStore, IExternalIdentityMap
{
    private const string Columns = "local_id, revision, fields_json, created_at, updated_at, deleted_at";

    public async Task<LocalWorkItemRecord> CreateAsync(FieldValues fields, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using var operation = store.AcquireOperation();
        return await InsertAsync(store.GetConnection(), LocalWorkItemId.New(), fields, ct);
    }

    public async Task<LocalWorkItemRecord?> GetAsync(LocalWorkItemId id, CancellationToken ct = default)
    {
        using var operation = store.AcquireOperation();
        return await ReadAsync(store.GetConnection(), id, ct);
    }

    public Task<LocalWriteResult> UpdateAsync(LocalWorkItemId id, long expectedRevision, FieldValues fields, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return CompareAndSwapAsync(
            id,
            expectedRevision,
            "fields_json = @fields",
            cmd => cmd.Parameters.AddWithValue("@fields", Serialize(fields)),
            ct);
    }

    public Task<LocalWriteResult> DeleteAsync(LocalWorkItemId id, long expectedRevision, CancellationToken ct = default)
        => CompareAndSwapAsync(id, expectedRevision, "deleted_at = @now", static _ => { }, ct);

    public Task<ExternalIdentityResolution> ResolveAsync(
        ExternalWorkItemIdentity identity, ProviderRevisionToken? observedToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return InTransactionAsync(async conn =>
        {
            var candidate = LocalWorkItemId.New();

            // The mapping row decides the race: whoever inserts it owns the identity, and a loser
            // reads the winner's id back without leaving an orphan local item behind.
            using (var insert = Command(conn, """
                INSERT INTO external_identities (provider_kind, resource_scope, native_id, local_id, provider_token, observed_at)
                VALUES (@kind, @scope, @native, @local, @token, @now)
                ON CONFLICT(provider_kind, resource_scope, native_id) DO NOTHING
                RETURNING local_id;
                """))
            {
                AddKey(insert, identity);
                insert.Parameters.AddWithValue("@local", candidate.ToString());
                insert.Parameters.AddWithValue("@token", (object?)observedToken?.Value ?? DBNull.Value);
                insert.Parameters.AddWithValue("@now", Now());
                if (await insert.ExecuteScalarAsync(ct) is not null)
                {
                    await InsertAsync(conn, candidate, FieldValues.Empty, ct);
                    return new ExternalIdentityResolution(candidate, Created: true);
                }
            }

            var existing = await FindCoreAsync(conn, identity, ct)
                ?? throw new InvalidOperationException("The external identity mapping vanished inside its own transaction.");
            return new ExternalIdentityResolution(existing, Created: false);
        }, ct);
    }

    /// <summary>
    /// Resolves <paramref name="identity"/> and records <paramref name="observedToken"/> as its latest
    /// observation in one transaction, for sync paths that see the same item repeatedly.
    /// </summary>
    internal Task<ExternalIdentityResolution> ObserveAsync(
        ExternalWorkItemIdentity identity, ProviderRevisionToken? observedToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return InTransactionAsync(async _ =>
        {
            var resolution = await ResolveAsync(identity, observedToken, ct);
            if (!resolution.Created && !await RecordObservedTokenAsync(identity, observedToken, ct))
                throw new InvalidOperationException("The external identity mapping vanished inside its own transaction.");
            return resolution;
        }, ct);
    }

    public async Task<LocalWorkItemId?> FindAsync(ExternalWorkItemIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var operation = store.AcquireOperation();
        return await FindCoreAsync(store.GetConnection(), identity, ct);
    }

    public async Task<IReadOnlyList<ExternalWorkItemIdentity>> GetExternalIdentitiesAsync(LocalWorkItemId id, CancellationToken ct = default)
    {
        RequireId(id);
        using var operation = store.AcquireOperation();
        using var cmd = Command(store.GetConnection(), """
            SELECT provider_kind, resource_scope, native_id FROM external_identities
            WHERE local_id = @local
            ORDER BY provider_kind COLLATE BINARY, resource_scope COLLATE BINARY, native_id COLLATE BINARY;
            """);
        cmd.Parameters.AddWithValue("@local", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var identities = new List<ExternalWorkItemIdentity>();
        while (await reader.ReadAsync(ct))
            identities.Add(Corrupt(() => new ExternalWorkItemIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2))));
        return identities;
    }

    public async Task<bool> RecordObservedTokenAsync(
        ExternalWorkItemIdentity identity, ProviderRevisionToken? token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var operation = store.AcquireOperation();
        using var cmd = Command(store.GetConnection(), """
            UPDATE external_identities SET provider_token = @token, observed_at = @now
            WHERE provider_kind = @kind AND resource_scope = @scope AND native_id = @native;
            """);
        AddKey(cmd, identity);
        cmd.Parameters.AddWithValue("@token", (object?)token?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", Now());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ObservedProviderToken?> GetObservedTokenAsync(ExternalWorkItemIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var operation = store.AcquireOperation();
        using var cmd = Command(store.GetConnection(), """
            SELECT provider_token, observed_at FROM external_identities
            WHERE provider_kind = @kind AND resource_scope = @scope AND native_id = @native;
            """);
        AddKey(cmd, identity);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var token = reader.IsDBNull(0) ? null : Corrupt(() => new ProviderRevisionToken(reader.GetString(0)));
        return new ObservedProviderToken(token, ParseTime(reader.GetString(1)));
    }
    private async Task<LocalWriteResult> CompareAndSwapAsync(
        LocalWorkItemId id, long expectedRevision, string assignment, Action<SqliteCommand> bind, CancellationToken ct)
    {
        if (id.IsEmpty)
            throw new ArgumentException("A local work item id must not be empty.", nameof(id));

        using var operation = store.AcquireOperation();
        var conn = store.GetConnection();

        // One statement is the whole check-and-write, so a stale revision can never land.
        using (var cmd = Command(conn, $"""
            UPDATE local_work_items
            SET {assignment}, revision = revision + 1, updated_at = @now
            WHERE local_id = @id AND revision = @expected AND deleted_at IS NULL
            RETURNING {Columns};
            """))
        {
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.Parameters.AddWithValue("@expected", expectedRevision);
            cmd.Parameters.AddWithValue("@now", Now());
            bind(cmd);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new LocalWriteApplied(Materialize(reader));
        }

        // Nothing was written; the read only classifies why.
        var current = await ReadAsync(conn, id, ct);
        if (current is null)
            return new LocalWorkItemNotFound(id);
        if (current.IsDeleted)
            return new LocalWorkItemTombstoned(current);
        return new LocalRevisionConflict(current);
    }

    private async Task<LocalWorkItemRecord> InsertAsync(SqliteConnection conn, LocalWorkItemId id, FieldValues fields, CancellationToken ct)
    {
        var now = Now();
        using var cmd = Command(conn, $"""
            INSERT INTO local_work_items (local_id, revision, fields_json, created_at, updated_at, deleted_at)
            VALUES (@id, 1, @fields, @now, @now, NULL)
            RETURNING {Columns};
            """);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@fields", Serialize(fields));
        cmd.Parameters.AddWithValue("@now", now);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The local work item insert returned no row.");
        return Materialize(reader);
    }

    private async Task<LocalWorkItemRecord?> ReadAsync(SqliteConnection conn, LocalWorkItemId id, CancellationToken ct)
    {
        using var cmd = Command(conn, $"SELECT {Columns} FROM local_work_items WHERE local_id = @id;");
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Materialize(reader) : null;
    }

    private async Task<LocalWorkItemId?> FindCoreAsync(SqliteConnection conn, ExternalWorkItemIdentity identity, CancellationToken ct)
    {
        using var cmd = Command(conn, """
            SELECT local_id FROM external_identities
            WHERE provider_kind = @kind AND resource_scope = @scope AND native_id = @native;
            """);
        AddKey(cmd, identity);
        return await cmd.ExecuteScalarAsync(ct) is string text ? ParseId(text) : null;
    }

    private async Task<T> InTransactionAsync<T>(Func<SqliteConnection, Task<T>> body, CancellationToken ct)
    {
        using var operation = store.AcquireOperation();
        var conn = store.GetConnection();
        if (store.ActiveTransaction is not null)
            return await body(conn);

        using var transaction = conn.BeginTransaction();
        store.ActiveTransaction = transaction;
        try
        {
            var result = await body(conn);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        finally
        {
            store.ActiveTransaction = null;
        }
    }

    private SqliteCommand Command(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = store.ActiveTransaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static LocalWorkItemRecord Materialize(SqliteDataReader reader)
        => new(
            ParseId(reader.GetString(0)),
            reader.GetInt64(1),
            Deserialize(reader.GetString(2)),
            ParseTime(reader.GetString(3)),
            ParseTime(reader.GetString(4)),
            reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)));

    private static LocalWorkItemId ParseId(string text)
        => LocalWorkItemId.TryParse(text, out var id)
            ? id
            : throw new InvalidDataException($"The durable store holds a malformed local work item id '{text}'.");

    private static DateTimeOffset ParseTime(string text)
        => DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Serialize(FieldValues fields)
        => JsonSerializer.Serialize(fields, TwigJsonContext.Default.FieldValues);

    private static FieldValues Deserialize(string json)
        => JsonSerializer.Deserialize(json, TwigJsonContext.Default.FieldValues)
            ?? throw new InvalidDataException("The durable store holds a null field-value map.");

    private static void AddKey(SqliteCommand cmd, ExternalWorkItemIdentity identity)
    {
        cmd.Parameters.AddWithValue("@kind", identity.ProviderKind);
        cmd.Parameters.AddWithValue("@scope", identity.ResourceScope);
        cmd.Parameters.AddWithValue("@native", identity.NativeId);
    }

    private static void RequireId(LocalWorkItemId id)
    {
        if (id.IsEmpty)
            throw new ArgumentException("A local work item id must not be empty.", nameof(id));
    }

    private static T Corrupt<T>(Func<T> construct)
    {
        try { return construct(); }
        catch (ArgumentException ex) { throw new InvalidDataException("The durable store holds a malformed external identity row.", ex); }
    }
}
