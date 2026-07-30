namespace Twig.Diagnostics;

/// <summary>
/// Emits a single, deduplicated warning when twig fails to classify a work item
/// state (twig#286).
/// </summary>
/// <remarks>
/// <para>
/// <c>StateCategoryResolver.Resolve</c> returns <c>StateCategory.Unknown</c> when neither
/// the authoritative <c>StateEntry</c> metadata nor the hardcoded fallback table matches a
/// state name. That is the correct return value, but it used to be silently folded into the
/// <em>proposed</em> count by every progress summary — so a board on a custom process
/// reported finished work as not-started with no signal that twig was guessing.
/// </para>
/// <para>
/// Granularity is <b>once per distinct state name, per process</b>, not per item. A board
/// with 200 items in a `Cut` state produces one line, not 200. Dedup lives here rather than
/// at the call sites because there are three independent summary blocks
/// (<c>HumanOutputFormatter</c> and two in <c>SpectreRenderer</c>) that can run in the same
/// process; per-call-site dedup would report the same name up to three times.
/// </para>
/// <para>
/// The warning goes to <b>stderr</b>. Progress summaries render to stdout, and stdout may be
/// piped into <c>jq</c> — a diagnostic on stdout would corrupt <c>--output json</c>.
/// </para>
/// </remarks>
internal static class UnknownStateDiagnostic
{
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>
    /// Where the warning is written. Defaults to <see cref="Console.Error"/>; overridable for tests.
    /// </summary>
    internal static TextWriter Writer { get; set; } = Console.Error;

    /// <summary>
    /// Reports <paramref name="unrecognizedStates"/> as unclassifiable, skipping any name
    /// already reported in this process. Does nothing when every name has been seen before
    /// or the collection is empty — the overwhelmingly common path.
    /// </summary>
    internal static void Report(IReadOnlyCollection<string?> unrecognizedStates)
    {
        if (unrecognizedStates.Count == 0)
            return;

        List<string>? fresh = null;

        lock (Gate)
        {
            foreach (var state in unrecognizedStates)
            {
                // Null/empty is a real, distinct case: it resolves to Unknown too, and the user
                // should be told the item has no state at all rather than an unlisted one.
                var name = string.IsNullOrWhiteSpace(state) ? "(empty)" : state;
                if (Reported.Add(name))
                    (fresh ??= new List<string>()).Add(name);
            }
        }

        if (fresh is null)
            return;

        fresh.Sort(StringComparer.OrdinalIgnoreCase);
        var names = string.Join(", ", fresh.Select(n => $"'{n}'"));
        Writer.WriteLine(
            $"warning: unrecognized work item state(s): {names}. " +
            "Counted as unclassified, not proposed. " +
            "Run 'twig sync' to refresh process metadata from Azure DevOps.");
    }

    /// <summary>Clears the dedup set. Test-only — production runs are one process, one report.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            Reported.Clear();
        Writer = Console.Error;
    }
}
