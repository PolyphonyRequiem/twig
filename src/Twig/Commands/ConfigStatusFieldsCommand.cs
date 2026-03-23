using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config status-fields</c>: generates a status-fields configuration
/// file, opens it in the user's editor, and persists the result to <c>.twig/status-fields</c>.
/// </summary>
public sealed class ConfigStatusFieldsCommand(
    IFieldDefinitionStore fieldDefinitionStore,
    IEditorLauncher editorLauncher,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory)
{
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var definitions = await fieldDefinitionStore.GetAllAsync(ct);
        if (definitions.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatError("No field definitions cached. Run 'twig refresh' first."));
            return 1;
        }

        var existingContent = File.Exists(paths.StatusFieldsPath)
            ? await File.ReadAllTextAsync(paths.StatusFieldsPath, ct)
            : null;

        var content = StatusFieldsConfig.Generate(definitions, existingContent);

        var edited = await editorLauncher.LaunchAsync(content, ct);
        if (edited is null)
        {
            Console.WriteLine(fmt.FormatInfo("Configuration cancelled."));
            return 0;
        }

        await File.WriteAllTextAsync(paths.StatusFieldsPath, edited, ct);

        var entries = StatusFieldsConfig.Parse(edited);
        var count = entries.Count(e => e.IsIncluded);

        Console.WriteLine(fmt.FormatSuccess($"Saved {count} field(s) to .twig/status-fields."));
        return 0;
    }
}
