using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Process;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Providers;

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
        // GH#466: a mutable handoff slot so `init` can publish the effective endpoint it
        // resolves from its OWN arguments, read lazily when the deferred bootstrap provider
        // below is actually admitted (never at this factory's construction time, which still
        // only sees the configuration discovered at startup).
        services.TryAddSingleton<BootstrapEndpointSelection>();

        services.AddSingleton<IAuthenticationProvider>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            var bindings = sp.GetRequiredService<IConnectionBindingService>();
            var intent = sp.GetService<ConnectionBindingOperationIntent>();

            // Explicit init metadata acquisition still routes through the
            // bootstrap provider (no attached binding yet) — but admission is
            // deferred so local init preconditions can fail without forcing a
            // default-binding lookup against the shared registry.
            if (intent?.InitializationMetadataOnly == true)
            {
                var endpointSelection = sp.GetRequiredService<BootstrapEndpointSelection>();
                return new DeferredBoundAuthenticationProvider(
                    ct => bindings.CreateBootstrapProviderAsync(endpointSelection.Effective ?? cfg, ct, endpointSelection.BindingId),
                    initializationMetadataOnly: true);
            }

            // Normal attached-worktree route: ResolveAsync admits the central
            // binding + registered identity before a bound provider is minted.
            // The deferral is what keeps that admission off the construction
            // path for commands whose local validation (portable manifest
            // conflicts, help, offline flows) completes without an HTTP call.
            var paths = sp.GetRequiredService<TwigPaths>();
            return new DeferredBoundAuthenticationProvider(async ct =>
            {
                var resolved = await bindings.ResolveAsync(cfg, paths, ct).ConfigureAwait(false);
                return bindings.CreateAuthenticationProvider(resolved);
            });
        });

        // HTTP client — singleton backed by SocketsHttpHandler for automatic
        // gzip/Brotli decompression and HTTP/2 multiplexing with HTTP/1.1 fallback.
        services.TryAddSingleton<HttpClient>(_ => CreateHttpClient());

        // Process-wide ADO concurrency limiter — shared across all ADO HTTP call sites.
        services.TryAddSingleton<AdoConcurrencyThrottle>();

        // Epic #1167: every ADO client below is built through AdoConfiguration, which refuses
        // unless the workspace provider resolves to ADO. Aliases resolve through these
        // factories, so they inherit the gate.
        services.AddSingleton<IAdoWorkItemService>(sp =>
        {
            var cfg = AdoConfiguration(sp);
            return new AdoRestClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                new WorkItemMapper(),
                sp.GetService<IFieldDefinitionStore>(),
                sp.GetRequiredService<AdoConcurrencyThrottle>());
        });
        // Epic #1167: the active provider follows the explicit workspace `provider` setting
        // (absent => ado). GitHub reads require a durable neutral target attachment;
        // configured slugs and explicit credential selection never substitute for that identity.
        services.AddSingleton<IWorkItemProvider>(sp =>
        {
            var kind = sp.GetRequiredService<TwigConfiguration>().ResolveWorkItemProviderKind();
            if (kind == ProviderCapabilityCatalog.GitHubProviderKind)
                return sp.GetRequiredService<GitHubWorkItemProvider>();
            AdoConfiguration(sp);
            return (IWorkItemProvider)sp.GetRequiredService<IAdoWorkItemService>();
        });
        AddGitHubProvider(services, config);

        // IRevisionBoundAdoWorkItemService and IAdoAssignedIdentityReader alias the
        // same AdoRestClient instance so interface-segregated consumers (plan
        // lifecycle, claim projection) resolve without the full IAdoWorkItemService
        // surface. Casts are safe: AdoRestClient implements all three.
        services.AddSingleton<IRevisionBoundAdoWorkItemService>(sp =>
            (IRevisionBoundAdoWorkItemService)sp.GetRequiredService<IAdoWorkItemService>());
        services.AddSingleton<IAdoAssignedIdentityReader>(sp =>
            (IAdoAssignedIdentityReader)sp.GetRequiredService<IAdoWorkItemService>());

        // Feature #1177: the provider read boundary that show, query, refresh and sync resolve.
        // The provider switch runs BEFORE any ADO factory, so a non-ADO workspace refuses here
        // without constructing an ADO client. Under ADO it is the same AdoRestClient instance.
        services.AddSingleton<IProviderWorkItemReader>(sp =>
        {
            RequireAdoBoundary(sp, "provider read boundary for these reads");
            return sp.GetRequiredService<IAdoWorkItemService>();
        });
        services.AddSingleton<IWorkItemDiscussionReader>(sp =>
        {
            RequireAdoBoundary(sp, "discussion read boundary");
            return (IWorkItemDiscussionReader)sp.GetRequiredService<IAdoWorkItemService>();
        });
        services.AddSingleton<IWorkItemAttachmentReader>(sp =>
        {
            RequireAdoBoundary(sp, "attachment read boundary");
            var cfg = AdoConfiguration(sp);
            return new AdoWorkItemAttachmentReader(
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                sp.GetRequiredService<AdoConcurrencyThrottle>());
        });
        services.AddSingleton<IWorkItemDiscussionWriter>(sp =>
        {
            RequireAdoBoundary(sp, "discussion write boundary");
            return (IWorkItemDiscussionWriter)sp.GetRequiredService<IAdoWorkItemService>();
        });
        services.AddSingleton<IWorkItemAttachmentWriter>(sp =>
        {
            RequireAdoBoundary(sp, "attachment write boundary");
            return (IWorkItemAttachmentWriter)sp.GetRequiredService<IAdoWorkItemService>();
        });
        // Feature #1178: the provider mutation boundary that update, state, note, link, patch,
        // edit, batch, the pending-change flusher and proposal apply resolve. Same ordering
        // as the read boundary: a non-ADO workspace refuses here, before any ADO client exists.
        // Legacy integer-ID mutation commands stay closed for GitHub. Its native proposal
        // executor (#1183) uses neutral identities and explicit observed-non-atomic effects,
        // not this contract's exact-revision promise.
        // The ADO adapter owns the opaque-revision precondition check, so a foreign or
        // non-canonical token is refused before any ADO call (#1178 AC (a)).
        services.AddSingleton<IProviderWorkItemWriter>(sp =>
        {
            RequireAdoBoundary(sp, "provider mutation boundary");
            return new AdoProviderWorkItemWriter(
                sp.GetRequiredService<IAdoWorkItemService>(),
                sp.GetRequiredService<IRevisionBoundAdoWorkItemService>());
        });
        // Converts the cache's ADO int revision to and from the opaque token. It only exists
        // at the ADO compatibility boundary; a non-ADO workspace refuses here with the port's
        // exact wording, before any ADO client exists (#1178 AC (a)).
        services.AddSingleton<IProviderRevisionCodec>(sp =>
        {
            RequireAdoBoundary(sp, "provider mutation boundary");
            return AdoProviderRevisionCodec.Instance;
        });
        // Authoritative revision-bound pre-operation reads (#1169 / #1178), keyed by neutral
        // identity and opaque revision token rather than ADO integers.
        services.AddSingleton<IAuthoritativeWorkItemReader>(sp =>
        {
            RequireAdoBoundary(sp, "authoritative revision-bound read");
            return new AdoAuthoritativeWorkItemReader(
                sp.GetRequiredService<IRevisionBoundAdoWorkItemService>(),
                sp.GetRequiredService<IWorkItemProvider>());
        });

        // IAdoGitService — conditional registration; only requires git project.
        // Repository is optional — when null, GetRepositoryIdAsync returns null
        // but GetRepositoryIdByNameAsync still works for --repo flag support.
        if (!string.IsNullOrWhiteSpace(resolvedGitProject))
        {
            var capturedGitProject = resolvedGitProject;
            var capturedRepository = resolvedRepository;
            services.AddSingleton<IAdoGitService>(sp =>
            {
                var cfg = AdoConfiguration(sp);
                return new AdoGitClient(
                    sp.GetRequiredService<HttpClient>(),
                    sp.GetRequiredService<IAuthenticationProvider>(),
                    cfg.Organization,
                    capturedGitProject,
                    capturedRepository,
                    cfg.Project,
                    throttle: sp.GetRequiredService<AdoConcurrencyThrottle>());
            });
        }

        services.AddSingleton(sp =>
        {
            var cfg = AdoConfiguration(sp);
            var team = TwigConfiguration.ResolveTeam(cfg.Project, cfg.Team);
            return new AdoIterationService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                team,
                throttle: sp.GetRequiredService<AdoConcurrencyThrottle>());
        });
        services.AddSingleton<IIterationService>(sp => sp.GetRequiredService<AdoIterationService>());
        // AB#1169 obligation: a GitHub workspace has no process rules, so it registers the
        // explicit empty source rather than reaching ADO. ADO keeps AdoIterationService.
        services.AddSingleton<IProcessRuleProvider>(sp =>
            sp.GetRequiredService<TwigConfiguration>().ResolveWorkItemProviderKind() == ProviderCapabilityCatalog.GitHubProviderKind
                ? EmptyProcessRuleProvider.Instance
                : sp.GetRequiredService<AdoIterationService>());
        services.AddSingleton<IFormLayoutProvider>(sp => sp.GetRequiredService<AdoIterationService>());
        services.AddSingleton<IProcessTypeFieldProvider>(sp => sp.GetRequiredService<AdoIterationService>());

        // 🔴 A SEPARATE instance, not another face of AdoIterationService. That service
        // memoizes every route it calls; the description must not cache anything, because a
        // stale description is a wrong description and the artifact is a truth claim about a
        // process at a moment in time. Registering it as a face of the cached service would
        // silently break the no-caching ruling.
        services.AddSingleton<IProcessDescriptionSource>(sp =>
        {
            var cfg = AdoConfiguration(sp);
            return new AdoProcessDescriptionSource(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                throttle: sp.GetRequiredService<AdoConcurrencyThrottle>());
        });

        // The assembler that consumes this source is registered in
        // AddConnectionDomainServices, not here: it is surface-neutral (both the CLI and the
        // agent surface assemble through it), while this registration is network-layer
        // because it needs the HTTP client and the auth provider. Registering it in both
        // places is last-wins and harmless at runtime, but it hides the boundary — the same
        // trap wayfinder 0016 already recorded for the mutation providers.

        return services;
    }

    private static TwigConfiguration AdoConfiguration(IServiceProvider sp)
    {
        var cfg = sp.GetRequiredService<TwigConfiguration>();
        WorkItemProviderSetting.RequireAdo(cfg.Provider);
        return cfg;
    }

    /// <summary>
    /// Provider switch for boundaries only ADO implements so far. Resolves the kind from the
    /// setting alone (unknown tokens still fail <c>provider-unknown</c>) and refuses any other
    /// provider before an ADO factory or <see cref="AdoConfiguration"/> is touched.
    /// </summary>
    private static void RequireAdoBoundary(IServiceProvider sp, string surface)
    {
        var kind = sp.GetRequiredService<TwigConfiguration>().ResolveWorkItemProviderKind();
        if (kind == ProviderCapabilityCatalog.AdoProviderKind)
            return;
        throw new TwigConfigurationException(
            $"{WorkItemProviderSetting.UnavailableReasonCode}: workspace provider '{kind}' has no {surface} yet.",
            new NotSupportedException(WorkItemProviderSetting.UnavailableReasonCode));
    }

    /// <summary>
    /// Epic #1167 read-only GitHub activation. Every piece resolves lazily from the live
    /// configuration, so nothing (process, credential, network) is touched until a GitHub read
    /// is actually requested. The snapshot reader is registered only for an explicit
    /// <c>provider=github</c> workspace.
    /// </summary>
    private static void AddGitHubProvider(IServiceCollection services, TwigConfiguration config)
    {
        services.TryAddSingleton<IBoundedProcessRunner, BoundedProcessRunner>();
        services.TryAddSingleton(sp => GitHubScope(sp.GetRequiredService<TwigConfiguration>()));
        services.TryAddSingleton<IGitHubTokenSource>(sp =>
            GitHubTokenSource(sp, sp.GetRequiredService<GitHubWorkspaceScope>()));
        services.TryAddSingleton(sp =>
        {
            var scope = sp.GetRequiredService<GitHubWorkspaceScope>();
            return new GitHubAuthenticatedActorReader(GitHubReadOnlyAuthHandler.CreateClient(
                sp.GetRequiredService<IGitHubTokenSource>(), scope.GraphQlEndpoint,
                sp.GetService<GitHubTransportOverride>()?.CreateHandler()), scope);
        });
        services.TryAddSingleton<GitHubBoundTargetResolver>();
        services.TryAddSingleton(sp => new GitHubNativeMutationService(
            sp.GetRequiredService<IGitHubTokenSource>(),
            sp.GetRequiredService<GitHubBoundTargetResolver>(),
            sp.GetRequiredService<GitHubWorkspaceScope>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<IExternalIdentityMap>(),
            sp.GetRequiredService<ILocalWorkItemStore>(),
            GitHubAccountSelection(sp.GetRequiredService<TwigConfiguration>()),
            sp.GetService<GitHubTransportOverride>(),
            sp.GetRequiredService<TwigConfiguration>().GitHubCredentialSource ?? "gh"));
        services.TryAddSingleton(sp => new GitHubCapturedReadSessionFactory(
            sp.GetRequiredService<IGitHubTokenSource>(),
            sp.GetRequiredService<GitHubBoundTargetResolver>(),
            sp.GetRequiredService<GitHubWorkspaceScope>(),
            GitHubAccountSelection(sp.GetRequiredService<TwigConfiguration>()),
            sp.GetService<GitHubTransportOverride>()));
        services.TryAddSingleton(sp => new GitHubProviderSetupService(
            () => sp.GetRequiredService<IProviderTargetBindingStore>(),
            () => sp.GetRequiredService<Twig.Infrastructure.Persistence.ProviderTargets.IProviderTargetAttachmentStore>(),
            async ct =>
            {
                var observation = await Twig.Infrastructure.Persistence.ProviderTargets.WorktreeLocalProviderTargetAttachmentStore
                    .ReadObservationWithRevisionAsync(sp.GetRequiredService<TwigPaths>(), ct).ConfigureAwait(false);
                if (!observation.IsSuccess)
                    throw new InvalidDataException(observation.Error);
                return observation.Value;
            },
            scope => GitHubReadOnlyAuthHandler.CreateClient(
                GitHubTokenSource(sp, scope),
                scope.GraphQlEndpoint, sp.GetService<GitHubTransportOverride>()?.CreateHandler())));
        services.TryAddSingleton(sp =>
        {
            var scope = sp.GetRequiredService<GitHubWorkspaceScope>();
            return new GitHubRepositoryDiscovery(GitHubReadOnlyAuthHandler.CreateClient(
                sp.GetRequiredService<IGitHubTokenSource>(), scope.GraphQlEndpoint,
                sp.GetService<GitHubTransportOverride>()?.CreateHandler()), scope);
        });
        services.TryAddSingleton(sp =>
        {
            var scope = sp.GetRequiredService<GitHubWorkspaceScope>();
            var http = GitHubReadOnlyAuthHandler.CreateClient(
                sp.GetRequiredService<IGitHubTokenSource>(),
                scope.GraphQlEndpoint,
                sp.GetService<GitHubTransportOverride>()?.CreateHandler());
            return new GitHubWorkItemProvider(http, scope.GraphQlEndpoint,
                resolveTarget: ct => sp.GetRequiredService<GitHubBoundTargetResolver>().ResolveAsync(ct),
                validateTarget: (context, ct) => sp.GetRequiredService<GitHubBoundTargetResolver>().ValidateAsync(context, ct));
        });

        if (string.Equals(config.Provider, ProviderCapabilityCatalog.GitHubProviderKind, StringComparison.Ordinal))
        {
            services.AddSingleton<IWorkItemSnapshotReader>(sp => sp.GetRequiredService<GitHubWorkItemProvider>());
            services.AddSingleton<IProviderWorkingSetReader>(sp => sp.GetRequiredService<GitHubWorkItemProvider>());
            services.TryAddSingleton<SqliteLocalWorkItemRepository>(sp =>
                new SqliteLocalWorkItemRepository(sp.GetRequiredService<SqliteCacheStore>()));
            services.TryAddSingleton<ILocalWorkItemStore>(sp =>
                sp.GetRequiredService<SqliteLocalWorkItemRepository>());
            services.TryAddSingleton<IExternalIdentityMap>(sp =>
                sp.GetRequiredService<SqliteLocalWorkItemRepository>());
            services.AddSingleton<IProviderWorkItemReadService>(sp => new ProviderWorkItemReadService(
                sp.GetRequiredService<IWorkItemSnapshotReader>(),
                sp.GetRequiredService<IExternalIdentityMap>(),
                sp.GetRequiredService<ILocalWorkItemStore>()));
            services.AddSingleton<ProviderWorkingSetSyncService>();
        }
    }

    private static IGitHubTokenSource GitHubTokenSource(IServiceProvider services, GitHubWorkspaceScope scope)
    {
        var config = services.GetRequiredService<TwigConfiguration>();
        return config.GitHubCredentialSource switch
        {
            null or "gh" => new GhCliTokenSource(
                services.GetRequiredService<IBoundedProcessRunner>(), scope.Host, config.GitHubAccount),
            "github-app" when scope.Host == "github.com" => GitHubAppAuthentication.CreateDefault(
                config.GitHubAppClientId ?? "", config.GitHubOAuthIdentity ?? ""),
            _ => throw new GitHubCredentialUnavailableException(
                "GitHub credential unavailable: unsupported explicit credential selection; no alternate source was used."),
        };
    }

    private static string? GitHubAccountSelection(TwigConfiguration config) =>
        config.GitHubCredentialSource == "github-app"
            ? $"{config.GitHubAppClientId}:{config.GitHubOAuthIdentity}" : config.GitHubAccount;

    private static GitHubWorkspaceScope GitHubScope(TwigConfiguration cfg)
    {
        if (cfg.ResolveWorkItemProviderKind() != ProviderCapabilityCatalog.GitHubProviderKind)
            throw new TwigConfigurationException(
                $"{WorkItemProviderSetting.UnavailableReasonCode}: the GitHub adapter is available only when provider=github.",
                new NotSupportedException(WorkItemProviderSetting.UnavailableReasonCode));
        if (!GitHubWorkspaceScope.TryCreate(cfg.GitHub, out var scope, out var problem))
            throw new TwigConfigurationException(
                $"{WorkItemProviderSetting.UnconfiguredReasonCode}: {problem}. " +
                "Run `twig config github.host <host>` and `twig config github.repository <owner/name>`.",
                new InvalidDataException(WorkItemProviderSetting.UnconfiguredReasonCode));
        return scope!;
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
