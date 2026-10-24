using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// A canonical work-item relationship between two provider-qualified identities.
/// </summary>
/// <remarks>
/// <see cref="LinkKind.ParentChild"/> is oriented parent to child;
/// <see cref="LinkKind.PredecessorSuccessor"/> is oriented predecessor to successor.
/// <see cref="LinkKind.Related"/> is symmetric and its endpoints are ordered ordinally for
/// uniqueness. <see cref="LinkKind.Artifact"/> is not a work-item relationship. Provider
/// relation names and provenance remain adapter-owned.
/// </remarks>
public sealed record ProviderWorkItemRelationship
{
    public ProviderWorkItemRelationship(ExternalWorkItemIdentity source, ExternalWorkItemIdentity target, LinkKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source == target)
            throw new ArgumentException("A work item cannot have a relationship to itself.", nameof(target));

        if (kind is not (LinkKind.ParentChild or LinkKind.PredecessorSuccessor or LinkKind.Related))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported canonical work-item relationship kind.");

        if (kind == LinkKind.Related && Compare(source, target) > 0)
            (source, target) = (target, source);

        Source = source;
        Target = target;
        Kind = kind;
    }

    /// <summary>The source endpoint, with direction determined by <see cref="Kind"/>.</summary>
    public ExternalWorkItemIdentity Source { get; }

    /// <summary>The target endpoint, with direction determined by <see cref="Kind"/>.</summary>
    public ExternalWorkItemIdentity Target { get; }

    /// <summary>
    /// The canonical semantic kind. Use <see cref="LinkKindNames.ToStableName"/> at persisted
    /// and serialized boundaries; enum ordinals are not stable identifiers.
    /// </summary>
    public LinkKind Kind { get; }

    private static int Compare(ExternalWorkItemIdentity left, ExternalWorkItemIdentity right)
    {
        var provider = string.Compare(left.ProviderKind, right.ProviderKind, StringComparison.Ordinal);
        if (provider != 0) return provider;

        var scope = string.Compare(left.ResourceScope, right.ResourceScope, StringComparison.Ordinal);
        return scope != 0
            ? scope
            : string.Compare(left.NativeId, right.NativeId, StringComparison.Ordinal);
    }
}
