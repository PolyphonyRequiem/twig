using System.Net;
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
        // Auth provider (resolve from config via centralized factory)
        services.AddSingleton<IAuthenticationProvider>(sp =>
            AuthProviderFactory.Create(sp.GetRequiredService<TwigConfiguration>().Auth.Method));

        // HTTP client — singleton backed by SocketsHttpHandler for automatic
        // gzip/Brotli decompression and HTTP/2 multiplexing with HTTP/1.1 fallback.
        services.AddSingleton<HttpClient>(_ => CreateHttpClient());

        // Process-wide ADO concurrency limiter — shared across all ADO HTTP call sites.
        services.AddSingleton<AdoConcurrencyThrottle>();

        services.AddSingleton<IAdoWorkItemService>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new AdoRestClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                sp.GetService<IFieldDefinitionStore>(),
                sp.GetRequiredService<AdoConcurrencyThrottle>());
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

    internal static HttpClient CreateHttpClient()
    {
        var handler = CreateSocketsHandler();
        return new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    internal static SocketsHttpHandler CreateSocketsHandler()
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        };
    }
}
