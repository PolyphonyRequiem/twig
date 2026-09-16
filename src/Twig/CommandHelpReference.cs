using System.Text.Json;

/// <summary>
/// Installed, executable-matched behavioral guidance from the canonical command
/// reference. Only narrative sections are reused: syntax, option names and defaults
/// always come from ConsoleAppFramework's generated declarations.
///
/// Packaging contract (owned by the parent integration): embed docs/commands/index.json
/// and docs/commands/**/*.md under LogicalName Twig.CommandReference/ followed by the
/// relative path (e.g. Twig.CommandReference/context/show.md). No checkout fallback.
/// </summary>
internal static class CommandHelpReference
{
    internal static void ShowIfPresent(string command, TextWriter? output = null)
    {
        output ??= Console.Out;
        using var indexStream = OpenResource("index.json");
        using var index = JsonDocument.Parse(indexStream);
        foreach (var item in index.RootElement.EnumerateArray())
        {
            if (item.GetProperty("command").GetString() != command) continue;
            var path = item.GetProperty("path").GetString()!;
            using var stream = OpenResource(path);
            using var reader = new StreamReader(stream);
            var narrative = ExtractNarrative(reader.ReadToEnd());
            output.WriteLine();
            output.WriteLine($"Behavior and effects (bundled reference: docs/commands/{path}):");
            output.WriteLine(narrative);
            return;
        }
        // A newly registered command must get a reference before it is shipped.
        // The completeness test detects this rather than substituting generic claims
        // like 'read only' that hide conditional writes or local journal effects.
        output.WriteLine();
        output.WriteLine($"Behavior reference is not bundled for '{command}'.");
    }

    internal static Stream OpenResource(string relativePath)
    {
        var assembly = typeof(CommandHelpReference).Assembly;
        var expected = "Twig.CommandReference/" + relativePath;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n =>
            n.Replace('\\', '/') == expected);
        return name is null
            ? throw new InvalidOperationException($"Bundled command help resource missing: {expected}")
            : assembly.GetManifestResourceStream(name)!;
    }

    internal static string ExtractNarrative(string markdown)
    {
        using var reader = new StringReader(markdown);
        using var writer = new StringWriter();
        var include = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                include = heading is "Behavior" or "Side effects" or "Effects"
                    || heading.StartsWith("Exit codes", StringComparison.Ordinal)
                    || heading.StartsWith("Failure", StringComparison.Ordinal);
            }
            if (include) writer.WriteLine(line);
        }
        return writer.ToString().TrimEnd();
    }
}
