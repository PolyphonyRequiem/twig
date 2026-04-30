using Shouldly;
using Twig.Domain.Services.Navigation;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Navigation;

public class ActiveItemResultExtensionsTests
{
    [Fact]
    public void TryGetWorkItem_Found_ReturnsTrueWithItem()
    {
        var workItem = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        ActiveItemResult result = new Found(workItem);

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
        var workItem = new WorkItemBuilder(99, "Item 99").InState("Active").Build();
        ActiveItemResult result = new FetchedFromAdo(workItem);

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
        ActiveItemResult result = new ActiveUnreachable(123, "Network timeout");

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeFalse();
        item.ShouldBeNull();
        errorId.ShouldBe(123);
        errorReason.ShouldBe("Network timeout");
    }

    [Fact]
    public void TryGetWorkItem_NoContext_ReturnsFalseWithNulls()
    {
        ActiveItemResult result = new ActiveNoContext();

        var success = result.TryGetWorkItem(out var item, out var errorId, out var errorReason);

        success.ShouldBeFalse();
        item.ShouldBeNull();
        errorId.ShouldBeNull();
        errorReason.ShouldBeNull();
    }

}
