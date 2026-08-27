using Shouldly;
using System.Reflection;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Behavioural tests for the Change Recipe rendering seam (AB#742, Spec #729).
/// <para>
/// These exercise the external contract only — render a recipe, inspect the proposals that
/// come back. Nothing here asserts private renderer structure, file layout, or class names.
/// </para>
/// </summary>
public sealed class ChangeRecipeRendererTests
{
    private static readonly PlanWorkspace Workspace = new() { Organization = "acme", Project = "cache" };

    private static ChangeRecipeRenderer BuildRenderer() => new(new PlanDocumentParser());

    // ── rendering identity ────────────────────────────────────────────────

    [Fact]
    public void Render_WithIdenticalInputs_ProducesIdenticalDigests()
    {
        // Defends against: a renderer that lets incidental ordering, a clock, a GUID, or any
        // other non-input state leak into the document. If it did, the same recipe rendered
        // twice would produce two digests, and an authorization bound to the first would be
        // refused when the second was applied — for no reason a reviewer could see.
        var renderer = BuildRenderer();
        var recipe = new SetStateRecipe();
        var inputs = new ChangeRecipeInputs(new Dictionary<string, string>
        {
            ["workItemId"] = "742",
            ["expectedRevision"] = "4",
            ["state"] = "Doing",
        });

        var first = renderer.Render(recipe, inputs);
        var second = renderer.Render(recipe, inputs);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        second[0].Digest.ShouldBe(first[0].Digest);
        second[0].CanonicalJson.ShouldBe(first[0].CanonicalJson);
    }

    [Fact]
    public void Render_WithDifferentInputs_ProducesDifferentDigests()
    {
        // Defends against: a digest that does not actually cover the operation payload. A
        // digest that ignored the staged value would let a reviewer authorize "Doing" and an
        // applier write "Done" under the same, still-matching digest.
        var renderer = BuildRenderer();
        var recipe = new SetStateRecipe();

        var doing = renderer.Render(recipe, Inputs(state: "Doing"))[0];
        var done = renderer.Render(recipe, Inputs(state: "Done"))[0];

        done.Digest.ShouldNotBe(doing.Digest);
    }

    [Fact]
    public void Render_ProducesOneProposalPerDocumentInOrder()
    {
        // Defends against: a renderer that collapses a multi-document render into one
        // proposal, or reorders them. Declared order is execution order, so a reordered
        // render is a different mutation.
        var proposals = BuildRenderer().Render(new TwoDocumentRecipe(), new ChangeRecipeInputs());

        proposals.Count.ShouldBe(2);
        FirstBatch(proposals[0]).WorkItemId.ShouldBe(1);
        FirstBatch(proposals[1]).WorkItemId.ShouldBe(2);
    }

    // ── digest identity with the validate/preview/apply path ──────────────

    [Fact]
    public void Render_Digest_EqualsTheDigestTheSameDocumentEarnsFromTheParser()
    {
        // Defends against: the renderer growing its own canonicalization. The apply gate
        // recomputes the digest with the parser; if rendering used a second implementation,
        // every rendered proposal would be refused at apply time with a digest mismatch, and
        // the mismatch would look like file tampering rather than a code defect.
        var proposal = BuildRenderer().Render(new SetStateRecipe(), Inputs())[0];

        var reparsed = new PlanDocumentParser().Parse(proposal.CanonicalJson);

        reparsed.IsValid.ShouldBeTrue();
        reparsed.Digest.ShouldBe(proposal.Digest);
        reparsed.CanonicalJson.ShouldBe(proposal.CanonicalJson);
    }

    [Fact]
    public void Render_Digest_IsSixtyFourLowercaseHexCharactersWithNoPrefix()
    {
        // Defends against: drifting onto the `sha256:`-prefixed spelling used for seed
        // fingerprints. The two hashes serve different purposes and confusing them would make
        // a confirmed digest never match.
        var digest = BuildRenderer().Render(new SetStateRecipe(), Inputs())[0].Digest;

        digest.Length.ShouldBe(64);
        digest.ShouldBe(digest.ToLowerInvariant());
        digest.ShouldNotStartWith("sha256:");
        digest.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    // ── missing / invalid input fails loudly ──────────────────────────────

    [Fact]
    public void Render_WithMissingInput_ThrowsNamingTheInput()
    {
        // Defends against: a recipe silently substituting a default for an input the caller
        // forgot. That produces a perfectly valid, perfectly wrong proposal — the most
        // dangerous possible outcome, because it still presents a clean digest to authorize.
        var inputs = new ChangeRecipeInputs(new Dictionary<string, string>
        {
            ["workItemId"] = "742",
            ["expectedRevision"] = "4",
            // "state" deliberately absent
        });

        var ex = Should.Throw<ChangeRecipeInputException>(
            () => BuildRenderer().Render(new SetStateRecipe(), inputs));

        ex.InputName.ShouldBe("state");
        ex.Message.ShouldContain("state");
    }

    [Fact]
    public void Render_WithBlankInput_ThrowsRatherThanStagingAnEmptyValue()
    {
        // Defends against: whitespace passing the "is it present?" check and being staged as
        // a field value, which would clear or corrupt the field on the board.
        var ex = Should.Throw<ChangeRecipeInputException>(
            () => BuildRenderer().Render(new SetStateRecipe(), Inputs(state: "   ")));

        ex.InputName.ShouldBe("state");
    }

    [Fact]
    public void Render_WithNonNumericIntegerInput_ThrowsRatherThanCoercing()
    {
        // Defends against: coercing an unparseable id to 0 (or default) and producing a
        // proposal that targets the wrong item, or no item at all.
        var inputs = new ChangeRecipeInputs(new Dictionary<string, string>
        {
            ["workItemId"] = "not-a-number",
            ["expectedRevision"] = "4",
            ["state"] = "Doing",
        });

        Should.Throw<ChangeRecipeInputException>(
            () => BuildRenderer().Render(new SetStateRecipe(), inputs));
    }

    [Fact]
    public void Render_WhenRecipeProducesAnInvalidDocument_FailsLoudly()
    {
        // Defends against: a defective recipe emitting a document the parser rejects and the
        // renderer handing back a half-built proposal anyway. A proposal that cannot be
        // validated must never reach a reviewer.
        var ex = Should.Throw<ChangeRecipeRenderException>(
            () => BuildRenderer().Render(new EmptyOperationsRecipe(), new ChangeRecipeInputs()));

        ex.Message.ShouldContain("invalid");
    }

    [Fact]
    public void Render_WhenRecipeProducesNothing_FailsLoudly()
    {
        // Defends against: an empty render being indistinguishable from a recipe that
        // silently dropped every operation. The contract is "one or more proposals"; the
        // intentional no-op case is a deferred seam with no vocabulary yet.
        Should.Throw<ChangeRecipeRenderException>(
            () => BuildRenderer().Render(new NoDocumentRecipe(), new ChangeRecipeInputs()));
    }

    // ── a recipe cannot apply ─────────────────────────────────────────────

    [Fact]
    public void ChangeRecipe_ExposesRenderingAsItsOnlyOperation()
    {
        // Defends against: someone adding an Apply/Execute/Submit member to the recipe
        // contract later. The "a template cannot mutate ADO" guarantee is the SHAPE of this
        // interface — there is no runtime guard behind it, so if a mutating member ever
        // appears the guarantee is simply gone. This test is the guard.
        var members = typeof(IChangeRecipe)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        members.ShouldContain(nameof(IChangeRecipe.Render));

        foreach (var forbidden in new[] { "Apply", "Execute", "Submit", "Mutate", "Commit", "Save" })
            members.ShouldNotContain(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RenderedProposal_ExposesNoMemberThatMutatesItsSemanticContent()
    {
        // Defends against: a settable Definition/CanonicalJson/Digest appearing on the
        // proposal. Immutability after rendering is what makes a digest mean anything — a
        // proposal that could be edited after review would be reviewed as one thing and
        // applied as another.
        var settable = typeof(ChangeProposal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } setter
                && setter.IsPublic
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            .Select(p => p.Name)
            .ToList();

        settable.ShouldBeEmpty();

        // And the operation list is handed out as a read-only view, not a mutable list.
        var proposal = BuildRenderer().Render(new SetStateRecipe(), Inputs())[0];
        proposal.Definition.Operations.ShouldBeAssignableTo<IReadOnlyList<PlanOperationDefinition>>();
        (proposal.Definition.Operations as System.Collections.IList)?.IsReadOnly.ShouldBeTrue();
    }

    // ── recipe provenance ─────────────────────────────────────────────────

    [Fact]
    public void RenderedProposal_CarriesTheRecipeItCameFrom()
    {
        // Defends against: losing the link back to the template, which leaves a reviewer
        // unable to tell a rendered proposal from a hand-authored one or to inspect the
        // recipe that produced it.
        var proposal = BuildRenderer().Render(new SetStateRecipe(), Inputs(), rationale: "claiming 742")[0];

        proposal.Recipe.ShouldNotBeNull();
        proposal.Recipe!.RecipeId.ShouldBe(SetStateRecipe.Id);
        proposal.Recipe.Version.ShouldBe(3);
        proposal.Rationale.ShouldBe("claiming 742");
    }

    [Fact]
    public void Rationale_DoesNotChangeTheDigest()
    {
        // Defends against: rationale leaking into the canonical form. Rationale is authorship
        // context, not semantic content; if it were hashed, re-wording a note would invalidate
        // an otherwise-valid authorization.
        var renderer = BuildRenderer();

        var withNote = renderer.Render(new SetStateRecipe(), Inputs(), rationale: "because")[0];
        var withoutNote = renderer.Render(new SetStateRecipe(), Inputs())[0];

        withNote.Digest.ShouldBe(withoutNote.Digest);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ChangeRecipeInputs Inputs(string state = "Doing") =>
        new(new Dictionary<string, string>
        {
            ["workItemId"] = "742",
            ["expectedRevision"] = "4",
            ["state"] = state,
        });

    private static BatchOperation FirstBatch(ChangeProposal proposal) =>
        proposal.Definition.Operations.OfType<BatchOperation>().First();

    /// <summary>Renders a single batch that moves one item's state.</summary>
    private sealed class SetStateRecipe : IChangeRecipe
    {
        public const string Id = "twig.test.set-state";

        public string RecipeId => Id;

        public int Version => 3;

        public IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs) =>
        [
            new PlanDefinition
            {
                Version = 1,
                Workspace = Workspace,
                Operations =
                [
                    new BatchOperation
                    {
                        Id = "op-1",
                        WorkItemId = inputs.RequireInt("workItemId"),
                        ExpectedRevision = inputs.RequireInt("expectedRevision"),
                        Fields = new Dictionary<string, string?> { ["System.State"] = inputs.Require("state") },
                    },
                ],
            },
        ];
    }

    private sealed class TwoDocumentRecipe : IChangeRecipe
    {
        public string RecipeId => "twig.test.two-documents";

        public int Version => 1;

        public IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs) =>
        [
            Document(1),
            Document(2),
        ];

        private static PlanDefinition Document(int workItemId) => new()
        {
            Version = 1,
            Workspace = Workspace,
            Operations =
            [
                new BatchOperation
                {
                    Id = $"op-{workItemId}",
                    WorkItemId = workItemId,
                    ExpectedRevision = 1,
                    Fields = new Dictionary<string, string?> { ["System.State"] = "Doing" },
                },
            ],
        };
    }

    private sealed class EmptyOperationsRecipe : IChangeRecipe
    {
        public string RecipeId => "twig.test.empty-operations";

        public int Version => 1;

        public IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs) =>
        [
            new PlanDefinition { Version = 1, Workspace = Workspace, Operations = [] },
        ];
    }

    private sealed class NoDocumentRecipe : IChangeRecipe
    {
        public string RecipeId => "twig.test.no-documents";

        public int Version => 1;

        public IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs) => [];
    }
}
