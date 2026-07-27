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

    public async Task<PublishIntent> RecordIntentAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        // An open intent is returned as-is rather than replaced. Re-minting would change the
        // idempotency tag, and the recovery query would then look for a tag that is not on the
        // item the first attempt may already have created — reintroducing the duplicate this
        // ledger exists to prevent.
        var existing = await GetIntentAsync(identity, ct);
        if (existing is { IsOpen: true })
            return existing;

        var intent = new PublishIntent
        {
            Identity = identity,
            IdempotencyTag = PublishIntent.TagFor(identity),
            RecordedAt = DateTimeOffset.UtcNow,
        };

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            INSERT INTO publish_intents (staged_identity, idempotency_tag, recorded_at, published_id, completed_at)
            VALUES (@identity, @tag, @recordedAt, NULL, NULL)
            ON CONFLICT(staged_identity) DO UPDATE SET
                idempotency_tag = excluded.idempotency_tag,
                recorded_at = excluded.recorded_at,
                published_id = NULL,
                completed_at = NULL;
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.Parameters.AddWithValue("@tag", intent.IdempotencyTag);
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
            SELECT staged_identity, idempotency_tag, recorded_at, published_id, completed_at
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
            SELECT staged_identity, idempotency_tag, recorded_at, published_id, completed_at
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
            IdempotencyTag = reader.GetString(1),
            RecordedAt = DateTimeOffset.Parse(reader.GetString(2)),
            PublishedId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            CompletedAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        };
    }
}
