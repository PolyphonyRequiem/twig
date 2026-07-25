using System.Reflection;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

/// <summary>
/// Contract parity between <c>twig history</c> and <c>twig_history</c> (twig#241).
/// Both surfaces share one data contract, so behavior learned on one must transfer to the
/// other. These tests pin the shared writer's document shape and the tool's catalog wiring.
/// </summary>
public sealed class HistoryContractParityTests
{
    private static WorkItemHistory SampleHistory() => new(
        WorkItemId: 3316,
        Complete: true,
        Events:
        [
            new WorkItemHistoryEvent(
                UpdateId: 1, Revision: 1,
                ChangedAt: DateTimeOffset.Parse("2026-07-25T02:45:09.883Z"),
                ChangedBy: "Daniel Green",
                ChangedByIdentity: null,
                ChangedFields: ["System.State"],
                Fields: [],
                Relations: [],
                Detailed: false),
            new WorkItemHistoryEvent(
                UpdateId: 2, Revision: 1,
                ChangedAt: DateTimeOffset.Parse("2026-07-25T02:45:11.053Z"),
                ChangedBy: "Daniel Green",
                ChangedByIdentity: "dangreen@microsoft.com",
                ChangedFields: ["System.State"],
                Fields: [new WorkItemFieldChange("System.State", "To Do", "Doing")],
                Relations:
                [
                    new WorkItemRelationChange(
                        RelationChangeKind.Added,
                        "System.LinkTypes.Hierarchy-Forward",
                        3319,
                        new WorkItemRelationTarget(3319, "Child", "Task", "Doing", Deleted: false)),
                ],
                Detailed: true),
        ]);

