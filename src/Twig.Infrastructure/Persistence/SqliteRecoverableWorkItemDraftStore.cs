using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>Durable shared draft storage in pending.db, isolated by stable item qualification.</summary>
internal sealed class SqliteRecoverableWorkItemDraftStore(SqliteCacheStore store) : IRecoverableWorkItemDraftStore
{
    public Task<RecoverableWorkItemDraft?> GetAsync(QualifiedWorkItemKey itemKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ct.ThrowIfCancellationRequested();
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = """
            SELECT generation, draft_json
            FROM pending.recoverable_work_item_drafts
            WHERE checkout_root = @checkoutRoot
              AND connection_id = @connectionId
              AND binding_id = @bindingId
              AND work_item_id = @workItemId;
            """;
        AddKey(command, itemKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<RecoverableWorkItemDraft?>(null);

        var generation = reader.GetInt64(0);
        var json = reader.GetString(1);
        var draft = JsonSerializer.Deserialize(json, TwigJsonContext.Default.RecoverableWorkItemDraft)
            ?? throw new InvalidDataException("The persisted recoverable draft was empty.");
        if (draft.ItemKey != itemKey || draft.Generation != generation)
            throw new InvalidDataException("The persisted recoverable draft does not match its indexed identity or generation.");
        return Task.FromResult<RecoverableWorkItemDraft?>(draft);
    }

    public Task<bool> TrySaveAsync(
        RecoverableWorkItemDraft draft,
        long? expectedGeneration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (expectedGeneration is < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (expectedGeneration is { } expected && draft.Generation <= expected)
            throw new ArgumentException("A saved draft must advance its generation.", nameof(draft));
        ct.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(draft, TwigJsonContext.Default.RecoverableWorkItemDraft);
        using var operation = store.AcquireOperation();
        var connection = store.GetConnection();
        // Editing owns a wider transaction that also fences the native observation.
        using var ownedTransaction = store.ActiveTransaction is null ? connection.BeginTransaction() : null;
        var transaction = store.ActiveTransaction ?? ownedTransaction!;
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (expectedGeneration is null)
            {
                command.CommandText = """
                    INSERT OR IGNORE INTO pending.recoverable_work_item_drafts
                        (checkout_root, connection_id, binding_id, work_item_id, generation, draft_json, updated_at)
                    VALUES (@checkoutRoot, @connectionId, @bindingId, @workItemId, @generation, @draftJson, @updatedAt);
                    """;
            }
            else
            {
                command.CommandText = """
                    UPDATE pending.recoverable_work_item_drafts
                    SET generation = @generation, draft_json = @draftJson, updated_at = @updatedAt
                    WHERE checkout_root = @checkoutRoot
                      AND connection_id = @connectionId
                      AND binding_id = @bindingId
                      AND work_item_id = @workItemId
                      AND generation = @expectedGeneration
                      AND generation < @generation;
                    """;
                command.Parameters.AddWithValue("@expectedGeneration", expectedGeneration.Value);
            }

            AddKey(command, draft.ItemKey);
            command.Parameters.AddWithValue("@generation", draft.Generation);
            command.Parameters.AddWithValue("@draftJson", json);
            command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("o"));
            var saved = command.ExecuteNonQuery() == 1;
            ownedTransaction?.Commit();
            return Task.FromResult(saved);
        }
        catch
        {
            ownedTransaction?.Rollback();
            throw;
        }
    }

    private static void AddKey(Microsoft.Data.Sqlite.SqliteCommand command, QualifiedWorkItemKey key)
    {
        command.Parameters.AddWithValue("@checkoutRoot", key.CheckoutRoot);
        command.Parameters.AddWithValue("@connectionId", key.ConnectionId);
        command.Parameters.AddWithValue("@bindingId", key.BindingId);
        command.Parameters.AddWithValue("@workItemId", key.WorkItemId);
    }
}
