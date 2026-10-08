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

        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("Description");
        output.ShouldContain("detailed work item description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_HtmlDescription_StrippedAndRendered()
    {
        var item = CreateWorkItem(11, "HTML Desc", "Active");
        item.SetField("System.Description", "<div><p>Hello <b>world</b></p></div>");

        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("Hello world");
        output.ShouldNotContain("<div>");
        output.ShouldNotContain("<b>");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NoDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(12, "No Desc", "Active");

        var output = await RenderStatusViewAsync(item);

        output.ShouldNotContain("Description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_WhitespaceDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(13, "Blank Desc", "Active");
        item.SetField("System.Description", "   ");

        var output = await RenderStatusViewAsync(item);

        output.ShouldNotContain("Description");
    }

    [Fact]
    public async Task BuildStatusViewAsync_HtmlOnlyTagsDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(14, "Empty HTML", "Active");
        item.SetField("System.Description", "<div>  </div>");

        var output = await RenderStatusViewAsync(item);

        output.ShouldNotContain("Description");
    }

    // ── Description excluded from extended field rows ────────────────

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionNotInExtendedFields_AutoDetection()
    {
        var item = CreateWorkItem(15, "Auto Detect", "Active");
        item.SetField("System.Description", "Should appear in description section only.");
        item.SetField("Custom.Priority", "High");

        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("Should appear in description section only");
        output.ShouldContain("High");
        output.ShouldNotContain("Description:");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DescriptionNotInExtendedFields_StatusFieldEntries()
    {
        var item = CreateWorkItem(16, "Field Entries", "Active");
        item.SetField("System.Description", "Full-width description text here.");
        item.SetField("Custom.Priority", "Medium");

        List<StatusFieldEntry> statusFieldEntries = [new("System.Description", true), new("Custom.Priority", true)];

        var output = await RenderStatusViewAsync(item, statusFieldEntries);

        output.ShouldContain("Full-width description text here");
        output.ShouldContain("Medium");
        output.ShouldNotContain("Description:");
    }

    // ── Long / multi-paragraph integration tests ──────────────────────

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public async Task BuildStatusViewAsync_LongDescription_KeepsSummaryClosedRoundedFrameAndPreviewLimit(int width)
    {
        _testConsole.Profile.Width = width;
        _testConsole.Profile.Capabilities.Unicode = true;
        var item = CreateWorkItem(17, "Long Desc", "Active");
        // 35 paragraphs → exceeds MaxDescriptionLines (30), triggers "(+N more lines)" marker
        var paragraphs = string.Concat(Enumerable.Range(1, 35).Select(i => $"<p>Paragraph {i} content.</p>"));
        item.SetField("System.Description", $"<div>{paragraphs}</div>");

        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("Description");
        output.ShouldContain("Paragraph 1 content");
        output.ShouldContain("(+");
        output.ShouldContain("more lines)");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r', ' ')).ToArray();
        lines[0].ShouldStartWith("#17 ");
        lines.Count(line => line.Contains("Long Desc", StringComparison.Ordinal)).ShouldBe(2);
        var top = lines.Single(line => line.StartsWith("╭", StringComparison.Ordinal));
        top.ShouldEndWith("╮");
        lines[^1].ShouldStartWith("╰");
        lines[^1].ShouldEndWith("╯");
        foreach (var line in lines.SkipWhile(line => line != top).Skip(1).SkipLast(1))
        {
            line.ShouldStartWith("│");
            line.ShouldEndWith("│");
        }
        output.ShouldNotContain("Paragraph 35 content");
    }

    [Fact]
    public async Task BuildStatusViewAsync_MultiParagraph_PreservesStructure()
    {
        var item = CreateWorkItem(18, "Multi Para", "Active");
        item.SetField("System.Description", "<p>First paragraph</p><p>Second paragraph</p>");

        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("First paragraph");
        output.ShouldContain("Second paragraph");
    }

    [Fact]
    public async Task FullContent_DescriptionRemainsRichAndCompleteWithoutFieldMetadata()
    {
        var item = CreateWorkItem(19, "Full cached description", "Active");
        item.SetField("System.Description", "<h2>Plan</h2><pre>    if (ready) {\n        finish();\n    }</pre>" +
            string.Concat(Enumerable.Range(1, 65).Select(i => $"<p>description-{i:D2}</p>")));
        item.SetField("Custom.Unknown", "<p>" + new string('x', 200) + " UNKNOWN-FIELD-TAIL</p>");
        var output = await RenderStatusViewAsync(item, fullContent: true);
        output.ShouldContain("description-65");
        output.ShouldContain("UNKNOWN-FIELD-TAIL");
        output.ShouldContain("    if (ready)");
        output.ShouldContain("        finish();");
        output.ShouldNotContain("<h2>");
        output.ShouldNotContain("<pre>");
        output.ShouldNotContain("more lines");
        item.Fields["System.Description"]!.ShouldContain("<h2>Plan</h2>");
        item.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task FullContent_AllCachedHtmlTailsRemainReadableDespiteSummaryFieldSelection()
    {
        var item = CreateWorkItem(20, "Full HTML fields", "Active");
        var definitions = new List<FieldDefinition>();
        for (var i = 1; i <= 12; i++)
        {
            var reference = $"Custom.Html{i}";
            definitions.Add(new FieldDefinition(reference, $"HTML field {i}", "html", false));
            item.SetField(reference, $"<p>Body-{i:D2}</p><table><tr><td>{new string('x', 200)} TAIL-{i:D2}</td></tr></table>");
        }
        var output = await RenderStatusViewAsync(item, statusFieldEntries: [], fieldDefinitions: definitions, fullContent: true);
        output.ShouldContain("TAIL-01");
        output.ShouldContain("TAIL-12");
        output.ShouldNotContain("more lines");
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

    private async Task<string> RenderStatusViewAsync(WorkItem item, List<StatusFieldEntry>? statusFieldEntries = null,
        IReadOnlyList<FieldDefinition>? fieldDefinitions = null, bool fullContent = false)
    {
        var renderable = await _renderer.BuildStatusViewAsync(
            item,
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
            statusFieldEntries: statusFieldEntries, fieldDefinitions: fieldDefinitions, fullContent: fullContent);
        _testConsole.Write(renderable);
        return _testConsole.Output;
    }
}
