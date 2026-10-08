using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Spectre.Console;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Renders complete cached work-item detail without changing context or refreshing ADO.</summary>
internal sealed class BenchDetailCommand(CommandContext ctx, CurrentBenchResolver resolver,
    IBenchRepository benches, IWorkItemRepository repository, IFieldDefinitionStore fieldDefinitions,
    IAuthenticationProvider authentication, IConnectionBindingService bindings, TwigPaths paths,
    RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(int id, int width = 80,
        string output = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (id == 0) throw new ArgumentException("A nonzero work-item ID or negative seed alias is required.");
            if (width < 1 || width > 10000) throw new ArgumentException("Width must be between 1 and 10000 columns.");
            if (output.Equals("ids", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A single-item detail supports human, json, and minimal output, not ids.");
            using var admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct);
            if (admission is null) throw new InvalidOperationException("Bench detail requires an admitted native connection.");
            using var operation = repository.AcquireOperation();
            var binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(binding, expectBinding, expectIdentity);
            // No first-use initialization here: it can discover the remote self identity.
            if (await benches.GetCurrentAsync(ct) is null && await benches.GetByNameAsync(Bench.DefaultName, ct) is null)
                throw new InvalidOperationException("No cached Bench is available. Open the Bench before requesting detail.");
            var bench = await resolver.ResolveStoredAsync(ct, expectBench);
            var item = await repository.GetByIdAsync(id, ct)
                ?? throw new InvalidOperationException($"Work item #{id} is not cached. Sync the Bench explicitly, then retry; no refresh was performed.");
            var definitions = (await fieldDefinitions.GetAllAsync(ct)).ToDictionary(
                definition => definition.ReferenceName, StringComparer.OrdinalIgnoreCase);
            var document = BuildDetail(item, definitions, width);
            // Metadata reads can yield while the current Bench changes. Never emit a
            // completed observation under an authority that no longer matches its capture.
            await resolver.ResolveStoredAsync(ct, bench.Id.ToString(CultureInfo.InvariantCulture));
            var currentBinding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(currentBinding, binding.Binding.BindingId, binding.Identity.IdentityId);
            if (currentBinding.Operation != binding.Operation)
                throw new InvalidOperationException("The browser connection changed while reading detail. Reconnect before retrying; no refresh was performed.");
            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")
            {
                var ansi = renderers.CaptureHuman(document, width, "always").Text;
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("benchDetail",
                    new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["version"] = RenderCell.Integer(1),
                        ["benchId"] = RenderCell.String(bench.Id.ToString(CultureInfo.InvariantCulture)),
                        ["benchName"] = RenderCell.String(bench.Name),
                        ["bindingId"] = RenderCell.String(binding.Binding.BindingId),
                        ["identityId"] = RenderCell.String(binding.Identity.IdentityId),
                        ["workItemId"] = RenderCell.Integer(item.Id),
                        ["title"] = RenderCell.String(RichHtmlRenderer.SafeText(item.Title)),
                        ["ansi"] = RenderCell.String(ansi),
                    })]));
            }
            else if (output.Equals("minimal", StringComparison.OrdinalIgnoreCase))
            {
                // Complete, readable, unstyled detail remains useful over a pipe.
                Console.Write(renderers.CaptureHuman(document, width, "never").Text);
            }
            else
            {
                var color = Console.IsOutputRedirected ? "never" : "always";
                renderers.GetHumanRenderer(Console.Out, width, color).Render(document);
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
                ["command"] = "bench-detail",
                ["output_format"] = output,
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }

    internal static RenderTree.RenderTree BuildDetail(WorkItem item,
        IReadOnlyDictionary<string, Twig.Domain.ValueObjects.FieldDefinition> definitions, int width)
    {
        var snapshot = new WorkItemMapper().ToSnapshot(item);
        var projection = WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot, definitions);
        var nodes = new List<RenderNode>
        {
            new RenderNode.Markup("[bold cyan]#" + item.Id.ToString(CultureInfo.InvariantCulture) + " " +
                Markup.Escape(RichHtmlRenderer.SafeText(item.Title)) + "[/]"),
            new RenderNode.Text(item.IsSeed ? "Read-only local seed · unpublished" : item.LastSyncedAt is { } synced
                ? "Read-only cache · last synced " + synced.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)
                : "Read-only cache · sync time unknown (not verified live)", Severity.Muted),
            new RenderNode.Text("Cached-field layout · no refresh performed", Severity.Muted),
            new RenderNode.Text(""),
        };
        foreach (var control in projection.Pages.SelectMany(page => page.AllGroups).SelectMany(group => group.Controls))
        {
            var value = control.Value;
            if (value is null) continue;
            var definition = definitions.GetValueOrDefault(control.Id);
            var label = RichHtmlRenderer.SafeText(definition?.DisplayName ?? control.Label);
            nodes.Add(new RenderNode.Markup("[bold underline]" + Markup.Escape(label) + "[/]"));
            if (value.State != DetailFieldState.HasValue)
                nodes.Add(new RenderNode.Text(value.State == DetailFieldState.EmptyOnServer
                    ? "(empty in cached snapshot)" : "(not carried by Twig; value unknown)", Severity.Muted));
            else if (string.Equals(definition?.DataType, "html", StringComparison.OrdinalIgnoreCase))
            {
                var rich = RichHtmlRenderer.Render(value.Full, width);
                if (rich.Count == 0) nodes.Add(new RenderNode.Text("(no visible text in cached HTML)", Severity.Muted));
                else nodes.AddRange(rich);
            }
            else nodes.Add(new RenderNode.Markup(Markup.Escape(RichHtmlRenderer.SafeText(value.Full!))));
            nodes.Add(new RenderNode.Text(""));
        }
        if (definitions.Count == 0)
            nodes.Add(new RenderNode.Text("Field metadata is not cached; unknown field types are shown as literal values.", Severity.Muted));
        return new RenderTree.RenderTree(nodes);
    }
}
