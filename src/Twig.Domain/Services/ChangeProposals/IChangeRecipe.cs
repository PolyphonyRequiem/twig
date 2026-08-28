using Twig.Domain.Services.Plan;

namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// A reusable, parameterized template that renders one or more Change Proposals from
/// concrete inputs.
/// <para>
/// <b>A recipe cannot apply.</b> <see cref="Render"/> is the only member on this interface,
/// and it returns declarative documents. There is deliberately no apply, execute, submit,
/// or mutate member — so "invoking a template cannot mutate ADO" is a property of the
/// contract itself, not a runtime check a future implementation could bypass. A caller that
/// wants to apply a rendered proposal must take it through the ordinary
/// validate → preview → authorize → apply lifecycle, which is where the digest gate and the
/// authorization record live.
/// </para>
/// <para>
/// <b>Rendering identity.</b> An implementation MUST be a pure function of its inputs and
/// the observed state it was constructed with: rendering the same inputs against the same
/// observed state must produce the same documents, and therefore the same digests. Reading
/// a clock, a random source, or live mutable state inside <see cref="Render"/> breaks that
/// and is a defect.
/// </para>
/// <para>
/// Input schema, versioning, eligibility declarations, rendering-time diagnostics,
/// per-operation authorization annotations, and any zero-render/no-op contract are
/// deliberately out of scope here and belong to the follow-on recipe-authoring spec.
/// </para>
/// </summary>
public interface IChangeRecipe
{
    /// <summary>Stable identifier for this recipe.</summary>
    string RecipeId { get; }

    /// <summary>
    /// This recipe's version. Travels onto every proposal it renders so a reviewer can tell
    /// which template revision produced the document in front of them.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Renders one or more Plan v1 documents from <paramref name="inputs"/>. The only
    /// surface a recipe exposes.
    /// <para>
    /// Implementations MUST fail loudly on missing or invalid input — throw
    /// <see cref="ChangeRecipeInputException"/> (most simply by using
    /// <see cref="ChangeRecipeInputs.Require"/>) rather than substituting a default,
    /// skipping an operation, or returning an empty list. A silently-dropped operation is
    /// precisely the failure this vocabulary exists to prevent.
    /// </para>
    /// </summary>
    /// <exception cref="ChangeRecipeInputException">
    /// An input the recipe requires was missing, blank, or not usable.
    /// </exception>
    IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs);
}
