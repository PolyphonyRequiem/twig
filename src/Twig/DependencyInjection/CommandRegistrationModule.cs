using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;

namespace Twig.DependencyInjection;

/// <summary>
/// Registers all CLI command classes into the DI container.
/// Uses factory lambdas only for commands that require explicit constructor wiring;
/// auto-resolves all others.
/// </summary>
public static class CommandRegistrationModule
{
    public static IServiceCollection AddTwigCommands(this IServiceCollection services)
    {
        AddCoreCommands(services);
        AddSelfUpdateCommands(services);
        services.AddSingleton<OhMyPoshCommands>();

        return services;
    }

    private static void AddCoreCommands(IServiceCollection services)
    {
        // InitCommand uses a factory to inject auth + HTTP
        // (instead of IIterationService) so it can construct an AdoIterationService
        // with the org/project args supplied at invocation time.
        services.AddSingleton<InitCommand>(sp => new InitCommand(
            sp.GetRequiredService<IAuthenticationProvider>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IGlobalProfileStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetService<ITelemetryClient>()));
        services.AddSingleton<SetCommand>();
        services.AddSingleton<ShowCommand>();
        services.AddSingleton<StateCommand>();
        services.AddSingleton<TreeCommand>();
        services.AddSingleton<NavigationCommands>();
        services.AddSingleton<NavigationHistoryCommands>();
        services.AddSingleton<NewCommand>();
        services.AddSingleton<SeedNewCommand>();
        services.AddSingleton<SeedEditCommand>();
        services.AddSingleton<SeedDiscardCommand>();
        services.AddSingleton<SeedViewCommand>();
        services.AddSingleton<SeedLinkCommand>();
        services.AddSingleton<LinkCommand>();
        services.AddSingleton<ArtifactLinkCommand>();
        services.AddSingleton<SeedChainCommand>();
        services.AddSingleton<SeedValidateCommand>();
        services.AddSingleton<SeedPublishCommand>(sp => new SeedPublishCommand(
            sp.GetRequiredService<SeedPublishOrchestrator>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<SeedReconcileCommand>();
        services.AddSingleton<WebCommand>();
        services.AddSingleton<NoteCommand>();
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<EditCommand>();

        services.AddSingleton<RefreshCommand>();
        services.AddSingleton<DiscardCommand>();
        services.AddSingleton<DeleteCommand>();
        services.AddSingleton<SyncCommand>();
        services.AddSingleton<WorkspaceCommand>();
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<ConfigStatusFieldsCommand>();
        services.AddSingleton<QueryCommand>();
        services.AddSingleton<StatesCommand>();
        services.AddSingleton<BatchCommand>();
        services.AddSingleton<TrackingCommand>();
        services.AddSingleton<AreaCommand>();
    }

    private static void AddSelfUpdateCommands(IServiceCollection services)
    {
        services.AddSingleton<IGitHubReleaseService>(sp =>
        {
            var repoSlug = "PolyphonyRequiem/twig";
            var attrs = typeof(TwigCommands).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
            foreach (var attr in attrs)
            {
                if (attr is System.Reflection.AssemblyMetadataAttribute meta && meta.Key == "GitHubRepo" && meta.Value is not null)
                {
                    repoSlug = meta.Value;
                    break;
                }
            }
            return new GitHubReleaseClient(sp.GetRequiredService<HttpClient>(), repoSlug);
        });
        services.AddSingleton<SelfUpdater>(sp => new SelfUpdater(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<SelfUpdateCommand>();
        services.AddSingleton<ChangelogCommand>();
    }
}