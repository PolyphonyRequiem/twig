using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services.Reconciliation;
using Twig.Domain.Services.Sync;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Reconciliation;

/// <summary>
/// Wayfinder 0004 slice 3 — the published → reconciled transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixture precondition, asserted explicitly in every conflict-path test.</b> Both
/// <see cref="ConflictResolver.Resolve"/> and <see cref="ThreeWayMerge.Resolve"/> short-circuit
/// to <see cref="NoConflict"/> when <c>local.Revision == remote.Revision</c>, and a freshly
/// constructed <see cref="WorkItem"/> is <c>Revision = 0</c> on BOTH sides. A conflict-path test
/// that forgets <c>remote.MarkSynced(n)</c> therefore exercises the happy path and passes
/// vacuously. AGENTS.md requires the precondition be asserted so a future setup regression
/// cannot silently hollow this file out.
/// </para>
/// <para>
/// <see cref="MergeResult"/> is a <c>union</c>: pattern-match the case
/// (<c>result is HasConflicts</c>). <c>ShouldBeOfType&lt;HasConflicts&gt;()</c> fails against
/// the wrapper type.
/// </para>
/// </remarks>
public class ThreeWayMergeTests
{
    private static PendingChangeRecord FieldEdit(string field, string? oldValue, string? newValue) =>
        new(WorkItemId: 1, ChangeType: "field", FieldName: field, OldValue: oldValue, NewValue: newValue);

    /// <summary>Asserts the divergent-revision precondition the merge branch depends on.</summary>
    private static void AssertConflictPathReachable(WorkItem local, WorkItem remote)
    {
        local.Revision.ShouldNotBe(
            remote.Revision,
            "fixture must advance the remote revision or Resolve short-circuits to NoConflict " +
            "and the branch under test never runs");
    }

