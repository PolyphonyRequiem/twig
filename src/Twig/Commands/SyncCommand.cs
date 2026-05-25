using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig sync</c>: flush pending changes to ADO (push), then refresh the local cache (pull).
/// Delegates push to <see cref="IPendingChangeFlusher"/> and pull to <see cref="RefreshCommand"/>.
/// </summary>
public sealed class SyncCommand(
    IPendingChangeFlusher pendingChangeFlusher,
    RefreshCommand refreshCommand,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Flush pending changes then refresh the local cache.</summary>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="force">When true, pass --force to the refresh phase.</param>
    /// <param name="pullOnly">When true, skip the flush phase and go directly to refresh.</param>
    // TODO: Add targetId parameter to scope flush to a single item (T-1342.1 / #1342 AC: `twig sync <id>`).
    // Deferred to a follow-up task — currently flushes all dirty items via FlushAllAsync.
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool force = false,
        bool pullOnly = false,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        FlushResult? flushResult = null;

        if (!pullOnly)
        {
            // ── Phase 1: Push ──────────────────────────────────────────
            flushResult = await pendingChangeFlusher.FlushAllAsync(outputFormat, ct);

            if (flushResult.Failures.Count > 0)
            {
                foreach (var failure in flushResult.Failures)
                    _stderr.WriteLine(fmt.FormatError($"Flush failed for #{failure.ItemId}: {failure.Error}"));
            }
        }

        // ── Phase 2: Pull ──────────────────────────────────────────
        var refreshExitCode = await refreshCommand.ExecuteAsync(outputFormat, force, ct);

        // ── Output summary ─────────────────────────────────────────
        var hasFlushFailures = flushResult?.Failures.Count > 0;
        var exitCode = hasFlushFailures || refreshExitCode != 0 ? 1 : 0;

        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            RenderSyncJson(flushResult, refreshExitCode, pullOnly, outputFormat ?? string.Empty);
        }
        else if (lower == "minimal")
        {
            if (!pullOnly)
            {
                var msg = flushResult!.ItemsFlushed > 0 || hasFlushFailures
                    ? $"flushed: {flushResult.ItemsFlushed}, failed: {flushResult.Failures.Count}"
                    : "nothing to flush";
                _rendererFactory.GetRenderer(outputFormat ?? string.Empty).Render(new RenderTree.RenderTree(new[]
                {
                    (RenderNode)new RenderNode.Text(msg),
                }));
            }
        }
        else if (!pullOnly)
        {
            if (flushResult!.ItemsFlushed > 0 || hasFlushFailures)
            {
                Console.WriteLine($"Sync push: {flushResult.ItemsFlushed} flushed, {flushResult.Failures.Count} failed.");
            }
            else
            {
                Console.WriteLine("Sync push: nothing to flush.");
            }
        }

        return exitCode;
    }

    private void RenderSyncJson(FlushResult? flush, int refreshExitCode, bool pullOnly, string outputFormat)
    {
        var flushFields = new List<DocumentField>
        {
            new("flushed", new RenderNode.KeyValue("flushed", RenderCell.Integer(flush?.ItemsFlushed ?? 0))),
            new("fieldChangesPushed", new RenderNode.KeyValue("fieldChangesPushed", RenderCell.Integer(flush?.FieldChangesPushed ?? 0))),
            new("notesPushed", new RenderNode.KeyValue("notesPushed", RenderCell.Integer(flush?.NotesPushed ?? 0))),
            new("failed", new RenderNode.KeyValue("failed", RenderCell.Integer(flush?.Failures.Count ?? 0))),
        };
        if (flush?.Failures.Count > 0)
        {
            var failuresTable = new RenderNode.Table(null,
                new List<RenderColumn>
                {
                    new("itemId", "Item ID"),
                    new("error", "Error"),
                },
                flush.Failures.Select(f => new RenderRow("flushFailure", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["itemId"] = RenderCell.Integer(f.ItemId),
                    ["error"] = RenderCell.String(f.Error),
                })).ToList());
            flushFields.Add(new("failures", failuresTable));
        }
        var refreshFields = new List<DocumentField>
        {
            new("exitCode", new RenderNode.KeyValue("exitCode", RenderCell.Integer(refreshExitCode))),
        };
        var rootFields = new List<DocumentField>
        {
            new("pullOnly", new RenderNode.KeyValue("pullOnly", RenderCell.Boolean(pullOnly))),
            new("flush", new RenderNode.Document(null, flushFields)),
            new("refresh", new RenderNode.Document(null, refreshFields)),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
        {
            (RenderNode)new RenderNode.Document("sync", rootFields),
        }));
    }
}
