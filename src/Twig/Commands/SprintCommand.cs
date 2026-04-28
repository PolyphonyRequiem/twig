using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements the <c>twig workspace sprint</c> command group for managing sprint iteration subscriptions:
/// <c>add</c>, <c>remove</c>, and <c>list</c>.
/// Sprint expressions can be relative (@current, @current±N) or absolute iteration paths.
/// </summary>
public sealed class SprintCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Add a sprint iteration expression to the workspace configuration.</summary>
    public async Task<int> AddAsync(string expression, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = ct;
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(expression))
        {
            Console.Error.WriteLine(fmt.FormatError("Sprint expression cannot be empty."));
            return 2;
        }

        config.Workspace.Sprints ??= [];

        // Duplicate check (case-insensitive)
        var existing = config.Workspace.Sprints
            .FindIndex(e => string.Equals(e.Expression, expression, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Sprint expression '{expression}' is already configured."));
            return 1;
        }

        var entry = new SprintEntry { Expression = expression };
        config.Workspace.Sprints.Add(entry);
        await config.SaveAsync(paths.ConfigPath, ct);

        Console.WriteLine(fmt.FormatSuccess($"Added sprint expression '{expression}'."));
        return 0;
    }

    /// <summary>Remove a sprint iteration expression from the workspace configuration.</summary>
    public async Task<int> RemoveAsync(string expression, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (config.Workspace.Sprints is not { Count: > 0 })
        {
            Console.Error.WriteLine(fmt.FormatError("No sprint expressions configured."));
            return 1;
        }

        var index = config.Workspace.Sprints
            .FindIndex(e => string.Equals(e.Expression, expression, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Sprint expression '{expression}' is not configured."));
            return 1;
        }

        config.Workspace.Sprints.RemoveAt(index);
        await config.SaveAsync(paths.ConfigPath, ct);

        Console.WriteLine(fmt.FormatSuccess($"Removed sprint expression '{expression}'."));
        return 0;
    }

    /// <summary>List all configured sprint iteration expressions.</summary>
    public Task<int> ListAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = ct;
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var entries = config.Workspace.Sprints;

        if (entries is not { Count: > 0 })
        {
            Console.WriteLine(fmt.FormatInfo("No sprint expressions configured. Use 'twig workspace sprint add <expr>' to configure."));
            return Task.FromResult(0);
        }

        foreach (var entry in entries)
            Console.WriteLine(fmt.FormatInfo(entry.Expression));
        Console.WriteLine(fmt.FormatInfo($"{entries.Count} sprint expression(s) configured."));
        return Task.FromResult(0);
    }
}
