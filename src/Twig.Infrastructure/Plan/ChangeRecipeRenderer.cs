using System.Collections.ObjectModel;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Renders a <see cref="IChangeRecipe"/> against concrete inputs into immutable, digest-bound
/// <see cref="ChangeProposal"/> values.
/// <para>
/// <b>Why this routes through the parser.</b> The renderer serializes each document the recipe
/// produced and feeds it straight back through <see cref="PlanDocumentParser"/>. That is what
/// guarantees the acceptance rule "two renderings from identical inputs produce identical
/// digests", and the stronger one behind it: the digest a proposal carries at render time is
/// the same digest the identical document earns at preview and at apply-time journal lookup.
/// A second, parallel digest implementation here would be free to drift from the one the
/// apply gate actually checks, and the failure would only ever show up as a refused apply.
/// </para>
/// <para>
/// <b>The digest invariant is preserved.</b> Nothing this class does consults an adapter, ADO,
/// or any server-supplied value. The digest is computed from the rendered document alone,
/// before any call could occur. Anything learned later is journal data, never digest input.
/// </para>
/// <para>
/// <b>This class cannot apply anything</b>, and neither can <see cref="IChangeRecipe"/>. A
/// rendered proposal has to be taken through the ordinary lifecycle, where the digest gate and
/// the authorization record live.
/// </para>
/// </summary>
public sealed class ChangeRecipeRenderer(PlanDocumentParser parser)
{
    private readonly PlanDocumentParser _parser = parser
        ?? throw new ArgumentNullException(nameof(parser));

    /// <summary>
    /// Renders <paramref name="recipe"/> against <paramref name="inputs"/>, returning one
    /// proposal per document the recipe produced, in the order it produced them.
    /// </summary>
    /// <param name="recipe">The template to render.</param>
    /// <param name="inputs">Concrete inputs. Missing or invalid values fail loudly.</param>
    /// <param name="rationale">
    /// Optional author rationale carried onto every proposal from this rendering. Never part
    /// of the digest.
    /// </param>
    /// <exception cref="ChangeRecipeInputException">
    /// An input the recipe requires was missing or unusable. Propagated from the recipe
    /// unchanged — the caller learns exactly which input failed.
    /// </exception>
    /// <exception cref="ChangeRecipeRenderException">
    /// The recipe produced no documents, or produced one that is not a valid Plan v1
    /// document.
    /// </exception>
    public IReadOnlyList<ChangeProposal> Render(
        IChangeRecipe recipe,
        ChangeRecipeInputs inputs,
        string? rationale = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(inputs);

        // Input failures surface as ChangeRecipeInputException from the recipe itself; they
        // are deliberately not caught and converted here, because the input's name is the
        // most useful thing the caller can be told and wrapping would bury it.
        var documents = recipe.Render(inputs);

        if (documents is null || documents.Count == 0)
        {
            // The contract is "one or more proposals". Whether a recipe may legitimately
            // render zero for a no-op input — and how that reason would be reported — is
            // deferred to the follow-on recipe-authoring spec, so there is no vocabulary here
            // for expressing an intentional empty render. Refusing is the honest reading:
            // returning an empty list would be indistinguishable from a recipe that silently
            // dropped every operation.
            throw new ChangeRecipeRenderException(
                $"Change Recipe '{recipe.RecipeId}' rendered no proposals. A recipe must render at least one.");
        }

        var reference = new ChangeRecipeReference { RecipeId = recipe.RecipeId, Version = recipe.Version };
        var proposals = new List<ChangeProposal>(documents.Count);

        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i]
                ?? throw new ChangeRecipeRenderException(
                    $"Change Recipe '{recipe.RecipeId}' rendered a null document at index {i}.");

            var json = PlanDocumentWriter.Write(document);
            var parsed = _parser.Parse(json);

            if (!parsed.IsValid || parsed.Plan is null || parsed.CanonicalJson is null || parsed.Digest is null)
            {
                var detail = parsed.Issues.Count == 0
                    ? "no issues were reported, which itself indicates a defect in the renderer"
                    : string.Join("; ", parsed.Issues.Select(issue => $"{issue.Code} at {Describe(issue.Path)}: {issue.Message}"));

                throw new ChangeRecipeRenderException(
                    $"Change Recipe '{recipe.RecipeId}' rendered an invalid Plan v1 document at index {i}: {detail}.");
            }

            proposals.Add(new ChangeProposal
            {
                // Wrap the operation list before it is sealed into the proposal. The parser
                // hands back a mutable list behind an IReadOnlyList reference, so a caller who
                // downcasts could still edit a "reviewed" proposal's operations in place. The
                // digest would not move with them — which is the one mutation that could make
                // a proposal apply something other than what its digest attested to.
                Definition = parsed.Plan with
                {
                    Operations = new ReadOnlyCollection<PlanOperationDefinition>(parsed.Plan.Operations.ToArray()),
                },
                CanonicalJson = parsed.CanonicalJson,
                Digest = parsed.Digest,
                Recipe = reference,
                Rationale = rationale,
            });
        }

        return proposals;
    }

    private static string Describe(string path) => string.IsNullOrEmpty(path) ? "<document>" : path;
}
