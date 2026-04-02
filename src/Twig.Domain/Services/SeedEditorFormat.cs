using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Generates and parses a section-header editor format for seed work items.
/// Each writable field is presented as <c># DisplayName</c> followed by the field value.
/// Lines starting with <c>## </c> are comments and ignored during parse.
/// </summary>
public static class SeedEditorFormat
{
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id",
        "System.Rev",
        "System.CreatedDate",
        "System.ChangedDate",
        "System.Watermark",
        "System.CreatedBy",
        "System.ChangedBy",
        "System.AuthorizedDate",
        "System.RevisedDate",
        "System.BoardColumn",
        "System.BoardColumnDone",
        "System.BoardLane",
    };

    /// <summary>
    /// Generates an editor buffer from the given seed and field definitions.
    /// </summary>
    public static string Generate(
        WorkItem seed,
        IReadOnlyList<FieldDefinition> fieldDefinitions)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Seed editor — edit fields below. Lines starting with ## are ignored.");
        sb.AppendLine("## Run 'twig sync' to sync field definitions from ADO.");
        sb.AppendLine();

        var writableFields = GetOrderedWritableFields(fieldDefinitions);

        // Graceful degradation: if no field definitions, show Title and Description with hint
        if (writableFields.Count == 0)
        {
            sb.AppendLine("## No field definitions found. Run 'twig sync' to sync from ADO.");
            sb.AppendLine();

            sb.AppendLine("# Title");
            sb.AppendLine(seed.Title);
            sb.AppendLine();

            sb.AppendLine("# Description");
            if (seed.Fields.TryGetValue("System.Description", out var descValue) && !string.IsNullOrEmpty(descValue))
                sb.AppendLine(descValue);

            return sb.ToString();
        }

        foreach (var field in writableFields)
        {
            sb.Append("# ");
            sb.AppendLine(field.DisplayName);

            var value = string.Equals(field.ReferenceName, "System.Title", StringComparison.OrdinalIgnoreCase)
                ? seed.Title
                : seed.Fields.TryGetValue(field.ReferenceName, out var v) ? v : null;
            if (!string.IsNullOrEmpty(value))
                sb.AppendLine(value);

            sb.AppendLine();
        }

        // Trim trailing blank line
        return sb.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    /// <summary>
    /// Parses an edited buffer back into a field dictionary keyed by reference name.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Parse(
        string content,
        IReadOnlyList<FieldDefinition> fieldDefinitions)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(content))
            return result;

        // Build display name → reference name lookup
        var displayToRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fd in fieldDefinitions)
        {
            displayToRef.TryAdd(fd.DisplayName, fd.ReferenceName);
        }

        // Always allow Title → System.Title and Description → System.Description
        // for graceful degradation when field definitions are empty
        displayToRef.TryAdd("Title", "System.Title");
        displayToRef.TryAdd("Description", "System.Description");

        string? currentReferenceName = null;
        var currentValue = new StringBuilder();

        var lines = content.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip comment lines
            if (line.StartsWith("## "))
                continue;

            // Check for section header
            if (line.StartsWith("# "))
            {
                // Flush previous section
                if (currentReferenceName is not null)
                {
                    result[currentReferenceName] = TrimValue(currentValue);
                }

                var displayName = line[2..].Trim();
                currentReferenceName = displayToRef.TryGetValue(displayName, out var refName)
                    ? refName
                    : null;
                currentValue.Clear();
                continue;
            }

            // Accumulate value lines for the current section
            if (currentReferenceName is not null)
            {
                if (currentValue.Length > 0)
                    currentValue.AppendLine();
                currentValue.Append(line);
            }
        }

        // Flush last section
        if (currentReferenceName is not null)
        {
            result[currentReferenceName] = TrimValue(currentValue);
        }

        return result;
    }

    private static List<FieldDefinition> GetOrderedWritableFields(IReadOnlyList<FieldDefinition> fieldDefinitions)
    {
        FieldDefinition? titleField = null;
        FieldDefinition? descField = null;
        var remaining = new List<FieldDefinition>();

        foreach (var fd in fieldDefinitions)
        {
            if (fd.IsReadOnly || ExcludedFields.Contains(fd.ReferenceName))
                continue;

            if (string.Equals(fd.ReferenceName, "System.Title", StringComparison.OrdinalIgnoreCase))
                titleField = fd;
            else if (string.Equals(fd.ReferenceName, "System.Description", StringComparison.OrdinalIgnoreCase))
                descField = fd;
            else
                remaining.Add(fd);
        }

        remaining.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        var ordered = new List<FieldDefinition>(remaining.Count + 2);
        if (titleField is not null) ordered.Add(titleField);
        if (descField is not null) ordered.Add(descField);
        ordered.AddRange(remaining);

        return ordered;
    }

    private static string? TrimValue(StringBuilder sb)
    {
        var value = sb.ToString().Trim();
        return value.Length == 0 ? null : value;
    }
}
