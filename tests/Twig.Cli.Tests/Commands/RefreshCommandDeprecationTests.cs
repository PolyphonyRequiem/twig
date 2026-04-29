using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests that the hidden <c>twig refresh</c> alias routes through <c>sync --pull-only</c>
/// silently (no deprecation hint).
/// </summary>
public sealed class RefreshCommandDeprecationTests : RefreshCommandTestBase
{
    private readonly IPendingChangeFlusher _flusher;

    public RefreshCommandDeprecationTests()
    {
        _flusher = Substitute.For<IPendingChangeFlusher>();
    }

    private TwigCommands CreateCommands()
    {
        var syncCommand = new SyncCommand(
            _flusher, CreateRefreshCommand(), _formatterFactory);

        var services = new ServiceCollection()
            .AddSingleton(syncCommand)
            .BuildServiceProvider();
        return new TwigCommands(services);
    }

    [Fact]
    public async Task Refresh_DoesNotWriteDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.Refresh(ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldNotContain("deprecated");
        stderr.ShouldNotContain("hint:");
    }

    [Fact]
    public async Task Refresh_RoutesToSyncPullOnly_SkipsFlush()
    {
        var commands = CreateCommands();

        await commands.Refresh(ct: CancellationToken.None);

        // FlushAllAsync must NOT be called — refresh routes through sync --pull-only
        await _flusher.DidNotReceive().FlushAllAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_RoutesToSyncPullOnly_CallsRefreshOrchestrator()
    {
        var commands = CreateCommands();

        await commands.Refresh(output: "json", ct: CancellationToken.None);

        // RefreshOrchestrator is called (QueryByWiqlAsync is the first significant op)
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_PassesForceFlag_ToSyncPullOnly()
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

        var commands = CreateCommands();
        var result = await commands.Refresh(force: true, ct: CancellationToken.None);

        result.ShouldBe(0);

        // Force bypasses dirty guard — SaveBatchAsync is called
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ReturnsZero_OnSuccess()
    {
        var commands = CreateCommands();
        var result = await commands.Refresh(ct: CancellationToken.None);

        result.ShouldBe(0);
    }
}
