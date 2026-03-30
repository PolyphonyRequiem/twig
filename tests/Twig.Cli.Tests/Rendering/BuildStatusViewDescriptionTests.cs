using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class BuildStatusViewDescriptionTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public BuildStatusViewDescriptionTests()
    {
        _testConsole = new TestConsole();
        _testConsole.Profile.Width = 120;
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Description rendered below grid ─────────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionPresent_RenderedBelowGrid()
    {
        var item = CreateWorkItem(10, "With Description", "Active");
        item.SetField("System.Description", "This is a detailed work item description.");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldContain("Description");
        output.ShouldContain("detailed work item description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_HtmlDescription_StrippedAndRendered()
    {
        var item = CreateWorkItem(11, "HTML Desc", "Active");
        item.SetField("System.Description", "<div><p>Hello <b>world</b></p></div>");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldContain("Hello world");
        output.ShouldNotContain("<div>");
        output.ShouldNotContain("<b>");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NoDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(12, "No Desc", "Active");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldNotContain("Description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_WhitespaceDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(13, "Blank Desc", "Active");
        item.SetField("System.Description", "   ");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldNotContain("Description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_HtmlOnlyTagsDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(14, "Empty HTML", "Active");
        item.SetField("System.Description", "<div>  </div>");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldNotContain("Description");
    }

    // ── Description excluded from extended field rows ────────────────

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionNotInExtendedFields_AutoDetection()
    {
        var item = CreateWorkItem(15, "Auto Detect", "Active");
        item.SetField("System.Description", "Should appear in description section only.");
        item.SetField("Custom.Priority", "High");

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        // Description appears in the full-width section
        output.ShouldContain("Should appear in description section only");
        // Custom field still shows as extended row
        output.ShouldContain("High");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionNotInExtendedFields_StatusFieldEntries()
    {
        var item = CreateWorkItem(16, "Field Entries", "Active");
        item.SetField("System.Description", "Full-width description text here.");
        item.SetField("Custom.Priority", "Medium");

        var statusFieldEntries = new List<StatusFieldEntry>
        {
            new("System.Description", true),
            new("Custom.Priority", true),
        };

        var renderable = await _renderer.BuildStatusViewAsync(
            () => Task.FromResult<WorkItem?>(item),
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            CancellationToken.None,
            statusFieldEntries: statusFieldEntries);

        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        // Description appears in the full-width section
        output.ShouldContain("Full-width description text here");
        // Custom field still shows as extended row
        output.ShouldContain("Medium");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
