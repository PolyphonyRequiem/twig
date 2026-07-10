using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SeedParentLinkPublishingTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");

    [Fact]
    public async Task ParentChildLinkToExistingItem_PublishesParentInCreateRequest()
    {
        var workItemRepo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
        var workItemLinkRepo = new SqliteWorkItemLinkRepository(_store);
        var seedLinkRepo = new SqliteSeedLinkRepository(_store);
        var publishIdMapRepo = new SqlitePublishIdMapRepository(_store);
        var unitOfWork = new SqliteUnitOfWork(_store);
        var adoService = Substitute.For<IAdoWorkItemService>();
        var rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        var fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        await workItemRepo.SaveAsync(new WorkItemBuilder(100, "Existing parent").AsEpic().Build());
        await workItemRepo.SaveAsync(new WorkItemBuilder(-1, "Child seed").AsTask().AsSeed().Build());

        var linkCommand = new SeedLinkCommand(
            seedLinkRepo,
            workItemRepo,
            new OutputFormatterFactory(new HumanOutputFormatter()));
        var linkResult = await linkCommand.LinkAsync(-1, 100, SeedLinkTypes.ParentChild);
        linkResult.ShouldBe(0);

        CreateWorkItemRequest? createRequest = null;
        adoService.CreateAsync(Arg.Do<CreateWorkItemRequest>(request => createRequest = request), Arg.Any<CancellationToken>())
            .Returns(500);
        adoService.FetchAsync(500, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(500, "Child seed").AsTask().WithParent(100).Build());

        var orchestrator = new SeedPublishOrchestrator(
            workItemRepo,
            adoService,
            seedLinkRepo,
            workItemLinkRepo,
            publishIdMapRepo,
            rulesProvider,
            unitOfWork,
            new BacklogOrderer(adoService, fieldDefinitionStore));

        var publishResult = await orchestrator.PublishAsync(-1);

        publishResult.Status.ShouldBe(SeedPublishStatus.Created);
        createRequest.ShouldNotBeNull();
        createRequest.ParentId.ShouldBe(100);
    }

    [Fact]
    public async Task PublishedDependencyLink_IsAvailableFromLocalRelationshipCache()
    {
        var workItemRepo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
        var workItemLinkRepo = new SqliteWorkItemLinkRepository(_store);
        var seedLinkRepo = new SqliteSeedLinkRepository(_store);
        var publishIdMapRepo = new SqlitePublishIdMapRepository(_store);
        var unitOfWork = new SqliteUnitOfWork(_store);
        var adoService = Substitute.For<IAdoWorkItemService>();
        var rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        var fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        await workItemRepo.SaveAsync(new WorkItemBuilder(99, "Existing predecessor").Build());
        await workItemRepo.SaveAsync(new WorkItemBuilder(-1, "Dependent seed").AsSeed().Build());
        await seedLinkRepo.AddLinkAsync(
            new SeedLink(-1, 99, SeedLinkTypes.BlockedBy, DateTimeOffset.UtcNow));

        var publishedItem = new WorkItemBuilder(500, "Dependent seed").Build();
        var publishedLinks = new[] { new WorkItemLink(500, 99, "Predecessor") };
        adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(500);
        adoService.FetchAsync(500, Arg.Any<CancellationToken>())
            .Returns(publishedItem);
        adoService.FetchWithLinksAsync(500, Arg.Any<CancellationToken>())
            .Returns((publishedItem, publishedLinks));

        var orchestrator = new SeedPublishOrchestrator(
            workItemRepo,
            adoService,
            seedLinkRepo,
            workItemLinkRepo,
            publishIdMapRepo,
            rulesProvider,
            unitOfWork,
            new BacklogOrderer(adoService, fieldDefinitionStore));

        var publishResult = await orchestrator.PublishAsync(-1);
        var cachedLinks = await workItemLinkRepo.GetLinksAsync(500);

        publishResult.Status.ShouldBe(SeedPublishStatus.Created);
        var cachedLink = cachedLinks.ShouldHaveSingleItem();
        cachedLink.TargetId.ShouldBe(99);
        cachedLink.LinkType.ShouldBe("Predecessor");
    }

    public void Dispose() => _store.Dispose();
}
