using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that <see cref="SpectreRenderer.BuildStatusViewAsync"/>
/// and <see cref="SpectreRenderer.RenderWorkItemAsync"/> render correctly at narrow
/// (60-char), standard (80-char), and wide (120-char) terminal widths.
/// </summary>
public sealed class TreeStatusShowWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string LongAreaPath = @"MyOrg\MyProject\Engineering\Backend\AuthTeam";
    private const string LongIterationPath = @"MyOrg\MyProject\Sprint 42\Week 2";
    private const string LongAssignedTo = "Alexander Christopher Hamilton-Burr III";

    // ── BuildStatusViewAsync — narrow (60) ──────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_RendersWithoutCrash()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(100, LongTitle, assignedTo: LongAssignedTo,
            areaPath: LongAreaPath, iterationPath: LongIterationPath);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("#100");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_ContainsCoreFields()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(101, ShortTitle, state: "Active");

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("#101");
        output.ShouldContain("Task");
        output.ShouldContain("Active");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_LongAreaPath_Rendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(102, ShortTitle, areaPath: LongAreaPath);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        // The area path should appear in the output (possibly truncated by Spectre)
        output.ShouldContain("Area");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_LongIterationPath_Rendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(103, ShortTitle, iterationPath: LongIterationPath);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("Iteration");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_LongAssignedTo_Rendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(104, ShortTitle, assignedTo: LongAssignedTo);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("Assigned");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_PanelHeaderContainsId()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(105, LongTitle);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("#105");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_WithRelationships_Renders()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(106, ShortTitle);
        var parent = CreateItem(50, LongTitle, type: WorkItemType.Issue);
        var child = CreateItem(200, LongTitle, type: WorkItemType.Task);

        var renderable = await BuildStatusViewAsync(renderer, item, parent: parent, children: [child]);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("#106");
        output.ShouldContain("Parent");
        output.ShouldContain("Child");
    }

    // ── BuildStatusViewAsync — standard (80) ────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_StandardWidth_ShortTitleFullyVisible()
    {
        var (renderer, console) = CreateRenderer(80);
        var item = CreateItem(110, ShortTitle);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(ShortTitle);
    }

    // ── BuildStatusViewAsync — wide (120) ───────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_WideWidth_FullTitlePreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(120, ShortTitle, assignedTo: "Jane Doe",
            areaPath: "Project", iterationPath: @"Project\Sprint 1");

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(ShortTitle);
        output.ShouldContain("Jane Doe");
        output.ShouldContain("Project");
        output.ShouldContain("Sprint 1");
    }

    [Fact]
    public async Task BuildStatusViewAsync_WideWidth_FullAreaPathPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(121, ShortTitle, areaPath: LongAreaPath);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(LongAreaPath);
    }

    [Fact]
    public async Task BuildStatusViewAsync_WideWidth_FullIterationPathPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(122, ShortTitle, iterationPath: LongIterationPath);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(LongIterationPath);
    }

    [Fact]
    public async Task BuildStatusViewAsync_WideWidth_FullAssignedToPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(123, ShortTitle, assignedTo: LongAssignedTo);

        var renderable = await BuildStatusViewAsync(renderer, item);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(LongAssignedTo);
    }

    [Fact]
    public async Task BuildStatusViewAsync_WideWidth_RelationshipTitlesPreserved()
    {
        var (renderer, console) = CreateRenderer(200);
        var item = CreateItem(124, ShortTitle);
        var parent = CreateItem(50, ShortTitle, type: WorkItemType.Issue);
        var child = CreateItem(200, ShortTitle, type: WorkItemType.Task);

        var renderable = await BuildStatusViewAsync(renderer, item, parent: parent, children: [child]);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    // ── BuildStatusViewAsync — dirty + narrow ───────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_NarrowWidth_DirtyItem_ShowsBullet()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = new WorkItemBuilder(130, ShortTitle)
            .InState("Active")
            .WithAreaPath("Project")
            .WithIterationPath(@"Project\Sprint 1")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(130, "field", "System.Title", "Old", ShortTitle)
        };

        var renderable = await BuildStatusViewAsync(renderer, item, changes: changes);
        console.Write(renderable);

        var output = console.Output;
        output.ShouldContain("●");
    }

    // ── RenderWorkItemAsync — narrow (60) ───────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_RendersWithoutCrash()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(200, LongTitle, assignedTo: LongAssignedTo,
            areaPath: LongAreaPath, iterationPath: LongIterationPath);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("#200");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_ContainsCoreFields()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(201, ShortTitle, state: "Active");

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("#201");
        output.ShouldContain("Task");
        output.ShouldContain("Active");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_AreaPathRendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(202, ShortTitle, areaPath: LongAreaPath);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Area");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_IterationPathRendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(203, ShortTitle, iterationPath: LongIterationPath);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Iteration");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_PanelHeaderContainsId()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(204, LongTitle);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("#204");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_DirtyMarkerShown()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(205, ShortTitle);
        item.SetDirty();

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), true, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("●");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_DescriptionRendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(206, ShortTitle);
        item.SetField("System.Description", "A detailed description of the task.");

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Description");
        output.ShouldContain("detailed description");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowWidth_TagsRendered()
    {
        var (renderer, console) = CreateRenderer(60);
        var item = CreateItem(207, ShortTitle);
        item.SetField("System.Tags", "backend; priority-1");

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Tags");
    }

    // ── RenderWorkItemAsync — wide (120) ────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_WideWidth_FullTitlePreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(210, ShortTitle, assignedTo: "Jane Doe",
            areaPath: "Project", iterationPath: @"Project\Sprint 1");

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain(ShortTitle);
        output.ShouldContain("Jane Doe");
        output.ShouldContain("Project");
        output.ShouldContain("Sprint 1");
    }

    [Fact]
    public async Task RenderWorkItemAsync_WideWidth_FullAreaPathPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(211, ShortTitle, areaPath: LongAreaPath);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain(LongAreaPath);
    }

    [Fact]
    public async Task RenderWorkItemAsync_WideWidth_FullIterationPathPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(212, ShortTitle, iterationPath: LongIterationPath);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain(LongIterationPath);
    }

    [Fact]
    public async Task RenderWorkItemAsync_WideWidth_FullAssignedToPreserved()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(213, ShortTitle, assignedTo: LongAssignedTo);

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain(LongAssignedTo);
    }

    [Fact]
    public async Task RenderWorkItemAsync_WideWidth_DescriptionFullyVisible()
    {
        var (renderer, console) = CreateRenderer(120);
        var item = CreateItem(214, ShortTitle);
        item.SetField("System.Description", "A detailed description of the work item with full context.");

        await renderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Description");
        output.ShouldContain("detailed description");
        output.ShouldContain("full context");
    }

    // ── Width comparison — same data at different widths ─────────────

    [Fact]
    public async Task BuildStatusViewAsync_NarrowVsWide_NarrowOutputShorter()
    {
        var item = CreateItem(300, LongTitle, assignedTo: LongAssignedTo,
            areaPath: LongAreaPath, iterationPath: LongIterationPath);

        var (narrowRenderer, narrowConsole) = CreateRenderer(60);
        var narrowRenderable = await BuildStatusViewAsync(narrowRenderer, item);
        narrowConsole.Write(narrowRenderable);
        var narrowLines = narrowConsole.Output.Split('\n');

        var (wideRenderer, wideConsole) = CreateRenderer(200);
        var wideRenderable = await BuildStatusViewAsync(wideRenderer, item);
        wideConsole.Write(wideRenderable);
        var wideLines = wideConsole.Output.Split('\n');

        // At narrow width, Spectre wraps content so there should be more lines
        narrowLines.Length.ShouldBeGreaterThanOrEqualTo(wideLines.Length);
    }

    [Fact]
    public async Task RenderWorkItemAsync_NarrowVsWide_BothContainId()
    {
        var item = CreateItem(301, LongTitle, assignedTo: LongAssignedTo,
            areaPath: LongAreaPath, iterationPath: LongIterationPath);

        var (narrowRenderer, narrowConsole) = CreateRenderer(60);
        await narrowRenderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var (wideRenderer, wideConsole) = CreateRenderer(200);
        await wideRenderer.RenderWorkItemAsync(
            () => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        narrowConsole.Output.ShouldContain("#301");
        wideConsole.Output.ShouldContain("#301");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (SpectreRenderer renderer, TestConsole console) CreateRenderer(int width)
    {
        var console = new TestConsole { Profile = { Width = width } };
        var renderer = new SpectreRenderer(console, new SpectreTheme(new DisplayConfig()));
        return (renderer, console);
    }

    private static WorkItem CreateItem(
        int id,
        string title,
        string state = "Active",
        WorkItemType? type = null,
        string? assignedTo = null,
        string? areaPath = null,
        string? iterationPath = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse(iterationPath ?? @"Project\Sprint 1").Value,
            AreaPath = AreaPath.Parse(areaPath ?? "Project").Value,
        };
    }

    private static async Task<Spectre.Console.Rendering.IRenderable> BuildStatusViewAsync(
        SpectreRenderer renderer,
        WorkItem item,
        List<PendingChangeRecord>? changes = null,
        WorkItem? parent = null,
        IReadOnlyList<WorkItem>? children = null)
    {
        return await renderer.BuildStatusViewAsync(
            item,
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(changes ?? []),
            parent: parent,
            children: children);
    }
}
