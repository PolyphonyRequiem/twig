using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Integration tests for <c>twig_set</c> cross-workspace ID lookup.
/// Verifies that numeric IDs resolve across multiple registered workspaces
/// and that ambiguous matches return clear errors listing matched workspaces.
/// </summary>
public sealed class MultiWorkspaceSetTests : ReadToolsTestBase
{
    private static readonly WorkspaceKey WsAlpha = new("orgA", "projectA");
    private static readonly WorkspaceKey WsBeta = new("orgB", "projectB");

    // ═══════════════════════════════════════════════════════════════
    //  Cross-workspace: item found in workspace A cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_FoundInWorkspaceACache_ResolvesToWorkspaceA()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(42, "Alpha Feature").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await sut.Set("42");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("Alpha Feature");
        root.GetProperty("workspace").GetString().ShouldBe("orgA/projectA");

        await mocks[WsAlpha].ContextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await mocks[WsBeta].ContextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        resolver.ActiveWorkspace.ShouldBe(WsAlpha);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cross-workspace: item found in workspace B cache (not A)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_FoundInWorkspaceBCache_ResolvesToWorkspaceB()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(99, "Beta Task").AsTask().InState("New").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        var result = await sut.Set("99");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(99);
        root.GetProperty("workspace").GetString().ShouldBe("orgB/projectB");

        await mocks[WsBeta].ContextStore.Received(1).SetActiveWorkItemIdAsync(99, Arg.Any<CancellationToken>());
        resolver.ActiveWorkspace.ShouldBe(WsBeta);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cross-workspace: not in cache, found via ADO in workspace B
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_NotCached_FetchedFromAdoInWorkspaceB()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(200, "ADO-only Item").AsTask().InState("New").Build();

        // Not in any cache
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        // ADO probe: not in A, found in B
        mocks[WsAlpha].AdoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoNotFoundException(200));
        mocks[WsBeta].AdoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await sut.Set("200");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(200);
        root.GetProperty("workspace").GetString().ShouldBe("orgB/projectB");
        resolver.ActiveWorkspace.ShouldBe(WsBeta);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Ambiguous: item found in both workspace caches → clear error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_AmbiguousMatch_ReturnsErrorListingWorkspaces()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var itemA = new WorkItemBuilder(77, "Shared Item A").AsFeature().InState("Active").Build();
        var itemB = new WorkItemBuilder(77, "Shared Item B").AsTask().InState("New").Build();

        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(itemA);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(itemB);

        var result = await sut.Set("77");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#77");
        text.ShouldContain("orgA/projectA");
        text.ShouldContain("orgB/projectB");
        text.ShouldContain("multiple workspaces", Case.Insensitive);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Ambiguous via ADO: item not cached, but ADO returns it in both
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_AmbiguousAdoMatch_ReturnsErrorListingWorkspaces()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(88, "ADO Ambiguous").AsTask().InState("New").Build();

        // Not in any cache
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        // ADO finds it in both
        mocks[WsAlpha].AdoService.FetchAsync(88, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].AdoService.FetchAsync(88, Arg.Any<CancellationToken>()).Returns(item);

        var result = await sut.Set("88");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#88");
        text.ShouldContain("orgA/projectA");
        text.ShouldContain("orgB/projectB");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Not found in any workspace → clear error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_NotFoundAnywhere_ReturnsError()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsAlpha].AdoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoNotFoundException(999));
        mocks[WsBeta].AdoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoNotFoundException(999));

        var result = await sut.Set("999");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#999");
        text.ShouldContain("not found", Case.Insensitive);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit workspace param bypasses probing
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ExplicitWorkspace_BypassesProbing()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(50, "Explicit Item").AsTask().InState("Active").Build();
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(item);

        var result = await sut.Set("50", workspace: "orgB/projectB");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(50);
        root.GetProperty("workspace").GetString().ShouldBe("orgB/projectB");

        // Should NOT probe workspace A at all
        await mocks[WsAlpha].WorkItemRepo.DidNotReceive().GetByIdAsync(50, Arg.Any<CancellationToken>());
        resolver.ActiveWorkspace.ShouldBe(WsBeta);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-workspace backward compat: works without workspace param
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_SingleWorkspace_BackwardCompat_NoWorkspaceParamNeeded()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha);
        var sut = new ContextTools(resolver);

        var item = new WorkItemBuilder(10, "Solo Item").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);

        var result = await sut.Set("10");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("workspace").GetString().ShouldBe("orgA/projectA");

        await mocks[WsAlpha].ContextStore.Received(1).SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
        resolver.ActiveWorkspace.ShouldBe(WsAlpha);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace switch: set in A, then set in B updates active
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_SwitchWorkspaces_UpdatesActiveWorkspace()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);
        var sut = new ContextTools(resolver);

        var itemA = new WorkItemBuilder(10, "Alpha Item").AsTask().InState("Active").Build();
        var itemB = new WorkItemBuilder(20, "Beta Item").AsTask().InState("New").Build();

        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(itemA);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);

        // Set to item in workspace A
        await sut.Set("10");
        resolver.ActiveWorkspace.ShouldBe(WsAlpha);

        // Switch to item in workspace B
        await sut.Set("20");
        resolver.ActiveWorkspace.ShouldBe(WsBeta);
    }
}
