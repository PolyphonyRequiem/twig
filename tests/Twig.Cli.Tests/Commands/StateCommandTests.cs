using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services.Mutation;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StateCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly SeedMutationProvider _seedMutationProvider;
    private readonly StringWriter _stderr;
    private readonly StateCommand _cmd;

    public StateCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _seedMutationProvider = new SeedMutationProvider(_workItemRepo);
        _stderr = new StringWriter();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var ctx = new CommandContext(
            new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true),
            formatterFactory,
            hintEngine,
            new TwigConfiguration(),
            Stderr: _stderr);

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new StateCommand(
            ctx, resolver, _workItemRepo, _adoService,
            _consoleInput, _seedMutationProvider,
            new StateTransitionWorkflow(_workItemRepo, _adoService, _pendingChangeStore, _processConfigProvider));
    }

    [Fact]
    public async Task State_ForwardTransition_AutoApplies()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active"); // Active (forward from New)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_AlreadyInState_NoOp()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Active"); // Active, already Active

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_InvalidState_ReturnsError()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Nonexistent"); // no match

        result.ShouldBe(1);
    }

    [Fact]
    public async Task State_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task State_AutoPushesNotes_OnStateChange()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var pendingNote = new PendingChangeRecord(1, "note", null, null, "Test note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "Test note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_PreservesFieldChanges_OnStateChange()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var pendingField = new PendingChangeRecord(1, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingField });

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // Field changes should NOT be cleared
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_BackwardTransition_NoConfirmationNeeded_AppliesChange()
    {
        // Active → New is now a Forward transition (no confirmation needed)
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("New"); // New (backward from Active — now Forward)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine(); // No confirmation prompt
    }

    [Fact]
    public async Task State_BackwardTransition_NoPrompt_NeverCancels()
    {
        // Active → New is now a Forward transition — no prompt, always proceeds
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("New");

        result.ShouldBe(0);
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task State_CutTransition_AppliesWithoutPrompt()
    {
        // New → Removed is a Cut transition for Agile UserStory — no prompt required
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Removed"); // Removed (cut from New)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task State_ForwardTransition_ReFetchesAndSavesServerItem()
    {
        var local = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(local);

        // Distinct "server" item representing post-transition state
        var serverItem = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        serverItem.MarkSynced(5);

        // First FetchAsync (conflict check) returns local; second (resync) returns serverItem
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(local, serverItem);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // FetchAsync called twice: conflict check + resync
        await _adoService.Received(2).FetchAsync(1, Arg.Any<CancellationToken>());
        // SaveAsync receives the re-fetched server item, not the local one
        await _workItemRepo.Received(1).SaveAsync(serverItem, Arg.Any<CancellationToken>());
        _stderr.ToString().ShouldNotContain("resync failed");
    }

    [Fact]
    public async Task State_PatchConflict_RetrySucceeds_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        // First patch attempt → conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        // Re-fetch returns fresh item at revision 5
        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        // FetchAsync: first call returns remote (pre-patch conflict check),
        // second returns freshItem (retry re-fetch from ConflictRetryHelper),
        // third returns freshItem again (resync after successful patch)
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        // Retry with fresh revision succeeds
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // PatchAsync called twice: once with old revision (conflict), once with fresh revision (success)
        await _adoService.Received(2).PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_PatchConflict_BothAttemptsFail_ThrowsAdoConflictException()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        // Re-fetch returns fresh item at revision 5
        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        // First patch attempt → conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        // Retry also conflicts
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        await Should.ThrowAsync<AdoConflictException>(
            () => _cmd.ExecuteAsync("Active"));
    }

    [Fact]
    public async Task State_Resync_NotAttempted_WhenTransitionFails()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (conflict check) succeeds
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // PatchAsync throws a non-conflict failure
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unavailable"));

        await Should.ThrowAsync<HttpRequestException>(() => _cmd.ExecuteAsync("Active"));

        // FetchAsync called only once (conflict check), never for resync
        await _adoService.Received(1).FetchAsync(1, Arg.Any<CancellationToken>());
        // SaveAsync never called — transition failed before resync
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_Resync_FetchThrows_ReturnsSuccessWithWarning()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (conflict check) returns item; second (resync) throws
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => item, _ => throw new HttpRequestException("network timeout"));

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        // Command still succeeds — the ADO state transition completed
        result.ShouldBe(0);
        // Warning emitted to stderr about resync failure with recovery hint
        var stderrOutput = _stderr.ToString();
        stderrOutput.ShouldContain("twig sync");
        stderrOutput.ShouldContain("network timeout");
        // SaveAsync never called — FetchAsync for resync threw before save
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_Resync_OperationCanceled_IsNotSwallowed()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (conflict check) returns item; second (resync) throws OperationCanceledException
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => item, _ => throw new OperationCanceledException());

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // OperationCanceledException should propagate — not be caught by the resync catch block
        await Should.ThrowAsync<OperationCanceledException>(() => _cmd.ExecuteAsync("Active"));
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private StateCommand CreateCommandWithPropagation()
    {
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var propagationService = new ParentStatePropagationService(
            _workItemRepo, _adoService, _processConfigProvider, protectedCacheWriter);

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var ctx = new CommandContext(
            new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true),
            formatterFactory,
            hintEngine,
            new TwigConfiguration(),
            Stderr: _stderr);
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        return new StateCommand(
            ctx, resolver, _workItemRepo, _adoService,
            _consoleInput, _seedMutationProvider,
            new StateTransitionWorkflow(_workItemRepo, _adoService, _pendingChangeStore, _processConfigProvider, parentPropagation: propagationService));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent propagation — transition to InProgress
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_ToInProgress_PropagatesParentFromProposedToInProgress()
    {
        // Child UserStory with parent Epic
        var child = new WorkItemBuilder(1, "My Story").AsUserStory().InState("New").WithParent(100).Build();
        SetupActiveItem(child);

        // Parent Epic in Proposed (New) state
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().InState("New").Build();
        parent.MarkSynced(5);

        // Child: conflict check + resync
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(child);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // Parent: cache hit, ADO fetch for revision, patch
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var cmd = CreateCommandWithPropagation();
        var result = await cmd.ExecuteAsync("Active"); // Active is InProgress for Agile UserStory

        result.ShouldBe(0);
        // Parent Epic should have been patched to Active
        await _adoService.Received().PatchAsync(
            100,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.State" &&
                c[0].OldValue == "New" &&
                c[0].NewValue == "Active"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_ToInProgress_ParentAlreadyActive_NoPropagationPatch()
    {
        // Child with parent already in Active (InProgress)
        var child = new WorkItemBuilder(1, "My Story").AsUserStory().InState("New").WithParent(100).Build();
        SetupActiveItem(child);

        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().InState("Active").Build();

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(child);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var cmd = CreateCommandWithPropagation();
        var result = await cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // PatchAsync should NOT be called for parent (it's already active)
        await _adoService.DidNotReceive().PatchAsync(
            100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_ToNonInProgress_NoPropagation()
    {
        // Child transitioning to Resolved (not InProgress) — propagation should NOT trigger
        var child = new WorkItemBuilder(1, "My Story").AsUserStory().InState("Active").WithParent(100).Build();
        SetupActiveItem(child);

        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().InState("New").Build();

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(child);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var cmd = CreateCommandWithPropagation();
        var result = await cmd.ExecuteAsync("Resolved"); // Resolved is not InProgress

        result.ShouldBe(0);
        // No patch call for parent
        await _adoService.DidNotReceive().PatchAsync(
            100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task State_WithExplicitId_TransitionsCorrectItem()
    {
        var item = CreateWorkItem(42, "Specific Item", "New", WorkItemType.UserStory);
        // Explicit ID does NOT require active context to be set
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active", id: 42);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Active context in context store should NOT be changed
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_WithExplicitId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        // ADO also doesn't have it
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var result = await _cmd.ExecuteAsync("Active", id: 99);

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed routing — local-only mutation via SeedMutationProvider
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_OnSeed_WritesLocally_NoAdoCall()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("New").Build();
        SetupActiveItem(seed);

        var result = await _cmd.ExecuteAsync("Doing");

        result.ShouldBe(0);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_OnSeed_AcceptsAnyStateName()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("New").Build();
        SetupActiveItem(seed);

        // "CustomState" is not in any process config — seeds accept anything
        var result = await _cmd.ExecuteAsync("CustomState");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.State == "CustomState"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_OnSeed_SkipsTransitionValidation()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("New").Build();
        SetupActiveItem(seed);

        // Process config is never consulted for seeds
        var result = await _cmd.ExecuteAsync("Done");

        result.ShouldBe(0);
        _processConfigProvider.DidNotReceive().GetConfiguration();
    }

    [Fact]
    public async Task State_OnSeed_AlreadyInState_NoOp()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("Doing").Build();
        SetupActiveItem(seed);

        var result = await _cmd.ExecuteAsync("Doing");

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_OnSeed_ProviderError_ReturnsExitCode1()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("New").Build();
        SetupActiveItem(seed);
        // First call returns seed (for ActiveItemResolver), second returns null (for SeedMutationProvider)
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(seed, (WorkItem?)null);

        var result = await _cmd.ExecuteAsync("Doing");

        result.ShouldBe(1);
        _stderr.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task State_OnSeed_NoParentPropagation()
    {
        var seed = new WorkItemBuilder(1, "Seed Task").AsTask().AsSeed().InState("New").WithParent(100).Build();
        SetupActiveItem(seed);

        var parent = new WorkItemBuilder(100, "Parent").AsUserStory().InState("New").Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var cmd = CreateCommandWithPropagation();
        var result = await cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // Parent should NOT be patched for seeds
        await _adoService.DidNotReceive().PatchAsync(
            100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Category resolution
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_CategoryName_ResolvesToFirstStateAndIssuesTransition()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("InProgress"));

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(
            1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].FieldName == "System.State" && c[0].NewValue == "Active"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
        stdout.ShouldContain("→ Active");
        stdout.ShouldContain("resolved category 'InProgress' → 'Active'");
    }

    [Fact]
    public async Task State_CategoryName_AlreadyInTargetState_NoOpsAndAnnotates()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("InProgress"));

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        stdout.ShouldContain("already in 'Active'");
        stdout.ShouldContain("category 'InProgress'");
    }

    [Fact]
    public async Task State_ExactStateName_DoesNotEmitCategoryHint()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Active"));

        result.ShouldBe(0);
        stdout.ShouldContain("→ Active");
        stdout.ShouldNotContain("resolved category");
    }

    [Fact]
    public async Task State_CategoryNoMatchingStateOnType_ReturnsError()
    {
        // Configure a Basic-style process where Resolved is not present.
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        var item = CreateWorkItem(1, "Test", "To Do", WorkItemType.Issue);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Resolved");

        result.ShouldBe(1);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        var err = _stderr.ToString();
        err.ShouldContain("Unknown state 'Resolved'");
        err.ShouldContain("To Do");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-chain (multi-hop transitions)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_DirectTransitionRejected_ChainsThroughIntermediates()
    {
        var item = CreateWorkItem(1, "Story", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Direct New → Closed: rejected
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException("TF401320: state transition is not allowed"));
        // New → Active
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        // Active → Resolved
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Resolved" && c.Single().OldValue == "Active"),
                1, Arg.Any<CancellationToken>())
            .Returns(2);
        // Resolved → Closed
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "Resolved"),
                2, Arg.Any<CancellationToken>())
            .Returns(3);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Closed"));

        result.ShouldBe(0);
        stdout.ShouldContain("New → Active → Resolved → Closed");
        stdout.ShouldContain("(3 transitions)");
    }

    [Fact]
    public async Task State_ChainStopsMidPath_ReturnsErrorAndShowsReachedStates()
    {
        var item = CreateWorkItem(1, "Story", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Direct New → Closed: rejected
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException("TF401320: state transition is not allowed"));
        // New → Active: succeeds
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        // Active → Resolved: rejected
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Resolved" && c.Single().OldValue == "Active"),
                1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException("TF401320: state transition is not allowed"));
        // Active → Closed (final retry): also rejected
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "Active"),
                1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException("TF401320: state transition is not allowed"));

        var result = await _cmd.ExecuteAsync("Closed");

        result.ShouldBe(1);
        var err = _stderr.ToString();
        err.ShouldContain("chain stopped at 'Active'");
        err.ShouldContain("New → Active");
    }

    [Fact]
    public async Task State_NonTransitionError_FastFailsWithoutChaining()
    {
        var item = CreateWorkItem(1, "Story", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        await Should.ThrowAsync<AdoAuthenticationException>(() => _cmd.ExecuteAsync("Closed"));

        // Single PATCH attempt; no chain follow-ups.
        await _adoService.Received(1).PatchAsync(
            1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_SingleHopSuccess_ShowsSimpleArrowFormat()
    {
        var item = CreateWorkItem(1, "Story", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Active"));

        result.ShouldBe(0);
        stdout.ShouldContain("→ Active");
        // Multi-hop format only kicks in when transitions > 1.
        stdout.ShouldNotContain("(1 transitions)");
        stdout.ShouldNotContain("transitions)");
    }

    [Fact]
    public async Task State_Success_JsonOutput_EmitsStateChangedRecord()
    {
        var item = CreateWorkItem(42, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Active", outputFormat: "json"));

        result.ShouldBe(0);
        stdout.ShouldContain("\"id\": 42");
        stdout.ShouldContain("\"toState\": \"Active\"");
        stdout.ShouldContain("\"fromState\": \"New\"");
        stdout.ShouldContain("\"transitionCount\": 1");
        stdout.ShouldContain("\"isCategoryResolution\": false");
        // Hints must NOT appear in JSON output — would corrupt single-Record shape into an array.
        stdout.ShouldNotContain("\"kind\":");
    }

    [Fact]
    public async Task State_AlreadyInState_JsonOutput_EmitsAlreadyInStateRecord()
    {
        var item = CreateWorkItem(7, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Active", outputFormat: "json"));

        result.ShouldBe(0);
        stdout.ShouldContain("\"id\": 7");
        stdout.ShouldContain("\"state\": \"Active\"");
        stdout.ShouldContain("\"requestedState\": \"Active\"");
        stdout.ShouldContain("\"isCategoryResolution\": false");
    }

    [Fact]
    public async Task State_Success_MinimalOutput_OmitsCheckmark()
    {
        var item = CreateWorkItem(13, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(13, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(13, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _pendingChangeStore.GetChangesAsync(13, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var (result, stdout) = await CaptureStdoutAsync(() => _cmd.ExecuteAsync("Active", outputFormat: "minimal"));

        result.ShouldBe(0);
        stdout.ShouldNotContain("✓");
        stdout.ShouldContain("→ Active");
    }

    private static async Task<(int Result, string Stdout)> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await action();
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