    [Fact]
    public void SharedWriter_EmitsTheV1DocumentShape()
    {
        using var document = JsonDocument.Parse(WorkItemHistoryJsonWriter.Write(SampleHistory()));
        var root = document.RootElement;

        root.GetProperty("workItemId").GetInt32().ShouldBe(3316);
        root.GetProperty("complete").GetBoolean().ShouldBeTrue();
        root.GetProperty("eventCount").GetInt32().ShouldBe(2);

        var brief = root.GetProperty("events")[0];
        brief.GetProperty("updateId").GetInt32().ShouldBe(1);
        brief.GetProperty("revision").GetInt32().ShouldBe(1);
        brief.GetProperty("changedBy").GetString().ShouldBe("Daniel Green");
        brief.GetProperty("changed")[0].GetString().ShouldBe("System.State");
        // Brief events carry no values and no identity.
        brief.TryGetProperty("fields", out _).ShouldBeFalse();
        brief.TryGetProperty("changedByIdentity", out _).ShouldBeFalse();

        var detailed = root.GetProperty("events")[1];
        var state = detailed.GetProperty("fields").GetProperty("System.State");
        state.GetProperty("oldValue").GetString().ShouldBe("To Do");
        state.GetProperty("newValue").GetString().ShouldBe("Doing");
        detailed.GetProperty("changedByIdentity").GetString().ShouldBe("dangreen@microsoft.com");

        var relation = detailed.GetProperty("relations")[0];
        relation.GetProperty("kind").GetString().ShouldBe("added");
        relation.GetProperty("relationType").GetString().ShouldBe("System.LinkTypes.Hierarchy-Forward");
        relation.GetProperty("targetId").GetInt32().ShouldBe(3319);
        relation.GetProperty("target").GetProperty("title").GetString().ShouldBe("Child");
        relation.GetProperty("target").GetProperty("deleted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void SharedWriter_IsDeterministic()
    {
        // Repeated calls and diffs must be stable.
        WorkItemHistoryJsonWriter.Write(SampleHistory())
            .ShouldBe(WorkItemHistoryJsonWriter.Write(SampleHistory()));
    }

    [Fact]
    public void SharedWriter_EmitsExplicitNulls_NotOmittedProperties()
    {
        // A consumer must be able to distinguish a cleared field from an unchanged one.
        var history = new WorkItemHistory(1, true,
        [
            new WorkItemHistoryEvent(1, 1, ChangedAt: null, ChangedBy: null, ChangedByIdentity: null,
                ChangedFields: [], Fields: [new WorkItemFieldChange("System.AssignedTo", "x", null)],
                Relations: [], Detailed: true),
        ]);

        using var document = JsonDocument.Parse(WorkItemHistoryJsonWriter.Write(history));
        var evt = document.RootElement.GetProperty("events")[0];

        evt.GetProperty("changedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        evt.GetProperty("changedBy").ValueKind.ShouldBe(JsonValueKind.Null);

        var field = evt.GetProperty("fields").GetProperty("System.AssignedTo");
        field.GetProperty("oldValue").GetString().ShouldBe("x");
        field.GetProperty("newValue").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Option parsing parity ───────────────────────────────────────

    [Theory]
    [InlineData("8,11,14", new[] { 8, 11, 14 })]
    [InlineData(" 8 , 11 ", new[] { 8, 11 })]
    [InlineData("8", new[] { 8 })]
    public void DetailOption_ParsesUpdateIdLists(string detail, int[] expected)
    {
        var options = WorkItemHistoryOptionsParser.Parse(detail, null);

        options.IsSuccess.ShouldBeTrue();
        options.Value.DetailAll.ShouldBeFalse();
        options.Value.DetailUpdateIds!.OrderBy(i => i).ShouldBe(expected);
        foreach (var id in expected) options.Value.IsDetailed(id).ShouldBeTrue();
        options.Value.IsDetailed(9999).ShouldBeFalse();
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public void DetailOption_AcceptsAll(string detail)
    {
        var options = WorkItemHistoryOptionsParser.Parse(detail, null);

        options.IsSuccess.ShouldBeTrue();
        options.Value.DetailAll.ShouldBeTrue();
        options.Value.IsDetailed(12345).ShouldBeTrue();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("8,abc")]
    [InlineData("-3")]
    [InlineData("0")]
    public void DetailOption_RejectsGarbage(string detail)
    {
        var options = WorkItemHistoryOptionsParser.Parse(detail, null);

        options.IsSuccess.ShouldBeFalse();
        options.Error.ShouldContain("--detail");
    }

    [Fact]
    public void FieldOption_ParsesReferenceNamesCaseInsensitively()
    {
        var options = WorkItemHistoryOptionsParser.Parse(null, "System.State,Microsoft.VSTS.Common.Release");

        options.IsSuccess.ShouldBeTrue();
        options.Value.HasFieldFilter.ShouldBeTrue();
        options.Value.Fields!.Contains("system.state").ShouldBeTrue();
        options.Value.Fields.Contains("Microsoft.VSTS.Common.Release").ShouldBeTrue();
    }

    [Fact]
    public void NoOptions_YieldsBriefWithNoFilter()
    {
        var options = WorkItemHistoryOptionsParser.Parse(null, null);

        options.IsSuccess.ShouldBeTrue();
        options.Value.DetailAll.ShouldBeFalse();
        options.Value.HasFieldFilter.ShouldBeFalse();
        options.Value.IsDetailed(1).ShouldBeFalse();
    }

    // ── Catalog and dispatcher wiring ───────────────────────────────

    [Fact]
    public void HistoryTool_IsInTheCompactAndBatchableCatalogs()
    {
        McpToolCatalog.AllToolNames.ShouldContain("twig_history");
        // Appears in the compact/default catalog per the issue's Surfaces decision.
        McpToolCatalog.CompactToolNames.ShouldContain("twig_history");
        // Routing through ToolDispatcher makes it batchable via twig_batch with no
        // separate batch surface.
        McpToolCatalog.BatchableToolNames.ShouldContain("twig_history");
    }

    [Fact]
    public void HistoryTool_ExposesTheSameOptionNamesAsTheCli()
    {
        var parameters = typeof(ReadTools)
            .GetMethod(nameof(ReadTools.History))!
            .GetParameters()
            .Select(p => p.Name)
            .ToList();

        parameters.ShouldContain("id");
        parameters.ShouldContain("detail");
        parameters.ShouldContain("field");
    }

    [Fact]
    public void HistoryTool_IsAnnotatedReadOnly()
    {
        // History must leave workspace, cache, context, and pending changes untouched.
        var annotations = typeof(McpToolCatalog)
            .GetMethod("BuildAnnotations", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["twig_history"]);

        var type = annotations!.GetType();
        type.GetProperty("ReadOnlyHint")!.GetValue(annotations).ShouldBe(true);
        type.GetProperty("DestructiveHint")!.GetValue(annotations).ShouldBe(false);
    }
}
