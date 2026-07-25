using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#270 — publishing a seed that carries a staged
/// note must succeed, and the note must survive onto the published ID.
/// <para>
/// The bug is materially worse than its sibling #268: <c>SeedPublishOrchestrator</c> creates
/// the ADO work item in Step 7, OUTSIDE the local transaction. The staged note's FOREIGN KEY
/// on <c>work_items(id)</c> then made the seed-row delete throw, the transaction rolled back,
/// and the remote item was orphaned with no <c>publish_id_map</c> entry — so every retry
/// created another duplicate ADO item.
/// </para>
/// <para>
/// These run against REAL SQLite, not substitutes: Microsoft.Data.Sqlite enables foreign-key
/// enforcement by default, and a mock cannot reproduce an FK violation. That is exactly why
/// the existing mock-based <c>SeedPublishOrchestratorTests</c> never caught this.
/// </para>
/// </summary>
public sealed class SeedPublishWithStagedNoteTests : IDisposable
{
    private const int SeedId = -1;
    private const int PublishedId = 4242;

    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteWorkItemRepository _repo;
    private readonly SqlitePublishIdMapRepository _publishIdMapRepo;
    private readonly SqliteUnitOfWork _unitOfWork;
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IWorkItemLinkRepository _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly ISeedPublishRulesProvider _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

    public SeedPublishWithStagedNoteTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
        _publishIdMapRepo = new SqlitePublishIdMapRepository(_store);
        _unitOfWork = new SqliteUnitOfWork(_store);

        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeedLink>>([]));
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeedLink>>([]));
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SeedPublishRules()));

        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(PublishedId));
        _adoService.FetchAsync(PublishedId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(PublishedItem()));
        _adoService.FetchWithLinksAsync(PublishedId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<(WorkItem, IReadOnlyList<WorkItemLink>)>((PublishedItem(), [])));
    }

    public void Dispose() => _store.Dispose();

    private SeedPublishOrchestrator CreateOrchestrator(bool withPendingChangeStore = true) =>
        new(
            _repo,
            _adoService,
            _seedLinkRepo,
            _workItemLinkRepo,
            _publishIdMapRepo,
            _rulesProvider,
            _unitOfWork,
            new BacklogOrderer(_adoService, _fieldDefinitionStore),
            withPendingChangeStore ? _changeStore : null);

    /// <summary>
    /// Fixture guard: proves the FK is really enforced here. Without this the whole class
    /// could pass vacuously — the failure mode that made two of three #251 tests worthless.
    /// </summary>
    [Fact]
    public async Task Fixture_ForeignKeyIsEnforced_SoTheBugIsReachable()
    {
        await SaveSeedAsync();
        await _changeStore.AddChangeAsync(SeedId, "note", null, null, "staged note");

        var ex = await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(
            async () => await _repo.DeleteByIdAsync(SeedId));

        ex.Message.ShouldContain("FOREIGN KEY constraint failed");
    }

    /// <summary>
    /// The unfixed sequence, reproduced explicitly: with no pending-change store the publish
    /// still throws. This pins the pre-fix behaviour so the fixed test below is non-vacuous.
    /// </summary>
    [Fact]
    public async Task PublishSeed_WithStagedNote_WithoutPendingChangeStore_StillThrows()
    {
        await SaveSeedAsync();
        await _changeStore.AddChangeAsync(SeedId, "note", null, null, "a note I still want");

        var ex = await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(
            async () => await CreateOrchestrator(withPendingChangeStore: false).PublishAsync(SeedId));

        ex.Message.ShouldContain("FOREIGN KEY constraint failed");

        // This is the duplicate-creation trap: the ADO item exists, but the rollback left the
        // seed in place and publish_id_map empty, so a retry would create a second item.
        await _adoService.Received(1).CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        (await _repo.GetByIdAsync(SeedId)).ShouldNotBeNull();
        (await _publishIdMapRepo.GetNewIdAsync(SeedId)).ShouldBeNull();
    }

    [Fact]
    public async Task PublishSeed_WithStagedNote_SucceedsAndPreservesTheNote()
    {
        await SaveSeedAsync();
        await _changeStore.AddChangeAsync(SeedId, "note", null, null, "a note I still want");

        var result = await CreateOrchestrator().PublishAsync(SeedId);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(PublishedId);

        (await _repo.GetByIdAsync(SeedId)).ShouldBeNull();
        (await _repo.GetByIdAsync(PublishedId)).ShouldNotBeNull();
        (await _publishIdMapRepo.GetNewIdAsync(SeedId)).ShouldBe(PublishedId);

        // The note is migrated, not destroyed — it flushes to the published item on next sync.
        (await _changeStore.GetChangesAsync(SeedId)).ShouldBeEmpty();
        var migrated = await _changeStore.GetChangesAsync(PublishedId);
        migrated.Count.ShouldBe(1);
        migrated[0].NewValue.ShouldBe("a note I still want");
    }

    [Fact]
    public async Task PublishSeed_WithStagedFieldEdit_SucceedsAndPreservesTheEdit()
    {
        await SaveSeedAsync();
        await _changeStore.AddChangeAsync(SeedId, "field", "System.Description", "old", "new");

        var result = await CreateOrchestrator().PublishAsync(SeedId);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        var migrated = await _changeStore.GetChangesAsync(PublishedId);
        migrated.Count.ShouldBe(1);
        migrated[0].FieldName.ShouldBe("System.Description");
    }

    /// <summary>Publishing exactly once must remain the behaviour for a clean seed.</summary>
    [Fact]
    public async Task PublishSeed_WithNoStagedChanges_StillSucceeds()
    {
        await SaveSeedAsync();

        var result = await CreateOrchestrator().PublishAsync(SeedId);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        (await _repo.GetByIdAsync(SeedId)).ShouldBeNull();
        (await _changeStore.GetChangesAsync(PublishedId)).ShouldBeEmpty();
        await _adoService.Received(1).CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    private static WorkItem PublishedItem() => new()
    {
        Id = PublishedId,
        Type = WorkItemType.Task,
        Title = "publish me",
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    private async Task SaveSeedAsync()
    {
        var seed = new WorkItem
        {
            Id = SeedId,
            Type = WorkItemType.Task,
            Title = "publish me",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            IsSeed = true,
        };
        await _repo.SaveAsync(seed);
    }
}
