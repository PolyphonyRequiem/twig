using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Providers;

/// <summary>Commits complete remote observations without writing any local-owned field or revision.</summary>
internal sealed class ProviderWorkingSetSyncService(
    IProviderWorkingSetReader reader, SqliteCacheStore store, SqliteLocalWorkItemRepository identities)
{
    internal async Task<int> PullAsync(CancellationToken ct)
    {
        var snapshots = await reader.FetchWorkingSetAsync(ct).ConfigureAwait(false);
        var scope = await reader.ResolveResourceScopeAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<ExternalWorkItemIdentity>();
        foreach (var snapshot in snapshots)
            if (snapshot.Identity.ProviderKind != reader.Capabilities.ProviderKind
                || snapshot.Identity.ResourceScope != scope || !seen.Add(snapshot.Identity)
                || !snapshot.RelationshipsComplete
                || snapshot.Relationships.Any(edge =>
                    edge.Source.ProviderKind != snapshot.Identity.ProviderKind || edge.Source.ResourceScope != scope
                    || edge.Target.ProviderKind != snapshot.Identity.ProviderKind || edge.Target.ResourceScope != scope))
                throw new InvalidDataException("provider-working-set-invalid: incomplete, duplicate or out-of-scope observation.");

        var unit = new SqliteUnitOfWork(store);
        await using var transaction = await unit.BeginAsync(ct).ConfigureAwait(false);
        foreach (var snapshot in snapshots)
        {
            await identities.ObserveAsync(snapshot.Identity, snapshot.RevisionToken, ct).ConfigureAwait(false);
            using var command = store.GetConnection().CreateCommand();
            command.Transaction = store.ActiveTransaction;
            command.CommandText = """
                INSERT INTO provider_work_item_snapshots(provider_kind,resource_scope,native_id,snapshot_json,observed_at)
                VALUES(@kind,@scope,@native,@snapshot,@now)
                ON CONFLICT(provider_kind,resource_scope,native_id) DO UPDATE SET
                    snapshot_json=excluded.snapshot_json,observed_at=excluded.observed_at;
                """;
            command.Parameters.AddWithValue("@kind", snapshot.Identity.ProviderKind);
            command.Parameters.AddWithValue("@scope", snapshot.Identity.ResourceScope);
            command.Parameters.AddWithValue("@native", snapshot.Identity.NativeId);
            command.Parameters.AddWithValue("@snapshot", JsonSerializer.Serialize(snapshot, TwigJsonContext.Default.ProviderWorkItemSnapshot));
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await unit.CommitAsync(transaction, ct).ConfigureAwait(false);
        return snapshots.Count;
    }
}
