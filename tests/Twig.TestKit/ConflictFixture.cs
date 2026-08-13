using Twig.Domain.Aggregates;

namespace Twig.TestKit;

/// <summary>
/// Builds the local/remote <see cref="WorkItem"/> pair a conflict-path test needs, with the
/// diverged-revision precondition enforced at construction rather than left to convention.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trap this exists to close.</b> Both <c>ConflictResolver.Resolve</c> and
/// <c>ThreeWayMerge.Resolve</c> short-circuit to <c>NoConflict</c> when
/// <c>local.Revision == remote.Revision</c>, before any field comparison runs. A freshly
/// constructed <see cref="WorkItem"/> has <c>Revision = 0</c> on BOTH sides, so a conflict
/// fixture that forgets to advance the remote revision returns from the short-circuit and
/// never reaches the branch it claims to test. The test passes. It proves nothing.
/// </para>
/// <para>
/// That hazard was previously recorded only in prose — in <c>AGENTS.md</c> and in
/// <c>ThreeWayMerge.Resolve</c>'s own remarks. Prose is exactly what fails to hold under a
/// future edit, which is why this repo already replaced "remember the exit code" with
/// <c>tools/run-tests.sh</c>. This type applies the same treatment: the precondition is
/// asserted here, once, instead of being re-remembered per test.
/// </para>
/// <para>
/// The inverse case — pinning the short-circuit as INTENDED behaviour so nobody "fixes" it —
/// is covered by the guard arms named in the extension points below. Both halves are needed:
/// this stops the fixture being built wrong, the guard arms stop the production behaviour
/// being removed.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var (local, remote) = ConflictFixture.Diverged(
///     new WorkItemBuilder(1, "Item").Build(),
///     new WorkItemBuilder(1, "Item").Build());
///
/// local.SetField("System.Description", "Local value");
/// remote.SetField("System.Description", "Remote value");
///
/// var result = ConflictResolver.Resolve(local, remote);
/// </code>
/// </example>
public static class ConflictFixture
{
    /// <summary>Revision stamped onto the local side by <see cref="Diverged"/>.</summary>
    public const int DefaultLocalRevision = 5;

    /// <summary>Revision stamped onto the remote side by <see cref="Diverged"/>.</summary>
    public const int DefaultRemoteRevision = 6;

    /// <summary>
    /// Stamps diverged revisions onto a local/remote pair and verifies they actually differ,
    /// so the caller cannot land on the <c>NoConflict</c> short-circuit by accident.
    /// </summary>
    /// <param name="local">The local side. Mutated: its revision is stamped.</param>
    /// <param name="remote">The remote side. Mutated: its revision is stamped ahead of local.</param>
    /// <param name="localRevision">Local revision. Defaults to <see cref="DefaultLocalRevision"/>.</param>
    /// <param name="remoteRevision">Remote revision. Defaults to <see cref="DefaultRemoteRevision"/>.</param>
    /// <returns>The same two items, revisions advanced, ready for divergent field edits.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the two revisions are equal. This is deliberate and is the whole point of
    /// the type: an equal-revision "conflict" fixture is not a weaker test, it is a test of a
    /// completely different code path wearing the wrong name. Failing loudly at construction
    /// beats passing silently at assertion.
    /// </exception>
    public static (WorkItem Local, WorkItem Remote) Diverged(
        WorkItem local,
        WorkItem remote,
        int localRevision = DefaultLocalRevision,
        int remoteRevision = DefaultRemoteRevision)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (localRevision == remoteRevision)
        {
            throw new InvalidOperationException(
                $"ConflictFixture.Diverged was given equal revisions ({localRevision}). " +
                "Resolve() short-circuits to NoConflict on revision equality, so this fixture " +
                "would never reach the conflict branch. Advance the remote revision, or use " +
                "ConflictFixture.SameRevision if testing the short-circuit is the intent.");
        }

        local.MarkSynced(localRevision);
        remote.MarkSynced(remoteRevision);

        // Belt and braces: MarkSynced is the only writer of Revision today, but this asserts the
        // POSTcondition rather than trusting the setter, so a future change to MarkSynced that
        // clamps or ignores the value surfaces here instead of silently hollowing every caller.
        if (local.Revision == remote.Revision)
        {
            throw new InvalidOperationException(
                $"ConflictFixture.Diverged failed to diverge the revisions (both are " +
                $"{local.Revision}). WorkItem.MarkSynced no longer stores the value it was " +
                "given — every conflict-path test in the suite is now testing the NoConflict " +
                "short-circuit. Fix MarkSynced, do not relax this check.");
        }

        return (local, remote);
    }

    /// <summary>
    /// Stamps the SAME revision onto both sides, for a test whose subject genuinely is the
    /// short-circuit. Named so that reading the call site tells you the equality is intentional
    /// rather than an oversight — the distinction <see cref="Diverged"/> exists to protect.
    /// </summary>
    public static (WorkItem Local, WorkItem Remote) SameRevision(
        WorkItem local,
        WorkItem remote,
        int revision = DefaultLocalRevision)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        local.MarkSynced(revision);
        remote.MarkSynced(revision);

        return (local, remote);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  EXTENSION POINTS — read this if you arrived here after being bitten
// ═══════════════════════════════════════════════════════════════════════════════
//
// This type was deliberately introduced NARROW (ADO #182 / wayfinder ticket 0006 M2). It is
// used by the new conflict-path tests only. The pre-existing suites still stamp revisions by
// hand. That was a considered choice, not an oversight: retrofitting a green suite is churn
// in a file the originating change did not otherwise touch, and it buys consistency rather
// than coverage.
//
// The cost of that choice is that TWO IDIOMS COEXIST. If that has now cost you real time —
// you hit a hollow test, or could not tell which idiom was canonical — the widening is
// already scoped. Do it:
//
//   1. RETROFIT THE EXISTING CONFLICT SUITES.
//      - tests/Twig.Domain.Tests/Services/Sync/ConflictResolverTests.cs
//        Twelve arms, each doing `local.MarkSynced(5); remote.MarkSynced(6);` by hand.
//      - tests/Twig.Domain.Tests/Services/Reconciliation/ThreeWayMergeTests.cs
//        Same hazard; ThreeWayMerge.Resolve carries the identical revision-equality
//        short-circuit and says so in its own <remarks>.
//      Mechanical: replace the paired MarkSynced calls with a ConflictFixture.Diverged
//      destructure. Expect roughly a 150-line diff. Land it as its OWN commit — it touches
//      currently-green tests and should be reviewable without the feature work around it.
//
//   2. ADD THE MISSING GUARD ARM FOR ThreeWayMerge.
//      ConflictResolverTests has Resolve_MatchingRevisions_ShortCircuits_EvenWithDivergedFields,
//      which pins the short-circuit as INTENDED so a future reader cannot mistake it for a bug
//      and "fix" it. ThreeWayMergeTests has no equivalent. It should. Copy the shape.
//
//   3. IF A THIRD RESOLVER APPEARS, this stops being a two-call-site helper and the case for
//      enforcing it structurally gets stronger. Consider whether the resolvers should share a
//      single revision-precondition guard in production code rather than each re-implementing
//      the short-circuit — at that point the test-side fixture is treating a symptom.
//
// WHAT NOT TO DO: do not relax Diverged's equality check to make a stubborn test pass. The
// check failing means the fixture is wrong, or MarkSynced is. Both are real defects and both
// are cheaper to fix here than to discover from a green suite that was never testing anything.
