using Microsoft.Data.Sqlite;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="ILocalRelationshipRepository"/> over the durable
/// <c>local_relationships</c> table. Parameterized SQL only; enum values are stored as
/// canonical string tokens. CAS writes run in one transaction (the ambient one if present).
/// </summary>
internal sealed class SqliteLocalRelationshipRepository(SqliteCacheStore store) : ILocalRelationshipRepository
{
    private const string Columns = "source_id, target_id, kind, storage, publication, revision, deleted, updated_at";

    public Task<IReadOnlyList<LocalRelationship>> GetForItemsAsync(
        IReadOnlyCollection<int> itemIds, bool includeDeleted = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
            return Task.FromResult<IReadOnlyList<LocalRelationship>>([]);

        using var bindingOperation = store.AcquireOperation();
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = store.ActiveTransaction;

        var names = new List<string>(itemIds.Count);
        var index = 0;
        foreach (var id in itemIds.Distinct())
        {
            var name = $"@id{index++}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, id);
        }

        var inList = string.Join(", ", names);
        cmd.CommandText = $"""
            SELECT {Columns} FROM local_relationships
            WHERE (source_id IN ({inList}) OR target_id IN ({inList}))
              AND (@includeDeleted = 1 OR deleted = 0)
            ORDER BY source_id, target_id, kind;
            """;
        cmd.Parameters.AddWithValue("@includeDeleted", includeDeleted ? 1 : 0);

        var result = new List<LocalRelationship>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(Read(reader));
        return Task.FromResult<IReadOnlyList<LocalRelationship>>(result);
    }

    public Task<LocalRelationship?> GetAsync(int sourceId, int targetId, LinkKind kind, CancellationToken ct = default)
    {
        using var bindingOperation = store.AcquireOperation();
        var conn = store.GetConnection();
        return Task.FromResult(Find(conn, store.ActiveTransaction, sourceId, targetId, kind));
    }

    public Task<LocalRelationshipWrite> UpsertAsync(
        LocalRelationship relationship, long? expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        if (relationship.Deleted)
            throw new ArgumentException("Use TombstoneAsync to delete a local relationship.", nameof(relationship));

        return Task.FromResult(InTransaction((conn, tx) =>
        {
            var current = Find(conn, tx, relationship.SourceId, relationship.TargetId, relationship.Kind);
            if (current?.Revision != expectedRevision)
                return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current);

            var written = relationship with { Revision = (current?.Revision ?? 0) + 1, Deleted = false };
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO local_relationships ({Columns})
                VALUES (@source, @target, @kind, @storage, @publication, @revision, 0, @updatedAt)
                ON CONFLICT (source_id, target_id, kind) DO UPDATE SET
                    storage = excluded.storage,
                    publication = excluded.publication,
                    revision = excluded.revision,
                    deleted = 0,
                    updated_at = excluded.updated_at;
                """;
            Bind(cmd, written);
            cmd.ExecuteNonQuery();
            return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Written, written);
        }));
    }

    public Task<LocalRelationshipWrite> TombstoneAsync(
        int sourceId, int targetId, LinkKind kind, long expectedRevision, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        return Task.FromResult(InTransaction((conn, tx) =>
        {
            var current = Find(conn, tx, sourceId, targetId, kind);
            if (current is null || current.Revision != expectedRevision)
                return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current);

            var written = current with { Revision = current.Revision + 1, Deleted = true, UpdatedAt = updatedAt };
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE local_relationships
                SET deleted = 1, revision = @revision, updated_at = @updatedAt
                WHERE source_id = @source AND target_id = @target AND kind = @kind;
                """;
            Bind(cmd, written);
            cmd.ExecuteNonQuery();
            return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Written, written);
        }));
    }

    private LocalRelationshipWrite InTransaction(Func<SqliteConnection, SqliteTransaction, LocalRelationshipWrite> work)
    {
        using var bindingOperation = store.AcquireOperation();
        var conn = store.GetConnection();
        var ambient = store.ActiveTransaction;
        if (ambient is not null)
            return work(conn, ambient);

        using var tx = conn.BeginTransaction();
        var result = work(conn, tx);
        tx.Commit();
        return result;
    }

    private static LocalRelationship? Find(SqliteConnection conn, SqliteTransaction? tx, int sourceId, int targetId, LinkKind kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {Columns} FROM local_relationships
            WHERE source_id = @source AND target_id = @target AND kind = @kind;
            """;
        cmd.Parameters.AddWithValue("@source", sourceId);
        cmd.Parameters.AddWithValue("@target", targetId);
        cmd.Parameters.AddWithValue("@kind", LinkKindTokens.ToToken(kind));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static void Bind(SqliteCommand cmd, LocalRelationship relationship)
    {
        cmd.Parameters.AddWithValue("@source", relationship.SourceId);
        cmd.Parameters.AddWithValue("@target", relationship.TargetId);
        cmd.Parameters.AddWithValue("@kind", LinkKindTokens.ToToken(relationship.Kind));
        cmd.Parameters.AddWithValue("@storage", CapabilityTokens.ToToken(CapabilityTokens.Storage, relationship.Storage));
        cmd.Parameters.AddWithValue("@publication", CapabilityTokens.ToToken(CapabilityTokens.Publication, relationship.Publication));
        cmd.Parameters.AddWithValue("@revision", relationship.Revision);
        cmd.Parameters.AddWithValue("@updatedAt", relationship.UpdatedAt.ToString("o"));
    }

    private static LocalRelationship Read(SqliteDataReader reader)
    {
        var kindToken = reader.GetString(2);
        var storageToken = reader.GetString(3);
        var publicationToken = reader.GetString(4);
        if (!LinkKindTokens.TryResolve(kindToken, out var kind)
            || !CapabilityTokens.TryResolve(CapabilityTokens.Storage, storageToken, out EmulationStorage storage)
            || !CapabilityTokens.TryResolve(CapabilityTokens.Publication, publicationToken, out PublicationState publication))
        {
            throw new InvalidOperationException(
                $"local-relationship-unreadable: stored tokens '{kindToken}', '{storageToken}', '{publicationToken}' are not canonical.");
        }

        return new LocalRelationship(
            reader.GetInt32(0),
            reader.GetInt32(1),
            kind,
            storage,
            publication,
            reader.GetInt64(5),
            reader.GetInt64(6) != 0,
            DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
    }
}
