using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Reads the configured team's actual area paths without importing workspace defaults or changing a Bench.</summary>
internal sealed class BenchAreaCandidatesCommand(CommandContext ctx, CurrentBenchResolver resolver,
    IBenchRepository benches, IWorkItemRepository repository, IIterationService iterations, IAuthenticationProvider authentication,
    IConnectionBindingService bindings, TwigPaths paths, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string output = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            using var admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct);
            if (admission is null) throw new InvalidOperationException("Team area candidates require an admitted native connection.");
            using var operation = repository.AcquireOperation();
            var binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(binding, expectBinding, expectIdentity);
            if (await benches.GetCurrentAsync(ct) is null && await benches.GetByNameAsync(Twig.Domain.Aggregates.Bench.DefaultName, ct) is null)
                throw new InvalidOperationException("No cached Bench is available. Open the Bench before requesting team areas.");
            var bench = await resolver.ResolveStoredAsync(ct, expectBench);
            var benchId = bench.Id.ToString(CultureInfo.InvariantCulture);
            var digest = BenchQueryRule.SettingsDigest(bench.Selectors);
            var team = TwigConfiguration.ResolveTeam(binding.Operation.Project, binding.Operation.Team);
            var areas = await iterations.GetTeamAreaPathsAsync(ct);
            if (areas.Any(area => string.IsNullOrWhiteSpace(area.Path)))
                throw new InvalidOperationException("The configured team returned an invalid area path. No settings were changed.");

            // A network observation cannot be relabelled as a later Bench/settings capture.
            var current = await resolver.ResolveStoredAsync(ct, benchId);
            if (!string.Equals(digest, BenchQueryRule.SettingsDigest(current.Selectors), StringComparison.Ordinal))
                throw new InvalidOperationException("Automatic settings changed while reading team areas. Refresh and retry; no settings were changed.");
            var currentBinding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(currentBinding, binding.Binding.BindingId, binding.Identity.IdentityId);
            if (currentBinding.Operation != binding.Operation)
                throw new InvalidOperationException("The browser connection changed while reading team areas. Reconnect before retrying; no settings were changed.");

            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")
            {
                var values = areas.Select(area => new RenderCell(string.Empty, new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["path"] = RenderCell.String(area.Path),
                    ["includeChildren"] = RenderCell.Boolean(area.IncludeChildren),
                }))).ToArray();
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("benchAreaCandidates", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["version"] = RenderCell.Integer(1),
                    ["benchId"] = RenderCell.String(benchId),
                    ["benchName"] = RenderCell.String(bench.Name),
                    ["bindingId"] = RenderCell.String(binding.Binding.BindingId),
                    ["identityId"] = RenderCell.String(binding.Identity.IdentityId),
                    ["team"] = RenderCell.String(team),
                    ["areas"] = new(string.Empty, new RenderValue.Array(values)),
                    ["settingsDigest"] = RenderCell.String(digest),
                })]));
            }
            else
            {
                var rows = areas.Select(area => new RenderRow(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["path"] = RenderCell.String(area.Path),
                    ["semantics"] = RenderCell.String(area.IncludeChildren ? "under (include descendants)" : "exact (this area only)"),
                })).ToArray();
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([
                    new RenderNode.Text("Configured team: " + team),
                    new RenderNode.Text("Select individual areas for Bench " + bench.Name + "; this does not change the team or workspace area defaults."),
                    new RenderNode.Table("Team area paths", [new("path", "Path"), new("semantics", "Semantics")], rows),
                    new RenderNode.Text(areas.Count == 0 ? "The configured team has no area paths." : "Settings digest: " + digest),
                ]));
            }
            exit = 0;
            return exit;
        }
        catch (ArgumentException ex)
        {
            exit = 2;
            CommandError.Write(renderers, ctx.StderrWriter, output, ex.Message);
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommandError.Write(renderers, ctx.StderrWriter, output, ex.Message);
            return exit;
        }
        finally
        {
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "bench-configuration-area-candidates",
                ["output_format"] = output,
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }
}
