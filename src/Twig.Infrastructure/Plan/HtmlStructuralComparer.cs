using System.Net;
using System.Text;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Compares well-formed HTML fragments by their structural tokens without discarding text.
/// ADO may normalize serialization and trailing ASCII whitespace at supported block edges.
/// Inline word boundaries, preformatted content, and unknown rendering contexts stay strict.
/// </summary>
internal static class HtmlStructuralComparer
{
    public static bool AreEquivalent(string expected, string actual)
        => TryCanonicalize(expected, out var expectedCanonical, out var expectedBlockEdges)
            && TryCanonicalize(actual, out var actualCanonical, out var actualBlockEdges)
            && (string.Equals(expectedCanonical, actualCanonical, StringComparison.Ordinal)
                || (expectedBlockEdges is not null && actualBlockEdges is not null
                    && string.Equals(expectedBlockEdges, actualBlockEdges, StringComparison.Ordinal)));

    private static bool TryCanonicalize(string source, out string canonical, out string? blockEdges)
    {
        canonical = string.Empty;
        blockEdges = null;
        var builder = new StringBuilder(source.Length);
        StringBuilder? normalized = null;
        var openTags = new Stack<string>();
        var supportsNormalization = true;
        var anchorDepth = 0;
        var index = 0;

        while (index < source.Length)
        {
            var tagStart = source.IndexOf('<', index);
            if (tagStart < 0)
            {
                AppendText(builder, ref normalized, source.AsSpan(index), trimBlockEdge: false);
                break;
            }

            // Supported nesting guarantees a block cannot have an inline/preformatted
            // ancestor. Never trim before an inline closing tag: that can join words.
            var trimBlockEdge = supportsNormalization && openTags.Count > 0
                && IsBlockElement(openTags.Peek())
                && source.AsSpan(tagStart).StartsWith("</", StringComparison.Ordinal);
            AppendText(builder, ref normalized, source.AsSpan(index, tagStart - index), trimBlockEdge);
            index = tagStart;
            var tokenStart = builder.Length;
            if (!TryAppendTag(source, ref index, builder, openTags, ref supportsNormalization, ref anchorDepth))
                return false;
            if (!supportsNormalization)
                normalized = null;
            else
                normalized?.Append(builder, tokenStart, builder.Length - tokenStart);
        }

        if (openTags.Count != 0)
            return false;

        canonical = builder.ToString();
        if (supportsNormalization)
            blockEdges = normalized?.ToString() ?? canonical;
        return true;
    }

