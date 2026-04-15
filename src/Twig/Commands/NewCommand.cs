using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

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
    public async Task<int> ExecuteAsync(
        string? title,
        string? type = null,
        string? area = null,
        string? iteration = null,
        string? description = null,
        int? parent = null,
        bool set = false,
        bool editor = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig new --title \"title\" --type <type>"));
            return 2;
        }

        if (type is null)
        {
            var msg = parent is null
                ? "Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."
                : "--type is required. Type inference from --parent is not yet supported; use --type <type> explicitly.";
            Console.Error.WriteLine(fmt.FormatError(msg));
            return 1;
        }

        var typeResult = WorkItemType.Parse(type);
        if (!typeResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
            return 1;
        }

        if (parent is <= 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"--parent must be a positive work-item ID (got {parent.Value})."));
            return 1;
        }

        var areaResult = ResolveAreaPath(area);
        if (!areaResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(areaResult.Error));
            return 1;
        }

        var iterResult = ResolveIterationPath(iteration);
        if (!iterResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(iterResult.Error));
            return 1;
        }

        var seedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var seedResult = SeedFactory.CreateUnparented(
            seedTitle,
            typeResult.Value,
            areaResult.Value,
            iterResult.Value,
            config.User.DisplayName,
            parent);

        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", description);

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

        WorkItem fetched;
        try
        {
            fetched = await adoService.FetchAsync(newId, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run 'twig sync' to recover."));
            return 1;
        }

        await workItemRepo.SaveAsync(fetched, ct);

        if (set)
            await contextStore.SetActiveWorkItemIdAsync(newId, ct);

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

    private string? ResolveRaw(string? flag, string? configDefault)
        => flag ?? configDefault ?? (string.IsNullOrWhiteSpace(config.Project) ? null : config.Project);

    private Result<AreaPath> ResolveAreaPath(string? flag)
    {
        var raw = ResolveRaw(flag, config.Defaults.AreaPath);
        return raw is null
            ? Result.Fail<AreaPath>("No area path: use --area, set defaults.areaPath in config, or ensure project is configured.")
            : AreaPath.Parse(raw);
    }

    private Result<IterationPath> ResolveIterationPath(string? flag)
    {
        var raw = ResolveRaw(flag, config.Defaults.IterationPath);
        return raw is null
            ? Result.Fail<IterationPath>("No iteration path: use --iteration, set defaults.iterationPath in config, or ensure project is configured.")
            : IterationPath.Parse(raw);
    }
}
