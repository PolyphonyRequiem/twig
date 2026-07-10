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
            .Returns(new WorkItemBuilder(500, "Child seed").AsTask().Build());

        var orchestrator = new SeedPublishOrchestrator(
            workItemRepo,
            adoService,
            seedLinkRepo,
            publishIdMapRepo,
            rulesProvider,
            unitOfWork,
            new BacklogOrderer(adoService, fieldDefinitionStore));

        var publishResult = await orchestrator.PublishAsync(-1);

        publishResult.Status.ShouldBe(SeedPublishStatus.Created);
        createRequest.ShouldNotBeNull();
        createRequest.ParentId.ShouldBe(100);
    }

    public void Dispose() => _store.Dispose();
}
