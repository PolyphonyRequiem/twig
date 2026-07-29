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
    //  Seed orchestrator wiring (#279, tightened by wayfinder 0004 §4)
    //
    //  HISTORY: both seed orchestrators used to carry additive constructor
    //  overloads that OMITTED IPendingChangeStore. Because SeedDiscardOrchestrator
    //  is registered as a bare AddSingleton<T>() (TwigServiceRegistration.cs),
    //  .NET DI picks the greediest SATISFIABLE constructor — so if the store
    //  stopped being registered, DI silently fell back to the degraded overload
    //  and resolution still SUCCEEDED, reintroducing #268/#270 with a green
    //  "does it resolve" test. That is why these assert the field, not the resolve.
    //
    //  0004 §4 ruled those overloads deleted rather than defaulted, and they now
    //  are: each orchestrator has exactly ONE constructor with the store required.
    //  The silent-fallback path is therefore unexpressible — a missing registration
    //  now throws at resolve instead of quietly degrading.
    //
    //  These tests are RETAINED deliberately. They are cheap, and they still fail
    //  loudly if anyone reintroduces an overload that makes the dependency optional
    //  again. The assertion message describes that regression, not the old default.
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
        services.AddSingleton(Substitute.For<IPublishIntentRepository>());

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
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IPublishIntentRepository>()));

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
                "SeedDiscardOrchestrator._pendingChangeStore is null. 0004 §4 requires this "
                + "dependency, so an optional overload must not be reintroduced (#268). "
                + "Discarding a seed with a staged note will fail on a SQLite FK violation (#268).");
    }

    [Fact]
    public void SeedPublishOrchestrator_Resolves_WithPendingChangeStoreWired()
    {
        using var provider = BuildProviderWithSeedOrchestrators();

        var orchestrator = provider.GetRequiredService<SeedPublishOrchestrator>();

        ReadPrivateField(orchestrator, "_pendingChangeStore")
            .ShouldNotBeNull(
                "SeedPublishOrchestrator._pendingChangeStore is null. 0004 §4 requires this "
                + "dependency, so an optional overload must not be reintroduced (#270). "
                + "Publishing a seed with a staged note will duplicate the ADO item (#270).");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Wayfinder 0004 §4 — the rule enforced structurally, not by prose
    //
    //  0004: "The nullable IPendingChangeStore? legacy overloads are DELETED,
    //  not defaulted. A dependency correctness depends on is not optional."
    //
    //  The field assertions above catch a store that failed to wire on THIS
    //  container. They cannot catch the actual regression 0004 named: someone
    //  re-adding a convenience overload that omits the store, or making the
    //  parameter nullable/defaulted again. DI would then resolve the greedy
    //  ctor here and stay green while every other construction site silently
    //  got the degraded one — which is exactly how #268/#270 recurred.
    //
    //  So assert the SHAPE: exactly one public constructor, and the pending
    //  store parameter is non-nullable with no default.
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(typeof(SeedDiscardOrchestrator))]
    [InlineData(typeof(SeedPublishOrchestrator))]
    public void SeedOrchestrator_HasExactlyOneConstructor_WithPendingChangeStoreRequired(Type orchestratorType)
    {
        var ctors = orchestratorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);

        ctors.Length.ShouldBe(1,
            $"{orchestratorType.Name} must expose exactly ONE constructor (wayfinder 0004 §4). "
            + "An additional overload lets .NET DI pick the greediest SATISFIABLE ctor, so a "
            + "missing registration silently degrades instead of throwing — the #268/#270 trap. "
            + $"Found {ctors.Length}: "
            + string.Join(" | ", ctors.Select(c =>
                "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")));

        var store = ctors[0].GetParameters()
            .SingleOrDefault(p => p.ParameterType == typeof(IPendingChangeStore));

        store.ShouldNotBeNull(
            $"{orchestratorType.Name}'s constructor must take IPendingChangeStore.");

        store.HasDefaultValue.ShouldBeFalse(
            $"{orchestratorType.Name}'s IPendingChangeStore parameter must not have a default "
            + "value. 0004 §4 ruled the legacy overloads DELETED, not defaulted — a defaulted "
            + "parameter reintroduces the same silent-degradation path the overloads had.");

        var nullability = new NullabilityInfoContext().Create(store);
        nullability.WriteState.ShouldBe(NullabilityState.NotNull,
            $"{orchestratorType.Name}'s IPendingChangeStore parameter must be non-nullable. "
            + "A nullable dependency makes correctness depend on the caller remembering to "
            + "pass it (wayfinder 0004 §4).");
    }

}
