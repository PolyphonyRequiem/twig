namespace Twig.Rendering;

/// <summary>
/// Measures the visible terminal width of an already-rendered display string, so a
/// projector can pad one column into alignment (AB#775).
/// </summary>
/// <remarks>
/// <para>
/// Two hazards make <see cref="string.Length"/> the wrong answer, and both have bitten
/// this codebase before:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>SGR escapes.</strong> Once <c>--color always</c> is in play a display string
/// may carry ANSI escape sequences, which occupy zero columns. Counting them pads short
/// and the column collapses exactly on the coloured rows.
/// </item>
/// <item>
/// <strong>Nerd font glyphs.</strong> A BMP Private Use Area badge is one UTF-16 char
/// but is laid out as a glyph plus a mandatory trailing space — see the remarks on
/// <see cref="Twig.Domain.ValueObjects.IconSet"/>. Callers must run badges through
/// <see cref="Twig.Domain.ValueObjects.IconSet.NormalizeBadgeWidth"/> before measuring
/// or padding; that normalization is what makes "one char is one column" true again,
/// and it is why this type needs no private-use special case of its own. Skipping it
/// misaligns nerd mode by exactly one cell per badge while unicode mode looks perfect.
/// </item>
/// </list>
/// </remarks>
internal static class DisplayWidth
{
    /// <summary>
    /// Returns the number of terminal columns <paramref name="text"/> occupies,
    /// ignoring any ANSI SGR escape sequences it contains.
    /// </summary>
    public static int Measure(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        var i = 0;
        while (i < text.Length)
        {
            // CSI ... m — an SGR sequence, zero columns. Anything else starting with
            // ESC is not something this renderer emits, so it is counted literally
            // rather than silently swallowed.
            if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var close = text.IndexOf('m', i + 2);
                if (close >= 0)
                {
                    i = close + 1;
                    continue;
                }
            }

            // Surrogate pairs are one glyph across two UTF-16 chars. They are banned
            // for badges (IconSet measures them as zero width) but a title or a
            // caller-supplied note may legitimately contain one.
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                width++;
                i += 2;
                continue;
            }

            width++;
            i++;
        }

        return width;
    }
}
