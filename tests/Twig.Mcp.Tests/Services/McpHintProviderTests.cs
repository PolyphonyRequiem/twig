using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpHintProviderTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();

    // ── GetHintsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetHintsAsync_NoPendingChanges_NoDirty_NoSeeds_ReturnsEmpty()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetHintsAsync_WithPendingChanges_ReturnsHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(42, "field", "System.Title", "Old", "New"),
                new PendingChangeRecord(42, "note", null, null, "A note"),
            });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldContain(h => h.Contains("2 pending changes") && h.Contains("twig_sync"));
    }

    [Fact]
    public async Task GetHintsAsync_SinglePendingChange_UsesSingularNoun()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "A", "B") });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldContain(h => h.Contains("1 pending change"));
        hints.ShouldNotContain(h => h.Contains("changes"));
    }

    [Fact]
    public async Task GetHintsAsync_DirtyItemsExcludingActive_ReturnsHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 20, 30 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldContain(h => h.Contains("2 other dirty items"));
    }

    [Fact]
    public async Task GetHintsAsync_OnlyActiveDirty_NoDirtyHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldNotContain(h => h.Contains("dirty"));
    }

    [Fact]
    public async Task GetHintsAsync_WithSeeds_ReturnsHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(-1, "Seed 1").AsTask().AsSeed().Build(),
                new WorkItemBuilder(-2, "Seed 2").AsTask().AsSeed().Build(),
                new WorkItemBuilder(-3, "Seed 3").AsTask().AsSeed().Build(),
            });

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldContain(h => h.Contains("3 unpublished seeds"));
    }

    [Fact]
    public async Task GetHintsAsync_SingleSeed_UsesSingularNoun()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(-1, "Seed").AsTask().AsSeed().Build() });

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.ShouldContain(h => h.Contains("1 unpublished seed"));
        hints.ShouldNotContain(h => h.Contains("seeds"));
    }

    [Fact]
    public async Task GetHintsAsync_AllConditions_ReturnsMultipleHints()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "F1", "A", "B") });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(-1, "S").AsTask().AsSeed().Build() });

        var ctx = BuildContext();
        var hints = await McpHintProvider.GetHintsAsync(ctx, CancellationToken.None);

        hints.Count.ShouldBe(3);
    }

    // ── ApplyHintsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyHintsAsync_VerboseFalse_ReturnsOriginalResult()
    {
        var original = McpResultBuilder.ToResult("""{"id":1}""");

        var result = await McpHintProvider.ApplyHintsAsync(
            original, verbose: false, BuildContext(), CancellationToken.None);

        result.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task ApplyHintsAsync_VerboseTrue_AddsHintsArray()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var original = McpResultBuilder.ToResult("""{"id":1}""");
        var ctx = BuildContext();

        var result = await McpHintProvider.ApplyHintsAsync(
            original, verbose: true, ctx, CancellationToken.None);

        var json = ParseJson(result);
        json.GetProperty("hints").GetArrayLength().ShouldBe(0);
        json.GetProperty("id").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ApplyHintsAsync_VerboseTrue_WithHints_IncludesHintStrings()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(5);
        _pendingChangeStore.GetChangesAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(5, "field", "F", "A", "B") });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var original = McpResultBuilder.ToResult("""{"ok":true}""");
        var ctx = BuildContext();

        var result = await McpHintProvider.ApplyHintsAsync(
            original, verbose: true, ctx, CancellationToken.None);

        var json = ParseJson(result);
        json.GetProperty("hints").GetArrayLength().ShouldBeGreaterThan(0);
        json.GetProperty("hints")[0].GetString()!.ShouldContain("pending");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private WorkspaceContext BuildContext()
    {
        var key = new WorkspaceKey("org", "proj");
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 5 },
        };
        var paths = TwigPaths.ForContext(Path.GetTempPath(), key.Org, key.Project);
        var cacheStore = new SqliteCacheStore("Data Source=:memory:");
        var adoService = Substitute.For<IAdoWorkItemService>();
        var iterationService = Substitute.For<IIterationService>();
        var processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        var promptStateWriter = Substitute.For<IPromptStateWriter>();
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();

        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncFactory = new SyncCoordinatorFactory(
            _workItemRepo, adoService, protectedWriter, _pendingChangeStore,
            linkRepo,
            readOnlyStaleMinutes: config.Display.CacheStaleMinutes,
            readWriteStaleMinutes: config.Display.CacheStaleMinutes);
        var contextChange = new ContextChangeService(
            _workItemRepo, adoService, syncFactory.ReadWrite, protectedWriter, linkRepo);
        var workingSet = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, iterationService,
            config.User.DisplayName);
        var flusher = new McpPendingChangeFlusher(_workItemRepo, adoService, _pendingChangeStore);
        var parentPropagation = new ParentStatePropagationService(
            _workItemRepo, adoService, processConfigProvider, protectedWriter);
        var sprintIterationResolver = new SprintIterationResolver(iterationService, _workItemRepo);

        return new WorkspaceContext(
            key, config, paths, cacheStore,
            _workItemRepo, _contextStore, _pendingChangeStore,
            adoService, iterationService, processConfigProvider,
            activeItemResolver, syncFactory, contextChange,
            workingSet, flusher, promptStateWriter, parentPropagation,
            stateTransitionWorkflow: null!,
            fieldUpdateWorkflow: null!,
            noteWorkflow: null!,
            discardWorkflow: null!,
            deleteWorkflow: null!,
            patchWorkflow: null!,
            sprintIterationResolver,
            processTypeStore, fieldDefStore,
            Substitute.For<ISeedLinkRepository>(), Substitute.For<IPublishIdMapRepository>(), Substitute.For<ISeedPublishRulesProvider>(), Substitute.For<IUnitOfWork>());
    }

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
