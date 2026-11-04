using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;

namespace Twig.DependencyInjection;

/// <summary>Native host utilities; Core data owners are registered after workspace admission.</summary>
public static class CommandRegistrationModule
{
    public static IServiceCollection AddTwigCommands(this IServiceCollection services)
    {
        services.AddSingleton<AuthLoginCommand>();
        services.AddSingleton<AuthPatCommand>();
        services.AddSingleton<AuthIdentitiesCommand>();
        services.AddSingleton<AuthGitHubCommand>();
        services.AddSingleton<ConnectionBindCommand>();
        services.AddSingleton<ConnectionListCommand>();
        services.AddSingleton<ConnectionStatusCommand>();
        services.AddSingleton<IConnectionRemoteWriteReconciliationService>(sp =>
            new ConnectionRemoteWriteReconciliationService(
                Path.GetDirectoryName(sp.GetRequiredService<IConnectionBindingService>().RegistryPath)!,
                sp.GetRequiredService<IConnectionBindingService>()));
        services.AddSingleton<ConnectionWritesCommand>();
        services.AddSingleton(sp => new UserBenchCommand(
            UserBenchStore.ForRegistry(sp.GetRequiredService<IConnectionBindingService>().RegistryPath),
            sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
            sp.GetService<Twig.Infrastructure.Telemetry.ITelemetryClient>(),
            sp.GetService<CoreWorkspaceFactory>()));
        services.AddSingleton(sp => new BenchPlanningCommand(
            UserBenchStore.ForRegistry(sp.GetRequiredService<IConnectionBindingService>().RegistryPath),
            sp.GetRequiredService<Twig.Rendering.RendererFactory>(), sp.GetService<CoreWorkspaceFactory>(),
            sp.GetService<Twig.Infrastructure.Telemetry.ITelemetryClient>()));
        return services;
    }
}
