using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig link artifact &lt;url&gt;</c>: adds an artifact link (ArtifactLink for
/// vstfs:// URIs, Hyperlink for http/https URLs) to a work item.
/// Separate from <see cref="LinkCommand"/> which manages parent–child hierarchy links.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success path emits an "artifactLinked" or "artifactAlreadyLinked" record per format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class ArtifactLinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    OutputFormatterFactory formatterFactory,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Add an artifact link to the active (or specified) work item.</summary>
    public async Task<int> ExecuteAsync(
        string url,
        string? name = null,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("link-artifact", outputFormat);
        int exitCode;
        try
        {
            exitCode = await ExecuteCoreAsync(url, name, id, outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(telemetryClient, "link-artifact", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ExecuteCoreAsync(
        string url,
        string? name,
        int? id,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        bool alreadyLinked;
        try
        {
            alreadyLinked = await adoService.AddArtifactLinkAsync(item.Id, url, name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatError($"Link failed: {ex.Message}"));
            return 1;
        }

        var message = alreadyLinked
            ? $"Already linked: {url} on #{item.Id}."
            : $"Linked {url} to #{item.Id}.";
        var kind = alreadyLinked ? "artifactAlreadyLinked" : "artifactLinked";

        var tree = BuildTree(kind, item.Id, url, name, alreadyLinked, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    private static RenderTree.RenderTree BuildTree(string kind, int itemId, string url, string? name, bool alreadyLinked, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildRecord(kind, itemId, url, name, alreadyLinked, message),
            _ => new RenderNode.Text(message, alreadyLinked ? Severity.Info : Severity.Success),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildRecord(string kind, int itemId, string url, string? name, bool alreadyLinked, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["itemId"] = RenderCell.Integer(itemId),
            ["url"] = RenderCell.String(url),
            ["alreadyLinked"] = RenderCell.Boolean(alreadyLinked),
            ["message"] = RenderCell.String(message),
        };
        if (!string.IsNullOrEmpty(name))
            fields["name"] = RenderCell.String(name);
        return new RenderNode.Record(kind, fields);
    }
}