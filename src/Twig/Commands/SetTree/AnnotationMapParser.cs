using System.Globalization;
using System.Text.Json;
using Twig.Domain.ValueObjects;

namespace Twig.Commands.SetTree;

/// <summary>
/// Parses the <c>--annotate</c> JSON map (id → annotation) for twig#277.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every failure here is loud.</strong> An unknown style name, an icon id
/// <see cref="IconSet"/> cannot resolve, a non-numeric key, or an annotation whose
/// id is not in the working set all abort the render with a message naming the
/// offending key. That is deliberate and is the ticket's central rule: in a consent
/// surface an annotation that fails to appear is worse than a crash, because the
/// reviewer approves a write believing they saw everything.
/// </para>
/// <para>
/// Parsed with <see cref="Utf8JsonReader"/>/<see cref="JsonDocument"/> rather than a
/// serializer so the path stays AOT-clean (no reflection), matching
/// <see cref="RenderTree.JsonRenderer"/>.
/// </para>
/// </remarks>
internal static class AnnotationMapParser
{
    internal sealed record Result(IReadOnlyDictionary<int, TreeAnnotation> Annotations, string? Error)
    {
        internal bool Ok => Error is null;
    }

    /// <summary>
    /// Reads the annotation source: inline JSON, <c>@file</c>, or <c>@-</c> for stdin.
    /// </summary>
    internal static Result Parse(
        string spec,
        Func<string, string>? readFile = null,
        Func<string>? readStdin = null)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return new Result(new Dictionary<int, TreeAnnotation>(), "--annotate requires a JSON map or @file.");

        var trimmed = spec.Trim();
        string json;
        if (trimmed.StartsWith('@'))
        {
            var path = trimmed[1..];
            if (path is "-")
            {
                try { json = (readStdin ?? Console.In.ReadToEnd)(); }
                catch (IOException ex)
                {
                    return new Result(new Dictionary<int, TreeAnnotation>(), $"Could not read annotations from stdin: {ex.Message}");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new Result(new Dictionary<int, TreeAnnotation>(), "--annotate @<file> requires a file path.");

                try { json = (readFile ?? File.ReadAllText)(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return new Result(new Dictionary<int, TreeAnnotation>(), $"Could not read annotations from '{path}': {ex.Message}");
                }
            }
        }
        else
        {
            json = trimmed;
        }

        return ParseJson(json);
    }

    private static Result ParseJson(string json)
    {
        var empty = new Dictionary<int, TreeAnnotation>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new Result(empty, $"--annotate is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Result(empty, "--annotate must be a JSON object mapping work item id to annotation.");

            var map = new Dictionary<int, TreeAnnotation>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var key = property.Name.Trim().TrimStart('#');
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    return new Result(empty, $"Annotation key '{property.Name}' is not a valid work item id.");

                if (property.Value.ValueKind != JsonValueKind.Object)
                    return new Result(empty, $"Annotation for #{id} must be an object with 'note', 'style', and/or 'icon'.");

                string? note = null;
                string? styleName = null;
                string? iconId = null;

                foreach (var field in property.Value.EnumerateObject())
                {
                    switch (field.Name.ToLowerInvariant())
                    {
                        case "note":
                            if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                                return new Result(empty, $"Annotation for #{id}: 'note' must be a string.");
                            note = field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetString();
                            break;
                        case "style":
                            if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                                return new Result(empty, $"Annotation for #{id}: 'style' must be a string.");
                            styleName = field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetString();
                            break;
                        case "icon":
                            if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                                return new Result(empty, $"Annotation for #{id}: 'icon' must be a string.");
                            iconId = field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetString();
                            break;
                        default:
                            return new Result(
                                empty,
                                $"Annotation for #{id}: unknown field '{field.Name}'. Expected 'note', 'style', or 'icon'.");
                    }
                }

                if (!AnnotationStyleParser.TryParse(styleName, out var style))
                {
                    return new Result(
                        empty,
                        $"Annotation for #{id}: unknown style '{styleName}'. Expected one of: {string.Join(", ", AnnotationStyleParser.Accepted)}.");
                }

                if (!string.IsNullOrWhiteSpace(iconId))
                {
                    // Validate against the unicode table: it and the nerd table are
                    // keyed identically, so an id present in one is present in both.
                    // Validating here — not at render time — means a bad icon fails
                    // before any partial tree reaches the reviewer.
                    if (!IconSet.UnicodeIconsByIconId.ContainsKey(iconId))
                    {
                        return new Result(
                            empty,
                            $"Annotation for #{id}: unknown icon id '{iconId}'. See IconSet for the accepted ids.");
                    }
                }
                else
                {
                    iconId = null;
                }

                map[id] = new TreeAnnotation(note ?? string.Empty, style, iconId);
            }

            return new Result(map, null);
        }
    }
}
