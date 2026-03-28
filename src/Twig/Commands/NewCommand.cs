using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig new --title "X" --type Epic [--area A] [--iteration I] [--description "..."] [--set] [--editor]</c>:
/// creates an unparented top-level work item and immediately publishes it to ADO.
/// </summary>
public sealed class NewCommand(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config)
{
    /// <summary>Create a new top-level work item in ADO.</summary>
    public async Task<int> ExecuteAsync(
        string? title,
        string type,
        string? area = null,
        string? iteration = null,
        string? description = null,
        bool set = false,
        bool editor = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // ── Validate title ──────────────────────────────────────────
        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig new --title \"title\" --type <type>"));
            return 2;
        }

        // ── Parse type ──────────────────────────────────────────────
        var typeResult = WorkItemType.Parse(type);
        if (!typeResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
            return 1;
        }

        // ── Resolve area path: flag → config.Defaults → config.Project ─
        var areaResult = ResolveAreaPath(area);
        if (!areaResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(areaResult.Error));
            return 1;
        }

        // ── Resolve iteration path: flag → config.Defaults → config.Project ─
        var iterResult = ResolveIterationPath(iteration);
        if (!iterResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(iterResult.Error));
            return 1;
        }

        // ── Create in-memory work item ──────────────────────────────
        var seedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var seedResult = SeedFactory.CreateUnparented(
            seedTitle,
            typeResult.Value,
            areaResult.Value,
            iterResult.Value,
            config.User.DisplayName);

        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        // ── Apply description ───────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", description);

        // ── Editor flow ─────────────────────────────────────────────
        if (editor)
        {
            var fieldDefs = await fieldDefStore.GetAllAsync(ct);
            var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
            var edited = await editorLauncher.LaunchAsync(buffer, ct);

            if (edited is null)
            {
                Console.WriteLine(fmt.FormatInfo("Creation cancelled (editor aborted)."));
                return 0;
            }

            var parsedFields = SeedEditorFormat.Parse(edited, fieldDefs);
            var newTitle = parsedFields.TryGetValue("System.Title", out var parsedTitle) && !string.IsNullOrWhiteSpace(parsedTitle)
                ? parsedTitle : seedTitle;
            seed = seed.WithSeedFields(newTitle, parsedFields);
        }

        // ── Create in ADO ───────────────────────────────────────────
        int newId;
        try
        {
            newId = await adoService.CreateAsync(seed, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Create failed: {ex.Message}"));
            return 1;
        }

        // ── Fetch back the full ADO item ────────────────────────────
        WorkItem fetched;
        try
        {
            fetched = await adoService.FetchAsync(newId, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run 'twig refresh' to recover."));
            return 1;
        }

        // ── Save fetched item locally ───────────────────────────────
        await workItemRepo.SaveAsync(fetched, ct);

        // ── Set context ─────────────────────────────────────────────
        if (set && newId > 0)
            await contextStore.SetActiveWorkItemIdAsync(newId, ct);

        // ── Output ──────────────────────────────────────────────────
        Console.WriteLine(fmt.FormatSuccess(
            $"Created #{newId} {fetched.Title} ({typeResult.Value})"));

        var hints = hintEngine.GetHints("new",
            outputFormat: outputFormat,
            createdId: newId);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    private Result<AreaPath> ResolveAreaPath(string? explicitFlag)
    {
        var raw = explicitFlag
            ?? config.Defaults.AreaPath
            ?? (string.IsNullOrWhiteSpace(config.Project) ? null : config.Project);

        if (raw is null)
            return Result.Fail<AreaPath>("No area path: use --area, set defaults.areaPath in config, or ensure project is configured.");

        return AreaPath.Parse(raw);
    }

    private Result<IterationPath> ResolveIterationPath(string? explicitFlag)
    {
        var raw = explicitFlag
            ?? config.Defaults.IterationPath
            ?? (string.IsNullOrWhiteSpace(config.Project) ? null : config.Project);

        if (raw is null)
            return Result.Fail<IterationPath>("No iteration path: use --iteration, set defaults.iterationPath in config, or ensure project is configured.");

        return IterationPath.Parse(raw);
    }
}
