using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Extensions;

/// <summary>
/// Extension methods for converting <see cref="WorkItem"/> aggregates
/// to write-path DTOs.
/// </summary>
public static class WorkItemExtensions
{
    /// <summary>
    /// Converts a <see cref="WorkItem"/> (typically a seed) to a
    /// <see cref="CreateWorkItemRequest"/> for the <c>CreateAsync</c> write path.
    /// Extracts only the properties needed for work item creation.
    /// </summary>
    public static CreateWorkItemRequest ToCreateRequest(this WorkItem seed)
        => new()
        {
            TypeName = seed.Type.Value,
            Title = seed.Title,
            AreaPath = seed.AreaPath.Value,
            IterationPath = seed.IterationPath.Value,
            ParentId = seed.ParentId,
            Fields = new Dictionary<string, string?>(seed.Fields, StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>
    /// Reads <c>System.CommentCount</c> off a work item, degrading to 0 (AB#618).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by every machine-readable surface that reports a comment count — the CLI's
    /// <c>show</c> / <c>show-batch</c> document and MCP's <c>FormatWorkItem</c>. It lives here
    /// rather than being repeated per surface so the three cannot answer the same question
    /// differently: two surfaces disagreeing on whether a note landed is a worse failure than
    /// neither reporting it, because it looks like an answer.
    /// </para>
    /// <para>
    /// Zero is returned for BOTH "the field is absent" and "the field is unparseable", and
    /// that is deliberate. ADO omits <c>System.CommentCount</c> entirely on an item that has
    /// never been commented on, so absence is the common shape and 0 is its true answer. A
    /// malformed or negative value is not a comment count either, and propagating one would
    /// put a value on the wire that no consumer can act on.
    /// </para>
    /// <para>
    /// 🔴 Callers must emit the result UNCONDITIONALLY. Suppressing the key when this returns
    /// 0 recreates the exact defect AB#618 fixes: a consumer cannot distinguish "no comments"
    /// from "this surface does not report comments", so a <c>twig note</c> write stays
    /// unverifiable through twig.
    /// </para>
    /// </remarks>
    public static int ReadCommentCount(this WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.Fields.TryGetValue("System.CommentCount", out var raw))
            return 0;

        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : 0;
    }
}
