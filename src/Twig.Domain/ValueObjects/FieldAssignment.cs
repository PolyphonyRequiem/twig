using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Parses the <c>fieldReferenceName=value</c> argument shape shared by
/// <c>twig new --field</c>, <c>twig seed new --field</c> and <c>twig batch --set</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT used by <c>LoopbackPkceFlow.ParseQuery</c>. That parses a URL query
/// string: it URL-decodes both halves and treats a pair with no <c>=</c> as a valid key
/// with an empty value. Those are different semantics, not a duplicate — folding it in
/// here would either break OAuth callbacks or force this type to grow a mode flag.
/// </para>
/// </remarks>
public static class FieldAssignment
{
    /// <summary>
    /// Parses every <c>key=value</c> pair, preserving input order so callers can apply
    /// them last-writer-wins. Fails closed on the first malformed pair: callers get a
    /// complete list or nothing, never a partially applied set.
    /// </summary>
    /// <param name="pairs">Raw arguments; null or empty yields an empty list.</param>
    /// <param name="flagName">
    /// The flag being parsed (e.g. <c>--field</c>, <c>--set</c>), used in the error
    /// message so shared code doesn't hard-code one caller's flag name.
    /// </param>
    public static Result<IReadOnlyList<FieldChange>> ParseAll(
        IReadOnlyList<string>? pairs,
        string flagName)
    {
        if (pairs is null || pairs.Count == 0)
            return Result.Ok<IReadOnlyList<FieldChange>>([]);

        var parsed = new List<FieldChange>(pairs.Count);
        foreach (var pair in pairs)
        {
            // Split on the FIRST '=' only, so a value may itself contain '='.
            // eqIndex < 1 rejects both "no equals" and a leading "=value" with an
            // empty field name.
            var eqIndex = pair.IndexOf('=');
            if (eqIndex < 1)
            {
                return Result.Fail<IReadOnlyList<FieldChange>>(
                    $"Invalid {flagName} format: '{pair}'. Expected fieldReferenceName=value.");
            }

            parsed.Add(new FieldChange(pair[..eqIndex], null, pair[(eqIndex + 1)..]));
        }

        return Result.Ok<IReadOnlyList<FieldChange>>(parsed);
    }
}
