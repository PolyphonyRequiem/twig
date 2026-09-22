using Shouldly;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Twig.RenderTree;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// The guaranteed terminal/text fallback (Spec #729 §Terminal/text fallback, AB#743).
/// <para>
/// These tests defend the presentation boundary: material effects, warnings and blockers
/// remain readable, while approval controls and machine bookkeeping stay structured.
/// </para>
/// </summary>
public sealed class ChangeProposalReviewRendererTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ChangeProposalReviewModel Model(
        IReadOnlyList<ReviewBlocker>? blockers = null) => new()
        {
            Digest = Digest,
            Workspace = new PlanWorkspace { Organization = "acme", Project = "cache" },
            Rationale = "Close out the sprint.",
            AffectedItems =
            [
                new ReviewAffectedItem { Id = 729, Type = "Spec", Title = "Change Recipe", State = "Doing", Role = "target" },
                new ReviewAffectedItem { Id = 742, Role = "peer" },
            ],
            Operations =
            [
                new ReviewOperation
                {
                    Ordinal = 0,
                    OpId = "op-batch",
                    Kind = "batch",
                    Target = new ReviewTarget { WorkItemId = 729 },
                    Summary = "Set state on #729",
                    Preconditions = [new ReviewPrecondition { Kind = "expectedRevision", Value = "7" }],
                    Consequences =
                    [
                        new ReviewConsequence { Kind = "field-set", Field = "System.State", To = "Done" },
                        new ReviewConsequence { Kind = "field-clear", Field = "System.Reason" },
                    ],
                },
                new ReviewOperation
                {
                    Ordinal = 1,
                    OpId = "op-link",
                    Kind = "add-link",
                    Target = new ReviewTarget { WorkItemId = 729 },
                    Summary = "Link #729 to #742",
                    Preconditions = [],
                    Consequences = [new ReviewConsequence { Kind = "link-add", Relation = "predecessor", OtherId = 742 }],
                },
                new ReviewOperation
                {
                    Ordinal = 2,
                    OpId = "op-seed",
                    Kind = "publish-seed",
                    Target = new ReviewTarget { StagedIdentity = "seed-abc" },
                    Summary = "Publish staged seed",
                    Preconditions = [new ReviewPrecondition { Kind = "expectedFingerprint", Value = "fp-1" }],
                    Consequences = [new ReviewConsequence { Kind = "seed-publish" }],
                },
            ],
            AuthorizationChoices = ["apply", "revise", "decline"],
            Blockers = blockers ?? [],
        };

    private static string RenderText(ChangeProposalReviewModel model)
    {
        var lines = ChangeProposalReviewRenderer.Render(model, true);
        var output = new StringWriter();
        new RendererFactory().GetRenderer("human", output).Render(new RenderTree.RenderTree(lines));
        return output.ToString();
    }

    // The human projection keeps material effects, warnings and blockers, but no approval controls.
    // Digest, workspace and wire-level operation bookkeeping remain machine data.
    [Fact]
    public void Render_EmitsMaterialEffectsWithoutMachineReviewBoilerplate()
    {
        var text = RenderText(Model());

        text.ShouldNotContain("Change Proposal review");
        text.ShouldNotContain(Digest);
        text.ShouldNotContain("workspace:");
        text.ShouldNotContain("acme/cache");
        text.ShouldNotContain("(ad hoc)");
        text.ShouldContain("rationale: Close out the sprint.");

        // Every affected item, including one the local cache does not know.
        text.ShouldContain("#729");
        text.ShouldContain("Change Recipe");
        text.ShouldContain("#742");
        text.ShouldContain("(uncached)");

        // Every operation's useful effect remains visible while wire metadata stays hidden.
        text.ShouldContain("seed seed-abc");
        text.ShouldNotContain("change:");
        text.ShouldNotContain("op-batch");
        text.ShouldNotContain("op-link");
        text.ShouldNotContain("op-seed");
        text.ShouldNotContain("[0]");
        text.ShouldNotContain("expectedRevision");
        text.ShouldNotContain("expectedFingerprint");
        text.ShouldNotContain("local-cache");

        text.ShouldContain("System.State");
        text.ShouldContain("\"Done\"");
        text.ShouldContain("System.Reason");
        text.ShouldContain("add predecessor link to #742");
        text.ShouldContain("publish staged draft");

        text.ShouldNotContain("authorization choices");
        text.ShouldNotContain("apply");
        text.ShouldNotContain("revise");
        text.ShouldNotContain("decline");
    }

    [Fact]
    public void Render_OnlyShowsRecipeAndNonblankRationaleWhenPresent()
    {
        var absent = RenderText(Model() with { Rationale = " \t" });
        absent.ShouldNotContain("recipe:");
        absent.ShouldNotContain("rationale:");

        var present = RenderText(Model() with
        {
            Recipe = new ChangeRecipeReference { RecipeId = "close-sprint", Version = 3 },
            Rationale = "Close the sprint after verification.",
        });
        present.ShouldContain("recipe: close-sprint v3");
        present.ShouldContain("rationale: Close the sprint after verification.");
    }

    [Fact]
    public void Render_TranslatesObservationFailureIntoMaterialWarning()
    {
        var model = Model();
        var operation = model.Operations[0];
        var consequence = operation.Consequences[0] with
        {
            Before = new ReviewBeforeValue { State = "unknown", Reason = "revision-mismatch" },
        };

        var text = RenderText(model with
        {
            Operations = [operation with { Consequences = [consequence] }],
        });

        text.ShouldContain("warning: previous value unavailable because the item changed since this proposal was prepared");
        text.ShouldNotContain("revision-mismatch");
        text.ShouldNotContain("cached item version");
    }

    // A blocked proposal still shows the material reason even though review does not offer approval.
    [Fact]
    public void Render_ShowsMaterialBlockerWithoutAuthorizationControls()
    {
        var blocked = Model(blockers:
            [new ReviewBlocker { Kind = "pending", WorkItemId = 740, Detail = "1 pending change staged" }]);

        var text = RenderText(blocked);
        text.ShouldContain("Pending local change for #740: 1 pending change staged");
        text.ShouldNotContain("authorization choices");
    }

    // T2 §4.3 rule 2. Defends against a build silently rendering the members it recognises out
    // of a newer model — showing a reviewer a proposal with unknown parts quietly missing.
    [Fact]
    public void UnsupportedModelVersion_IsRefused()
    {
        ChangeProposalReviewRenderer.IsSupported(ChangeProposalReviewRenderer.SupportedModelVersion)
            .ShouldBeTrue();
        ChangeProposalReviewRenderer.IsSupported(2).ShouldBeFalse();
        ChangeProposalReviewRenderer.IsSupported(0).ShouldBeFalse();
    }

}
