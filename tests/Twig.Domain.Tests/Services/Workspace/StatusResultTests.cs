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
        StatusResult result = new StatusNoContext();

        result.ShouldBeUnionCase<StatusNoContext>();
    }

    [Fact]
    public void Unreachable_PreservesAllProperties()
    {
        var result = new StatusUnreachable(
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
        StatusResult result = new StatusUnreachable(1, 2, "reason");

        result.ShouldBeUnionCase<StatusUnreachable>();
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

        var result = new StatusSuccess(item, pending, seeds);

        result.Item.ShouldBe(item);
        result.PendingChanges.ShouldBe(pending);
        result.Seeds.ShouldBe(seeds);
    }

    [Fact]
    public void Success_IsStatusResult()
    {
        var item = new WorkItemBuilder(1, "Test Item").Build();

        StatusResult result = new StatusSuccess(item, [], []);

        result.ShouldBeUnionCase<StatusSuccess>();
    }

    [Fact]
    public void Success_WithEmptyCollections()
    {
        var item = new WorkItemBuilder(1, "Test Item").Build();

        var result = new StatusSuccess(item, [], []);

        result.PendingChanges.ShouldBeEmpty();
        result.Seeds.ShouldBeEmpty();
    }

    [Fact]
    public void PatternMatching_DistinguishesAllSubtypes()
    {
        var cases = new StatusResult[]
        {
            new StatusNoContext(),
            new StatusUnreachable(1, 1, "gone"),
            new StatusSuccess(new WorkItemBuilder(1, "Item").Build(), [], []),
        };

        var labels = cases.Select(c => c switch
        {
            StatusNoContext => "no-context",
            StatusUnreachable u => $"unreachable-{u.UnreachableId}",
            StatusSuccess s => $"success-{s.Item.Id}",
            _ => "unknown",
        }).ToList();

        labels.ShouldBe(["no-context", "unreachable-1", "success-1"]);
    }

    [Fact]
    public void RecordEquality_Works_ForNoContext()
    {
        var a = new StatusNoContext();
        var b = new StatusNoContext();

        a.ShouldBe(b);
    }

    [Fact]
    public void RecordEquality_Works_ForUnreachable()
    {
        var a = new StatusUnreachable(1, 2, "reason");
        var b = new StatusUnreachable(1, 2, "reason");

        a.ShouldBe(b);
    }
}
