using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Twig.Infrastructure.GitHub;
using Twig.Rendering;

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
        AddGitCommands(services);
        AddFlowCommands(services);
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
        services.AddSingleton<StatusCommand>();
        services.AddSingleton<StateCommand>(sp => new StateCommand(
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IPromptStateWriter>(),
            parentPropagationService: sp.GetRequiredService<Domain.Services.ParentStatePropagationService>()));
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
        services.AddSingleton<SeedPublishCommand>();
        services.AddSingleton<SeedReconcileCommand>();
        services.AddSingleton<WebCommand>();
        services.AddSingleton<NoteCommand>();
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<EditCommand>();
        services.AddSingleton<SaveCommand>();
        services.AddSingleton<RefreshCommand>();
        services.AddSingleton<DiscardCommand>();
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

    private static void AddGitCommands(IServiceCollection services)
    {
        services.AddSingleton<BranchCommand>(sp => new BranchCommand(
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<CommitCommand>(sp => new CommitCommand(
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<PrCommand>(sp => new PrCommand(
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<StashCommand>(sp => new StashCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<LogCommand>(sp => new LogCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetService<IGitService>()));

        services.AddSingleton<HookInstaller>();
        services.AddSingleton<HooksCommand>(sp => new HooksCommand(
            sp.GetRequiredService<HookInstaller>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>()));
        services.AddSingleton<GitContextCommand>(sp => new GitContextCommand(
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<HookHandlerCommand>(sp => new HookHandlerCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
    }

    private static void AddFlowCommands(IServiceCollection services)
    {
        services.AddSingleton<FlowStartCommand>(sp => new FlowStartCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>(),
            sp.GetRequiredService<Domain.Services.ProtectedCacheWriter>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetService<IGitService>(),
            sp.GetService<IIterationService>(),
            sp.GetRequiredService<IPromptStateWriter>(),
            sp.GetService<INavigationHistoryStore>(),
            sp.GetService<Domain.Services.ContextChangeService>(),
            sp.GetRequiredService<Domain.Services.ParentStatePropagationService>()));
        services.AddSingleton<FlowDoneCommand>(sp => new FlowDoneCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IPendingChangeFlusher>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<Domain.Services.FlowTransitionService>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<FlowCloseCommand>(sp => new FlowCloseCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<Domain.Services.FlowTransitionService>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
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
