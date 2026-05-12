using Twig.Domain.Interfaces;

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
///   <item><c>null</c> — auto-detect via field DataType (auto entry points only).</item>
/// </list>
/// </remarks>
public static class HtmlFieldFormatter
{
    public const string MarkdownFormat = "markdown";
    public const string RawFormat = "raw";

    /// <summary>Result of a format resolution.</summary>
    /// <param name="EffectiveValue">Value to send to ADO.</param>
    /// <param name="IsHtml">
    /// True when the resulting field will hold HTML markup — either because
    /// conversion happened or because the destination is an HTML-typed field
    /// or comment. Drives <see cref="FieldAppender.Append"/> wrapping.
    /// </param>
    public readonly record struct FormatResult(string EffectiveValue, bool IsHtml);

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
    /// Auto-detect resolution for arbitrary fields. Used by <c>twig new</c>,
    /// <c>twig patch</c>, <c>twig update</c>, <c>twig_update</c>, <c>twig_patch</c>.
    /// </summary>
    /// <param name="onMissingFieldDef">
    /// Optional callback invoked once when format is auto and the field
    /// definition cannot be found. Used by CLI to surface a stderr warning;
    /// MCP passes <see langword="null"/>.
    /// </param>
    public static async Task<FormatResult> ResolveAsync(
        string fieldRefName,
        string value,
        string? format,
        IFieldDefinitionStore fieldDefStore,
        Action<string>? onMissingFieldDef = null,
        CancellationToken ct = default)
    {
        if (string.Equals(format, MarkdownFormat, StringComparison.OrdinalIgnoreCase))
            return new FormatResult(MarkdownConverter.ToHtml(value), IsHtml: true);

        var fieldDef = await fieldDefStore.GetByReferenceNameAsync(fieldRefName, ct);
        var fieldIsHtml = fieldDef is not null
            && string.Equals(fieldDef.DataType, "html", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase))
            return new FormatResult(value, IsHtml: fieldIsHtml);

        // format == null → auto
        if (fieldDef is null)
            onMissingFieldDef?.Invoke(fieldRefName);

        if (fieldIsHtml)
            return new FormatResult(MarkdownConverter.ToHtml(value), IsHtml: true);

        return new FormatResult(value, IsHtml: false);
    }

    /// <summary>
    /// Resolves a value for a surface whose existing contract is "Markdown by
    /// default" (e.g. <c>twig_new</c>, <c>twig_seed_new</c>, <c>twig_seed_edit</c>
    /// description parameters, and CLI <c>twig new --description</c> targeting
    /// <c>System.Description</c>). Does not consult the field-definition store,
    /// so it is robust against a stale or missing cache.
    /// </summary>
    public static FormatResult ResolveForcedMarkdownDefault(string value, string? format)
    {
        if (string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase))
            return new FormatResult(value, IsHtml: true);

        // markdown or null → convert
        return new FormatResult(MarkdownConverter.ToHtml(value), IsHtml: true);
    }

    /// <summary>
    /// Resolves a comment body for <c>twig note</c> / <c>twig_note</c>. ADO
    /// comments accept HTML; default to Markdown→HTML conversion so the
    /// rendered comment matches operator intent.
    /// </summary>
    public static FormatResult ResolveComment(string text, string? format)
    {
        if (string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase))
            return new FormatResult(text, IsHtml: true);

        return new FormatResult(MarkdownConverter.ToHtml(text), IsHtml: true);
    }
}
