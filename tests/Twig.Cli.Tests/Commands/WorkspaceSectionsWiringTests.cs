using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class WorkspaceSectionsWiringTests
{
    [Fact]
    public void WorkspaceBuild_WithSections_SectionsPopulated()
    {
        var item = CreateWorkItem();
        var sections = WorkspaceSections.Build(new[] { item }, excludedIds: new[] { 10 });
        var workspace = Workspace.Build(null, new[] { item }, Array.Empty<WorkItem>(), sections: sections);
        workspace.Sections.ShouldNotBeNull();
        workspace.Sections.Sections.Count.ShouldBe(1);
        workspace.Sections.Sections[0].ModeName.ShouldBe("Sprint");
        workspace.Sections.Sections[0].Items.Count.ShouldBe(1);
        workspace.Sections.ExcludedItemIds.ShouldContain(10);
    }

    [Fact]
    public void WorkspaceBuild_NullSections_SectionsNull() =>
        Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>()).Sections.ShouldBeNull();

    [Fact]
    public void SprintItemsLoaded_CarriesSections()
    {
        var item = CreateWorkItem();
        var sections = WorkspaceSections.Build(new[] { item }, excludedIds: new[] { 5 });
        var chunk = new SprintItemsLoaded(new[] { item }, sections);
        chunk.Sections.ShouldNotBeNull();
        chunk.Sections.Sections.Count.ShouldBe(1);
        chunk.Sections.ExcludedItemIds.ShouldContain(5);
    }

    [Fact]
    public void SprintItemsLoaded_NullSections_DefaultsToNull() =>
        new SprintItemsLoaded(Array.Empty<WorkItem>()).Sections.ShouldBeNull();

    private static WorkItem CreateWorkItem() => new()
    {
        Id = 1, Type = WorkItemType.Task, Title = "Item", State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
