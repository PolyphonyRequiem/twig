using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class WorkspaceResolverTests
{
    private static readonly WorkspaceKey KeyA = new("orgA", "proj1");
    private static readonly WorkspaceKey KeyB = new("orgB", "proj2");
    private static readonly WorkspaceKey KeyC = new("orgC", "proj3");

    // ── Resolve (standard tool calls) ───────────────────────────────

    [Fact]
    public void Resolve_ExplicitWorkspace_ReturnsMatchingContext()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        var result = resolver.Resolve("orgA/proj1");

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public void Resolve_ExplicitWorkspace_WithWhitespace_ParsesCorrectly()
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = resolver.Resolve("  orgA / proj1  ");

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public void Resolve_ExplicitWorkspace_UnknownKey_ThrowsKeyNotFound()
    {
        var (resolver, _) = CreateResolver(KeyA);

        Should.Throw<KeyNotFoundException>(() => resolver.Resolve("unknown/missing"));
    }

    [Fact]
    public void Resolve_ExplicitWorkspace_Malformed_ThrowsFormatException()
    {
        var (resolver, _) = CreateResolver(KeyA);

        Should.Throw<FormatException>(() => resolver.Resolve("no-slash"));
    }

    [Fact]
    public void Resolve_SingleWorkspace_NoExplicitParam_ReturnsSoleWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = resolver.Resolve();

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public void Resolve_SingleWorkspace_NullParam_ReturnsSoleWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = resolver.Resolve(null);

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public void Resolve_SingleWorkspace_EmptyParam_ReturnsSoleWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = resolver.Resolve("");

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public void Resolve_MultipleWorkspaces_ActiveSet_ReturnsActiveWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);
        resolver.ActiveWorkspace = KeyB;

        var result = resolver.Resolve();

        result.ShouldBeSameAs(contexts[KeyB]);
    }

    [Fact]
    public void Resolve_MultipleWorkspaces_NoActive_ThrowsAmbiguous()
    {
        var (resolver, _) = CreateResolver(KeyA, KeyB);

        var ex = Should.Throw<AmbiguousWorkspaceException>(() => resolver.Resolve());

        ex.AvailableWorkspaces.ShouldContain(KeyA);
        ex.AvailableWorkspaces.ShouldContain(KeyB);
        ex.WorkItemId.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ExplicitOverridesActive()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);
        resolver.ActiveWorkspace = KeyA;

        var result = resolver.Resolve("orgB/proj2");

        result.ShouldBeSameAs(contexts[KeyB]);
    }

    // ── Active workspace tracking ───────────────────────────────────

    [Fact]
    public void ActiveWorkspace_InitiallyNull()
    {
        var (resolver, _) = CreateResolver(KeyA);

        resolver.ActiveWorkspace.ShouldBeNull();
    }

    [Fact]
    public void ActiveWorkspace_SetAndGet()
    {
        var (resolver, _) = CreateResolver(KeyA, KeyB);

        resolver.ActiveWorkspace = KeyA;
        resolver.ActiveWorkspace.ShouldBe(KeyA);

        resolver.ActiveWorkspace = KeyB;
        resolver.ActiveWorkspace.ShouldBe(KeyB);
    }

    [Fact]
    public void ActiveWorkspace_CanBeCleared()
    {
        var (resolver, _) = CreateResolver(KeyA);

        resolver.ActiveWorkspace = KeyA;
        resolver.ActiveWorkspace = null;

        resolver.ActiveWorkspace.ShouldBeNull();
    }

    // ── ResolveForSetAsync — explicit workspace ─────────────────────

    [Fact]
    public async Task ResolveForSet_ExplicitWorkspace_ReturnsMatchingContext()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        var result = await resolver.ResolveForSetAsync(12345, "orgA/proj1");

        result.ShouldBeSameAs(contexts[KeyA]);
    }

    [Fact]
    public async Task ResolveForSet_ExplicitWorkspace_SetsActiveWorkspace()
    {
        var (resolver, _) = CreateResolver(KeyA, KeyB);

        await resolver.ResolveForSetAsync(12345, "orgB/proj2");

        resolver.ActiveWorkspace.ShouldBe(KeyB);
    }

    [Fact]
    public async Task ResolveForSet_ExplicitWorkspace_Unknown_ThrowsKeyNotFound()
    {
        var (resolver, _) = CreateResolver(KeyA);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => resolver.ResolveForSetAsync(12345, "unknown/missing"));
    }

    // ── ResolveForSetAsync — single workspace ───────────────────────

    [Fact]
    public async Task ResolveForSet_SingleWorkspace_SkipsProbing()
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = await resolver.ResolveForSetAsync(12345);

        result.ShouldBeSameAs(contexts[KeyA]);
        resolver.ActiveWorkspace.ShouldBe(KeyA);
    }

    // ── ResolveForSetAsync — cache probe ────────────────────────────

    [Fact]
    public async Task ResolveForSet_CacheHit_SingleMatch_ReturnsWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // Item found in KeyB's cache only
        SetupCacheHit(contexts[KeyB], 42);

        var result = await resolver.ResolveForSetAsync(42);

        result.ShouldBeSameAs(contexts[KeyB]);
        resolver.ActiveWorkspace.ShouldBe(KeyB);
    }

    [Fact]
    public async Task ResolveForSet_CacheHit_MultipleMatches_ThrowsAmbiguous()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // Item found in both caches
        SetupCacheHit(contexts[KeyA], 42);
        SetupCacheHit(contexts[KeyB], 42);

        var ex = await Should.ThrowAsync<AmbiguousWorkspaceException>(
            () => resolver.ResolveForSetAsync(42));

        ex.WorkItemId.ShouldBe(42);
        ex.AvailableWorkspaces.ShouldContain(KeyA);
        ex.AvailableWorkspaces.ShouldContain(KeyB);
    }

    // ── ResolveForSetAsync — ADO probe fallback ─────────────────────

    [Fact]
    public async Task ResolveForSet_NoCacheHit_AdoHit_ReturnsWorkspace()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // No cache hits, but ADO finds it in KeyA
        SetupAdoHit(contexts[KeyA], 99);
        SetupAdoMiss(contexts[KeyB], 99);

        var result = await resolver.ResolveForSetAsync(99);

        result.ShouldBeSameAs(contexts[KeyA]);
        resolver.ActiveWorkspace.ShouldBe(KeyA);
    }

    [Fact]
    public async Task ResolveForSet_NoCacheHit_AdoHit_MultipleMatches_ThrowsAmbiguous()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // ADO finds it in both
        SetupAdoHit(contexts[KeyA], 99);
        SetupAdoHit(contexts[KeyB], 99);

        var ex = await Should.ThrowAsync<AmbiguousWorkspaceException>(
            () => resolver.ResolveForSetAsync(99));

        ex.WorkItemId.ShouldBe(99);
        ex.AvailableWorkspaces.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveForSet_NoCacheHit_NoAdoHit_ThrowsNotFound()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // Nothing in cache, ADO throws for all
        SetupAdoMiss(contexts[KeyA], 999);
        SetupAdoMiss(contexts[KeyB], 999);

        var ex = await Should.ThrowAsync<WorkItemNotFoundException>(
            () => resolver.ResolveForSetAsync(999));

        ex.WorkItemId.ShouldBe(999);
        ex.SearchedWorkspaces.ShouldContain(KeyA);
        ex.SearchedWorkspaces.ShouldContain(KeyB);
    }

    // ── ResolveForSetAsync — cache hit takes priority over ADO ──────

    [Fact]
    public async Task ResolveForSet_CacheHit_DoesNotProbeAdo()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        SetupCacheHit(contexts[KeyA], 50);
        // KeyB cache returns null (default mock behavior = miss)

        var result = await resolver.ResolveForSetAsync(50);

        result.ShouldBeSameAs(contexts[KeyA]);
        // ADO should not have been called
        await contexts[KeyA].AdoService.DidNotReceive().FetchAsync(50, Arg.Any<CancellationToken>());
        await contexts[KeyB].AdoService.DidNotReceive().FetchAsync(50, Arg.Any<CancellationToken>());
    }

    // ── ResolveForSetAsync — three workspaces ───────────────────────

    [Fact]
    public async Task ResolveForSet_ThreeWorkspaces_FindsInThird()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB, KeyC);

        SetupCacheHit(contexts[KeyC], 77);

        var result = await resolver.ResolveForSetAsync(77);

        result.ShouldBeSameAs(contexts[KeyC]);
        resolver.ActiveWorkspace.ShouldBe(KeyC);
    }

    // ── Resolution precedence ───────────────────────────────────────

    [Fact]
    public void Resolve_Precedence_SingleWorkspaceBeforeActive()
    {
        // With single workspace, active is irrelevant — single-workspace fast path wins
        var (resolver, contexts) = CreateResolver(KeyA);
        resolver.ActiveWorkspace = KeyA;

        var result = resolver.Resolve();
        result.ShouldBeSameAs(contexts[KeyA]);
    }

    // ── AmbiguousWorkspaceException properties ──────────────────────

    [Fact]
    public void AmbiguousWorkspaceException_NoWorkItemId_HasCorrectProperties()
    {
        var workspaces = new List<WorkspaceKey> { KeyA, KeyB };
        var ex = new AmbiguousWorkspaceException(workspaces);

        ex.WorkItemId.ShouldBeNull();
        ex.AvailableWorkspaces.ShouldBe(workspaces);
        ex.Message.ShouldContain("orgA/proj1");
        ex.Message.ShouldContain("orgB/proj2");
    }

    [Fact]
    public void AmbiguousWorkspaceException_WithWorkItemId_HasCorrectProperties()
    {
        var workspaces = new List<WorkspaceKey> { KeyA, KeyB };
        var ex = new AmbiguousWorkspaceException(42, workspaces);

        ex.WorkItemId.ShouldBe(42);
        ex.AvailableWorkspaces.ShouldBe(workspaces);
        ex.Message.ShouldContain("#42");
    }

    // ── WorkItemNotFoundException properties ────────────────────────

    [Fact]
    public void WorkItemNotFoundException_HasCorrectProperties()
    {
        var workspaces = new List<WorkspaceKey> { KeyA, KeyB };
        var ex = new WorkItemNotFoundException(999, workspaces);

        ex.WorkItemId.ShouldBe(999);
        ex.SearchedWorkspaces.ShouldBe(workspaces);
        ex.Message.ShouldContain("#999");
        ex.Message.ShouldContain("orgA/proj1");
    }

    // ── Zero workspaces ─────────────────────────────────────────────

    [Fact]
    public void Resolve_NoWorkspaces_NoExplicit_ThrowsAmbiguous()
    {
        var (resolver, _) = CreateResolver();

        var ex = Should.Throw<AmbiguousWorkspaceException>(() => resolver.Resolve());
        ex.AvailableWorkspaces.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveForSet_NoWorkspaces_ThrowsNotFound()
    {
        var (resolver, _) = CreateResolver();

        var ex = await Should.ThrowAsync<WorkItemNotFoundException>(
            () => resolver.ResolveForSetAsync(123));

        ex.WorkItemId.ShouldBe(123);
        ex.SearchedWorkspaces.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (WorkspaceResolver Resolver, Dictionary<WorkspaceKey, WorkspaceContext> Contexts)
        CreateResolver(params WorkspaceKey[] keys)
    {
        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(keys.ToList().AsReadOnly());
        registry.IsSingleWorkspace.Returns(keys.Length == 1);

        var contexts = new Dictionary<WorkspaceKey, WorkspaceContext>();
        var factoryMock = Substitute.For<IWorkspaceContextFactory>();

        foreach (var key in keys)
        {
            var ctx = CreateStubContext(key);
            contexts[key] = ctx;
            factoryMock.GetOrCreate(key).Returns(ctx);
        }

        // Unknown keys throw KeyNotFoundException
        factoryMock.When(f => f.GetOrCreate(Arg.Is<WorkspaceKey>(k => !keys.Contains(k))))
            .Do(callInfo => throw new KeyNotFoundException(
                $"Workspace '{callInfo.Arg<WorkspaceKey>()}' is not registered."));

        var resolver = new WorkspaceResolver(registry, factoryMock);
        return (resolver, contexts);
    }

    /// <summary>
    /// Creates a <see cref="WorkspaceContext"/> with NSubstitute mocks for the services
    /// the resolver accesses during probing (<see cref="IWorkItemRepository"/>,
    /// <see cref="IAdoWorkItemService"/>). All other services use <c>null!</c>
    /// since the resolver never touches them.
    /// </summary>
    private static WorkspaceContext CreateStubContext(WorkspaceKey key)
    {
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        return new WorkspaceContext(
            key: key,
            config: null!,
            paths: null!,
            cacheStore: null!,
            workItemRepo: workItemRepo,
            contextStore: null!,
            pendingChangeStore: null!,
            adoService: adoService,
            iterationService: null!,
            processConfigProvider: null!,
            activeItemResolver: null!,
            syncCoordinatorFactory: null!,
            contextChangeService: null!,
            statusOrchestrator: null!,
            workingSetService: null!,
            flusher: null!,
            promptStateWriter: null!);
    }

    private static void SetupCacheHit(WorkspaceContext ctx, int id)
    {
        var workItem = WorkItemBuilder.Simple(id, $"Item {id}");
        ctx.WorkItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Aggregates.WorkItem?>(workItem));
    }

    private static void SetupAdoHit(WorkspaceContext ctx, int id)
    {
        var workItem = WorkItemBuilder.Simple(id, $"Item {id}");
        ctx.AdoService.FetchAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(workItem));
    }

    private static void SetupAdoMiss(WorkspaceContext ctx, int id)
    {
        ctx.AdoService.FetchAsync(id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not found"));
    }
}
