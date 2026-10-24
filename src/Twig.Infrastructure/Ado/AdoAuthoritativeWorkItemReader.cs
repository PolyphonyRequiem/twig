using System.Globalization;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// ADO's encoding of provider-neutral identity and revision tokens (Feature #1177). It matches
/// the neutral store's mirror projection (#1168): the native id and the revision are both the
/// canonical invariant decimal spelling of a positive integer.
/// </summary>
internal static class AdoProviderTokens
{
    public static string EncodeNativeId(int id) => EncodePositive(id, nameof(id));

    public static ProviderRevisionToken EncodeRevision(int revision)
        => new(EncodePositive(revision, nameof(revision)));

    /// <summary>
    /// Accepts only a canonical positive decimal — no sign, padding, separators, whitespace or
    /// leading zero — so exactly one spelling maps to each integer and a token cannot alias.
    /// </summary>
    public static bool TryDecodePositive(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value) || value[0] == '0')
            return false;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return false;

        result = parsed;
        return true;
    }

    private static string EncodePositive(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// ADO adapter for <see cref="IAuthoritativeWorkItemReader"/>: decodes the neutral identity
/// and token, proves the identity belongs to this ADO organisation, then reads the historical
/// revision from the server (never the cache) and verifies the answer is the one requested.
/// </summary>
internal sealed class AdoAuthoritativeWorkItemReader(
    IRevisionBoundAdoWorkItemService revisionBound,
    IWorkItemProvider provider) : IAuthoritativeWorkItemReader
{
    public async Task<AuthoritativeWorkItemSnapshot> ReadAtRevisionAsync(
        ExternalWorkItemIdentity identity,
        ProviderRevisionToken expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expectedRevision);

        if (!string.Equals(identity.ProviderKind, ProviderCapabilityCatalog.AdoProviderKind, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Identity provider '{identity.ProviderKind}' is not owned by the Azure DevOps adapter.",
                nameof(identity));
        if (!AdoProviderTokens.TryDecodePositive(identity.NativeId, out var id))
            throw new ArgumentException("Azure DevOps native id must be a canonical positive decimal.", nameof(identity));
        if (!AdoProviderTokens.TryDecodePositive(expectedRevision.Value, out var revision))
            throw new ArgumentException("Azure DevOps revision token must be a canonical positive decimal.", nameof(expectedRevision));

        var scope = await provider.ResolveResourceScopeAsync(ct).ConfigureAwait(false);
        if (scope is null)
            throw new InvalidOperationException(
                "The Azure DevOps resource scope could not be resolved; refusing an unscoped authoritative read.");
        if (!string.Equals(scope, identity.ResourceScope, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The identity's resource scope is not this workspace's Azure DevOps organisation; refusing the read.");

        var snapshot = await revisionBound.FetchAtRevisionAsync(id, revision, ct).ConfigureAwait(false);
        if (snapshot.Id != id || snapshot.Revision != revision)
            throw new InvalidOperationException(
                $"Azure DevOps answered #{snapshot.Id} rev {snapshot.Revision} for a read of #{id} rev {revision}; refusing a mismatched snapshot.");

        return new AuthoritativeWorkItemSnapshot(identity, expectedRevision, snapshot);
    }
}
