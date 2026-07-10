using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed edit &lt;id&gt;</c>: opens the seed in an external editor,
/// parses changes, and saves the updated seed locally.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success/info output is built as a <see cref="RenderTree.RenderTree"/> per output format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SeedEditCommand(
    IWorkItemRepository workItemRepo,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Edit seed fields in an external editor.</summary>
    public async Task<int> ExecuteAsync(
        int id,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var seed = await workItemRepo.GetByIdAsync(id, ct);
        if (seed is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Seed #{id} not found."));
            return 1;
        }

        if (!seed.IsSeed)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{id} is not a seed."));
            return 1;
        }

        var fieldDefs = await fieldDefStore.GetAllAsync(ct);
        var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
        var edited = await editorLauncher.LaunchAsync(buffer, ct);

        if (edited is null)
        {
            RenderInfo("Seed edit cancelled (editor aborted).", "seedEditCancelled", outputFormat);
            return 0;
        }

        var parsedFields = SeedEditorFormat.Parse(edited, fieldDefs);

        var newTitle = parsedFields.TryGetValue("System.Title", out var parsedTitle) && !string.IsNullOrWhiteSpace(parsedTitle)
            ? parsedTitle : seed.Title;

        // Compute number of changed fields
        var changedCount = 0;
        if (!string.Equals(newTitle, seed.Title, StringComparison.Ordinal))
            changedCount++;

        foreach (var kvp in parsedFields)
        {
            if (string.Equals(kvp.Key, "System.Title", StringComparison.OrdinalIgnoreCase))
                continue;

            seed.Fields.TryGetValue(kvp.Key, out var oldValue);
            if (!string.Equals(oldValue, kvp.Value, StringComparison.Ordinal))
                changedCount++;
        }

        if (changedCount == 0)
        {
            RenderInfo("No changes detected.", "seedEditNoChanges", outputFormat);
            return 0;
        }

        var updateResult = seed.TryWithSeedFields(newTitle, parsedFields);
        if (!updateResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(updateResult.Error));
            return 1;
        }

        var updated = updateResult.Value;
        await workItemRepo.SaveAsync(updated, ct);

        var message = $"Updated seed #{id} {updated.Title} ({changedCount} field(s) changed)";
        RenderUpdated(id, updated.Title, changedCount, message, outputFormat);
        return 0;
    }

    private void RenderUpdated(int id, string title, int changedCount, string message, string outputFormat)
    {
        var tree = BuildUpdatedTree(id, title, changedCount, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderInfo(string message, string recordKind, string outputFormat)
    {
        var tree = BuildInfoTree(message, recordKind, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildUpdatedTree(
        int id, string title, int changedCount, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildUpdatedRecord(id, title, changedCount, message),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderTree.RenderTree BuildInfoTree(string message, string recordKind, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildInfoRecord(recordKind, message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildUpdatedRecord(int id, string title, int changedCount, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(id.ToString(), new RenderValue.Integer(id)),
            ["title"] = new RenderCell(title, new RenderValue.String(title)),
            ["changedFieldCount"] = new RenderCell(changedCount.ToString(), new RenderValue.Integer(changedCount)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("seedEdited", fields);
    }

    private static RenderNode BuildInfoRecord(string recordKind, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record(recordKind, fields);
    }
}