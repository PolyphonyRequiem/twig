using System.Text;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Pure domain service for generating, parsing, and merging the status-fields
/// configuration file that controls which extended fields appear in <c>twig status</c>.
/// </summary>
public static class StatusFieldsConfig
{
    /// <summary>
    /// The 9 core fields that are always displayed as dedicated rows in the status view
    /// and must never appear in the status-fields configuration file.
    /// Superset of <c>FieldImportFilter.CoreFieldRefs</c> — adds <c>System.TeamProject</c>.
    /// </summary>
    internal static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.WorkItemType",
        "System.Title", "System.State", "System.AssignedTo",
        "System.IterationPath", "System.AreaPath", "System.TeamProject",
    };

    private static readonly string[] DefaultStarKeywords =
        ["effort", "points", "priority", "severity", "tags"];

    /// <summary>
    /// Curated reference-name sets for known ADO process templates.
    /// Fields listed here are starred by default (in addition to dateTime fields)
    /// when a matching process template is detected on first-time generation.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ProcessTemplateDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Agile"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.VSTS.Common.Priority",
            "Microsoft.VSTS.Scheduling.StoryPoints",
            "Microsoft.VSTS.Common.ValueArea",
            "System.Tags",
        },
        ["Scrum"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.VSTS.Scheduling.Effort",
            "Microsoft.VSTS.Common.BusinessValue",
            "Microsoft.VSTS.Common.BacklogPriority",
            "System.Tags",
        },
        ["CMMI"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.VSTS.Common.Priority",
            "Microsoft.VSTS.Scheduling.Size",
            "Microsoft.VSTS.CMMI.Blocked",
            "System.Tags",
        },
    };

    private const string CommentHeader =
        """
        # twig status-fields configuration
        # Lines starting with '#' are comments and ignored.
        # Prefix a line with '*' to include that field in 'twig status' output.
        # The order of lines determines the display order.
        # To reset, delete this file and run 'twig config status-fields' again.
        #
        # Format: [*] Display Name              (reference.name)           [data_type]
        #
        """;

    /// <summary>
    /// Returns <c>true</c> if the field should appear in the status-fields configuration.
    /// Excludes core fields (9-field set) and fields that <see cref="FieldImportFilter"/> rejects.
    /// </summary>
    public static bool IsImportable(FieldDefinition def)
        => !CoreFields.Contains(def.ReferenceName)
           && FieldImportFilter.ShouldImport(def.ReferenceName, def);

    /// <summary>
    /// Returns <c>true</c> if the field should be starred by default on first-time generation.
    /// Matches fields whose display name contains effort/points/priority/severity/tags
    /// (case-insensitive) or whose data type is <c>dateTime</c>.
    /// </summary>
    public static bool IsDefaultStarred(FieldDefinition def)
    {
        if (string.Equals(def.DataType, "dateTime", StringComparison.OrdinalIgnoreCase))
            return true;

        var displayName = def.DisplayName;
        foreach (var keyword in DefaultStarKeywords)
        {
            if (displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if the field should be starred by default on first-time generation,
    /// using process-template-aware curated lists when <paramref name="processTemplate"/> matches
    /// a known template (Agile, Scrum, CMMI). For unknown or null templates, falls back to the
    /// keyword heuristic.
    /// </summary>
    public static bool IsDefaultStarred(FieldDefinition def, string? processTemplate)
    {
        if (processTemplate is not null
            && ProcessTemplateDefaults.TryGetValue(processTemplate, out var curatedSet))
        {
            if (string.Equals(def.DataType, "dateTime", StringComparison.OrdinalIgnoreCase))
                return true;

            return curatedSet.Contains(def.ReferenceName);
        }

        return IsDefaultStarred(def);
    }

    /// <summary>
    /// Generates the status-fields configuration file content.
    /// When <paramref name="existingContent"/> is <c>null</c>, produces a fresh file with
    /// intelligent defaults. When provided, merges: preserves existing order and selections,
    /// appends new importable fields unmarked, drops removed or no-longer-importable fields.
    /// </summary>
    public static string Generate(IReadOnlyList<FieldDefinition> definitions, string? existingContent = null)
    {
        var importable = new List<FieldDefinition>();
        var importableLookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in definitions)
        {
            if (IsImportable(def))
            {
                importable.Add(def);
                importableLookup[def.ReferenceName] = def;
            }
        }

        if (existingContent is not null)
            return GenerateMerged(importableLookup, existingContent);

        return GenerateFresh(importable);
    }

    /// <summary>
    /// Generates the status-fields configuration file content with optional process-template-aware
    /// smart defaults. When <paramref name="existingContent"/> is <c>null</c> and
    /// <paramref name="processTemplate"/> matches a known template, uses curated reference-name
    /// sets for starring. When <paramref name="existingContent"/> is non-null (merge), the
    /// <paramref name="processTemplate"/> is ignored — user selections always win.
    /// </summary>
    public static string Generate(IReadOnlyList<FieldDefinition> definitions, string? existingContent, string? processTemplate)
    {
        if (existingContent is not null || processTemplate is null)
            return Generate(definitions, existingContent);

        var importable = new List<FieldDefinition>();

        foreach (var def in definitions)
        {
            if (IsImportable(def))
                importable.Add(def);
        }

        return GenerateFresh(importable, processTemplate);
    }

    /// <summary>
    /// Parses the status-fields configuration content into a list of entries.
    /// </summary>
    public static IReadOnlyList<StatusFieldEntry> Parse(string content)
    {
        var entries = new List<StatusFieldEntry>();
        var lines = content.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip blank and comment lines
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
                continue;

            // Extract reference name from (...) using simple string search
            var openParen = line.IndexOf('(');
            if (openParen < 0)
                continue;
            var closeParen = line.IndexOf(')', openParen + 1);
            if (closeParen < 0)
                continue;

            var refName = line.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            if (refName.Length == 0)
                continue;
            var isIncluded = trimmed.StartsWith('*');

            entries.Add(new StatusFieldEntry(refName, isIncluded));
        }

        return entries;
    }

    private static string GenerateFresh(List<FieldDefinition> importable, string? processTemplate = null)
    {
        var starred = new List<FieldDefinition>();
        var unstarred = new List<FieldDefinition>();

        foreach (var def in importable)
        {
            if (IsDefaultStarred(def, processTemplate))
                starred.Add(def);
            else
                unstarred.Add(def);
        }

        starred.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        unstarred.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        var (displayWidth, refWidth) = ComputeColumnWidths(importable);

        var sb = new StringBuilder();
        sb.AppendLine(CommentHeader);

        foreach (var def in starred)
            sb.AppendLine(FormatLine(def, true, displayWidth, refWidth));

        foreach (var def in unstarred)
            sb.AppendLine(FormatLine(def, false, displayWidth, refWidth));

        return sb.ToString();
    }

    private static string GenerateMerged(
        Dictionary<string, FieldDefinition> importableLookup,
        string existingContent)
    {
        var existingEntries = Parse(existingContent);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect entries that are still importable, in their existing order
        var keptEntries = new List<(FieldDefinition Def, bool IsIncluded)>();
        foreach (var entry in existingEntries)
        {
            if (importableLookup.TryGetValue(entry.ReferenceName, out var def))
            {
                keptEntries.Add((def, entry.IsIncluded));
                seen.Add(entry.ReferenceName);
            }
            // else: field removed or no longer importable → drop
        }

        // Append new importable fields not in existing, unmarked, sorted by display name
        var newFields = new List<FieldDefinition>();
        foreach (var kvp in importableLookup)
        {
            if (!seen.Contains(kvp.Key))
                newFields.Add(kvp.Value);
        }
        newFields.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        // Compute padding across all entries
        var allDefs = new List<FieldDefinition>(keptEntries.Count + newFields.Count);
        foreach (var (def, _) in keptEntries)
            allDefs.Add(def);
        allDefs.AddRange(newFields);

        var (displayWidth, refWidth) = ComputeColumnWidths(allDefs);

        var sb = new StringBuilder();
        sb.AppendLine(CommentHeader);

        foreach (var (def, isIncluded) in keptEntries)
            sb.AppendLine(FormatLine(def, isIncluded, displayWidth, refWidth));

        foreach (var def in newFields)
            sb.AppendLine(FormatLine(def, false, displayWidth, refWidth));

        return sb.ToString();
    }

    private static string FormatLine(FieldDefinition def, bool starred, int displayWidth, int refWidth)
    {
        var prefix = starred ? "* " : "  ";
        var paddedDisplay = def.DisplayName.PadRight(displayWidth);
        var paddedRef = $"({def.ReferenceName})".PadRight(refWidth + 2); // +2 for parens
        return $"{prefix}{paddedDisplay}  {paddedRef}  [{def.DataType}]";
    }

    private static (int DisplayWidth, int RefWidth) ComputeColumnWidths(List<FieldDefinition> defs)
    {
        var displayWidth = 0;
        var refWidth = 0;

        foreach (var def in defs)
        {
            if (def.DisplayName.Length > displayWidth)
                displayWidth = def.DisplayName.Length;
            if (def.ReferenceName.Length > refWidth)
                refWidth = def.ReferenceName.Length;
        }

        return (displayWidth, refWidth);
    }
}
