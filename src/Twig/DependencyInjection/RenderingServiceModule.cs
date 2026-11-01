using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
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
        this IServiceCollection services)
    {
        // Output formatters and factory — HumanOutputFormatter is the sole
        // surviving formatter after AB#3301 retired the machine-shape variants.
        services.AddSingleton<HumanOutputFormatter>();
        services.AddSingleton<OutputFormatterFactory>();
        services.AddSingleton<RendererFactory>(sp => new RendererFactory(sp.GetRequiredService<SpectreTheme>()));

        // Spectre.Console rendering pipeline
        services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
        services.AddSingleton<SpectreTheme>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new SpectreTheme(cfg.Display, cfg.TypeAppearances);
        });
        return services;
    }
}
