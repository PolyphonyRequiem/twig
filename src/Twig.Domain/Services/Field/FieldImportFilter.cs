using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Field;

/// <summary>
/// Determines whether a given ADO field should be imported into <c>WorkItem.Fields</c>.
/// Excludes core fields already mapped to first-class properties, filters by data type,
/// and allows display-worthy read-only fields through.
/// </summary>
public static class FieldImportFilter
{
    private static readonly HashSet<string> CoreFieldRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.WorkItemType",
        "System.Title", "System.State", "System.AssignedTo",
        "System.IterationPath", "System.AreaPath",
    };

    // Boolean is intentionally excluded: ADO returns booleans as JSON true/false
    // which our string-only Fields dictionary cannot represent faithfully.
    private static readonly HashSet<string> ImportableDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "integer", "double", "dateTime", "html", "plainText",
    };

    private static readonly HashSet<string> DisplayWorthyReadOnlyRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.CreatedDate", "System.ChangedDate", "System.CreatedBy",
        "System.ChangedBy", "System.Tags", "System.Description",
        "System.BoardColumn", "System.BoardColumnDone",
    };

    /// <summary>
    /// Returns <c>true</c> if the field identified by <paramref name="refName"/> should be
    /// imported into <c>WorkItem.Fields</c>. When <paramref name="fieldDef"/> is <c>null</c>
    /// (no metadata available), all non-core fields are imported as a fallback.
    /// </summary>
    public static bool ShouldImport(string refName, FieldDefinition? fieldDef)
    {
        if (CoreFieldRefs.Contains(refName)) return false;
        if (fieldDef is null) return true; // fallback: import everything non-core
        if (DisplayWorthyReadOnlyRefs.Contains(refName)) return true;
        if (fieldDef.IsReadOnly) return false;
        return ImportableDataTypes.Contains(fieldDef.DataType);
    }
}
