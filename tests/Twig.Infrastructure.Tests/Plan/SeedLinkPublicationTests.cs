using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

public sealed class SeedLinkPublicationTests
{
    [Theory]
    [InlineData(false, "missing", false)]
    [InlineData(true, "missing", false)]
    [InlineData(false, "wrong", false)]
    [InlineData(true, "wrong", false)]
    [InlineData(false, "raw", true)]
    [InlineData(true, "raw", true)]
    [InlineData(false, "friendly", true)]
    [InlineData(true, "friendly", true)]
    [InlineData(false, "promotion-failed", false)]
    [InlineData(true, "promotion-failed", false)]
    public async Task SecondEndpointPublication_VerifiesThePromotedEdgeInEitherOrder(
        bool targetFirst, string evidence, bool expectedVerified)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var items = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var links = new SqliteSeedLinkRepository(store);
        var registry = new SqliteStagedIdentityRegistry(store);
        var map = new SqlitePublishIdMapRepository(store, registry);
        var intents = new SqlitePublishIntentRepository(store);
        var fields = new SqliteFieldDefinitionStore(store);
        var pending = new SqlitePendingChangeStore(store);
        var ado = Substitute.For<IAdoWorkItemService>();
        var rules = Substitute.For<ISeedPublishRulesProvider>();
        rules.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);
        var orchestrator = new SeedPublishOrchestrator(items, ado, links,
            new SqliteWorkItemLinkRepository(store), map, rules, new SqliteUnitOfWork(store),
            new BacklogOrderer(ado, fields), pending, intents, ReferenceProfileBuilder.SprintPolicy());
        var publisher = new PlanSeedPublisher(ado, items, links, registry, map, intents,
            (id, ct) => orchestrator.PublishAsync(id, ct: ct),
            (id, ct) => orchestrator.RecoverPublishedAsync(id, ct));
        var source = await registry.MintAsync();
        var target = await registry.MintAsync();
        foreach (var staged in new[] { source, target })
            await items.SaveAsync(new WorkItem
            {
                Id = staged.Alias.Value, StagedIdentity = staged.Identity, IsSeed = true,
                Type = WorkItemType.Task, State = "New", Title = staged.Identity.ToString(),
            });
        await links.AddLinkAsync(new SeedLink(source.Alias.Value, target.Alias.Value,
            SeedLinkTypes.BlockedBy, DateTimeOffset.UnixEpoch));
        var order = targetFirst ? new[] { target, source } : new[] { source, target };
        var operations = new List<PublishSeedOperation>();
        foreach (var staged in order)
        {
            var seed = (await items.GetByIdAsync(staged.Alias.Value))!;
            var fingerprint = await SeedFingerprintCalculator.ComputeAsync(seed,
                await links.GetLinksForItemAsync(seed.Id), registry, map);
            operations.Add(new PublishSeedOperation
            {
                Id = staged.Identity.ToString(), StagedIdentity = staged.Identity,
                ExpectedFingerprint = fingerprint,
            });
        }
        var sourceId = targetFirst ? 102 : 101;
        var targetId = targetFirst ? 101 : 102;
        var promoted = false;
        var remoteItems = new Dictionary<int, WorkItem>();
        ado.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.ArgAt<CreateWorkItemRequest>(0);
            var id = request.Title == source.Identity.ToString() ? sourceId : targetId;
            var item = new WorkItem { Id = id, Title = request.Title, Type = WorkItemType.Task, State = "New" };
            item.MarkSynced(1);
            remoteItems.Add(id, item);
            return id;
        });
        ado.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => remoteItems[call.ArgAt<int>(0)]);
        ado.AddLinkAsync(sourceId, targetId, "System.LinkTypes.Dependency-Reverse", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (evidence == "promotion-failed")
                    throw new InvalidOperationException("Provider refused the dependency");
                promoted = true;
                return Task.CompletedTask;
            });
        ado.FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.ArgAt<int>(0);
            var relation = id == sourceId ? "System.LinkTypes.Dependency-Reverse" : "System.LinkTypes.Dependency-Forward";
            if (evidence == "wrong")
                relation = id == sourceId ? "System.LinkTypes.Dependency-Forward" : "System.LinkTypes.Dependency-Reverse";
            if (evidence == "friendly")
                relation = id == sourceId ? "Predecessor" : "Successor";
            IReadOnlyList<WorkItemLink> observed = promoted && evidence != "missing"
                ? [new WorkItemLink(id, id == sourceId ? targetId : sourceId, relation)] : [];
            return (remoteItems[id], observed);
        });

        var first = await publisher.ExecuteAsync(operations[0], CancellationToken.None);
        first.Outcome.ShouldBe(PlanExecutionOutcome.Applied, first.Error);
        promoted.ShouldBeFalse("the other endpoint is still unpublished");
        var deferred = (await links.GetLinksForItemAsync(101)).Single();
        Math.Min(deferred.SourceId, deferred.TargetId).ShouldBeLessThan(0);
        Math.Max(deferred.SourceId, deferred.TargetId).ShouldBe(101);
        (await publisher.ReadbackAsync(operations[0], first, CancellationToken.None)).Ok.ShouldBeTrue();

        var second = await publisher.ExecuteAsync(operations[1], CancellationToken.None);
        second.Outcome.ShouldBe(evidence == "promotion-failed"
            ? PlanExecutionOutcome.Indeterminate : PlanExecutionOutcome.Applied, second.Error);
        promoted.ShouldBe(evidence != "promotion-failed");
        (await links.GetLinksForItemAsync(102)).Single().ShouldBe(
            new SeedLink(sourceId, targetId, SeedLinkTypes.BlockedBy, DateTimeOffset.UnixEpoch));
        await ado.Received(1).AddLinkAsync(sourceId, targetId,
            "System.LinkTypes.Dependency-Reverse", Arg.Any<CancellationToken>());
        var readback = await publisher.ReadbackAsync(operations[1], second, CancellationToken.None);
        readback.Ok.ShouldBe(expectedVerified);
        if (!expectedVerified)
        {
            readback.Deterministic.ShouldBeFalse();
            readback.Error.ShouldNotBeNull().ShouldContain("missing");
        }
    }
}
