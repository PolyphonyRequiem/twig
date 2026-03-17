using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Twig.Infrastructure.Persistence;

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
    public static IServiceCollection AddTwigCoreServices(this IServiceCollection services)
    {
        // Configuration
        services.AddSingleton(sp =>
        {
            var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
            var configPath = Path.Combine(twigDir, "config");
            return File.Exists(configPath)
                ? TwigConfiguration.LoadAsync(configPath).GetAwaiter().GetResult()
                : new TwigConfiguration();
        });

        // Multi-context DB path: .twig/{org}/{project}/twig.db
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<TwigConfiguration>();
            var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
            return (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
                ? TwigPaths.ForContext(twigDir, config.Organization, config.Project)
                : new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
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
        services.AddSingleton<IPendingChangeStore>(sp => new SqlitePendingChangeStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IUnitOfWork>(sp => new SqliteUnitOfWork(sp.GetRequiredService<SqliteCacheStore>()));

        // Domain services
        services.AddSingleton<IProcessTypeStore>(sp => new SqliteProcessTypeStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IProcessConfigurationProvider>(sp => new DynamicProcessConfigProvider(sp.GetRequiredService<IProcessTypeStore>()));

        // Git service — registers the local git CLI service shared by CLI and TUI entry points.
        // IAdoGitService registration remains in the CLI Program.cs because it requires
        // runtime auto-detection of the git remote URL during DI composition.
        services.AddSingleton<IGitService, GitCliService>();

        return services;
    }
}
