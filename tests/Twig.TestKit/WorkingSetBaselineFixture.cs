using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.TestKit;

/// <summary>
/// The deterministic scenario the default-Bench parity baseline is captured from (spec test 1).
/// <para>
/// Every category the read model carries is populated, and each one is populated with something
/// a naive reimplementation gets wrong: sprint items that must be filtered by assignee, an item
/// in the iteration assigned to somebody else, a tracked item that is ALSO a sprint item (the
/// overlap case), a tracked subtree, a seed, and a dirty item outside the iteration.
/// </para>
/// <para>
/// 🔴 A fixture that degrades into the happy path hollows the suite out, so the parity test
/// asserts the discriminating preconditions explicitly rather than trusting this comment.
/// </para>
/// </summary>
public static class WorkingSetBaselineFixture
{
    public const string UserDisplayName = "Ada Lovelace";
    public const string OtherUser = "Grace Hopper";

    public const string CurrentIterationPath = @"Project\Sprint 7";
    public const string PastIterationPath = @"Project\Sprint 6";

    public static IterationPath CurrentIteration => IterationPath.Parse(CurrentIterationPath).Value;
    public static IterationPath PastIteration => IterationPath.Parse(PastIterationPath).Value;

    /// <summary>The active item — has both a parent chain and children.</summary>
    public const int ActiveId = 500;

    /// <summary>Items returned for the current iteration, BEFORE the assignee filter.</summary>
    public static IReadOnlyList<WorkItem> IterationItems =>
    [
        // Assigned to the user — survives the filter.
        new WorkItemBuilder(501, "Sprint item, mine")
            .AsUserStory().InState("Active").AssignedTo(UserDisplayName)
            .WithIterationPath(CurrentIterationPath).Build(),

        // Assigned to somebody else — MUST be filtered out. If the filter is dropped this
        // item appears and the baseline diverges.
        new WorkItemBuilder(502, "Sprint item, not mine")
            .AsUserStory().InState("Active").AssignedTo(OtherUser)
            .WithIterationPath(CurrentIterationPath).Build(),

        // Unassigned — also filtered out, because the filter is an equality match on the name.
        new WorkItemBuilder(503, "Sprint item, unassigned")
            .AsTask().InState("New")
            .WithIterationPath(CurrentIterationPath).Build(),

        // Assigned with different casing — the filter is case-insensitive, so this SURVIVES.
        new WorkItemBuilder(504, "Sprint item, mine in lower case")
            .AsTask().InState("Active").AssignedTo(UserDisplayName.ToLowerInvariant())
            .WithIterationPath(CurrentIterationPath).Build(),

        // Also tracked by hand — the overlap case (spec test 8). Must appear exactly once.
        new WorkItemBuilder(505, "Sprint item that is also pinned")
            .AsBug().InState("Active").AssignedTo(UserDisplayName)
            .WithIterationPath(CurrentIterationPath).Build(),
    ];

    public static IReadOnlyList<WorkItem> ParentChain =>
    [
        new WorkItemBuilder(100, "Epic above the active item").AsEpic().InState("Active").Build(),
        new WorkItemBuilder(200, "Feature above the active item").AsFeature().InState("Active").WithParent(100).Build(),
    ];

    public static IReadOnlyList<WorkItem> Children =>
    [
        new WorkItemBuilder(601, "Child of active").AsTask().InState("New").WithParent(ActiveId).Build(),
        new WorkItemBuilder(602, "Second child of active").AsTask().InState("Active").WithParent(ActiveId).Build(),
    ];

    /// <summary>Seeds — never pushed, so no server-side query can ever return them.</summary>
    public static IReadOnlyList<WorkItem> Seeds =>
    [
        new WorkItemBuilder(-1, "Seed, drafted locally").AsUserStory().AsSeed().Build(),
    ];

    /// <summary>Dirty items: staged edits. #701 is outside the current iteration on purpose.</summary>
    public static IReadOnlyList<WorkItem> DirtyItems =>
    [
        new WorkItemBuilder(701, "Staged edit, outside the sprint")
            .AsBug().InState("Active").AssignedTo(UserDisplayName)
            .WithIterationPath(PastIterationPath).Dirty().Build(),
    ];

    /// <summary>Ids the pending store reports as having unpushed changes.</summary>
    public static IReadOnlyList<int> PendingIds => [702];

    /// <summary>
    /// Hand pins. #505 overlaps a sprint item; #800 is a subtree pin; #801 is a plain pin
    /// on an item no other rule selects.
    /// </summary>
    public static IReadOnlyList<TrackedItem> TrackedItems =>
    [
        new TrackedItem(505, TrackingMode.Single, FixedInstant(0)),
        new TrackedItem(800, TrackingMode.Tree, FixedInstant(1)),
        new TrackedItem(801, TrackingMode.Single, FixedInstant(2)),
    ];

    /// <summary>Descendants of the #800 subtree pin, one of them added "after" the pin.</summary>
    public static IReadOnlyList<WorkItem> SubtreeUnder800 =>
    [
        new WorkItemBuilder(810, "Child of the pinned subtree").AsTask().WithParent(800).Build(),
    ];

    /// <summary>A stable clock so the golden file never moves on its own.</summary>
    public static DateTimeOffset FixedInstant(int offsetMinutes)
        => new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero).AddMinutes(offsetMinutes);
}
