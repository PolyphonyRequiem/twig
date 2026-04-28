using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.DependencyInjection;

public sealed class CommandServiceModuleTests
{
    private static ServiceProvider BuildProviderWithConfig(TwigConfiguration config)
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton(Substitute.For<IProcessConfigurationProvider>());
        services.AddSingleton(Substitute.For<IProcessTypeStore>());
        services.AddSingleton(Substitute.For<IFieldDefinitionStore>());
        services.AddSingleton(Substitute.For<ISeedLinkRepository>());
        services.AddSingleton(Substitute.For<IPublishIdMapRepository>());
        services.AddSingleton(Substitute.For<ISeedPublishRulesProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IConsoleInput>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());
        services.AddSingleton(new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter()));
        services.AddSingleton(Substitute.For<IAsyncRenderer>());
        services.AddSingleton<RenderingPipelineFactory>();
        services.AddSingleton(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-test"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "twig.db")));

        services.AddSingleton(config);
        services.AddTwigCommandServices();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildFullProvider() =>
        BuildProviderWithConfig(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" }
        });

    [Fact]
    public void ContextChangeService_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var service = provider.GetRequiredService<ContextChangeService>();

        service.ShouldNotBeNull();
    }

    [Fact]
    public void SyncCoordinatorPair_Resolves_WithBothTiers()
    {
        using var provider = BuildFullProvider();

        // GetRequiredService throws if the registration is missing
        provider.GetRequiredService<SyncCoordinatorPair>();
    }

    [Fact]
    public void SyncCoordinator_Resolves_ToFactoryReadWrite()
    {
        using var provider = BuildFullProvider();

        var pair = provider.GetRequiredService<SyncCoordinatorPair>();
        var coordinator = provider.GetRequiredService<SyncCoordinator>();

        coordinator.ShouldBeSameAs(pair.ReadWrite);
    }

    [Fact]
    public void CommandContext_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var ctx = provider.GetRequiredService<CommandContext>();

        ctx.ShouldNotBeNull();
        ctx.PipelineFactory.ShouldNotBeNull();
        ctx.FormatterFactory.ShouldNotBeNull();
        ctx.HintEngine.ShouldNotBeNull();
        ctx.Config.ShouldNotBeNull();
    }

    [Fact]
    public void CommandContext_TelemetryClient_IsNullWhenNotRegistered()
    {
        using var provider = BuildFullProvider();

        var ctx = provider.GetRequiredService<CommandContext>();

        ctx.TelemetryClient.ShouldBeNull();
    }

    [Fact]
    public void StatusFieldConfigReader_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var reader = provider.GetRequiredService<StatusFieldConfigReader>();

        reader.ShouldNotBeNull();
    }

}
