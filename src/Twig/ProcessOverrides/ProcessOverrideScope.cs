using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Rendering;

namespace Twig.ProcessOverrides;

/// <summary>Legacy read-only layout scope. Unpinned schema inspection is explicitly refused.</summary>
internal static class ProcessOverrideScope
{
    public static ServiceProvider Build(string org, string project, TwigUserConfig userPrefs)
    {
        var config = new TwigConfiguration
        {
            RepoCoords = new TwigRepoConfig { Organization = org, Project = project },
            UserPrefs = userPrefs,
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices(stateEntries: null);
        services.AddSingleton(sp => new ProcessLayoutCommand(
            sp.GetRequiredService<IFormLayoutProvider>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<RendererFactory>()));
        return services.BuildServiceProvider();
    }
}
