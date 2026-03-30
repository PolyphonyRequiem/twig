using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class RenderWorkItemTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public RenderWorkItemTests()
    {
        _testConsole = new TestConsole();
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Core fields render immediately ──────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_CoreFields_RenderedImmediately()
    {
        var item = CreateWorkItem(42, "My Task", "Active");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("#42");
        output.ShouldContain("My Task");
        output.ShouldContain("Active");
        output.ShouldContain("(unassigned)");
    }

    [Fact]
    public async Task RenderWorkItemAsync_TypeBadge_Rendered()
    {
        var item = CreateWorkItem(1, "Bug Item", "New", WorkItemType.Bug);

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Bug");
    }

    [Fact]
    public async Task RenderWorkItemAsync_AreaAndIteration_Rendered()
    {
        var item = CreateWorkItem(1, "Test", "New");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Project");
        output.ShouldContain("Sprint 1");
    }

    // ── Extended fields populate progressively ──────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_Description_RenderedWhenPresent()
    {
        var item = CreateWorkItem(1, "With Desc", "Active");
        item.SetField("System.Description", "This is a detailed description of the work item.");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Description:");
        output.ShouldContain("detailed description");
    }

    [Fact]
    public async Task RenderWorkItemAsync_History_RenderedWhenPresent()
    {
        var item = CreateWorkItem(1, "With History", "Active");
        item.SetField("System.History", "Last comment about progress");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("History:");
        output.ShouldContain("Last comment about progress");
    }

    [Fact]
    public async Task RenderWorkItemAsync_Tags_RenderedWhenPresent()
    {
        var item = CreateWorkItem(1, "With Tags", "Active");
        item.SetField("System.Tags", "backend; priority-1; sprint-5");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Tags:");
        output.ShouldContain("backend; priority-1; sprint-5");
    }

    [Fact]
    public async Task RenderWorkItemAsync_NoExtendedFields_OnlyCoreFieldsShown()
    {
        var item = CreateWorkItem(1, "Plain Item", "New");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Plain Item");
        output.ShouldNotContain("Description:");
        output.ShouldNotContain("History:");
        output.ShouldNotContain("Tags:");
    }

    // ── Dirty marker ────────────────────────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_ShowDirtyTrue_DirtyItem_ShowsMarker()
    {
        var item = CreateWorkItem(1, "Dirty Item", "Active");
        item.SetDirty();

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), true, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("✎");
    }

    [Fact]
    public async Task RenderWorkItemAsync_ShowDirtyFalse_DirtyItem_NoMarker()
    {
        var item = CreateWorkItem(1, "Dirty Item", "Active");
        item.SetDirty();

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        // The output should contain the title but not the dirty marker in the header
        var output = _testConsole.Output;
        output.ShouldContain("Dirty Item");
        output.ShouldNotContain("✎");
    }

    // ── Null item ───────────────────────────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_NullItem_NoOutput()
    {
        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(null), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldBeEmpty();
    }

    // ── HTML stripping ──────────────────────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_HtmlDescription_StrippedCleanly()
    {
        var item = CreateWorkItem(1, "HTML Desc", "Active");
        item.SetField("System.Description", "<div>Hello <b>world</b></div>");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Hello world");
        output.ShouldNotContain("<div>");
        output.ShouldNotContain("<b>");
    }

    // ── Truncation ──────────────────────────────────────────────────

    [Fact]
    public void TruncateField_ShortValue_ReturnsUnchanged()
    {
        SpectreRenderer.TruncateField("hello", 200).ShouldBe("hello");
    }

    [Fact]
    public void TruncateField_LongValue_TruncatesWithEllipsis()
    {
        var longText = new string('x', 250);
        var result = SpectreRenderer.TruncateField(longText, 200);
        result.Length.ShouldBe(200);
        result.ShouldEndWith("…");
    }

    [Fact]
    public void TruncateField_HtmlContent_StrippedBeforeTruncation()
    {
        var html = "<p>" + new string('a', 100) + "</p>";
        var result = SpectreRenderer.TruncateField(html, 200);
        result.ShouldNotContain("<p>");
        result.ShouldNotContain("</p>");
    }

    // ── StripHtmlTags unit tests ────────────────────────────────────

    [Fact]
    public void StripHtmlTags_PlainText_ReturnsUnchanged()
    {
        SpectreRenderer.StripHtmlTags("plain text").ShouldBe("plain text");
    }

    [Fact]
    public void StripHtmlTags_BasicHtml_StripsAllTags()
    {
        SpectreRenderer.StripHtmlTags("<p>Hello <b>World</b></p>").ShouldBe("Hello World");
    }

    [Fact]
    public void StripHtmlTags_EmptyString_ReturnsEmpty()
    {
        SpectreRenderer.StripHtmlTags("").ShouldBe("");
    }

    [Fact]
    public void StripHtmlTags_SelfClosingTags_Stripped()
    {
        SpectreRenderer.StripHtmlTags("Line 1<br/>Line 2").ShouldBe("Line 1Line 2");
    }

    [Fact]
    public void StripHtmlTags_NestedTags_AllStripped()
    {
        SpectreRenderer.StripHtmlTags("<div><span><b>text</b></span></div>").ShouldBe("text");
    }

    [Fact]
    public void StripHtmlTags_UnclosedAngleBracket_TreatedAsLiteral()
    {
        SpectreRenderer.StripHtmlTags("5 < 10 is true").ShouldBe("5 < 10 is true");
    }

    [Fact]
    public void StripHtmlTags_UnclosedAngleBracketAtEnd_TreatedAsLiteral()
    {
        SpectreRenderer.StripHtmlTags("value <").ShouldBe("value <");
    }

    [Fact]
    public void StripHtmlTags_MixedUnclosedAndRealTags_HandledCorrectly()
    {
        SpectreRenderer.StripHtmlTags("a < b <b>bold</b> end").ShouldBe("a < b bold end");
    }

    // ── Extended field with whitespace-only content ──────────────────

    [Fact]
    public async Task RenderWorkItemAsync_WhitespaceOnlyDescription_NotShown()
    {
        var item = CreateWorkItem(1, "Whitespace Desc", "Active");
        item.SetField("System.Description", "   ");

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldNotContain("Description:");
    }

    // ── Assigned user shown ─────────────────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_WithAssignee_ShowsName()
    {
        var item = new WorkItem
        {
            Id = 99,
            Type = WorkItemType.Task,
            Title = "Assigned Task",
            State = "Active",
            AssignedTo = "Jane Doe",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Jane Doe");
    }

    // ── Cancellation token honored between stages ─────────────────

    [Fact]
    public async Task RenderWorkItemAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var item = CreateWorkItem(1, "Cancel Me", "Active");
        item.SetField("System.Description", "Should not render this");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        await Should.ThrowAsync<OperationCanceledException>(
            () => _renderer.RenderWorkItemAsync(
                () => Task.FromResult<WorkItem?>(item), false, cts.Token));
    }

    // ── Tags truncation ─────────────────────────────────────────────

    [Fact]
    public async Task RenderWorkItemAsync_LongTags_Truncated()
    {
        var item = CreateWorkItem(1, "Many Tags", "Active");
        var longTags = string.Join("; ", Enumerable.Range(1, 100).Select(i => $"tag-{i}"));
        item.SetField("System.Tags", longTags);

        await _renderer.RenderWorkItemAsync(() => Task.FromResult<WorkItem?>(item), false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Tags:");
        output.ShouldContain("…");
    }

    // ── BuildStatusViewAsync — description rendering ───────────────

    [Fact]
    public async Task BuildStatusViewAsync_Description_RendersFullWidthSection()
    {
        var item = CreateWorkItem(1, "Described Item", "Active");
        item.SetField("System.Description", "<p>Acceptance criteria for the feature.</p>");

        var renderable = await _renderer.BuildStatusViewAsync(
            ItemFunc(item), NoPendingChanges(), CancellationToken.None);
        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldContain("Description");
        output.ShouldContain("Acceptance criteria for the feature.");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NoDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(1, "Plain Item", "Active");

        var renderable = await _renderer.BuildStatusViewAsync(
            ItemFunc(item), NoPendingChanges(), CancellationToken.None);
        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldNotContain("Description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_LongDescription_TruncatedWithIndicator()
    {
        var item = CreateWorkItem(1, "Long Desc", "Active");
        item.SetField("System.Description",
            string.Concat(Enumerable.Range(1, 20).Select(i => $"<p>Paragraph {i}</p>")));

        var renderable = await _renderer.BuildStatusViewAsync(
            ItemFunc(item), NoPendingChanges(), CancellationToken.None);
        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldContain("(+");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionExcludedFromExtendedGrid()
    {
        var item = CreateWorkItem(1, "Grid Check", "Active");
        item.SetField("System.Description", "<p>Unique description text</p>");

        var renderable = await _renderer.BuildStatusViewAsync(
            ItemFunc(item), NoPendingChanges(), CancellationToken.None);
        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        var occurrences = output.Split("Unique description text").Length - 1;
        occurrences.ShouldBe(1);
    }

    [Fact]
    public async Task BuildStatusViewAsync_MultiParagraph_PreservesStructure()
    {
        var item = CreateWorkItem(1, "Multi Para", "Active");
        item.SetField("System.Description", "<p>First paragraph</p><p>Second paragraph</p>");

        var renderable = await _renderer.BuildStatusViewAsync(
            ItemFunc(item), NoPendingChanges(), CancellationToken.None);
        _testConsole.Write(renderable);
        var output = _testConsole.Output;

        output.ShouldContain("First paragraph");
        output.ShouldContain("Second paragraph");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static Func<Task<WorkItem?>> ItemFunc(WorkItem item) =>
        () => Task.FromResult<WorkItem?>(item);

    private static Func<Task<IReadOnlyList<PendingChangeRecord>>> NoPendingChanges() =>
        () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>());

    private static WorkItem CreateWorkItem(int id, string title, string state, WorkItemType? type = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
