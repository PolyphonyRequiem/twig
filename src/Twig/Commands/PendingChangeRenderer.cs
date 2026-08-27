using Twig.Domain.ValueObjects;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Shared projection of <see cref="PendingChangeDetail"/> rows into
/// <see cref="RenderCell"/>/<see cref="RenderNode"/> shapes. Owned in one place so the
/// plan preview surface (which embeds pending changes under <c>pendingChanges</c>) and the
/// <c>twig pending</c> command emit exactly the same field names and preserve exactly the
/// same raw strings — no coalescing, no relabeling, no timestamp reformatting.
/// </summary>
/// <remarks>
/// Values are copied verbatim: <see cref="PendingChangeDetail.OldValue"/>,
/// <see cref="PendingChangeDetail.NewValue"/>, <see cref="PendingChangeDetail.Note"/> and
/// <see cref="PendingChangeDetail.Field"/> pass through as UTF-16 strings without
/// truncation or normalization. The order returned by the store is preserved.
/// <para>
/// This renderer is deliberately not a telemetry vehicle. Pending values may contain
/// customer field content; commands MUST render them only to their own stdout, never to
/// <see cref="Twig.Domain.Interfaces.ITelemetryClient"/>.
/// </para>
/// </remarks>
internal static class PendingChangeRenderer
{
    /// <summary>Deterministic machine field name for the row array.</summary>
    internal const string PendingChangesKey = "pendingChanges";

    /// <summary>
    /// Wraps <paramref name="rows"/> into a single <see cref="RenderCell"/> whose value is
    /// a JSON-array projection with per-row keyed objects. Order preserved.
    /// </summary>
    internal static RenderCell PendingChangesCell(IReadOnlyList<PendingChangeDetail> rows)
    {
        if (rows.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var items = new List<RenderCell>(rows.Count);
        foreach (var row in rows)
            items.Add(RowCell(row));
        return new RenderCell($"{rows.Count} pending change(s)", new RenderValue.Array(items));
    }

    /// <summary>
    /// Projects one row to a keyed object. The key names are the deterministic surface
    /// consumed by MCP too; do not rename without matching there.
    /// </summary>
    internal static RenderCell RowCell(PendingChangeDetail row)
    {
        var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["pendingChangeId"] = RenderCell.Integer(row.PendingChangeId),
            ["workItemId"] = RenderCell.Integer(row.WorkItemId),
            ["kind"] = RenderCell.String(row.Kind),
            ["field"] = OptionalString(row.Field),
            ["note"] = OptionalString(row.Note),
            ["oldValue"] = OptionalString(row.OldValue),
            ["newValue"] = OptionalString(row.NewValue),
            ["stagedAt"] = new RenderCell(row.StagedAt.ToString("O"), new RenderValue.DateTime(row.StagedAt)),
            ["seedRemap"] = SeedRemapCell(row.SeedRemap),
        };
        var display = $"#{row.WorkItemId} {row.Kind}{(row.Field is null ? string.Empty : ":" + row.Field)}";
        return new RenderCell(display, new RenderValue.Object(obj));
    }

    /// <summary>Human single-line summary of one pending row. Used by the pending command.</summary>
    internal static string HumanLine(PendingChangeDetail row)
    {
        var field = row.Field is null ? string.Empty : $" {row.Field}";
        var newValue = row.NewValue ?? row.Note;
        var value = newValue is null ? string.Empty : $" = {Truncate(newValue)}";
        return $"#{row.WorkItemId} {row.Kind}{field}{value}";
    }

    private static string Truncate(string value)
    {
        const int max = 80;
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static RenderCell OptionalString(string? value)
        => value is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : RenderCell.String(value);

    private static RenderCell SeedRemapCell(SeedRemapIdentity? remap)
    {
        if (remap is null)
            return new RenderCell("(none)", new RenderValue.Null());

        var identity = remap.Value.StagedIdentity.ToString();
        var alias = remap.Value.StagedAlias.Value;
        var published = remap.Value.PublishedWorkItemId;
        var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["stagedIdentity"] = RenderCell.String(identity),
            ["stagedAlias"] = RenderCell.Integer(alias),
            ["publishedWorkItemId"] = published is null
                ? new RenderCell("(none)", new RenderValue.Null())
                : RenderCell.Integer(published.Value),
        };
        var display = published is null ? $"alias={alias}" : $"alias={alias}, published=#{published}";
        return new RenderCell(display, new RenderValue.Object(obj));
    }
}
