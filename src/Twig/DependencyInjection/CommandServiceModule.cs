using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.DependencyInjection;

/// <summary>
/// Registers the command-support services that name the CLI surface: the hint engine, the
/// editor launcher and console input, the pending-change flusher, <see cref="CommandContext"/>,
/// and the status-field config reader.
/// </summary>
/// <remarks>
/// Surface-neutral domain services (resolvers, sync coordination, the mutation workflows) are
/// <b>not</b> registered here — they live in
/// <c>TwigServiceRegistration.AddConnectionServices()</c> so that every surface, MCP included,
/// resolves one definition instead of maintaining a hand-written mirror (wayfinder 0016).
/// <para/>
/// The former DD-12 note claiming those services had to live in the CLI because
/// <see cref="IAdoWorkItemService"/> was "registered with CLI-layer factory logic" was false:
/// it is registered in Infrastructure, in
/// <c>NetworkServiceModule.AddTwigNetworkServices</c>.
/// </remarks>
public static class CommandServiceModule
{
    public static IServiceCollection AddTwigCommandServices(this IServiceCollection services)
    {
        // Hint engine — reads display config at startup, uses process config for dynamic state resolution.
        // Resolving process configuration creates the SQLite cache. Keep it deferred until
        // a cache already exists so 'twig init' can recognize and resume partial workspaces.
        services.AddSingleton<HintEngine>(sp =>
        {
            var display = sp.GetRequiredService<TwigConfiguration>().Display;
            IProcessConfigurationProvider? provider = null;
            var paths = sp.GetRequiredService<TwigPaths>();
            if (File.Exists(paths.DbPath))
            {
                try
                {
                    provider = sp.GetRequiredService<IProcessConfigurationProvider>();
                }
                catch (InvalidOperationException)
                {
                    // Workspace not initialized — hints degrade gracefully without process config
                }
            }
            var referenceProfileProvider = sp.GetService<IReferenceProfileProvider>();
            return new HintEngine(display, provider, referenceProfileProvider);
        });

        // Editor launcher and console input
        services.AddSingleton<IEditorLauncher, EditorLauncher>();
        services.AddSingleton<IConsoleInput, ConsoleInput>();

        // PendingChangeFlusher — flush loop shared by SyncCommand. Stays in the CLI: it takes
        // IConsoleInput for interactive conflict prompts and an output-format string, so it
        // names the surface. MCP has its own headless McpPendingChangeFlusher.
        services.AddSingleton<IPendingChangeFlusher>(sp => new PendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>()));

        // EPIC-2121: CommandContext parameter object — consolidates cross-cutting command deps
        services.AddSingleton(sp => new CommandContext(
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<ITelemetryClient>(),
            AttachmentStatus: sp.GetRequiredService<IAttachmentStatusProjection>()));

        // EPIC-2121: StatusFieldConfigReader — encapsulates File.Exists + ReadAllTextAsync + Parse
        services.AddSingleton(sp => new StatusFieldConfigReader(
            sp.GetRequiredService<TwigPaths>()));

        return services;
    }
}
