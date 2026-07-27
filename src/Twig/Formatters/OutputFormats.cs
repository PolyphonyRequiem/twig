namespace Twig.Formatters;

/// <summary>
/// The single accept-list for the <c>-o</c>/<c>--output</c> option (wayfinder 0019).
/// </summary>
/// <remarks>
/// <para>
/// Before this type existed, both <see cref="OutputFormatterFactory"/> and
/// <see cref="Twig.Rendering.RendererFactory"/> ended their format switch in a
/// catch-all arm that silently meant <c>human</c>. A typo — <c>-o jsno</c>,
/// <c>-o json5</c> — therefore produced ANSI prose on stdout and exit code 0,
/// which is the worst failure mode available to a tool whose output is piped
/// into <c>jq</c>.
/// </para>
/// <para>
/// The fix is one list, in one place, validated once at the entrypoint
/// (<c>Program.cs</c>, before <c>app.Run</c>). Both factories now resolve
/// through <see cref="Normalize"/> so the list and the switch arms cannot
/// drift apart; <see cref="OutputFormatsAcceptListTests"/> pins that they
/// cannot diverge.
/// </para>
/// <para>
/// This is the narrow entrypoint-only version the ticket sanctions as an
/// escape hatch: the accept-list lives as a literal in one file and is
/// deliberately the <em>seed</em> of ticket 0002's capability-seam collapse.
/// The 33 copy-pasted machine-format predicates in <c>src/Twig/Commands/</c>
/// are NOT touched here — collapsing them is 0002's work.
/// </para>
/// </remarks>
public static class OutputFormats
{
    /// <summary>The format used when <c>-o</c> is not supplied.</summary>
    public const string Default = "human";

    /// <summary>
    /// Every value <c>-o</c> accepts. Membership is matched case-insensitively
    /// by lowercasing the input, preserving the historical
    /// <c>ToLowerInvariant()</c> behaviour of both factories.
    /// </summary>
    public static readonly IReadOnlyList<string> Accepted =
    [
        "human",
        "json",
        "json-full",
        "json-compact",
        "minimal",
        "ids",
    ];

    /// <summary>
    /// Normalizes <paramref name="format"/> to its canonical lower-case form.
    /// Returns <see cref="Default"/> for <see langword="null"/>. Returns
    /// <see langword="null"/> when the value is not on the accept-list.
    /// </summary>
    public static string? Normalize(string? format)
    {
        if (format is null)
            return Default;

        var lowered = format.ToLowerInvariant();
        foreach (var accepted in Accepted)
        {
            if (string.Equals(lowered, accepted, StringComparison.Ordinal))
                return accepted;
        }

        return null;
    }

    /// <summary>True when <paramref name="format"/> is on the accept-list.</summary>
    public static bool IsAccepted(string? format) => Normalize(format) is not null;

    /// <summary>
    /// The accepted values as a comma-separated list, for error messages and
    /// help text. Never restate the list by hand — read it from here.
    /// </summary>
    public static string Describe() => string.Join(", ", Accepted);
}
