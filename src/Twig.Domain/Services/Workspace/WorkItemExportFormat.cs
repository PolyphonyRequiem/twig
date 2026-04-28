using System.Text;
using System.Text.RegularExpressions;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Generates and parses a markdown-based multi-item export format for published work items.
/// This is a pure computation service — no I/O, no ADO calls, AOT-compatible.
/// </summary>
/// <remarks>
/// <para>
/// The generated format uses HTML comments for machine-readable item metadata and
/// H2 (<c>##</c>) headers for field sections. This differentiates from
/// <see cref="SeedEditorFormat"/> which uses H1 (<c>#</c>) headers for seeds.
/// </para>
/// <para>
/// <c>System.State</c> is excluded from exported fields — state changes must go through
/// the validated <c>twig state</c> workflow.
/// </para>
/// </remarks>
public static class WorkItemExportFormat
{
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id",
        "System.Rev",
        "System.State",
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

    // Matches: <!-- item: id=123 rev=4 type=Task -->
    private static readonly Regex MetadataPattern = new(
        @"<!--\s*item:\s*id=(\d+)\s+rev=(\d+)\s+type=([^-]+?)\s*-->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Generates a markdown export document from one or more work items.
    /// Items are separated by <c>---</c> horizontal rules.
    /// </summary>
    /// <param name="items">Work items to export.</param>
    /// <param name="fieldDefinitions">Field metadata used to resolve display names and filter read-only fields.</param>
    /// <returns>Markdown string representing all exported items.</returns>
    public static string Generate(
        IEnumerable<WorkItem> items,
        IReadOnlyList<FieldDefinition> fieldDefinitions)
    {
        var writableFields = GetOrderedWritableFields(fieldDefinitions);
        var sb = new StringBuilder();
        sb.AppendLine("<!-- twig-export: edit field values below, then run 'twig import' -->");
        sb.AppendLine();

        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
            first = false;

            // Machine-readable metadata comment
            sb.AppendLine($"<!-- item: id={item.Id} rev={item.Revision} type={item.Type.Value} -->");
            sb.AppendLine();

            // Human-readable heading (not parsed on import)
            sb.AppendLine($"## {item.Id} \u2014 {item.Title}");
            sb.AppendLine();

            foreach (var field in writableFields)
            {
                sb.AppendLine($"## {field.DisplayName}");
                var value = string.Equals(field.ReferenceName, "System.Title", StringComparison.OrdinalIgnoreCase)
                    ? item.Title
                    : item.Fields.TryGetValue(field.ReferenceName, out var v) ? v : null;
                if (!string.IsNullOrEmpty(value))
                    sb.AppendLine(value);
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    /// <summary>
    /// Parses a markdown export document back into <see cref="ExportedWorkItem"/> records.
    /// Unrecognized field headers are silently ignored (forward-compatible).
    /// </summary>
    /// <param name="content">Markdown content previously produced by <see cref="Generate"/>.</param>
    /// <param name="fieldDefinitions">Field metadata used to map display names to reference names.</param>
    /// <returns>Parsed work items, in document order.</returns>
    public static IReadOnlyList<ExportedWorkItem> Parse(
        string content,
        IReadOnlyList<FieldDefinition> fieldDefinitions)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        // Build display name → reference name lookup
        var displayToRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fd in fieldDefinitions)
            displayToRef.TryAdd(fd.DisplayName, fd.ReferenceName);

        var results = new List<ExportedWorkItem>();

        // Current item state
        int? currentId = null;
        int currentRev = 0;
        string currentType = string.Empty;
        var currentFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Current field accumulation state
        string? currentRefName = null;
        var currentValue = new StringBuilder();
        bool inItemHeading = false; // skip the "## ID — Title" heading line

        var lines = content.Split('\n');

        void FlushField()
        {
            if (currentRefName is not null)
            {
                var val = currentValue.ToString().Trim();
                currentFields[currentRefName] = val.Length == 0 ? null : val;
            }
            currentRefName = null;
            currentValue.Clear();
        }

        void FlushItem()
        {
            FlushField();
            if (currentId.HasValue)
            {
                results.Add(new ExportedWorkItem(
                    currentId.Value,
                    currentRev,
                    currentType,
                    new Dictionary<string, string?>(currentFields, StringComparer.OrdinalIgnoreCase)));
            }
            currentId = null;
            currentRev = 0;
            currentType = string.Empty;
            currentFields.Clear();
            inItemHeading = false;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Check for item metadata comment
            var metaMatch = MetadataPattern.Match(line);
            if (metaMatch.Success)
            {
                // Start a new item — flush previous if any
                FlushItem();

                currentId = int.Parse(metaMatch.Groups[1].Value);
                currentRev = int.Parse(metaMatch.Groups[2].Value);
                currentType = metaMatch.Groups[3].Value.Trim();
                inItemHeading = true; // next ## heading is the human-readable title, skip it
                continue;
            }

            // Skip non-item lines before the first metadata comment
            if (currentId is null)
                continue;

            // Skip the file-level comment
            if (line.StartsWith("<!-- "))
                continue;

            // Item separator
            if (line == "---")
            {
                FlushField();
                continue;
            }

            // H2 field header
            if (line.StartsWith("## "))
            {
                if (inItemHeading)
                {
                    // This is the "## ID — Title" heading — not a field
                    inItemHeading = false;
                    continue;
                }

                FlushField();

                var displayName = line[3..].Trim();
                currentRefName = displayToRef.TryGetValue(displayName, out var refName)
                    ? refName
                    : null; // unrecognized — silently ignore
                continue;
            }

            // Accumulate value lines
            if (currentRefName is not null)
            {
                if (currentValue.Length > 0)
                    currentValue.AppendLine();
                currentValue.Append(line);
            }
        }

        // Flush last item
        FlushItem();

        return results;
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
}
