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
}
