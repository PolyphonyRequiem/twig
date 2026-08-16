using System.Text.Json;
using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

/// <summary>
/// Tests for <see cref="RenderValue.Array"/> — the array-valued cell added by ADO #154 so a
/// single <see cref="RenderRow"/> can carry a collection.
/// </summary>
public class RenderValueArrayTests
{
    private static string RenderJson(RenderTree tree)
    {
        var sw = new StringWriter();
        new JsonRenderer(sw).Render(tree);
        return sw.ToString();
    }

    private static RenderTree OneRowWith(string key, RenderValue value)
    {
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(1),
            [key] = new RenderCell(string.Empty, value),
        };
        return new RenderTree([new RenderNode.Table(null, [], [new RenderRow(null, cells)])]);
    }

    [Fact]
    public void JsonRenderer_ArrayOfObjects_ProjectsAsJsonArrayOfObjects()
    {
        var items = new List<RenderCell>
        {
            new(string.Empty, new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["targetId"] = RenderCell.Integer(200),
            })),
            new(string.Empty, new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["targetId"] = RenderCell.Integer(300),
            })),
        };

        var json = RenderJson(OneRowWith("links", new RenderValue.Array(items)));

        using var doc = JsonDocument.Parse(json);
        var links = doc.RootElement[0].GetProperty("links");
        links.ValueKind.ShouldBe(JsonValueKind.Array);
        links.GetArrayLength().ShouldBe(2);
        links[0].GetProperty("targetId").GetInt32().ShouldBe(200);
        links[1].GetProperty("targetId").GetInt32().ShouldBe(300);
    }

    [Fact]
    public void JsonRenderer_EmptyArray_ProjectsAsEmptyJsonArrayNotOmitted()
    {
        var json = RenderJson(OneRowWith("links", new RenderValue.Array([])));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement[0].TryGetProperty("links", out var links).ShouldBeTrue();
        links.ValueKind.ShouldBe(JsonValueKind.Array);
        links.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void JsonRenderer_ArrayOfScalars_ProjectsTypedElements()
    {
        var items = new List<RenderCell>
        {
            RenderCell.Integer(7),
            RenderCell.String("seven"),
            RenderCell.Boolean(true),
        };

        var json = RenderJson(OneRowWith("mixed", new RenderValue.Array(items)));

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement[0].GetProperty("mixed");
        arr[0].GetInt32().ShouldBe(7);
        arr[1].GetString().ShouldBe("seven");
        arr[2].GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// 🔴 An Absent ELEMENT is rendered, not skipped. Object properties and table cells drop
    /// Absent values, but dropping an array element would shorten the array and shift every
    /// later index — a silent corruption rather than an omission.
    /// </summary>
    [Fact]
    public void JsonRenderer_AbsentElement_IsRenderedRatherThanShorteningTheArray()
    {
        var items = new List<RenderCell>
        {
            RenderCell.Integer(1),
            RenderCell.DisplayOnly("no machine value"),
            RenderCell.Integer(3),
        };

        var json = RenderJson(OneRowWith("mixed", new RenderValue.Array(items)));

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement[0].GetProperty("mixed");
        arr.GetArrayLength().ShouldBe(3);
        arr[0].GetInt32().ShouldBe(1);
        arr[2].GetInt32().ShouldBe(3);
    }

    [Fact]
    public void JsonRenderer_NestedArrayInsideAnObjectElement_Projects()
    {
        var inner = new RenderValue.Array([RenderCell.Integer(9)]);
        var items = new List<RenderCell>
        {
            new(string.Empty, new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["inner"] = new RenderCell(string.Empty, inner),
            })),
        };

        var json = RenderJson(OneRowWith("outer", new RenderValue.Array(items)));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement[0].GetProperty("outer")[0].GetProperty("inner")[0].GetInt32().ShouldBe(9);
    }

    /// <summary>
    /// 🔴 The IDs renderer does NOT descend into array cells, even when their elements are
    /// Objects carrying an <c>id</c> key. Those ids belong to OTHER entities (a link's target),
    /// not to this row. Descending corrupted <c>show-batch -o ids</c> — caught by
    /// <c>ShowBatchLinksTests.ShowBatch_IdsFormat_IsNotPollutedByRelationTargetIds</c>.
    /// </summary>
    [Fact]
    public void IdsRenderer_DoesNotDescendIntoArrayCells()
    {
        var items = new List<RenderCell>
        {
            new(string.Empty, new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                // An `id` key on an array element — the exact shape `relations` produces.
                ["id"] = RenderCell.Integer(777),
            })),
        };

        var sw = new StringWriter();
        new IdsRenderer(sw).Render(OneRowWith("relations", new RenderValue.Array(items)));

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        lines.ShouldBe(["1"]);
        lines.ShouldNotContain("777");
    }

    /// <summary>
    /// The contrast case that proves the rule above is about ARRAYS, not about nesting:
    /// an <see cref="RenderValue.Object"/> cell IS still descended into, because it carries
    /// more of this row's own data.
    /// </summary>
    [Fact]
    public void IdsRenderer_StillDescendsIntoObjectCells()
    {
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["fields"] = new RenderCell(string.Empty, new RenderValue.Object(
                new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["id"] = RenderCell.Integer(42),
                })),
        };
        var tree = new RenderTree([new RenderNode.Table(null, [], [new RenderRow(null, cells)])]);

        var sw = new StringWriter();
        new IdsRenderer(sw).Render(tree);

        sw.ToString().Trim().ShouldBe("42");
    }

    [Fact]
    public void RenderValue_Array_ParticipatesInTheClosedUnionSwitch()
    {
        RenderValue value = new RenderValue.Array([RenderCell.Integer(1)]);

        var tag = value switch
        {
            RenderValue.Array arr => $"arr:{arr.Items.Count}",
            RenderValue.Object obj => $"obj:{obj.Cells.Count}",
            _ => "other",
        };

        tag.ShouldBe("arr:1");
    }
}
