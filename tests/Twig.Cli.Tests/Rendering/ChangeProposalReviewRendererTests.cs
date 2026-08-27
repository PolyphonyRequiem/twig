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
/// The contract these tests defend is narrow and total: every material entry of the canonical
/// review model reaches the reviewer, no authorization choice is invented or dropped, the digest
/// is echoed rather than recomputed, and an unknown model version refuses outright.
/// </para>
/// </summary>
public sealed class ChangeProposalReviewRendererTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ChangeProposalReviewModel Model(
        IReadOnlyList<string>? choices = null,
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
            AuthorizationChoices = choices ?? ["apply", "revise", "decline"],
            Blockers = blockers ?? [],
        };

    private static string RenderText(
        ChangeProposalReviewModel model,
        SessionSteeringMode steering = SessionSteeringMode.HumanSteered)
    {
        var lines = ChangeProposalReviewRenderer.Render(model, steering);
        return string.Join("\n", lines.Select(n => n switch
        {
            RenderNode.Text t => t.Content,
            RenderNode.Hint h => h.Content,
            _ => n.ToString(),
        }));
    }

    // 🔴 The core compliance rule: no material entry may be elided. Defends against a renderer
    // that summarises — showing operation ids but not the field values they will write, which
    // is precisely "authorized a mutation they were never shown".
    [Fact]
    public void Render_EmitsEveryMaterialEntryOfTheModel()
    {
        var text = RenderText(Model());

        // Digest, verbatim.
        text.ShouldContain(Digest);
        text.ShouldContain("acme/cache");
        text.ShouldContain("Close out the sprint.");

        // Every affected item, including one the local cache does not know.
        text.ShouldContain("#729");
        text.ShouldContain("Change Recipe");
        text.ShouldContain("#742");
        text.ShouldContain("(uncached)");

        // Every operation, by ordinal and id, including the seed with no work item id.
        text.ShouldContain("op-batch");
        text.ShouldContain("op-link");
        text.ShouldContain("op-seed");
        text.ShouldContain("seed seed-abc");

        // Every precondition.
        text.ShouldContain("expectedRevision = 7");
        text.ShouldContain("expectedFingerprint = fp-1");

        // Every consequence, with its values — not just its kind.
        text.ShouldContain("field-set System.State = Done");
        text.ShouldContain("field-clear System.Reason");
        text.ShouldContain("link-add predecessor");
        text.ShouldContain("seed-publish");

        // Every authorization choice.
        text.ShouldContain("apply");
        text.ShouldContain("revise");
        text.ShouldContain("decline");
    }

    // Defends against: a fallback that offers `apply` on a proposal the model says cannot
    // apply, which misrepresents the decision the reviewer is making.
    [Fact]
    public void Render_NeverAddsAnAuthorizationChoiceTheModelWithheld()
    {
        var blocked = Model(
            choices: ["revise", "decline"],
            blockers: [new ReviewBlocker { Kind = "pending", WorkItemId = 740, Detail = "1 pending change staged" }]);

        var text = RenderText(blocked);

        text.ShouldContain("authorization choices (2)");
        text.ShouldContain("revise, decline");
        text.ShouldNotContain("apply,");
        text.ShouldContain("#740 1 pending change staged");
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

    // In a human-steered session the fallback must say the proposal is NOT applied. Defends
    // against a reviewer reading a rendered proposal as a report of something already done.
    [Fact]
    public void HumanSteered_HoldsApplyPendingConfirmation()
    {
        var text = RenderText(Model(), SessionSteeringMode.HumanSteered);

        text.ShouldContain("Not applied");
        text.ShouldContain("sign-off");
    }

    [Fact]
    public void Afk_NamesTheModelAuthorizationItRequires()
    {
        var text = RenderText(Model(), SessionSteeringMode.Afk);

        text.ShouldContain("AFK-steered");
        text.ShouldContain("model authorization record");
    }

    // Spec #729: steering mode is a session property, never a transport attachment. Defends
    // against rendering that varies with the pane/worktree/agent-session a run is attached to,
    // which would make the same proposal read differently depending on where it was opened.
    [Theory]
    [InlineData(SessionSteeringMode.HumanSteered)]
    [InlineData(SessionSteeringMode.Afk)]
    public void Render_IsIdenticalRegardlessOfTransportIdentity(SessionSteeringMode steering)
    {
        var baseline = RenderText(Model(), steering);

        string[] transportVariables = ["HERDR_ENV", "HERDR_TAB_ID", "WORK_ITEM", "BATON"];
        var saved = transportVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var variable in transportVariables)
                Environment.SetEnvironmentVariable(variable, $"transport-{Guid.NewGuid():N}");

            RenderText(Model(), steering).ShouldBe(baseline);
        }
        finally
        {
            foreach (var (variable, value) in saved)
                Environment.SetEnvironmentVariable(variable, value);
        }
    }
}
