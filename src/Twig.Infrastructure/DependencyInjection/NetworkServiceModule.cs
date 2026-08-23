using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
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
                new WorkItemMapper(),
                sp.GetService<IFieldDefinitionStore>(),
                sp.GetRequiredService<AdoConcurrencyThrottle>());
        });

        // IRevisionBoundAdoWorkItemService aliases the same AdoRestClient instance so
        // interface-segregated consumers (plan lifecycle) resolve without the full
        // IAdoWorkItemService surface. Cast is safe: AdoRestClient implements both.
        services.AddSingleton<IRevisionBoundAdoWorkItemService>(sp =>
            (IRevisionBoundAdoWorkItemService)sp.GetRequiredService<IAdoWorkItemService>());

        // IAdoGitService — conditional registration; only requires git project.
        // Repository is optional — when null, GetRepositoryIdAsync returns null
        // but GetRepositoryIdByNameAsync still works for --repo flag support.
        if (!string.IsNullOrWhiteSpace(resolvedGitProject))
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

        services.AddSingleton(sp =>
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
        services.AddSingleton<IIterationService>(sp => sp.GetRequiredService<AdoIterationService>());
        services.AddSingleton<IProcessRuleProvider>(sp => sp.GetRequiredService<AdoIterationService>());
        services.AddSingleton<IFormLayoutProvider>(sp => sp.GetRequiredService<AdoIterationService>());
        services.AddSingleton<IProcessTypeFieldProvider>(sp => sp.GetRequiredService<AdoIterationService>());

        // 🔴 A SEPARATE instance, not another face of AdoIterationService. That service
        // memoizes every route it calls; the description must not cache anything, because a
        // stale description is a wrong description and the artifact is a truth claim about a
        // process at a moment in time. Registering it as a face of the cached service would
        // silently break the no-caching ruling.
        services.AddSingleton<IProcessDescriptionSource>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new AdoProcessDescriptionSource(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project);
        });

        // The assembler that consumes this source is registered in
        // AddConnectionDomainServices, not here: it is surface-neutral (both the CLI and the
        // agent surface assemble through it), while this registration is network-layer
        // because it needs the HTTP client and the auth provider. Registering it in both
        // places is last-wins and harmless at runtime, but it hides the boundary — the same
        // trap wayfinder 0016 already recorded for the mutation providers.

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
