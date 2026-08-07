using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// ADO #149 — the VIEW follows the switch (docs/specs/bench.spec.md §5).
/// <para>
/// 🔴 This is the acceptance sentence "switching changes which items the view shows" asserted at
/// the READ path, not at the workflow. The workflow tests prove the pointer moves and that pins
/// land on the right Bench; neither of them can see a <see cref="WorkingSetService"/> that ignores
/// the pointer and keeps evaluating the default. That is a silent failure — the person switches,
/// nothing errors, and they keep looking at the arrangement they put down.
/// </para>
/// </summary>
public sealed class WorkingSetFollowsCurrentBenchTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly IBenchRepository _benchRepo = Substitute.For<IBenchRepository>();

    private static readonly IterationPath Sprint = IterationPath.Parse(@"Project\Sprint 7").Value;

    private static readonly Bench DefaultBench = new()
    {
        Id = 1,
        Name = Bench.DefaultName,
        IsDefault = true,
        Selectors = [BenchSelector.ForItem(111)],
    };

    private static readonly Bench Other = new()
    {
        Id = 2,
        Name = "release blockers",
        Selectors = [BenchSelector.ForItem(222)],
    };

    public WorkingSetFollowsCurrentBenchTests()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(Sprint);
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>()).Returns(new[] { Sprint });
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TrackedItem>());

        _benchRepo.GetOrCreateDefaultAsync(
                Arg.Any<IReadOnlyCollection<BenchSelector>>(), Arg.Any<CancellationToken>())
            .Returns(DefaultBench);
    }

    private WorkingSetService CreateSut() => new(
        _contextStore, _workItemRepo, _pendingStore, _iterationService,
        userDisplayName: null,
        _benchRepo,
        new BenchEvaluator(_workItemRepo, _calendar, _pendingStore));

    [Fact]
    public async Task WithNoSwitch_TheViewEvaluatesTheDefaultBench()
    {
        _benchRepo.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns((Bench?)null);

        var set = await CreateSut().ComputeAsync();

        set.TrackedItemIds.ShouldContain(111);
    }

    /// <summary>
    /// The discriminating pair: the same fixture, differing only in what the stored pointer says,
    /// must produce different views. If the assertion only checked that 222 was present, an
    /// implementation unioning both Benches would pass — so the item from the Bench that was put
    /// DOWN is asserted absent.
    /// </summary>
    [Fact]
    public async Task AfterSwitching_TheViewEvaluatesTheBenchTheyAreStandingOn()
    {
        _benchRepo.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Other);

        var set = await CreateSut().ComputeAsync();

        set.TrackedItemIds.ShouldContain(222);
        set.TrackedItemIds.ShouldNotContain(111);
    }

    /// <summary>
    /// A pointer whose Bench has been deleted falls back to the default rather than throwing or
    /// showing nothing: the default cannot go missing, the person named nothing, and there is no
    /// wrong target to act on. That is NOT the unknown-Bench error in disguise — that error is
    /// about a name a person just typed.
    /// </summary>
    [Fact]
    public async Task WhenTheStoredPointerNoLongerResolves_TheViewFallsBackToTheDefault()
    {
        _benchRepo.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns((Bench?)null);

        var set = await CreateSut().ComputeAsync();

        set.TrackedItemIds.ShouldContain(111);
    }
}
