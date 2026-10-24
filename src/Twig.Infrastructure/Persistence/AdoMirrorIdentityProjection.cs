using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Projects every ADO work item written to the disposable mirror onto a durable
/// <see cref="LocalWorkItemId"/> (Epic #1168), recording the observed ADO revision as the opaque
/// <see cref="ProviderRevisionToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mapping lives in the durable file, so mirror eviction, deletion and rebuild never change an
/// item's local identity: the next save re-observes the same id.
/// </para>
/// <para>
/// Seeds are skipped: they have no ADO identity until published. The ADO adapter supplies and
/// normalizes the resource scope (Epic #1167).
/// </para>
/// </remarks>
internal sealed class AdoMirrorIdentityProjection
{
    public const string ProviderKind = "ado";

    private readonly SqliteLocalWorkItemRepository _identities;
    private readonly Func<IWorkItemProvider>? _providerFactory;
    private readonly string? _resourceScope;

    public AdoMirrorIdentityProjection(SqliteLocalWorkItemRepository identities, string resourceScope)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ValidateScope(resourceScope);
        _identities = identities;
        _resourceScope = resourceScope;
    }

    public AdoMirrorIdentityProjection(
        SqliteLocalWorkItemRepository identities,
        Func<IWorkItemProvider> providerFactory)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(providerFactory);
        _identities = identities;
        _providerFactory = providerFactory;
    }

    public bool ShouldProject(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !item.IsSeed && item.Id > 0;
    }

    public async Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
    {
        if (_resourceScope is not null)
            return _resourceScope;

        var provider = (_providerFactory
            ?? throw new InvalidOperationException("The ADO provider factory was not configured."))();
        if (provider.Capabilities.ProviderKind != ProviderKind)
            throw new InvalidOperationException(
                $"ado-provider-required: durable ADO mirror projection cannot use provider '{provider.Capabilities.ProviderKind}'.");

        var scope = await provider.ResolveResourceScopeAsync(ct).ConfigureAwait(false);
        if (scope is not null)
            ValidateScope(scope);
        return scope;
    }

    public async Task<string?> PrepareAsync(IReadOnlyCollection<WorkItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();

        var projectable = items.Where(ShouldProject).ToArray();
        foreach (var item in projectable)
        {
            if (item.Revision <= 0)
                throw new InvalidOperationException(
                    $"ado-revision-unavailable: work item {item.Id.ToString(CultureInfo.InvariantCulture)} cannot be projected without a positive provider revision.");
        }

        if (projectable.Length == 0)
            return null;

        var scope = await ResolveResourceScopeAsync(ct).ConfigureAwait(false);
        if (scope is null)
            throw new InvalidOperationException(
                "ado-resource-scope-unavailable: the provider did not resolve a durable ADO resource scope.");

        return scope;
    }

    public async Task ProjectAsync(WorkItem item, string resourceScope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ShouldProject(item))
            return;
        if (item.Revision <= 0)
            throw new InvalidOperationException(
                $"ado-revision-unavailable: work item {item.Id.ToString(CultureInfo.InvariantCulture)} cannot be projected without a positive provider revision.");

        var identity = new ExternalWorkItemIdentity(
            ProviderKind, resourceScope, item.Id.ToString(CultureInfo.InvariantCulture));
        var token = new ProviderRevisionToken(item.Revision.ToString(CultureInfo.InvariantCulture));
        await _identities.ObserveAsync(identity, token, ct).ConfigureAwait(false);
    }

    private static void ValidateScope(string resourceScope)
    {
        // Validate with the identity contract's own rules rather than restating them.
        _ = new ExternalWorkItemIdentity(ProviderKind, resourceScope, "0");
    }
}
