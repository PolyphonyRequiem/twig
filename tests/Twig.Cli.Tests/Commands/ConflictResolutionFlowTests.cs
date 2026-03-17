using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
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

    public ConflictResolutionFlowTests()
    {
        _fmt = Substitute.For<IOutputFormatter>();
        _fmt.FormatError(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _fmt.FormatInfo(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _fmt.FormatSuccess(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        _consoleInput = Substitute.For<IConsoleInput>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
    }

    [Fact]
    public async Task NoConflicts_ReturnsProceed()
    {
        // Same revision → NoConflict
        var local = CreateWorkItem(1, "Title", "New");
        var remote = CreateWorkItem(1, "Title", "New");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

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
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_JsonFormat_ReturnsConflictJsonEmitted()
    {
        var (local, remote) = CreateConflictingPair();

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var result = await ConflictResolutionFlow.ResolveAsync(
                local, remote, _fmt, "json", _consoleInput, _workItemRepo, "accepted");

            result.ShouldBe(ConflictOutcome.ConflictJsonEmitted);
            stdout.ToString().ShouldContain("conflicts");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task HasConflicts_JsonFormat_CaseInsensitive()
    {
        var (local, remote) = CreateConflictingPair();

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var result = await ConflictResolutionFlow.ResolveAsync(
                local, remote, _fmt, "JSON", _consoleInput, _workItemRepo, "accepted");

            result.ShouldBe(ConflictOutcome.ConflictJsonEmitted);
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task HasConflicts_UserChoosesAbort_ReturnsAborted()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("a");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

        result.ShouldBe(ConflictOutcome.Aborted);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_NullInput_ReturnsAborted()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns((string?)null);

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

        result.ShouldBe(ConflictOutcome.Aborted);
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_SavesRemoteAndReturnsAcceptedRemote()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted remote");

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        await _workItemRepo.Received(1).SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_PrintsAcceptRemoteMessage()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");
        _fmt.FormatSuccess("Custom accept message").Returns("Custom accept message");

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            await ConflictResolutionFlow.ResolveAsync(
                local, remote, _fmt, "human", _consoleInput, _workItemRepo, "Custom accept message");

            _fmt.Received(1).FormatSuccess("Custom accept message");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
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
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted", onAcceptRemote);

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        callOrder.ShouldBe(new[] { "callback", "save" });
    }

    [Fact]
    public async Task HasConflicts_UserChoosesRemote_WithoutCallback_SavesWithoutError()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("r");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted", onAcceptRemote: null);

        result.ShouldBe(ConflictOutcome.AcceptedRemote);
        await _workItemRepo.Received(1).SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasConflicts_UserChoosesLocal_ReturnsProceed()
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns("l");

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("y")]
    public async Task HasConflicts_UnrecognizedInput_ReturnsProceed(string input)
    {
        var (local, remote) = CreateConflictingPair();
        _consoleInput.ReadLine().Returns(input);

        var result = await ConflictResolutionFlow.ResolveAsync(
            local, remote, _fmt, "human", _consoleInput, _workItemRepo, "accepted");

        result.ShouldBe(ConflictOutcome.Proceed);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Creates a pair of WorkItems that will produce HasConflicts from ConflictResolver.</summary>
    private static (WorkItem Local, WorkItem Remote) CreateConflictingPair()
    {
        var local = CreateWorkItem(1, "Local Title", "New");
        var remote = CreateWorkItem(1, "Remote Title", "New");
        remote.MarkSynced(5); // Different revision + different Title → HasConflicts
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
