using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

public sealed class ProcessToolsTests : ReadToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  twig_process (no args) — list all types
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Process_NoArgs_NoTypes_ReturnsError()
    {
        _processTypeStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProcessTypeRecord>());

        var sut = CreateProcessSut();
        var result = await sut.Process();

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No process types found");
    }

    [Fact]
    public async Task Process_NoArgs_ReturnsTypesList()
    {
        _processTypeStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ProcessTypeRecord
                {
                    TypeName = "Bug",
                    States = [new StateEntry("New", StateCategory.Proposed, "b2b2b2"), new StateEntry("Closed", StateCategory.Completed, "339933")],
                    ValidChildTypes = ["Task"],
                    ColorHex = "CC293D",
                },
                new ProcessTypeRecord
                {
                    TypeName = "Task",
                    States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                    ValidChildTypes = [],
                    ColorHex = null,
                }
            });

        var sut = CreateProcessSut();
        var result = await sut.Process();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("totalTypes").GetInt32().ShouldBe(2);
        var types = root.GetProperty("types");
        types.GetArrayLength().ShouldBe(2);

        types[0].GetProperty("typeName").GetString().ShouldBe("Bug");
        types[0].GetProperty("stateCount").GetInt32().ShouldBe(2);
        types[0].GetProperty("childTypeCount").GetInt32().ShouldBe(1);
        types[0].GetProperty("color").GetString().ShouldBe("CC293D");

        types[1].GetProperty("typeName").GetString().ShouldBe("Task");
        types[1].GetProperty("stateCount").GetInt32().ShouldBe(1);
        types[1].GetProperty("childTypeCount").GetInt32().ShouldBe(0);
        types[1].GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Process_NoArgs_SingleType_ReturnsCorrectTotalTypes()
    {
        _processTypeStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ProcessTypeRecord
                {
                    TypeName = "Epic",
                    States = [new StateEntry("New", StateCategory.Proposed, null)],
                    ValidChildTypes = ["Feature"],
                    ColorHex = "FF7B00",
                }
            });

        var sut = CreateProcessSut();
        var result = await sut.Process();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("totalTypes").GetInt32().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_process with type — type details
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Process_WithType_TypeNotFound_ReturnsError()
    {
        _processTypeStore.GetByNameAsync("Unknown", Arg.Any<CancellationToken>())
            .Returns((ProcessTypeRecord?)null);

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Unknown");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    [Fact]
    public async Task Process_WithType_ReturnsTypeDetail()
    {
        _processTypeStore.GetByNameAsync("Bug", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord
            {
                TypeName = "Bug",
                States =
                [
                    new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
                    new StateEntry("Active", StateCategory.InProgress, "007acc"),
                    new StateEntry("Closed", StateCategory.Completed, "339933"),
                ],
                ValidChildTypes = ["Task"],
                ColorHex = "CC293D",
            });
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new FieldDefinition("System.Title", "Title", "String", false),
                new FieldDefinition("System.State", "State", "String", true),
            });

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Bug");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("typeName").GetString().ShouldBe("Bug");
        root.GetProperty("color").GetString().ShouldBe("CC293D");
        root.GetProperty("stateCount").GetInt32().ShouldBe(3);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(2);

        var states = root.GetProperty("states");
        states.GetArrayLength().ShouldBe(3);
        states[0].GetProperty("name").GetString().ShouldBe("New");
        states[0].GetProperty("category").GetString().ShouldBe("Proposed");
        states[0].GetProperty("color").GetString().ShouldBe("b2b2b2");

        var fields = root.GetProperty("fields");
        fields.GetArrayLength().ShouldBe(2);
        fields[0].GetProperty("referenceName").GetString().ShouldBe("System.Title");
        fields[0].GetProperty("displayName").GetString().ShouldBe("Title");
        fields[0].GetProperty("isReadOnly").GetBoolean().ShouldBeFalse();
        fields[1].GetProperty("isReadOnly").GetBoolean().ShouldBeTrue();

        var transitions = root.GetProperty("transitions");
        transitions.GetArrayLength().ShouldBe(6); // 3 states -> 3*2 = 6

        root.GetProperty("validChildTypes").GetArrayLength().ShouldBe(1);
        root.GetProperty("validChildTypes")[0].GetString().ShouldBe("Task");
    }

    [Fact]
    public async Task Process_WithType_NoFields_ReturnsEmptyFieldsArray()
    {
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
            });
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Task");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("fields").GetArrayLength().ShouldBe(0);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Process_WithType_TransitionsMarkRemovedAsCut()
    {
        _processTypeStore.GetByNameAsync("Bug", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord
            {
                TypeName = "Bug",
                States =
                [
                    new StateEntry("New", StateCategory.Proposed, null),
                    new StateEntry("Active", StateCategory.InProgress, null),
                    new StateEntry("Removed", StateCategory.Removed, null),
                ],
                ValidChildTypes = [],
            });
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Bug");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var transitions = root.GetProperty("transitions");

        // Find New -> Removed transition (should be "Cut")
        var newToRemoved = FindTransition(transitions, "New", "Removed");
        newToRemoved.ShouldNotBeNull();
        newToRemoved.Value.GetProperty("kind").GetString().ShouldBe("Cut");

        // Find New -> Active transition (should be "Forward")
        var newToActive = FindTransition(transitions, "New", "Active");
        newToActive.ShouldNotBeNull();
        newToActive.Value.GetProperty("kind").GetString().ShouldBe("Forward");
    }

    [Fact]
    public async Task Process_WithType_SingleState_NoTransitions()
    {
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
            });
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Task");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("transitions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Process_WithType_NullColor_WritesNull()
    {
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
                ColorHex = null,
            });
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var sut = CreateProcessSut();
        var result = await sut.Process(type: "Task");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("color").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Process_InvalidWorkspace_ReturnsError()
    {
        var sut = CreateProcessSut();
        var result = await sut.Process(workspace: "invalid/workspace");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private ProcessTools CreateProcessSut()
    {
        var res = BuildResolver(DefaultConfig);
        return new ProcessTools(res);
    }

    private static JsonElement? FindTransition(JsonElement transitions, string from, string to)
    {
        for (var i = 0; i < transitions.GetArrayLength(); i++)
        {
            var t = transitions[i];
            if (t.GetProperty("from").GetString() == from && t.GetProperty("to").GetString() == to)
                return t;
        }
        return null;
    }
}
