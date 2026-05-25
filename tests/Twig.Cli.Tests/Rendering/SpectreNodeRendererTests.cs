using System.Collections.Generic;
using Shouldly;
using Spectre.Console.Testing;
using Twig.RenderTree;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests for <see cref="SpectreNodeRenderer"/>, the human-format leaf of the
/// AB#3301 seam. Verifies each <see cref="RenderNode"/> variant produces sensible
/// Spectre output and that severity maps to colour as expected.
/// </summary>
public sealed class SpectreNodeRendererTests
{
    [Fact]
    public void Text_Plain_WritesUnstyledLine()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([new RenderNode.Text("hello world")]);

        renderer.Render(tree);

        console.Output.ShouldContain("hello world");
    }

    [Fact]
    public void Text_ErrorSeverity_EmitsRedAnsi()
    {
        var (renderer, console) = CreateRenderer(emitAnsi: true);
        var tree = new RenderTree.RenderTree([
            new RenderNode.Text("something broke", Severity.Error),
        ]);

        renderer.Render(tree);

        // ANSI red foreground is 31; emit-ansi mode surfaces it in the output.
        console.Output.ShouldContain("something broke");
        console.Output.ShouldContain("\x1b[");
    }

    [Fact]
    public void Hint_WritesDimLine()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([new RenderNode.Hint("try --help")]);

        renderer.Render(tree);

        console.Output.ShouldContain("try --help");
    }

    [Fact]
    public void KeyValue_WritesLabelledLine()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([
            new RenderNode.KeyValue("State", RenderCell.String("Active")),
        ]);

        renderer.Render(tree);

        console.Output.ShouldContain("State");
        console.Output.ShouldContain("Active");
    }

    [Fact]
    public void Record_KindEmitsHeader_FieldsIndented()
    {
        var (renderer, console) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["title"] = RenderCell.String("Refactor the world"),
        };
        var tree = new RenderTree.RenderTree([
            new RenderNode.Record("workItem", fields),
        ]);

        renderer.Render(tree);

        console.Output.ShouldContain("workItem");
        console.Output.ShouldContain("id");
        console.Output.ShouldContain("42");
        console.Output.ShouldContain("title");
        console.Output.ShouldContain("Refactor the world");
    }

    [Fact]
    public void Table_RendersColumnHeadersAndCells()
    {
        var (renderer, console) = CreateRenderer();
        var columns = new[]
        {
            new RenderColumn("id", "ID"),
            new RenderColumn("title", "Title"),
        };
        var rows = new[]
        {
            new RenderRow(Kind: "workItem", Cells: new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(101),
                ["title"] = RenderCell.String("Alpha"),
            }),
            new RenderRow(Kind: "workItem", Cells: new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(102),
                ["title"] = RenderCell.String("Beta"),
            }),
        };
        var tree = new RenderTree.RenderTree([
            new RenderNode.Table(Caption: null, columns, rows),
        ]);

        renderer.Render(tree);

        console.Output.ShouldContain("ID");
        console.Output.ShouldContain("Title");
        console.Output.ShouldContain("101");
        console.Output.ShouldContain("Alpha");
        console.Output.ShouldContain("102");
        console.Output.ShouldContain("Beta");
    }

    [Fact]
    public void TreeView_RendersBoxDrawingHierarchy()
    {
        var (renderer, console) = CreateRenderer();
        var leaf = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["title"] = RenderCell.String("Leaf"),
            }),
            []);
        var root = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["title"] = RenderCell.String("Root"),
            }),
            [leaf]);
        var tree = new RenderTree.RenderTree([new RenderNode.TreeView(root)]);

        renderer.Render(tree);

        console.Output.ShouldContain("Root");
        console.Output.ShouldContain("Leaf");
        // Spectre's Tree uses box-drawing characters.
        console.Output.ShouldContain("─");
    }

    [Fact]
    public void Section_HeaderWrittenBeforeChildren()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([
            new RenderNode.Section("Pending changes", [
                new RenderNode.Text("first"),
                new RenderNode.Text("second"),
            ]),
        ]);

        renderer.Render(tree);

        var output = console.Output;
        output.ShouldContain("Pending changes");
        output.ShouldContain("first");
        output.ShouldContain("second");

        output.IndexOf("Pending changes").ShouldBeLessThan(output.IndexOf("first"));
        output.IndexOf("first").ShouldBeLessThan(output.IndexOf("second"));
    }

    [Fact]
    public void EmptyTree_ProducesNoOutput()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([]);

        renderer.Render(tree);

        console.Output.ShouldBeEmpty();
    }

    [Fact]
    public void MarkupCharactersInDisplayText_AreEscaped()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([
            new RenderNode.Text("[bold]not actually bold[/]"),
        ]);

        // Must not throw a Spectre markup parse error.
        renderer.Render(tree);

        console.Output.ShouldContain("[bold]not actually bold[/]");
    }

    private static (SpectreNodeRenderer Renderer, TestConsole Console) CreateRenderer(bool emitAnsi = false)
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        if (emitAnsi)
        {
            console.EmitAnsiSequences();
        }

        return (new SpectreNodeRenderer(console), console);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Document + audience rendering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Document_MachineOnlyField_IsSkipped()
    {
        var (renderer, console) = CreateRenderer();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "visibleField",
                new RenderNode.Text("you should see this")),
            new DocumentField(
                "hiddenField",
                new RenderNode.Text("YOU_SHOULD_NOT_SEE_THIS"),
                Audience: RenderAudience.MachineOnly),
        ]);

        renderer.Render(new RenderTree.RenderTree([doc]));

        console.Output.ShouldContain("you should see this");
        console.Output.ShouldNotContain("YOU_SHOULD_NOT_SEE_THIS");
    }

    [Fact]
    public void Document_HumanOnlyField_IsEmitted()
    {
        var (renderer, console) = CreateRenderer();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "humanLine",
                new RenderNode.Text("human-only payload"),
                Audience: RenderAudience.HumanOnly),
        ]);

        renderer.Render(new RenderTree.RenderTree([doc]));

        console.Output.ShouldContain("human-only payload");
    }

    [Fact]
    public void Document_HumanOverride_UsedInsteadOfMachineNode()
    {
        var (renderer, console) = CreateRenderer();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "states",
                new RenderNode.KeyValue("states", RenderCell.String("MACHINE_PAYLOAD")),
                HumanOverride: new RenderNode.Text("HUMAN_LINE")),
        ]);

        renderer.Render(new RenderTree.RenderTree([doc]));

        console.Output.ShouldContain("HUMAN_LINE");
        console.Output.ShouldNotContain("MACHINE_PAYLOAD");
    }

    [Fact]
    public void Document_FieldHeader_EmittedBeforeChild()
    {
        var (renderer, console) = CreateRenderer();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "states",
                new RenderNode.Text("New"),
                Header: "States"),
        ]);

        renderer.Render(new RenderTree.RenderTree([doc]));

        var output = console.Output;
        var headerPos = output.IndexOf("States", System.StringComparison.Ordinal);
        var contentPos = output.IndexOf("New", System.StringComparison.Ordinal);
        headerPos.ShouldBeGreaterThanOrEqualTo(0);
        contentPos.ShouldBeGreaterThan(headerPos);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Markup rendering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Markup_PassesContentThroughSpectreMarkupParser()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([new RenderNode.Markup("Set active item: #42 Foo [[[green]Active[/]]]")]);

        renderer.Render(tree);

        var output = console.Output;
        output.ShouldContain("Set active item: #42 Foo [Active]");
        output.ShouldNotContain("[green]");
        output.ShouldNotContain("[/]");
    }

    [Fact]
    public void Markup_LiteralBracketsAreUnescaped()
    {
        var (renderer, console) = CreateRenderer();
        var tree = new RenderTree.RenderTree([new RenderNode.Markup("Plain [[brackets]] only")]);

        renderer.Render(tree);

        console.Output.ShouldContain("Plain [brackets] only");
    }
}
