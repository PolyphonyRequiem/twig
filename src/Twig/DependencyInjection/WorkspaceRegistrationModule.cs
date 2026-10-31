using Microsoft.Extensions.DependencyInjection;
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

/// <summary>One composition path for a CLI invocation or explicitly reopened browser runtime.</summary>
internal static class WorkspaceRegistrationModule
{
    internal static IServiceCollection AddTwigWorkspaceServices(this IServiceCollection services,
        string startDir, IReadOnlyList<string> args, string? globalDisplayPath = null)
    {
        startDir = Path.GetFullPath(startDir);
        var workspaceRoot = WorkspaceDiscovery.FindWorkspaceRoot(startDir);
        var twigDir = workspaceRoot is not null
            ? Path.Combine(workspaceRoot, ".twig")
            : Path.Combine(startDir, ".twig");

        var probePaths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"),
            startDir, globalDisplayPath);
        var config = TwigConfiguration.LoadSplit(probePaths);
        var admission = Twig.CoreMutationAdmissionGuard.Validate(args, twigDir);
        if (admission is not null) throw new InvalidOperationException(admission);
        if (File.Exists(Path.Combine(twigDir, CoreWorkspaceFactory.FileName)))
        {
            var corePaths = TwigPaths.BuildPaths(twigDir, config, startDir);
            services.AddCoreWorkspaceServices(config, corePaths);
            services.AddTwigRenderingServices();
            services.AddSingleton<Twig.Domain.Interfaces.IConsoleInput, ConsoleInput>();
            services.AddSingleton(sp => new CoreCommandAdapter(sp.GetRequiredService<CoreWorkspaceFactory>(),
                sp.GetRequiredService<Twig.Rendering.RendererFactory>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.IConsoleInput>()));
            return services;
        }
        services.AddConnectionServices(config, twigDir, startDir);

        // Git discovery belongs to composition, never to a local navigation gesture.
        var gitProject = config.GetGitProject();
        var repository = config.Git.Repository;
        Task<(string? GitProject, string? Repository)>? gitDetectTask = null;
        if (string.IsNullOrWhiteSpace(repository))
        {
            var capturedConfig = config;
            gitDetectTask = Task.Run(() => DetectGitRemote(capturedConfig, startDir));
        }
        if (gitDetectTask is not null)
            (gitProject, repository) = gitDetectTask.GetAwaiter().GetResult();

        // Only explicit init may use the unattached initialization-metadata provider.
        services.AddSingleton(new ConnectionBindingOperationIntent(args.Count > 0 && args[0] == "init"));
        services.AddTwigNetworkServices(config, gitProject, repository);

        IReadOnlyList<StateEntry>? stateEntries = null;
        try
        {
            var paths = TwigPaths.BuildPaths(twigDir, config, startDir);
            if (!(args.Count > 0 && args[0] == "provider")
                && !(args.Count >= 2 && args[0] == "workspace" && args[1] == "migrate")
                && !(args.Count >= 2 && args[0] == "connection" && args[1] is "migrate" or "pin" or "unpin" or "default")
                && Directory.Exists(paths.TwigDir) && File.Exists(paths.DbPath))
            {
                using var cacheStore = SqliteCacheStore.OpenWorkspace(paths);
                var processTypeStore = new SqliteProcessTypeStore(cacheStore);
                var records = processTypeStore.GetAllAsync().GetAwaiter().GetResult();
                stateEntries = records.SelectMany(r => r.States).ToList();
                config.TypeAppearances = records
                    .Select(r => new TypeAppearanceConfig
                    {
                        Name = r.TypeName,
                        Color = r.ColorHex ?? string.Empty,
                        IconId = r.IconId,
                    })
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Preserve ordinary startup's empty/unavailable metadata behavior. The
            // admitted command still reports any real storage or authority refusal.
        }

        services.AddTwigRenderingServices(stateEntries);
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services;
    }

    private static (string? GitProject, string? Repository) DetectGitRemote(TwigConfiguration config, string cwd)
    {
        var gitProject = config.GetGitProject();
        string? repository = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "remote get-url origin")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
                var exited = proc.WaitForExit(2000);
                if (!exited)
                {
                    try { proc.Kill(); } catch (Exception) { }
                }
                var remoteUrl = stdoutTask.Result.Trim();
                if (exited && proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(remoteUrl))
                {
                    var parsed = AdoRemoteParser.Parse(remoteUrl);
                    if (parsed is not null)
                    {
                        repository = parsed.Repository;
                        if (string.IsNullOrWhiteSpace(config.Git.Project))
                            gitProject = parsed.Project;
                    }
                }
            }
        }
        catch (Exception) { /* Git discovery remains best-effort. */ }
        return (gitProject, repository);
    }
}
