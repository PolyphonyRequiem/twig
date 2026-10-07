using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Auth;
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
            sp.GetService<ITelemetryClient>(),
            sp.GetRequiredService<Twig.Domain.Interfaces.IManagedWorktreeInitializer>(),
            sp.GetRequiredService<Twig.Domain.Interfaces.ISystemWorktreeRegistry>(),
            sp.GetRequiredService<Twig.Domain.Services.Attachment.IProfileRegistrySource>(),
            sp.GetRequiredService<Twig.Infrastructure.Ado.AdoConcurrencyThrottle>(),
            sp.GetRequiredService<Twig.Infrastructure.Auth.BootstrapEndpointSelection>()));
        services.AddSingleton<SetCommand>();
        services.AddSingleton<ShowCommand>(sp => new ShowCommand(
            sp.GetRequiredService<CommandContext>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<SyncCoordinatorFactory>(),
            sp.GetRequiredService<StatusFieldConfigReader>(),
            fieldDefinitionStore: sp.GetService<IFieldDefinitionStore>(),
            processConfigProvider: sp.GetService<IProcessConfigurationProvider>(),
            contextStore: sp.GetService<IContextStore>(),
            activeItemResolver: sp.GetService<ActiveItemResolver>(),
            pendingChangeStore: sp.GetService<IPendingChangeStore>(),
            workingSetService: sp.GetService<WorkingSetService>(),
            twigPaths: sp.GetService<TwigPaths>(),
            adoGitService: sp.GetService<IAdoGitService>(),
            treeRenderingService: sp.GetService<TreeRenderingService>(),
            rendererFactory: sp.GetRequiredService<Twig.Rendering.RendererFactory>()));
        services.AddSingleton<StateCommand>();
        services.AddSingleton<TreeRenderingService>();
        services.AddSingleton<Twig.Commands.SetTree.WorkingSetTreeCommand>(sp =>
            new Twig.Commands.SetTree.WorkingSetTreeCommand(
                sp.GetRequiredService<CommandContext>(),
                sp.GetRequiredService<IWorkItemRepository>(),
                sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
                sp.GetRequiredService<TwigConfiguration>()));
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
            sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<SeedLinkRepairCommand>();
        services.AddSingleton<WebCommand>();
        services.AddSingleton<NoteCommand>();
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<PatchCommand>();
        services.AddSingleton<EditCommand>();

        services.AddSingleton<RefreshCommand>();
        services.AddSingleton<DiscardCommand>();
        services.AddSingleton<DeleteCommand>();
        services.AddSingleton<SyncCommand>();
        services.AddSingleton<BenchSyncCommand>();
        services.AddSingleton<BenchConfigurationCommand>();
        // 'twig save' is deprecated but still dispatches to SaveCommand
        // (Program.cs Save handler); without this it throws at runtime.
        services.AddSingleton<SaveCommand>();
        services.AddSingleton<WorkspaceCommand>(sp => new WorkspaceCommand(
            sp.GetRequiredService<CommandContext>(), sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(), sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<IProcessTypeStore>(), sp.GetRequiredService<IFieldDefinitionStore>(),
            sp.GetRequiredService<ActiveItemResolver>(), sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<ITrackingService>(), sp.GetRequiredService<ISprintHierarchyBuilder>(),
            sp.GetRequiredService<SprintIterationResolver>(), sp.GetService<TreeRenderingService>(),
            sp.GetService<SyncCoordinatorFactory>(), sp.GetService<Twig.Rendering.RendererFactory>(),
            sp.GetRequiredService<CurrentBenchResolver>(), sp.GetRequiredService<BenchEvaluator>(),
            sp.GetRequiredService<IAuthenticationProvider>())
        {
            ResolveBrowserBindingAsync = ct => sp.GetRequiredService<IConnectionBindingService>().ResolveAsync(
                sp.GetRequiredService<TwigConfiguration>(), sp.GetRequiredService<TwigPaths>(), ct),
        });
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<MigrateConfigCommand>();
        services.AddSingleton<ConfigStatusFieldsCommand>();
        services.AddSingleton<AuthClearCommand>();
        services.AddSingleton<AuthLoginCommand>();
        services.AddSingleton<AuthIdentitiesCommand>();
        services.AddSingleton<AuthPatCommand>();
        services.AddSingleton<ConnectionBindCommand>();
        services.AddSingleton<ConnectionListCommand>();
        services.AddSingleton<ConnectionStatusCommand>();
        services.AddSingleton<ConnectionCheckCommand>();
        services.AddSingleton<ConnectionMigrateCommand>();
        services.AddSingleton<ConnectionPinCommand>();
        services.AddSingleton<ConnectionWritesCommand>();
        services.AddSingleton<ConnectionDefaultCommand>();
        services.AddSingleton<QueryCommand>();
        services.AddSingleton<HistoryCommand>();
        services.AddSingleton<ProcessCommand>();
        services.AddSingleton<ProcessLayoutCommand>();
        services.AddSingleton<ProcessDescriptionCommand>();
        services.AddSingleton<BatchCommand>();
        services.AddSingleton<TrackingCommand>(sp => new TrackingCommand(
            sp.GetRequiredService<ITrackingService>(), sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(), sp.GetRequiredService<Twig.Infrastructure.Services.Mutation.PinWorkflow>(),
            sp.GetService<Twig.Rendering.RendererFactory>(), sp.GetRequiredService<IAuthenticationProvider>())
        {
            ResolveBrowserBindingAsync = ct => sp.GetRequiredService<IConnectionBindingService>().ResolveAsync(
                sp.GetRequiredService<TwigConfiguration>(), sp.GetRequiredService<TwigPaths>(), ct),
        });
        // ADO #148: the Bench command surface. The workflow it depends on is registered in the
        // SHARED domain-services module, beside PinWorkflow, so the MCP surface can build it too.
        services.AddSingleton<BenchCommand>(sp => new BenchCommand(
            sp.GetRequiredService<Twig.Infrastructure.Services.Mutation.BenchWorkflow>(),
            sp.GetRequiredService<OutputFormatterFactory>(), sp.GetService<Twig.Rendering.RendererFactory>(),
            sp.GetRequiredService<IAuthenticationProvider>(), sp.GetRequiredService<IWorkItemRepository>())
        {
            ResolveBrowserBindingAsync = ct => sp.GetRequiredService<IConnectionBindingService>().ResolveAsync(
                sp.GetRequiredService<TwigConfiguration>(), sp.GetRequiredService<TwigPaths>(), ct),
        });
        services.AddSingleton<AreaCommand>();
        services.AddSingleton<SprintCommand>();
        services.AddSingleton<PlanCommand>();
        services.AddSingleton<PendingCommand>();
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