    // ═══════════════════════════════════════════════════════════════
    //  The defect this slice exists to fix
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// THE regression test for wayfinder 0004 / 0006. Remote moved a field the user never
    /// touched. Two-way <see cref="ConflictResolver"/> must call this a conflict — it has no
    /// base and says so in its own code. Three-way must auto-merge it.
    /// </summary>
    [Fact]
    public void RemoteOnlyChange_OnUnstagedField_IsNotAConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "as last synced");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Description", "changed remotely");

        AssertConflictPathReachable(local, remote);

        // Positive control: the two-way resolver flags this precisely because it has no base.
        (ConflictResolver.Resolve(local, remote) is HasConflicts).ShouldBeTrue(
            "two-way Resolve has no merge base, so it must conservatively flag this");

        // The user staged nothing, so the local side did not move.
        var result = ThreeWayMerge.Resolve(local, remote, MergeBase.Empty);

        if (result is not AutoMergeable merged) { Assert.Fail($"Expected AutoMergeable but got {result}"); return; }
        merged.MergedFields.ShouldContain("System.Description");
    }

    /// <summary>
    /// The cache mirror never carries the user's edit — staging writes it to pending_changes and
    /// only stamps <c>_edited</c> on the aggregate. So the local intent must come from the base,
    /// and the conflict must be reported against that intent, not the stale mirror value.
    /// </summary>
    [Fact]
    public void BothSidesMoved_ConflictsOnStagedIntent_NotTheStaleMirror()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("Custom.Priority", "P3"); // the mirror: value at last sync

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("Custom.Priority", "P1");

        AssertConflictPathReachable(local, remote);

        // The user staged P3 → P2. Remote independently moved P3 → P1.
        var mergeBase = MergeBase.FromPendingChanges([FieldEdit("Custom.Priority", "P3", "P2")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        if (result is not HasConflicts conflicts) { Assert.Fail($"Expected HasConflicts but got {result}"); return; }
        var conflict = conflicts.ConflictingFields.ShouldHaveSingleItem();
        conflict.FieldName.ShouldBe("Custom.Priority");
        conflict.LocalValue.ShouldBe("P2", "the conflict must report the user's staged intent");
        conflict.RemoteValue.ShouldBe("P1");
    }

    [Fact]
    public void LocalOnlyChange_RemoteStillAtBase_IsNotAConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("Custom.Priority", "P3");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6); // revision moved (some other field), value did not
        remote.SetField("Custom.Priority", "P3");

        AssertConflictPathReachable(local, remote);

        var mergeBase = MergeBase.FromPendingChanges([FieldEdit("Custom.Priority", "P3", "P2")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        (result is NoConflict).ShouldBeTrue(
            $"remote never moved off the base, so the staged edit stands — got {result}");
    }

    [Fact]
    public void ConvergentEdit_BothSidesMovedToSameValue_IsNotAConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("Custom.Priority", "P3");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("Custom.Priority", "P1");

        AssertConflictPathReachable(local, remote);

        // Both sides independently moved P3 → P1. Nothing to choose between.
        var mergeBase = MergeBase.FromPendingChanges([FieldEdit("Custom.Priority", "P3", "P1")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        (result is NoConflict).ShouldBeTrue($"expected NoConflict but got {result}");
    }

    [Fact]
    public void SameRevision_ShortCircuits_EvenWithStagedEdits()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(5);

        var mergeBase = MergeBase.FromPendingChanges([FieldEdit("Custom.Priority", "P3", "P2")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        (result is NoConflict).ShouldBeTrue($"expected NoConflict but got {result}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  First-class properties — init-only, so the base is the ONLY local intent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RemoteOnlyTitleChange_WithNoStagedTitleEdit_IsNotAConflict()
    {
        var local = new WorkItemBuilder(1, "As last synced").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Renamed remotely").Build();
        remote.MarkSynced(6);

        AssertConflictPathReachable(local, remote);

        // Positive control: two-way flags the title divergence.
        (ConflictResolver.Resolve(local, remote) is HasConflicts).ShouldBeTrue();

        var result = ThreeWayMerge.Resolve(local, remote, MergeBase.Empty);

        if (result is not AutoMergeable merged) { Assert.Fail($"Expected AutoMergeable but got {result}"); return; }
        merged.MergedFields.ShouldContain("System.Title");
    }

    [Fact]
    public void BothSidesRenamed_IsAConflict()
    {
        var local = new WorkItemBuilder(1, "Original").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Renamed remotely").Build();
        remote.MarkSynced(6);

        AssertConflictPathReachable(local, remote);

        var mergeBase = MergeBase.FromPendingChanges(
            [FieldEdit("System.Title", "Original", "Renamed locally")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        if (result is not HasConflicts conflicts) { Assert.Fail($"Expected HasConflicts but got {result}"); return; }
        var conflict = conflicts.ConflictingFields.ShouldHaveSingleItem();
        conflict.FieldName.ShouldBe("System.Title");
        conflict.LocalValue.ShouldBe("Renamed locally");
        conflict.RemoteValue.ShouldBe("Renamed remotely");
    }

    /// <summary>
    /// Regression: a staged edit to a first-class property is recorded in pending_changes under
    /// the same canonical name (<c>System.Title</c>), so it is reachable both as a property and
    /// via <see cref="MergeBase.StagedFields"/>. Classifying both ways reported ONE divergence
    /// as TWO conflicts — the second against an absent Fields entry, with an empty remote value,
    /// which would have shown the user a phantom "remote cleared the title" conflict.
    /// </summary>
    [Fact]
    public void FirstClassProperty_IsClassifiedExactlyOnce()
    {
        var local = new WorkItemBuilder(1, "Original").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Renamed remotely").Build();
        remote.MarkSynced(6);

        AssertConflictPathReachable(local, remote);

        var mergeBase = MergeBase.FromPendingChanges(
            [FieldEdit("System.Title", "Original", "Renamed locally")]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        if (result is not HasConflicts conflicts) { Assert.Fail($"Expected HasConflicts but got {result}"); return; }
        conflicts.ConflictingFields
            .Count(c => string.Equals(c.FieldName, "System.Title", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1, "one divergence must not be reported as two conflicts");
    }

    // ═══════════════════════════════════════════════════════════════
    //  MergeBase projection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MergeBase_NoteRows_AreExcluded()
    {
        var mergeBase = MergeBase.FromPendingChanges(
        [
            new PendingChangeRecord(1, "note", null, null, "a comment"),
            new PendingChangeRecord(1, "add_note", null, null, "legacy comment"),
        ]);

        mergeBase.IsEmpty.ShouldBeTrue("a note is an append and can never conflict with a field");
    }

    [Fact]
    public void MergeBase_LegacyFieldChangeTypes_AreHonoured()
    {
        var mergeBase = MergeBase.FromPendingChanges(
        [
            new PendingChangeRecord(1, "set_field", "Custom.A", "old", "new"),
            new PendingChangeRecord(1, "state", "System.State", "Active", "Closed"),
        ]);

        mergeBase.For("Custom.A").ShouldBe(new FieldMergeBase("old", "new"));
        mergeBase.For("System.State").ShouldBe(new FieldMergeBase("Active", "Closed"));
    }

    /// <summary>
    /// Repeated edits to one field: the FIRST row's OldValue is the base (the value at last
    /// sync); the LAST row's NewValue is the current intent. Collapsing to the latest row alone
    /// would move the base onto the user's own intermediate edit, making a genuine remote
    /// divergence look like agreement.
    /// </summary>
    [Fact]
    public void MergeBase_RepeatedEdits_KeepEarliestBase_AndLatestIntent()
    {
        var mergeBase = MergeBase.FromPendingChanges(
        [
            FieldEdit("Custom.Priority", "P3", "P2"),
            FieldEdit("Custom.Priority", "P2", "P1"),
        ]);

        mergeBase.For("Custom.Priority").ShouldBe(new FieldMergeBase("P3", "P1"));
    }

    /// <summary>
    /// The consequence of the rule above, at the merge level: with the base correctly held at
    /// P3, a remote that also moved off P3 is a real conflict. Had the base drifted to P2, the
    /// remote's P2 would have looked like agreement and the user's P1 would have been lost.
    /// </summary>
    [Fact]
    public void RepeatedEdits_DoNotHideARemoteDivergence()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("Custom.Priority", "P3");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("Custom.Priority", "P2");

        AssertConflictPathReachable(local, remote);

        var mergeBase = MergeBase.FromPendingChanges(
        [
            FieldEdit("Custom.Priority", "P3", "P2"),
            FieldEdit("Custom.Priority", "P2", "P1"),
        ]);

        var result = ThreeWayMerge.Resolve(local, remote, mergeBase);

        if (result is not HasConflicts conflicts) { Assert.Fail($"Expected HasConflicts but got {result}"); return; }
        var conflict = conflicts.ConflictingFields.ShouldHaveSingleItem();
        conflict.LocalValue.ShouldBe("P1");
        conflict.RemoteValue.ShouldBe("P2");
    }

    [Fact]
    public void MergeBase_RowWithNoFieldName_IsIgnored()
    {
        var mergeBase = MergeBase.FromPendingChanges(
            [new PendingChangeRecord(1, "field", null, "old", "new")]);

        mergeBase.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void MergeBase_Empty_HasNoStagedFields()
    {
        MergeBase.Empty.IsEmpty.ShouldBeTrue();
        MergeBase.Empty.StagedFields.ShouldBeEmpty();
        MergeBase.Empty.For("anything").ShouldBeNull();
    }
}
