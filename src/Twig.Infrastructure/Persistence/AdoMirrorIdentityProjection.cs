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
/// the caller and never derived here; the ADO adapter owns its normalization (Epic #1167).
/// </para>
/// </remarks>
internal sealed class AdoMirrorIdentityProjection
{
    public const string ProviderKind = "ado";

    private readonly SqliteLocalWorkItemRepository _identities;
    private readonly string _resourceScope;

    public AdoMirrorIdentityProjection(SqliteLocalWorkItemRepository identities, string resourceScope)
    {
        ArgumentNullException.ThrowIfNull(identities);
        // Validates the scope with the identity contract's own rules rather than restating them.
        _ = new ExternalWorkItemIdentity(ProviderKind, resourceScope, "0");
        _identities = identities;
        _resourceScope = resourceScope;
    }

    public async Task ProjectAsync(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSeed || item.Id <= 0)
            return;

        var identity = new ExternalWorkItemIdentity(
            ProviderKind, _resourceScope, item.Id.ToString(CultureInfo.InvariantCulture));
        var token = item.Revision > 0
            ? new ProviderRevisionToken(item.Revision.ToString(CultureInfo.InvariantCulture))
            : null;
        await _identities.ObserveAsync(identity, token, ct);
    }
}
