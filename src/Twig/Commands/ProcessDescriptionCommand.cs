using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Projects the same admitted Core schema, with an explicit version, scope and presentation gaps.</summary>
internal sealed class ProcessDescriptionCommand(
    ProcessCommand process,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    internal const string CompleteFormat = "json";
    internal static bool IsCompleteFormat(string? outputFormat) =>
        OutputFormats.Normalize(outputFormat) is "json" or "json-full" or "json-compact";

    public async Task<int> ExecuteAsync(
        string? typeName = null, string? outPath = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default,
        string? sections = null, string? fields = null)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        int Error(string message, int code = 1)
        {
            CommandError.Write(rendererFactory, _stderr, outputFormat, message);
            return code;
        }
        if (OutputFormats.Normalize(outputFormat) == "ids")
            return Error("Process descriptions contain no item IDs. Use -o json.", 2);
        var selected = sections?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compact = selected is { Count: > 0 };
        if (selected is not null && !compact
            || selected?.Any(section => !section.Equals("fields", StringComparison.OrdinalIgnoreCase)
                && !section.Equals("requirements", StringComparison.OrdinalIgnoreCase)) == true)
            return Error("Valid --sections are fields, requirements. Omit --sections for the full Core schema description.", 2);
        if (!compact && !string.IsNullOrWhiteSpace(fields))
            return Error("--fields requires --sections fields and/or requirements.", 2);
        if (compact && (typeName is null || !IsCompleteFormat(outputFormat)))
            return Error("Compact descriptions require an explicit type reference and -o json.", 2);
        var filter = string.IsNullOrWhiteSpace(fields) ? null
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        if (filter is { Count: 0 }) return Error("--fields must name at least one field reference.", 2);

        var acquired = await process.AcquireAsync(ct);
        if (!acquired.IsSuccess) return Error(acquired.Error.Message);
        var schema = acquired.Value;
        var types = typeName is null ? schema.Types.ToArray()
            : ProcessCommand.FindType(schema, typeName) is { } type ? [type] : [];
        if (types.Length == 0) return Error($"Unknown work-item type '{typeName}'. Run 'twig process' to list discovered references.");
        if (filter is not null && filter.Any(reference => !types.Single().Fields.Any(field => field.Reference == reference)))
            return Error("Unknown field reference in --fields; no filtered description was written.", 2);
        var gaps = schema.Gaps.Concat([
            "Native form layout is presentation metadata and is not included in the Core schema.",
            "Display names, colors, category membership and behavior metadata are outside this projection; type and field identities are references.",
            "This is provider-discovered Core schema evidence, not the legacy complete process descriptor or a guarantee about external automation.",
        ]);
        var tree = new RenderTree.RenderTree([new RenderNode.Document(null, [
            new("descriptorVersion", new RenderNode.KeyValue("descriptorVersion", RenderCell.String("core-schema-1"))),
            new("scope", new RenderNode.KeyValue("scope", RenderCell.String("provider-discovered-core-schema"))),
            new("schemaFingerprint", new RenderNode.KeyValue("schemaFingerprint", RenderCell.String(schema.Fingerprint))),
            new("knownGaps", new RenderNode.KeyValue("knownGaps", ProcessCommand.Array(gaps.Select(gap => RenderCell.String(gap))))),
            new("types", new RenderNode.Section(null, types.OrderBy(type => type.Reference, StringComparer.Ordinal)
                .Select(type => ProcessCommand.TypeDocument(type,
                    fields: !compact || selected!.Contains("fields"),
                    rules: !compact || selected!.Contains("requirements"), fieldFilter: filter)).ToArray())),
        ])]);
        // Render completely before replacing a retained file.
        using var rendered = new StringWriter();
        rendererFactory.GetRenderer(outputFormat, rendered).Render(tree);
        if (outPath is null)
        {
            Console.Out.WriteLine(rendered.ToString());
            return 0;
        }
        var temporary = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await File.WriteAllTextAsync(temporary, rendered.ToString(), ct);
            File.Move(temporary, outPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Error($"Could not write '{outPath}': {ex.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CommandError.Write(rendererFactory, _stderr, outputFormat, $"Could not remove scratch file '{temporary}': {ex.Message}");
            }
        }
        _stderr.WriteLine(fmt.FormatInfo($"Wrote process description (core-schema-1) to {outPath}"));
        return 0;
    }
}
