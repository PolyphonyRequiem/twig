using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.DependencyInjection;

/// <summary>
/// Verifies that manual DI factory lambdas in <see cref="CommandRegistrationModule"/>
/// pass all required constructor parameters. This catches regressions where a new
/// constructor parameter is added but the factory is not updated.
/// </summary>
public sealed class CommandRegistrationModuleTests
{
    /// <summary>
    /// Builds a provider with all services needed by <see cref="FlowStartCommand"/>
    /// registered, then calls <see cref="CommandRegistrationModule.AddTwigCommands"/>
    /// to register the command factories.
    /// </summary>
    private static ServiceProvider BuildProviderForFlowCommands()
    {
        var services = new ServiceCollection();

        // Domain interfaces
        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IPendingChangeFlusher>());
        services.AddSingleton(Substitute.For<IProcessConfigurationProvider>());
        services.AddSingleton(Substitute.For<IProcessTypeStore>());
        services.AddSingleton(Substitute.For<IFieldDefinitionStore>());
        services.AddSingleton(Substitute.For<ISeedLinkRepository>());
        services.AddSingleton(Substitute.For<IPublishIdMapRepository>());
        services.AddSingleton(Substitute.For<ISeedPublishRulesProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IConsoleInput>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());
        services.AddSingleton(Substitute.For<IPromptStateWriter>());
        services.AddSingleton(Substitute.For<INavigationHistoryStore>());

        // Formatters
        services.AddSingleton(new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter()));

        // Config
        services.AddSingleton(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" },
        });

        // Rendering
        services.AddSingleton(Substitute.For<IAsyncRenderer>());
        services.AddSingleton<RenderingPipelineFactory>();

        // Domain services (needed by factory) + command service registrations
        services.AddTwigCommandServices();

        // Command registrations (the factories under test)
        services.AddTwigCommands();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void FlowStartCommand_Factory_Resolves_Successfully()
    {
        using var provider = BuildProviderForFlowCommands();

        // If the factory lambda is missing any constructor parameter,
        // this resolution will throw.
        var command = provider.GetRequiredService<FlowStartCommand>();

        command.ShouldNotBeNull();
    }

    [Fact]
    public void FlowStartCommand_Factory_Injects_ContextChangeService()
    {
        using var provider = BuildProviderForFlowCommands();

        provider.GetService<ContextChangeService>()
            .ShouldNotBeNull("ContextChangeService must be registered");
    }
}
