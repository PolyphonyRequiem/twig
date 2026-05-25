using System.Text;

namespace Twig.RenderTree;

/// <summary>
/// Helpers for working with Spectre.Console markup strings inside
/// format-agnostic renderers (which intentionally don't reference
/// Spectre.Console).
/// </summary>
public static class MarkupHelpers
{
    /// <summary>
    /// Removes Spectre.Console markup tags from <paramref name="markup"/>,
    /// returning the human-visible text. Recognises the literal-bracket
    /// escapes <c>[[</c> and <c>]]</c> per Spectre's markup grammar.
    /// </summary>
    /// <remarks>
    /// This is a deliberately lightweight strip — it does not validate
    /// nesting, balance, or style names. Callers should pass markup that
    /// originated from <see cref="RenderNode.Markup"/> nodes built by command
    /// code; arbitrary user-supplied text containing stray <c>[</c> or
    /// <c>]</c> characters should be Spectre-escaped before being wrapped
    /// in a markup node.
    /// </remarks>
    public static string StripMarkup(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);

        if (markup.Length == 0)
        {
            return markup;
        }

        var sb = new StringBuilder(markup.Length);
        var i = 0;
        while (i < markup.Length)
        {
            var ch = markup[i];

            if (ch == '[' && i + 1 < markup.Length && markup[i + 1] == '[')
            {
                sb.Append('[');
                i += 2;
                continue;
            }

            if (ch == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                sb.Append(']');
                i += 2;
                continue;
            }

            if (ch == '[')
            {
                var closeIdx = markup.IndexOf(']', i + 1);
                if (closeIdx < 0)
                {
                    sb.Append(markup, i, markup.Length - i);
                    break;
                }

                i = closeIdx + 1;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }
}
