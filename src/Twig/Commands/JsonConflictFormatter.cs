using System.Text;
using Twig.Domain.Services.Sync;

namespace Twig.Commands;

/// <summary>
/// Shared helper for formatting conflict data as JSON.
/// Extracted from StateCommand and UpdateCommand to eliminate duplication (I-004).
/// </summary>
internal static class JsonConflictFormatter
{
    /// <summary>
    /// Formats a list of field conflicts as a JSON string: {"conflicts":[...]}.
    /// </summary>
    internal static string FormatConflictsAsJson(IReadOnlyList<FieldConflict> conflicts)
    {
        var sb = new StringBuilder();
        sb.Append("{\"conflicts\":[");
        for (var i = 0; i < conflicts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var c = conflicts[i];
            sb.Append($"{{\"field\":\"{EscapeJson(c.FieldName)}\",\"local\":\"{EscapeJson(c.LocalValue)}\",\"remote\":\"{EscapeJson(c.RemoteValue)}\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a string value for safe embedding in a JSON string literal.
    /// Handles all control characters per RFC 8259 §7.
    /// </summary>
    internal static string EscapeJson(string? value)
    {
        var s = value ?? "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                char ch when ch < 0x20 => $"\\u{(int)ch:X4}",
                _ => c.ToString()
            });
        }
        return sb.ToString();
    }
}