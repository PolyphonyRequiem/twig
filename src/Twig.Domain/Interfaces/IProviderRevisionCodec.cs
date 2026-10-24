using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Converts the cached integer revision carried by today's work item cache, proposals and
/// journal into the selected provider's opaque <see cref="ProviderRevisionPrecondition"/>, and
/// the provider's returned token back (Feature #1178).
/// </summary>
/// <remarks>
/// <para>
/// This is a compatibility boundary, not neutral logic: it exists because the cache,
/// proposal digests and journal remain integer-shaped until the neutral store carries opaque
/// tokens (#1168, #1183). Only the provider adapter implements it, so the integer encoding
/// never leaks into neutral code. Composition resolves it from the workspace's selected
/// provider and refuses providers with no writer as <c>provider-unavailable</c>.
/// </para>
/// </remarks>
public interface IProviderRevisionCodec
{
    /// <summary>The provider kind whose preconditions this codec mints.</summary>
    string ProviderKind { get; }

    /// <summary>
    /// Builds the precondition for a cached revision. A revision of zero or below means no
    /// authoritative revision is known and is refused as
    /// <see cref="ProviderRevisionPreconditionRefusal.Missing"/>; it is never sent.
    /// </summary>
    /// <exception cref="ProviderRevisionPreconditionRefusedException">The revision is not positive.</exception>
    ProviderRevisionPrecondition FromCacheRevision(int revision);

    /// <summary>Decodes a token this provider returned after a write into the cached integer revision.</summary>
    /// <remarks>
    /// This runs after the write reached the provider, so a failure here is deliberately
    /// <b>not</b> a <see cref="ProviderRevisionPreconditionRefusedException"/>: that exception
    /// promises nothing was sent, and callers classify it as a determinate refusal.
    /// </remarks>
    /// <exception cref="FormatException">The token is missing or not canonical for this provider.</exception>
    int ToCacheRevision(ProviderRevisionToken revision);
}
