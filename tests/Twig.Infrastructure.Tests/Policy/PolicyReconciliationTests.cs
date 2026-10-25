using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Policy;

/// <summary>Real cache, link verification, assessment and native proposal parser composition.</summary>
public sealed class PolicyReconciliationTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteWorkItemRepository _items;
    private readonly SqliteWorkItemLinkRepository _links;
    private readonly PolicyReconciliationService _service;
    private static readonly PlanWorkspace Workspace = new() { Organization = "sample", Project = "project" };
    private static readonly ParentTypeRule Parent = new()
    {
        Id = "parent", ChildType = "Leaf", AllowedParentTypes = ["Container"],
    };
    private static readonly LabelApplicabilityRule Label = new()
    {
        Id = "labels", Prefix = "mode:", AllowedTypes = ["Leaf"], Exclusive = true,
    };
    private static readonly DependencyIntegrityRule Dependencies = new() { Id = "dependencies" };

    public PolicyReconciliationTests()
    {
        _items = new(_store, new WorkItemMapper());
        _links = new(_store);
        _service = new(_items, _links);
    }

    public void Dispose() => _store.Dispose();

    private async Task Cache(int id, string type = "Leaf", int? parent = null,
        string? tags = "", bool includeTags = true, bool verifiedLinks = true)
    {
        var item = new WorkItem
        {
            Id = id, Type = WorkItemType.Parse(type).Value, ParentId = parent, Title = $"Sample item {id}",
            LastSyncedAt = DateTimeOffset.UnixEpoch,
        };
        if (includeTags) item.UpdateField("System.Tags", tags);
        item.MarkSynced(7);
        await _items.SaveAsync(item);
        if (verifiedLinks) await _links.SaveLinksAsync(id, []);
    }

    private static DeclaredPolicy Policy(params DeclaredPolicyRule[] rules) => new() { Rules = rules };

    private async Task<PolicyRepairResult> Repair(DeclaredPolicy policy, int[] ids, PolicyAssessment assessment,
        string finding, string strategy, int? parent = null) =>
        await _service.ProposeAsync(policy, ids,
            new(assessment.EvidenceDigest, finding, strategy, parent), Workspace);

    [Fact]
    public async Task CompliantControl_ReportsExactCachedScopeAndVerifiedEmptyLinks()
    {
        await Cache(1, parent: 2);
        await Cache(2, "Container");
        var result = await _service.AssessAsync(Policy(Parent, Label, Dependencies), [1]);
        result.Compliant.ShouldBeTrue();
        result.Complete.ShouldBeTrue();
        result.Scope.ShouldBe([1]);
        result.Freshness.ShouldBe("cached");
        result.Items.Single(i => i.WorkItemId == 1).LinksVerifiedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingFieldOrUnverifiedEmptyLinks_IsUnavailable(bool includeTags, bool verifiedLinks)
    {
        await Cache(1, includeTags: includeTags, verifiedLinks: verifiedLinks);
        var result = await _service.AssessAsync(Policy(Label, Dependencies), [1]);
        result.Status.ShouldBe("unavailable");
        result.Compliant.ShouldBeFalse();
        result.Findings.ShouldContain(f => f.Code == PolicyFindingCode.EvidenceUnavailable);
        result.Findings.SelectMany(f => f.Strategies).ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingItemOrParent_IsUnavailable_NotCompliant()
    {
        await Cache(1, parent: 99);
        var result = await _service.AssessAsync(Policy(Parent), [1, 98]);
        result.Complete.ShouldBeFalse();
        result.Findings.SelectMany(f => f.Evidence.MissingIds).ShouldContain(99);
        result.Findings.SelectMany(f => f.Evidence.MissingIds).ShouldContain(98);
    }

    [Fact]
    public async Task ParentAlternatives_RequireExplicitTargetAndRenderRevisionBoundNativeOperations()
    {
        await Cache(1, parent: 2);
        await Cache(2, "Other");
        await Cache(3, "Container");
        var policy = Policy(Parent, Label, Dependencies);
        var assessment = await _service.AssessAsync(policy, [1, 3]);
        var finding = assessment.Findings.ShouldHaveSingleItem();
        finding.Evidence.ParentId.ShouldBe(2);
        finding.Strategies.Select(s => s.Id).ShouldBe(["detach-parent", "reparent"]);
        (await Repair(policy, [1, 3], assessment, finding.Id, "reparent")).CanPropose.ShouldBeFalse();
        var result = await Repair(policy, [1, 3], assessment, finding.Id, "reparent", 3);
        result.CanPropose.ShouldBeTrue(result.Error);
        result.Projected!.Compliant.ShouldBeTrue();
        result.Before.Compliant.ShouldBeFalse();
        var remove = result.Proposal!.Operations[0].ShouldBeOfType<RemoveLinkOperation>();
        var add = result.Proposal.Operations[1].ShouldBeOfType<AddLinkOperation>();
        remove.ExpectedRevision.ShouldBe(7);
        remove.OtherId.ShouldBe(2);
        add.ExpectedRevision.ShouldBe(8);
        add.OtherId.ShouldBe(3);
        new PlanDocumentParser().Parse(PlanDocumentWriter.Write(result.Proposal)).IsValid.ShouldBeTrue();
        (await _items.GetByIdAsync(1))!.ParentId.ShouldBe(2, "proposal authoring must not mutate the cache");
    }

    [Fact]
    public async Task Reparent_ReassessesAllRules_AndRefusesCrossRuleConflict()
    {
        await Cache(1, parent: 2);
        await Cache(2, "Other");
        await Cache(3, "Container");
        var policy = Policy(Parent, Parent with { Id = "other-parent", AllowedParentTypes = ["Other"] });
        var assessment = await _service.AssessAsync(policy, [1, 3]);
        var result = await Repair(policy, [1, 3], assessment, assessment.Findings.Single().Id, "reparent", 3);
        result.CanPropose.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("another declared rule");
        result.Projected!.Findings.ShouldContain(f => f.RuleId == "other-parent");
    }

    [Fact]
    public async Task Reparent_RefusesHierarchyCycle()
    {
        await Cache(1, parent: 2);
        await Cache(2, "Other");
        await Cache(3, "Container", parent: 1);
        var policy = Policy(Parent);
        var assessment = await _service.AssessAsync(policy, [1, 3]);
        var result = await Repair(policy, [1, 3], assessment, assessment.Findings.Single().Id, "reparent", 3);
        result.Error.ShouldNotBeNull().ShouldContain("hierarchy cycle");
    }

    [Fact]
    public async Task Labels_RetainExplicitValue_PreservesUnrelatedLabels_AndLeavesOtherFindingsVisible()
    {
        await Cache(1, tags: "mode:a; unrelated; mode:b");
        await Cache(2, "Other", tags: "mode:c");
        var policy = Policy(Label);
        var assessment = await _service.AssessAsync(policy, [1, 2]);
        var exclusive = assessment.Findings.Single(f => f.Code == PolicyFindingCode.ExclusiveLabels);
        var chosen = exclusive.Strategies.Single(s => s.RetainLabel == "mode:b");
        var result = await Repair(policy, [1, 2], assessment, exclusive.Id, chosen.Id);
        result.CanPropose.ShouldBeTrue(result.Error);
        result.Projected!.Status.ShouldBe("noncompliant", "one repair is not a whole-scope compliance claim");
        var batch = result.Proposal!.Operations.Single().ShouldBeOfType<BatchOperation>();
        batch.Fields["System.Tags"].ShouldBe("unrelated; mode:b");
        var decisions = await new DeclaredPolicyEvaluator(_items, _links).EvaluateAsync(policy, result.Proposal);
        decisions.Any(d => d.IsBlocking).ShouldBeFalse();
    }

    [Fact]
    public async Task WarnRule_ViolationIsStillNoncompliance_AndCanBeRepaired()
    {
        await Cache(1, "Other", tags: "mode:a; untouched");
        var policy = Policy(Label with { Enforcement = PolicyEnforcement.Warn });
        var assessment = await _service.AssessAsync(policy, [1]);
        assessment.Compliant.ShouldBeFalse();
        var result = await Repair(policy, [1], assessment, assessment.Findings.Single().Id, "remove-labels");
        result.Projected!.Compliant.ShouldBeTrue();
        result.Proposal!.Operations.Single().ShouldBeOfType<BatchOperation>().Fields["System.Tags"].ShouldBe("untouched");
    }

    [Fact]
    public async Task CycleWitness_OffersEveryWitnessedEdge_NoDefaultChoice_UsesExistingRemoveLink()
    {
        await Cache(1);
        await Cache(2);
        await Cache(3);
        await _links.SaveLinksAsync(1, [new(1, 2, LinkTypes.Successor), new(1, 3, LinkTypes.Predecessor)]);
        await _links.SaveLinksAsync(2, [new(2, 3, LinkTypes.Successor), new(2, 1, LinkTypes.Predecessor)]);
        await _links.SaveLinksAsync(3, [new(3, 1, LinkTypes.Successor), new(3, 2, LinkTypes.Predecessor)]);
        var policy = Policy(Dependencies);
        var assessment = await _service.AssessAsync(policy, [1]);
        assessment.Complete.ShouldBeTrue();
        var cycle = assessment.Findings.ShouldHaveSingleItem();
        cycle.Code.ShouldBe(PolicyFindingCode.DependencyCycle);
        cycle.Evidence.Edges.Count.ShouldBe(3);
        cycle.Strategies.Count.ShouldBe(3, "mirrored inverse relations are not duplicate dependencies");
        foreach (var choice in cycle.Strategies)
        {
            var result = await Repair(policy, [1], assessment, cycle.Id, choice.Id);
            result.CanPropose.ShouldBeTrue(result.Error);
            result.Projected!.Compliant.ShouldBeTrue();
            result.Proposal!.Operations.Single().ShouldBeOfType<RemoveLinkOperation>().ExpectedRevision.ShouldBe(7);
        }
    }

    [Fact]
    public async Task SelfDependency_CanBeRemovedWithoutABypass()
    {
        await Cache(1);
        await _links.SaveLinksAsync(1, [new(1, 1, LinkTypes.Successor)]);
        var policy = Policy(Dependencies);
        var assessment = await _service.AssessAsync(policy, [1]);
        var finding = assessment.Findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(PolicyFindingCode.SelfDependency);
        var result = await Repair(policy, [1], assessment, finding.Id, finding.Strategies.Single().Id);
        result.Projected!.Compliant.ShouldBeTrue();
        var decisions = await new DeclaredPolicyEvaluator(_items, _links).EvaluateAsync(policy, result.Proposal!);
        decisions.Any(d => d.IsBlocking).ShouldBeFalse();
    }

    [Fact]
    public async Task GraphLimitAndUnverifiedFrontier_PreserveUnavailableAlongsideDefiniteCycle()
    {
        await Cache(1);
        await Cache(2, verifiedLinks: false);
        await _links.SaveLinksAsync(1, [new(1, 1, LinkTypes.Successor), new(1, 2, LinkTypes.Successor)]);
        var policy = Policy(Dependencies);
        var result = await _service.AssessAsync(policy, [1]);
        result.Complete.ShouldBeFalse();
        result.Findings.ShouldContain(f => f.Code == PolicyFindingCode.SelfDependency);
        result.Findings.ShouldContain(f => f.Code == PolicyFindingCode.EvidenceUnavailable);
        var definite = result.Findings.First(f => f.Code == PolicyFindingCode.SelfDependency);
        (await Repair(policy, [1], result, definite.Id, definite.Strategies.Single().Id)).CanPropose.ShouldBeFalse();
        (await _service.AssessAsync(Policy(Dependencies with { MaxNodes = 1 }), [1])).Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangedObservationOrPolicy_RefusesOldChoice()
    {
        await Cache(1, "Other", tags: "mode:a");
        var policy = Policy(Label);
        var before = await _service.AssessAsync(policy, [1]);
        var finding = before.Findings.Single();
        await Cache(1, "Other", tags: "mode:b");
        (await Repair(policy, [1], before, finding.Id, "remove-labels")).Error.ShouldNotBeNull().ShouldContain("changed");
        await Cache(1, "Other", tags: "mode:a");
        (await Repair(Policy(Label with { Prefix = "different:" }), [1], before, finding.Id, "remove-labels"))
            .Error.ShouldNotBeNull().ShouldContain("changed");
    }

    [Fact]
    public async Task AlternativesAreBoundedAndExplicitlyMarked()
    {
        await Cache(1, tags: string.Join("; ", Enumerable.Range(1, 50).Select(i => $"mode:{i}")));
        var result = await _service.AssessAsync(Policy(Label), [1]);
        var finding = result.Findings.Single();
        finding.Strategies.Count.ShouldBe(PolicyReconciliationService.MaxStrategies);
        finding.StrategiesTruncated.ShouldBeTrue();
        result.Complete.ShouldBeTrue("strategy enumeration limits do not hide evidence");
    }
}
