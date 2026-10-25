using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Policy;

public sealed class PolicyReconciliationServiceTests
{
    private readonly IWorkItemRepository _items = Substitute.For<IWorkItemRepository>();
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly Dictionary<int, WorkItem> _cache = [];
    private readonly List<WorkItemLink> _edges = [];
    private readonly PolicyReconciliationService _service;
    private static readonly PlanWorkspace Workspace = new() { Organization = "sample", Project = "project" };
    private static readonly DeclaredPolicy Policy = new()
    {
        Rules = [new DependencyIntegrityRule { Id = "dependencies" }],
    };

    public PolicyReconciliationServiceTests()
    {
        _service = new(_items, _links);
        _items.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<WorkItem>)ci.Arg<IEnumerable<int>>()
                .Where(_cache.ContainsKey).Select(id => _cache[id]).ToArray());
        _links.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<int, DateTimeOffset>)ci.Arg<IReadOnlyList<int>>()
                .ToDictionary(id => id, _ => DateTimeOffset.UnixEpoch));
        _links.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<WorkItemLink>)_edges
                .Where(edge => ci.Arg<IReadOnlyList<int>>().Contains(edge.SourceId)).ToArray());
        _links.GetIncomingDependencySourcesAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<int>)_edges
                .Where(edge => ci.Arg<IReadOnlyList<int>>().Contains(edge.TargetId))
                .Select(edge => edge.SourceId).Distinct().Order().Take(ci.Arg<int>()).ToArray());
    }

    private void Cache(int id, string tags = "")
    {
        var item = new WorkItem { Id = id, Type = WorkItemType.Task, LastSyncedAt = DateTimeOffset.UnixEpoch };
        item.UpdateField("System.Tags", tags);
        item.MarkSynced(1);
        _cache[id] = item;
    }

    [Fact]
    public async Task PreservedPhysicalDuplicates_OfferExplicitWholeDependencyRemoval()
    {
        Cache(1); Cache(2);
        _edges.AddRange([new(1, 2, LinkTypes.Successor), new(1, 2, LinkTypes.Successor),
            new(2, 1, LinkTypes.Predecessor)]);
        var assessment = await _service.AssessAsync(Policy, [1]);
        var finding = assessment.Findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(PolicyFindingCode.DuplicateDependency);
        finding.Evidence.Count.ShouldBe(2);
        var result = await _service.ProposeAsync(Policy, [1],
            new(assessment.EvidenceDigest, finding.Id, finding.Strategies.Single().Id), Workspace);
        result.CanPropose.ShouldBeTrue(result.Error);
        result.Projected!.Compliant.ShouldBeTrue();
        result.Proposal!.Operations.Single().ShouldBeOfType<RemoveLinkOperation>();
    }

    [Fact]
    public async Task PartialCycleRepair_DoesNotMistakeADifferentWitnessForANewViolation()
    {
        Cache(1); Cache(2); Cache(3);
        _edges.AddRange([
            new(1, 2, LinkTypes.Successor), new(2, 1, LinkTypes.Successor),
            new(2, 3, LinkTypes.Successor), new(3, 1, LinkTypes.Successor),
            new(2, 1, LinkTypes.Predecessor), new(1, 2, LinkTypes.Predecessor),
            new(3, 2, LinkTypes.Predecessor), new(1, 3, LinkTypes.Predecessor),
        ]);
        var assessment = await _service.AssessAsync(Policy, [1]);
        var finding = assessment.Findings.First(f => f.Evidence.Edges.Count == 2);
        var choice = finding.Strategies.Single(s => s.RemoveEdge == new PolicyDependencyEdge(2, 1));
        var result = await _service.ProposeAsync(Policy, [1],
            new(assessment.EvidenceDigest, finding.Id, choice.Id), Workspace);
        result.CanPropose.ShouldBeTrue(result.Error);
        result.Projected!.Status.ShouldBe("noncompliant");
    }

    [Fact]
    public async Task ObservationChangesDuringRendering_RefusesEvenWhenInitialDigestMatches()
    {
        Cache(1, "mode:a; mode:b");
        var policy = new DeclaredPolicy
        {
            Rules = [new LabelApplicabilityRule
            {
                Id = "labels", Prefix = "mode:", AllowedTypes = ["Task"], Exclusive = true,
            }],
        };
        var assessment = await _service.AssessAsync(policy, [1]);
        var loads = 0;
        _items.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++loads == 2) Cache(1, "mode:c");
                return (IReadOnlyList<WorkItem>)new[] { _cache[1] };
            });
        var result = await _service.ProposeAsync(policy, [1],
            new(assessment.EvidenceDigest, "f1", "remove-labels"), Workspace);
        result.CanPropose.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("while rendering");
    }

    [Fact]
    public async Task FieldOverlayUsesCaseInsensitiveReferenceNamesAcrossAllRules()
    {
        Cache(1, "mode:a; mode:b; mode:special");
        var rule = new LabelApplicabilityRule
        {
            Id = "labels", Prefix = "mode:", AllowedTypes = ["Task"], Exclusive = true, Field = "system.tags",
        };
        var policy = new DeclaredPolicy { Rules = [rule, rule with { Id = "second", Field = "SYSTEM.TAGS" }] };
        var assessment = await _service.AssessAsync(policy, [1]);
        var result = await _service.ProposeAsync(policy, [1],
            new(assessment.EvidenceDigest, "f1", "remove-labels"), Workspace);
        result.CanPropose.ShouldBeTrue(result.Error);
        result.Projected!.Compliant.ShouldBeTrue();
    }

    [Fact]
    public async Task MissingRepository_IsUnavailable_NotVerifiedEmpty()
    {
        Cache(1);
        var assessment = await new PolicyReconciliationService(_items, null).AssessAsync(Policy, [1]);
        assessment.Status.ShouldBe("unavailable");
        assessment.Findings.Single().Code.ShouldBe(PolicyFindingCode.EvidenceUnavailable);
    }

    [Fact]
    public async Task DuplicateLabelTokens_AgreeWithNativeEnforcement_AndRetainExactlyOne()
    {
        Cache(1, "mode:a; mode:A; untouched; untouched");
        var policy = new DeclaredPolicy
        {
            Rules = [new LabelApplicabilityRule
            {
                Id = "labels", Prefix = "mode:", AllowedTypes = ["Task"], Exclusive = true,
            }],
        };
        var assessment = await _service.AssessAsync(policy, [1]);
        var finding = assessment.Findings.Single();
        finding.Code.ShouldBe(PolicyFindingCode.ExclusiveLabels);
        var choice = finding.Strategies.Single(s => s.RetainLabel is not null);
        var result = await _service.ProposeAsync(policy, [1],
            new(assessment.EvidenceDigest, finding.Id, choice.Id), Workspace);
        result.CanPropose.ShouldBeTrue(result.Error);
        var batch = result.Proposal!.Operations.Single().ShouldBeOfType<BatchOperation>();
        batch.Fields["System.Tags"].ShouldBe("mode:a; untouched; untouched");
        result.Projected!.Compliant.ShouldBeTrue();
        (await new DeclaredPolicyEvaluator(_items, _links).EvaluateAsync(policy, result.Proposal))
            .Any(d => d.IsBlocking).ShouldBeFalse();
    }
}
