using System.Text.RegularExpressions;

namespace Twig.Infrastructure.Content;

/// <summary>
/// HTML-aware helper for appending values to ADO work-item fields.
/// </summary>
internal static partial class FieldAppender
{
    /// <summary>
    /// Appends <paramref name="newValue"/> to <paramref name="existingValue"/>
    /// with an appropriate separator. Returns <paramref name="newValue"/> unchanged
    /// when <paramref name="existingValue"/> is null or whitespace-only.
    /// When <paramref name="asHtml"/> is true, forces HTML-mode output regardless
    /// of whether the existing content contains HTML.
    /// </summary>
    public static string Append(string? existingValue, string newValue, bool asHtml = false)
    {
        if (string.IsNullOrWhiteSpace(existingValue))
            return newValue;

        bool htmlMode = asHtml || LooksLikeHtml(existingValue);

        if (htmlMode)
        {
            string wrappedNew = LooksLikeHtml(newValue) ? newValue : $"<p>{newValue}</p>";
            return string.Concat(existingValue, wrappedNew);
        }

        return string.Concat(existingValue, "\n\n", newValue);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> appears to contain
    /// HTML markup by checking for common HTML tags.
    /// </summary>
    public static bool LooksLikeHtml(string value)
        => HtmlTagPattern().IsMatch(value);

    [GeneratedRegex(@"<\/?(p|div|br|ul|ol|li|h[1-6]|span|em|strong|table|tr|td|th|a|img)\b[^>]*\/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagPattern();
}
