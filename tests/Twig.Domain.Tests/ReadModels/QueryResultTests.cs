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

}
