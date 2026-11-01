using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Twig.Commands;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;

namespace Twig.DependencyInjection;

/// <summary>The native host graph, with Core data owners only in an admitted workspace.</summary>
internal static class WorkspaceRegistrationModule
{
    internal static IServiceCollection AddTwigWorkspaceServices(this IServiceCollection services,
        string startDir, IReadOnlyList<string> args, string? globalDisplayPath = null)
    {
        startDir = Path.GetFullPath(startDir);
        if (args.Count > 0 && args[0] == "init")
        {
            var directory = Path.Combine(startDir, ".twig");
            services.AddCoreBootstrapServices(new TwigConfiguration(),
                new TwigPaths(directory, Path.Combine(directory, "config"), Path.Combine(directory, "core.db"),
                    startDir, globalDisplayPath));
            services.AddTwigRenderingServices();
            services.AddSingleton(sp => new InitCommand(sp.GetRequiredService<TwigPaths>(),
                sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
                () => sp.GetRequiredService<IConnectionBindingService>(),
                sp.GetService<Twig.Domain.Interfaces.ITelemetryClient>(), sp.GetService<CoreTransportOverride>(),
                sp.GetService<Twig.Infrastructure.GitHub.IBoundedProcessRunner>()));
            return services;
        }
        var workspaceRoot = WorkspaceDiscovery.FindWorkspaceRoot(startDir);
        var twigDir = workspaceRoot is not null
            ? Path.Combine(workspaceRoot, ".twig")
            : Path.Combine(startDir, ".twig");

        var probePaths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"),
            startDir, globalDisplayPath);
        var config = TwigConfiguration.LoadSplit(probePaths);
        services.AddCoreBootstrapServices(config, probePaths);
        services.AddTwigRenderingServices();
        services.AddTwigCommands();
        var admission = Twig.CoreMutationAdmissionGuard.Validate(args, twigDir);
        if (admission is not null) throw new InvalidOperationException(admission);
        if (File.Exists(Path.Combine(twigDir, CoreWorkspaceFactory.FileName)))
        {
            var corePaths = TwigPaths.BuildPaths(twigDir, config, startDir);
            services.AddCoreWorkspaceServices(config, corePaths);
            services.AddSingleton<Twig.Domain.Interfaces.IConsoleInput, ConsoleInput>();
            services.TryAddSingleton<Twig.Domain.Interfaces.IEditorLauncher, EditorLauncher>();
            services.AddSingleton(sp => new CoreCommandAdapter(sp.GetRequiredService<CoreWorkspaceFactory>(),
                sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.IConsoleInput>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.IEditorLauncher>(),
                sp.GetService<Twig.Domain.Interfaces.ITelemetryClient>()));
            services.AddSingleton<ProcessCommand>();
            services.AddSingleton<ProcessDescriptionCommand>();
            services.AddSingleton<LinkCommand>();
            services.AddSingleton(sp => new LocalRelationshipCommand(sp.GetRequiredService<CoreWorkspaceFactory>(),
                sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
                sp.GetService<Twig.Domain.Interfaces.ITelemetryClient>(), sp.GetService<TextWriter>()));
            services.AddSingleton<WorkspaceCommand>();
            return services;
        }
        return services;
    }
}
