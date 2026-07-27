using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using System.Reflection;
using Xunit;

namespace Twig.Cli.Tests.DependencyInjection;

public sealed class CommandServiceModuleTests
{
    private static ServiceProvider BuildProviderWithConfig(TwigConfiguration config)
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton(Substitute.For<IProcessConfigurationProvider>());
        services.AddSingleton(Substitute.For<IProcessTypeStore>());
        services.AddSingleton(Substitute.For<IFieldDefinitionStore>());
        services.AddSingleton(Substitute.For<ISeedLinkRepository>());
        services.AddSingleton(Substitute.For<IPublishIdMapRepository>());
        services.AddSingleton(Substitute.For<ISeedPublishRulesProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IConsoleInput>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());
        services.AddSingleton(new OutputFormatterFactory(new HumanOutputFormatter()));
        services.AddSingleton(Substitute.For<IAsyncRenderer>());
        services.AddSingleton<RenderingPipelineFactory>();
        services.AddSingleton(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-test"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "twig.db")));

        services.AddSingleton(config);
        // Surface-neutral domain services moved to Infrastructure (wayfinder 0016);
        // compose both seams rather than re-listing either.
        services.AddConnectionDomainServices();
        services.AddTwigCommandServices();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildFullProvider() =>
        BuildProviderWithConfig(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" }
        });

    [Fact]
    public void ContextChangeService_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var service = provider.GetRequiredService<ContextChangeService>();

        service.ShouldNotBeNull();
    }

    [Fact]
    public void SyncCoordinatorFactory_Resolves_WithBothTiers()
    {
        using var provider = BuildFullProvider();

        // GetRequiredService throws if the registration is missing
        provider.GetRequiredService<SyncCoordinatorFactory>();
    }

    [Fact]
    public void SyncCoordinator_Resolves_ToFactoryReadWrite()
    {
        using var provider = BuildFullProvider();

        var pair = provider.GetRequiredService<SyncCoordinatorFactory>();
        var coordinator = provider.GetRequiredService<SyncCoordinator>();

        coordinator.ShouldBeSameAs(pair.ReadWrite);
    }

    [Fact]
    public void CommandContext_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var ctx = provider.GetRequiredService<CommandContext>();

        ctx.ShouldNotBeNull();
        ctx.PipelineFactory.ShouldNotBeNull();
        ctx.FormatterFactory.ShouldNotBeNull();
        ctx.HintEngine.ShouldNotBeNull();
        ctx.Config.ShouldNotBeNull();
    }

    [Fact]
    public void CommandContext_TelemetryClient_IsNullWhenNotRegistered()
    {
        using var provider = BuildFullProvider();

        var ctx = provider.GetRequiredService<CommandContext>();

        ctx.TelemetryClient.ShouldBeNull();
    }

    [Fact]
    public void StatusFieldConfigReader_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var reader = provider.GetRequiredService<StatusFieldConfigReader>();

        reader.ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed orchestrator wiring (#279)
    //
    //  Both seed orchestrators carry an additive constructor overload that
    //  OMITS IPendingChangeStore, retained for public-API compatibility and
    //  documented as degraded: without it, discarding or publishing a seed
    //  that carries a staged note fails on a SQLite FK violation (#268, #270).
    //
    //  A plain "does it resolve" test is NOT enough. SeedDiscardOrchestrator is
    //  registered as a bare AddSingleton<T>() (TwigServiceRegistration.cs:100),
    //  so .NET DI selects the greediest SATISFIABLE constructor. If
    //  IPendingChangeStore ever stops being registered, DI silently falls back
    //  to the degraded overload and resolution still succeeds. These tests
    //  therefore assert the private field was actually populated.
    // ═══════════════════════════════════════════════════════════════

    private static ServiceProvider BuildProviderWithSeedOrchestrators()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IFieldDefinitionStore>());
        services.AddSingleton(Substitute.For<ISeedLinkRepository>());
        services.AddSingleton(Substitute.For<IPublishIdMapRepository>());
        services.AddSingleton(Substitute.For<ISeedPublishRulesProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());

        // Mirrors the production registrations:
        //   TwigServiceRegistration.cs:100 (discard, bare AddSingleton)
        //   CommandServiceModule.cs:98     (publish, explicit factory)
        services.AddSingleton<SeedDiscardOrchestrator>();
        services.AddSingleton<BacklogOrderer>();
        services.AddSingleton<SeedPublishOrchestrator>(sp => new SeedPublishOrchestrator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<ISeedLinkRepository>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<IPublishIdMapRepository>(),
            sp.GetRequiredService<ISeedPublishRulesProvider>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<BacklogOrderer>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        return services.BuildServiceProvider();
    }

    private static object? ReadPrivateField(object target, string fieldName) =>
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull($"expected a private field '{fieldName}' on {target.GetType().Name}")
            .GetValue(target);

    [Fact]
    public void SeedDiscardOrchestrator_Resolves_WithPendingChangeStoreWired()
    {
        using var provider = BuildProviderWithSeedOrchestrators();

        var orchestrator = provider.GetRequiredService<SeedDiscardOrchestrator>();

        ReadPrivateField(orchestrator, "_pendingChangeStore")
            .ShouldNotBeNull(
                "SeedDiscardOrchestrator resolved via the DEGRADED constructor overload. "
                + "Discarding a seed with a staged note will fail on a SQLite FK violation (#268).");
    }

    [Fact]
    public void SeedPublishOrchestrator_Resolves_WithPendingChangeStoreWired()
    {
        using var provider = BuildProviderWithSeedOrchestrators();

        var orchestrator = provider.GetRequiredService<SeedPublishOrchestrator>();

        ReadPrivateField(orchestrator, "_pendingChangeStore")
            .ShouldNotBeNull(
                "SeedPublishOrchestrator resolved via the DEGRADED constructor overload. "
                + "Publishing a seed with a staged note will duplicate the ADO item (#270).");
    }

}
