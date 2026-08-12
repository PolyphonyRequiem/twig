namespace Twig.Domain.ValueObjects;

/// <summary>
/// Whether a rule was authored on this process or came with the parent — in a form that can
/// carry "the server did not tell us" rather than collapsing it into one of the real answers.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is the tag the whole carry-everything ruling rests on.</b> A type derived from a
/// system one carries ~54 rules, of which one or two are authored here; the document carries
/// all of them rather than filtering, precisely because a difference that exists only in the
/// omitted part diffs clean. What makes that volume bearable is that every rule says which
/// class it belongs to, so filtering is available <i>to the reader</i>, downstream. A rule
/// whose class is lost is a rule the reader cannot filter — which quietly takes the ruling's
/// mitigation away while keeping its cost.
/// </para>
/// <para>
/// 🔴 <b>So a missing <c>customizationType</c> is its own case and never <see cref="System"/>.</b>
/// This is the same shape as <see cref="FieldValueConstraint"/> (AB#237) and for the same
/// reason: rendering an absent key as one of the stated answers is "absence of evidence
/// rendered as a stated fact". The route is version-sensitive — <c>7.1</c> and
/// <c>7.1-preview.2</c> return byte-identical bodies today, but a future version that drops
/// the key must surface as <see cref="RuleCustomizationKind.Unknown"/> rather than silently
/// reclassifying every authored rule as inherited plumbing.
/// </para>
/// <para>
/// 🔴 <see cref="Token"/> keeps the server's own spelling even for a value this type does not
/// recognise. Twig does not reinterpret the server's vocabulary, and an unrecognised token is
/// a fact worth carrying rather than an error worth erasing — the reader can see it, and a new
/// server-side class does not become invisible the moment it appears.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Solution S3, Implementation Decision 4.
/// </para>
/// </remarks>
/// <param name="Kind">Which class this rule belongs to, or that we do not know.</param>
/// <param name="Token">
/// The server's verbatim <c>customizationType</c> string, or empty when the key was absent.
/// Carried so an unrecognised class is visible rather than erased, and so the document does
/// not paraphrase the server.
/// </param>
internal sealed record RuleCustomization(RuleCustomizationKind Kind, string Token)
{
    /// <summary>The server did not say. Never rendered as one of the real classes.</summary>
    internal static readonly RuleCustomization Unknown =
        new(RuleCustomizationKind.Unknown, string.Empty);

    /// <summary>
    /// Classifies a verbatim server token, keeping the token whichever way it lands.
    /// </summary>
    /// <remarks>
    /// Matched <c>OrdinalIgnoreCase</c> because this route family is already known to be
    /// inconsistent about spelling across api-versions, and a casing difference must not
    /// demote an authored rule to <see cref="RuleCustomizationKind.Unknown"/>.
    /// <para>
    /// 🔴 An unrecognised non-empty token is <see cref="RuleCustomizationKind.Unknown"/> with
    /// the token PRESERVED. Guessing which of the three known classes a new server value
    /// resembles would be a confident claim about a vocabulary Twig does not own.
    /// </para>
    /// </remarks>
    internal static RuleCustomization From(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Unknown;

        var kind = token switch
        {
            _ when string.Equals(token, "custom", StringComparison.OrdinalIgnoreCase)
                => RuleCustomizationKind.Custom,
            _ when string.Equals(token, "inherited", StringComparison.OrdinalIgnoreCase)
                => RuleCustomizationKind.Inherited,
            _ when string.Equals(token, "system", StringComparison.OrdinalIgnoreCase)
                => RuleCustomizationKind.System,
            _ => RuleCustomizationKind.Unknown,
        };

        return new RuleCustomization(kind, token);
    }
}

/// <summary>The four ways a rule can stand with respect to where it was authored.</summary>
/// <remarks>
/// 🔴 Four and not three: <see cref="Unknown"/> is a distinct answer, not a default. Folding it
/// into <see cref="System"/> would silently reclassify every authored rule as inherited
/// plumbing the moment the server stopped sending the key — which is exactly the filter a
/// reader would then use to throw those rules away.
/// <para>
/// The numbering is load-bearing: it is a sort key in the assembler's tiebreak chain, so
/// inserting a member renumbers the document's rule order.
/// </para>
/// </remarks>
internal enum RuleCustomizationKind
{
    /// <summary>The server did not say. Not a claim about the rule.</summary>
    Unknown = 0,

    /// <summary>Authored on this process.</summary>
    Custom = 1,

    /// <summary>Inherited from the parent process.</summary>
    Inherited = 2,

    /// <summary>System plumbing that came with the stock process.</summary>
    System = 3,
}
