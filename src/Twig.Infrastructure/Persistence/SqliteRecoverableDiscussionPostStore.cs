using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Discussion;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed class SqliteRecoverableDiscussionPostStore(SqliteCacheStore store)
    : IRecoverableDiscussionPostStore
{
    public Task<RecoverableDiscussionPost?> GetAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT generation, state, payload_digest, post_json
            FROM pending.discussion_post_operations
            WHERE operation_id = @operationId;
            """;
        command.Parameters.AddWithValue("@operationId", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<RecoverableDiscussionPost?>(null);

        var generation = reader.GetInt64(0);
        var state = reader.GetString(1);
        var digest = reader.GetString(2);
        var post = Deserialize(reader.GetString(3));
        ValidateIndex(post, operationId, generation, state, digest);
        return Task.FromResult<RecoverableDiscussionPost?>(post);
    }

    public Task<IReadOnlyList<RecoverableDiscussionPost>> GetUnresolvedAsync(
        QualifiedWorkItemKey itemKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ct.ThrowIfCancellationRequested();
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT operation_id, generation, state, payload_digest, post_json
            FROM pending.discussion_post_operations
            WHERE checkout_root = @checkoutRoot
              AND connection_id = @connectionId
              AND binding_id = @bindingId
              AND work_item_id = @workItemId
              AND state IN ('Dispatching', 'Indeterminate')
            ORDER BY created_at, operation_id;
            """;
        AddKey(command, itemKey);

        using var reader = command.ExecuteReader();
        var posts = new List<RecoverableDiscussionPost>();
        while (reader.Read())
        {
            var operationId = Guid.Parse(reader.GetString(0));
            var generation = reader.GetInt64(1);
            var state = reader.GetString(2);
            var digest = reader.GetString(3);
            var post = Deserialize(reader.GetString(4));
            ValidateIndex(post, operationId, generation, state, digest);
            if (post.ItemKey != itemKey || !post.RequiresReconciliation)
                throw new InvalidDataException("The unresolved discussion post does not match its indexed item or state.");
            posts.Add(post);
        }
        return Task.FromResult<IReadOnlyList<RecoverableDiscussionPost>>(posts.AsReadOnly());
    }

    public Task<IReadOnlyList<RecoverableDiscussionPost>> GetJournalAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT generation, state, post_json
            FROM pending.discussion_post_journal
            WHERE operation_id = @operationId
            ORDER BY generation;
            """;
        command.Parameters.AddWithValue("@operationId", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        var journal = new List<RecoverableDiscussionPost>();
        while (reader.Read())
        {
            var generation = reader.GetInt64(0);
            var state = reader.GetString(1);
            var post = Deserialize(reader.GetString(2));
            if (post.OperationId != operationId
                || post.Generation != generation
                || !string.Equals(post.State.ToString(), state, StringComparison.Ordinal))
                throw new InvalidDataException("The discussion post journal row does not match its operation, generation, or state.");
            journal.Add(post);
        }
        return Task.FromResult<IReadOnlyList<RecoverableDiscussionPost>>(journal.AsReadOnly());
    }

    public Task<bool> TryCreateAsync(RecoverableDiscussionPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (post.Generation != 0 || post.State != DiscussionPostState.Prepared || post.Receipt is not null)
            throw new ArgumentException("A new discussion post must be an unstarted generation-zero intent.", nameof(post));
        ct.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(post, TwigJsonContext.Default.RecoverableDiscussionPost);
        using var operation = store.AcquireOperation();
        var connection = store.GetConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO pending.discussion_post_operations
                    (operation_id, checkout_root, connection_id, binding_id, work_item_id,
                     payload_digest, generation, state, created_at, post_json)
                VALUES (@operationId, @checkoutRoot, @connectionId, @bindingId, @workItemId,
                        @payloadDigest, @generation, @state, @createdAt, @postJson);
                """;
            AddPost(command, post, json);
            var created = command.ExecuteNonQuery() == 1;
            if (created)
                AppendJournal(connection, transaction, post, json);
            transaction.Commit();
            return Task.FromResult(created);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task<bool> TrySaveAsync(
        RecoverableDiscussionPost post,
        long expectedGeneration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (expectedGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (post.Generation != checked(expectedGeneration + 1))
            throw new ArgumentException("A saved discussion post must advance exactly one generation.", nameof(post));
        ct.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(post, TwigJsonContext.Default.RecoverableDiscussionPost);
        using var operation = store.AcquireOperation();
        var connection = store.GetConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE pending.discussion_post_operations
                SET generation = @generation, state = @state, post_json = @postJson
                WHERE operation_id = @operationId
                  AND checkout_root = @checkoutRoot
                  AND connection_id = @connectionId
                  AND binding_id = @bindingId
                  AND work_item_id = @workItemId
                  AND payload_digest = @payloadDigest
                  AND generation = @expectedGeneration
                  AND @generation = @expectedGeneration + 1
                  AND (
                      (state = 'Prepared' AND @state = 'Dispatching')
                      OR (state = 'Dispatching' AND @state IN ('Indeterminate', 'Confirmed'))
                      OR (state = 'Indeterminate' AND @state = 'Confirmed')
                  );
                """;
            command.Parameters.AddWithValue("@expectedGeneration", expectedGeneration);
            AddPost(command, post, json);
            var saved = command.ExecuteNonQuery() == 1;
            if (saved)
                AppendJournal(connection, transaction, post, json);
            transaction.Commit();
            return Task.FromResult(saved);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static RecoverableDiscussionPost Deserialize(string json)
        => JsonSerializer.Deserialize(json, TwigJsonContext.Default.RecoverableDiscussionPost)
            ?? throw new InvalidDataException("The persisted discussion post was empty.");

    private static void ValidateIndex(
        RecoverableDiscussionPost post,
        Guid operationId,
        long generation,
        string state,
        string digest)
    {
        if (post.OperationId != operationId
            || post.Generation != generation
            || !string.Equals(post.State.ToString(), state, StringComparison.Ordinal)
            || !string.Equals(post.PayloadDigest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("The persisted discussion post does not match its indexed operation, generation, or state.");
    }

    private static void AddPost(SqliteCommand command, RecoverableDiscussionPost post, string json)
    {
        command.Parameters.AddWithValue("@operationId", post.OperationId.ToString("D"));
        command.Parameters.AddWithValue("@checkoutRoot", post.ItemKey.CheckoutRoot);
        command.Parameters.AddWithValue("@connectionId", post.ItemKey.ConnectionId);
        command.Parameters.AddWithValue("@bindingId", post.ItemKey.BindingId);
        command.Parameters.AddWithValue("@workItemId", post.ItemKey.WorkItemId);
        command.Parameters.AddWithValue("@payloadDigest", post.PayloadDigest);
        command.Parameters.AddWithValue("@generation", post.Generation);
        command.Parameters.AddWithValue("@state", post.State.ToString());
        command.Parameters.AddWithValue("@createdAt", post.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("@postJson", json);
    }

    private static void AddKey(SqliteCommand command, QualifiedWorkItemKey key)
    {
        command.Parameters.AddWithValue("@checkoutRoot", key.CheckoutRoot);
        command.Parameters.AddWithValue("@connectionId", key.ConnectionId);
        command.Parameters.AddWithValue("@bindingId", key.BindingId);
        command.Parameters.AddWithValue("@workItemId", key.WorkItemId);
    }

    private static void AppendJournal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecoverableDiscussionPost post,
        string json)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pending.discussion_post_journal
                (operation_id, generation, state, recorded_at, post_json)
            VALUES (@operationId, @generation, @state, @recordedAt, @postJson);
            """;
        command.Parameters.AddWithValue("@operationId", post.OperationId.ToString("D"));
        command.Parameters.AddWithValue("@generation", post.Generation);
        command.Parameters.AddWithValue("@state", post.State.ToString());
        command.Parameters.AddWithValue("@recordedAt", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@postJson", json);
        command.ExecuteNonQuery();
    }
}
