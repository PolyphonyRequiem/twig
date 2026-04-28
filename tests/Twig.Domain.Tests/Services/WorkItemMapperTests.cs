using System.Reflection;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="WorkItemMapper"/>.
/// Verifies snapshot → WorkItem aggregate conversion with value object parsing.
/// </summary>
public sealed class WorkItemMapperTests
{
    private readonly WorkItemMapper _mapper = new();

    [Fact]
    public void Map_BasicSnapshot_MapsAllProperties()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 42,
            Revision = 5,
            TypeName = "User Story",
            Title = "Implement login",
            State = "Active",
            AssignedTo = "John Doe",
            IterationPath = @"MyProject\Sprint 1",
            AreaPath = @"MyProject\Backend",
            ParentId = 10,
        };

        var result = _mapper.Map(snapshot);

        result.Id.ShouldBe(42);
        result.Revision.ShouldBe(5);
        result.Type.ShouldBe(WorkItemType.UserStory);
        result.Title.ShouldBe("Implement login");
        result.State.ShouldBe("Active");
        result.AssignedTo.ShouldBe("John Doe");
        result.IterationPath.Value.ShouldBe(@"MyProject\Sprint 1");
        result.AreaPath.Value.ShouldBe(@"MyProject\Backend");
        result.ParentId.ShouldBe(10);
        result.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Map_WithRevision_MarksSynced()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 7,
            TypeName = "Task",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Revision.ShouldBe(7);
        result.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Map_ZeroRevision_DoesNotMarkSynced()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 0,
            TypeName = "Task",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Revision.ShouldBe(0);
    }

    [Fact]
    public void Map_IsDirtyTrue_RestoresDirtyFlag()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 3,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            IsDirty = true,
        };

        var result = _mapper.Map(snapshot);

        result.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Map_WithFields_ImportsFields()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["System.Description"] = "Some description",
        };
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            Fields = fields,
        };

        var result = _mapper.Map(snapshot);

        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
        result.Fields.ShouldContainKey("System.Description");
        result.Fields["System.Description"].ShouldBe("Some description");
    }

    [Fact]
    public void Map_EmptyTypeName_FallsBackToTask()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Type.ShouldBe(WorkItemType.Task);
    }

    [Fact]
    public void Map_NullIterationPath_ReturnsDefaultIterationPath()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Bug",
            Title = "Test",
            State = "New",
            IterationPath = null,
        };

        var result = _mapper.Map(snapshot);

        result.IterationPath.ShouldBe(default(IterationPath));
    }

    [Fact]
    public void Map_NullAreaPath_ReturnsDefaultAreaPath()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Bug",
            Title = "Test",
            State = "New",
            AreaPath = null,
        };

        var result = _mapper.Map(snapshot);

        result.AreaPath.ShouldBe(default(AreaPath));
    }

    [Fact]
    public void Map_SeedProperties_Preserved()
    {
        var createdAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var syncedAt = new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 0,
            TypeName = "Task",
            Title = "Seed item",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = createdAt,
            LastSyncedAt = syncedAt,
        };

        var result = _mapper.Map(snapshot);

        result.IsSeed.ShouldBeTrue();
        result.SeedCreatedAt.ShouldBe(createdAt);
        result.LastSyncedAt.ShouldBe(syncedAt);
    }

    [Fact]
    public void Map_NullAssignedTo_PreservesNull()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            AssignedTo = null,
        };

        var result = _mapper.Map(snapshot);

        result.AssignedTo.ShouldBeNull();
    }

    [Fact]
    public void Map_CustomWorkItemType_PreservesTypeName()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Change Request",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Type.Value.ShouldBe("Change Request");
    }

    [Fact]
    public void Map_DirtyAfterSynced_SetsIsDirtyTrue()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 5,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            IsDirty = true,
        };

        var result = _mapper.Map(snapshot);

        // MarkSynced clears dirty, then SetDirty re-enables it
        result.IsDirty.ShouldBeTrue();
        result.Revision.ShouldBe(5);
    }

    // ── Property-preservation theory ────────────────────────────────

    [Fact]
    public void Map_PopulatesAllInitOnlyProperties()
    {
        // Discover all init-only properties on WorkItem via reflection.
        // If a new init property is added to WorkItem, this test fails —
        // prompting the developer to update WorkItemSnapshot, WorkItemMapper, and this list.
        var initPropertyNames = typeof(WorkItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(IsInitOnly)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        initPropertyNames.ShouldBe(
        [
            "AreaPath",
            "AssignedTo",
            "Id",
            "IsSeed",
            "IterationPath",
            "LastSyncedAt",
            "ParentId",
            "SeedCreatedAt",
            "Title",
            "Type",
        ]);

        // Map a fully-populated snapshot and verify each init property was set
        var snapshot = new WorkItemSnapshot
        {
            Id = 99,
            Revision = 7,
            TypeName = "Epic",
            Title = "Theory Test Item",
            State = "Active",
            AssignedTo = "Test User",
            IterationPath = @"Project\Sprint 1",
            AreaPath = @"Project\Team A",
            ParentId = 42,
            IsSeed = true,
            SeedCreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSyncedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };

        var result = _mapper.Map(snapshot);

        result.Id.ShouldBe(99);
        result.Type.ShouldBe(WorkItemType.Epic);
        result.Title.ShouldBe("Theory Test Item");
        result.AssignedTo.ShouldBe("Test User");
        result.IterationPath.Value.ShouldBe(@"Project\Sprint 1");
        result.AreaPath.Value.ShouldBe(@"Project\Team A");
        result.ParentId.ShouldBe(42);
        result.IsSeed.ShouldBeTrue();
        result.SeedCreatedAt.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        result.LastSyncedAt.ShouldBe(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
    }

    // ── Round-trip validation ───────────────────────────────────────

    [Fact]
    public void Map_FullyPopulatedSnapshot_RoundTripPreservesAllValues()
    {
        var seedCreated = new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);
        var lastSynced = new DateTimeOffset(2026, 3, 16, 8, 0, 0, TimeSpan.Zero);
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["System.Description"] = "Full round-trip description",
            ["Custom.NullField"] = null,
        };

        var snapshot = new WorkItemSnapshot
        {
            Id = 100,
            Revision = 12,
            TypeName = "User Story",
            Title = "Round-Trip Test Story",
            State = "Resolved",
            AssignedTo = "Jane Smith",
            IterationPath = @"MyProject\Release 2\Sprint 5",
            AreaPath = @"MyProject\Backend\API",
            ParentId = 50,
            IsSeed = true,
            SeedCreatedAt = seedCreated,
            LastSyncedAt = lastSynced,
            IsDirty = true,
            Fields = fields,
        };

        var result = _mapper.Map(snapshot);

        // Identity & metadata
        result.Id.ShouldBe(100);
        result.Type.ShouldBe(WorkItemType.UserStory);
        result.Title.ShouldBe("Round-Trip Test Story");
        result.State.ShouldBe("Resolved");
        result.AssignedTo.ShouldBe("Jane Smith");
        result.IterationPath.Value.ShouldBe(@"MyProject\Release 2\Sprint 5");
        result.AreaPath.Value.ShouldBe(@"MyProject\Backend\API");
        result.ParentId.ShouldBe(50);

        // Revision & sync state
        result.Revision.ShouldBe(12);
        result.IsDirty.ShouldBeTrue();

        // Seed properties
        result.IsSeed.ShouldBeTrue();
        result.SeedCreatedAt.ShouldBe(seedCreated);
        result.LastSyncedAt.ShouldBe(lastSynced);

        // Fields — including null value preservation
        result.Fields.Count.ShouldBe(3);
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
        result.Fields["System.Description"].ShouldBe("Full round-trip description");
        result.Fields.ShouldContainKey("Custom.NullField");
        result.Fields["Custom.NullField"].ShouldBeNull();
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Fact]
    public void Map_NullFieldsDictionary_Throws()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            Fields = null!,
        };

        Should.Throw<NullReferenceException>(() => _mapper.Map(snapshot));
    }

    [Fact]
    public void Map_NullTypeName_FallsBackToTask()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = null!,
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Type.ShouldBe(WorkItemType.Task);
    }

    [Fact]
    public void Map_WhitespaceTypeName_FallsBackToTask()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "   ",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Type.ShouldBe(WorkItemType.Task);
    }

    [Fact]
    public void Map_EmptyStringIterationPath_ReturnsDefault()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            IterationPath = "",
        };

        var result = _mapper.Map(snapshot);

        result.IterationPath.ShouldBe(default(IterationPath));
    }

    [Fact]
    public void Map_EmptyStringAreaPath_ReturnsDefault()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            AreaPath = "",
        };

        var result = _mapper.Map(snapshot);

        result.AreaPath.ShouldBe(default(AreaPath));
    }

    [Fact]
    public void Map_EmptyFieldsDictionary_ProducesEmptyFields()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            Fields = new Dictionary<string, string?>(),
        };

        var result = _mapper.Map(snapshot);

        result.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void Map_UnknownTypeName_PreservesAsCustomType()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Completely Unknown Widget",
            Title = "Test",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Type.Value.ShouldBe("Completely Unknown Widget");
    }

    [Fact]
    public void Map_ZeroId_IsPreserved()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 0,
            Revision = 0,
            TypeName = "Task",
            Title = "Zero ID item",
            State = "New",
        };

        var result = _mapper.Map(snapshot);

        result.Id.ShouldBe(0);
    }

    [Fact]
    public void Map_NullParentId_PreservesNull()
    {
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 1,
            TypeName = "Task",
            Title = "Test",
            State = "New",
            ParentId = null,
        };

        var result = _mapper.Map(snapshot);

        result.ParentId.ShouldBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static bool IsInitOnly(PropertyInfo property)
    {
        var setter = property.SetMethod;
        if (setter is null) return false;
        return setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }
}
