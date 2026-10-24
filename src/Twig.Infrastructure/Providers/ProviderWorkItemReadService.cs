using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Infrastructure.Providers;

/// <summary>
/// Resolves provider snapshots through durable local identities without inventing provider
/// revisions or changing the legacy integer-keyed mirror.
/// </summary>
internal sealed class ProviderWorkItemReadService(
    IWorkItemSnapshotReader snapshotReader,
    IExternalIdentityMap identities,
    ILocalWorkItemStore localStore) : IProviderWorkItemReadService
{
    public async Task<ProviderWorkItemReadResult> ReadByIdentityAsync(
        ExternalWorkItemIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var scope = await GetActiveScopeAsync(ct);
        EnsureActiveIdentity(identity, scope);

        var snapshot = await snapshotReader.FetchSnapshotAsync(identity, ct);
        if (snapshot.Identity != identity)
            throw new InvalidOperationException(
                $"provider-identity-mismatch: requested '{identity.ProviderKind}/{identity.ResourceScope}/{identity.NativeId}' " +
                $"but the provider returned '{snapshot.Identity.ProviderKind}/{snapshot.Identity.ResourceScope}/{snapshot.Identity.NativeId}'.");

        var resolution = await identities.ResolveAsync(identity, snapshot.RevisionToken, ct);
        if (resolution.Id.IsEmpty)
            throw new InvalidOperationException("provider-local-identity-invalid: the identity map returned an empty local identity.");

        if (!resolution.Created && snapshot.RevisionToken is not null
            && !await identities.RecordObservedTokenAsync(identity, snapshot.RevisionToken, ct))
            throw new InvalidOperationException("provider-identity-map-lost: the mapped identity disappeared while recording its observed token.");

        var local = await localStore.GetAsync(resolution.Id, ct)
            ?? throw new InvalidOperationException("provider-local-record-missing: the identity map points to no durable local record.");

        return Compose(snapshot, local);
    }

    public async Task<ProviderWorkItemReadResult?> ReadByLocalIdAsync(
        LocalWorkItemId id,
        CancellationToken ct = default)
    {
        if (id.IsEmpty)
            throw new ArgumentException("A local work item identity must not be empty.", nameof(id));

        var local = await localStore.GetAsync(id, ct);
        if (local is null)
            return null;

        var scope = await GetActiveScopeAsync(ct);
        var matches = (await identities.GetExternalIdentitiesAsync(id, ct))
            .Where(identity => string.Equals(
                    identity.ProviderKind, snapshotReader.Capabilities.ProviderKind, StringComparison.Ordinal)
                && string.Equals(identity.ResourceScope, scope, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
            return null;
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"provider-identity-ambiguous: local work item {id} has {matches.Length} identities in the active provider scope.");

        var result = await ReadByIdentityAsync(matches[0], ct);
        if (result.Local.Id != id)
            throw new InvalidOperationException(
                $"provider-identity-map-changed: the selected provider identity no longer maps to local work item {id}.");
        return result;
    }

    private async Task<string> GetActiveScopeAsync(CancellationToken ct)
    {
        var scope = await snapshotReader.ResolveResourceScopeAsync(ct);
        if (string.IsNullOrWhiteSpace(scope))
            throw new InvalidOperationException("provider-scope-unavailable: the active provider did not resolve a stable resource scope.");
        return scope;
    }

    private void EnsureActiveIdentity(ExternalWorkItemIdentity identity, string scope)
    {
        if (!string.Equals(identity.ProviderKind, snapshotReader.Capabilities.ProviderKind, StringComparison.Ordinal)
            || !string.Equals(identity.ResourceScope, scope, StringComparison.Ordinal))
            throw new ArgumentException(
                "provider-identity-scope-mismatch: the identity does not belong to the active provider and resource scope.",
                nameof(identity));
    }

    private static ProviderWorkItemReadResult Compose(
        ProviderWorkItemSnapshot snapshot,
        LocalWorkItemRecord local)
    {
        var fields = snapshot.Fields;
        foreach (var (referenceName, value) in local.Fields)
            fields = fields.Set(referenceName, value);

        return new ProviderWorkItemReadResult(
            snapshot.Identity,
            local,
            snapshot.RevisionToken,
            snapshot.Fields,
            fields,
            snapshot.Relationships);
    }
}
