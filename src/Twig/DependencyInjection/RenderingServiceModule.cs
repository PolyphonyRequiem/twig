using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.DependencyInjection;

/// <summary>
/// Registers rendering and output-formatting services.
/// Lives in the CLI layer because <c>Twig.Infrastructure</c> does not reference <c>Spectre.Console</c>.
/// </summary>
public static class RenderingServiceModule
{
    public static IServiceCollection AddTwigRenderingServices(
        this IServiceCollection services,
        IReadOnlyList<StateEntry>? stateEntries = null)
    {
        // Output formatters and factory
        services.AddSingleton<HumanOutputFormatter>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new HumanOutputFormatter(cfg.Display, cfg.TypeAppearances, stateEntries);
        });
        services.AddSingleton<JsonOutputFormatter>();
        services.AddSingleton<MinimalOutputFormatter>();
        services.AddSingleton<OutputFormatterFactory>();

        // Spectre.Console rendering pipeline
        services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
        services.AddSingleton<SpectreTheme>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new SpectreTheme(cfg.Display, cfg.TypeAppearances, stateEntries);
        });
        services.AddSingleton<IAsyncRenderer>(sp => new SpectreRenderer(
            sp.GetRequiredService<IAnsiConsole>(),
            sp.GetRequiredService<SpectreTheme>()));
        services.AddSingleton<RenderingPipelineFactory>();

        return services;
    }
}
