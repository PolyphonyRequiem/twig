using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#268 — discarding a seed that carries a staged
/// note must succeed rather than failing with "Cache corrupted".
/// <para>
/// These run against a REAL SQLite store, not substitutes, because the bug lives in the
/// database layer: <c>pending_changes.work_item_id</c> has a FOREIGN KEY to
/// <c>work_items(id)</c>, and Microsoft.Data.Sqlite enables foreign-key enforcement by
/// default (unlike raw SQLite). Deleting the seed row while a staged note still referenced
/// it raised <c>SQLite Error 19: FOREIGN KEY constraint failed</c>, which the CLI reported
/// as the misleading and destructive-sounding "Cache corrupted. Run 'twig init --force'".
/// A mock-based test cannot catch this — it must hit real SQLite.
/// </para>
/// </summary>
public sealed class SeedDiscardWithStagedNoteTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteWorkItemRepository _repo;
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();

    public SeedDiscardWithStagedNoteTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    private SeedDiscardOrchestrator CreateOrchestrator() =>
        new(_repo, _seedLinkRepo, _contextStore, _changeStore);

    /// <summary>
    /// Fixture guard, inverted by wayfinder 0013: the FK is gone by construction, so deleting a
    /// seed that carries staged changes is no longer a constraint violation.
    /// </summary>
    [Fact]
    public async Task Fixture_ForeignKeyIsGone_SoTheFailureClassIsUnexpressible()
    {
        await SaveSeedAsync(-1);
        await _changeStore.AddChangeAsync(-1, "note", null, null, "staged note");

        await Should.NotThrowAsync(async () => await _repo.DeleteByIdAsync(-1));

        (await _repo.GetByIdAsync(-1)).ShouldBeNull();
        (await _changeStore.GetChangesAsync(-1)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DiscardSeed_WithStagedNote_Succeeds()
    {
        await SaveSeedAsync(-1);
        await _changeStore.AddChangeAsync(-1, "note", null, null, "an important note");

        var orchestrator = CreateOrchestrator();
        var plan = await orchestrator.BuildDiscardPlanAsync(-1);
        plan.ShouldNotBeNull();

        // Before the fix this threw SqliteException -> "Cache corrupted".
        await orchestrator.ExecuteDiscardAsync(plan!);

        (await _repo.GetByIdAsync(-1)).ShouldBeNull();
        (await _changeStore.GetChangesAsync(-1)).ShouldBeEmpty("staged rows must not be orphaned");
    }

    [Fact]
    public async Task DiscardSeed_WithStagedFieldEdit_Succeeds()
    {
        await SaveSeedAsync(-1);
        await _changeStore.AddChangeAsync(-1, "field", "System.Title", "Old", "New");

        var orchestrator = CreateOrchestrator();
        var plan = await orchestrator.BuildDiscardPlanAsync(-1);

        await orchestrator.ExecuteDiscardAsync(plan!);

        (await _repo.GetByIdAsync(-1)).ShouldBeNull();
        (await _changeStore.GetChangesAsync(-1)).ShouldBeEmpty();
    }

    /// <summary>
    /// Cascade discard walks children before parents; a staged note on any descendant must
    /// not break the whole cascade partway through.
    /// </summary>
    [Fact]
    public async Task DiscardSeed_CascadeWithStagedNoteOnChild_Succeeds()
    {
        await SaveSeedAsync(-1);
        await SaveSeedAsync(-2, parentId: -1);
        await _changeStore.AddChangeAsync(-2, "note", null, null, "child note");

        var orchestrator = CreateOrchestrator();
        var plan = await orchestrator.BuildDiscardPlanAsync(-1);
        plan!.AllIds.ShouldContain(-2);

        await orchestrator.ExecuteDiscardAsync(plan);

        (await _repo.GetByIdAsync(-1)).ShouldBeNull();
        (await _repo.GetByIdAsync(-2)).ShouldBeNull();
        (await _changeStore.GetChangesAsync(-2)).ShouldBeEmpty();
    }

    /// <summary>A seed with nothing staged must keep working exactly as before.</summary>
    [Fact]
    public async Task DiscardSeed_WithNoStagedChanges_StillSucceeds()
    {
        await SaveSeedAsync(-1);

        var orchestrator = CreateOrchestrator();
        var plan = await orchestrator.BuildDiscardPlanAsync(-1);

        await orchestrator.ExecuteDiscardAsync(plan!);

        (await _repo.GetByIdAsync(-1)).ShouldBeNull();
    }

    private async Task SaveSeedAsync(int id, int? parentId = null)
    {
        var seed = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = $"seed {id}",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            IsSeed = true,
            ParentId = parentId,
        };
        await _repo.SaveAsync(seed);
    }
}
