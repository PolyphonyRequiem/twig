using System.Diagnostics.CodeAnalysis;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// The closed, provider-neutral relation vocabulary a change proposal's add-link and
/// remove-link operations may name (Feature #1178 AC (b)).
/// </summary>
/// <remarks>
/// <para>
/// These are the proposal's own words (<c>parent</c>, <c>child</c>, <c>related</c>,
/// <c>predecessor</c>, <c>successor</c>), not any provider's link-type names. Only a provider
/// adapter maps them to native relation types, so neutral code never sees a provider spelling.
/// </para>
/// <para>
/// The set is closed: instances exist only as the static members below, and
/// <see cref="TryParse"/> refuses anything else. #1170's canonical relationship identity
/// replaces this vocabulary behind the mutation port later.
/// </para>
/// </remarks>
public sealed class ProposalRelation
{
    private ProposalRelation(string name) => Name = name;

    /// <summary>The source item's parent is the target.</summary>
    public static ProposalRelation Parent { get; } = new("parent");

    /// <summary>The target is a child of the source item.</summary>
    public static ProposalRelation Child { get; } = new("child");

    /// <summary>A symmetric, non-hierarchical relation.</summary>
    public static ProposalRelation Related { get; } = new("related");

    /// <summary>The target must complete before the source item.</summary>
    public static ProposalRelation Predecessor { get; } = new("predecessor");

    /// <summary>The source item must complete before the target.</summary>
    public static ProposalRelation Successor { get; } = new("successor");

    /// <summary>Every member of the closed vocabulary.</summary>
    public static IReadOnlyList<ProposalRelation> All { get; } = [Parent, Child, Related, Predecessor, Successor];

    /// <summary>The proposal's lowercase spelling of this relation.</summary>
    public string Name { get; }

    /// <summary>
    /// Resolves a proposal spelling, case-insensitively, to a member of the closed set.
    /// </summary>
    /// <returns><see langword="false"/> for a null, blank or unknown spelling.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ProposalRelation? relation)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                relation = candidate;
                return true;
            }
        }

        relation = null;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
