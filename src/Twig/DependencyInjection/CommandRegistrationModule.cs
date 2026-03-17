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
/// Preserves factory lambdas for commands that require explicit constructor wiring.
/// </summary>
public static class CommandRegistrationModule
{
    public static IServiceCollection AddTwigCommands(this IServiceCollection services)
    {
        // InitCommand uses a factory to inject auth + HTTP
        // (instead of IIterationService) so it can construct an AdoIterationService
        // with the org/project args supplied at invocation time.
        services.AddSingleton<InitCommand>(sp => new InitCommand(
            sp.GetRequiredService<IAuthenticationProvider>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>()));
        services.AddSingleton<SetCommand>();
        services.AddSingleton<StatusCommand>(sp => new StatusCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>()));
        services.AddSingleton<StateCommand>();
        services.AddSingleton<TreeCommand>(sp => new TreeCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>()));
        services.AddSingleton<NavigationCommands>();
        services.AddSingleton<SeedCommand>();
        services.AddSingleton<NoteCommand>();
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<EditCommand>();
        services.AddSingleton<SaveCommand>(sp => new SaveCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<RefreshCommand>(sp => new RefreshCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<WorkspaceCommand>(sp => new WorkspaceCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetRequiredService<Domain.Services.ActiveItemResolver>()));
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<BranchCommand>(sp => new BranchCommand(
            sp.GetRequiredService<IContextStore>(),
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
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<PrCommand>(sp => new PrCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<StashCommand>(sp => new StashCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
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

        // Git hooks & context commands
        services.AddSingleton<HookInstaller>();
        services.AddSingleton<HooksCommand>(sp => new HooksCommand(
            sp.GetRequiredService<HookInstaller>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>()));
        services.AddSingleton<GitContextCommand>(sp => new GitContextCommand(
            sp.GetRequiredService<IContextStore>(),
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

        // Flow lifecycle commands
        services.AddSingleton<FlowStartCommand>(sp => new FlowStartCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetService<IGitService>(),
            sp.GetService<IIterationService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<FlowDoneCommand>(sp => new FlowDoneCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<SaveCommand>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));
        services.AddSingleton<FlowCloseCommand>(sp => new FlowCloseCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>(),
            sp.GetRequiredService<IPromptStateWriter>()));

        // Self-update services
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

        // Oh My Posh helper
        services.AddSingleton<OhMyPoshCommands>();

        return services;
    }
}
