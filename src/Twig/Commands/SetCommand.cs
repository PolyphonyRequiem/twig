using SpectreMarkup = Spectre.Console.Markup;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig set &lt;idOrPattern&gt;</c>: resolves a work item by ID or title pattern,
/// fetches from ADO if not cached, sets active context, and emits a single confirmation line.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: the command builds a <see cref="RenderTree.RenderTree"/> per output format
/// and dispatches through <see cref="RendererFactory"/>. The legacy
/// <see cref="IAsyncRenderer"/> dependency is retained only for the interactive
/// disambiguation prompt (a TTY-only feature outside the rendering seam's scope);
/// <see cref="OutputFormatterFactory"/> is used only for stderr error formatting.
/// </remarks>
public sealed class SetCommand(
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    RendererFactory? rendererFactory = null,
    IPromptStateWriter? promptStateWriter = null,
    INavigationHistoryStore? historyStore = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    public async Task<int> ExecuteAsync(string idOrPattern, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("set", outputFormat);
        int exitCode;
        try
        {
            exitCode = await ExecuteCoreAsync(idOrPattern, outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "set", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ExecuteCoreAsync(string idOrPattern, string outputFormat, CancellationToken ct)
    {
        var (fmt, interactiveRenderer) = ctx.Resolve(outputFormat);

        if (string.IsNullOrWhiteSpace(idOrPattern))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig set <id or pattern>"));
            return 2;
        }

        Domain.Aggregates.WorkItem? item = null;

        if (int.TryParse(idOrPattern, out var id))
        {
            // Numeric ID: cache lookup then auto-fetch via ActiveItemResolver
            var result = await activeItemResolver.ResolveByIdAsync(id, ct);
            if (!result.TryGetWorkItem(out item, out var errId, out var errReason))
            {
                Console.Error.WriteLine(fmt.FormatError(errId is not null
                    ? $"Work item #{errId} could not be fetched: {errReason}"
                    : $"Work item #{id} not found."));
                return 1;
            }
            if (result is FetchedFromAdo)
            {
                var infoRenderer = _rendererFactory.GetRenderer(outputFormat);
                infoRenderer.Render(new RenderTree.RenderTree(new[] { (RenderNode)new RenderNode.Hint($"Fetching work item {id} from ADO...") }));
            }
        }
        else
        {
            // Pattern: search cache first — DD-10: disambiguation remains inline
            var cached = await workItemRepo.FindByPatternAsync(idOrPattern);
            if (cached.Count == 1)
            {
                item = cached[0];
            }
            else if (cached.Count > 1)
            {
                var matches = cached.Select(c => (c.Id, $"{c.Title} [{c.State}]")).ToList();

                if (interactiveRenderer is not null)
                {
                    // Interactive disambiguation — TTY + human format
                    var selected = await interactiveRenderer.PromptDisambiguationAsync(matches, ct);
                    if (selected is not null)
                    {
                        item = cached.FirstOrDefault(c => c.Id == selected.Value.Id);
                        if (item is null)
                            return 1;
                    }
                    else
                    {
                        return 1;
                    }
                }
                else
                {
                    // Static list fallback — JSON, minimal, piped, non-TTY
                    var disambigTree = BuildDisambiguationTree(matches);
                    var stderrRenderer = _rendererFactory.GetRenderer(outputFormat, Console.Error);
                    stderrRenderer.Render(disambigTree);
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine(fmt.FormatError($"No cached items match '{idOrPattern}'."));
                return 1;
            }
        }

        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        if (historyStore is not null)
            await historyStore.RecordVisitAsync(item.Id, ct);
        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var confirmTree = BuildConfirmationTree(item, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(confirmTree);

        return 0;
    }

    private static RenderTree.RenderTree BuildConfirmationTree(WorkItem item, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text($"#{item.Id}"),
            "json" or "json-full" or "json-compact" or "ids" => BuildConfirmationRecord(item),
            _ => BuildHumanConfirmation(item),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildConfirmationRecord(WorkItem item)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"]    = new RenderCell(item.Id.ToString(), new RenderValue.Integer(item.Id)),
            ["title"] = new RenderCell(item.Title, new RenderValue.String(item.Title)),
            ["state"] = new RenderCell(item.State, new RenderValue.String(item.State)),
            ["type"]  = new RenderCell(item.Type.ToString(), new RenderValue.String(item.Type.ToString())),
        };
        return new RenderNode.Record("setConfirmation", fields);
    }

    private static RenderNode BuildHumanConfirmation(WorkItem item)
    {
        var stateColor = GetStateMarkupColor(item.State);
        var escapedTitle = SpectreMarkup.Escape(item.Title);
        var escapedState = SpectreMarkup.Escape(item.State);
        // `[[` / `]]` are literal-bracket escapes in Spectre markup.
        var content = $"Set active item: #{item.Id} {escapedTitle} [[[{stateColor}]{escapedState}[/]]]";
        return new RenderNode.Markup(content);
    }

    private static RenderTree.RenderTree BuildDisambiguationTree(IReadOnlyList<(int Id, string Title)> matches)
    {
        var columns = new[]
        {
            new RenderColumn("id", "ID"),
            new RenderColumn("title", "Title"),
        };

        var rows = new List<RenderRow>(matches.Count);
        foreach (var (id, title) in matches)
        {
            var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["id"]    = new RenderCell(id.ToString(), new RenderValue.Integer(id)),
                ["title"] = new RenderCell(title, new RenderValue.String(title)),
            };
            rows.Add(new RenderRow(null, cells));
        }

        var table = new RenderNode.Table("Multiple matches:", columns, rows);
        return new RenderTree.RenderTree(new[] { (RenderNode)table });
    }

    private static string GetStateMarkupColor(string state) =>
        state.ToLowerInvariant() switch
        {
            "active" or "doing" or "in progress" => "yellow",
            "done" or "closed" or "completed" or "resolved" => "green",
            "new" or "to do" or "proposed" => "blue",
            "removed" or "cut" => "grey",
            _ => "white",
        };
}