    private static bool TryAppendTag(
        string source,
        ref int index,
        StringBuilder builder,
        Stack<string> openTags,
        ref bool supportsNormalization,
        ref int anchorDepth)
    {
        var length = source.Length;
        var cursor = index + 1;
        if (cursor >= length)
            return false;

        if (source.AsSpan(cursor).StartsWith("!--", StringComparison.Ordinal))
        {
            var end = source.IndexOf("-->", cursor + 3, StringComparison.Ordinal);
            if (end < 0)
                return false;

            builder.Append('C');
            AppendToken(builder, source.Substring(cursor + 3, end - cursor - 3));
            index = end + 3;
            return true;
        }

        if (source[cursor] == '/')
        {
            cursor++;
            if (!TryReadName(source, ref cursor, out var name))
                return false;
            SkipWhitespace(source, ref cursor, ref supportsNormalization);
            if (cursor >= length || source[cursor] != '>' || openTags.Count == 0
                || !string.Equals(openTags.Peek(), name, StringComparison.Ordinal))
                return false;

            openTags.Pop();
            if (name == "a")
                anchorDepth--;
            builder.Append('E');
            AppendToken(builder, name);
            index = cursor + 1;
            return true;
        }

        if (source[cursor] == '!')
        {
            supportsNormalization = false;
            var end = source.IndexOf('>', cursor + 1);
            if (end < 0)
                return false;

            builder.Append('D');
            AppendToken(builder, source.Substring(cursor + 1, end - cursor - 1).Trim().ToLowerInvariant());
            index = end + 1;
            return true;
        }

        if (!TryReadName(source, ref cursor, out var tagName))
            return false;

        // Unknown rendering contexts retain the existing exact text comparison.
        supportsNormalization &= SupportsBlockEdgeNormalization(
            tagName, openTags.Count == 0 ? null : openTags.Peek(), anchorDepth);

        var attributes = new List<(string Name, string? Value)>();
        var selfClosing = false;
        while (true)
        {
            SkipWhitespace(source, ref cursor, ref supportsNormalization);
            if (cursor >= length)
                return false;
            if (source[cursor] == '>')
            {
                cursor++;
                break;
            }
            if (source[cursor] == '/')
            {
                cursor++;
                SkipWhitespace(source, ref cursor, ref supportsNormalization);
                if (cursor >= length || source[cursor] != '>')
                    return false;
                selfClosing = true;
                cursor++;
                break;
            }

            if (!TryReadName(source, ref cursor, out var attributeName))
                return false;

            SkipWhitespace(source, ref cursor, ref supportsNormalization);
            string? attributeValue = null;
            if (cursor < length && source[cursor] == '=')
            {
                cursor++;
                SkipWhitespace(source, ref cursor, ref supportsNormalization);
                if (!TryReadAttributeValue(source, ref cursor, out attributeValue))
                    return false;
                attributeValue = NormalizeText(attributeValue);
            }

            attributes.Add((attributeName, attributeValue));
            if (attributeName is "style" or "class")
                supportsNormalization = false;
        }

        if (selfClosing && !IsVoidElement(tagName))
            supportsNormalization = false;

        attributes.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return byName != 0
                ? byName
                : string.Compare(left.Value, right.Value, StringComparison.Ordinal);
        });
        for (var i = 1; i < attributes.Count; i++)
            if (attributes[i - 1].Name == attributes[i].Name)
                supportsNormalization = false;

        builder.Append('S');
        AppendToken(builder, tagName);
        builder.Append(attributes.Count).Append(';');
        foreach (var attribute in attributes)
        {
            AppendToken(builder, attribute.Name);
            AppendToken(builder, attribute.Value);
        }

        if (!selfClosing && !IsVoidElement(tagName))
        {
            openTags.Push(tagName);
            if (tagName == "a")
                anchorDepth++;
        }

        index = cursor;
        return true;
    }

    private static bool TryReadName(string source, ref int cursor, out string name)
    {
        var start = cursor;
        while (cursor < source.Length && IsNameCharacter(source[cursor]))
            cursor++;

        if (start == cursor)
        {
            name = string.Empty;
            return false;
        }

        name = source.Substring(start, cursor - start).ToLowerInvariant();
        return true;
    }

    private static bool TryReadAttributeValue(string source, ref int cursor, out string value)
    {
        if (cursor >= source.Length)
        {
            value = string.Empty;
            return false;
        }

        var quote = source[cursor];
        if (quote is '\'' or '"')
        {
            var start = ++cursor;
            while (cursor < source.Length && source[cursor] != quote)
                cursor++;
            if (cursor >= source.Length)
            {
                value = string.Empty;
                return false;
            }

            value = source.Substring(start, cursor - start);
            cursor++;
            return true;
        }

        var unquotedStart = cursor;
        while (cursor < source.Length && !char.IsWhiteSpace(source[cursor]) && source[cursor] != '>')
            cursor++;
        if (unquotedStart == cursor)
        {
            value = string.Empty;
            return false;
        }

        value = source.Substring(unquotedStart, cursor - unquotedStart);
        return true;
    }

    private static void AppendText(
        StringBuilder builder, ref StringBuilder? normalized, ReadOnlySpan<char> text, bool trimBlockEdge)
    {
        if (text.Length == 0)
            return;

        var value = NormalizeText(text.ToString());
        var trimmed = trimBlockEdge ? value.AsSpan().TrimEnd(" \t\r\n\f") : value.AsSpan();
        // Allocate a second token stream only at the first changed edge; tags are parsed
        // and attributes sorted once, with subsequent tokens shared between streams.
        if (trimmed.Length != value.Length)
            normalized ??= new StringBuilder(builder.Capacity).Append(builder);
        if (normalized is not null && trimmed.Length > 0)
            normalized.Append('T').Append(trimmed.Length).Append(':').Append(trimmed).Append(';');
        builder.Append('T');
        AppendToken(builder, value);
    }

    private static void AppendToken(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-;");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static string NormalizeText(string value)
        => WebUtility.HtmlDecode(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

    private static void SkipWhitespace(string source, ref int cursor, ref bool supportsNormalization)
    {
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
        {
            // Preserve the old structural parser, but do not broaden equality when it
            // encounters Unicode whitespace that HTML does not recognize as tag syntax.
            if (source[cursor] is not (' ' or '\t' or '\r' or '\n' or '\f'))
                supportsNormalization = false;
            cursor++;
        }
    }

    private static bool IsNameCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is ':' or '-' or '_';

    private static bool IsBlockElement(string name)
        => name is "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "blockquote" or "ul" or "ol" or "li";

    private static bool SupportsBlockEdgeNormalization(string name, string? parent, int anchorDepth)
    {
        if (!IsBlockElement(name)
            && name is not ("a" or "b" or "strong" or "em" or "i" or "u" or "s"
                or "span" or "code" or "br" or "pre" or "textarea"))
            return false;

        // No normalization inside raw/preformatted content, nor inside inline ancestors
        // containing blocks (which an HTML parser would repair rather than nest).
        if (parent is "pre" or "textarea")
            return false;
        if (name == "li")
            return parent is "ul" or "ol";
        if (parent is "ul" or "ol")
            return false;
        if (IsBlockElement(name) || name == "pre")
            return parent is null or "div" or "blockquote" or "li";
        return name != "a" || anchorDepth == 0;
    }

    private static bool IsVoidElement(string name)
        => name is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input"
            or "link" or "meta" or "param" or "source" or "track" or "wbr";
}
