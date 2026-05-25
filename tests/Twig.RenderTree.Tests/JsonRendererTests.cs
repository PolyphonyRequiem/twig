using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class JsonRendererTests
{
    [Fact]
    public void SingleRootRecord_EmitsObjectOfFields_NoKind()
    {
        // Parity case: this is the shape JsonOutputFormatter.FormatSetConfirmation produces.
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["title"] = RenderCell.String("Fix login bug"),
            ["state"] = RenderCell.String("Active"),
            ["type"] = RenderCell.String("Task"),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        var json = writer.ToString();
        json.ShouldContain("\"id\": 42");
        json.ShouldContain("\"title\": \"Fix login bug\"");
        json.ShouldContain("\"state\": \"Active\"");
        json.ShouldContain("\"type\": \"Task\"");
        json.ShouldNotContain("\"kind\"");
        // Indented output starts with `{` and ends with `}`.
        json.TrimStart().ShouldStartWith("{");
        json.TrimEnd().ShouldEndWith("}");
    }

    [Fact]
    public void RenderValue_AllVariantsProjectCorrectly()
    {
        var (renderer, writer) = CreateRenderer();
        var dt = new System.DateTimeOffset(2026, 5, 24, 10, 30, 0, System.TimeSpan.Zero);
        var fields = new Dictionary<string, RenderCell>
        {
            ["str"] = RenderCell.String("text"),
            ["int"] = RenderCell.Integer(7),
            ["dec"] = new RenderCell("3.14", new RenderValue.Decimal(3.14m)),
            ["bool"] = RenderCell.Boolean(true),
            ["when"] = new RenderCell("2026-05-24", new RenderValue.DateTime(dt)),
            ["nul"] = new RenderCell("—", new RenderValue.Null()),
        };
        var tree = new RenderTree([new RenderNode.Record(null, fields)]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        var root = doc.RootElement;
        root.GetProperty("str").GetString().ShouldBe("text");
        root.GetProperty("int").GetInt32().ShouldBe(7);
        root.GetProperty("dec").GetDecimal().ShouldBe(3.14m);
        root.GetProperty("bool").GetBoolean().ShouldBeTrue();
        root.GetProperty("when").GetString().ShouldBe("2026-05-24T10:30:00.0000000+00:00");
        root.GetProperty("nul").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void AbsentValues_AreOmittedFromObject()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["present"] = RenderCell.String("here"),
            ["missing"] = RenderCell.DisplayOnly("—"),
        };
        var tree = new RenderTree([new RenderNode.Record(null, fields)]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.TryGetProperty("present", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("missing", out _).ShouldBeFalse();
    }

    [Fact]
    public void SingleRootTable_EmitsTopLevelArrayOfRowObjects()
    {
        var (renderer, writer) = CreateRenderer();
        var columns = new[]
        {
            new RenderColumn("id", "ID"),
            new RenderColumn("title", "Title"),
        };
        var rows = new[]
        {
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(1),
                ["title"] = RenderCell.String("Alpha"),
            }),
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(2),
                ["title"] = RenderCell.String("Beta"),
            }),
        };
        var tree = new RenderTree([new RenderNode.Table(null, columns, rows)]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(2);
        doc.RootElement[0].GetProperty("id").GetInt32().ShouldBe(1);
        doc.RootElement[0].GetProperty("title").GetString().ShouldBe("Alpha");
        doc.RootElement[0].GetProperty("kind").GetString().ShouldBe("workItem");
        doc.RootElement[1].GetProperty("id").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void TreeView_EmitsNestedChildrenArrays()
    {
        var (renderer, writer) = CreateRenderer();
        var leaf = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(3),
                ["title"] = RenderCell.String("Leaf"),
            }),
            []);
        var root = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(1),
                ["title"] = RenderCell.String("Root"),
            }),
            [leaf]);
        var tree = new RenderTree([new RenderNode.TreeView(root)]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        var rootJson = doc.RootElement[0];
        rootJson.GetProperty("id").GetInt32().ShouldBe(1);
        rootJson.GetProperty("children").GetArrayLength().ShouldBe(1);
        rootJson.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(3);
        // Leaf node has no children property when its children list is empty.
        rootJson.GetProperty("children")[0].TryGetProperty("children", out _).ShouldBeFalse();
    }

    [Fact]
    public void MultipleRootNodes_EmittedAsArray()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Text("first"),
            new RenderNode.Text("second"),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(2);
        doc.RootElement[0].GetProperty("text").GetString().ShouldBe("first");
        doc.RootElement[1].GetProperty("text").GetString().ShouldBe("second");
    }

    [Fact]
    public void TextWithSeverity_EmitsSeverityProperty()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Text("oops", Severity.Error),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement[0].GetProperty("text").GetString().ShouldBe("oops");
        doc.RootElement[0].GetProperty("severity").GetString().ShouldBe("Error");
    }

    [Fact]
    public void Hint_IsOmittedFromOutput()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Hint("dim"),
            new RenderNode.Text("visible"),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetArrayLength().ShouldBe(1);
        doc.RootElement[0].GetProperty("text").GetString().ShouldBe("visible");
    }

    [Fact]
    public void Section_EmitsHeaderAndChildren()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Section("pending", [
                new RenderNode.Text("first"),
                new RenderNode.Text("second"),
            ]),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        var section = doc.RootElement[0];
        section.GetProperty("header").GetString().ShouldBe("pending");
        section.GetProperty("children").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void KeyValue_EmitsKeyAndValueWithType()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.KeyValue("count", RenderCell.Integer(7)),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement[0].GetProperty("key").GetString().ShouldBe("count");
        doc.RootElement[0].GetProperty("value").GetInt32().ShouldBe(7);
    }

    [Fact]
    public void RecordInArray_EmitsKindProperty()
    {
        // When a Record is one of multiple roots, kind IS emitted (it carries
        // discriminator meaning in a heterogeneous list). Only the special
        // "single root Record" case suppresses it for backward-compat with the
        // legacy SetConfirmation shape.
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(1),
        };
        var tree = new RenderTree([
            new RenderNode.Text("preamble"),
            new RenderNode.Record("workItem", fields),
        ]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement[1].GetProperty("kind").GetString().ShouldBe("workItem");
        doc.RootElement[1].GetProperty("id").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void EmptyTree_EmitsEmptyArray()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([]);

        renderer.Render(tree);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(0);
    }

    private static (JsonRenderer Renderer, StringWriter Writer) CreateRenderer()
    {
        var writer = new StringWriter();
        return (new JsonRenderer(writer), writer);
    }
}
