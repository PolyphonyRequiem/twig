using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Sync;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="ConflictResolutionFlow"/> covering all branches:
/// no-conflict, auto-mergeable, JSON conflict output, human prompt (l/r/a/null/unrecognized),
/// and the optional <c>onAcceptRemote</c> callback.
/// </summary>
public class ConflictResolutionFlowTests
{
    private readonly IOutputFormatter _fmt;
    private readonly IConsoleInput _consoleInput;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;

    public ConflictResolutionFlowTests()
    {
        _fmt = Substitute.For<IOutputFormatter>();
        _fmt.FormatError(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _fmt.FormatInfo(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _fmt.FormatSuccess(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        _consoleInput = Substitute.For<IConsoleInput>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();

        // No staged edits: the local side has not moved, so any divergence is
        // remote-only. Tests that need a real merge base override this.
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    [Fact]
    public async Task NoConflicts_ReturnsProceed()
    {
        // Same revision → NoConflict
        var local = CreateWorkItem(1, "Title", "New");
        var remote = CreateWorkItem(1, "Title", "New");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoMergeable_ReturnsProceed()
    {
        // Different revisions with a field present on only one side → AutoMergeable
        var local = CreateWorkItem(1, "Title", "New");
        local.SetField("Custom.Field", "value");
        var remote = CreateWorkItem(1, "Title", "New");
        remote.MarkSynced(5); // Different revision + field on local only → AutoMergeable

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_JsonFormat_ReturnsConflictJsonEmitted()
    {
        var (local, remote) = CreateConflictingPair();

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "json", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.ConflictJsonEmitted);
    }

    [Fact]
    public async Task HasConflicts_JsonFormat_CaseInsensitive()
    {
        var (local, remote) = CreateConflictingPair();

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "JSON", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.ConflictJsonEmitted);
    }

    [Fact]
    public async Task HasConflicts_UserChoosesAbort_ReturnsAborted()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("a");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Aborted);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_NullInput_ReturnsAborted()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns((string?)null);

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Aborted);
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_SavesRemoteAndReturnsAcceptedRemote()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted remote");

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        await _workItemRepo.Received(1).SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_PrintsAcceptRemoteMessage()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var writer = new System.IO.StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            await ConflictResolutionFlow.ResolveAsync(
                local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "Custom accept message");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        writer.ToString().ShouldContain("Custom accept message");
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_WithCallback_InvokesCallbackBeforeSave()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var callOrder = new List<string>();
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("save"));

        Func<Task> onAcceptRemote = () =>
        {
            callOrder.Add("callback");
            return Task.CompletedTask;
        };

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted", onAcceptRemote);

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        callOrder.ShouldBe(new[] { "callback", "save" });
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_WithoutCallback_SavesWithoutError()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted", onAcceptRemote: null);

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        await _workItemRepo.Received(1).SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_UserChoosesLocal_ReturnsProceed()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("l");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("y")]
    public async Task HasConflicts_UnrecognizedInput_ReturnsAborted(string input)
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns(input);

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, _pendingChangeStore, "accepted");

        result.ShouldBe(ConflictOutcome.Aborted);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Creates a pair of WorkItems that produce HasConflicts from <see cref="ThreeWayMerge"/>,
    /// and stages the local edit that makes it a genuine conflict.
    /// </summary>
    /// <remarks>
    /// Wayfinder 0004 slice 3: a divergence alone is NO LONGER a conflict. With a merge base,
    /// "remote changed a field the user never touched" is an auto-merge — that narrowing is the
    /// point of the module. A conflict requires BOTH sides to have moved off the base, so this
    /// fixture must stage the local edit; without it these tests would assert the old two-way
    /// semantics and pass vacuously against a resolver that ignores local intent entirely.
    /// <para>
    /// The staged row is also the ONLY source of local intent for a first-class property: Title
    /// is init-only on <see cref="WorkItem"/>, so staging never writes it to the aggregate.
    /// </para>
    /// </remarks>
    private (WorkItem Local, WorkItem Remote) CreateConflictingPair()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5); // Different revision + different Title

        // Base "Base Title" -> local staged "Local Title", remote moved to "Remote Title".
        // Both sides moved off the base and disagree: a genuine conflict.
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title"),
            });

        return (local, remote);
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
