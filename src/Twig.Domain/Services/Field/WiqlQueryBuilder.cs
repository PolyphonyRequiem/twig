using System.Text;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Field;

/// <summary>
/// Builds WIQL SELECT statements from a <see cref="QueryParameters"/> instance.
/// All clauses are AND-joined; when no filters are specified the builder still
/// produces a valid query with a default ORDER BY.
/// </summary>
internal static class WiqlQueryBuilder
{
    private const string SelectPrefix = "SELECT [System.Id] FROM WorkItems";
    private const string OrderBySuffix = " ORDER BY [System.ChangedDate] DESC";

    /// <summary>
    /// Constructs a WIQL query string from the supplied <paramref name="parameters"/>.
    /// </summary>
    public static string Build(QueryParameters parameters)
    {
        var clauses = new List<string>();

        AppendSearchText(clauses, parameters.SearchText);
        AppendContainsClause(clauses, "System.Title", parameters.TitleFilter);
        AppendContainsClause(clauses, "System.Description", parameters.DescriptionFilter);
        AppendEqualsClause(clauses, "System.WorkItemType", parameters.TypeFilter);
        AppendEqualsClause(clauses, "System.State", parameters.StateFilter);
        AppendEqualsClause(clauses, "System.AssignedTo", parameters.AssignedToFilter);
        AppendUnderClause(clauses, "System.AreaPath", parameters.AreaPathFilter);
        AppendUnderClause(clauses, "System.IterationPath", parameters.IterationPathFilter);
        AppendDefaultAreaPaths(clauses, parameters.DefaultAreaPaths);
        AppendDateClause(clauses, "System.CreatedDate", parameters.CreatedSinceDays);
        AppendDateClause(clauses, "System.ChangedDate", parameters.ChangedSinceDays);

        var sb = new StringBuilder(SelectPrefix);

        if (clauses.Count > 0)
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", clauses));
        }

        sb.Append(OrderBySuffix);

        return sb.ToString();
    }

    private static void AppendSearchText(List<string> clauses, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return;

        var escaped = EscapeWiqlString(searchText);
        clauses.Add($"([System.Title] CONTAINS '{escaped}' OR [System.Description] CONTAINS '{escaped}')");
    }

    private static void AppendContainsClause(List<string> clauses, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        clauses.Add($"[{fieldName}] CONTAINS '{EscapeWiqlString(value)}'");
    }

    private static void AppendEqualsClause(List<string> clauses, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        clauses.Add($"[{fieldName}] = '{EscapeWiqlString(value)}'");
    }

    private static void AppendUnderClause(List<string> clauses, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        clauses.Add($"[{fieldName}] UNDER '{EscapeWiqlString(value)}'");
    }

    private static void AppendDefaultAreaPaths(
        List<string> clauses,
        IReadOnlyList<(string Path, bool IncludeChildren)>? defaultAreaPaths)
    {
        if (defaultAreaPaths is null || defaultAreaPaths.Count == 0)
            return;

        var parts = new List<string>(defaultAreaPaths.Count);
        foreach (var (path, includeChildren) in defaultAreaPaths)
            parts.Add($"[System.AreaPath] {(includeChildren ? "UNDER" : "=")} '{EscapeWiqlString(path)}'");

        clauses.Add(parts.Count == 1 ? parts[0] : $"({string.Join(" OR ", parts)})");
    }

    private static void AppendDateClause(List<string> clauses, string fieldName, int? days)
    {
        if (days is null)
            return;

        clauses.Add($"[{fieldName}] >= @Today - {days.Value}");
    }

    private static string EscapeWiqlString(string value)
    {
        return value.Replace("'", "''");
    }
}
