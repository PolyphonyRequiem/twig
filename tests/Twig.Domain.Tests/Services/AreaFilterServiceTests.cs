using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class AreaFilterServiceTests
{
    private static WorkItem MakeItem(int id, string areaPath)
    {
        var ap = AreaPath.Parse(areaPath).Value;
        return new WorkItem
        {
            Id = id,
            Title = $"Item {id}",
            Type = WorkItemType.Task,
            State = "New",
            AreaPath = ap,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };
    }

    // ── FilterByArea ───────────────────────────────────────────────────

    [Fact]
    public void FilterByArea_EmptyFilters_ReturnsEmpty()
    {
        var items = new[] { MakeItem(1, @"Project\Team A") };
        var filters = Array.Empty<AreaPathFilter>();

        var result = AreaFilterService.FilterByArea(items, filters);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FilterByArea_MatchingFilter_ReturnsMatchedItems()
    {
        var items = new[]
        {
            MakeItem(1, @"Project\Team A"),
            MakeItem(2, @"Project\Team B"),
            MakeItem(3, @"Project\Team A\Sub"),
        };
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: true) };

        var result = AreaFilterService.FilterByArea(items, filters);

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Id == 1);
        result.ShouldContain(i => i.Id == 3);
    }

    [Fact]
    public void FilterByArea_ExactFilter_OnlyMatchesExact()
    {
        var items = new[]
        {
            MakeItem(1, @"Project\Team A"),
            MakeItem(2, @"Project\Team A\Sub"),
        };
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: false) };

        var result = AreaFilterService.FilterByArea(items, filters);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public void FilterByArea_MultipleFilters_ORSemantics()
    {
        var items = new[]
        {
            MakeItem(1, @"Project\Team A"),
            MakeItem(2, @"Project\Team B"),
            MakeItem(3, @"Project\Team C"),
        };
        var filters = new[]
        {
            new AreaPathFilter(@"Project\Team A", IncludeChildren: true),
            new AreaPathFilter(@"Project\Team C", IncludeChildren: true),
        };

        var result = AreaFilterService.FilterByArea(items, filters);

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Id == 1);
        result.ShouldContain(i => i.Id == 3);
    }

    // ── IsInArea ───────────────────────────────────────────────────────

    [Fact]
    public void IsInArea_MatchingPath_ReturnsTrue()
    {
        var path = AreaPath.Parse(@"Project\Team A\Sub").Value;
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: true) };

        AreaFilterService.IsInArea(path, filters).ShouldBeTrue();
    }

    [Fact]
    public void IsInArea_NoMatch_ReturnsFalse()
    {
        var path = AreaPath.Parse(@"Project\Team B").Value;
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: true) };

        AreaFilterService.IsInArea(path, filters).ShouldBeFalse();
    }

    [Fact]
    public void IsInArea_EmptyFilters_ReturnsFalse()
    {
        var path = AreaPath.Parse(@"Project\Team A").Value;
        var filters = Array.Empty<AreaPathFilter>();

        AreaFilterService.IsInArea(path, filters).ShouldBeFalse();
    }

    // ── FilterByArea — additional edge cases ───────────────────────────

    [Fact]
    public void FilterByArea_EmptyItems_ReturnsEmpty()
    {
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: true) };

        var result = AreaFilterService.FilterByArea(Array.Empty<WorkItem>(), filters);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FilterByArea_NoDuplicatesWhenItemMatchesMultipleFilters()
    {
        var items = new[] { MakeItem(1, @"Project\Team A") };
        var filters = new[]
        {
            new AreaPathFilter(@"Project\Team A", IncludeChildren: true),
            new AreaPathFilter("Project", IncludeChildren: true),
        };

        var result = AreaFilterService.FilterByArea(items, filters);

        // Item matches both filters but should appear only once (LINQ Where + Any)
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public void FilterByArea_MixedExactAndUnder_ReturnsUnion()
    {
        var items = new[]
        {
            MakeItem(1, @"Project\Team A"),
            MakeItem(2, @"Project\Team A\Sub"),
            MakeItem(3, @"Project\Team B"),
        };
        var filters = new[]
        {
            new AreaPathFilter(@"Project\Team A", IncludeChildren: false), // exact: only item 1
            new AreaPathFilter(@"Project\Team B", IncludeChildren: true),  // under: item 3
        };

        var result = AreaFilterService.FilterByArea(items, filters);

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Id == 1);
        result.ShouldContain(i => i.Id == 3);
    }

    [Fact]
    public void FilterByArea_PreservesOriginalOrder()
    {
        var items = new[]
        {
            MakeItem(5, @"Project\Team A"),
            MakeItem(3, @"Project\Team B"),
            MakeItem(1, @"Project\Team A\Sub"),
        };
        var filters = new[] { new AreaPathFilter("Project", IncludeChildren: true) };

        var result = AreaFilterService.FilterByArea(items, filters);

        result.Count.ShouldBe(3);
        result[0].Id.ShouldBe(5);
        result[1].Id.ShouldBe(3);
        result[2].Id.ShouldBe(1);
    }

    // ── IsInArea — additional edge cases ───────────────────────────────

    [Fact]
    public void IsInArea_MultipleFilters_AnyMatch_ReturnsTrue()
    {
        var path = AreaPath.Parse(@"Project\Team B").Value;
        var filters = new[]
        {
            new AreaPathFilter(@"Project\Team A", IncludeChildren: true),
            new AreaPathFilter(@"Project\Team B", IncludeChildren: false),
        };

        AreaFilterService.IsInArea(path, filters).ShouldBeTrue();
    }

    [Fact]
    public void IsInArea_CaseInsensitive_ReturnsTrue()
    {
        var path = AreaPath.Parse(@"PROJECT\TEAM A").Value;
        var filters = new[] { new AreaPathFilter(@"Project\Team A", IncludeChildren: true) };

        AreaFilterService.IsInArea(path, filters).ShouldBeTrue();
    }
}
