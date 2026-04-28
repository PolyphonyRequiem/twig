using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for conflict UX (FM-006, FM-007): display format, l/r/a flow,
/// and JSON output for conflict scenarios.
/// </summary>
public class ConflictUxTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly CommandContext _ctx;
    private readonly Domain.Services.Navigation.ActiveItemResolver _resolver;

    public ConflictUxTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _ctx = new CommandContext(
            new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            hintEngine,
            new TwigConfiguration());
        _resolver = new Domain.Services.Navigation.ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    [Fact]
    public async Task State_Conflict_KeepLocal_Proceeds()
    {
        var local = CreateWorkItem(1, "Test", "New");
        var remote = CreateWorkItem(1, "Test", "Active");
        remote.MarkSynced(5); // Different revision

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        _consoleInput.ReadLine().Returns("l"); // keep local

        var cmd = new StateCommand(
            _ctx, _resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _consoleInput);

        var result = await cmd.ExecuteAsync("c"); // c = Active

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_Conflict_KeepRemote_UpdatesCache()
    {
        var local = CreateWorkItem(1, "Test", "New");
        var remote = CreateWorkItem(1, "Test", "Active");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        _consoleInput.ReadLine().Returns("r"); // keep remote

        var cmd = new StateCommand(
            _ctx, _resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _consoleInput);

        var result = await cmd.ExecuteAsync("c"); // c = Active

        result.ShouldBe(0);
        // I-005/I-006: 'r' discards the local action — PatchAsync should NOT be called
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_Conflict_Abort_NoChange()
    {
        var local = CreateWorkItem(1, "Test", "New");
        var remote = CreateWorkItem(1, "Test", "Active");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        _consoleInput.ReadLine().Returns("a"); // abort

        var cmd = new StateCommand(
            _ctx, _resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _consoleInput);

        var result = await cmd.ExecuteAsync("c");

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_Conflict_KeepLocal_Proceeds()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        _consoleInput.ReadLine().Returns("l"); // keep local

        var cmd = new UpdateCommand(_resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, _formatterFactory);
        var result = await cmd.ExecuteAsync("System.Title", "New Title");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_Conflict_KeepRemote_UpdatesCache()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        _consoleInput.ReadLine().Returns("r"); // keep remote

        var cmd = new UpdateCommand(_resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, _formatterFactory);
        var result = await cmd.ExecuteAsync("System.Title", "New Title");

        result.ShouldBe(0);
        // 'r' discards the local action — PatchAsync should NOT be called
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_Conflict_KeepRemote_DiscardsLocal()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        _consoleInput.ReadLine().Returns("r"); // keep remote

        var cmd = new SaveCommand(_workItemRepo, _pendingChangeStore,
            new PendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory),
            _resolver, _formatterFactory);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_Conflict_JsonOutput_ReturnsConflictsAndExitOne()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        var cmd = new SaveCommand(_workItemRepo, _pendingChangeStore,
            new PendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory),
            _resolver, _formatterFactory);
        var result = await cmd.ExecuteAsync(all: true, outputFormat: "json");

        result.ShouldBe(1);
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.UserStory,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
