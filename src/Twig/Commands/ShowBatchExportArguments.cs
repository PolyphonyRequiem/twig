using System.Globalization;

namespace Twig.Commands;

/// <summary>
/// Validates the opt-in target export before startup can open a repository.
/// The ordinary show-batch parser and its legacy ID handling are unchanged.
/// </summary>
internal static class ShowBatchExportArguments
{
    public static string? Validate(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "show-batch"
            || !args.Any(arg => arg == "--include-fields" || arg.StartsWith("--include-fields=", StringComparison.Ordinal)))
            return null;

        string? batch = null, positional = null;
        var output = "human";
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--include-fields")
                continue;
            if (arg is "--batch" or "-o" or "--output")
            {
                if (++i == args.Count)
                    return $"error: {arg} requires a value.";
                if (arg == "--batch")
                    batch = args[i];
                else
                {
                    output = args[i];
                    if (!string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
                        return "error: --include-fields requires -o json.";
                }
                continue;
            }
            if (arg is "--fields" or "--sections" or "--refresh")
                return $"error: --include-fields cannot be combined with {arg}.";
            // A negative seed alias is an operand, not an option.
            if (positional is null && TryParseIds(arg, out _))
            {
                positional = arg;
                continue;
            }
            return $"error: Unsupported argument '{arg}' for --include-fields.";
        }

        if (!string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
            return "error: --include-fields requires -o json.";
        return TryParseIds(batch ?? positional, out _)
            ? null
            : "error: --include-fields requires a non-empty comma-separated list of distinct nonzero work item IDs.";
    }

    public static bool TryParseIds(string? batch, out List<int> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(batch))
            return false;
        var seen = new HashSet<int>();
        foreach (var segment in batch.Split(','))
        {
            if (!int.TryParse(segment.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
                || id == 0 || !seen.Add(id))
                return false;
            ids.Add(id);
        }
        return true;
    }
}
