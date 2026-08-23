namespace Twig.Commands;

/// <summary>
/// The single reader behind every command that accepts a long text body from an
/// inline argument, <c>--file &lt;path&gt;</c>, or <c>--stdin</c>.
/// </summary>
/// <remarks>
/// <para>
/// AB#617 added <c>--file</c>/<c>--stdin</c> to <c>twig note</c> and
/// <c>twig new --description</c>, which <c>twig update</c> already had. The card
/// asks for <em>the same semantics</em>, so the resolution lives here once and all
/// three call it, rather than each command growing its own reader. Two readers
/// drifting apart is the defect the card reports, one level down.
/// </para>
/// <para>
/// 🔴 <b>Ambiguity is an error, never a silent preference.</b> Supplying more than
/// one source returns <see cref="Outcome.Ambiguous"/> with exit code 2. Quietly
/// preferring one input over another would make <c>twig note --text a --file b</c>
/// report success while discarding half of what the caller asked for — the
/// false-green class AGENTS.md catalogues.
/// </para>
/// </remarks>
internal static class TextBodySource
{
    /// <summary>Why a resolution failed, or that it succeeded.</summary>
    internal enum Outcome
    {
        /// <summary>Exactly one source was given and read successfully.</summary>
        Resolved,

        /// <summary>No source was given. Callers decide whether that is legal.</summary>
        None,

        /// <summary>More than one source was given.</summary>
        Ambiguous,

        /// <summary><c>--file</c> named a path that does not exist.</summary>
        FileNotFound,
    }

    /// <summary>Where a resolved body came from, for reporting.</summary>
    internal enum Origin
    {
        /// <summary>No source supplied.</summary>
        None,

        /// <summary>An inline argument or option value.</summary>
        Inline,

        /// <summary><c>--file</c>.</summary>
        File,

        /// <summary><c>--stdin</c>.</summary>
        Stdin,
    }

    /// <param name="Outcome">Whether the body resolved, and if not, why.</param>
    /// <param name="Value">The resolved body; null unless <paramref name="Outcome"/> is <see cref="Outcome.Resolved"/>.</param>
    /// <param name="Origin">Which source the body came from.</param>
    /// <param name="Error">A caller-ready message when the outcome is not <see cref="Outcome.Resolved"/>.</param>
    internal readonly record struct Result(Outcome Outcome, string? Value, Origin Origin, string? Error);

    /// <summary>
    /// Resolves exactly one of <paramref name="inline"/>, <paramref name="filePath"/>,
    /// or <paramref name="readStdin"/> into a text body.
    /// </summary>
    /// <param name="inline">The inline value, or null when not supplied.</param>
    /// <param name="filePath">The <c>--file</c> path, or null when not supplied.</param>
    /// <param name="readStdin">Whether <c>--stdin</c> was supplied.</param>
    /// <param name="stdin">Reader standing in for standard input; a test seam.</param>
    /// <param name="inlineLabel">How the inline source is named in error text (e.g. "inline value", "note text").</param>
    /// <param name="fileFlag">The spelling of this command's file flag, so the error names a flag that actually exists.</param>
    /// <param name="stdinFlag">The spelling of this command's stdin flag.</param>
    /// <param name="trimTrailingNewline">
    /// Trim a trailing newline from file/stdin content. Files and here-docs almost
    /// always end in one and it is not part of the body the caller meant. Inline
    /// values are never trimmed — they carry exactly what was typed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<Result> ResolveAsync(
        string? inline,
        string? filePath,
        bool readStdin,
        TextReader stdin,
        string inlineLabel,
        string fileFlag,
        string stdinFlag,
        bool trimTrailingNewline,
        CancellationToken ct = default)
    {
        var sourceCount = (inline is not null ? 1 : 0)
                        + (filePath is not null ? 1 : 0)
                        + (readStdin ? 1 : 0);

        if (sourceCount == 0)
            return new Result(Outcome.None, null, Origin.None, null);

        if (sourceCount > 1)
            return new Result(Outcome.Ambiguous, null, Origin.None,
                $"Multiple value sources. Use exactly one of: {inlineLabel}, {fileFlag}, or {stdinFlag}.");

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
                return new Result(Outcome.FileNotFound, null, Origin.File, $"File not found: {filePath}");

            var fromFile = await File.ReadAllTextAsync(filePath, ct);
            return new Result(Outcome.Resolved, Trim(fromFile, trimTrailingNewline), Origin.File, null);
        }

        if (readStdin)
        {
            var fromStdin = await stdin.ReadToEndAsync(ct);
            return new Result(Outcome.Resolved, Trim(fromStdin, trimTrailingNewline), Origin.Stdin, null);
        }

        return new Result(Outcome.Resolved, inline, Origin.Inline, null);
    }

    private static string Trim(string value, bool trimTrailingNewline)
        => trimTrailingNewline ? value.TrimEnd('\r', '\n') : value;
}
