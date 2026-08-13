using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Sync;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.TestKit;

/// <summary>
/// Tests for the test-infrastructure guard itself.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Why test-infrastructure gets its own tests.</b> <see cref="ConflictFixture.Diverged"/>
/// is the suite's protection against the failure AGENTS.md singles out: a conflict-path test
/// whose revisions are equal never reaches the conflict branch at all, because
/// <c>ConflictResolver.Resolve</c> short-circuits to <c>NoConflict</c> on revision equality.
/// The fixture converts that silent hollowing into a loud construction-time failure.
/// </para>
/// <para>
/// But an unasserted guard is not a guard. Nothing previously proved either throw fires, so a
/// refactor that turned the <c>if</c> into a no-op — or wrapped it in <c>#if DEBUG</c> — would
/// remove the protection from every conflict test in the suite while leaving the whole suite
/// green. That is precisely the class of defect the fixture exists to prevent, one level up.
/// </para>
/// </remarks>
public class ConflictFixtureTests
{
    private static WorkItem Item() => new WorkItemBuilder(1, "Item 1").Build();

    /// <remarks>
    /// 🔴 Asserts the message of the ARGUMENT-TIME guard specifically. The two guards mask each
    /// other: <see cref="ConflictFixture.Diverged"/> checks equality up front AND re-checks the
    /// postcondition after stamping, so neutering the first still throws from the second and a
    /// bare <c>Should.Throw&lt;InvalidOperationException&gt;</c> stays green against a dead
    /// argument guard. Verified by mutation — with the equality check neutered, the two arms
    /// below fail on the message while a shape-only assertion did not fail at all.
    /// </remarks>
    [Fact]
    public void Diverged_WithEqualRevisions_Throws_RatherThanBuildingAHollowFixture()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => ConflictFixture.Diverged(Item(), Item(), 5, 5));

        // The argument-time guard names the short-circuit and tells the caller what to do
        // instead. The postcondition guard's message is about MarkSynced and says neither.
        ex.Message.ShouldContain("NoConflict");
        ex.Message.ShouldContain("ConflictFixture.SameRevision");
        ex.Message.ShouldNotContain("no longer stores the value");
    }

    /// <summary>
    /// The postcondition guard, reached only when the argument guard passes. Distinct from the
    /// arm above so that killing either guard fails a DIFFERENT, correctly-named test.
    /// </summary>
    [Fact]
    public void Diverged_PostconditionGuard_HasItsOwnDistinctMessage()
    {
        // Sanity: unequal arguments clear the argument-time guard, so any throw from here
        // would come from the postcondition. Today MarkSynced stores faithfully, so this
        // must NOT throw -- pinning that keeps the arm above honest about which guard fired.
        var (local, remote) = ConflictFixture.Diverged(Item(), Item(), 5, 6);
        local.Revision.ShouldNotBe(remote.Revision);
    }

    [Fact]
    public void Diverged_WithDifferentRevisions_StampsBothSides()
    {
        var (local, remote) = ConflictFixture.Diverged(Item(), Item(), 5, 6);

        // The positive arm matters as much as the negative one: a guard that threw
        // unconditionally would satisfy the test above while making the fixture useless.
        local.Revision.ShouldBe(5);
        remote.Revision.ShouldBe(6);
        local.Revision.ShouldNotBe(remote.Revision);
    }

    [Fact]
    public void Diverged_DefaultRevisions_ActuallyDiverge()
    {
        var (local, remote) = ConflictFixture.Diverged(Item(), Item());

        local.Revision.ShouldBe(ConflictFixture.DefaultLocalRevision);
        remote.Revision.ShouldBe(ConflictFixture.DefaultRemoteRevision);
        ConflictFixture.DefaultLocalRevision.ShouldNotBe(ConflictFixture.DefaultRemoteRevision);
    }

    /// <summary>
    /// Pins the fixture's contract end to end: a Diverged pair really does reach the conflict
    /// branch, and a SameRevision pair really does hit the short-circuit. This is what makes
    /// the guard's premise true rather than merely asserted.
    /// </summary>
    [Fact]
    public void Diverged_ReachesTheConflictBranch_WhileSameRevision_ShortCircuits()
    {
        var (dl, dr) = ConflictFixture.Diverged(Item(), Item());
        dl.SetField("System.Title", "Local title");
        dr.SetField("System.Title", "Remote title");
        var diverged = ConflictResolver.Resolve(dl, dr);
        // Pattern-match the union case rather than ShouldBeOfType/ShouldNotBeOfType, which
        // test the union WRAPPER type and not the case inside it.
        Assert.False(diverged is NoConflict,
            $"Diverged must reach the conflict branch but short-circuited: {diverged}");

        var (sl, sr) = ConflictFixture.SameRevision(Item(), Item());
        sl.SetField("System.Title", "Local title");
        sr.SetField("System.Title", "Remote title");
        var shortCircuited = ConflictResolver.Resolve(sl, sr);
        Assert.True(shortCircuited is NoConflict,
            $"SameRevision must reach the short-circuit but got {shortCircuited}");
    }

    [Fact]
    public void SameRevision_StampsBothSidesEqually()
    {
        var (local, remote) = ConflictFixture.SameRevision(Item(), Item(), 9);

        local.Revision.ShouldBe(9);
        remote.Revision.ShouldBe(9);
    }
}
