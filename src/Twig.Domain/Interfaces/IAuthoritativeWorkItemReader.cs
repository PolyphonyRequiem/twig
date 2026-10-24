using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Authoritative pre-operation snapshot read (Feature #1177, consumed by #1169 / #1178).
/// Reads the server's state of one work item AT an expected opaque revision, bypassing the
/// local cache, so a planned mutation can be validated against exactly what it was planned on.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the provider-qualified <see cref="ExternalWorkItemIdentity"/> and an opaque
/// <see cref="ProviderRevisionToken"/>, never by a bare integer id or integer revision: the
/// caller must not need to know how a provider spells either. Each adapter owns its encoding
/// and refuses identities it does not own.
/// </para>
/// <para>
/// Fails closed. A foreign provider kind, a scope other than the provider's resolved resource
/// scope, an unresolvable scope, a malformed id or token, or a server answer at any other
/// revision throws rather than returning a snapshot the caller might trust.
/// </para>
/// </remarks>
internal interface IAuthoritativeWorkItemReader
{
    Task<AuthoritativeWorkItemSnapshot> ReadAtRevisionAsync(
        ExternalWorkItemIdentity identity,
        ProviderRevisionToken expectedRevision,
        CancellationToken ct = default);
}

/// <summary>
/// The server's state of <paramref name="Identity"/> at <paramref name="Revision"/>. The
/// <paramref name="Item"/> projection is the cache-shaped snapshot the rule gate consumes.
/// </summary>
internal sealed record AuthoritativeWorkItemSnapshot(
    ExternalWorkItemIdentity Identity,
    ProviderRevisionToken Revision,
    WorkItemSnapshot Item);
