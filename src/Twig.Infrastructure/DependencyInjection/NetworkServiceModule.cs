using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.DependencyInjection;

/// <summary>
/// Registers network-layer services: authentication, HTTP, ADO work-item and git clients, iteration service.
/// Lives in <c>Twig.Infrastructure</c> because all network/ADO types are defined here.
/// </summary>
public static class NetworkServiceModule
{
    /// <summary>
    /// Registers network services into the DI container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Configuration loaded at startup (used for org/project/team).</param>
    /// <param name="resolvedGitProject">Git project resolved after auto-detection (may differ from config).</param>
    /// <param name="resolvedRepository">Git repository resolved after auto-detection (may differ from config).</param>
    public static IServiceCollection AddTwigNetworkServices(
        this IServiceCollection services,
        TwigConfiguration config,
        string? resolvedGitProject = null,
        string? resolvedRepository = null)
    {
        // Auth provider (resolve from config)
        services.AddSingleton<IAuthenticationProvider>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            if (string.Equals(cfg.Auth.Method, "pat", StringComparison.OrdinalIgnoreCase))
                return new PatAuthProvider();
            return new AzCliAuthProvider();
        });

        // HTTP client — singleton is acceptable for short-lived CLI process.
        services.AddSingleton<HttpClient>();

        services.AddSingleton<IAdoWorkItemService>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new AdoRestClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                sp.GetService<IFieldDefinitionStore>());
        });

        // IAdoGitService — conditional registration; only when git project and repository are resolved.
        if (!string.IsNullOrWhiteSpace(resolvedGitProject) && !string.IsNullOrWhiteSpace(resolvedRepository))
        {
            var capturedGitProject = resolvedGitProject;
            var capturedRepository = resolvedRepository;
            services.AddSingleton<IAdoGitService>(sp =>
                new AdoGitClient(
                    sp.GetRequiredService<HttpClient>(),
                    sp.GetRequiredService<IAuthenticationProvider>(),
                    sp.GetRequiredService<TwigConfiguration>().Organization,
                    capturedGitProject,
                    capturedRepository,
                    sp.GetRequiredService<TwigConfiguration>().Project));
        }

        services.AddSingleton<IIterationService>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            var team = string.IsNullOrWhiteSpace(cfg.Team) ? $"{cfg.Project} Team" : cfg.Team;
            return new AdoIterationService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                team);
        });

        return services;
    }
}
