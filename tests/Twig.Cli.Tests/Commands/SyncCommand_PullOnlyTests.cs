using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Focused tests for <c>twig sync --pull-only</c>: verifies that the pull-only flag
/// skips the flush phase while still calling <see cref="RefreshOrchestrator"/>.
/// </summary>
public sealed class SyncCommand_PullOnlyTests : RefreshCommandTestBase
{
    private readonly IPendingChangeFlusher _flusher;

    public SyncCommand_PullOnlyTests()
    {
        _flusher = Substitute.For<IPendingChangeFlusher>();
    }

    private SyncCommand CreateSyncCommand(TextWriter? stderr = null) =>
        new(_flusher, CreateRefreshCommand(stderr), _formatterFactory, stderr);

    // ═══════════════════════════════════════════════════════════════
    //  --pull-only calls RefreshOrchestrator but NOT FlushAllAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PullOnly_CallsRefreshOrchestrator_ButNotFlushAllAsync()
    {
        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync(pullOnly: true);

        result.ShouldBe(0);

        // FlushAllAsync must NOT have been called — pull-only skips flush
        await _flusher.DidNotReceive().FlushAllAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // RefreshOrchestrator was invoked (QueryByWiqlAsync is its first significant call)
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullOnly_DoesNotCallFlushAsync_Either()
    {
        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync(pullOnly: true);

        // Neither FlushAllAsync nor FlushAsync should be called
        await _flusher.DidNotReceive().FlushAllAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  --pull-only --force passes force=true to refresh
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PullOnly_WithForce_PassesForceToRefresh()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });

        var item = new WorkItem
        {
            Id = 1, Title = "Item", Type = WorkItemType.Task, State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value
        };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync(force: true, pullOnly: true);

        result.ShouldBe(0);

        // Force bypasses dirty guard — SaveBatchAsync used instead of protected writer
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());

        // Flush was still skipped despite force flag
        await _flusher.DidNotReceive().FlushAllAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullOnly_WithForce_ReturnsZero()
    {
        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync(force: true, pullOnly: true);

        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No flags — both phases execute
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoFlags_CallsBothFlushAndRefresh()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Flush was called
        await _flusher.Received(1).FlushAllAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Refresh was called
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoFlags_FlushCalledBeforeRefresh()
    {
        var callOrder = new List<string>();

        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("flush");
                return new FlushResult(0, 0, 0, []);
            });

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("refresh");
                return Array.Empty<int>();
            });

        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync();

        callOrder.ShouldBe(new[] { "flush", "refresh" });
    }

    // ═══════════════════════════════════════════════════════════════
    //  --pull-only does not emit push summary to stdout
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PullOnly_DoesNotEmitPushSummary()
    {
        var cmd = CreateSyncCommand();

        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try { await cmd.ExecuteAsync(pullOnly: true); }
        finally { Console.SetOut(originalOut); }

        stdout.ToString().ShouldNotContain("Sync push");
    }

    // ═══════════════════════════════════════════════════════════════
    //  --pull-only does not write to stderr (no errors, no hint)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PullOnly_NoStderrOutput()
    {
        var stderr = new StringWriter();
        var cmd = CreateSyncCommand(stderr);
        await cmd.ExecuteAsync(pullOnly: true);

        stderr.ToString().ShouldBeEmpty();
    }
}
