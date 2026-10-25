using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;
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

        var d = (await Evaluate(Policy(TaskUnderIssue),
            Plan(Batch("b", 1, ("System.WorkItemType", "Task"))))).ShouldHaveSingleItem();

        d.Outcome.ShouldBe(PolicyOutcome.Refuse);
        d.OpId.ShouldBe("b");
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
}
