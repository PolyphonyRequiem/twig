using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Formatters;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class TrackingCommandTests
{
    private readonly ITrackingService _trackingService = Substitute.For<ITrackingService>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly OutputFormatterFactory _formatterFactory = new(new HumanOutputFormatter());

    // ADO #145/#146: pin/unpin route through the shared mutation-workflow seam, and the BENCH is
    // the only pin store — the tracking file's pin half was deleted rather than migrated. The
    // Bench store is REAL (in-memory SQLite) because selectors are written and read back within a
    // test, and a substitute returning an empty Bench would answer "was it pinned?" with no every
    // time while the assertions still looked plausible.
    private readonly SqliteCacheStore _benchStore = new("Data Source=:memory:");

    private SqliteBenchRepository BenchRepo => new(_benchStore);

    private TrackingCommand CreateCommand()
    {
        var pinWorkflow = new PinWorkflow(BenchRepo, new DefaultBenchSelectors(null));
        return new TrackingCommand(_trackingService, _workItemRepo, _formatterFactory, pinWorkflow);
    }

    /// <summary>
    /// Asserts against the Bench's actual selectors rather than a substitute's received calls —
    /// a received-call assertion passes against a write that lands where nothing reads.
    /// </summary>
    private async Task<IReadOnlyCollection<BenchSelector>> SelectorsAsync()
        => (await BenchRepo.GetByNameAsync(Bench.DefaultName))?.Selectors ?? [];

    // ── Track ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Track_ValidId_ReturnsZero()
    {
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.TrackAsync(42));

        result.ShouldBe(0);
        stdout.ShouldContain("Tracking #42");
        (await SelectorsAsync()).ShouldContain(BenchSelector.ForItem(42));
    }

    [Fact]
    public async Task Track_IncludesTitleWhenCached()
    {
        var item = CreateWorkItem(42, "Fix the login bug");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.TrackAsync(42));

        result.ShouldBe(0);
        stdout.ShouldContain("Fix the login bug");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public async Task Track_InvalidId_ReturnsTwo(int invalidId)
    {
        var cmd = CreateCommand();
        var (result, stderr) = await StderrCapture.RunAsync(() => cmd.TrackAsync(invalidId));

        result.ShouldBe(2);
        stderr.ShouldContain("Cannot track seeds or invalid IDs");
    }

    // ── TrackTree ──────────────────────────────────────────────────────

    [Fact]
    public async Task TrackTree_ValidId_ReturnsZero()
    {
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.TrackTreeAsync(100));

        result.ShouldBe(0);
        stdout.ShouldContain("Tracking #100");
        stdout.ShouldContain("(tree)");
        (await SelectorsAsync()).ShouldContain(BenchSelector.ForSubtree(100));
    }

    [Fact]
    public async Task TrackTree_InvalidId_ReturnsTwo()
    {
        var cmd = CreateCommand();
        var (result, stderr) = await StderrCapture.RunAsync(() => cmd.TrackTreeAsync(-5));

        result.ShouldBe(2);
        stderr.ShouldContain("Cannot track seeds or invalid IDs");
    }

    // ── Untrack ────────────────────────────────────────────────────────

    [Fact]
    public async Task Untrack_ValidId_ReturnsZero()
    {
        // The discriminating precondition: it really is pinned before the unpin, so
        // "Untracked #42" is a report about state and not the message this command always prints.
        var cmd = CreateCommand();
        await StdoutCapture.RunAsync(() => cmd.TrackAsync(42));
        (await SelectorsAsync()).ShouldContain(BenchSelector.ForItem(42));

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.UntrackAsync(42));

        result.ShouldBe(0);
        stdout.ShouldContain("Untracked #42");
        (await SelectorsAsync()).ShouldNotContain(BenchSelector.ForItem(42));
    }

    [Fact]
    public async Task Untrack_NotTracked_ShowsNotTrackedMessage()
    {
        // Nothing pinned 42 on the Bench and nothing tracked it in the file.
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.UntrackAsync(42));

        result.ShouldBe(0);
        stdout.ShouldContain("#42 was not tracked");
    }

    [Fact]
    public async Task Untrack_InvalidId_ReturnsTwo()
    {
        var cmd = CreateCommand();
        var (result, stderr) = await StderrCapture.RunAsync(() => cmd.UntrackAsync(0));

        result.ShouldBe(2);
        stderr.ShouldContain("Cannot untrack seeds or invalid IDs");
    }

    // ── Exclude ────────────────────────────────────────────────────────

    [Fact]
    public async Task Exclude_ValidId_ReturnsZero()
    {
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExcludeAsync(42));

        result.ShouldBe(0);
        stdout.ShouldContain("Excluded #42");
        await _trackingService.Received(1).ExcludeAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exclude_InvalidId_ReturnsTwo()
    {
        var cmd = CreateCommand();
        var (result, stderr) = await StderrCapture.RunAsync(() => cmd.ExcludeAsync(-1));

        result.ShouldBe(2);
        stderr.ShouldContain("Cannot exclude seeds or invalid IDs");
    }

    // ── Exclusions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Exclusions_Empty_ShowsNoExclusions()
    {
        _trackingService.ListExclusionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("No exclusions configured");
    }

    [Fact]
    public async Task Exclusions_WithItems_ListsAll()
    {
        var items = new List<ExcludedItem>
        {
            new(10, "noisy", DateTimeOffset.UtcNow),
            new(20, "done", DateTimeOffset.UtcNow),
        };
        _trackingService.ListExclusionsAsync(Arg.Any<CancellationToken>()).Returns(items);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(10, "Noisy item"));
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(20, "Done item"));
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("#10: Noisy item");
        stdout.ShouldContain("#20: Done item");
        stdout.ShouldContain("2 exclusion(s) total");
    }

    [Fact]
    public async Task Exclusions_WithMissingCacheItem_ShowsIdOnly()
    {
        var items = new List<ExcludedItem>
        {
            new(99, "reason", DateTimeOffset.UtcNow),
        };
        _trackingService.ListExclusionsAsync(Arg.Any<CancellationToken>()).Returns(items);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("#99");
    }

    [Fact]
    public async Task Exclusions_Clear_RemovesAll()
    {
        _trackingService.ClearExclusionsAsync(Arg.Any<CancellationToken>()).Returns(3);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync(clear: true));

        result.ShouldBe(0);
        stdout.ShouldContain("Cleared 3 exclusion(s)");
        await _trackingService.Received(1).ClearExclusionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exclusions_Clear_NoExclusions_ShowsInfo()
    {
        _trackingService.ClearExclusionsAsync(Arg.Any<CancellationToken>()).Returns(0);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync(clear: true));

        result.ShouldBe(0);
        stdout.ShouldContain("No exclusions to clear");
    }

    [Fact]
    public async Task Exclusions_Remove_ExistingExclusion_ShowsSuccess()
    {
        _trackingService.RemoveExclusionAsync(42, Arg.Any<CancellationToken>()).Returns(true);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync(remove: 42));

        result.ShouldBe(0);
        stdout.ShouldContain("Removed exclusion for #42");
        await _trackingService.Received(1).RemoveExclusionAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exclusions_Remove_NotExcluded_ShowsInfo()
    {
        _trackingService.RemoveExclusionAsync(42, Arg.Any<CancellationToken>()).Returns(false);
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ExclusionsAsync(remove: 42));

        result.ShouldBe(0);
        stdout.ShouldContain("#42 was not excluded");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Exclusions_Remove_InvalidId_ReturnsTwo(int invalidId)
    {
        var cmd = CreateCommand();

        var (result, stderr) = await StderrCapture.RunAsync(() => cmd.ExclusionsAsync(remove: invalidId));

        result.ShouldBe(2);
        stderr.ShouldContain("Provide a positive work item ID");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
