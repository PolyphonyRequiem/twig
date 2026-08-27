using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ProcessCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly StringWriter _stderr;
    private readonly ProcessCommand _cmd;

    public ProcessCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _stderr = new StringWriter();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _rendererFactory = new RendererFactory();

        _cmd = new ProcessCommand(
            _activeItemResolver,
            _processTypeStore,
            _fieldDefinitionStore,
            _formatterFactory,
            _rendererFactory,
            stderr: _stderr);
    }

    public void Dispose()
    {
        _stderr.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    //  process (no args) — list all types
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_NoArgs_NoTypes_ReturnsExitCode1()
    {
        _processTypeStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProcessTypeRecord>());

        var result = await _cmd.ExecuteAsync(typeName: null, outputFormat: "json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No process types found");
        _stderr.ToString().ShouldContain("twig sync");
    }

    [Fact]
    public async Task Execute_NoArgs_JsonOutput_ContainsTypesArray()
    {
        SetupProcessTypes([
            new ProcessTypeRecord
            {
                TypeName = "Bug",
                States = [new StateEntry("New", StateCategory.Proposed, "b2b2b2"), new StateEntry("Closed", StateCategory.Completed, "339933")],
                ValidChildTypes = ["Task"],
                ColorHex = "CC293D",
                IconId = "icon_insect",
            },
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null), new StateEntry("Done", StateCategory.Completed, null)],
                ValidChildTypes = [],
                ColorHex = null,
                IconId = null,
            }
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        output.ShouldContain("\"types\":");
        output.ShouldContain("\"totalTypes\": 2");
        output.ShouldContain("\"typeName\": \"Bug\"");
        output.ShouldContain("\"typeName\": \"Task\"");
        output.ShouldContain("\"stateCount\": 2");
        output.ShouldContain("\"iconId\": \"icon_insect\"");
        output.ShouldContain("\"iconId\": null");
    }

    [Fact]
    public async Task Execute_NoArgs_HumanOutput_ContainsTypeNames()
    {
        SetupProcessTypes([
            new ProcessTypeRecord
            {
                TypeName = "Bug",
                States = [new StateEntry("New", StateCategory.Proposed, "b2b2b2")],
                ValidChildTypes = [],
                ColorHex = "CC293D",
            },
            new ProcessTypeRecord
            {
                TypeName = "Epic",
                States = [new StateEntry("New", StateCategory.Proposed, null), new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = ["Feature"],
                ColorHex = null,
            }
        ]);

        var (exitCode, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: null, outputFormat: "human"));

        exitCode.ShouldBe(0);
        output.ShouldContain("Bug");
        output.ShouldContain("Epic");
        output.ShouldContain("1 states");
        output.ShouldContain("2 states");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task Execute_NoArgs_JsonFormats_ContainTypesArray(string format)
    {
        SetupProcessTypes([
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
            }
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: null, outputFormat: format));

        output.ShouldContain("\"types\":");
        output.ShouldContain("\"totalTypes\": 1");
    }

    [Fact]
    public async Task Execute_NoArgs_JsonOutput_NullColor_WritesNull()
    {
        SetupProcessTypes([
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("New", StateCategory.Proposed, null)],
                ValidChildTypes = [],
                ColorHex = null,
            }
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        output.ShouldContain("\"color\": null");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AB#656 — category membership on the machine surface
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// AB#656. The machine surface must carry enough to tell a user-creatable type from one
    /// ADO keeps for its own tooling. Both halves are asserted deliberately: an absence-only
    /// or hidden-only assertion is the false-green class AGENTS.md names, because an empty
    /// or all-hidden listing would satisfy it.
    /// </summary>
    /// <summary>
    /// AB#657: hidden types must not appear in the DEFAULT listing at all. Marking them
    /// (AB#656) stopped them reading as usable vocabulary; it did not stop them padding the
    /// list — 9 of this board's 21 types are tooling machinery, so nearly half the default
    /// output was noise a caller must never act on.
    /// </summary>
    /// <remarks>
    /// Asserted BOTH ways deliberately. A test that only checks the hidden type is absent
    /// passes against a listing that returns nothing at all, which is this repo's named
    /// false-green shape: a check that cannot fail is not a check.
    /// </remarks>
    [Fact]
    public async Task Execute_NoArgs_Json_OmitsHiddenTypesByDefault()
    {
        SetupProcessTypes([HiddenType("Code Review Request"), UsableType("Bug")]);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        exitCode.ShouldBe(0);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var names = doc.RootElement.GetProperty("types").EnumerateArray()
            .Select(t => t.GetProperty("typeName").GetString()).ToList();

        names.ShouldNotContain("Code Review Request");
        names.ShouldContain("Bug");
    }

    [Fact]
    public async Task Execute_NoArgs_Json_IncludeHidden_RestoresThem()
    {
        SetupProcessTypes([HiddenType("Code Review Request"), UsableType("Bug")]);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(typeName: null, outputFormat: "json", includeHidden: true));

        exitCode.ShouldBe(0);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var types = doc.RootElement.GetProperty("types").EnumerateArray().ToList();

        types.Count.ShouldBe(2);
        // The opt-in restores them AND keeps the marker — an unmarked hidden type is the
        // AB#656 defect, so --include-hidden must not regress it.
        types.Single(t => t.GetProperty("typeName").GetString() == "Code Review Request")
            .GetProperty("isHidden").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Execute_NoArgs_Human_OmitsHiddenTypesByDefault()
    {
        // The human surface must agree with the machine one; two surfaces disagreeing about
        // whether a type is usable is worse than neither reporting it, because it looks like
        // an answer.
        SetupProcessTypes([HiddenType("Code Review Request"), UsableType("Bug")]);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(typeName: null, outputFormat: "human"));

        exitCode.ShouldBe(0);
        output.ShouldNotContain("Code Review Request");
        output.ShouldContain("Bug");
    }

    /// <summary>
    /// A hidden type is omitted from the LIST, never made unreachable. Suppressing the
    /// listing is a default-output decision; refusing to describe a type the caller named
    /// outright would be a different and worse defect.
    /// </summary>
    [Fact]
    public async Task Execute_HiddenTypeByName_StillReturnsItsDetail()
    {
        var hidden = HiddenType("Code Review Request");
        SetupProcessTypes([hidden, UsableType("Bug")]);
        _processTypeStore.GetByNameAsync("Code Review Request", Arg.Any<CancellationToken>())
            .Returns(hidden);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(typeName: "Code Review Request", outputFormat: "json"));

        exitCode.ShouldBe(0);
        output.ShouldContain("Code Review Request");
    }

    private static ProcessTypeRecord HiddenType(string name) => new()
    {
        TypeName = name,
        States = [new StateEntry("To Do", StateCategory.Proposed, null)],
        ValidChildTypes = [],
        CategoryReferenceNames = ["Microsoft.HiddenCategory"],
    };

    private static ProcessTypeRecord UsableType(string name) => new()
    {
        TypeName = name,
        States = [new StateEntry("To Do", StateCategory.Proposed, null)],
        ValidChildTypes = [],
        CategoryReferenceNames = ["Microsoft.RequirementCategory"],
    };

    /// <summary>
    /// </summary>
    [Fact]
    public async Task Execute_NoArgs_JsonOutput_DistinguishesHiddenTypesFromUsableOnes()
    {
        SetupProcessTypes([
            // Measured on the live Hyperbright process: 'Issue' is in HiddenCategory AND
            // BugCategory AND RequirementCategory at once — membership is many-to-many and
            // is not derivable from the type name.
            new ProcessTypeRecord
            {
                TypeName = "Issue",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
                CategoryReferenceNames = [
                    "Microsoft.HiddenCategory",
                    "Microsoft.BugCategory",
                    "Microsoft.RequirementCategory",
                ],
            },
            // ...while 'Bug' is in RequirementCategory and NOT BugCategory, and is usable.
            new ProcessTypeRecord
            {
                TypeName = "Bug",
                States = [new StateEntry("To Do", StateCategory.Proposed, null)],
                ValidChildTypes = [],
                CategoryReferenceNames = ["Microsoft.RequirementCategory"],
            },
        ]);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(typeName: null, outputFormat: "json", includeHidden: true));

        exitCode.ShouldBe(0);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var types = doc.RootElement.GetProperty("types").EnumerateArray().ToList();

        // Ordinary process types must remain represented — not silently dropped.
        // AB#657: --include-hidden is required here. The DEFAULT listing now omits hidden
        // types entirely; this test's subject is the per-type isHidden/categories SHAPE, so
        // it opts in rather than asserting the default, which its sibling tests cover.
        types.Count.ShouldBe(2);

        var issue = types.Single(t => t.GetProperty("typeName").GetString() == "Issue");
        var bug = types.Single(t => t.GetProperty("typeName").GetString() == "Bug");

        // The hidden fact is explicit, per type, on the machine surface.
        issue.GetProperty("isHidden").GetBoolean().ShouldBeTrue();
        bug.GetProperty("isHidden").GetBoolean().ShouldBeFalse();

        // ...and the full membership is carried, not collapsed to one category.
        var issueCategories = issue.GetProperty("categories")
            .EnumerateArray().Select(c => c.GetString()).ToList();
        issueCategories.ShouldContain("Microsoft.HiddenCategory");
        issueCategories.ShouldContain("Microsoft.BugCategory");
        issueCategories.ShouldContain("Microsoft.RequirementCategory");

        var bugCategories = bug.GetProperty("categories")
            .EnumerateArray().Select(c => c.GetString()).ToList();
        bugCategories.ShouldBe(["Microsoft.RequirementCategory"]);
    }

    // ═══════════════════════════════════════════════════════════════
    //  process <type> — type detail mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_WithType_TypeNotFound_ReturnsExitCode1()
    {
        _processTypeStore.GetByNameAsync("Unknown", Arg.Any<CancellationToken>())
            .Returns((ProcessTypeRecord?)null);

        var result = await _cmd.ExecuteAsync(typeName: "Unknown", outputFormat: "json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No states found");
        _stderr.ToString().ShouldContain("twig sync");
    }

    [Fact]
    public async Task Execute_WithType_EmptyStates_ReturnsExitCode1()
    {
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord { TypeName = "Task", States = [] });

        var result = await _cmd.ExecuteAsync(typeName: "Task", outputFormat: "json");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Execute_WithType_JsonOutput_ContainsStatesFieldsTransitions()
    {
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
            new StateEntry("Closed", StateCategory.Completed, "339933"),
        ]);
        SetupFields([
            new FieldDefinition("System.Title", "Title", "String", false),
            new FieldDefinition("System.State", "State", "String", true),
        ]);

        var (exitCode, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: "Task", outputFormat: "json"));

        exitCode.ShouldBe(0);
        output.ShouldContain("\"type\": \"Task\"");
        output.ShouldContain("\"name\": \"New\"");
        output.ShouldContain("\"name\": \"Active\"");
        output.ShouldContain("\"name\": \"Closed\"");
        output.ShouldContain("\"category\": \"Proposed\"");
        output.ShouldContain("\"category\": \"InProgress\"");
        output.ShouldContain("\"color\": \"007acc\"");

        // Fields
        output.ShouldContain("\"fields\":");
        output.ShouldContain("\"referenceName\": \"System.Title\"");
        output.ShouldContain("\"displayName\": \"Title\"");

        // Transitions
        output.ShouldContain("\"transitions\":");
        output.ShouldContain("\"from\": \"New\"");
        output.ShouldContain("\"to\": \"Active\"");
    }

    [Fact]
    public async Task Execute_WithType_JsonOutput_NullColor_WritesNull()
    {
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, null),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: "Task", outputFormat: "json"));

        output.ShouldContain("\"color\": null");
    }

    [Fact]
    public async Task Execute_WithType_HumanOutput_ContainsStateNames()
    {
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
        ]);
        SetupFields([]);

        var (exitCode, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: "Task", outputFormat: "human"));

        exitCode.ShouldBe(0);
        output.ShouldContain("New");
        output.ShouldContain("Active");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task Execute_WithType_JsonFormats_ContainStatesArray(string format)
    {
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: "Task", outputFormat: format));

        output.ShouldContain("\"states\":");
    }

    [Fact]
    public async Task Execute_WithType_TransitionsIncludeCutForRemoved()
    {
        SetupProcessType("Bug", [
            new StateEntry("New", StateCategory.Proposed, null),
            new StateEntry("Active", StateCategory.InProgress, null),
            new StateEntry("Removed", StateCategory.Removed, null),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: "Bug", outputFormat: "json"));

        output.ShouldContain("\"kind\": \"Cut\"");
        output.ShouldContain("\"kind\": \"Forward\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  states alias — backward compat
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteStates_NoActiveItem_ReturnsExitCode1AndWritesError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteStatesAsync("json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    [Fact]
    public async Task ExecuteStates_TypeNotInStore_ReturnsExitCode1AndWritesError()
    {
        SetupActiveItem(42, "My Task", "Task");
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>()).Returns((ProcessTypeRecord?)null);

        var result = await _cmd.ExecuteStatesAsync("json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No states found");
        _stderr.ToString().ShouldContain("twig sync");
    }

    [Fact]
    public async Task ExecuteStates_EmptyStates_ReturnsExitCode1()
    {
        SetupActiveItem(42, "My Task", "Task");
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord { TypeName = "Task", States = [] });

        var result = await _cmd.ExecuteStatesAsync("json");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteStates_JsonOutput_ContainsExpectedSchema()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
            new StateEntry("Closed", StateCategory.Completed, "339933"),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteStatesAsync("json"));

        output.ShouldContain("\"type\": \"Task\"");
        output.ShouldContain("\"name\": \"New\"");
        output.ShouldContain("\"name\": \"Active\"");
        output.ShouldContain("\"name\": \"Closed\"");
        output.ShouldContain("\"category\": \"Proposed\"");
        output.ShouldContain("\"category\": \"InProgress\"");
        output.ShouldContain("\"color\": \"007acc\"");
    }

    [Fact]
    public async Task ExecuteStates_JsonOutput_NullColor_WritesNullValue()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, null),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteStatesAsync("json"));

        output.ShouldContain("\"color\": null");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task ExecuteStates_JsonOutput_ContainsStatesArray(string format)
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteStatesAsync(format));

        output.ShouldContain("\"states\":");
    }

    [Fact]
    public async Task ExecuteStates_HumanOutput_ContainsStateNames()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
        ]);
        SetupFields([]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteStatesAsync("human"));

        output.ShouldContain("New");
        output.ShouldContain("Active");
    }

    [Fact]
    public async Task ExecuteStates_DoesNotCallAdoService()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
        ]);
        SetupFields([]);

        await _cmd.ExecuteStatesAsync("json");

        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStates_ActiveIdSetButNotInCache_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("offline"));

        var result = await _cmd.ExecuteStatesAsync("json");

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void SetupActiveItem(int id, string title, string type)
    {
        var item = new WorkItemBuilder(id, title).AsType(WorkItemType.Parse(type).Value).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private void SetupProcessType(string typeName, IReadOnlyList<StateEntry> states)
    {
        var record = new ProcessTypeRecord { TypeName = typeName, States = states };
        _processTypeStore.GetByNameAsync(typeName, Arg.Any<CancellationToken>()).Returns(record);
    }

    private void SetupProcessTypes(IReadOnlyList<ProcessTypeRecord> types)
    {
        _processTypeStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(types);
    }

    private void SetupFields(IReadOnlyList<FieldDefinition> fields)
    {
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(fields);
    }
}
