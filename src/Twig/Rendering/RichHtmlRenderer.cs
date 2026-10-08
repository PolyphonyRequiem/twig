using System.Globalization;
using System.Net;
using System.Text;
using Spectre.Console;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>Shared, inert HTML presentation for previews and complete read-only detail.</summary>
internal static class RichHtmlRenderer
{
    internal static IReadOnlyList<RenderNode> Render(string? html, int width)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var nodes = new List<RenderNode>();
        RenderChildren(Parse(html).Children, nodes, Math.Max(width, 1), 0, "");
        return nodes;
    }

    // The legacy show preview uses the same parser, with a textual table presentation.
    internal static string ToMarkup(string? html)
    {
        var output = new StringBuilder();
        foreach (var node in Render(html, 1))
        {
            if (node is RenderNode.Text { Content: "" }) continue;
            if (output.Length > 0) output.Append('\n');
            if (node is RenderNode.Markup markup) output.Append(markup.Content);
            else if (node is RenderNode.Text text) output.Append(Markup.Escape(text.Content));
        }
        return output.ToString();
    }

    internal static string SafeText(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch == '\t') result.Append("    ");
            else if ((char.IsControl(ch) && ch != '\n') ||
                char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                result.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
            else result.Append(ch);
        }
        return result.ToString();
    }

    private sealed class Element(string name, string? text = null, string? href = null)
    {
        internal string Name { get; } = name;
        internal string? Text { get; } = text;
        internal string? Href { get; } = href;
        internal List<Element> Children { get; } = [];
    }

    private static Element Parse(string html)
    {
        var root = new Element("");
        var stack = new List<Element> { root };
        for (var i = 0; i < html.Length;)
        {
            if (html[i] != '<')
            {
                var end = html.IndexOf('<', i);
                if (end < 0) end = html.Length;
                stack[^1].Children.Add(new Element("#text", SafeText(WebUtility.HtmlDecode(html[i..end]))));
                i = end;
                continue;
            }
            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            var tagEnd = TagEnd(html, i + 1);
            if (tagEnd < 0)
            {
                stack[^1].Children.Add(new Element("#text", SafeText(WebUtility.HtmlDecode(html[i..]))));
                break;
            }
            var tagStart = i;
            var tag = html[(i + 1)..tagEnd].Trim();
            var closing = tag.StartsWith('/');
            var start = closing ? 1 : 0;
            var nameEnd = start;
            while (nameEnd < tag.Length && char.IsAsciiLetterOrDigit(tag[nameEnd])) nameEnd++;
            var name = tag[start..nameEnd].ToLowerInvariant();
            i = tagEnd + 1;
            if (start >= tag.Length || !char.IsAsciiLetter(tag[start]))
            {
                if (!tag.StartsWith('!') && !tag.StartsWith('?'))
                    stack[^1].Children.Add(new Element("#text", SafeText(WebUtility.HtmlDecode(html[tagStart..i]))));
                continue;
            }
            if (closing)
            {
                for (var s = stack.Count - 1; s > 0; s--)
                    if (stack[s].Name == name) { stack.RemoveRange(s, stack.Count - s); break; }
                continue;
            }
            // Raw active content is never rendered or requested. HTML is data, not a browser.
            if (name is "script" or "style" or "iframe" or "object" or "template")
            {
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { i = html.Length; continue; }
                var closeEnd = TagEnd(html, end + 2);
                i = closeEnd < 0 ? html.Length : closeEnd + 1;
                continue;
            }
            if (name is "img" or "input" or "link" or "meta" or "base" or "embed") continue;
            // Common omitted end tags are repaired without swallowing the following values.
            if (name is "li" or "tr" or "td" or "th" or "p")
                for (var s = stack.Count - 1; s > 0; s--)
                {
                    if (stack[s].Name == name || (name is "td" or "th" && stack[s].Name is "td" or "th"))
                    { stack.RemoveRange(s, stack.Count - s); break; }
                    if (name == "li" && stack[s].Name is "ul" or "ol") break;
                    if (name is "td" or "th" && stack[s].Name == "tr") break;
                }
            var element = new Element(name, href: name == "a" ? Attribute(tag, nameEnd, "href") : null);
            stack[^1].Children.Add(element);
            // Flatten excessive nesting rather than recursing without bound; retain every text value.
            if (name is not ("br" or "hr" or "wbr") && !tag.EndsWith('/') && stack.Count < 64)
                stack.Add(element);
        }
        return root;
    }

    private static int TagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start; i < html.Length; i++)
        {
            var ch = html[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; }
            else if (ch is '\'' or '"') quote = ch;
            else if (ch == '>') return i;
        }
        return -1;
    }

    private static string? Attribute(string tag, int offset, string wanted)
    {
        while (offset < tag.Length)
        {
            while (offset < tag.Length && (char.IsWhiteSpace(tag[offset]) || tag[offset] == '/')) offset++;
            var start = offset;
            while (offset < tag.Length && !char.IsWhiteSpace(tag[offset]) && tag[offset] is not ('=' or '/')) offset++;
            var name = tag[start..offset];
            while (offset < tag.Length && char.IsWhiteSpace(tag[offset])) offset++;
            if (offset >= tag.Length || tag[offset] != '=') continue;
            offset++;
            while (offset < tag.Length && char.IsWhiteSpace(tag[offset])) offset++;
            if (offset >= tag.Length) break;
            var quote = tag[offset] is '\'' or '"' ? tag[offset++] : '\0';
            start = offset;
            while (offset < tag.Length && (quote == '\0' ? !char.IsWhiteSpace(tag[offset]) : tag[offset] != quote)) offset++;
            var value = tag[start..offset];
            if (quote != '\0' && offset < tag.Length) offset++;
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return WebUtility.HtmlDecode(value);
        }
        return null;
    }

    private static bool Block(Element element) => element.Name is
        "p" or "div" or "section" or "article" or "header" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
        "ul" or "ol" or "li" or "pre" or "table" or "blockquote" or "hr";

    private static bool ContainsBlock(Element element) => Block(element) || element.Children.Any(ContainsBlock);

    private static void RenderChildren(IReadOnlyList<Element> children, List<RenderNode> output, int width, int depth, string indent)
    {
        var inline = new StringBuilder();
        void Flush()
        {
            var value = inline.ToString().Trim();
            inline.Clear();
            if (value.Length > 0) output.Add(new RenderNode.Markup(indent + value));
        }
        foreach (var child in children)
        {
            if (!ContainsBlock(child)) { inline.Append(Inline(child)); continue; }
            Flush();
            switch (child.Name)
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    output.Add(new RenderNode.Markup(indent + Styled("bold cyan underline", InlineChildren(child))));
                    break;
                case "pre":
                    var code = Plain(child).Trim('\n');
                    output.Add(new RenderNode.Markup(string.Join('\n', code.Split('\n').Select(line => indent + "[grey]│[/] [silver]" + Markup.Escape(line) + "[/]"))));
                    break;
                case "ul":
                case "ol":
                    var index = 0;
                    foreach (var item in child.Children)
                    {
                        if (item.Name != "li") { RenderChildren([item], output, width, depth, indent); continue; }
                        var bullet = child.Name == "ol" ? (++index).ToString(CultureInfo.InvariantCulture) + ". " : "• ";
                        var listIndent = indent + new string(' ', depth * 2);
                        var start = output.Count;
                        RenderChildren(item.Children, output, width, depth + 1, listIndent + new string(' ', bullet.Length));
                        if (output.Count == start) output.Add(new RenderNode.Markup(listIndent + bullet));
                        else if (output[start] is RenderNode.Markup first && first.Content.StartsWith(listIndent + new string(' ', bullet.Length), StringComparison.Ordinal))
                            output[start] = new RenderNode.Markup(listIndent + "[cyan]" + bullet + "[/]" + first.Content[(listIndent.Length + bullet.Length)..]);
                        else output.Insert(start, new RenderNode.Markup(listIndent + "[cyan]" + bullet + "[/]"));
                    }
                    break;
                case "li":
                    output.Add(new RenderNode.Markup(indent + "• " + InlineChildren(child)));
                    break;
                case "table": RenderTable(child, output, width); break;
                case "blockquote": RenderChildren(child.Children, output, width, depth, indent + "│ "); break;
                case "hr": output.Add(new RenderNode.Markup(indent + "[grey]────────────────[/]")); break;
                default: RenderChildren(child.Children, output, width, depth, indent); break;
            }
            if (output.Count > 0 && output[^1] is not RenderNode.Text { Content: "" }) output.Add(new RenderNode.Text(""));
        }
        Flush();
        while (output.Count > 0 && output[^1] is RenderNode.Text { Content: "" }) output.RemoveAt(output.Count - 1);
    }

    private static string InlineChildren(Element element) => string.Concat(element.Children.Select(Inline));
    private static string Inline(Element element)
    {
        if (element.Text is { } text) return Markup.Escape(CollapseWhitespace(text));
        if (element.Name == "br") return "\n";
        var body = InlineChildren(element);
        var style = element.Name switch
        {
            "b" or "strong" => "bold",
            "i" or "em" => "italic",
            "u" => "underline",
            "s" or "del" or "strike" => "strikethrough",
            "code" or "kbd" => "silver on grey15",
            _ => null,
        };
        if (element.Name == "a" && element.Href is { } href &&
            Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            // Visible inert URLs remain useful over SSH and emit no OSC hyperlink controls.
            var url = SafeText(uri.AbsoluteUri);
            return Styled("cyan underline", body) + " [grey](" + Markup.Escape(url) + ")[/]";
        }
        return style is null ? body : Styled(style, body);
    }

    private static string Styled(string style, string body) =>
        string.Join('\n', body.Split('\n').Select(line => "[" + style + "]" + line + "[/]"));

    private static string CollapseWhitespace(string text)
    {
        var output = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { if (!space) output.Append(' '); space = true; }
            else { output.Append(ch); space = false; }
        }
        return output.ToString();
    }

    private static string Plain(Element element) => element.Text ??
        (element.Name == "br" ? "\n" : string.Concat(element.Children.Select(Plain)));

    private static string CellText(Element element)
    {
        if (element.Text is { } text) return text;
        if (element.Name == "br") return "\n";
        var value = string.Concat(element.Children.Select(CellText));
        if (element.Name == "a" && element.Href is { } href &&
            Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            value += " (" + SafeText(uri.AbsoluteUri) + ")";
        return Block(element) ? "\n" + value + "\n" : value;
    }

    private static void RenderTable(Element table, List<RenderNode> output, int width)
    {
        var rows = new List<List<Element>>();
        void Visit(Element element)
        {
            if (element.Name == "tr") rows.Add(element.Children.Where(e => e.Name is "td" or "th").ToList());
            else foreach (var child in element.Children) Visit(child);
        }
        Visit(table);
        var count = rows.Select(row => row.Count).DefaultIfEmpty().Max();
        if (count == 0) { RenderChildren(table.Children, output, width, 0, ""); return; }
        var hasHeader = rows[0].Count > 0 && rows[0].All(cell => cell.Name == "th");
        var headers = Enumerable.Range(0, count).Select(i => hasHeader && i < rows[0].Count
            ? CollapseWhitespace(Plain(rows[0][i])).Trim() : "Column " + (i + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        foreach (var caption in table.Children.Where(c => c.Name == "caption"))
            output.Add(new RenderNode.Markup("[bold]" + InlineChildren(caption) + "[/]"));
        if (hasHeader && rows.Count == 1)
        {
            output.Add(new RenderNode.Markup("[bold]" + Markup.Escape(string.Join(" · ", headers)) + "[/]"));
            return;
        }
        if (width >= count * 12 + count + 1)
        {
            var columns = headers.Select((header, i) => new RenderColumn(i.ToString(CultureInfo.InvariantCulture), header)).ToArray();
            output.Add(new RenderNode.Table(null, columns, rows.Skip(hasHeader ? 1 : 0).Select(row =>
                new RenderRow(null, row.Select((cell, i) => KeyValuePair.Create(i.ToString(CultureInfo.InvariantCulture),
                    RenderCell.String(CellText(cell).Trim()))).ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray()));
        }
        else
        {
            // Narrow viewports stack cells rather than eliding columns or squeezing values away.
            foreach (var row in rows.Skip(hasHeader ? 1 : 0))
            {
                for (var i = 0; i < row.Count; i++)
                {
                    var label = "[bold]" + Markup.Escape(headers[i]) + "[/]: ";
                    if (row[i].Children.Any(ContainsBlock))
                    {
                        output.Add(new RenderNode.Markup(label));
                        RenderChildren(row[i].Children, output, width, 0, "  ");
                    }
                    else output.Add(new RenderNode.Markup(label + InlineChildren(row[i])));
                }
                output.Add(new RenderNode.Text(""));
            }
        }
    }
}
