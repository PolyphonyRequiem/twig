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
    private readonly StringWriter _stdout;
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
        _stdout = new StringWriter();
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
            formatterFactory, hintEngine, stdout: _stdout, stderr: _stderr);
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
        // Use a restricted config where only "To Do" and "Done" are valid states.
        // An item in state "Design" (not in the config's state list) trying to transition
        // to "Done" (a valid target state) produces a disallowed transition because the
        // (Design → Done) pair is not in the transition rules.
        var restrictedConfig = new ProcessConfigBuilder()
            .AddType("User Story",
                ProcessConfigBuilder.S(("To Do", StateCategory.Proposed), ("Done", StateCategory.Completed)))
            .Build();
        _processConfigProvider.GetConfiguration().Returns(restrictedConfig);

        var item = CreateWorkItem(1, "Test", "Design", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync(state: "Done");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("not allowed");
    }

    [Fact]
    public async Task StateOnly_BackwardTransition_NoConfirmationNeeded_Succeeds()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(state: "New"); // Active → New is now Forward

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine(); // No confirmation prompt
    }

    [Fact]
    public async Task StateOnly_BackwardTransition_NoPrompt_NeverCancels()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(state: "New"); // Active → New is now Forward

        result.ShouldBe(0);
        _consoleInput.DidNotReceive().ReadLine();
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
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            id: 42);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── --ids multi-item mode ──────────────────────────────────────────

    [Fact]
    public async Task IdsParameter_MultipleItems_AllSucceed()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        SetupSuccessfulPatch(item1);
        SetupSuccessfulPatch(item2);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10,20");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(10,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().PatchAsync(20,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdsParameter_ContinuesOnFailure_ReportsPartialSuccess()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        // Item 20 not in cache and not fetchable
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("not found"));
        _adoService.PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10,20");

        result.ShouldBe(1); // Partial failure → exit code 1
        // Item 10 should still have been patched
        await _adoService.Received().PatchAsync(10,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdsParameter_FetchThrowsDuringProcess_ContinuesToNextItem()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        // Item 10: FetchAsync throws during ProcessItemAsync step 1
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO service unavailable"));
        // Item 20: succeeds normally
        SetupSuccessfulPatch(item2);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10,20");

        result.ShouldBe(1); // Partial failure → exit code 1
        // Item 10 failed but item 20 should still succeed
        await _adoService.Received().PatchAsync(20,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Verify error message for item 10 was reported
        _stderr.ToString().ShouldContain("ADO service unavailable");
    }

    [Fact]
    public async Task IdsParameter_SkipsInteractiveConflictResolution()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        SetupSuccessfulPatch(item1);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10");

        result.ShouldBe(0);
        // consoleInput should NOT have been called (no interactive prompts in multi-item)
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task IdsParameter_BackwardTransition_NoConfirmation_Succeeds()
    {
        var item = CreateWorkItem(10, "Item A", "Active", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "New", // backward transition — now Forward, no confirmation
            ids: "10");

        result.ShouldBe(0);
        // Should NOT prompt — backward is now Forward
        _consoleInput.DidNotReceive().ReadLine();
        await _adoService.Received().PatchAsync(10,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdsParameter_InvalidIds_ReturnsExitCode2()
    {
        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "abc,xyz");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("valid comma-separated integer IDs");
    }

    [Fact]
    public async Task IdsParameter_IdAndIdsMutuallyExclusive_ReturnsExitCode2()
    {
        var result = await _cmd.ExecuteAsync(
            state: "Active",
            id: 1,
            ids: "2,3");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task IdsParameter_JsonOutput_ProducesStructuredBatchResult()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        SetupSuccessfulPatch(item1);
        SetupSuccessfulPatch(item2);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10,20",
            outputFormat: "json");

        result.ShouldBe(0);
        var output = _stdout.ToString();
        output.ShouldContain("\"totalItems\": 2");
        output.ShouldContain("\"succeeded\": 2");
        output.ShouldContain("\"failed\": 0");
        output.ShouldContain("\"items\"");
        output.ShouldContain("\"itemId\": 10");
        output.ShouldContain("\"itemId\": 20");
        output.ShouldContain("\"success\": true");
    }

    [Fact]
    public async Task IdsParameter_JsonOutput_PartialFailure_ProducesPerItemErrorField()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        // Item 10 succeeds
        SetupSuccessfulPatch(item1);
        // Item 20 fails at fetch
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO service unavailable"));

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10,20",
            outputFormat: "json");

        result.ShouldBe(1); // Partial failure → exit code 1
        var output = _stdout.ToString();
        output.ShouldContain("\"totalItems\": 2");
        output.ShouldContain("\"succeeded\": 1");
        output.ShouldContain("\"failed\": 1");
        output.ShouldContain("\"success\": true");
        output.ShouldContain("\"success\": false");
        output.ShouldContain("\"error\": \"ADO service unavailable\"");
    }

    [Fact]
    public async Task IdsParameter_WithNote_AddsNoteToAllItems()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync(
            note: "Batch note",
            ids: "10,20");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(10, "Batch note", Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(20, "Batch note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdsParameter_SingleId_TreatedAsMultiItemMode()
    {
        var item = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        SetupSuccessfulPatch(item);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: "10");

        result.ShouldBe(0);
        // Still multi-item mode (non-interactive)
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task IdsParameter_WhitespaceAroundIds_ParsesCorrectly()
    {
        var item1 = CreateWorkItem(10, "Item A", "New", WorkItemType.UserStory);
        var item2 = CreateWorkItem(20, "Item B", "New", WorkItemType.UserStory);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        SetupSuccessfulPatch(item1);
        SetupSuccessfulPatch(item2);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            ids: " 10 , 20 ");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(10,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().PatchAsync(20,
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Single-item conflict outcome rendering ────────────────────

    [Theory]
    [InlineData("a")] // user aborts
    [InlineData("r")] // user accepts remote
    public async Task SingleItem_ConflictResolved_WithNote_DoesNotAddNote(string userResponse)
    {
        var local = CreateWorkItem(1, "Local Title", "New", WorkItemType.UserStory);
        var remote = CreateWorkItem(1, "Remote Title", "New", WorkItemType.UserStory);
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _consoleInput.ReadLine().Returns(userResponse);

        var result = await _cmd.ExecuteAsync(
            state: "Active",
            note: "Should not be submitted");

        result.ShouldBe(0);
        _stdout.ToString().ShouldNotContain("note added");
        await _adoService.DidNotReceive().AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleItem_ConflictAborted_NoRedundantSuccessLine()
    {
        var local = CreateWorkItem(1, "Local Title", "New", WorkItemType.UserStory);
        var remote = CreateWorkItem(1, "Remote Title", "New", WorkItemType.UserStory);
        remote.MarkSynced(5);

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _consoleInput.ReadLine().Returns("a");

        var result = await _cmd.ExecuteAsync(state: "Active");

        result.ShouldBe(0);
        // No success render for a no-op Aborted outcome
        _stdout.ToString().ShouldNotContain("#1");
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
