using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Integration tests verifying that two workspaces with independent mock sets
/// maintain fully isolated active context items and pending changes.
/// </summary>
public sealed class MultiWorkspaceIsolationTests : ReadToolsTestBase
{
    private static readonly WorkspaceKey WsAlpha = new("orgA", "projectA");
    private static readonly WorkspaceKey WsBeta = new("orgB", "projectB");

    // ═══════════════════════════════════════════════════════════════
    //  Active context: setting in A does not affect B
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_InWorkspaceA_DoesNotSetContextInWorkspaceB()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(42, "Alpha Feature").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        await sut.Set("42");

        await mocks[WsAlpha].ContextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await mocks[WsBeta].ContextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active context: each workspace tracks its own active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_EachWorkspace_TracksOwnActiveItem()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var itemA = new WorkItemBuilder(10, "Alpha Item").AsTask().InState("Active").Build();
        var itemB = new WorkItemBuilder(20, "Beta Item").AsTask().InState("New").Build();

        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(itemA);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);

        // Set item in workspace A
        await sut.Set("10");
        await mocks[WsAlpha].ContextStore.Received(1).SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());

        // Set item in workspace B
        await sut.Set("20");
        await mocks[WsBeta].ContextStore.Received(1).SetActiveWorkItemIdAsync(20, Arg.Any<CancellationToken>());

        // Workspace A should not have received the second set
        await mocks[WsAlpha].ContextStore.DidNotReceive().SetActiveWorkItemIdAsync(20, Arg.Any<CancellationToken>());
        // Workspace B should not have received the first set
        await mocks[WsBeta].ContextStore.DidNotReceive().SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes: note in A does not stage in B
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_InWorkspaceA_DoesNotStagePendingInWorkspaceB()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);

        // Set up active item in workspace A
        var item = new WorkItemBuilder(42, "Alpha Feature").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        // First, set context to workspace A
        var contextTools = new ContextTools(resolver);
        await contextTools.Set("42");

        // Configure active item resolution for workspace A
        mocks[WsAlpha].ContextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Make ADO AddComment fail to force local staging
        mocks[WsAlpha].AdoService.AddCommentAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Network error")));

        var mutationTools = new MutationTools(resolver);
        var result = await mutationTools.Note("Test note for A");

        result.IsError.ShouldBeNull();

        // Workspace A should have staged the pending change
        await mocks[WsAlpha].PendingChangeStore.Received(1).AddChangeAsync(
            42, "note", Arg.Any<string?>(), Arg.Any<string?>(), "Test note for A", Arg.Any<CancellationToken>());

        // Workspace B should not have any pending change interactions
        await mocks[WsBeta].PendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state: each workspace writes its own prompt state
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_WritesPromptState_OnlyForResolvedWorkspace()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(42, "Alpha Feature").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        await sut.Set("42");

        await mocks[WsAlpha].PromptStateWriter.Received(1).WritePromptStateAsync();
        await mocks[WsBeta].PromptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resolver state: active workspace reflects last twig_set call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Resolver_ActiveWorkspace_ReflectsLastSetCall()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        resolver.ActiveWorkspace.ShouldBeNull();

        var itemA = new WorkItemBuilder(10, "A").AsTask().Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(itemA);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        await sut.Set("10");
        resolver.ActiveWorkspace.ShouldBe(WsAlpha);

        var itemB = new WorkItemBuilder(20, "B").AsTask().Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);

        await sut.Set("20");
        resolver.ActiveWorkspace.ShouldBe(WsBeta);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resolver fallback: after twig_set, other tools use active ws
    // ═══════════════════════════════════════════════════════════════

}
