using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for FormatSprintView in all output formatters.
/// </summary>
public class SprintViewFormatterTests
{
    [Fact]
    public void HumanFormatter_SprintView_GroupsByAssignee()
    {
        var fmt = new HumanOutputFormatter();
        var items = new[]
        {
            CreateItem(1, "Task A", "Alice Smith"),
            CreateItem(2, "Task B", "Bob Jones"),
            CreateItem(3, "Task C", "Alice Smith"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = fmt.FormatSprintView(ws, 14);

        output.ShouldContain("Sprint");
        output.ShouldContain("3 items");
        output.ShouldContain("Alice Smith");
        output.ShouldContain("Bob Jones");
    }

    [Fact]
    public void HumanFormatter_SprintView_HandlesUnassigned()
    {
        var fmt = new HumanOutputFormatter();
        var items = new[]
        {
            CreateItem(1, "Task A", null),
            CreateItem(2, "Task B", "Bob Jones"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = fmt.FormatSprintView(ws, 14);

        output.ShouldContain("(unassigned)");
        output.ShouldContain("Bob Jones");
    }

    [Fact]
    public void JsonFormatter_SprintView_GroupsByAssignee()
    {
        var fmt = new JsonOutputFormatter();
        var items = new[]
        {
            CreateItem(1, "Task A", "Alice Smith"),
            CreateItem(2, "Task B", "Bob Jones"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = fmt.FormatSprintView(ws, 14);

        output.ShouldContain("sprintByAssignee");
        output.ShouldContain("Alice Smith");
        output.ShouldContain("Bob Jones");
        output.ShouldContain("totalSprintItems");
    }

    [Fact]
    public void MinimalFormatter_SprintView_IncludesAssignee()
    {
        var fmt = new MinimalOutputFormatter();
        var items = new[]
        {
            CreateItem(1, "Task A", "Alice Smith"),
            CreateItem(2, "Task B", "Bob Jones"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = fmt.FormatSprintView(ws, 14);

        output.ShouldContain("@Alice Smith");
        output.ShouldContain("@Bob Jones");
    }

    [Fact]
    public void HumanFormatter_SprintView_EmptySprint_ShowsZeroItems()
    {
        var fmt = new HumanOutputFormatter();
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var output = fmt.FormatSprintView(ws, 14);

        output.ShouldContain("0 items");
    }

    private static WorkItem CreateItem(int id, string title, string? assignedTo)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
