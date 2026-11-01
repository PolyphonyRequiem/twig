using System.Text;

namespace Twig.Commands;

internal static class SeedFieldEditor
{
    internal static bool IsSystemManaged(string reference) => reference.ToLowerInvariant() is
        "system.id" or "system.rev" or "system.createddate" or "system.changeddate" or "system.watermark"
        or "system.createdby" or "system.changedby" or "system.authorizeddate" or "system.reviseddate"
        or "system.boardcolumn" or "system.boardcolumndone" or "system.boardlane";

    internal static string Generate(IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<(string ReferenceName, string DisplayName, bool IsReadOnly)> fields)
    {
        var buffer = new StringBuilder();
        buffer.AppendLine("## Seed editor — edit fields below. Lines starting with ## are ignored.");
        buffer.AppendLine("## Run 'twig sync' to sync field definitions from ADO.");
        buffer.AppendLine();
        var writable = fields.Where(field => !field.IsReadOnly)
            .OrderBy(field => field.ReferenceName == "System.Title" ? 0
                : field.ReferenceName == "System.Description" ? 1 : 2)
            .ThenBy(field => field.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var field in writable)
        {
            buffer.AppendLine("# " + field.DisplayName);
            if (values.TryGetValue(field.ReferenceName, out var value) && !string.IsNullOrEmpty(value))
                buffer.AppendLine(value);
            buffer.AppendLine();
        }
        return buffer.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    internal static IReadOnlyDictionary<string, string?> Parse(string content,
        IReadOnlyList<(string ReferenceName, string DisplayName, bool IsReadOnly)> fields)
    {
        var names = fields.ToDictionary(field => field.DisplayName, field => field.ReferenceName,
            StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? reference = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (reference is not null)
            {
                var text = value.ToString().Trim();
                result[reference] = text.Length == 0 ? null : text;
            }
        }
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                reference = names.GetValueOrDefault(line[2..].Trim());
                value.Clear();
            }
            else if (reference is not null)
            {
                if (value.Length > 0) value.AppendLine();
                value.Append(line);
            }
        }
        Flush();
        return result;
    }
}
