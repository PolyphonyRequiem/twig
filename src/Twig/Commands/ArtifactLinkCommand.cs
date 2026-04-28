using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig link artifact &lt;url&gt;</c>: adds an artifact link (ArtifactLink for
/// vstfs:// URIs, Hyperlink for http/https URLs) to a work item.
/// Separate from <see cref="LinkCommand"/> which manages parent–child hierarchy links.
/// </summary>
public sealed class ArtifactLinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Add an artifact link to the active (or specified) work item.</summary>
    public async Task<int> ExecuteAsync(
        string url,
        string? name = null,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
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
        Console.WriteLine(fmt.FormatSuccess(message));
        return 0;
    }
}