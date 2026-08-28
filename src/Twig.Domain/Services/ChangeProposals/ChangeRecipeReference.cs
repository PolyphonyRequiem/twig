namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// Points a rendered <see cref="ChangeProposal"/> back at the Change Recipe that produced
/// it, so a reviewer can inspect the template rather than reverse-engineering it from the
/// operations.
/// <para>
/// A <c>null</c> reference on a proposal means the proposal is <em>ad hoc</em> — hand
/// authored, with no template to inspect. That distinction is one a reviewer must be able
/// to make, so absence is meaningful and is never synthesised into a placeholder.
/// </para>
/// </summary>
public sealed record ChangeRecipeReference
{
    /// <summary>Stable identifier of the recipe.</summary>
    public required string RecipeId { get; init; }

    /// <summary>
    /// The recipe's version at render time. Two renderings by different versions of the
    /// same recipe are not interchangeable, so the version travels with the reference.
    /// </summary>
    public required int Version { get; init; }
}
