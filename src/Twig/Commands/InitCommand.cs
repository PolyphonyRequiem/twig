using System.Diagnostics;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Telemetry;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>Creates one fresh, natively bound Core store. Existing state is never adopted or reset.</summary>
public sealed class InitCommand
{
    private readonly TwigPaths _paths;
    private readonly RendererFactory _renderers;
    private readonly Func<IConnectionBindingService> _bindings;
    private readonly ITelemetryClient? _telemetry;
    private readonly CoreTransportOverride? _transport;
    private readonly IBoundedProcessRunner? _githubProcesses;

    internal InitCommand(TwigPaths paths, RendererFactory renderers, Func<IConnectionBindingService> bindings,
        ITelemetryClient? telemetry = null, CoreTransportOverride? transport = null, IBoundedProcessRunner? githubProcesses = null)
    {
        _paths = paths;
        _renderers = renderers;
        _bindings = bindings;
        _telemetry = telemetry;
        _transport = transport;
        _githubProcesses = githubProcesses;
    }

    public async Task<int> ExecuteAsync(string org, string project, string? team = null, string? gitProject = null,
        bool force = false, string outputFormat = OutputFormatterFactory.DefaultFormat, string? sprint = null,
        string? area = null, bool reinitialize = false, CancellationToken ct = default, string? profile = null,
        string? provider = null, string? host = null, string? repository = null, string? binding = null,
        string? coreStore = null, string? account = null)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (force || reinitialize || sprint is not null || area is not null || profile is not null)
                return exit = Fail("--force, --reinitialize, --sprint, --area and --profile are retired. Init only creates a fresh Core workspace; it never resets, migrates or prefills old state.", 2);
            if (OutputFormats.Normalize(outputFormat) == "ids")
                return exit = Fail("Init has no work-item IDs to emit. Use human, json, json-full, json-compact or minimal.", 2);
            var effects = new NativeSetupEffects();
            var result = await NativeWorkspaceSetup.InitializeAsync(new(_paths.StartDir, org, project, team,
                gitProject, provider, host, repository, binding, coreStore, account, _paths.GlobalDisplayPath),
                _bindings, effects, ct, _transport, _githubProcesses);
            if (!result.IsSuccess)
            {
                if (NativeConnectionSetup.ReadFailure(result.Error) is { IsSuccess: true, Value.Cancelled: true })
                    throw new OperationCanceledException(ct);
                return exit = Fail(result.Error.Message, effects.CommandExitCode);
            }
            var database = result.Value.Document.DatabasePath;
            var twigDir = Path.Combine(_paths.StartDir, ".twig");
            _renderers.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(
                [new RenderNode.Record("initialized", new Dictionary<string, RenderCell>
                {
                    ["coreInitialized"] = RenderCell.Boolean(true),
                    ["databasePath"] = RenderCell.String(database),
                    ["message"] = RenderCell.String("Initialized a new natively bound Core workspace; no legacy cache or existing state was adopted."
                        + (coreStore is not null && !database.StartsWith(twigDir + Path.DirectorySeparatorChar,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            ? " Custom store files outside .twig are not automatically Git-ignored; protect the database and SQLite sidecars separately." : "")),
                })]));
            return exit = 0;
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(_telemetry, "init", outputFormat, exit, started); }
            catch (Exception) { }
        }

        int Fail(string message, int code = 1)
        {
            CommandError.Write(_renderers, Console.Error, outputFormat, message);
            return code;
        }
    }
}
