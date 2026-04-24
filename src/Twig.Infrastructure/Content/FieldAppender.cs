namespace Twig.Infrastructure.Content;

/// <summary>
/// HTML-aware helper for appending values to ADO work-item fields.
/// </summary>
internal static class FieldAppender
{
    /// <summary>
    /// Appends <paramref name="newValue"/> to <paramref name="existingValue"/>
    /// with an appropriate separator. Returns <paramref name="newValue"/> unchanged
    /// when <paramref name="existingValue"/> is null or whitespace-only.
    /// </summary>
    public static string Append(string? existingValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(existingValue))
            return newValue;

        var separator = LooksLikeHtml(existingValue) ? "<br><br>" : "\n\n";
        return string.Concat(existingValue, separator, newValue);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> appears to contain
    /// HTML markup (i.e. contains both '&lt;' and '&gt;' characters).
    /// </summary>
    public static bool LooksLikeHtml(string value)
        => value.Contains('<') && value.Contains('>');
}
