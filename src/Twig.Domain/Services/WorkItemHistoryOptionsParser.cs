using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Parses caller-supplied history options from their wire form. Shared by the CLI
/// (<c>--detail</c>/<c>--field</c>) and the MCP tool so both surfaces accept the same syntax
/// and reject the same input (twig#241).
/// </summary>
public static class WorkItemHistoryOptionsParser
{
    /// <summary>Sentinel accepted by <c>--detail</c> to request full detail for every event.</summary>
    public const string DetailAllToken = "all";

    /// <summary>
    /// Parses a comma-delimited <c>--detail</c> value (update IDs or <c>all</c>) and a
    /// comma-delimited <c>--field</c> value (ADO reference names).
    /// </summary>
    public static Result<WorkItemHistoryOptions> Parse(string? detail, string? fields)
    {
        var detailAll = false;
        HashSet<int>? detailIds = null;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var tokens = Split(detail);
            if (tokens.Count == 0)
                return Result<WorkItemHistoryOptions>.Fail("--detail requires update IDs or 'all'.");

            foreach (var token in tokens)
            {
                if (string.Equals(token, DetailAllToken, StringComparison.OrdinalIgnoreCase))
                {
                    detailAll = true;
                    continue;
                }

                if (!int.TryParse(token, out var updateId) || updateId <= 0)
                {
                    return Result<WorkItemHistoryOptions>.Fail(
                        $"Invalid --detail value '{token}'. Expected update IDs (e.g. 8,11,14) or 'all'.");
                }

                (detailIds ??= []).Add(updateId);
            }
        }

        HashSet<string>? fieldFilter = null;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            var tokens = Split(fields);
            if (tokens.Count == 0)
                return Result<WorkItemHistoryOptions>.Fail("--field requires at least one field reference name.");

            fieldFilter = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        }

        return Result<WorkItemHistoryOptions>.Ok(new WorkItemHistoryOptions(
            DetailAll: detailAll,
            DetailUpdateIds: detailIds,
            Fields: fieldFilter));
    }

    private static List<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
}
