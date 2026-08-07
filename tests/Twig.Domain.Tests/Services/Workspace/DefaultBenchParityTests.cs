using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// Spec test 1 — DEFAULT-BENCH PARITY (docs/specs/bench.spec.md, Testing Decisions).
/// <para>
/// 🔴 The acceptance bar for ADO #144: with one Bench and no user action, the computed view is
/// identical to today's — same items, same order, same output. This test compares against a
/// CAPTURED BASELINE, not by eye.
/// </para>
/// <para>
/// The golden file was captured at the pre-fix commit, BEFORE the Bench existed, by running
/// this same fixture through the then-current <see cref="WorkingSetService"/>. Regenerating it
/// from post-change code would launder a regression into the baseline, so the file is checked
/// in and the test only ever READS it. To re-capture deliberately, set
/// <c>TWIG_CAPTURE_BASELINE=1</c> — and only ever do that at a commit where the behaviour is
/// known good.
/// </para>
/// </summary>
public sealed class DefaultBenchParityTests
{
    private const string GoldenFileName = "working-set-baseline.txt";

    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();

    public DefaultBenchParityTests()
    {
        var f = typeof(WorkingSetBaselineFixture);
        _ = f; // fixture is static; referenced for clarity

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.ActiveId);

        _workItemRepo.GetParentChainAsync(WorkingSetBaselineFixture.ActiveId, Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.ParentChain);
        _workItemRepo.GetChildrenAsync(WorkingSetBaselineFixture.ActiveId, Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.Children);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.IterationItems);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.Seeds);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.DirtyItems);
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.PendingIds);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.CurrentIteration);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.TrackedItems);
    }

    private WorkingSetService CreateSut() => new(
        _contextStore, _workItemRepo, _pendingStore, _iterationService,
        WorkingSetBaselineFixture.UserDisplayName, _trackingRepo);

    // ═══════════════════════════════════════════════════════════════
    //  The parity bar
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputedView_MatchesCapturedBaseline_ExactlyAndInOrder()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        var actual = Render(ws);
        var goldenPath = ResolveGoldenPath();

        if (Environment.GetEnvironmentVariable("TWIG_CAPTURE_BASELINE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
        }

        File.Exists(goldenPath).ShouldBeTrue(
            $"The parity baseline is missing at {goldenPath}. It must be captured at a known-good " +
            "commit, never regenerated from the code under test.");

        var expected = File.ReadAllText(goldenPath);

        Normalise(actual).ShouldBe(Normalise(expected),
            "The computed view diverged from the captured baseline. With one Bench and no user " +
            "action, twig must behave exactly as it did before.");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Fixture preconditions — a fixture that degrades into the happy
    //  path proves nothing, so each discriminating property is asserted.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Precondition_TheAssigneeFilterActuallyRemovesSomething()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        // 502 is assigned to another user and 503 to nobody. If either appears, the assignee
        // filter has been dropped and the parity test above would be asserting the wrong shape.
        ws.SprintItemIds.ShouldNotContain(502);
        ws.SprintItemIds.ShouldNotContain(503);
        ws.SprintItemIds.Count.ShouldBeLessThan(WorkingSetBaselineFixture.IterationItems.Count);
    }

    [Fact]
    public async Task Precondition_TheAssigneeFilterIsCaseInsensitive()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        // 504 is assigned with different casing. Dropping the case-insensitive comparison would
        // silently shrink the view.
        ws.SprintItemIds.ShouldContain(504);
    }

    [Fact]
    public async Task Precondition_APinnedItemOverlapsASprintItem()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        // 505 is both a sprint item and a hand pin. That is what makes the union non-trivial.
        ws.SprintItemIds.ShouldContain(505);
        ws.TrackedItemIds.ShouldContain(505);
        ws.AllIds.Count(id => id == 505).ShouldBe(1);
    }

    [Fact]
    public async Task Precondition_AStagedEditSitsOutsideTheIteration()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        // 701 is dirty and in a PAST iteration — nothing in the sprint rule selects it.
        ws.SprintItemIds.ShouldNotContain(701);
        ws.DirtyItemIds.ShouldContain(701);
    }

    [Fact]
    public async Task Precondition_ASeedIsPresent()
    {
        var ws = await CreateSut().ComputeAsync([WorkingSetBaselineFixture.CurrentIteration]);

        // A seed has never been pushed, so no server-side query could return it.
        ws.SeedIds.ShouldNotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Rendering — a stable, diffable text form of the read model
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders the read model in a form where BOTH membership and order are visible, so a
    /// reordering shows up as a diff rather than passing silently.
    /// </summary>
    private static string Render(WorkingSet ws)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# twig working-set baseline");
        sb.AppendLine("# Captured before the Bench existed. Read-only in tests.");
        sb.AppendLine();
        sb.AppendLine($"ActiveItemId: {Format(ws.ActiveItemId)}");
        sb.AppendLine($"ParentChainIds: {Join(ws.ParentChainIds)}");
        sb.AppendLine($"ChildrenIds: {Join(ws.ChildrenIds)}");
        sb.AppendLine($"SprintItemIds: {Join(ws.SprintItemIds)}");
        sb.AppendLine($"SeedIds: {Join(ws.SeedIds)}");
        // DirtyItemIds is a set with no defined order — sorted so the file is stable.
        sb.AppendLine($"DirtyItemIds: {Join(ws.DirtyItemIds.OrderBy(i => i))}");
        sb.AppendLine($"TrackedItemIds: {Join(ws.TrackedItemIds)}");
        sb.AppendLine($"IterationPaths: {string.Join(", ", ws.IterationPaths.Select(p => p.Value))}");
        sb.AppendLine($"AllIds: {Join(ws.AllIds.OrderBy(i => i))}");
        return sb.ToString();
    }

    private static string Format(int? value) => value?.ToString() ?? "(none)";

    private static string Join(IEnumerable<int> ids)
    {
        var list = ids.ToList();
        return list.Count == 0 ? "(empty)" : string.Join(", ", list);
    }

    private static string Normalise(string text)
        => text.Replace("\r\n", "\n").TrimEnd();

    private static string ResolveGoldenPath()
    {
        // The golden file is checked in beside the test sources, not copied to the output
        // directory — a build-output copy could go stale relative to the repo.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Baselines", GoldenFileName);
            if (File.Exists(candidate))
                return candidate;

            var srcCandidate = Path.Combine(dir, "Services", "Workspace", "Baselines", GoldenFileName);
            if (File.Exists(srcCandidate))
                return srcCandidate;

            dir = Path.GetDirectoryName(dir);
        }

        // Fall back to the canonical location so a first capture writes somewhere sensible.
        return Path.Combine(RepoRelativeBaselineDir(), GoldenFileName);
    }

    private static string RepoRelativeBaselineDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return Path.Combine(dir, "tests", "Twig.Domain.Tests", "Services", "Workspace", "Baselines");
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not locate the repository root to resolve the parity baseline.");
    }
}
