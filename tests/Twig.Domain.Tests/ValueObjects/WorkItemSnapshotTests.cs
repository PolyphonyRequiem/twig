using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class WorkItemSnapshotTests
{
    [Fact]
    public void Construction_DefaultValues_SetsExpectedDefaults()
    {
        var snapshot = new WorkItemSnapshot();

        snapshot.Id.ShouldBe(0);
        snapshot.Revision.ShouldBe(0);
        snapshot.TypeName.ShouldBe(string.Empty);
        snapshot.Title.ShouldBe(string.Empty);
        snapshot.State.ShouldBe(string.Empty);
        snapshot.AssignedTo.ShouldBeNull();
        snapshot.IterationPath.ShouldBeNull();
        snapshot.AreaPath.ShouldBeNull();
        snapshot.ParentId.ShouldBeNull();
        snapshot.IsSeed.ShouldBeFalse();
        snapshot.SeedCreatedAt.ShouldBeNull();
        snapshot.LastSyncedAt.ShouldBeNull();
        snapshot.IsDirty.ShouldBeFalse();
        snapshot.Fields.ShouldNotBeNull();
        snapshot.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void Construction_AllProperties_SetsValues()
    {
        var seedCreated = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var lastSynced = new DateTimeOffset(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        var fields = new Dictionary<string, string?>
        {
            ["System.Description"] = "A description",
            ["Custom.Priority"] = "High",
            ["Custom.Optional"] = null,
        };

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
            IsSeed = true,
            SeedCreatedAt = seedCreated,
            LastSyncedAt = lastSynced,
            IsDirty = true,
            Fields = fields,
        };

        snapshot.Id.ShouldBe(42);
        snapshot.Revision.ShouldBe(5);
        snapshot.TypeName.ShouldBe("User Story");
        snapshot.Title.ShouldBe("Implement login");
        snapshot.State.ShouldBe("Active");
        snapshot.AssignedTo.ShouldBe("John Doe");
        snapshot.IterationPath.ShouldBe(@"MyProject\Sprint 1");
        snapshot.AreaPath.ShouldBe(@"MyProject\Backend");
        snapshot.ParentId.ShouldBe(10);
        snapshot.IsSeed.ShouldBeTrue();
        snapshot.SeedCreatedAt.ShouldBe(seedCreated);
        snapshot.LastSyncedAt.ShouldBe(lastSynced);
        snapshot.IsDirty.ShouldBeTrue();
        snapshot.Fields.Count.ShouldBe(3);
        snapshot.Fields["System.Description"].ShouldBe("A description");
        snapshot.Fields["Custom.Priority"].ShouldBe("High");
        snapshot.Fields["Custom.Optional"].ShouldBeNull();
    }

    [Fact]
    public void Fields_DefaultsToEmptyDictionary_NotNull()
    {
        var snapshot = new WorkItemSnapshot { Id = 1 };

        snapshot.Fields.ShouldNotBeNull();
        snapshot.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void Fields_WithNullValue_Preserved()
    {
        var snapshot = new WorkItemSnapshot
        {
            Fields = new Dictionary<string, string?>
            {
                ["Custom.Optional"] = null,
            },
        };

        snapshot.Fields.ShouldContainKey("Custom.Optional");
        snapshot.Fields["Custom.Optional"].ShouldBeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var fields = new Dictionary<string, string?> { ["Key"] = "Value" };
        var timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var a = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 2,
            TypeName = "Bug",
            Title = "Fix crash",
            State = "New",
            AssignedTo = "Jane",
            IterationPath = @"Proj\Sprint 1",
            AreaPath = @"Proj\Team",
            ParentId = 5,
            IsSeed = false,
            SeedCreatedAt = timestamp,
            LastSyncedAt = timestamp,
            IsDirty = true,
            Fields = fields,
        };

        var b = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 2,
            TypeName = "Bug",
            Title = "Fix crash",
            State = "New",
            AssignedTo = "Jane",
            IterationPath = @"Proj\Sprint 1",
            AreaPath = @"Proj\Team",
            ParentId = 5,
            IsSeed = false,
            SeedCreatedAt = timestamp,
            LastSyncedAt = timestamp,
            IsDirty = true,
            Fields = fields, // same reference — record equality uses reference equality for collections
        };

        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentId()
    {
        var a = new WorkItemSnapshot { Id = 1 };
        var b = new WorkItemSnapshot { Id = 2 };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentRevision()
    {
        var a = new WorkItemSnapshot { Id = 1, Revision = 1 };
        var b = new WorkItemSnapshot { Id = 1, Revision = 2 };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentTypeName()
    {
        var a = new WorkItemSnapshot { TypeName = "Bug" };
        var b = new WorkItemSnapshot { TypeName = "Task" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentState()
    {
        var a = new WorkItemSnapshot { State = "Active" };
        var b = new WorkItemSnapshot { State = "Closed" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentIsDirty()
    {
        var a = new WorkItemSnapshot { IsDirty = false };
        var b = new WorkItemSnapshot { IsDirty = true };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new WorkItemSnapshot
        {
            Id = 42,
            Revision = 1,
            TypeName = "Task",
            Title = "Original",
            State = "New",
            IsDirty = false,
        };

        var modified = original with { Title = "Modified", Revision = 2, IsDirty = true };

        modified.Id.ShouldBe(42);
        modified.TypeName.ShouldBe("Task");
        modified.Title.ShouldBe("Modified");
        modified.Revision.ShouldBe(2);
        modified.State.ShouldBe("New");
        modified.IsDirty.ShouldBeTrue();

        // Original unchanged
        original.Title.ShouldBe("Original");
        original.Revision.ShouldBe(1);
        original.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void WithExpression_FieldsDefaultPreserved()
    {
        var original = new WorkItemSnapshot { Id = 1 };

        var modified = original with { Title = "Updated" };

        modified.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void ParentId_Zero_IsDistinctFromNull()
    {
        var withZero = new WorkItemSnapshot { ParentId = 0 };
        var withNull = new WorkItemSnapshot { ParentId = null };

        withZero.ParentId.ShouldBe(0);
        withNull.ParentId.ShouldBeNull();
        withZero.ShouldNotBe(withNull);
    }

    [Fact]
    public void IsSeed_WithSeedCreatedAt_IndependentFlags()
    {
        var timestamp = new DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero);

        var seedWithTimestamp = new WorkItemSnapshot { IsSeed = true, SeedCreatedAt = timestamp };
        var seedWithoutTimestamp = new WorkItemSnapshot { IsSeed = true, SeedCreatedAt = null };
        var nonSeedWithTimestamp = new WorkItemSnapshot { IsSeed = false, SeedCreatedAt = timestamp };

        seedWithTimestamp.IsSeed.ShouldBeTrue();
        seedWithTimestamp.SeedCreatedAt.ShouldBe(timestamp);

        seedWithoutTimestamp.IsSeed.ShouldBeTrue();
        seedWithoutTimestamp.SeedCreatedAt.ShouldBeNull();

        nonSeedWithTimestamp.IsSeed.ShouldBeFalse();
        nonSeedWithTimestamp.SeedCreatedAt.ShouldBe(timestamp);
    }

    [Fact]
    public void AllWorkItemProperties_HaveCorrespondingSnapshotProperty()
    {
        // WorkItem init-only properties that must be present in WorkItemSnapshot:
        // Id, Type→TypeName, Title, State, AssignedTo, IterationPath, AreaPath,
        // ParentId, Revision, IsDirty, IsSeed, SeedCreatedAt, LastSyncedAt, Fields
        var snapshot = new WorkItemSnapshot
        {
            Id = 1,
            Revision = 3,
            TypeName = "Epic",
            Title = "Test",
            State = "Doing",
            AssignedTo = "User",
            IterationPath = @"Project\Iteration",
            AreaPath = @"Project\Area",
            ParentId = 99,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            LastSyncedAt = DateTimeOffset.UtcNow,
            IsDirty = true,
            Fields = new Dictionary<string, string?> { ["f1"] = "v1" },
        };

        // All 14 properties should be settable and readable
        snapshot.Id.ShouldBe(1);
        snapshot.Revision.ShouldBe(3);
        snapshot.TypeName.ShouldBe("Epic");
        snapshot.Title.ShouldBe("Test");
        snapshot.State.ShouldBe("Doing");
        snapshot.AssignedTo.ShouldBe("User");
        snapshot.IterationPath.ShouldBe(@"Project\Iteration");
        snapshot.AreaPath.ShouldBe(@"Project\Area");
        snapshot.ParentId.ShouldBe(99);
        snapshot.IsSeed.ShouldBeTrue();
        snapshot.SeedCreatedAt.ShouldNotBeNull();
        snapshot.LastSyncedAt.ShouldNotBeNull();
        snapshot.IsDirty.ShouldBeTrue();
        snapshot.Fields.Count.ShouldBe(1);
    }
}
