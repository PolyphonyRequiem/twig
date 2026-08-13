using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

public class ConflictResolverTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Same revision — NoConflict
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_SameRevision_NoConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(5);

        var result = ConflictResolver.Resolve(local, remote);

        Assert.True(result is NoConflict, $"Expected NoConflict but got {result}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Disjoint field changes — AutoMergeable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DisjointFieldChanges_AutoMergeable()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "Updated locally");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Title", "Updated remotely");

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not AutoMergeable merged) { Assert.Fail("Expected AutoMergeable"); return; }
        merged.MergedFields.Count.ShouldBe(2);
        merged.MergedFields.ShouldContain("System.Description");
        merged.MergedFields.ShouldContain("System.Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Overlapping same value — no conflict
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_OverlappingSameValue_NoConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "Same value");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Same value");

        var result = ConflictResolver.Resolve(local, remote);

        // Same field, same value — treated as NoConflict
        Assert.True(result is NoConflict, $"Expected NoConflict but got {result}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Overlapping different values — HasConflicts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_OverlappingDifferentValues_HasConflicts()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "Local value");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote value");

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.Count.ShouldBe(1);
        conflicts.ConflictingFields[0].FieldName.ShouldBe("System.Description");
        conflicts.ConflictingFields[0].LocalValue.ShouldBe("Local value");
        conflicts.ConflictingFields[0].RemoteValue.ShouldBe("Remote value");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple conflicts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_MultipleConflicts()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "Local desc");
        local.SetField("System.Title", "Local title");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");
        remote.SetField("System.Title", "Remote title");

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.Count.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mixed: some auto-merge, some conflict
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_MixedAutoMergeAndConflict_ReportsConflicts()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);
        local.SetField("System.Description", "Local desc");
        local.SetField("Local.Only", "local-only-value");

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");
        remote.SetField("Remote.Only", "remote-only-value");

        var result = ConflictResolver.Resolve(local, remote);

        // Even though some fields are auto-mergeable, conflicts take precedence
        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.Count.ShouldBe(1);
        conflicts.ConflictingFields[0].FieldName.ShouldBe("System.Description");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Different revisions, no field changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DifferentRevisions_NoFieldChanges_NoConflict()
    {
        var local = new WorkItemBuilder(1, "Item 1").Build();
        local.MarkSynced(5);

        var remote = new WorkItemBuilder(1, "Item 1").Build();
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        Assert.True(result is NoConflict, $"Expected NoConflict but got {result}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  First-class property conflict detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_StateDiffers_HasConflicts()
    {
        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "Active" };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "Resolved" };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.State");
        conflicts.ConflictingFields.First(f => f.FieldName == "System.State").LocalValue.ShouldBe("Active");
        conflicts.ConflictingFields.First(f => f.FieldName == "System.State").RemoteValue.ShouldBe("Resolved");
    }

    [Fact]
    public void Resolve_TitleDiffers_HasConflicts()
    {
        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Local Title", State = "New" };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Remote Title", State = "New" };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.Title");
    }

    [Fact]
    public void Resolve_AssignedToDiffers_HasConflicts()
    {
        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", AssignedTo = "alice" };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", AssignedTo = "bob" };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.AssignedTo");
    }

    [Fact]
    public void Resolve_IterationPathDiffers_HasConflicts()
    {
        var iterA = IterationPath.Parse("Project\\Sprint 1").Value;
        var iterB = IterationPath.Parse("Project\\Sprint 2").Value;

        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", IterationPath = iterA };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", IterationPath = iterB };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.IterationPath");
    }

    [Fact]
    public void Resolve_DifferentRevisions_AllFirstClassPropertiesMatch_NoConflict()
    {
        var iter = IterationPath.Parse("Project\\Sprint 1").Value;
        var area = AreaPath.Parse("Project\\Team").Value;

        var local = new WorkItem
        {
            Id = 1, Type = WorkItemType.Task, Title = "Same Title", State = "Active",
            AssignedTo = "alice", IterationPath = iter, AreaPath = area, ParentId = 10
        };
        local.MarkSynced(5);

        var remote = new WorkItem
        {
            Id = 1, Type = WorkItemType.Task, Title = "Same Title", State = "Active",
            AssignedTo = "alice", IterationPath = iter, AreaPath = area, ParentId = 10
        };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        Assert.True(result is NoConflict, $"Expected NoConflict but got {result}");
    }

    [Fact]
    public void Resolve_ParentIdDiffers_HasConflicts()
    {
        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", ParentId = 10 };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", ParentId = 20 };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        if (result is not HasConflicts conflicts) { Assert.Fail("Expected HasConflicts"); return; }
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.Parent");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Revision-equality short-circuit — PINNED AS INTENDED BEHAVIOUR
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pins the revision-equality short-circuit as DELIBERATE, not a defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Resolve</c> returns <c>NoConflict</c> on <c>local.Revision == remote.Revision</c>
    /// before comparing a single field. Read cold, that looks like a bug: the two items below
    /// disagree on three separate fields and the resolver reports no conflict. It is not a bug —
    /// equal revisions mean the two sides are the same version of the item, so any value
    /// divergence is local edits against a still-current base, which is not a conflict.
    /// </para>
    /// <para>
    /// This arm exists so that reading is available to whoever finds the short-circuit next.
    /// Without it, a well-meaning change that "fixes" the early return would turn every
    /// unsynced local edit into a phantom conflict, and no existing test would catch it —
    /// every other arm in this file diverges the revisions and so never exercises the branch.
    /// </para>
    /// <para>
    /// The mirror hazard is the fixture that lands here BY ACCIDENT. A freshly constructed
    /// <c>WorkItem</c> has <c>Revision = 0</c> on both sides, so a conflict test that forgets
    /// to advance the remote revision short-circuits here and passes while proving nothing.
    /// <c>Twig.TestKit.ConflictFixture.Diverged</c> exists to make that unconstructable;
    /// <c>ConflictFixture.SameRevision</c> names the intentional case, as used below.
    /// </para>
    /// <para>
    /// 🔴 <c>ThreeWayMerge.Resolve</c> carries the identical short-circuit and has no
    /// equivalent arm. See the extension points in <c>ConflictFixture</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Resolve_MatchingRevisions_ShortCircuits_EvenWithDivergedFields()
    {
        var (local, remote) = ConflictFixture.SameRevision(
            new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Local Title", State = "Active", AssignedTo = "alice" },
            new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Remote Title", State = "Resolved", AssignedTo = "bob" });

        local.SetField("System.Description", "Local value");
        remote.SetField("System.Description", "Remote value");

        // Precondition, asserted rather than assumed: the revisions really are equal, and the
        // values really do diverge. If either stopped holding, this arm would still pass while
        // testing nothing — which is the exact failure mode it was written to prevent.
        local.Revision.ShouldBe(remote.Revision);
        local.Title.ShouldNotBe(remote.Title);
        local.State.ShouldNotBe(remote.State);

        var result = ConflictResolver.Resolve(local, remote);

        Assert.True(
            result is NoConflict,
            $"Equal revisions must short-circuit to NoConflict regardless of field divergence, but got {result}. " +
            "If this arm now fails, the short-circuit was removed or reordered — that is a behaviour change, " +
            "not a test defect. Every unsynced local edit becomes a phantom conflict without it.");
    }

}
