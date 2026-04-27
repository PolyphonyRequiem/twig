using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

/// <summary>
/// Unit tests for <see cref="WorkItem.WithParentId"/> and <see cref="WorkItem.WithIsSeed"/>.
/// </summary>
public class WorkItemCopyTests
{
    // ═══════════════════════════════════════════════════════════════
    //  WithParentId tests (E3-T11)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WithParentId_ChangesParentId()
    {
        var original = CreateSeedWithFields(parentId: 42);

        var copy = original.WithParentId(100);

        copy.ParentId.ShouldBe(100);
    }

    [Fact]
    public void WithParentId_NullClearsParent()
    {
        var original = CreateSeedWithFields(parentId: 42);

        var copy = original.WithParentId(null);

        copy.ParentId.ShouldBeNull();
    }

    [Fact]
    public void WithParentId_PreservesAllOtherProperties()
    {
        var iterPath = IterationPath.Parse(@"Project\Sprint1").Value;
        var areaPath = AreaPath.Parse(@"Project\Team").Value;

        var original = new WorkItem
        {
            Id = -5,
            Type = WorkItemType.UserStory,
            Title = "Original Title",
            State = "New",
            AssignedTo = "alice@example.com",
            IterationPath = iterPath,
            AreaPath = areaPath,
            ParentId = 42,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        original.MarkSynced(7);
        original.ImportFields(new Dictionary<string, string?> { ["System.Description"] = "A desc" });

        var copy = original.WithParentId(999);

        copy.Id.ShouldBe(-5);
        copy.Type.ShouldBe(WorkItemType.UserStory);
        copy.Title.ShouldBe("Original Title");
        copy.State.ShouldBe("New");
        copy.AssignedTo.ShouldBe("alice@example.com");
        copy.IterationPath.Value.ShouldBe(@"Project\Sprint1");
        copy.AreaPath.Value.ShouldBe(@"Project\Team");
        copy.IsSeed.ShouldBeTrue();
        copy.SeedCreatedAt.ShouldBe(original.SeedCreatedAt);
        copy.Revision.ShouldBe(7);
        copy.Fields["System.Description"].ShouldBe("A desc");
    }

    [Fact]
    public void WithParentId_PreservesDirtyFlag()
    {
        var original = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        original.UpdateField("System.Description", "dirty");
        original.IsDirty.ShouldBeTrue();

        var copy = original.WithParentId(100);

        copy.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void WithParentId_CleanItemStaysClean()
    {
        var original = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Clean",
            State = "New",
            IsSeed = true,
        };
        original.IsDirty.ShouldBeFalse();

        var copy = original.WithParentId(50);

        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void WithParentId_DoesNotMutateOriginal()
    {
        var original = CreateSeedWithFields(parentId: 42);

        _ = original.WithParentId(999);

        original.ParentId.ShouldBe(42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  WithIsSeed tests (E3-T11)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WithIsSeed_True_SetsSeedFlag()
    {
        var original = new WorkItem
        {
            Id = 123,
            Type = WorkItemType.Task,
            Title = "ADO Item",
            State = "Active",
            IsSeed = false,
        };
        original.MarkSynced(5);

        var copy = original.WithIsSeed(true);

        copy.IsSeed.ShouldBeTrue();
        copy.Id.ShouldBe(123);
        copy.Title.ShouldBe("ADO Item");
        copy.Revision.ShouldBe(5);
    }

    [Fact]
    public void WithIsSeed_False_ClearsSeedFlag()
    {
        var original = new WorkItemBuilder(-1, "Seed bug").AsBug().AsSeed().Build();

        var copy = original.WithIsSeed(false);

        copy.IsSeed.ShouldBeFalse();
    }

    [Fact]
    public void WithIsSeed_PreservesAllOtherProperties()
    {
        var iterPath = IterationPath.Parse(@"Project\Sprint2").Value;
        var areaPath = AreaPath.Parse(@"Project\Backend").Value;

        var original = new WorkItem
        {
            Id = 200,
            Type = WorkItemType.Feature,
            Title = "Feature X",
            State = "Active",
            AssignedTo = "bob@example.com",
            IterationPath = iterPath,
            AreaPath = areaPath,
            ParentId = 50,
            IsSeed = false,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        original.MarkSynced(3);
        original.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });

        var copy = original.WithIsSeed(true);

        copy.Id.ShouldBe(200);
        copy.Type.ShouldBe(WorkItemType.Feature);
        copy.Title.ShouldBe("Feature X");
        copy.State.ShouldBe("Active");
        copy.AssignedTo.ShouldBe("bob@example.com");
        copy.IterationPath.Value.ShouldBe(@"Project\Sprint2");
        copy.AreaPath.Value.ShouldBe(@"Project\Backend");
        copy.ParentId.ShouldBe(50);
        copy.SeedCreatedAt.ShouldBe(original.SeedCreatedAt);
        copy.Revision.ShouldBe(3);
        copy.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
    }

    [Fact]
    public void WithIsSeed_DoesNotPreserveDirtyFlag()
    {
        var original = new WorkItemBuilder(-1, "Dirty seed").AsSeed().Build();
        original.UpdateField("System.Description", "dirty");
        original.IsDirty.ShouldBeTrue();

        // WithIsSeed does NOT copy dirty flag (by design — used on fetched-back items)
        var copy = original.WithIsSeed(true);

        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void WithIsSeed_DoesNotMutateOriginal()
    {
        var original = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Original",
            State = "New",
            IsSeed = false,
        };

        _ = original.WithIsSeed(true);

        original.IsSeed.ShouldBeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateSeedWithFields(int? parentId)
    {
        var item = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Test seed",
            State = "New",
            ParentId = parentId,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
        };
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "A description",
        });
        return item;
    }
}
