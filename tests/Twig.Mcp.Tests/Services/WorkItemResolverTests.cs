using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Mcp.Services;
using Twig.Mcp.Tests.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

/// <summary>
/// Tests for the shared <see cref="WorkItemResolver.ResolveWorkItemAsync"/> helper.
/// </summary>
public sealed class WorkItemResolverTests : ReadToolsTestBase
{
    private WorkspaceContext BuildCtx()
    {
        var resolver = BuildResolver(DefaultConfig);
        resolver.TryResolve(null, out var ctx, out _);
        return ctx!;
    }

    // ═══════════════════════════════════════════════════════════════
    //  With explicit ID — item found in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_WithId_ReturnsItemFromCache()
    {
        var expected = new WorkItemBuilder(42, "Cached Item").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(expected);

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, 42, CancellationToken.None);

        item.ShouldNotBeNull();
        item.Id.ShouldBe(42);
        error.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  With explicit ID — falls back to ADO when not in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_WithId_FallsBackToAdo()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var adoItem = new WorkItemBuilder(99, "ADO Item").AsTask().InState("To Do").Build();
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(adoItem);

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, 99, CancellationToken.None);

        item.ShouldNotBeNull();
        item.Id.ShouldBe(99);
        error.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  With explicit ID — not found anywhere → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_WithId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, 999, CancellationToken.None);

        item.ShouldBeNull();
        error.ShouldNotBeNull();
        error.IsError.ShouldBe(true);
        GetErrorText(error).ShouldContain("999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Without ID — active context found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_NoId_ReturnsActiveItem()
    {
        var activeItem = new WorkItemBuilder(7, "Active Item").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(activeItem);

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, null, CancellationToken.None);

        item.ShouldNotBeNull();
        item.Id.ShouldBe(7);
        error.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Without ID — no active context → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_NoId_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, null, CancellationToken.None);

        item.ShouldBeNull();
        error.ShouldNotBeNull();
        error.IsError.ShouldBe(true);
        GetErrorText(error).ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Without ID — active item unreachable → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_NoId_Unreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(123);
        _workItemRepo.GetByIdAsync(123, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(123, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unreachable"));

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, null, CancellationToken.None);

        item.ShouldBeNull();
        error.ShouldNotBeNull();
        error.IsError.ShouldBe(true);
        GetErrorText(error).ShouldContain("unreachable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  With explicit ID — negative seed ID works
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkItemAsync_WithNegativeId_ReturnsSeedItem()
    {
        var seed = new WorkItemBuilder(-5, "Seed Item").AsTask().AsSeed().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);

        var ctx = BuildCtx();
        var (item, error) = await WorkItemResolver.ResolveWorkItemAsync(ctx, -5, CancellationToken.None);

        item.ShouldNotBeNull();
        item.Id.ShouldBe(-5);
        error.ShouldBeNull();
    }
}
