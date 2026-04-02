using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Verifies that IPendingChangeFlusher resolves correctly from the real
/// <see cref="CommandServiceModule.AddTwigCommandServices"/> registration.
/// </summary>
public sealed class PendingChangeFlusherDiTests
{
    [Fact]
    public void Interface_ResolvesFromCommandServiceModule()
    {
        var services = new ServiceCollection();

        // Register the infrastructure stubs that PendingChangeFlusher's factory needs
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter()));

        // Wire all command-layer services via the real module
        services.AddTwigCommandServices();

        var flusher = services.BuildServiceProvider().GetRequiredService<IPendingChangeFlusher>();

        flusher.ShouldBeOfType<PendingChangeFlusher>();
    }
}
