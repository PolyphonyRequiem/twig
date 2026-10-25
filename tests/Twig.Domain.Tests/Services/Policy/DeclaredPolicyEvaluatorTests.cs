using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Policy;

/// <summary>
/// Declared work-management rules (AB#1169). Type and label names below are test
/// data supplied by a policy file; the evaluator itself names none.
/// </summary>
public sealed class DeclaredPolicyEvaluatorTests
{
    private const string Tags = "System.Tags";

    private readonly Dictionary<int, WorkItem> _cache = [];
    private readonly IWorkItemRepository _items = Substitute.For<IWorkItemRepository>();
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly HashSet<int> _verified = [];
    private readonly List<WorkItemLink> _edges = [];

    public DeclaredPolicyEvaluatorTests()
    {
        _items.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<WorkItem>)ci.Arg<IEnumerable<int>>()
                .Where(_cache.ContainsKey).Select(i => _cache[i]).ToList());
        _items.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<WorkItem>)[]);
        _links.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<int, DateTimeOffset>)ci.Arg<IReadOnlyList<int>>()
                .Where(_verified.Contains).ToDictionary(i => i, _ => DateTimeOffset.UnixEpoch));
        _links.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyList<int>>();
                return (IReadOnlyList<WorkItemLink>)_edges.Where(e => ids.Contains(e.SourceId)).ToList();
            });
    }

    private WorkItem Cache(int id, WorkItemType type, int? parent = null, string? tags = null)
    {
        var builder = new WorkItemBuilder(id, $"item {id}").AsType(type);
        if (parent is { } p) builder.WithParent(p);
        if (tags is not null) builder.WithField(Tags, tags);
        var item = builder.Build();
        _cache[id] = item;
        return item;
    }

    private static PlanDefinition Plan(params PlanOperationDefinition[] ops) => new()
    {
        Version = 1,
        Workspace = new PlanWorkspace { Organization = "o", Project = "p" },
        Operations = ops,
    };

    private static AddLinkOperation Link(string id, int from, string relation, int to) => new()
    {
        Id = id, WorkItemId = from, ExpectedRevision = 1, Relation = relation, OtherId = to,
    };

    private static BatchOperation Batch(string id, int item, params (string Key, string? Value)[] fields) => new()
    {
        Id = id, WorkItemId = item, ExpectedRevision = 1,
        Fields = fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static DeclaredPolicy Policy(params DeclaredPolicyRule[] rules) => new() { Rules = rules };

    private static readonly ParentTypeRule TaskUnderIssue = new()
    {
        Id = "task-parent", ChildType = "Task", AllowedParentTypes = ["Issue"],
    };

    private static readonly LabelApplicabilityRule Priority = new()
    {
        Id = "priority-labels", Prefix = "priority:", AllowedTypes = ["Issue"], Exclusive = true,
    };

    private static readonly DependencyIntegrityRule Deps = new() { Id = "deps" };

    private Task<IReadOnlyList<PolicyDecision>> Evaluate(DeclaredPolicy policy, PlanDefinition plan, bool withLinks = true) =>
        new DeclaredPolicyEvaluator(_items, withLinks ? _links : null).EvaluateAsync(policy, plan);

    [Fact]
    public async Task ParentType_AllowedParent_Permits()
    {
        Cache(1, WorkItemType.Task);
        Cache(2, WorkItemType.Issue);

        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(Link("a", 1, "parent", 2)));

        decisions.ShouldHaveSingleItem().Outcome.ShouldBe(PolicyOutcome.Permit);
    }

    [Fact]
    public async Task ParentType_DisallowedParent_Refuses_NamingItemsAndAllowedTypes()
    {
        Cache(1, WorkItemType.Task);
        Cache(2, WorkItemType.Epic);

        var d = (await Evaluate(Policy(TaskUnderIssue), Plan(Link("a", 1, "parent", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.IsBlocking.ShouldBeTrue();
        d.RuleId.ShouldBe("task-parent");
        d.OpId.ShouldBe("a");
        d.WorkItemId.ShouldBe(1);
        d.Reason.ShouldContain("#2");
        d.Reason.ShouldContain("Issue");
    }

    [Fact]
    public async Task ParentType_WarnEnforcement_DowngradesRefuse()
    {
        Cache(1, WorkItemType.Task);
        Cache(2, WorkItemType.Epic);

        var d = (await Evaluate(Policy(TaskUnderIssue with { Enforcement = PolicyEnforcement.Warn }),
            Plan(Link("a", 1, "parent", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Warn);
        d.IsBlocking.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentType_UncachedParent_IsUnavailable_NotPermit()
    {
        Cache(1, WorkItemType.Task);

        var d = (await Evaluate(Policy(TaskUnderIssue), Plan(Link("a", 1, "parent", 99)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Unavailable);
        d.IsBlocking.ShouldBeTrue();
    }

    [Fact]
    public async Task ParentType_TypeChangeOnChild_RejudgesExistingParent()
    {
        Cache(1, WorkItemType.Bug, parent: 2);
        Cache(2, WorkItemType.Epic);

        var decisions = await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("b", 1, ("System.WorkItemType", "Task"))));

        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Refuse
            && d.OpId == "b" && d.WorkItemId == 1);
        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Unavailable
            && d.OpId == "b" && d.WorkItemId == 1);
    }

    [Fact]
    public async Task ParentType_RetypeToDisallowedType_RejudgesExplicitChildAndReportsIncompleteChildSet()
    {
        Cache(1, WorkItemType.Task, parent: 2);
        Cache(2, WorkItemType.Issue);

        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(
            Batch("parent-retype", 2, ("System.WorkItemType", "Epic")),
            Batch("child-touch", 1, ("System.Title", "touched"))));

        var violation = decisions.Single(d => d.Outcome == PolicyOutcome.Refuse);
        violation.RuleId.ShouldBe("task-parent");
        violation.OpId.ShouldBe("parent-retype");
        violation.WorkItemId.ShouldBe(1);
        var unavailable = decisions.Single(d => d.Outcome == PolicyOutcome.Unavailable);
        unavailable.OpId.ShouldBe("parent-retype");
        unavailable.WorkItemId.ShouldBe(2);
    }

    [Fact]
    public async Task ParentType_RetypeToDisallowedType_KnownCachedLeafIsUnavailableNotPermit()
    {
        // No child is present in this local cache, but it carries no complete-child certificate.
        Cache(2, WorkItemType.Issue);

        var d = (await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("parent-retype", 2, ("System.WorkItemType", "Epic"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Unavailable);
        d.OpId.ShouldBe("parent-retype");
        d.WorkItemId.ShouldBe(2);
    }

    [Fact]
    public async Task ParentType_RetypeAndChildRetype_EvaluatesSharedEdgeOnceUnderParentOperation()
    {
        Cache(1, WorkItemType.Bug, parent: 2);
        Cache(2, WorkItemType.Issue);

        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(
            Batch("parent-retype", 2, ("System.WorkItemType", "Epic")),
            Batch("child-retype", 1, ("System.WorkItemType", "Task"))));

        decisions.Count(d => d.Outcome == PolicyOutcome.Refuse && d.WorkItemId == 1).ShouldBe(1);
        decisions.Single(d => d.Outcome == PolicyOutcome.Refuse && d.WorkItemId == 1)
            .OpId.ShouldBe("parent-retype");
        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Unavailable
            && d.WorkItemId == 2 && d.OpId == "parent-retype");
    }

    [Fact]
    public async Task ParentType_RetypeToAllowedType_DoesNotRequireChildSetEvidence()
    {
        Cache(2, WorkItemType.Epic);

        var d = (await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("parent-retype", 2, ("System.WorkItemType", "Issue"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Permit);
    }

    [Fact]
    public async Task ParentType_RetypeAndDeleteSameParent_DoesNotRequireChildSetEvidence()
    {
        Cache(2, WorkItemType.Issue);

        var delete = new DeleteOperation
        {
            Id = "delete-parent",
            WorkItemId = 2,
            ExpectedRevision = 1,
        };
        var d = (await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("parent-retype", 2, ("System.WorkItemType", "Epic")), delete))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Permit);
    }

    [Fact]
    public async Task ParentType_RetypeWithExistingViolation_IsNotDowngradedToD4Warning()
    {
        Cache(1, WorkItemType.Task, parent: 2);
        Cache(2, WorkItemType.Epic);

        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(
            Batch("parent-retype", 2, ("System.WorkItemType", "Feature")),
            Batch("child-touch", 1, ("System.Title", "touched"))));

        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Refuse
            && d.OpId == "parent-retype" && d.WorkItemId == 1);
        decisions.ShouldNotContain(d => d.Outcome == PolicyOutcome.Warn);
        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Unavailable && d.WorkItemId == 2);
    }

    [Fact]
    public async Task ParentType_RetypeAndReparentAway_UsesFinalEdgeAndDoesNotRefuseOldEdge()
    {
        Cache(1, WorkItemType.Task, parent: 2);
        Cache(2, WorkItemType.Issue);
        Cache(3, WorkItemType.Issue);

        var remove = new RemoveLinkOperation
        {
            Id = "remove-old-parent",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Relation = "parent",
            OtherId = 2,
        };
        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(
            Batch("parent-retype", 2, ("System.WorkItemType", "Epic")),
            remove,
            Link("add-new-parent", 1, "parent", 3)));

        decisions.ShouldNotContain(d => d.Outcome == PolicyOutcome.Refuse && d.WorkItemId == 1);
        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Unavailable
            && d.OpId == "parent-retype" && d.WorkItemId == 2);
    }

    [Fact]
    public async Task ParentType_RetypeAndRemoveUnrelatedParentLink_StillChecksExistingEdge()
    {
        Cache(1, WorkItemType.Task, parent: 2);
        Cache(2, WorkItemType.Issue);

        var remove = new RemoveLinkOperation
        {
            Id = "remove-other-parent",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Relation = "parent",
            OtherId = 99,
        };
        var decisions = await Evaluate(Policy(TaskUnderIssue), Plan(
            Batch("parent-retype", 2, ("System.WorkItemType", "Epic")),
            remove));

        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Refuse
            && d.OpId == "parent-retype" && d.WorkItemId == 1);
        decisions.ShouldContain(d => d.Outcome == PolicyOutcome.Unavailable
            && d.OpId == "parent-retype" && d.WorkItemId == 2);
    }

    [Fact]
    public async Task ParentType_UnchangedTypeWithExistingViolation_WarnsRatherThanBlockingRepair()
    {
        Cache(1, WorkItemType.Task, parent: 2);
        Cache(2, WorkItemType.Epic);

        var d = (await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("b", 1, ("System.WorkItemType", "Task"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Warn);
        d.Reason.ShouldContain("Pre-existing");
    }

    [Fact]
    public async Task Labels_NewLabelOnDisallowedType_Refuses()
    {
        Cache(1, WorkItemType.Task);

        var d = (await Evaluate(Policy(Priority), Plan(Batch("b", 1, (Tags, "priority:high"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.Reason.ShouldContain("priority:high");
    }

    [Fact]
    public async Task Labels_SecondExclusiveValue_Refuses()
    {
        Cache(1, WorkItemType.Issue, tags: "priority:high");

        var d = (await Evaluate(Policy(Priority),
            Plan(Batch("b", 1, (Tags, "priority:high; priority:low"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.Reason.ShouldContain("at most one");
    }

    [Fact]
    public async Task Labels_PreExistingViolationNotWorsened_WarnsOnly_D4()
    {
        Cache(1, WorkItemType.Task, tags: "priority:high");

        var d = (await Evaluate(Policy(Priority),
            Plan(Batch("b", 1, (Tags, "priority:high; other"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Warn);
        d.Reason.ShouldContain("Pre-existing");
    }

    [Fact]
    public async Task Labels_TypeChangeIntoDisallowedType_Refuses()
    {
        Cache(1, WorkItemType.Issue, tags: "priority:high");

        var d = (await Evaluate(Policy(Priority),
            Plan(Batch("b", 1, ("System.WorkItemType", "Task"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
    }

    [Fact]
    public async Task Labels_UncachedItem_IsUnavailable()
    {
        var d = (await Evaluate(Policy(Priority), Plan(Batch("b", 7, (Tags, "priority:high"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Unavailable);
    }

    [Fact]
    public async Task Dependencies_SelfLink_Refuses()
    {
        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 1)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.Reason.ShouldContain("itself");
    }

    [Fact]
    public async Task Dependencies_NewAcyclicEdge_Permits()
    {
        _verified.UnionWith([1, 2]);

        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Permit);
    }

    [Fact]
    public async Task Dependencies_ClosingACycle_Refuses()
    {
        _verified.UnionWith([1, 2, 3]);
        _edges.Add(new WorkItemLink(2, 3, LinkTypes.Successor));
        _edges.Add(new WorkItemLink(3, 1, LinkTypes.Successor));

        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.Reason.ShouldContain("cycle");
    }

    [Fact]
    public async Task Dependencies_ExistingEdge_Refuses()
    {
        _verified.UnionWith([1, 2]);
        _edges.Add(new WorkItemLink(1, 2, LinkTypes.Successor));

        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.Reason.ShouldContain("already exists");
    }

    [Fact]
    public async Task Dependencies_UnverifiedLinks_IsUnavailable()
    {
        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 2)))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Unavailable);
    }

    [Fact]
    public async Task Dependencies_NoLinkRepository_IsUnavailable_NotPermit()
    {
        var d = (await Evaluate(Policy(Deps), Plan(Link("a", 1, "successor", 2)), withLinks: false)).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Unavailable);
    }

    [Fact]
    public async Task EmptyPolicy_ProducesNoDecisions()
    {
        (await Evaluate(Policy(), Plan(Link("a", 1, "parent", 2)))).ShouldBeEmpty();
    }

    [Fact]
    public async Task V1Decisions_AreUnchangedWhenProviderSnapshotReaderIsRegistered()
    {
        Cache(1, WorkItemType.Task);
        var plan = Plan(Batch("labels", 1, (Tags, "priority:high")));
        var policy = Policy(Priority);
        var reader = Substitute.For<IWorkItemSnapshotReader>();

        var withoutProvider = await new DeclaredPolicyEvaluator(_items, _links).EvaluateAsync(policy, plan);
        var withProvider = await new DeclaredPolicyEvaluator(_items, _links, reader).EvaluateAsync(policy, plan);

        withProvider.ShouldHaveSingleItem().ShouldBe(withoutProvider.ShouldHaveSingleItem());
        await reader.DidNotReceiveWithAnyArgs().FetchSnapshotAsync(default!, default);
    }

    [Fact]
    public async Task ProviderLabels_UnavailableSnapshotBlocksInsteadOfTreatingItAsEmpty()
    {
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_offline");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(identity, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ProviderWorkItemSnapshot>(new HttpRequestException("offline")));
        var operation = ProviderBatch("labels", identity, ("System.Tags", "priority:high"));

        var decision = (await new DeclaredPolicyEvaluator(_items, null, reader)
            .EvaluateAsync(Policy(Priority), ProviderPlan(operation))).ShouldHaveSingleItem();

        decision.Outcome.ShouldBe(PolicyOutcome.Unavailable);
        decision.ProviderIdentity.ShouldBe(identity);
        decision.Reason.ShouldContain("provider request failed");
        decision.Reason.ShouldContain("not exact-revision apply authority");
    }

    [Fact]
    public async Task ProviderLabels_UseTypedObservedFactsAndExplainPreviewOnlyDecision()
    {
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_issue");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(identity, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(identity, "Task", ["bug"]));
        var operation = ProviderBatch("labels", identity, ("System.Tags", "priority:high"));

        var decision = (await new DeclaredPolicyEvaluator(_items, null, reader)
            .EvaluateAsync(Policy(Priority), ProviderPlan(operation))).ShouldHaveSingleItem();

        decision.Outcome.ShouldBe(PolicyOutcome.Refuse);
        decision.OpId.ShouldBe("labels");
        decision.WorkItemId.ShouldBeNull();
        decision.ProviderIdentity.ShouldBe(identity);
        decision.Reason.ShouldContain("I_issue");
        decision.Reason.ShouldContain("Observed at preview");
        decision.Reason.ShouldContain("not exact-revision apply authority");
    }

    [Fact]
    public async Task ProviderLabels_DistinguishKnownEmptyOverlayFromAmbiguousScalar()
    {
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_issue");
        var evaluator = new DeclaredPolicyEvaluator(_items, null, providerSnapshots: null);

        var empty = (await evaluator.EvaluateAsync(
            Policy(Priority), ProviderPlan(ProviderBatch("clear", identity, ("System.Tags", null)))))
            .ShouldHaveSingleItem();
        var ambiguous = (await evaluator.EvaluateAsync(
            Policy(Priority), ProviderPlan(ProviderBatch("ambiguous", identity, ("System.Tags", "priority:a;priority:b")))))
            .ShouldHaveSingleItem();

        empty.Outcome.ShouldBe(PolicyOutcome.Permit);
        empty.Reason.ShouldContain("Observed at preview");
        ambiguous.Outcome.ShouldBe(PolicyOutcome.Unavailable);
        ambiguous.Reason.ShouldContain("without splitting a provider value");
    }

    [Theory]
    [InlineData(true, PolicyOutcome.Permit)]
    [InlineData(false, PolicyOutcome.Unavailable)]
    public async Task ProviderDependencies_RequireExplicitlyCompleteRelationshipGraph(
        bool complete, PolicyOutcome expected)
    {
        var source = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_source");
        var target = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_target");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(source, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(source, "Issue", [], complete));
        reader.FetchSnapshotAsync(target, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(target, "Issue", [], complete));
        var operation = new ProviderMutationOperation
        {
            Id = "dependency",
            OperationKind = PlanOperationKind.AddLink,
            Target = ProviderTarget(source),
            Other = ProviderReference(target),
            Relation = "successor",
        };

        var decision = (await new DeclaredPolicyEvaluator(_items, null, reader)
            .EvaluateAsync(Policy(Deps), ProviderPlan(operation))).ShouldHaveSingleItem();

        decision.Outcome.ShouldBe(expected);
        decision.Reason.ShouldContain("Observed at preview");
    }

    [Theory]
    [InlineData(true, PolicyOutcome.Refuse)]
    [InlineData(false, PolicyOutcome.Unavailable)]
    public async Task ProviderParentType_RetypeRechecksExistingParentOnlyWithCompleteRelationships(
        bool complete, PolicyOutcome expected)
    {
        var child = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_child");
        var parent = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_parent");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(child, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(child, "Bug", [], complete,
                [new ProviderWorkItemRelationship(parent, child, LinkKind.ParentChild)]));
        reader.FetchSnapshotAsync(parent, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(parent, "Epic", []));
        var operation = ProviderBatch("retype", child, ("System.WorkItemType", "Task"));

        var decision = (await new DeclaredPolicyEvaluator(_items, null, reader)
            .EvaluateAsync(Policy(TaskUnderIssue), ProviderPlan(operation))).ShouldHaveSingleItem();

        decision.Outcome.ShouldBe(expected);
        decision.ProviderIdentity.ShouldBe(child);
        decision.OpId.ShouldBe("retype");
        decision.Reason.ShouldContain("Observed at preview");
    }

    [Theory]
    [InlineData(false, PolicyOutcome.Refuse)]
    [InlineData(true, PolicyOutcome.Permit)]
    public async Task ProviderParentType_RetypeOnlyExcludesTheExactRemovedParent(
        bool removeActualParent, PolicyOutcome expected)
    {
        var child = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_child");
        var actualParent = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_actual_parent");
        var removedParent = removeActualParent
            ? actualParent
            : new ExternalWorkItemIdentity("github", "https://api.github.com", "I_other_parent");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(child, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(child, "Bug", [], relationships:
                [new ProviderWorkItemRelationship(actualParent, child, LinkKind.ParentChild)]));
        reader.FetchSnapshotAsync(actualParent, Arg.Any<CancellationToken>())
            .Returns(ProviderSnapshot(actualParent, "Epic", []));
        var retype = ProviderBatch("retype", child, ("System.WorkItemType", "Task"));
        var remove = new ProviderMutationOperation
        {
            Id = "remove-parent",
            OperationKind = PlanOperationKind.RemoveLink,
            Target = ProviderTarget(child),
            Other = ProviderReference(removedParent),
            Relation = "parent",
        };

        var decision = (await new DeclaredPolicyEvaluator(_items, null, reader)
            .EvaluateAsync(Policy(TaskUnderIssue), ProviderPlan(retype, remove))).ShouldHaveSingleItem();

        decision.Outcome.ShouldBe(expected);
        decision.ProviderIdentity.ShouldBe(expected == PolicyOutcome.Refuse ? child : null);
    }

    private static PlanDefinition ProviderPlan(params PlanOperationDefinition[] operations) => new()
    {
        Version = 2,
        Workspace = new PlanWorkspace { Organization = "acme", Project = "widgets" },
        Operations = operations,
    };

    private static ProviderMutationOperation ProviderBatch(
        string id, ExternalWorkItemIdentity identity, params (string Key, string? Value)[] fields) => new()
    {
        Id = id,
        OperationKind = PlanOperationKind.Batch,
        Target = ProviderTarget(identity),
        Fields = fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
    };

    private static ProviderWorkItemMutationTarget ProviderTarget(ExternalWorkItemIdentity identity) => new()
    {
        Item = ProviderReference(identity),
        ExpectedProviderRevision = new ProviderRevisionToken("opaque-token"),
    };

    private static ProviderWorkItemReference ProviderReference(ExternalWorkItemIdentity identity) => new()
    {
        LocalId = LocalWorkItemId.New(),
        ExternalIdentity = identity,
    };

    private static ProviderWorkItemSnapshot ProviderSnapshot(
        ExternalWorkItemIdentity identity,
        string? type,
        IReadOnlyList<string> tags,
        bool complete = true,
        IReadOnlyList<ProviderWorkItemRelationship>? relationships = null)
    {
        var fields = FieldValues.Empty.Set("System.Tags", new StringListValue(tags));
        if (type is not null)
            fields = fields.Set("System.WorkItemType", new TextValue(type));
        return new ProviderWorkItemSnapshot(identity, null, fields, relationships ?? [], complete);
    }
}
