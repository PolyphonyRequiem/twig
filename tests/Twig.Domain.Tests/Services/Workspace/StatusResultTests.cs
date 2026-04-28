using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services.Workspace;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

public sealed class StatusResultTests
{
    [Fact]
    public void NoContext_IsStatusResult()
    {
        StatusResult result = new StatusResult.NoContext();

        result.ShouldBeOfType<StatusResult.NoContext>();
    }

    [Fact]
    public void Unreachable_PreservesAllProperties()
    {
        var result = new StatusResult.Unreachable(
            ActiveId: 42,
            UnreachableId: 99,
            Reason: "Item deleted");

        result.ActiveId.ShouldBe(42);
        result.UnreachableId.ShouldBe(99);
        result.Reason.ShouldBe("Item deleted");
    }

    [Fact]
    public void Unreachable_IsStatusResult()
    {
        StatusResult result = new StatusResult.Unreachable(1, 2, "reason");

        result.ShouldBeOfType<StatusResult.Unreachable>();
    }

    [Fact]
    public void Success_PreservesAllProperties()
    {
        var item = new WorkItemBuilder(1, "Test Item").Build();
        var pending = new List<PendingChangeRecord>
        {
            new(WorkItemId: 1, ChangeType: "FieldUpdate", FieldName: "System.Title", OldValue: "Old", NewValue: "New")
        };
        var seeds = new List<WorkItem> { new WorkItemBuilder(2, "Seed").Build() };

        var result = new StatusResult.Success(item, pending, seeds);

        result.Item.ShouldBe(item);
        result.PendingChanges.ShouldBe(pending);
        result.Seeds.ShouldBe(seeds);
    }

    [Fact]
    public void Success_IsStatusResult()
    {
        var item = new WorkItemBuilder(1, "Test Item").Build();

        StatusResult result = new StatusResult.Success(item, [], []);

        result.ShouldBeOfType<StatusResult.Success>();
    }

    [Fact]
    public void Success_WithEmptyCollections()
    {
        var item = new WorkItemBuilder(1, "Test Item").Build();

        var result = new StatusResult.Success(item, [], []);

        result.PendingChanges.ShouldBeEmpty();
        result.Seeds.ShouldBeEmpty();
    }

    [Fact]
    public void PatternMatching_DistinguishesAllSubtypes()
    {
        var cases = new StatusResult[]
        {
            new StatusResult.NoContext(),
            new StatusResult.Unreachable(1, 1, "gone"),
            new StatusResult.Success(new WorkItemBuilder(1, "Item").Build(), [], []),
        };

        var labels = cases.Select(c => c switch
        {
            StatusResult.NoContext => "no-context",
            StatusResult.Unreachable u => $"unreachable-{u.UnreachableId}",
            StatusResult.Success s => $"success-{s.Item.Id}",
            _ => "unknown",
        }).ToList();

        labels.ShouldBe(["no-context", "unreachable-1", "success-1"]);
    }

    [Fact]
    public void RecordEquality_Works_ForNoContext()
    {
        var a = new StatusResult.NoContext();
        var b = new StatusResult.NoContext();

        a.ShouldBe(b);
    }

    [Fact]
    public void RecordEquality_Works_ForUnreachable()
    {
        var a = new StatusResult.Unreachable(1, 2, "reason");
        var b = new StatusResult.Unreachable(1, 2, "reason");

        a.ShouldBe(b);
    }
}
