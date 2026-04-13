using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

public class QueryResultTests
{
    [Fact]
    public void Constructor_StoresItemsAndTruncatedFlag()
    {
        var items = new[] { WorkItemBuilder.Simple(1, "Item 1"), WorkItemBuilder.Simple(2, "Item 2") };

        var result = new QueryResult(items, IsTruncated: true);

        result.Items.ShouldBe(items);
        result.IsTruncated.ShouldBeTrue();
    }

    [Fact]
    public void Constructor_EmptyItems_NotTruncated()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        result.Items.ShouldBeEmpty();
        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public void IsTruncated_TrueWhenCountEqualsTop()
    {
        const int top = 3;
        var items = new[]
        {
            WorkItemBuilder.Simple(1, "A"),
            WorkItemBuilder.Simple(2, "B"),
            WorkItemBuilder.Simple(3, "C"),
        };

        var result = new QueryResult(items, items.Length >= top);

        result.IsTruncated.ShouldBeTrue();
    }

    [Fact]
    public void IsTruncated_FalseWhenCountBelowTop()
    {
        const int top = 5;
        var items = new[]
        {
            WorkItemBuilder.Simple(1, "A"),
            WorkItemBuilder.Simple(2, "B"),
        };

        var result = new QueryResult(items, items.Length >= top);

        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public void Equality_SameItemsAndFlag_AreEqual()
    {
        var items = new[] { WorkItemBuilder.Simple(1, "X") };

        var a = new QueryResult(items, IsTruncated: false);
        var b = new QueryResult(items, IsTruncated: false);

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentTruncatedFlag_AreNotEqual()
    {
        var items = new[] { WorkItemBuilder.Simple(1, "X") };

        var a = new QueryResult(items, IsTruncated: true);
        var b = new QueryResult(items, IsTruncated: false);

        a.ShouldNotBe(b);
    }
}
