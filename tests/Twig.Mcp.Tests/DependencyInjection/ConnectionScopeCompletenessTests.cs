using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services.Mutation;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.DependencyInjection;

/// <summary>
/// Guards the wayfinder 0016 seam: every surface-neutral service an MCP tool resolves from a
/// <see cref="ConnectionScope"/> must be registered by the SHARED Infrastructure module.
/// </summary>
/// <remarks>
/// Before 0016, MCP could not reference the CLI-only <c>CommandServiceModule</c>, so
/// <c>WorkspaceContextFactory.CreateContext</c> hand-mirrored those registrations across 183
/// lines. A mirror has no compiler forcing the two copies to agree, so a dependency added on the
/// CLI side was simply absent on the MCP side — the mechanism behind PolyphonyRequiem/twig#269
/// and #270, and the reason wiring one new dependency for 0015 needed four separate edits.
/// <para/>
/// This test binds to the real registration call, not to a fixture copy of it. Delete any
/// registration from <c>AddConnectionDomainServices</c> and it fails — which is what makes it a
/// guard rather than a restatement.
/// </remarks>
public sealed class ConnectionScopeCompletenessTests
{
    /// <summary>
    /// Every service type resolved via <c>ctx.Get&lt;T&gt;()</c> anywhere in the MCP tool layer.
    /// </summary>
    public static TheoryData<Type> SurfaceNeutralServices() =>
    [
        typeof(ActiveItemResolver),
        typeof(ProtectedCacheWriter),
        typeof(SyncCoordinatorFactory),
        typeof(SyncCoordinator),
        typeof(WorkingSetService),
        typeof(ContextChangeService),
        typeof(RefreshOrchestrator),
        typeof(ParentStatePropagationService),
        typeof(StateTransitionWorkflow),
        typeof(FieldUpdateWorkflow),
        typeof(NoteWorkflow),
        typeof(DiscardWorkflow),
        typeof(DeleteWorkflow),
        typeof(PatchWorkflow),
        typeof(BacklogOrderer),
        typeof(SeedPublishOrchestrator),
        typeof(SeedReconcileOrchestrator),
        typeof(SeedMutationProvider),
        typeof(AdoMutationProvider),
        typeof(SprintIterationResolver),
        typeof(WorkItemFetcher),
    ];

    [Theory]
    [MemberData(nameof(SurfaceNeutralServices))]
    public void SharedModule_Registers_EverySurfaceNeutralService(Type serviceType)
    {
        using var scope = BuildScopeOverSubstitutes();

        Resolve(scope, serviceType).ShouldNotBeNull(
            $"{serviceType.Name} is resolved from a ConnectionScope by the MCP tool layer, so it " +
            "must be registered by the shared Infrastructure module (AddConnectionDomainServices). " +
            "If this fails, MCP and the CLI no longer share one definition — the wayfinder 0016 " +
            "seam has regressed and the #269/#270 drift class is expressible again.");
    }

    /// <summary>
    /// The mutation workflows are the specific services whose constructors gained a dependency in
    /// #269/#270/0015. Resolving them proves the shared module supplies their FULL dependency
    /// graph, not merely that the type name is registered.
    /// </summary>
    [Fact]
    public void SharedModule_Resolves_MutationWorkflows_WithCompleteDependencyGraph()
    {
        using var scope = BuildScopeOverSubstitutes();

        scope.Get<StateTransitionWorkflow>().ShouldNotBeNull();
        scope.Get<PatchWorkflow>().ShouldNotBeNull();
        scope.Get<SeedPublishOrchestrator>().ShouldNotBeNull();
    }

    /// <summary>
    /// Resolves <paramref name="serviceType"/> through the scope's own generic accessor, so the
    /// test exercises exactly the path the tool layer uses.
    /// </summary>
    private static object? Resolve(ConnectionScope scope, Type serviceType) =>
        typeof(ConnectionScope).GetMethod(nameof(ConnectionScope.Get))!
            .MakeGenericMethod(serviceType)
            .Invoke(scope, null);

    private static ConnectionScope BuildScopeOverSubstitutes()
        => TestConnectionScope.Build(
            new Connection("testorg", "testproject"),
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
}
