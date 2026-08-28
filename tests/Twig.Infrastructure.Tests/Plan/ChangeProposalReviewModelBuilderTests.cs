using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Behavioural tests for the canonical semantic review model (AB#742, T2 §4).
/// <para>
/// The model is the sole source of truth for what a reviewer must be shown, so these tests
/// are about completeness above all: every operation, precondition and consequence present in
/// the proposal must be present in the model. They assert the model's observable content, not
/// how the builder is structured internally.
/// </para>
/// </summary>
public sealed class ChangeProposalReviewModelBuilderTests
{
    private static readonly PlanWorkspace Workspace = new() { Organization = "acme", Project = "cache" };

    private readonly IWorkItemRepository _workItems = Substitute.For<IWorkItemRepository>();

    public ChangeProposalReviewModelBuilderTests() =>
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>([]));

    private ChangeProposalReviewModelBuilder Builder() => new(_workItems);

    // ── completeness ──────────────────────────────────────────────────────

    [Fact]
    public async Task Model_DescribesEveryOperationInDeclaredOrder()
    {
        // Defends against: the single worst failure this model exists to prevent — an
        // operation present in the proposal but absent from what the reviewer was shown. A
        // reviewer would then authorize a digest covering a mutation they never saw.
        var model = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: true);

        model.Operations.Count.ShouldBe(5);
        model.Operations.Select(o => o.Kind).ShouldBe(
            ["batch", "add-link", "remove-link", "publish-seed", "delete"]);
        model.Operations.Select(o => o.Ordinal).ShouldBe([0, 1, 2, 3, 4]);
        model.Operations.ShouldAllBe(o => !string.IsNullOrWhiteSpace(o.Summary));
    }

    [Fact]
    public async Task Model_GivesEveryOperationItsPrecondition()
    {
        // Defends against: dropping the revision/fingerprint bound from the review surface.
        // Preconditions are why a proposal would refuse to apply; a reviewer who cannot see
        // them cannot tell a stale proposal from a fresh one.
        var model = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: true);

        model.Operations.ShouldAllBe(o => o.Preconditions.Count > 0);

        var seed = model.Operations.Single(o => o.Kind == "publish-seed");
        seed.Preconditions.Single().Kind.ShouldBe("expectedFingerprint");

        foreach (var op in model.Operations.Where(o => o.Kind != "publish-seed"))
            op.Preconditions.Single().Kind.ShouldBe("expectedRevision");
    }

    [Fact]
    public async Task Model_EnumeratesOneConsequencePerStagedField()
    {
        // Defends against: a batch being summarised as "3 fields changed" with the field list
        // dropped. The reviewer is authorizing the individual writes, not the count.
        var document = Document(new BatchOperation
        {
            Id = "op-1",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["System.State"] = "Doing",
                ["System.AssignedTo"] = "Daniel Green (daniel danielgreen.net)",
                ["System.Reason"] = "Started",
            },
        });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        var consequences = model.Operations.Single().Consequences;
        consequences.Count.ShouldBe(3);
        consequences.ShouldAllBe(c => c.Kind == "field-set");
        consequences.Select(c => c.Field).ShouldBe(
            ["System.State", "System.AssignedTo", "System.Reason"], ignoreOrder: true);
        consequences.Single(c => c.Field == "System.State").To.ShouldBe("Doing");
    }

    [Fact]
    public async Task Model_DistinguishesClearingAFieldFromSettingIt()
    {
        // Defends against: rendering a null value as the literal word "null" or as an empty
        // set. Clearing System.AssignedTo (unassigning someone) is a materially different act
        // from assigning them the string "null", and a reviewer must be able to tell.
        var document = Document(new BatchOperation
        {
            Id = "op-1",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?> { ["System.AssignedTo"] = null },
        });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        var consequence = model.Operations.Single().Consequences.Single();
        consequence.Kind.ShouldBe("field-clear");
        consequence.Field.ShouldBe("System.AssignedTo");
        consequence.To.ShouldBeNull();
    }

    // ── affected items ────────────────────────────────────────────────────

    [Fact]
    public async Task Model_ListsLinkPeersAsAffected_NotJustOperationTargets()
    {
        // Defends against: showing only the item being PATCHed. Adding a parent link changes
        // what the other item's tree looks like too, so a reviewer who only sees the source
        // has an incomplete picture of the blast radius.
        var document = Document(new AddLinkOperation
        {
            Id = "op-1",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Relation = "predecessor",
            OtherId = 740,
        });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        model.AffectedItems.Select(i => i.Id).ShouldBe([740, 742], ignoreOrder: true);
        model.AffectedItems.Single(i => i.Id == 742).Role.ShouldBe("target");
        model.AffectedItems.Single(i => i.Id == 740).Role.ShouldBe("peer");
    }

    [Fact]
    public async Task Model_KeepsAnAffectedItemEvenWhenTheCacheDoesNotKnowIt()
    {
        // Defends against: silently dropping an affected item because the local cache has no
        // row for it. An unknown item is exactly the one a reviewer most needs flagged; the
        // enrichment fields go null, the item itself never disappears.
        var document = Document(new BatchOperation
        {
            Id = "op-1",
            WorkItemId = 999_999,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Doing" },
        });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        var item = model.AffectedItems.Single();
        item.Id.ShouldBe(999_999);
        item.Title.ShouldBeNull();
        item.State.ShouldBeNull();
        item.Type.ShouldBeNull();
    }

    [Fact]
    public async Task Model_EnrichesAffectedItemsFromTheCacheWhenAvailable()
    {
        // Defends against: emitting bare ids. "#742" tells a reviewer nothing; the title and
        // state are how they recognise whether it is the item they meant.
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>(
            [
                new WorkItem { Id = 742, Title = "Implement Change Recipe and Proposal core", Type = WorkItemType.Task },
            ]));

        var document = Document(new DeleteOperation { Id = "op-1", WorkItemId = 742, ExpectedRevision = 4 });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        var item = model.AffectedItems.Single();
        item.Title.ShouldBe("Implement Change Recipe and Proposal core");
        item.Type.ShouldBe("Task");
    }

    [Fact]
    public async Task Model_TargetsAStagedSeedByIdentity_NotByAnInventedWorkItemId()
    {
        // Defends against: synthesising a work item id (or the negative alias) for a seed that
        // has not been published. Any number shown there would be meaningless on the board.
        var identity = StagedIdentity.New();
        var document = Document(new PublishSeedOperation
        {
            Id = "op-1",
            StagedIdentity = identity,
            ExpectedFingerprint = "fingerprint-1",
        });

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], [], canApply: true);

        var op = model.Operations.Single();
        op.Target.WorkItemId.ShouldBeNull();
        op.Target.StagedIdentity.ShouldBe(identity.Value.ToString());
        model.AffectedItems.ShouldBeEmpty();
    }

    // ── authorization choices and blockers ────────────────────────────────

    [Fact]
    public async Task Model_OffersApply_OnlyWhenTheProposalCanActuallyApply()
    {
        // Defends against: presenting an "apply" control that is guaranteed to refuse. A
        // reviewer choosing it learns only that the tool lied about the options.
        var applicable = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: true);
        var blocked = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: false);

        applicable.AuthorizationChoices.ShouldContain("apply");
        blocked.AuthorizationChoices.ShouldNotContain("apply");
        blocked.AuthorizationChoices.ShouldContain("revise");
        blocked.AuthorizationChoices.ShouldContain("decline");
    }

    [Fact]
    public async Task Model_ReportsPendingRowsAndIssuesAsBlockers()
    {
        // Defends against: a proposal that cannot apply presenting no reason why. "canApply:
        // false" with an empty blocker list is an unactionable dead end for the reviewer.
        var pending = new PendingChangeDetail(
            PendingChangeId: 1,
            WorkItemId: 742,
            Kind: "field",
            Field: "System.State",
            Note: null,
            OldValue: "To do",
            NewValue: "Doing",
            StagedAt: DateTimeOffset.UnixEpoch,
            SeedRemap: null);

        var issue = new PlanValidationIssue
        {
            Code = PlanValidationCodes.EmptyFields,
            Path = "/operations/0/fields",
            Message = "batch declared no fields",
        };

        var model = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [issue], [pending], canApply: false);

        model.Blockers.Count.ShouldBe(2);
        model.Blockers.ShouldContain(b => b.Kind == "issue" && b.Detail.Contains(PlanValidationCodes.EmptyFields));

        var pendingBlocker = model.Blockers.Single(b => b.Kind == "pending");
        pendingBlocker.WorkItemId.ShouldBe(742);
        pendingBlocker.Detail.ShouldContain("System.State");
    }

    // ── model identity ────────────────────────────────────────────────────

    [Fact]
    public async Task Model_CarriesTheProposalDigestVerbatimAndDeclaresItsVersion()
    {
        // Defends against: a renderer recomputing or reformatting the digest. The digest is
        // the value an authorization binds to; any transformation of it breaks the binding.
        var digest = new string('a', 64);

        var model = await Builder().BuildAsync(AllKindsDocument(), digest, [], [], canApply: true);

        model.Digest.ShouldBe(digest);
        model.Model.ShouldBe("twig.change-proposal.review");
        model.ModelVersion.ShouldBe(1);
        model.Workspace.Organization.ShouldBe("acme");
    }

    [Fact]
    public async Task Model_MarksAnAdHocProposalByCarryingNoRecipe()
    {
        // Defends against: inventing a placeholder recipe reference for a hand-authored
        // proposal, which would send a reviewer looking for a template that does not exist.
        var adHoc = await Builder().BuildAsync(AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: true);
        var rendered = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], canApply: true,
            recipe: new ChangeRecipeReference { RecipeId = "twig.test.recipe", Version = 2 });

        adHoc.Recipe.ShouldBeNull();
        rendered.Recipe.ShouldNotBeNull();
        rendered.Recipe!.RecipeId.ShouldBe("twig.test.recipe");
        rendered.Recipe.Version.ShouldBe(2);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static PlanDefinition Document(params PlanOperationDefinition[] operations) => new()
    {
        Version = 1,
        Workspace = Workspace,
        Operations = operations,
    };

    /// <summary>One document carrying every operation kind, in the closed-set order.</summary>
    private static PlanDefinition AllKindsDocument() => Document(
        new BatchOperation
        {
            Id = "op-batch",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Doing" },
        },
        new AddLinkOperation
        {
            Id = "op-add",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Relation = "predecessor",
            OtherId = 740,
        },
        new RemoveLinkOperation
        {
            Id = "op-remove",
            WorkItemId = 742,
            ExpectedRevision = 4,
            Relation = "related",
            OtherId = 741,
        },
        new PublishSeedOperation
        {
            Id = "op-seed",
            StagedIdentity = StagedIdentity.New(),
            ExpectedFingerprint = "fingerprint-1",
        },
        new DeleteOperation
        {
            Id = "op-delete",
            WorkItemId = 743,
            ExpectedRevision = 2,
        });
}
