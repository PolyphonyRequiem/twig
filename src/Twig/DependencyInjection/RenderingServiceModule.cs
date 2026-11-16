using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Twig.Formatters;
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
        services.AddSingleton<RendererFactory>();

        // Spectre.Console rendering pipeline
        services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
        return services;
    }
}
