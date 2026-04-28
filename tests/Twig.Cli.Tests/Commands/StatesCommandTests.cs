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
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class StatesCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly StringWriter _stderr;
    private readonly StatesCommand _cmd;

    public StatesCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _stderr = new StringWriter();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _cmd = new StatesCommand(
            _activeItemResolver,
            _processTypeStore,
            _formatterFactory,
            stderr: _stderr);
    }

    public void Dispose()
    {
        _stderr.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_NoActiveItem_ReturnsExitCode1AndWritesError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active item but type not in process store
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_TypeNotInStore_ReturnsExitCode1AndWritesError()
    {
        SetupActiveItem(42, "My Task", "Task");
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>()).Returns((ProcessTypeRecord?)null);

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No states found");
        _stderr.ToString().ShouldContain("twig sync");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type with empty states
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_EmptyStates_ReturnsExitCode1()
    {
        SetupActiveItem(42, "My Task", "Task");
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns(new ProcessTypeRecord { TypeName = "Task", States = [] });

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON output — happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_JsonOutput_ContainsExpectedSchema()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
            new StateEntry("Closed", StateCategory.Completed, "339933"),
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync("json"));

        output.ShouldContain("\"type\": \"Task\"");
        output.ShouldContain("\"name\": \"New\"");
        output.ShouldContain("\"name\": \"Active\"");
        output.ShouldContain("\"name\": \"Closed\"");
        output.ShouldContain("\"category\": \"Proposed\"");
        output.ShouldContain("\"category\": \"InProgress\"");
        output.ShouldContain("\"color\": \"007acc\"");
    }

    [Fact]
    public async Task Execute_JsonOutput_NullColor_WritesNullValue()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, null),
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync("json"));

        output.ShouldContain("\"color\": null");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    public async Task Execute_JsonOutput_ContainsStatesArray(string format)
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync(format));

        output.ShouldContain("\"states\":");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Human output — happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_HumanOutput_ContainsStateNames()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
            new StateEntry("Active", StateCategory.InProgress, "007acc"),
        ]);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteAsync("human"));

        output.ShouldContain("New");
        output.ShouldContain("Active");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No network calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_DoesNotCallAdoService()
    {
        SetupActiveItem(42, "My Task", "Task");
        SetupProcessType("Task", [
            new StateEntry("New", StateCategory.Proposed, "b2b2b2"),
        ]);

        await _cmd.ExecuteAsync("json");

        // After the active item is resolved from cache, no ADO calls should occur
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active item not in cache (resolved via ADO auto-fetch)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_ActiveIdSetButNotInCache_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("offline"));

        var result = await _cmd.ExecuteAsync("json");

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

}
