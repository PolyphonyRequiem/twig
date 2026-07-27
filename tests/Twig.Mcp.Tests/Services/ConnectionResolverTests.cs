using Twig.Infrastructure.Config;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class ConnectionResolverTests
{
    private static readonly Connection KeyA = new("orgA", "proj1");
    private static readonly Connection KeyB = new("orgB", "proj2");
    private static readonly Connection KeyC = new("orgC", "proj3");

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

        var ex = Should.Throw<KeyNotFoundException>(() => resolver.Resolve("unknown/missing"));

        ex.Message.ShouldContain("Retry without the 'workspace' parameter");
        ex.Message.ShouldContain("orgA/proj1");
    }

    [Fact]
    public void TryResolve_ExplicitWorkspace_UnknownKey_ReturnsInferenceHint()
    {
        var (resolver, _) = CreateResolver(KeyA);

        var success = resolver.TryResolve("unknown/missing", out _, out var error);

        success.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("Retry without the 'workspace' parameter");
        error.ShouldContain("orgA/proj1");
    }

    [Fact]
    public void Resolve_ExplicitWorkspace_UnknownKeyWithoutInference_DoesNotSuggestOmission()
    {
        var (resolver, _) = CreateResolver(KeyA, KeyB);

        var ex = Should.Throw<KeyNotFoundException>(() => resolver.Resolve("unknown/missing"));

        ex.Message.ShouldNotContain("Retry without the 'workspace' parameter");
    }

    [Fact]
    public void Resolve_ExplicitWorkspace_Malformed_ThrowsFormatException()
    {
        var (resolver, _) = CreateResolver(KeyA);

        Should.Throw<FormatException>(() => resolver.Resolve("no-slash"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_SingleWorkspace_NullOrEmptyParam_ReturnsSoleWorkspace(string? workspace)
    {
        var (resolver, contexts) = CreateResolver(KeyA);

        var result = resolver.Resolve(workspace);

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

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => resolver.ResolveForSetAsync(12345, "unknown/missing"));

        ex.Message.ShouldContain("Retry without the 'workspace' parameter");
        ex.Message.ShouldContain("orgA/proj1");
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

    [Fact]
    public async Task ResolveForSet_AdoProbe_NonNotFoundError_Propagates()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // KeyA returns an auth error — should propagate, not be swallowed
        contexts[KeyA].Get<IAdoWorkItemService>().FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => resolver.ResolveForSetAsync(99));
    }

    [Fact]
    public async Task ResolveForSet_AdoProbe_OfflineError_Propagates()
    {
        var (resolver, contexts) = CreateResolver(KeyA, KeyB);

        // ADO is unreachable — should propagate, not be misreported as "not found"
        contexts[KeyA].Get<IAdoWorkItemService>().FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoOfflineException(new HttpRequestException("DNS failure")));

        await Should.ThrowAsync<AdoOfflineException>(
            () => resolver.ResolveForSetAsync(99));
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
        await contexts[KeyA].Get<IAdoWorkItemService>().DidNotReceive().FetchAsync(50, Arg.Any<CancellationToken>());
        await contexts[KeyB].Get<IAdoWorkItemService>().DidNotReceive().FetchAsync(50, Arg.Any<CancellationToken>());
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
        var workspaces = new List<Connection> { KeyA, KeyB };
        var ex = new AmbiguousWorkspaceException(workspaces);

        ex.WorkItemId.ShouldBeNull();
        ex.AvailableWorkspaces.ShouldBe(workspaces);
        ex.Message.ShouldContain("orgA/proj1");
        ex.Message.ShouldContain("orgB/proj2");
    }

    [Fact]
    public void AmbiguousWorkspaceException_WithWorkItemId_HasCorrectProperties()
    {
        var workspaces = new List<Connection> { KeyA, KeyB };
        var ex = new AmbiguousWorkspaceException(42, workspaces);

        ex.WorkItemId.ShouldBe(42);
        ex.AvailableWorkspaces.ShouldBe(workspaces);
        ex.Message.ShouldContain("#42");
    }

    // ── WorkItemNotFoundException properties ────────────────────────

    [Fact]
    public void WorkItemNotFoundException_HasCorrectProperties()
    {
        var workspaces = new List<Connection> { KeyA, KeyB };
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

    private static (ConnectionResolver Resolver, Dictionary<Connection, ConnectionScope> Contexts)
        CreateResolver(params Connection[] keys)
    {
        var registry = Substitute.For<IConnectionRegistry>();
        registry.Workspaces.Returns(keys.ToList().AsReadOnly());
        registry.IsSingleWorkspace.Returns(keys.Length == 1);

        var contexts = new Dictionary<Connection, ConnectionScope>();
        var factoryMock = Substitute.For<IConnectionScopeFactory>();

        foreach (var key in keys)
        {
            var ctx = CreateStubContext(key);
            contexts[key] = ctx;
            factoryMock.GetOrCreate(key).Returns(ctx);
        }

        // Unknown keys throw KeyNotFoundException
        factoryMock.When(f => f.GetOrCreate(Arg.Is<Connection>(k => !keys.Contains(k))))
            .Do(callInfo => throw new KeyNotFoundException(
                $"Workspace '{callInfo.Arg<Connection>()}' is not registered."));

        var resolver = new ConnectionResolver(registry, factoryMock);
        return (resolver, contexts);
    }

    /// <summary>
    /// Builds a <see cref="ConnectionScope"/> over substitutes. Only the repository and ADO
    /// service matter here — the resolver never touches anything else — but the scope is built
    /// from the real registrations, so it cannot drift from production wiring.
    /// </summary>
    private static ConnectionScope CreateStubContext(Connection key)
        => TestConnectionScope.Build(
            key,
            new TwigConfiguration { Display = new DisplayConfig { CacheStaleMinutes = 5 } },
            Substitute.For<IContextStore>(),
            Substitute.For<IWorkItemRepository>(),
            Substitute.For<IAdoWorkItemService>(),
            Substitute.For<IPendingChangeStore>(),
            Substitute.For<IWorkItemLinkRepository>(),
            Substitute.For<IIterationService>(),
            Substitute.For<IProcessConfigurationProvider>(),
            Substitute.For<IPromptStateWriter>(),
            Substitute.For<IProcessTypeStore>(),
            Substitute.For<IFieldDefinitionStore>(),
            Substitute.For<ISeedLinkRepository>(),
            Substitute.For<IPublishIdMapRepository>(),
            Substitute.For<ISeedPublishRulesProvider>(),
            Substitute.For<IUnitOfWork>());

    private static void SetupCacheHit(ConnectionScope ctx, int id)
    {
        var workItem = WorkItemBuilder.Simple(id, $"Item {id}");
        ctx.Get<IWorkItemRepository>().GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Aggregates.WorkItem?>(workItem));
    }

    private static void SetupAdoHit(ConnectionScope ctx, int id)
    {
        var workItem = WorkItemBuilder.Simple(id, $"Item {id}");
        ctx.Get<IAdoWorkItemService>().FetchAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(workItem));
    }

    private static void SetupAdoMiss(ConnectionScope ctx, int id)
    {
        ctx.Get<IAdoWorkItemService>().FetchAsync(id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoNotFoundException(id));
    }
}
