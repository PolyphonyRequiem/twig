using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Verifies that IPendingChangeFlusher resolves correctly from the DI container.
/// </summary>
public sealed class PendingChangeFlusherDiTests
{
    [Fact]
    public void Interface_ResolvesFromDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IConsoleInput>());
        services.AddSingleton(new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter()));
        services.AddSingleton<IPendingChangeFlusher>(sp => new PendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>()));

        var flusher = services.BuildServiceProvider().GetRequiredService<IPendingChangeFlusher>();

        flusher.ShouldNotBeNull();
    }
}
