using System.Globalization;

namespace Twig.Commands.SetTree;

/// <summary>
/// Parses the <c>--items</c> working-set argument: either an inline comma-separated
/// list of ids (<c>--items 1,2,3</c>) or an <c>@file</c> reference whose contents are
/// one id per line (twig#277).
/// </summary>
/// <remarks>
/// Every parse failure is reported rather than skipped. A working-set render is a
/// consent surface, so an id the caller asked for that silently vanishes from the
/// output is the worst available failure mode.
/// </remarks>
internal static class WorkingSetIdParser
{
    internal sealed record Result(IReadOnlyList<int> Ids, string? Error)
    {
        internal bool Ok => Error is null;
    }

    /// <summary>
    /// Parses <paramref name="spec"/> into a de-duplicated, order-preserving id list.
    /// </summary>
    /// <param name="spec">
    /// <c>"1,2,3"</c>, or <c>"@path/to/ids.txt"</c>, or <c>"@-"</c> to read stdin.
    /// </param>
    /// <param name="readFile">
    /// File reader seam, so tests need no disk. Receives the path with the leading
    /// <c>@</c> already stripped.
    /// </param>
    /// <param name="readStdin">Stdin reader seam for the <c>@-</c> form.</param>
    internal static Result Parse(
        string spec,
        Func<string, string>? readFile = null,
        Func<string>? readStdin = null)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return new Result([], "--items requires at least one work item id.");

        string text;
        var trimmed = spec.Trim();
        if (trimmed.StartsWith('@'))
        {
            var path = trimmed[1..];
            if (path is "-")
            {
                try
                {
                    text = (readStdin ?? Console.In.ReadToEnd)();
                }
                catch (IOException ex)
                {
                    return new Result([], $"Could not read work item ids from stdin: {ex.Message}");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new Result([], "--items @<file> requires a file path.");

                try
                {
                    text = (readFile ?? File.ReadAllText)(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return new Result([], $"Could not read work item ids from '{path}': {ex.Message}");
                }
            }
        }
        else
        {
            text = trimmed;
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();

        // Accept commas, newlines, and whitespace interchangeably so the inline
        // and @file forms parse identically.
        var tokens = text.Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            var token = raw.Trim().TrimStart('#');
            if (token.Length == 0)
                continue;

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return new Result([], $"'{raw.Trim()}' is not a valid work item id.");

            if (seen.Add(id))
                ids.Add(id);
        }

        return ids.Count == 0
            ? new Result([], "--items requires at least one work item id.")
            : new Result(ids, null);
    }
}
