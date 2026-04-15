using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ContextTools.Status"/> (twig.status MCP tool).
/// Covers no-context error, success with item, unreachable item,
/// pending changes, and seeds in the status snapshot.
/// </summary>
public sealed class ContextToolsStatusTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly IPromptStateWriter _promptStateWriter = Substitute.For<IPromptStateWriter>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();

    private SyncCoordinator CreateSyncCoordinator() =>
        new(_workItemRepo, _adoService,
            new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore),
            _pendingChangeStore, _linkRepo, cacheStaleMinutes: 5);

    private StatusOrchestrator CreateStatusOrchestrator(ActiveItemResolver resolver) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, resolver,
            new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null),
            CreateSyncCoordinator());

    private ContextTools CreateSut()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        return new ContextTools(
            _workItemRepo, _contextStore, resolver,
            CreateSyncCoordinator(), CreateStatusOrchestrator(resolver),
            _promptStateWriter);
    }

    private static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No context — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateSut().Status();

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("No active work item");
        text.ShouldContain("twig.set");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — active item with no pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithActiveItem_ReturnsStatusSnapshot()
    {
        var item = new WorkItemBuilder(42, "My Feature").AsFeature().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("item").GetProperty("title").GetString().ShouldBe("My Feature");
        root.GetProperty("pendingChanges").GetArrayLength().ShouldBe(0);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable item — returns formatted status (not error)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_UnreachableItem_ReturnsStatusWithUnreachableFields()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Network error"));

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("unreachableId").GetInt32().ShouldBe(99);
        root.GetProperty("unreachableReason").GetString().ShouldNotBeNullOrEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  With pending changes — included in snapshot
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithPendingChanges_IncludesChangesInResult()
    {
        var item = new WorkItemBuilder(10, "Task With Changes").AsTask().InState("New").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(10, "FieldUpdate", "System.State", "New", "Active"),
            });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var changes = root.GetProperty("pendingChanges");
        changes.GetArrayLength().ShouldBe(1);
        changes[0].GetProperty("workItemId").GetInt32().ShouldBe(10);
        changes[0].GetProperty("fieldName").GetString().ShouldBe("System.State");
    }

    // ═══════════════════════════════════════════════════════════════
    //  With seeds — included in snapshot
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithSeeds_IncludesSeedsInResult()
    {
        var item = new WorkItemBuilder(20, "Feature").AsFeature().InState("Active").Build();
        var seed = new WorkItemBuilder(30, "Seed Item").AsTask().InState("New").AsSeed().Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(20);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(20, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { seed });

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var seeds = root.GetProperty("seeds");
        seeds.GetArrayLength().ShouldBe(1);
        seeds[0].GetProperty("id").GetInt32().ShouldBe(30);
        seeds[0].GetProperty("title").GetString().ShouldBe("Seed Item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation — propagates (not swallowed)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_Cancelled_PropagatesException()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Status());
    }
}
