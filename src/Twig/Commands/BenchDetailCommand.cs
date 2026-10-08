using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Spectre.Console;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Renders full Show detail from cache, with an explicit selected-item-only pull.</summary>
internal sealed class BenchDetailCommand(CommandContext ctx, CurrentBenchResolver resolver,
    IBenchRepository benches, IWorkItemRepository repository, IFieldDefinitionStore fieldDefinitions,
    IAuthenticationProvider authentication, IConnectionBindingService bindings, TwigPaths paths,
    RendererFactory renderers, IWorkItemLinkRepository links, IPendingChangeStore pendingChanges,
    StatusFieldConfigReader statusFields, SyncCoordinatorFactory syncCoordinators,
    IProcessConfigurationProvider processConfiguration, SpectreTheme theme)
{
    internal async Task<int> ExecuteAsync(int id, int width = 80,
        string output = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        bool sync = false, CancellationToken ct = default)
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
            if (sync)
            {
                if (id < 0)
                    throw new ArgumentException("Local seeds cannot sync or publish from the detail viewer.");
                await EnsureCaptureAsync();
                try
                {
                    // Pull only this root and its edge metadata. SyncRootLinksAsync also
                    // materializes related targets, which are outside the captured selection.
                    await syncCoordinators.ReadOnly.SyncLinksAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Sync failed or incomplete for #{id}: {ex.Message}", ex);
                }
                await EnsureCaptureAsync();
            }
            var item = await repository.GetByIdAsync(id, ct)
                ?? throw new InvalidOperationException(sync
                    ? $"Work item #{id} could not be loaded after sync; local pending changes may protect it."
                    : $"Work item #{id} is not cached. Press S or run 'twig bench detail {id} --sync' to pull this item explicitly; no refresh was performed.");
            var definitions = await fieldDefinitions.GetAllAsync(ct);
            var entries = await statusFields.ReadAsync(ct);
            var children = await repository.GetChildrenAsync(item.Id, ct);
            var parent = item.ParentId.HasValue ? await repository.GetByIdAsync(item.ParentId.Value, ct) : null;
            var cachedLinks = await links.GetLinksAsync(item.Id, ct);
            var changes = await pendingChanges.GetChangesAsync(item.Id, ct);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var useAnsi = !output.Equals("minimal", StringComparison.OrdinalIgnoreCase) &&
                (output.ToLowerInvariant() is "json" or "json-full" or "json-compact" || !Console.IsOutputRedirected);
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(writer),
                Ansi = useAnsi ? AnsiSupport.Yes : AnsiSupport.No,
                ColorSystem = useAnsi ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
            });
            console.Profile.Width = width;
            console.Profile.Capabilities.Ansi = useAnsi;
            console.Profile.Capabilities.ColorSystem = useAnsi ? ColorSystem.TrueColor : ColorSystem.NoColors;
            console.Profile.Capabilities.Links = false;
            console.Profile.Capabilities.Interactive = false;
            console.Profile.Capabilities.Unicode = true;
            var view = await new SpectreRenderer(console, theme).BuildStatusViewAsync(item,
                () => Task.FromResult(changes), fieldDefinitions: definitions, statusFieldEntries: entries,
                childProgress: processConfiguration.ComputeChildProgress(children), links: cachedLinks,
                parent: parent, children: children, cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                fullContent: true);
            console.Write(view);
            // All yielding reads and rendering finish before the captured authority is
            // rechecked. A changed Bench/connection must never receive this observation.
            await EnsureCaptureAsync();

            async Task EnsureCaptureAsync()
            {
                var currentBinding = await bindings.ResolveAsync(ctx.Config, paths, ct);
                BrowserOriginGuard.EnsureExpected(currentBinding, binding.Binding.BindingId, binding.Identity.IdentityId);
                if (currentBinding.Operation != binding.Operation)
                    throw new InvalidOperationException("The browser connection changed while reading detail. Reconnect before retrying.");
                await resolver.ResolveStoredAsync(ct, bench.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")
            {
                var ansi = writer.ToString();
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
            else
                Console.Write(writer.ToString());
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

}
