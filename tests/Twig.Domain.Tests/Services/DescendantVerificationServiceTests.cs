using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class DescendantVerificationServiceTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly DescendantVerificationService _service;

    public DescendantVerificationServiceTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        _service = new DescendantVerificationService(
            _workItemRepo, _adoService, _processConfigProvider);
    }

    // ── Verified: all children terminal ────────────────────────────

    [Fact]
    public async Task Verified_WhenAllChildrenAreDone()
    {
        var child1 = new WorkItemBuilder(10, "Task 1").AsTask().InState("Done").WithParent(1).Build();
        var child2 = new WorkItemBuilder(11, "Task 2").AsTask().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
        result.TotalChecked.ShouldBe(2);
        result.Incomplete.ShouldBeEmpty();
        result.RootId.ShouldBe(1);
    }

    [Fact]
    public async Task Verified_WhenNoChildren()
    {
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
        result.TotalChecked.ShouldBe(0);
        result.Incomplete.ShouldBeEmpty();
    }

    // ── Not verified: incomplete children ──────────────────────────

    [Fact]
    public async Task NotVerified_WhenChildInDoing()
    {
        var child = new WorkItemBuilder(10, "Task In Progress")
            .AsTask().InState("Doing").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeFalse();
        result.TotalChecked.ShouldBe(1);
        result.Incomplete.Count.ShouldBe(1);

        var item = result.Incomplete[0];
        item.Id.ShouldBe(10);
        item.Title.ShouldBe("Task In Progress");
        item.Type.ShouldBe("Task");
        item.State.ShouldBe("Doing");
        item.ParentId.ShouldBe(1);
        item.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task NotVerified_WhenChildInToDo()
    {
        var child = new WorkItemBuilder(10, "Task Not Started")
            .AsTask().InState("To Do").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeFalse();
        result.Incomplete.Count.ShouldBe(1);
        result.Incomplete[0].State.ShouldBe("To Do");
    }

    // ── Root item excluded from counts ─────────────────────────────

    [Fact]
    public async Task RootNotIncludedInCheckedOrIncomplete()
    {
        // Root is in Doing (non-terminal), but should not appear in results
        var child = new WorkItemBuilder(10, "Task Done").AsTask().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.RootId.ShouldBe(1);
        result.TotalChecked.ShouldBe(1); // only the child, not root
        result.Verified.ShouldBeTrue();
    }

    // ── Recursive traversal ────────────────────────────────────────

    [Fact]
    public async Task RecursesIntoGrandchildren()
    {
        var child = new WorkItemBuilder(10, "Issue 1").AsIssue().InState("Done").WithParent(1).Build();
        var grandchild = new WorkItemBuilder(20, "Task Under Issue")
            .AsTask().InState("Doing").WithParent(10).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { grandchild });
        _adoService.FetchChildrenAsync(20, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1, maxDepth: 2);

        result.Verified.ShouldBeFalse();
        result.TotalChecked.ShouldBe(2);
        result.Incomplete.Count.ShouldBe(1);
        result.Incomplete[0].Id.ShouldBe(20);
        result.Incomplete[0].Depth.ShouldBe(2);
    }

    // ── maxDepth limiting ──────────────────────────────────────────

    [Fact]
    public async Task RespectsMaxDepth_DoesNotTraverseBeyondLimit()
    {
        var child = new WorkItemBuilder(10, "Issue 1").AsIssue().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        // maxDepth=1 means only direct children, no grandchildren lookup
        var result = await _service.VerifyAsync(1, maxDepth: 1);

        result.TotalChecked.ShouldBe(1);
        result.Verified.ShouldBeTrue();

        // Should NOT have called FetchChildrenAsync for child ID 10
        await _adoService.DidNotReceive().FetchChildrenAsync(10, Arg.Any<CancellationToken>());
    }

    // ── ADO-first with cache fallback ──────────────────────────────

    [Fact]
    public async Task FallsBackToCache_WhenAdoFails()
    {
        var cachedChild = new WorkItemBuilder(10, "Cached Task")
            .AsTask().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network error"));
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { cachedChild });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
        result.TotalChecked.ShouldBe(1);
    }

    [Fact]
    public async Task DoesNotFallBackToCache_OnCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.VerifyAsync(1, ct: cts.Token));
    }

    // ── Unmapped type handling ─────────────────────────────────────

    [Fact]
    public async Task UnmappedType_TreatedAsNonTerminal()
    {
        // Create a child with a type that isn't in the Basic process config
        var child = new WorkItemBuilder(10, "Custom Item")
            .AsFeature() // Feature is not in Basic config
            .InState("Done")
            .WithParent(1)
            .Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeFalse();
        result.Incomplete.Count.ShouldBe(1);
        result.Incomplete[0].Id.ShouldBe(10);
    }

    // ── Terminal state categories ──────────────────────────────────

    [Fact]
    public async Task TerminalState_Completed()
    {
        var child = new WorkItemBuilder(10, "Task").AsTask().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
    }

    [Fact]
    public async Task TerminalState_Resolved_InAgileProcess()
    {
        // Agile process has Resolved state for User Stories
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());

        var child = new WorkItemBuilder(10, "Story").AsUserStory().InState("Resolved").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
    }

    [Fact]
    public async Task TerminalState_Removed_InAgileProcess()
    {
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());

        var child = new WorkItemBuilder(10, "Task").AsTask().InState("Removed").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeTrue();
    }

    // ── Mixed terminal and non-terminal ────────────────────────────

    [Fact]
    public async Task MixedChildren_OnlyIncompleteOnesReported()
    {
        var done = new WorkItemBuilder(10, "Done Task").AsTask().InState("Done").WithParent(1).Build();
        var doing = new WorkItemBuilder(11, "Doing Task").AsTask().InState("Doing").WithParent(1).Build();
        var todo = new WorkItemBuilder(12, "ToDo Task").AsTask().InState("To Do").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { done, doing, todo });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(12, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeFalse();
        result.TotalChecked.ShouldBe(3);
        result.Incomplete.Count.ShouldBe(2);
        result.Incomplete.ShouldContain(i => i.Id == 11);
        result.Incomplete.ShouldContain(i => i.Id == 12);
    }

    // ── Multi-level with partial fallback ──────────────────────────

    [Fact]
    public async Task PartialAdoFailure_FallsBackPerLevel()
    {
        var issue = new WorkItemBuilder(10, "Issue 1").AsIssue().InState("Done").WithParent(1).Build();
        var task = new WorkItemBuilder(20, "Task 1").AsTask().InState("Done").WithParent(10).Build();

        // Level 1: ADO succeeds
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { issue });

        // Level 2: ADO fails, cache fallback
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Timeout"));
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { task });

        var result = await _service.VerifyAsync(1, maxDepth: 2);

        result.Verified.ShouldBeTrue();
        result.TotalChecked.ShouldBe(2);
    }

    // ── maxDepth=0 ─────────────────────────────────────────────────

    [Fact]
    public async Task MaxDepthZero_SkipsAllChildren()
    {
        // maxDepth=0: BFS starts at depth 1, which is > 0 → skips everything, FetchChildrenAsync never called
        var result = await _service.VerifyAsync(1, maxDepth: 0);

        result.Verified.ShouldBeTrue();
        result.TotalChecked.ShouldBe(0);
        result.Incomplete.ShouldBeEmpty();
    }

    // ── Mixed Issues and Tasks ─────────────────────────────────────

    [Fact]
    public async Task MixedIssuesAndTasks_OnlyNonTerminalTasksInIncomplete()
    {
        // Issue is Done (terminal), but its child Tasks are not all Done
        var issue = new WorkItemBuilder(10, "Issue 1").AsIssue().InState("Done").WithParent(1).Build();
        var taskDone = new WorkItemBuilder(20, "Task Done").AsTask().InState("Done").WithParent(10).Build();
        var taskDoing = new WorkItemBuilder(21, "Task Doing").AsTask().InState("Doing").WithParent(10).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { issue });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { taskDone, taskDoing });
        _adoService.FetchChildrenAsync(20, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(21, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1, maxDepth: 2);

        result.Verified.ShouldBeFalse();
        result.TotalChecked.ShouldBe(3); // issue + 2 tasks
        result.Incomplete.Count.ShouldBe(1);

        var item = result.Incomplete[0];
        item.Id.ShouldBe(21);
        item.Title.ShouldBe("Task Doing");
        item.Type.ShouldBe("Task");
        item.State.ShouldBe("Doing");
        item.ParentId.ShouldBe(10);
        item.Depth.ShouldBe(2);
    }

    // ── ADO always called first ────────────────────────────────────

    [Fact]
    public async Task AdoCalledFirst_CacheNotTouched_WhenAdoSucceeds()
    {
        var child = new WorkItemBuilder(10, "Task").AsTask().InState("Done").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        await _service.VerifyAsync(1);

        // Cache should never be consulted when ADO succeeds
        await _workItemRepo.DidNotReceive().GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Multiple non-terminal tasks with field assertions ──────────

    [Fact]
    public async Task MultipleNonTerminalTasks_AllInIncompleteWithCorrectFields()
    {
        var task1 = new WorkItemBuilder(10, "Task Alpha").AsTask().InState("Doing").WithParent(1).Build();
        var task2 = new WorkItemBuilder(11, "Task Beta").AsTask().InState("To Do").WithParent(1).Build();

        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { task1, task2 });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _service.VerifyAsync(1);

        result.Verified.ShouldBeFalse();
        result.TotalChecked.ShouldBe(2);
        result.Incomplete.Count.ShouldBe(2);

        var alpha = result.Incomplete.Single(i => i.Id == 10);
        alpha.Title.ShouldBe("Task Alpha");
        alpha.Type.ShouldBe("Task");
        alpha.State.ShouldBe("Doing");
        alpha.ParentId.ShouldBe(1);
        alpha.Depth.ShouldBe(1);

        var beta = result.Incomplete.Single(i => i.Id == 11);
        beta.Title.ShouldBe("Task Beta");
        beta.Type.ShouldBe("Task");
        beta.State.ShouldBe("To Do");
        beta.ParentId.ShouldBe(1);
        beta.Depth.ShouldBe(1);
    }
}
