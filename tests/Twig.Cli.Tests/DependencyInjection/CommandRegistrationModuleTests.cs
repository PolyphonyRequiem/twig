using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
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
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton(Substitute.For<ITrackingRepository>());

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

        // Paths (needed by StatusFieldConfigReader)
        services.AddSingleton(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-test"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "twig.db")));

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

    [Fact]
    public void StatusCommand_AutoResolution_Resolves_Successfully()
    {
        using var provider = BuildProviderForFlowCommands();

        // StatusCommand uses auto-resolution (no factory lambda).
        // This verifies CommandContext and StatusFieldConfigReader resolve
        // correctly after the constructor was refactored.
        var command = provider.GetRequiredService<StatusCommand>();

        command.ShouldNotBeNull();
    }

    [Fact]
    public void SetCommand_AutoResolution_Resolves_Successfully()
    {
        using var provider = BuildProviderForFlowCommands();

        // SetCommand uses auto-resolution (no factory lambda).
        // This verifies CommandContext and StatusFieldConfigReader resolve
        // correctly after the constructor was refactored.
        var command = provider.GetRequiredService<SetCommand>();

        command.ShouldNotBeNull();
    }

    [Fact]
    public void ShowCommand_AutoResolution_Resolves_Successfully()
    {
        using var provider = BuildProviderForFlowCommands();

        // ShowCommand uses auto-resolution (no factory lambda).
        // This verifies CommandContext and StatusFieldConfigReader resolve
        // correctly after the constructor was refactored.
        var command = provider.GetRequiredService<ShowCommand>();

        command.ShouldNotBeNull();
    }
}
