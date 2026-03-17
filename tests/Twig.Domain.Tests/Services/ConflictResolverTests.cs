using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ConflictResolverTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Same revision — NoConflict
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_SameRevision_NoConflict()
    {
        var local = MakeItem(1);
        local.MarkSynced(5);

        var remote = MakeItem(1);
        remote.MarkSynced(5);

        var result = ConflictResolver.Resolve(local, remote);

        result.ShouldBeOfType<MergeResult.NoConflict>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Disjoint field changes — AutoMergeable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DisjointFieldChanges_AutoMergeable()
    {
        var local = MakeItem(1);
        local.MarkSynced(5);
        local.SetField("System.Description", "Updated locally");

        var remote = MakeItem(1);
        remote.MarkSynced(6);
        remote.SetField("System.Title", "Updated remotely");

        var result = ConflictResolver.Resolve(local, remote);

        var merged = result.ShouldBeOfType<MergeResult.AutoMergeable>();
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
        var local = MakeItem(1);
        local.MarkSynced(5);
        local.SetField("System.Description", "Same value");

        var remote = MakeItem(1);
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Same value");

        var result = ConflictResolver.Resolve(local, remote);

        // Same field, same value — treated as NoConflict
        result.ShouldBeOfType<MergeResult.NoConflict>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Overlapping different values — HasConflicts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_OverlappingDifferentValues_HasConflicts()
    {
        var local = MakeItem(1);
        local.MarkSynced(5);
        local.SetField("System.Description", "Local value");

        var remote = MakeItem(1);
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote value");

        var result = ConflictResolver.Resolve(local, remote);

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
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
        var local = MakeItem(1);
        local.MarkSynced(5);
        local.SetField("System.Description", "Local desc");
        local.SetField("System.Title", "Local title");

        var remote = MakeItem(1);
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");
        remote.SetField("System.Title", "Remote title");

        var result = ConflictResolver.Resolve(local, remote);

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
        conflicts.ConflictingFields.Count.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mixed: some auto-merge, some conflict
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_MixedAutoMergeAndConflict_ReportsConflicts()
    {
        var local = MakeItem(1);
        local.MarkSynced(5);
        local.SetField("System.Description", "Local desc");
        local.SetField("Local.Only", "local-only-value");

        var remote = MakeItem(1);
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");
        remote.SetField("Remote.Only", "remote-only-value");

        var result = ConflictResolver.Resolve(local, remote);

        // Even though some fields are auto-mergeable, conflicts take precedence
        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
        conflicts.ConflictingFields.Count.ShouldBe(1);
        conflicts.ConflictingFields[0].FieldName.ShouldBe("System.Description");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Different revisions, no field changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DifferentRevisions_NoFieldChanges_NoConflict()
    {
        var local = MakeItem(1);
        local.MarkSynced(5);

        var remote = MakeItem(1);
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        result.ShouldBeOfType<MergeResult.NoConflict>();
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

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
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

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
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

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
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

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
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

        result.ShouldBeOfType<MergeResult.NoConflict>();
    }

    [Fact]
    public void Resolve_ParentIdDiffers_HasConflicts()
    {
        var local = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", ParentId = 10 };
        local.MarkSynced(5);

        var remote = new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New", ParentId = 20 };
        remote.MarkSynced(6);

        var result = ConflictResolver.Resolve(local, remote);

        var conflicts = result.ShouldBeOfType<MergeResult.HasConflicts>();
        conflicts.ConflictingFields.ShouldContain(f => f.FieldName == "System.Parent");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(int id)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = $"Item {id}",
            State = "New",
        };
    }
}
