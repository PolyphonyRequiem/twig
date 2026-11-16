namespace Twig.Infrastructure.Content;

/// <summary>
/// Resolves operator-supplied values for ADO work-item fields by deciding
/// whether to convert Markdown → HTML before sending to ADO.
/// </summary>
/// <remarks>
/// <para>
/// ADO models some fields as HTML (<c>System.Description</c>, <c>System.History</c>,
/// <c>Microsoft.VSTS.TCM.ReproSteps</c>, <c>Microsoft.VSTS.Common.AcceptanceCriteria</c>,
/// etc.). When operators or agents author those fields they are almost always
/// writing Markdown, not raw HTML. This helper centralizes the policy.
/// </para>
/// <para>The <c>format</c> string accepts:</para>
/// <list type="bullet">
///   <item><c>"markdown"</c> — always convert.</item>
///   <item><c>"raw"</c> — never convert; pass through unchanged.</item>
///   <item><c>null</c> — convert Markdown to HTML.</item>
/// </list>
/// </remarks>
public static class HtmlFieldFormatter
{
    public const string MarkdownFormat = "markdown";
    public const string RawFormat = "raw";

    /// <summary>Result of a format resolution.</summary>
    /// <param name="EffectiveValue">Value to send to ADO.</param>
    public readonly record struct FormatResult(string EffectiveValue);

    /// <summary>
    /// Validates a format option string. Returns <see langword="null"/> when
    /// valid, otherwise an error message suitable for stderr.
    /// </summary>
    public static string? ValidateFormat(string? format)
    {
        if (format is null) return null;
        if (string.Equals(format, MarkdownFormat, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Unknown format '{format}'. Supported formats: markdown, raw";
    }

    /// <summary>
    /// Resolves a comment body for <c>twig note</c>. ADO
    /// comments accept HTML; default to Markdown→HTML conversion so the
    /// rendered comment matches operator intent.
    /// </summary>
    public static FormatResult ResolveComment(string text, string? format)
    {
        if (string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase))
            return new FormatResult(text);

        return new FormatResult(MarkdownConverter.ToHtml(text));
    }
}
