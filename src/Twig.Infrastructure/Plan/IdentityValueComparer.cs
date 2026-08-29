using System.Text;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Compares two renderings of the same ADO identity for equivalence.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Why this exists (AB#802).</b> ADO rewrites an identity on write. A plan may stage
/// <c>daniel@danielgreen.net</c> and the refreshed read returns
/// <c>Daniel Green (daniel danielgreen.net)</c> — the same person, never byte-equal. The
/// readback compared identity values with ordinal equality, so every landed identity write
/// reported <c>Indeterminate</c>, which is contractually <i>outcome unknown</i> and poisons
/// the journal for an operation that in fact succeeded.
/// </para>
/// <para>
/// 🔴 <b>Applied by field metadata, never by value shape.</b> Callers reach this comparer
/// only for a field ADO declares with <c>isIdentity</c>. An ordinary string that merely
/// looks like an email stays on the ordinal path, exactly as an ordinary string that looks
/// like markup stays off <see cref="HtmlStructuralComparer"/>.
/// </para>
/// <para>
/// 🔴 <b>Equivalence, not identity resolution.</b> This type does not resolve people; it
/// decides whether two strings denote the same identity by reducing each to its most
/// specific stable part. It never contacts ADO, so it cannot decide that two genuinely
/// different spellings of one human are the same — only that one is ADO's rendering of the
/// other. That is the exact question the readback asks.
/// </para>
/// </remarks>
internal static class IdentityValueComparer
{
    public static bool AreEquivalent(string expected, string actual)
    {
        var expectedKey = ToStableKey(expected);
        var actualKey = ToStableKey(actual);
        return expectedKey.Length > 0
            && actualKey.Length > 0
            && string.Equals(expectedKey, actualKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces an identity rendering to its most specific stable part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADO's display rendering is <c>Display Name (unique name)</c>, and the unique name is
    /// the stable half — a person's display name can change without the account changing.
    /// So when a parenthesised tail is present it wins outright and the display half is
    /// discarded; otherwise the whole value is the key.
    /// </para>
    /// <para>
    /// 🔴 <b>The separator fold is load-bearing.</b> Verified live on 2026-08-28: the
    /// account <c>daniel@danielgreen.net</c> reads back as
    /// <c>Daniel Green (daniel danielgreen.net)</c> — ADO renders the <c>@</c> as a space.
    /// Folding <c>@</c> and whitespace runs to a single separator is what lets those two
    /// spellings meet. Every other character is preserved, so two different accounts on one
    /// host stay distinct.
    /// </para>
    /// </remarks>
    private static string ToStableKey(string value)
    {
        var span = value.AsSpan().Trim();
        if (span.Length == 0)
            return string.Empty;

        // Prefer the parenthesised unique name when ADO supplied one. Scan from the end so a
        // display name that itself contains parentheses cannot capture the wrong span.
        if (span[^1] == ')')
        {
            var open = span.LastIndexOf('(');
            if (open >= 0)
            {
                var inner = span[(open + 1)..^1].Trim();
                if (inner.Length > 0)
                    span = inner;
            }
        }

        return FoldSeparators(span);
    }

    /// <summary>
    /// Collapses <c>@</c> and every whitespace run to one space so ADO's separator rewrite
    /// does not make two renderings of one account compare unequal.
    /// </summary>
    private static string FoldSeparators(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var character in value)
        {
            if (character == '@' || char.IsWhiteSpace(character))
            {
                // Defer the separator so a trailing one never lands in the key.
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                builder.Append(' ');
                pendingSeparator = false;
            }
            builder.Append(character);
        }

        return builder.ToString();
    }
}
