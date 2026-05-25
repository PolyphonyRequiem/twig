using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class DocumentRenderingTests
{
    // ═══════════════════════════════════════════════════════════════
    //  JsonRenderer + Document
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Json_SingleDocumentRoot_EmitsTopLevelObject()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document("processType", [
            new DocumentField("type", new RenderNode.KeyValue("type", RenderCell.String("Task"))),
            new DocumentField("totalStates", new RenderNode.KeyValue("totalStates", RenderCell.Integer(3))),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        json.RootElement.GetProperty("type").GetString().ShouldBe("Task");
        json.RootElement.GetProperty("totalStates").GetInt32().ShouldBe(3);
        // Single Document root suppresses Kind (consistent with Record).
        json.RootElement.TryGetProperty("kind", out _).ShouldBeFalse();
    }

    [Fact]
    public void Json_DocumentFieldWithTableNode_ProjectsAsArrayUnderFieldKey()
    {
        var writer = new StringWriter();
        var columns = new[] { new RenderColumn("name", "Name"), new RenderColumn("category", "Category") };
        var rows = new[]
        {
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["name"] = RenderCell.String("New"),
                ["category"] = RenderCell.String("Proposed"),
            }),
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["name"] = RenderCell.String("Active"),
                ["category"] = RenderCell.String("InProgress"),
            }),
        };
        var doc = new RenderNode.Document(null, [
            new DocumentField("states", new RenderNode.Table(null, columns, rows)),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        var states = json.RootElement.GetProperty("states");
        states.ValueKind.ShouldBe(JsonValueKind.Array);
        states.GetArrayLength().ShouldBe(2);
        states[0].GetProperty("name").GetString().ShouldBe("New");
        states[1].GetProperty("category").GetString().ShouldBe("InProgress");
    }

    [Fact]
    public void Json_DocumentFieldHumanOnly_IsOmittedFromJson()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField("visible", new RenderNode.KeyValue("visible", RenderCell.String("yes"))),
            new DocumentField(
                "humanDisplay",
                new RenderNode.Text("for humans only"),
                Audience: RenderAudience.HumanOnly),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        json.RootElement.TryGetProperty("visible", out _).ShouldBeTrue();
        json.RootElement.TryGetProperty("humanDisplay", out _).ShouldBeFalse();
    }

    [Fact]
    public void Json_DocumentFieldMachineOnly_IsEmitted()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "machineKey",
                new RenderNode.KeyValue("k", RenderCell.Integer(99)),
                Audience: RenderAudience.MachineOnly),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        json.RootElement.GetProperty("machineKey").GetInt32().ShouldBe(99);
    }

    [Fact]
    public void Json_DocumentFieldHumanOverride_IgnoredByJsonRenderer()
    {
        // HumanOverride is only consulted by the human renderer; JSON
        // always uses the structured Node.
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "states",
                new RenderNode.KeyValue("states", RenderCell.String("MACHINE")),
                HumanOverride: new RenderNode.Text("HUMAN_OVERRIDE")),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        json.RootElement.GetProperty("states").GetString().ShouldBe("MACHINE");
        writer.ToString().ShouldNotContain("HUMAN_OVERRIDE");
    }

    [Fact]
    public void Json_DocumentInArray_EmitsKindDiscriminator()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document("processType", [
            new DocumentField("type", new RenderNode.KeyValue("type", RenderCell.String("Task"))),
        ]);
        var tree = new RenderTree([
            new RenderNode.Text("preamble"),
            doc,
        ]);

        new JsonRenderer(writer).Render(tree);

        using var json = JsonDocument.Parse(writer.ToString());
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        json.RootElement[1].GetProperty("kind").GetString().ShouldBe("processType");
        json.RootElement[1].GetProperty("type").GetString().ShouldBe("Task");
    }

    [Fact]
    public void Json_CompactMode_NoIndentation()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField("id", new RenderNode.KeyValue("id", RenderCell.Integer(42))),
        ]);
        var tree = new RenderTree([doc]);

        new JsonRenderer(writer, indented: false).Render(tree);

        var output = writer.ToString();
        output.ShouldNotContain("\n");
        output.ShouldNotContain("  ");
        output.ShouldBe("{\"id\":42}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  MinimalRenderer + Document
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Minimal_DocumentKeyValueField_EmitsFieldKeyEqualsValue()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField("type", new RenderNode.KeyValue("ignored", RenderCell.String("Task"))),
            new DocumentField("count", new RenderNode.KeyValue("ignored", RenderCell.Integer(3))),
        ]);

        new MinimalRenderer(writer).Render(new RenderTree([doc]));

        // KeyValue inside a Document collapses to use the surrounding field key,
        // not the KeyValue's own (ignored) label.
        var lines = writer.ToString().Split(["\r\n", "\n"], System.StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("type=Task");
        lines.ShouldContain("count=3");
    }

    [Fact]
    public void Minimal_DocumentFieldHumanOnly_IsSkipped()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document(null, [
            new DocumentField("keep", new RenderNode.KeyValue("k", RenderCell.String("ok"))),
            new DocumentField(
                "drop",
                new RenderNode.KeyValue("k", RenderCell.String("hidden")),
                Audience: RenderAudience.HumanOnly),
        ]);

        new MinimalRenderer(writer).Render(new RenderTree([doc]));

        writer.ToString().ShouldContain("keep=ok");
        writer.ToString().ShouldNotContain("drop=");
        writer.ToString().ShouldNotContain("hidden");
    }

    [Fact]
    public void Minimal_DocumentKindPrefix_Emitted()
    {
        var writer = new StringWriter();
        var doc = new RenderNode.Document("workItem", [
            new DocumentField("id", new RenderNode.KeyValue("id", RenderCell.Integer(42))),
        ]);

        new MinimalRenderer(writer).Render(new RenderTree([doc]));

        var output = writer.ToString();
        output.ShouldContain("kind=workItem");
        output.ShouldContain("id=42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  IdsRenderer + Document
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ids_RecursesIntoDocumentFieldsAndExtractsIdCells()
    {
        var writer = new StringWriter();
        var columns = new[] { new RenderColumn("id", "ID"), new RenderColumn("name", "Name") };
        var rows = new[]
        {
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(10),
                ["name"] = RenderCell.String("Alpha"),
            }),
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(11),
                ["name"] = RenderCell.String("Beta"),
            }),
        };
        var doc = new RenderNode.Document(null, [
            new DocumentField("items", new RenderNode.Table(null, columns, rows)),
        ]);

        new IdsRenderer(writer).Render(new RenderTree([doc]));

        var lines = writer.ToString().Split(["\r\n", "\n"], System.StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldBe(["10", "11"]);
    }

    [Fact]
    public void Ids_DocumentFieldHumanOnly_IsSkipped()
    {
        var writer = new StringWriter();
        var rows = new[]
        {
            new RenderRow(null, new Dictionary<string, RenderCell> { ["id"] = RenderCell.Integer(99) }),
        };
        var columns = new[] { new RenderColumn("id", "ID") };
        var doc = new RenderNode.Document(null, [
            new DocumentField(
                "humanOnly",
                new RenderNode.Table(null, columns, rows),
                Audience: RenderAudience.HumanOnly),
        ]);

        new IdsRenderer(writer).Render(new RenderTree([doc]));

        writer.ToString().ShouldNotContain("99");
    }
}
