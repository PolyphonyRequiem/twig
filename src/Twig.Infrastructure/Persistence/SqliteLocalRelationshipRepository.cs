using System.Globalization;
using Microsoft.Data.Sqlite;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="ILocalRelationshipRepository"/> over #1168's durable
/// <c>local_relationships</c> table (v13) plus #1170's additive <c>storage</c> and
/// <c>publication</c> columns (v14). Parameterized SQL only; endpoints are persisted
/// <see cref="LocalWorkItemId"/> "D" text, enums are canonical string tokens and timestamps are
/// "O" UTC. CAS writes run in one transaction (the ambient one if present).
/// </summary>
internal sealed class SqliteLocalRelationshipRepository(SqliteCacheStore store) : ILocalRelationshipRepository
{
    private const string Columns =
        "source_local_id, target_local_id, kind, storage, publication, revision, created_at, updated_at, deleted_at";

    public Task<IReadOnlyList<LocalRelationship>> GetForItemsAsync(
        IReadOnlyCollection<LocalWorkItemId> itemIds, bool includeDeleted = false, CancellationToken ct = default)
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
            cmd.Parameters.AddWithValue(name, Key(id));
        }

        var inList = string.Join(", ", names);
        cmd.CommandText = $"""
            SELECT {Columns} FROM local_relationships
            WHERE (source_local_id IN ({inList}) OR target_local_id IN ({inList}))
              AND (@includeDeleted = 1 OR deleted_at IS NULL)
            ORDER BY source_local_id, target_local_id, kind;
            """;
        cmd.Parameters.AddWithValue("@includeDeleted", includeDeleted ? 1 : 0);

        var result = new List<LocalRelationship>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(Read(reader));
        return Task.FromResult<IReadOnlyList<LocalRelationship>>(result);
    }

    public Task<LocalRelationship?> GetAsync(
        LocalWorkItemId source, LocalWorkItemId target, LinkKind kind, CancellationToken ct = default)
    {
        using var bindingOperation = store.AcquireOperation();
        var conn = store.GetConnection();
        (source, target) = LocalRelationship.Normalize(kind, source, target);
        return Task.FromResult(Find(conn, store.ActiveTransaction, source, target, kind));
    }

    public Task<LocalRelationshipWrite> UpsertAsync(
        LocalRelationship relationship, long? expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        if (relationship.IsDeleted)
            throw new ArgumentException("Use TombstoneAsync to delete a local relationship.", nameof(relationship));

        var (source, target) = LocalRelationship.Normalize(relationship.Kind, relationship.Source, relationship.Target);
        relationship = relationship with { Source = source, Target = target };

        return Task.FromResult(InTransaction((conn, tx) =>
        {
            var current = Find(conn, tx, source, target, relationship.Kind);
            if (current?.Revision != expectedRevision)
                return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current);

            var written = relationship with
            {
                Revision = (current?.Revision ?? 0) + 1,
                CreatedAt = current?.CreatedAt ?? relationship.CreatedAt,
                DeletedAt = null,
            };
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO local_relationships ({Columns})
                VALUES (@source, @target, @kind, @storage, @publication, @revision, @createdAt, @updatedAt, NULL)
                ON CONFLICT (source_local_id, target_local_id, kind) DO UPDATE SET
                    storage = excluded.storage,
                    publication = excluded.publication,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at,
                    deleted_at = NULL;
                """;
            Bind(cmd, written);
            cmd.ExecuteNonQuery();
            return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Written, written);
        }));
    }

    public Task<LocalRelationshipWrite> TombstoneAsync(
        LocalWorkItemId source, LocalWorkItemId target, LinkKind kind, long expectedRevision,
        DateTimeOffset deletedAt, CancellationToken ct = default)
    {
        (source, target) = LocalRelationship.Normalize(kind, source, target);
        return Task.FromResult(InTransaction((conn, tx) =>
        {
            var current = Find(conn, tx, source, target, kind);
            if (current is null || current.Revision != expectedRevision)
                return new LocalRelationshipWrite(LocalRelationshipWriteStatus.Conflict, current);

            var written = current with { Revision = current.Revision + 1, UpdatedAt = deletedAt, DeletedAt = deletedAt };
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE local_relationships
                SET revision = @revision, updated_at = @updatedAt, deleted_at = @deletedAt
                WHERE source_local_id = @source AND target_local_id = @target AND kind = @kind;
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

    private static LocalRelationship? Find(
        SqliteConnection conn, SqliteTransaction? tx, LocalWorkItemId source, LocalWorkItemId target, LinkKind kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {Columns} FROM local_relationships
            WHERE source_local_id = @source AND target_local_id = @target AND kind = @kind;
            """;
        cmd.Parameters.AddWithValue("@source", Key(source));
        cmd.Parameters.AddWithValue("@target", Key(target));
        cmd.Parameters.AddWithValue("@kind", LinkKindNames.ToStableName(kind));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static string Key(LocalWorkItemId id) =>
        id.IsEmpty ? throw new ArgumentException("A local relationship endpoint cannot be empty.", nameof(id)) : id.ToString();

    private static void Bind(SqliteCommand cmd, LocalRelationship relationship)
    {
        cmd.Parameters.AddWithValue("@source", Key(relationship.Source));
        cmd.Parameters.AddWithValue("@target", Key(relationship.Target));
        cmd.Parameters.AddWithValue("@kind", LinkKindNames.ToStableName(relationship.Kind));
        cmd.Parameters.AddWithValue("@storage", CapabilityTokens.ToToken(CapabilityTokens.Storage, relationship.Storage));
        cmd.Parameters.AddWithValue("@publication", CapabilityTokens.ToToken(CapabilityTokens.Publication, relationship.Publication));
        cmd.Parameters.AddWithValue("@revision", relationship.Revision);
        cmd.Parameters.AddWithValue("@createdAt", Stamp(relationship.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", Stamp(relationship.UpdatedAt));
        cmd.Parameters.AddWithValue("@deletedAt", relationship.DeletedAt is { } deleted ? Stamp(deleted) : DBNull.Value);
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static LocalRelationship Read(SqliteDataReader reader)
    {
        var sourceText = reader.GetString(0);
        var targetText = reader.GetString(1);
        var kindToken = reader.GetString(2);
        var storageToken = reader.IsDBNull(3) ? "<null>" : reader.GetString(3);
        var publicationToken = reader.IsDBNull(4) ? "<null>" : reader.GetString(4);
        if (!LocalWorkItemId.TryParse(sourceText, out var source)
            || !LocalWorkItemId.TryParse(targetText, out var target)
            || !LinkKindNames.TryParseStableName(kindToken, out var kind)
            || kind == LinkKind.Artifact
            || !CapabilityTokens.TryResolve(CapabilityTokens.Storage, storageToken, out EmulationStorage storage)
            || !CapabilityTokens.TryResolve(CapabilityTokens.Publication, publicationToken, out PublicationState publication))
        {
            throw new InvalidOperationException(
                $"local-relationship-unreadable: stored values '{sourceText}', '{targetText}', '{kindToken}', "
                + $"'{storageToken}', '{publicationToken}' are not canonical.");
        }

        return new LocalRelationship(
            source,
            target,
            kind,
            storage,
            publication,
            reader.GetInt64(5),
            ParseStamp(reader.GetString(6)),
            ParseStamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseStamp(reader.GetString(8)));
    }
}
