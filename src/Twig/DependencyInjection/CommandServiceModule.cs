using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.DependencyInjection;

/// <summary>
/// Registers command-support services: hint engine, editor launcher, console input,
/// and shared domain services (<see cref="ActiveItemResolver"/>, <see cref="ProtectedCacheWriter"/>,
/// <see cref="SyncCoordinator"/>).
/// </summary>
/// <remarks>
/// Shared services are registered here (CLI layer) rather than in
/// <c>TwigServiceRegistration.AddTwigCoreServices()</c> (Infrastructure layer) because they
/// depend on <see cref="IAdoWorkItemService"/> which is registered with CLI-layer factory logic (DD-12).
/// </remarks>
public static class CommandServiceModule
{
    public static IServiceCollection AddTwigCommandServices(this IServiceCollection services)
    {
        // Hint engine — reads display config at startup, uses process config for dynamic state resolution
        services.AddSingleton<HintEngine>(sp =>
            new HintEngine(
                sp.GetRequiredService<TwigConfiguration>().Display,
                sp.GetRequiredService<IProcessConfigurationProvider>()));

        // Editor launcher and console input
        services.AddSingleton<IEditorLauncher>(sp => new EditorLauncher(sp.GetRequiredService<TwigPaths>()));
        services.AddSingleton<IConsoleInput, ConsoleInput>();

        // Shared domain services (DD-12: registered in CLI layer)
        services.AddSingleton<ActiveItemResolver>(sp => new ActiveItemResolver(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>()));

        services.AddSingleton<ProtectedCacheWriter>(sp => new ProtectedCacheWriter(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        // DD-13: SyncCoordinator accepts int cacheStaleMinutes (not TwigConfiguration)
        // to avoid Domain → Infrastructure circular reference.
        services.AddSingleton<SyncCoordinator>(sp => new SyncCoordinator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

        // DD-02: WorkingSetService accepts string? userDisplayName primitive (same pattern)
        services.AddSingleton<WorkingSetService>(sp => new WorkingSetService(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

        // EPIC-003: Seed publish orchestrator
        services.AddSingleton<BacklogOrderer>(sp => new BacklogOrderer(
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IFieldDefinitionStore>()));
        services.AddSingleton<SeedPublishOrchestrator>(sp => new SeedPublishOrchestrator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<ISeedLinkRepository>(),
            sp.GetRequiredService<IPublishIdMapRepository>(),
            sp.GetRequiredService<ISeedPublishRulesProvider>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<BacklogOrderer>()));
        services.AddSingleton<SeedReconcileOrchestrator>(sp => new SeedReconcileOrchestrator(
            sp.GetRequiredService<ISeedLinkRepository>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPublishIdMapRepository>()));

        // EPIC-002: Domain orchestration services
        services.AddSingleton<FlowTransitionService>(sp => new FlowTransitionService(
            sp.GetRequiredService<ActiveItemResolver>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<ProtectedCacheWriter>()));

        services.AddSingleton<RefreshOrchestrator>(sp => new RefreshOrchestrator(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<SyncCoordinator>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<IFieldDefinitionStore>()));

        services.AddSingleton<StatusOrchestrator>(sp => new StatusOrchestrator(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<ActiveItemResolver>(),
            sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<SyncCoordinator>()));

        // PendingChangeFlusher — flush loop shared by SaveCommand, SyncCommand, FlowDoneCommand
        services.AddSingleton<IPendingChangeFlusher>(sp => new PendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>()));

        return services;
    }
}
