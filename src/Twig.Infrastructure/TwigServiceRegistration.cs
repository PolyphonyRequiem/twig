using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Telemetry;

namespace Twig.Infrastructure;

/// <summary>
/// Registers core Twig services into an <see cref="IServiceCollection"/>.
/// Shared by both CLI and TUI entry points to eliminate duplicate DI setup.
/// </summary>
/// <remarks>
/// <b>Public visibility</b>: This class MUST be <c>public</c> because
/// <c>InternalsVisibleTo</c> in <c>Twig.Infrastructure.csproj</c> does NOT
/// include <c>Twig.Tui</c>. An <c>internal</c> class would cause a compilation
/// error in the TUI project.
/// <para/>
/// <b>LegacyDbMigrator exclusion</b>: <c>LegacyDbMigrator</c> is an
/// <c>internal static class</c> in the CLI project and cannot be referenced
/// from Infrastructure. CLI <c>Program.cs</c> must call
/// <c>LegacyDbMigrator.MigrateIfNeeded()</c> directly after consuming
/// <see cref="AddTwigCoreServices"/>.
/// </remarks>
public static class TwigServiceRegistration
{
    /// <summary>
    /// Registers core Twig services: configuration, paths, SQLite persistence,
    /// repositories, stores, and the process configuration provider.
    /// Uses factory-based <c>AddSingleton(sp => ...)</c> for AOT robustness.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="preloadedConfig">Optional pre-loaded config to avoid redundant file I/O.
    /// When null, config is loaded from disk on first resolution.</param>
    /// <param name="twigDir">Optional explicit path to the <c>.twig</c> directory.
    /// When null, falls back to <c>Path.Combine(Directory.GetCurrentDirectory(), ".twig")</c>.</param>
    /// <param name="startDir">Optional CWD override. Stored on <see cref="TwigPaths.StartDir"/>
    /// so commands like <c>twig init</c> can create workspaces relative to the invocation
    /// directory rather than the walked-up <c>.twig</c> ancestor.</param>
    public static IServiceCollection AddTwigCoreServices(
        this IServiceCollection services,
        TwigConfiguration? preloadedConfig = null,
        string? twigDir = null,
        string? startDir = null)
    {
        var resolvedTwigDir = twigDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".twig");

        // Configuration — use pre-loaded instance if available, otherwise load on first resolution
        if (preloadedConfig is not null)
        {
            services.AddSingleton(preloadedConfig);
        }
        else
        {
            services.AddSingleton(_ =>
            {
                var configPath = Path.Combine(resolvedTwigDir, "config");
                return TwigConfiguration.Load(configPath);
            });
        }

        // Multi-context DB path: .twig/{org}/{project}/twig.db
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<TwigConfiguration>();
            return TwigPaths.BuildPaths(resolvedTwigDir, config, startDir);
        });

        // SQLite persistence — registered unconditionally. SqliteCacheStore is
        // created lazily (on first resolution) and throws a descriptive error
        // if .twig/ hasn't been initialized yet.
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<TwigPaths>();
            if (!Directory.Exists(paths.TwigDir))
                throw new InvalidOperationException("Twig workspace not initialized. Run 'twig init' first.");
            return new SqliteCacheStore($"Data Source={paths.DbPath}");
        });

        services.AddSingleton<IWorkItemRepository>(sp => new SqliteWorkItemRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IContextStore>(sp => new SqliteContextStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<INavigationHistoryStore>(sp => new SqliteNavigationHistoryStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IPendingChangeStore>(sp => new SqlitePendingChangeStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IUnitOfWork>(sp => new SqliteUnitOfWork(sp.GetRequiredService<SqliteCacheStore>()));

        // Domain services
        services.AddSingleton<IProcessTypeStore>(sp => new SqliteProcessTypeStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IProcessConfigurationProvider>(sp => new DynamicProcessConfigProvider(sp.GetRequiredService<IProcessTypeStore>()));
        services.AddSingleton<IFieldDefinitionStore>(sp => new SqliteFieldDefinitionStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IWorkItemLinkRepository>(sp => new SqliteWorkItemLinkRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<ISeedLinkRepository>(sp => new SqliteSeedLinkRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IPublishIdMapRepository>(sp => new SqlitePublishIdMapRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IWorkspaceModeStore>(sp => new SqliteWorkspaceModeStore(sp.GetRequiredService<SqliteCacheStore>()));

        // Git service — registers the local git CLI service shared by CLI and TUI entry points.
        // IAdoGitService registration remains in the CLI Program.cs because it requires
        // runtime auto-detection of the git remote URL during DI composition.
        services.AddSingleton<IGitService, GitCliService>();

        // Seed publish rules provider — loads .twig/seed-rules.json or falls back to defaults.
        services.AddSingleton<ISeedPublishRulesProvider>(sp =>
        {
            var paths = sp.GetRequiredService<TwigPaths>();
            return new FileSeedPublishRulesProvider(paths.TwigDir);
        });

        // Global profile store — best-effort file-backed storage for process profiles.
        services.AddSingleton<IGlobalProfileStore, GlobalProfileStore>();

        // Telemetry client — no-op when TWIG_TELEMETRY_ENDPOINT is unset.
        services.AddSingleton<ITelemetryClient, TelemetryClient>();

        // Prompt state writer — writes .twig/prompt.json atomically after mutating commands.
        services.AddSingleton<IPromptStateWriter>(sp => new PromptStateWriter(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<IProcessTypeStore>()));

        return services;
    }
}
