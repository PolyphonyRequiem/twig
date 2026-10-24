using System.Globalization;
using Twig.Domain.Aggregates;
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
/// Seeds are skipped: they have no ADO identity until published. The resource scope is supplied by
/// the ADO adapter (Epic #1167, <c>IWorkItemProvider.ResolveResourceScopeAsync</c>) and never derived
/// here. It is resolved lazily, on the first save, and the outcome is kept for this instance's
/// lifetime. A null or blank scope refuses projection; a failed resolution surfaces and is retried
/// on the next save.
/// </para>
/// </remarks>
internal sealed class AdoMirrorIdentityProjection
{
    public const string ProviderKind = "ado";

    private readonly SqliteLocalWorkItemRepository _identities;
    private readonly Func<CancellationToken, Task<string?>> _resolveScope;
    private string? _scope;
    private bool _resolved;

    public AdoMirrorIdentityProjection(
        SqliteLocalWorkItemRepository identities, Func<CancellationToken, Task<string?>> resolveScope)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(resolveScope);
        _identities = identities;
        _resolveScope = resolveScope;
    }

    /// <summary>Projects with an already-known scope, validated immediately.</summary>
    public AdoMirrorIdentityProjection(SqliteLocalWorkItemRepository identities, string resourceScope)
        : this(identities, _ => Task.FromResult<string?>(resourceScope))
    {
        Validate(resourceScope);
    }

    /// <summary>
    /// Returns the ADO resource scope, or null when the adapter cannot resolve one, in which case
    /// projection is refused. Callers resolve before opening a transaction so no provider round
    /// trip happens while the store is locked.
    /// </summary>
    public async Task<string?> ResolveScopeAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _resolved))
            return _scope;

        var scope = await _resolveScope(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(scope))
            scope = null;
        else
            Validate(scope);

        _scope = scope;
        Volatile.Write(ref _resolved, true);
        return scope;
    }

    // Validates the scope with the identity contract's own rules rather than restating them.
    private static void Validate(string scope) => _ = new ExternalWorkItemIdentity(ProviderKind, scope, "0");

    public async Task ProjectAsync(WorkItem item, string resourceScope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSeed || item.Id <= 0)
            return;

        var identity = new ExternalWorkItemIdentity(
            ProviderKind, resourceScope, item.Id.ToString(CultureInfo.InvariantCulture));
        var token = item.Revision > 0
            ? new ProviderRevisionToken(item.Revision.ToString(CultureInfo.InvariantCulture))
            : null;
        await _identities.ObserveAsync(identity, token, ct);
    }
}
