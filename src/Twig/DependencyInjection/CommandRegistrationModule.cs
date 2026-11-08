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
        services.AddSingleton(sp => new AuthLoginCommand(sp.GetRequiredService<Twig.Formatters.OutputFormatterFactory>(),
            () => sp.GetRequiredService<IConnectionBindingService>(), sp.GetRequiredService<Twig.Infrastructure.Config.TwigConfiguration>(),
            sp.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>(), sp.GetRequiredService<Twig.Rendering.RendererFactory>()));
        services.AddSingleton<AuthPatCommand>();
        services.AddSingleton<AuthIdentitiesCommand>();
        services.AddSingleton<AuthGitHubCommand>();
        services.AddSingleton(sp => new ConnectionBindCommand(() => sp.GetRequiredService<IConnectionBindingService>(),
            sp.GetRequiredService<Twig.Infrastructure.Config.TwigConfiguration>(),
            sp.GetRequiredService<Twig.Formatters.OutputFormatterFactory>(), sp.GetRequiredService<Twig.Rendering.RendererFactory>()));
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
