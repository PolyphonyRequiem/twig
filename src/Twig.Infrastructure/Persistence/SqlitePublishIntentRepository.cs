using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPublishIntentRepository"/> over the durable
/// store's <c>publish_intents</c> table (wayfinder 0015).
/// <para>
/// The table lives in the attached <c>pending</c> schema; SQLite resolves unqualified table
/// names across attached schemas, so the SQL below carries no prefix (0013).
/// </para>
/// </summary>
public sealed class SqlitePublishIntentRepository : IPublishIntentRepository
{
    private readonly SqliteCacheStore _store;

    public SqlitePublishIntentRepository(SqliteCacheStore store) => _store = store;

    public async Task<PublishIntent> RecordIntentAsync(
        StagedIdentity identity,
        string title,
        string typeName,
        CancellationToken ct = default)
    {
        // An EXISTING intent is returned as-is — whether it is open or completed. Two distinct
        // reasons, and the second one is the bug review caught:
        //
        // OPEN: re-stamping RecordedAt would move the lower bound the recovery query fences on
        // PAST the create the first attempt may already have made, so the orphan stops being
        // findable.
        //
        // COMPLETED: the row NAMES the ADO id. A previous attempt created the item and then
        // died in step 10, so this row is the only surviving proof the item exists. An earlier
        // version preserved the row only when `IsOpen`, letting a completed intent fall through
        // to the ON CONFLICT below — which set published_id back to NULL and re-stamped
        // recorded_at, destroying the evidence AND moving the fence past the orphan. CreateAsync
        // then fired again: a duplicate, in exactly the #270 scenario this ledger exists to
        // prevent. The caller adopts PublishedId when it is set.
        var existing = await GetIntentAsync(identity, ct);
        if (existing is not null)
            return existing;

        var intent = new PublishIntent
        {
            Identity = identity,
            Title = title,
            TypeName = typeName,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            INSERT INTO publish_intents (staged_identity, title, type_name, recorded_at, published_id, completed_at)
            VALUES (@identity, @title, @typeName, @recordedAt, NULL, NULL)
            ON CONFLICT(staged_identity) DO UPDATE SET
                title = excluded.title,
                type_name = excluded.type_name,
                recorded_at = excluded.recorded_at,
                published_id = NULL,
                completed_at = NULL;
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.Parameters.AddWithValue("@title", intent.Title);
        cmd.Parameters.AddWithValue("@typeName", intent.TypeName);
        cmd.Parameters.AddWithValue("@recordedAt", intent.RecordedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        return intent;
    }

    public Task CompleteIntentAsync(StagedIdentity identity, int publishedId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            UPDATE publish_intents
            SET published_id = @publishedId, completed_at = @completedAt
            WHERE staged_identity = @identity;
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.Parameters.AddWithValue("@publishedId", publishedId);
        cmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<PublishIntent?> GetIntentAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            SELECT staged_identity, title, type_name, recorded_at, published_id, completed_at
            FROM publish_intents
            WHERE staged_identity = @identity;
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? Map(reader) : null);
    }

    public Task<IReadOnlyList<PublishIntent>> GetOpenIntentsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            SELECT staged_identity, title, type_name, recorded_at, published_id, completed_at
            FROM publish_intents
            WHERE published_id IS NULL
            ORDER BY recorded_at;
            """;

        var intents = new List<PublishIntent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Map(reader) is { } intent)
                intents.Add(intent);
        }

        return Task.FromResult<IReadOnlyList<PublishIntent>>(intents);
    }

    // An unparseable identity is skipped rather than coerced to a plausible value (0003 §4).
    private static PublishIntent? Map(SqliteDataReader reader)
    {
        if (!StagedIdentity.TryParse(reader.GetString(0), out var identity))
            return null;

        return new PublishIntent
        {
            Identity = identity,
            Title = reader.GetString(1),
            TypeName = reader.GetString(2),
            RecordedAt = DateTimeOffset.Parse(reader.GetString(3)),
            PublishedId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            CompletedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        };
    }
}
