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

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter(), new IdsOutputFormatter());
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
            },
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("To Do", StateCategory.Proposed, null), new StateEntry("Done", StateCategory.Completed, null)],
                ValidChildTypes = [],
                ColorHex = null,
            }
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        output.ShouldContain("\"types\":");
        output.ShouldContain("\"totalTypes\": 2");
        output.ShouldContain("\"typeName\": \"Bug\"");
        output.ShouldContain("\"typeName\": \"Task\"");
        output.ShouldContain("\"stateCount\": 2");
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
