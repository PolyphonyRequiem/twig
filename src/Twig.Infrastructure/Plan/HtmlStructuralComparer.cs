using System.Net;
using System.Text;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Compares well-formed HTML fragments by their structural tokens without discarding text.
/// ADO is free to normalize tag casing, attribute quoting and order, entity spelling, and
/// CRLF line endings; every tag, attribute value, and text node remains significant.
/// </summary>
internal static class HtmlStructuralComparer
{
    public static bool AreEquivalent(string expected, string actual)
        => TryCanonicalize(expected, out var expectedCanonical)
            && TryCanonicalize(actual, out var actualCanonical)
            && string.Equals(expectedCanonical, actualCanonical, StringComparison.Ordinal);

    private static bool TryCanonicalize(string source, out string canonical)
    {
        var builder = new StringBuilder(source.Length);
        var openTags = new Stack<string>();
        var index = 0;

        while (index < source.Length)
        {
            var tagStart = source.IndexOf('<', index);
            if (tagStart < 0)
            {
                AppendText(builder, source.AsSpan(index));
                index = source.Length;
                break;
            }

            AppendText(builder, source.AsSpan(index, tagStart - index));
            index = tagStart;
            if (!TryAppendTag(source, ref index, builder, openTags))
            {
                canonical = string.Empty;
                return false;
            }
        }

        if (openTags.Count != 0)
        {
            canonical = string.Empty;
            return false;
        }

        canonical = builder.ToString();
        return true;
    }

    private static bool TryAppendTag(
        string source,
        ref int index,
        StringBuilder builder,
        Stack<string> openTags)
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
            SkipWhitespace(source, ref cursor);
            if (cursor >= length || source[cursor] != '>' || openTags.Count == 0
                || !string.Equals(openTags.Peek(), name, StringComparison.Ordinal))
                return false;

            openTags.Pop();
            builder.Append('E');
            AppendToken(builder, name);
            index = cursor + 1;
            return true;
        }

        if (source[cursor] == '!')
        {
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

        var attributes = new List<(string Name, string? Value)>();
        var selfClosing = false;
        while (true)
        {
            SkipWhitespace(source, ref cursor);
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
                SkipWhitespace(source, ref cursor);
                if (cursor >= length || source[cursor] != '>')
                    return false;
                selfClosing = true;
                cursor++;
                break;
            }

            if (!TryReadName(source, ref cursor, out var attributeName))
                return false;

            SkipWhitespace(source, ref cursor);
            string? attributeValue = null;
            if (cursor < length && source[cursor] == '=')
            {
                cursor++;
                SkipWhitespace(source, ref cursor);
                if (!TryReadAttributeValue(source, ref cursor, out attributeValue))
                    return false;
                attributeValue = NormalizeText(attributeValue);
            }

            attributes.Add((attributeName, attributeValue));
        }

        attributes.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return byName != 0
                ? byName
                : string.Compare(left.Value, right.Value, StringComparison.Ordinal);
        });

        builder.Append('S');
        AppendToken(builder, tagName);
        builder.Append(attributes.Count).Append(';');
        foreach (var attribute in attributes)
        {
            AppendToken(builder, attribute.Name);
            AppendToken(builder, attribute.Value);
        }

        if (!selfClosing && !IsVoidElement(tagName))
            openTags.Push(tagName);

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

    private static void AppendText(StringBuilder builder, ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return;

        builder.Append('T');
        AppendToken(builder, NormalizeText(text.ToString()));
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

    private static void SkipWhitespace(string source, ref int cursor)
    {
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
    }

    private static bool IsNameCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is ':' or '-' or '_';

    private static bool IsVoidElement(string name)
        => name is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input"
            or "link" or "meta" or "param" or "source" or "track" or "wbr";
}
