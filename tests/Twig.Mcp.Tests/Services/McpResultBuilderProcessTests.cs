using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpResultBuilderProcessTests
{
    // ── FormatProcessList ───────────────────────────────────────────

    [Fact]
    public void FormatProcessList_EmptyList_ReturnsEmptyArray()
    {
        var result = McpResultBuilder.FormatProcessList([]);
        var root = ParseJson(result);

        root.GetProperty("types").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalTypes").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatProcessList_SingleType_WritesTypeSummary()
    {
        var types = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Bug",
                States = new[]
                {
                    new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
                    new StateEntry("Active", StateCategory.InProgress, "007acc"),
                    new StateEntry("Closed", StateCategory.Completed, "339933"),
                },
                ValidChildTypes = ["Task"],
                ColorHex = "CC293D",
            }
        };

        var result = McpResultBuilder.FormatProcessList(types);
        var root = ParseJson(result);

        root.GetProperty("totalTypes").GetInt32().ShouldBe(1);
        var arr = root.GetProperty("types");
        arr.GetArrayLength().ShouldBe(1);

        var entry = arr[0];
        entry.GetProperty("typeName").GetString().ShouldBe("Bug");
        entry.GetProperty("stateCount").GetInt32().ShouldBe(3);
        entry.GetProperty("childTypeCount").GetInt32().ShouldBe(1);
        entry.GetProperty("color").GetString().ShouldBe("CC293D");
    }

    [Fact]
    public void FormatProcessList_MultipleTypes_WritesAll()
    {
        var types = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Epic",
                States = [new StateEntry("New", StateCategory.Proposed, null)],
                ValidChildTypes = ["Feature", "User Story"],
                ColorHex = "FF7B00",
            },
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States =
                [
                    new StateEntry("To Do", StateCategory.Proposed, null),
                    new StateEntry("Done", StateCategory.Completed, null),
                ],
                ValidChildTypes = [],
                ColorHex = null,
            }
        };

        var result = McpResultBuilder.FormatProcessList(types);
        var root = ParseJson(result);

        root.GetProperty("totalTypes").GetInt32().ShouldBe(2);
        var arr = root.GetProperty("types");
        arr.GetArrayLength().ShouldBe(2);

        arr[0].GetProperty("typeName").GetString().ShouldBe("Epic");
        arr[0].GetProperty("stateCount").GetInt32().ShouldBe(1);
        arr[0].GetProperty("childTypeCount").GetInt32().ShouldBe(2);
        arr[0].GetProperty("color").GetString().ShouldBe("FF7B00");

        arr[1].GetProperty("typeName").GetString().ShouldBe("Task");
        arr[1].GetProperty("stateCount").GetInt32().ShouldBe(2);
        arr[1].GetProperty("childTypeCount").GetInt32().ShouldBe(0);
        arr[1].GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatProcessList_NullColor_WritesNull()
    {
        var types = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Issue",
                States = [new StateEntry("Open", StateCategory.Proposed, null)],
                ValidChildTypes = [],
                ColorHex = null,
            }
        };

        var result = McpResultBuilder.FormatProcessList(types);
        var root = ParseJson(result);

        root.GetProperty("types")[0].GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── FormatProcessType ───────────────────────────────────────────

    [Fact]
    public void FormatProcessType_WritesTypeNameAndColor()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States = [new StateEntry("New", StateCategory.Proposed, "b2b2b2")],
            ValidChildTypes = [],
            ColorHex = "CC293D",
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("typeName").GetString().ShouldBe("Bug");
        root.GetProperty("color").GetString().ShouldBe("CC293D");
    }

    [Fact]
    public void FormatProcessType_NullColor_WritesNull()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
            ColorHex = null,
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatProcessType_WritesStatesWithCategoryAndColor()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States =
            [
                new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
                new StateEntry("Active", StateCategory.InProgress, "007acc"),
                new StateEntry("Resolved", StateCategory.Resolved, "ff9d00"),
                new StateEntry("Closed", StateCategory.Completed, "339933"),
                new StateEntry("Removed", StateCategory.Removed, "ffffff"),
            ],
            ValidChildTypes = [],
            ColorHex = "CC293D",
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        var states = root.GetProperty("states");
        states.GetArrayLength().ShouldBe(5);

        states[0].GetProperty("name").GetString().ShouldBe("New");
        states[0].GetProperty("category").GetString().ShouldBe("Proposed");
        states[0].GetProperty("color").GetString().ShouldBe("b2b2b2");

        states[3].GetProperty("name").GetString().ShouldBe("Closed");
        states[3].GetProperty("category").GetString().ShouldBe("Completed");

        states[4].GetProperty("name").GetString().ShouldBe("Removed");
        states[4].GetProperty("category").GetString().ShouldBe("Removed");
    }

    [Fact]
    public void FormatProcessType_StateWithNullColor_WritesNull()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("states")[0].GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatProcessType_WritesFields()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States = [new StateEntry("New", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };
        var fields = new[]
        {
            new FieldDefinition("System.Title", "Title", "String", false),
            new FieldDefinition("System.State", "State", "String", true),
            new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
        };

        var result = McpResultBuilder.FormatProcessType(type, fields);
        var root = ParseJson(result);

        var fieldsArr = root.GetProperty("fields");
        fieldsArr.GetArrayLength().ShouldBe(3);

        fieldsArr[0].GetProperty("referenceName").GetString().ShouldBe("System.Title");
        fieldsArr[0].GetProperty("displayName").GetString().ShouldBe("Title");
        fieldsArr[0].GetProperty("dataType").GetString().ShouldBe("String");
        fieldsArr[0].GetProperty("isReadOnly").GetBoolean().ShouldBeFalse();

        fieldsArr[1].GetProperty("isReadOnly").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void FormatProcessType_EmptyFields_WritesEmptyArray()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("fields").GetArrayLength().ShouldBe(0);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatProcessType_WritesTransitions()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States =
            [
                new StateEntry("New", StateCategory.Proposed, null),
                new StateEntry("Active", StateCategory.InProgress, null),
                new StateEntry("Removed", StateCategory.Removed, null),
            ],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        var transitions = root.GetProperty("transitions");
        // 3 states -> 3*2 = 6 transitions
        transitions.GetArrayLength().ShouldBe(6);

        // New -> Active = Forward
        var t0 = transitions[0];
        t0.GetProperty("from").GetString().ShouldBe("New");
        t0.GetProperty("to").GetString().ShouldBe("Active");
        t0.GetProperty("kind").GetString().ShouldBe("Forward");

        // New -> Removed = Cut
        var t1 = transitions[1];
        t1.GetProperty("from").GetString().ShouldBe("New");
        t1.GetProperty("to").GetString().ShouldBe("Removed");
        t1.GetProperty("kind").GetString().ShouldBe("Cut");

        // Active -> Removed = Cut
        var t3 = transitions[3];
        t3.GetProperty("from").GetString().ShouldBe("Active");
        t3.GetProperty("to").GetString().ShouldBe("Removed");
        t3.GetProperty("kind").GetString().ShouldBe("Cut");

        // Removed -> New = Forward (transitioning out of removed is forward)
        var t4 = transitions[4];
        t4.GetProperty("from").GetString().ShouldBe("Removed");
        t4.GetProperty("to").GetString().ShouldBe("New");
        t4.GetProperty("kind").GetString().ShouldBe("Forward");
    }

    [Fact]
    public void FormatProcessType_SingleState_NoTransitions()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("transitions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatProcessType_WritesValidChildTypes()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Epic",
            States = [new StateEntry("New", StateCategory.Proposed, null)],
            ValidChildTypes = ["Feature", "User Story"],
            ColorHex = "FF7B00",
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        var childTypes = root.GetProperty("validChildTypes");
        childTypes.GetArrayLength().ShouldBe(2);
        childTypes[0].GetString().ShouldBe("Feature");
        childTypes[1].GetString().ShouldBe("User Story");
    }

    [Fact]
    public void FormatProcessType_NoChildTypes_WritesEmptyArray()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("validChildTypes").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatProcessType_WritesCounts()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States =
            [
                new StateEntry("New", StateCategory.Proposed, null),
                new StateEntry("Active", StateCategory.InProgress, null),
                new StateEntry("Closed", StateCategory.Completed, null),
            ],
            ValidChildTypes = [],
        };
        var fields = new[]
        {
            new FieldDefinition("System.Title", "Title", "String", false),
            new FieldDefinition("System.State", "State", "String", true),
        };

        var result = McpResultBuilder.FormatProcessType(type, fields);
        var root = ParseJson(result);

        root.GetProperty("stateCount").GetInt32().ShouldBe(3);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatProcessType_NoStates_WritesEmptyArraysAndZeroCounts()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Custom",
            States = [],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        var root = ParseJson(result);

        root.GetProperty("states").GetArrayLength().ShouldBe(0);
        root.GetProperty("transitions").GetArrayLength().ShouldBe(0);
        root.GetProperty("stateCount").GetInt32().ShouldBe(0);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatProcessList_ResultIsNotError()
    {
        var result = McpResultBuilder.FormatProcessList([]);
        result.IsError.ShouldBeNull();
    }

    [Fact]
    public void FormatProcessType_ResultIsNotError()
    {
        var type = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new StateEntry("To Do", StateCategory.Proposed, null)],
            ValidChildTypes = [],
        };

        var result = McpResultBuilder.FormatProcessType(type, []);
        result.IsError.ShouldBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
