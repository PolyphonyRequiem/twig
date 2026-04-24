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
}
