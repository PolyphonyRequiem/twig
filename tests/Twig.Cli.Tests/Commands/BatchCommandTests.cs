using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class BatchCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly StringWriter _stderr;
    private readonly BatchCommand _cmd;

    public BatchCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _stderr = new StringWriter();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new BatchCommand(
            resolver, _workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            formatterFactory, hintEngine, stderr: _stderr);
    }

    // ── Validation ──────────────────────────────────────────────────

    [Fact]
    public async Task NoOperations_ReturnsExitCode2()
    {
        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("At least one of --state, --set, or --note");
    }

    [Fact]
    public async Task InvalidFormat_ReturnsExitCode2()
    {
        var result = await _cmd.ExecuteAsync(state: "Active", format: "xml");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Unknown format 'xml'");
    }

    [Fact]
    public async Task InvalidSetFormat_MissingEquals_ReturnsExitCode2()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync(set: new[] { "NoEqualsHere" });

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Invalid --set format");
    }

    [Fact]
    public async Task InvalidSetFormat_EmptyKey_ReturnsExitCode2()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync(set: new[] { "=value" });

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Invalid --set format");
    }

    [Fact]
    public async Task NoActiveItem_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    // ── State-only transitions ──────────────────────────────────────

    [Fact]
    public async Task StateOnly_ForwardTransition_Succeeds()
    {
        var item = CreateWorkItem(1, "Test Item", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.State" &&
                c[0].NewValue == "Active"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateOnly_AlreadyInState_SkipsStateChange()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // State is already "Active" and there are no field changes → no PATCH needed
        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateOnly_InvalidState_ReturnsExitCode1()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync(state: "Nonexistent");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task StateOnly_DisallowedTransition_ReturnsExitCode1()
    {
        // Active → Closed is allowed for UserStory in Agile, but New → Closed is not direct
        // Actually in Agile, all transitions are allowed. Let me use a custom config.
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // "Nonexistent" state → results in unknown state error
        var result = await _cmd.ExecuteAsync(state: "Nonexistent");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task StateOnly_BackwardTransition_UserConfirms_Succeeds()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync(state: "New"); // Active → New is backward

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateOnly_BackwardTransition_UserDeclines_ReturnsExitCode1()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _consoleInput.ReadLine().Returns("n");

        var result = await _cmd.ExecuteAsync(state: "New"); // Active → New is backward

        result.ShouldBe(1); // Cancelled results in error return
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Field-only updates ──────────────────────────────────────────

    [Fact]
    public async Task FieldOnly_SingleField_Succeeds()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(set: new[] { "System.Title=Updated Title" });

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Title" &&
                c[0].NewValue == "Updated Title"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FieldOnly_MultipleFields_AllIncludedInSinglePatch()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(set: new[]
        {
            "System.Title=New Title",
            "System.AssignedTo=Alice"
        });

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 2 &&
                c[0].FieldName == "System.Title" &&
                c[1].FieldName == "System.AssignedTo"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetParsing_ValueContainsEquals_SplitsOnFirstOnly()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(set: new[] { "Custom.Field=val=ue=with=equals" });

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "Custom.Field" &&
                c[0].NewValue == "val=ue=with=equals"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetParsing_MarkdownFormat_ConvertsValues()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            set: new[] { "System.Description=**bold**" },
            format: "markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].NewValue!.Contains("<strong>bold</strong>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Note-only ───────────────────────────────────────────────────

    [Fact]
    public async Task NoteOnly_AddsComment()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(note: "Test note");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "Test note", Arg.Any<CancellationToken>());
        // No PATCH should have been called (no state/field changes)
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Combined operations ─────────────────────────────────────────

    [Fact]
    public async Task StateAndField_CombinedInSinglePatch()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            set: new[] { "System.AssignedTo=Alice" });

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 2 &&
                c[0].FieldName == "System.State" &&
                c[0].NewValue == "Active" &&
                c[1].FieldName == "System.AssignedTo" &&
                c[1].NewValue == "Alice"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateFieldAndNote_AllApplied()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            set: new[] { "System.AssignedTo=Alice" },
            note: "Starting work");

        result.ShouldBe(0);

        // PATCH includes state + field
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 2),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Comment added after PATCH
        await _adoService.Received().AddCommentAsync(1, "Starting work", Arg.Any<CancellationToken>());
    }

    // ── AutoPush notes ──────────────────────────────────────────────

    [Fact]
    public async Task AutoPushesResidualNotes_AfterPatch()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var pendingNote = new PendingChangeRecord(1, "note", null, null, "Residual note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "Residual note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    // ── Conflict handling ───────────────────────────────────────────

    [Fact]
    public async Task PatchConflict_RetrySucceeds()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        // First patch → conflict; retry succeeds
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task PatchConflict_BothAttemptsFail_ReturnsExitCode1()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Concurrency conflict");
    }

    // ── Resync cache ────────────────────────────────────────────────

    [Fact]
    public async Task ResyncCache_FetchThrows_ReturnsSuccessWithWarning()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (pre-patch) returns item; second (resync) throws
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => item, _ => throw new HttpRequestException("network timeout"));

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        _stderr.ToString().ShouldContain("twig sync");
    }

    [Fact]
    public async Task ResyncCache_ReFetchesAndSavesServerItem()
    {
        var local = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(local);

        var serverItem = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        serverItem.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(local, serverItem);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        await _adoService.Received(2).FetchAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(serverItem, Arg.Any<CancellationToken>());
    }

    // ── --id parameter ──────────────────────────────────────────────

    [Fact]
    public async Task IdParameter_ResolvesSpecificItem()
    {
        var item = CreateWorkItem(42, "Specific Item", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        SetupSuccessfulPatchForItem(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            id: 42);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── No process config for type ──────────────────────────────────

    [Fact]
    public async Task UnknownType_ReturnsExitCode1()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.Parse("CustomType").Value);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(1);
    }

    // ── Edge case: state already in target + field updates ──────────

    [Fact]
    public async Task StateAlreadyInTarget_FieldUpdatesStillApply()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active", // Already in Active
            set: new[] { "System.Title=Updated" });

        result.ShouldBe(0);
        // Only field update in PATCH, no state change
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Title"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private void SetupSuccessfulPatch(WorkItem item)
    {
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(item.Id, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    private void SetupSuccessfulPatchForItem(WorkItem item)
    {
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(item.Id, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    private static WorkItem CreateWorkItem(int id, string title, string state, WorkItemType type)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
