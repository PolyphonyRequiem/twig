using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.DependencyInjection;

public sealed class CommandServiceModuleTests
{
    /// <summary>
    /// Builds a service provider with all infrastructure mocks registered,
    /// then calls <see cref="CommandServiceModule.AddTwigCommandServices"/>.
    /// </summary>
    private static ServiceProvider BuildFullProvider()
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

        services.AddSingleton(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" }
        });

        services.AddTwigCommandServices();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ContextChangeService_Resolves_WithAllDependencies()
    {
        using var provider = BuildFullProvider();

        var service = provider.GetRequiredService<ContextChangeService>();

        service.ShouldNotBeNull();
    }

}
