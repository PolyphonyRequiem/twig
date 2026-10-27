using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
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

    [Fact]
    public async Task Enrichment_UsesOnlyCleanMatchingRevision_AndKeepsEmptyDistinctFromAbsent()
    {
        var item = new WorkItem { Id = 742, Title = "Target", Type = WorkItemType.Task };
        item.ImportFields(new Dictionary<string, string?> { ["Custom.Empty"] = "", ["Custom.Absent"] = null });
        item.MarkSynced(4);
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>([item]));
        var doc = Document(new BatchOperation { Id = "edit", WorkItemId = 742, ExpectedRevision = 4,
            Fields = new Dictionary<string, string?> { ["Custom.Empty"] = null, ["Custom.Absent"] = "x", ["Custom.Missing"] = "y" } });
        var model = await Builder().BuildAsync(doc, "digest", [], []);
        var effects = model.Operations.Single().Consequences;
        effects[0].Before!.State.ShouldBe("value");
        effects[0].Before!.Value.ShouldBe("");
        effects[1].Before!.State.ShouldBe("absent");
        effects[2].Before!.State.ShouldBe("unknown");
        effects[2].Before!.Reason.ShouldBe("field-not-cached");
        item.MarkSynced(5);
        var stale = await Builder().BuildAsync(doc, "digest", [], []);
        stale.Operations.Single().Consequences.ShouldAllBe(c => c.Before!.State == "unknown" && c.Before.Reason == "revision-mismatch");
        stale.Digest.ShouldBe(model.Digest);
    }

    [Fact]
    public async Task Enrichment_KeepsSeedIdentity_AndBoundsCachedParentContextToOneHop()
    {
        var identity = StagedIdentity.New();
        var seed = new WorkItem { Id = -2, IsSeed = true, StagedIdentity = identity, Title = "Draft", Type = WorkItemType.Task, ParentId = 10 };
        _workItems.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<WorkItem>>([seed]));
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<WorkItem>>(call.Arg<IEnumerable<int>>().Where(id => id == 10)
                .Select(id => new WorkItem { Id = id, Title = "Parent", Type = WorkItemType.Feature, ParentId = 99 }).ToArray()));
        var model = await Builder().BuildAsync(Document(new PublishSeedOperation { Id = "publish", StagedIdentity = identity, ExpectedFingerprint = "exact-fingerprint" }), "digest", [], []);
        var target = model.Operations.Single().Target;
        target.WorkItemId.ShouldBeNull(); target.StagedIdentity.ShouldBe(identity.Value.ToString());
        target.Seed!.DisplayAlias.ShouldBe(-2); target.Seed.Title.ShouldBe("Draft"); target.Seed.Type.ShouldBe("Task");
        model.AffectedItems.ShouldBeEmpty();
        model.ContextItems.Single().Id.ShouldBe(10); model.ContextItems.Single().Role.ShouldBe("context");
        model.ContextItems.Single().Url.ShouldBe("https://dev.azure.com/acme/cache/_workitems/edit/10");
        model.Operations.Single().Preconditions.Single().Value.ShouldBe("exact-fingerprint");
    }

    [Fact]
    public async Task Enrichment_LabelsAndMetricComeFromCore_AndDirtyOrMissingBaselineNeverInventCounts()
    {
        var item = new WorkItem { Id = 742, Title = "Target", Type = WorkItemType.Task };
        item.ImportFields(new Dictionary<string, string?> { ["System.Description"] = "A😀Z" });
        item.MarkSynced(4);
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>([item]));
        var fields = Substitute.For<IFieldDefinitionStore>();
        fields.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<FieldDefinition>>([new("System.Description", "Description", "html", false)]));
        var builder = new ChangeProposalReviewModelBuilder(_workItems, fields);
        var doc = Document(new BatchOperation { Id = "edit", WorkItemId = 742, ExpectedRevision = 4, Fields = new Dictionary<string, string?> { ["System.Description"] = "A😁Z" } });
        var model = await builder.BuildAsync(doc, "digest", [], []);
        var con = model.Operations.Single().Consequences.Single();
        con.FieldLabel.ShouldBe("Description"); con.FieldType.ShouldBe("html");
        con.Before!.Value.ShouldBe("A😀Z"); con.TextChange!.Removed.ShouldBe(1); con.TextChange.Inserted.ShouldBe(1);
        item.UpdateField("System.Description", "local text");
        var dirty = await builder.BuildAsync(doc, "digest", [], []);
        dirty.Operations.Single().Consequences.Single().Before!.Reason.ShouldBe("local-edits");
        dirty.Operations.Single().Consequences.Single().TextChange.ShouldBeNull();
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<WorkItem>>([]));
        var missing = await builder.BuildAsync(doc, "digest", [], []);
        missing.Operations.Single().Consequences.Single().Before!.Reason.ShouldBe("item-not-cached");
        missing.Operations.Single().Consequences.Single().TextChange.ShouldBeNull();
    }

    [Fact]
    public async Task Enrichment_DisambiguatesLabelsAcrossRequestedEffectsOnly_WithoutChangingReferences()
    {
        var fields = Substitute.For<IFieldDefinitionStore>();
        var definitions = new FieldDefinition[]
        {
            new("Custom.BusinessPriority", "Priority", "integer", false),
            new("Custom.SupportPriority", "Priority", "integer", false),
            new("System.State", "State", "string", false),
            new("Custom.UnusedState", "State", "string", false),
        };
        fields.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<FieldDefinition>>(definitions));
        var builder = new ChangeProposalReviewModelBuilder(_workItems, fields);
        var doc = Document(
            new BatchOperation { Id = "business", WorkItemId = 742, ExpectedRevision = 4,
                Fields = new Dictionary<string, string?> { ["Custom.BusinessPriority"] = "1", ["System.State"] = "Doing" } },
            new BatchOperation { Id = "support", WorkItemId = 743, ExpectedRevision = 5,
                Fields = new Dictionary<string, string?> { ["Custom.SupportPriority"] = null, ["System.State"] = "New" } },
            new BatchOperation { Id = "business-again", WorkItemId = 744, ExpectedRevision = 6,
                Fields = new Dictionary<string, string?> { ["Custom.BusinessPriority"] = "2", ["Custom.NoMetadata"] = "exact" } });
        var model = await builder.BuildAsync(doc, "digest", [], []);
        var effects = model.Operations.SelectMany(o => o.Consequences).ToArray();
        effects.Select(c => c.FieldLabel).ShouldBe([
            "Priority (Custom.BusinessPriority)", "State", "Priority (Custom.SupportPriority)", "State",
            "Priority (Custom.BusinessPriority)", "Custom.NoMetadata",
        ]);
        var references = new[] { "Custom.BusinessPriority", "System.State", "Custom.SupportPriority", "System.State", "Custom.BusinessPriority", "Custom.NoMetadata" };
        effects.Select(c => c.Field).ShouldBe(references);
        using var json = System.Text.Json.JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var wireEffects = json.RootElement.GetProperty("operations").EnumerateArray()
            .SelectMany(o => o.GetProperty("consequences").EnumerateArray()).ToArray();
        wireEffects.Select(c => c.GetProperty("field").GetString()).ShouldBe(references);
        wireEffects.Select(c => c.GetProperty("fieldLabel").GetString()).ShouldBe(effects.Select(c => c.FieldLabel));
        definitions.Select(d => d.DisplayName).ShouldBe(["Priority", "Priority", "State", "State"]);
    }

    // ── completeness ──────────────────────────────────────────────────────

    [Fact]
    public async Task Model_DescribesEveryOperationInDeclaredOrder()
    {
        // Defends against: the single worst failure this model exists to prevent — an
        // operation present in the proposal but absent from what the reviewer was shown. A
        // reviewer would then authorize a digest covering a mutation they never saw.
        var model = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], []);

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
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

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

        var model = await Builder().BuildAsync(document, "d".PadLeft(64, 'a'), [], []);

        var op = model.Operations.Single();
        op.Target.WorkItemId.ShouldBeNull();
        op.Target.StagedIdentity.ShouldBe(identity.Value.ToString());
        model.AffectedItems.ShouldBeEmpty();
    }

    // ── blockers ──────────────────────────────────────────────────────────

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
            AllKindsDocument(), "d".PadLeft(64, 'a'), [issue], [pending]);

        model.Blockers.Count.ShouldBe(2);
        model.Blockers.ShouldContain(b => b.Kind == "issue" && b.Detail.Contains(PlanValidationCodes.EmptyFields));

        var pendingBlocker = model.Blockers.Single(b => b.Kind == "pending");
        pendingBlocker.WorkItemId.ShouldBe(742);
        pendingBlocker.Detail.ShouldContain("System.State");
    }

    [Fact]
    public async Task Model_PreservesPolicyDecisionsAndProjectsBlockingReasonsToReview()
    {
        var decisions = new PolicyDecision[]
        {
            new() { RuleId = "parent", Kind = "parent-type", Outcome = PolicyOutcome.Warn, Reason = "Existing violation is not worsened.", WorkItemId = 742, OpId = "op-1" },
            new() { RuleId = "parent", Kind = "parent-type", Outcome = PolicyOutcome.Refuse, Reason = "The parent type is not allowed.", WorkItemId = 729, OpId = "op-2", ProviderIdentity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_729") },
        };

        var model = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [], policyDecisions: decisions);

        model.PolicyDecisions.ShouldBe(decisions);
        var blocker = model.Blockers.ShouldHaveSingleItem();
        blocker.Kind.ShouldBe("policy");
        blocker.WorkItemId.ShouldBe(729);
        blocker.Detail.ShouldContain("[parent]");
        blocker.Detail.ShouldContain("refuse");
        blocker.Detail.ShouldContain("The parent type is not allowed.");

        using var json = System.Text.Json.JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var policyDecisions = json.RootElement.GetProperty("policyDecisions");
        policyDecisions.GetArrayLength().ShouldBe(2);
        policyDecisions[0].GetProperty("outcome").GetString().ShouldBe("warn");
        policyDecisions[0].GetProperty("blocking").GetBoolean().ShouldBeFalse();
        policyDecisions[0].GetProperty("reason").GetString().ShouldBe("Existing violation is not worsened.");
        policyDecisions[1].GetProperty("outcome").GetString().ShouldBe("refuse");
        policyDecisions[1].GetProperty("blocking").GetBoolean().ShouldBeTrue();
        policyDecisions[1].GetProperty("workItemId").GetInt32().ShouldBe(729);
        policyDecisions[1].GetProperty("providerIdentity").GetProperty("providerKind").GetString().ShouldBe("github");
        policyDecisions[1].GetProperty("providerIdentity").GetProperty("resourceScope").GetString().ShouldBe("https://api.github.com");
        policyDecisions[1].GetProperty("providerIdentity").GetProperty("nativeId").GetString().ShouldBe("I_729");
        policyDecisions[1].GetProperty("opId").GetString().ShouldBe("op-2");
    }

    // ── model identity ────────────────────────────────────────────────────

    [Fact]
    public async Task Model_CarriesTheProposalDigestVerbatimAndDeclaresItsVersion()
    {
        // Defends against: a renderer recomputing or reformatting the digest. The digest is
        // the value an authorization binds to; any transformation of it breaks the binding.
        var digest = new string('a', 64);

        var model = await Builder().BuildAsync(AllKindsDocument(), digest, [], []);

        model.Digest.ShouldBe(digest);
        model.Model.ShouldBe("twig.change-proposal.review");
        model.ModelVersion.ShouldBe(1);
        model.Workspace.Organization.ShouldBe("acme");
        using var json = System.Text.Json.JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        json.RootElement.TryGetProperty("providerAffectedItems", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Model_ProjectsV2ProviderIdentityAndOpaquePreconditionWithoutAdoAlias()
    {
        var source = new ProviderWorkItemReference
        {
            LocalId = LocalWorkItemId.New(),
            ExternalIdentity = new ExternalWorkItemIdentity("github", "repo:acme/widgets", "issue:7"),
        };
        var peer = new ProviderWorkItemReference
        {
            LocalId = LocalWorkItemId.New(),
            ExternalIdentity = new ExternalWorkItemIdentity("github", "repo:acme/widgets", "issue:8"),
        };
        var definition = new PlanDefinition
        {
            Version = 2,
            Workspace = Workspace,
            Operations =
            [
                new ProviderMutationOperation
                {
                    Id = "edit",
                    OperationKind = PlanOperationKind.Batch,
                    Target = new ProviderWorkItemMutationTarget
                    {
                        Item = source,
                        ExpectedProviderRevision = new ProviderRevisionToken("etag:\"opaque\""),
                    },
                    Fields = new Dictionary<string, string?> { ["System.Title"] = "Updated" },
                },
                new ProviderMutationOperation
                {
                    Id = "link",
                    OperationKind = PlanOperationKind.AddLink,
                    Target = new ProviderWorkItemMutationTarget
                    {
                        Item = source,
                        ExpectedProviderRevision = new ProviderRevisionToken("opaque:next"),
                    },
                    Relation = "related",
                    Other = peer,
                },
            ],
        };

        var model = await Builder().BuildAsync(definition, "digest-v2", [], []);

        model.Operations.Select(operation => operation.Target.ProviderWorkItem)
            .ShouldBe(new ProviderWorkItemReference?[] { source, source });
        model.Operations[0].Preconditions.Single().ShouldBe(new ReviewPrecondition
        {
            Kind = "expectedProviderRevision",
            Value = "etag:\"opaque\"",
        });
        model.Operations[0].Consequences.Single().Field.ShouldBe("System.Title");
        model.Operations[1].Consequences.Single().OtherProviderWorkItem.ShouldBe(peer);
        model.Operations.Select(operation => operation.Summary).ShouldAllBe(summary =>
            summary.Contains(source.ExternalIdentity.NativeId) && summary.Contains(source.LocalId.ToString()));
        _workItems.ReceivedCalls().ShouldBeEmpty();

        using var json = System.Text.Json.JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var target = json.RootElement.GetProperty("operations")[0].GetProperty("target").GetProperty("providerWorkItem");
        target.GetProperty("localId").GetString().ShouldBe(source.LocalId.ToString());
        target.GetProperty("externalIdentity").GetProperty("nativeId").GetString().ShouldBe("issue:7");
        json.RootElement.GetProperty("operations")[0].GetProperty("preconditions")[0]
            .GetProperty("value").GetString().ShouldBe("etag:\"opaque\"");
        json.RootElement.GetProperty("operations")[1].GetProperty("consequences")[0]
            .GetProperty("otherProviderWorkItem").GetProperty("externalIdentity")
            .GetProperty("nativeId").GetString().ShouldBe("issue:8");
    }

    [Fact]
    public async Task Model_ListsV2TargetsAndPeersByCanonicalIdentityWithTargetRolePrecedence()
    {
        static ProviderWorkItemReference Reference(string nativeId) => new()
        {
            LocalId = LocalWorkItemId.New(),
            ExternalIdentity = new ExternalWorkItemIdentity("github", "repo:acme/widgets", nativeId),
        };

        var shared = Reference("issue:7");
        var source = Reference("issue:8");
        var peer = Reference("issue:9");
        ProviderMutationOperation Batch(string id, ProviderWorkItemReference item) => new()
        {
            Id = id,
            OperationKind = PlanOperationKind.Batch,
            Target = new ProviderWorkItemMutationTarget
            {
                Item = item,
                ExpectedProviderRevision = new ProviderRevisionToken($"revision:{id}"),
            },
            Fields = new Dictionary<string, string?> { ["System.Title"] = id },
        };
        ProviderMutationOperation Link(string id, ProviderWorkItemReference target, ProviderWorkItemReference other) => new()
        {
            Id = id,
            OperationKind = PlanOperationKind.AddLink,
            Target = new ProviderWorkItemMutationTarget
            {
                Item = target,
                ExpectedProviderRevision = new ProviderRevisionToken($"revision:{id}"),
            },
            Relation = "related",
            Other = other,
        };
        var definition = new PlanDefinition
        {
            Version = 2,
            Workspace = Workspace,
            Operations =
            [
                Link("link-shared", source, shared),
                Batch("batch", shared),
                Link("link-peer", source, peer),
            ],
        };

        var model = await Builder().BuildAsync(definition, "digest-v2", [], []);

        var affected = model.ProviderAffectedItems!;
        affected.Count.ShouldBe(3);
        affected.Select(item => item.Item).ToHashSet().SetEquals([shared, source, peer]).ShouldBeTrue();
        affected.Single(item => item.Item == shared).Role.ShouldBe("target");
        affected.Single(item => item.Item == source).Role.ShouldBe("target");
        affected.Single(item => item.Item == peer).Role.ShouldBe("peer");
        model.AffectedItems.ShouldBeEmpty();
        _workItems.ReceivedCalls().ShouldBeEmpty();

        using var json = System.Text.Json.JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var providerItems = json.RootElement.GetProperty("providerAffectedItems").EnumerateArray().ToArray();
        providerItems.Length.ShouldBe(3);
        providerItems.ShouldContain(item =>
            item.GetProperty("item").GetProperty("localId").GetString() == shared.LocalId.ToString()
            && item.GetProperty("item").GetProperty("externalIdentity").GetProperty("providerKind").GetString() == "github"
            && item.GetProperty("item").GetProperty("externalIdentity").GetProperty("resourceScope").GetString() == "repo:acme/widgets"
            && item.GetProperty("role").GetString() == "target");
        providerItems.ShouldContain(item =>
            item.GetProperty("item").GetProperty("externalIdentity").GetProperty("nativeId").GetString() == "issue:9"
            && item.GetProperty("role").GetString() == "peer");
        foreach (var providerItem in providerItems)
        {
            var item = providerItem.GetProperty("item");
            item.TryGetProperty("title", out var title).ShouldBeFalse();
            item.TryGetProperty("type", out var type).ShouldBeFalse();
            item.TryGetProperty("state", out var state).ShouldBeFalse();
            title.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
            type.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
            state.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
        }
    }

    [Fact]
    public async Task Model_MarksAnAdHocProposalByCarryingNoRecipe()
    {
        // Defends against: inventing a placeholder recipe reference for a hand-authored
        // proposal, which would send a reviewer looking for a template that does not exist.
        var adHoc = await Builder().BuildAsync(AllKindsDocument(), "d".PadLeft(64, 'a'), [], []);
        var rendered = await Builder().BuildAsync(
            AllKindsDocument(), "d".PadLeft(64, 'a'), [], [],
            recipe: new ChangeRecipeReference { RecipeId = "twig.test.recipe", Version = 2 });

        adHoc.Recipe.ShouldBeNull();
        rendered.Recipe.ShouldNotBeNull();
        rendered.Recipe!.RecipeId.ShouldBe("twig.test.recipe");
        rendered.Recipe.Version.ShouldBe(2);
    }

    // ── declared capability advisory (AB#1191) ────────────────────────────

    private static PlanDefinition AddLink(string relation) => Document(new AddLinkOperation
    {
        Id = "op-1",
        WorkItemId = 742,
        ExpectedRevision = 4,
        Relation = relation,
        OtherId = 740,
    });

    private static async Task<ReviewOperation> BuildAddLinkAsync(
        IWorkItemRepository workItems, string relation, Twig.Domain.Capabilities.ProviderCapabilityDescriptor? descriptor)
    {
        var builder = new ChangeProposalReviewModelBuilder(workItems) { CapabilityDescriptor = descriptor };
        var model = await builder.BuildAsync(AddLink(relation), "d".PadLeft(64, 'a'), [], []);
        return model.Operations.Single();
    }

    [Theory]
    [InlineData("github", "parent", "parent-child", "native")]
    [InlineData("github", "predecessor", "predecessor-successor", "native")]
    [InlineData("github", "related", "related", "unsupported")]
    [InlineData("ado", "related", "related", "native")]
    public async Task AddLink_CarriesDeclaredNotObservedAdvisory(string provider, string relation, string kind, string declared)
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet(provider, out var descriptor).ShouldBeTrue();

        var op = await BuildAddLinkAsync(_workItems, relation, descriptor);

        op.Capability.ShouldNotBeNull();
        op.Capability!.Relationship.ShouldBe(kind);
        op.Capability.Declared.ShouldBe(declared);
        op.Capability.Availability.ShouldBe("unknown");
        op.Capability.Reason.ShouldBe("not-probed");
    }

    [Fact]
    public async Task AddLink_WithoutDescriptor_StillEmitsDeclaredUnknown()
    {
        var op = await BuildAddLinkAsync(_workItems, "parent", descriptor: null);

        op.Capability.ShouldNotBeNull();
        op.Capability!.Declared.ShouldBe("unknown");
        op.Capability.Availability.ShouldBe("unknown");
    }

    [Fact]
    public async Task AddLink_WithUnknownRelation_IsDeclaredUnknown()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();

        var op = await BuildAddLinkAsync(_workItems, "mystery", descriptor);

        op.Capability!.Relationship.ShouldBe("unknown");
        op.Capability.Declared.ShouldBe("unknown");
    }

    private static ProviderWorkItemReference GitHubRef(string issue) => new()
    {
        LocalId = LocalWorkItemId.New(),
        ExternalIdentity = new ExternalWorkItemIdentity("github", "repo:acme/widgets", issue),
    };

    private static PlanDefinition ProviderOp(PlanOperationKind kind, string? relation) => new()
    {
        Version = 2,
        Workspace = Workspace,
        Operations =
        [
            new ProviderMutationOperation
            {
                Id = "op-1",
                OperationKind = kind,
                Target = new ProviderWorkItemMutationTarget
                {
                    Item = GitHubRef("issue:7"),
                    ExpectedProviderRevision = new ProviderRevisionToken("opaque:next"),
                },
                Relation = relation,
                Other = kind == PlanOperationKind.AddLink ? GitHubRef("issue:8") : null,
                Fields = kind == PlanOperationKind.Batch ? new Dictionary<string, string?> { ["System.Title"] = "T" } : null,
            },
        ],
    };

    [Theory]
    [InlineData("parent", "parent-child", "native")]
    [InlineData("predecessor", "predecessor-successor", "native")]
    [InlineData("related", "related", "unsupported")]
    [InlineData("mystery", "unknown", "unknown")]
    [InlineData(null, "unknown", "unknown")]
    [InlineData("", "unknown", "unknown")]
    public async Task ProviderAddLink_CarriesDeclaredNotObservedAdvisory(string? relation, string kind, string declared)
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();
        var builder = new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor };

        var model = await builder.BuildAsync(ProviderOp(PlanOperationKind.AddLink, relation), "d".PadLeft(64, 'a'), [], []);

        var capability = model.Operations.Single().Capability;
        capability.ShouldNotBeNull();
        capability!.Relationship.ShouldBe(kind);
        capability.Declared.ShouldBe(declared);
        capability.Availability.ShouldBe("unknown");
        capability.Reason.ShouldBe("not-probed");
    }

    [Fact]
    public async Task ProviderAddLink_WithoutDescriptor_IsDeclaredUnknown()
    {
        var builder = new ChangeProposalReviewModelBuilder(_workItems);

        var model = await builder.BuildAsync(ProviderOp(PlanOperationKind.AddLink, "parent"), "d".PadLeft(64, 'a'), [], []);

        model.Operations.Single().Capability!.Declared.ShouldBe("unknown");
    }

    [Fact]
    public async Task ProviderNonLinkOperation_CarriesNoAdvisory()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();
        var builder = new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor };

        var model = await builder.BuildAsync(ProviderOp(PlanOperationKind.Batch, null), "d".PadLeft(64, 'a'), [], []);

        model.Operations.Single().Capability.ShouldBeNull();
    }

    [Fact]
    public async Task ProviderAddLink_AdvisoryLeavesCanonicalBytesAndLegacyReviewBytesUnchanged()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();
        var plan = ProviderOp(PlanOperationKind.AddLink, "parent");
        static string DeriveDigest(string planJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(planJson);
            return PlanCanonicalizer.Canonicalize(doc.RootElement).Digest;
        }

        var canonicalBefore = PlanDocumentWriter.Write(plan);
        var digestBefore = DeriveDigest(canonicalBefore);
        digestBefore.Length.ShouldBe(64);

        var withAdvisory = await new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor }
            .BuildAsync(plan, digestBefore, [], []);

        PlanDocumentWriter.Write(plan).ShouldBe(canonicalBefore);
        DeriveDigest(PlanDocumentWriter.Write(plan)).ShouldBe(digestBefore);
        withAdvisory.Digest.ShouldBe(digestBefore);
        var stripped = ChangeProposalReviewModelJson.Serialize(withAdvisory with
        {
            Operations = [.. withAdvisory.Operations.Select(o => o with { Capability = null })],
        });
        stripped.ShouldNotContain("\"capability\"");
        ChangeProposalReviewModelJson.Serialize(withAdvisory).ShouldContain("\"capability\"");
    }

    [Fact]
    public async Task NonAddLinkOperations_CarryNoAdvisory()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();
        var builder = new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor };

        var model = await builder.BuildAsync(AllKindsDocument(), "d".PadLeft(64, 'a'), [], []);

        model.Operations.Where(o => o.Kind != "add-link").ShouldAllBe(o => o.Capability == null);
    }

    [Fact]
    public async Task Advisory_JsonNamesDeclaredOnly_AndLegacyShapeIsUnchangedWithoutIt()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var descriptor).ShouldBeTrue();
        var builder = new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor };
        var model = await builder.BuildAsync(AddLink("parent"), "d".PadLeft(64, 'a'), [], []);

        var json = ChangeProposalReviewModelJson.Serialize(model);
        var stripped = ChangeProposalReviewModelJson.Serialize(model with
        {
            Operations = [.. model.Operations.Select(o => o with { Capability = null })],
        });

        json.ShouldContain("\"capability\":{\"relationship\":\"parent-child\",\"declared\":\"native\",\"availability\":\"unknown\",\"reason\":\"not-probed\"}");
        json.ShouldNotContain("provider-native");
        json.ShouldNotContain("origin");
        stripped.ShouldNotContain("\"capability\"");
        json.Replace(",\"capability\":{\"relationship\":\"parent-child\",\"declared\":\"native\",\"availability\":\"unknown\",\"reason\":\"not-probed\"}", "")
            .ShouldBe(stripped);
    }

    // Defends the digest claim: the advisory sits outside canonical effects, so the canonical plan
    // bytes, the digest and every non-advisory review byte are identical whichever descriptor is
    // configured (including none).
    [Fact]
    public async Task Advisory_LeavesCanonicalPlanBytesDigestAndNonAdvisoryReviewBytesUnchanged()
    {
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("github", out var github).ShouldBeTrue();
        Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet("ado", out var ado).ShouldBeTrue();
        var plan = AddLink("predecessor");

        // The digest is derived from the canonical plan bytes, not a supplied constant.
        static string DeriveDigest(string planJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(planJson);
            return PlanCanonicalizer.Canonicalize(doc.RootElement).Digest;
        }

        var canonical = PlanDocumentWriter.Write(plan);
        var digestBefore = DeriveDigest(canonical);
        digestBefore.Length.ShouldBe(64);

        var models = new List<ChangeProposalReviewModel>();
        foreach (var descriptor in new Twig.Domain.Capabilities.ProviderCapabilityDescriptor?[] { null, ado, github })
        {
            var builder = new ChangeProposalReviewModelBuilder(_workItems) { CapabilityDescriptor = descriptor };
            models.Add(await builder.BuildAsync(plan, digestBefore, [], []));
            PlanDocumentWriter.Write(plan).ShouldBe(canonical);
            DeriveDigest(PlanDocumentWriter.Write(plan)).ShouldBe(digestBefore);
        }

        models.ShouldAllBe(m => m.Digest == digestBefore);
        var legacy = models.Select(m => ChangeProposalReviewModelJson.Serialize(m with
        {
            Operations = [.. m.Operations.Select(o => o with { Capability = null })],
        })).Distinct().ToList();
        legacy.Count.ShouldBe(1);
        legacy[0].ShouldNotContain("\"capability\"");
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
