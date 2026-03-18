using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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
        // Hint engine — reads display config at startup
        services.AddSingleton<HintEngine>(sp =>
            new HintEngine(sp.GetRequiredService<TwigConfiguration>().Display));

        // Editor launcher and console input
        services.AddSingleton<IEditorLauncher, EditorLauncher>();
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
            sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

        // DD-02: WorkingSetService accepts string? userDisplayName primitive (same pattern)
        services.AddSingleton<WorkingSetService>(sp => new WorkingSetService(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

        return services;
    }
}
