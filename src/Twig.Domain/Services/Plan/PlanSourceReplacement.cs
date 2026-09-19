namespace Twig.Domain.Services.Plan;

/// <summary>
/// AB#832: evidence that the plan file at a given path is no longer the file that produced
/// the transaction(s) journaled against that path.
/// <para>
/// Plan files are contractually immutable and single-use — allocated once per transaction,
/// never appended to, never reused. Nothing outside twig enforces that, so two sessions
/// sharing one plans directory can compute the same "next free sequence" and write the same
/// path, the loser's document silently replacing the winner's.
/// </para>
/// <para>
/// The journal is keyed by digest, so an overwritten path resolves to whichever transaction
/// matches the bytes currently on disk. That is a truthful answer to the wrong question, and
/// it is indistinguishable from the honest cases: before the replacing document is previewed
/// the path looks like it was never previewed at all, and afterwards it looks like a clean
/// journal for a transaction the original session never authored. Comparing the source path's
/// journaled digests against the file's current digest separates both from the honest cases.
/// </para>
/// <para>
/// Note the discriminator is deliberately NOT "the journal I found records a different source
/// path". After the replacing document is previewed its journal records the very same path, so
/// that check reports nothing. The signal is the <i>existence of other digests</i> against this
/// path.
/// </para>
/// </summary>
public sealed record PlanSourceReplacement
{
    /// <summary>Absolute path whose journaled digests disagree with the file now on disk.</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Digest of the bytes currently at <see cref="SourcePath"/>. Null when the file no longer
    /// parses to a digest at all.
    /// </summary>
    public required string? CurrentDigest { get; init; }

    /// <summary>
    /// Every digest journaled against <see cref="SourcePath"/> that is not
    /// <see cref="CurrentDigest"/>, oldest preview first. Never empty — the replacement is not
    /// reported unless at least one such digest exists.
    /// </summary>
    public required IReadOnlyList<string> SupersededDigests { get; init; }

    /// <summary>
    /// True when the current bytes have themselves been journaled. Distinguishes the two
    /// observable shapes: <c>false</c> is the window after the overwrite but before the
    /// replacing document was previewed (which otherwise reads as a lost journal),
    /// <c>true</c> is a path that has now carried more than one transaction.
    /// </summary>
    public required bool CurrentDigestJournaled { get; init; }
}
