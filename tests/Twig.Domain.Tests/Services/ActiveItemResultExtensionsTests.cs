using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ActiveItemResultExtensionsTests
{
    [Fact]
    public void TryGetWorkItem_Found_ReturnsTrueWithItem()
    {
        var workItem = MakeItem(42);
        var result = new ActiveItemResult.Found(workItem);

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeTrue();
        item.ShouldNotBeNull();
        item.Id.ShouldBe(42);
        errorId.ShouldBeNull();
        errorReason.ShouldBeNull();
    }

    [Fact]
    public void TryGetWorkItem_FetchedFromAdo_ReturnsTrueWithItem()
    {
        var workItem = MakeItem(99);
        var result = new ActiveItemResult.FetchedFromAdo(workItem);

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeTrue();
        item.ShouldNotBeNull();
        item.Id.ShouldBe(99);
        errorId.ShouldBeNull();
        errorReason.ShouldBeNull();
    }

    [Fact]
    public void TryGetWorkItem_Unreachable_ReturnsFalseWithIdAndReason()
    {
        var result = new ActiveItemResult.Unreachable(123, "Network timeout");

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeFalse();
        item.ShouldBeNull();
        errorId.ShouldBe(123);
        errorReason.ShouldBe("Network timeout");
    }

    [Fact]
    public void TryGetWorkItem_NoContext_ReturnsFalseWithNulls()
    {
        var result = new ActiveItemResult.NoContext();

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeFalse();
        item.ShouldBeNull();
        errorId.ShouldBeNull();
        errorReason.ShouldBeNull();
    }

    private static WorkItem MakeItem(int id) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = $"Item {id}",
        State = "Active",
    };
}
