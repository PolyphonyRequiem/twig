using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Sync;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Computes the working set — the view a person sees — by evaluating a Bench against local cache
/// state (docs/specs/bench.spec.md §3, ADO #144).
/// <para>
/// 🔴 This service was already a Bench in all but name: one hard-coded question, plus hand pins,
/// assembled per access with nowhere to persist the hand edits. It is PROMOTED, not replaced.
/// The read model it returns keeps its shape, so call sites are not rewritten, and the acceptance
/// bar is that with one Bench and no user action twig behaves exactly as it did before — same
/// items, same order, same output, checked against a captured baseline.
/// </para>
/// <para>
/// 🔴 The hard-coded sprint question is now the default Bench's query selector — the first row of
/// the selector mechanism, not a special case beside it. There is no branch here that asks "is
/// this the default Bench?"; the default is evaluated by exactly the same code path as any other.
/// </para>
/// <para>
/// The seeds-and-dirty half of the read model is deliberately NOT selector-derived. Those are an
/// invariant on evaluation rather than rules a person could remove — a display preference must
/// not be able to conceal work twig owes ADO. Ticket #147 makes that guard explicit; this ticket
/// preserves today's behaviour, which already surfaces them unconditionally.
/// </para>
/// </summary>
public sealed class WorkingSetService
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingStore;
    private readonly IIterationService _iterationService;
    private readonly ITrackingRepository? _trackingRepo;
    private readonly string? _userDisplayName;
    private readonly IBenchRepository? _benchRepo;
    private readonly BenchEvaluator? _benchEvaluator;

    /// <summary>
    /// The signature that shipped before the Bench existed. Kept working unchanged: it is declared
    /// public API and existing call sites must not be rewritten. With no Bench wired up, the same
    /// default selectors are evaluated through the same evaluator, so this is the identical code
    /// path with an unsaved Bench rather than a second implementation that could drift.
    /// </summary>
    public WorkingSetService(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingStore,
        IIterationService iterationService,
        string? userDisplayName,
        ITrackingRepository? trackingRepo = null,
        IBenchRepository? benchRepo = null,
        BenchEvaluator? benchEvaluator = null)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _pendingStore = pendingStore;
        _iterationService = iterationService;
        _userDisplayName = userDisplayName;
        _trackingRepo = trackingRepo;
        _benchRepo = benchRepo;
        _benchEvaluator = benchEvaluator;
    }

    /// <summary>
    /// Computes the current working set by evaluating the default Bench against the local cache.
    /// When <paramref name="iterationPaths"/> is provided the sprint rule resolves to it directly;
    /// otherwise the rule is answered from local state.
    /// </summary>
    public async Task<WorkingSet> ComputeAsync(
        IReadOnlyList<IterationPath>? iterationPaths = null, CancellationToken ct = default)
    {
        // 1. Read active ID from context
        var activeId = await _contextStore.GetActiveWorkItemIdAsync(ct);

        // 2. Query parent chain (empty list when no active item or item not in cache)
        var parentChain = activeId.HasValue
            ? await _workItemRepo.GetParentChainAsync(activeId.Value, ct)
            : [];

        // 3. Query children of the active item
        var children = activeId.HasValue
            ? await _workItemRepo.GetChildrenAsync(activeId.Value, ct)
            : [];

        // 4. Resolve iterations, then evaluate the Bench.
        var iterations = iterationPaths
            ?? [await _iterationService.GetCurrentIterationAsync(ct)];

        var (sprintItemIds, trackedItemIds, resolvedIterations) =
            await EvaluateBenchAsync(iterations, ct);

        // 5. Query seeds. An invariant, not a selector: ADO has never heard of a seed, so no rule
        //    on any Bench could match one, and hiding one would hide unpublished work.
        var seeds = await _workItemRepo.GetSeedsAsync(ct);

        // 6. Query dirty IDs via SyncGuard. Same reasoning: what twig OWES ADO is not a display
        //    preference and must not be concealable by a view setting.
        var dirtyIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingStore, ct);

        return new WorkingSet
        {
            ActiveItemId = activeId,
            ParentChainIds = parentChain.Select(w => w.Id).ToList(),
            ChildrenIds = children.Select(w => w.Id).ToList(),
            SprintItemIds = sprintItemIds,
            SeedIds = seeds.Select(w => w.Id).ToList(),
            DirtyItemIds = dirtyIds,
            TrackedItemIds = trackedItemIds,
            IterationPaths = resolvedIterations,
        };
    }

    /// <summary>
    /// Evaluates the default Bench and projects its membership onto the read model's categories.
    /// <para>
    /// The projection is by SELECTOR KIND, and that is a presentation split rather than two
    /// memberships: the view renders a hand pin differently from a query result, so collapsing
    /// them into one flat list would change what the person sees and fail the parity bar.
    /// Membership itself stays the order-free union — an item matched by a query selector and a
    /// pin appears in both categories and exactly once in <c>AllIds</c>.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<int> SprintItemIds, IReadOnlyList<int> TrackedItemIds, IReadOnlyList<IterationPath> Iterations)>
        EvaluateBenchAsync(IReadOnlyList<IterationPath> iterations, CancellationToken ct)
    {
        if (_benchRepo is null || _benchEvaluator is null)
        {
            // No Bench wired up (a caller constructing this service with primitives only). Fall
            // back to the same selectors the default Bench would hold, evaluated by the same
            // evaluator — so this is the identical code path with an unsaved Bench, NOT a second
            // implementation of the question that could drift from the first.
            var transient = new Bench
            {
                Name = Bench.DefaultName,
                IsDefault = true,
                Selectors = await DefaultSelectorsAsync(ct),
            };

            var evaluator = _benchEvaluator ?? new BenchEvaluator(_workItemRepo, new NullIterationCalendar());
            var transientMembership = await evaluator.EvaluateAsync(transient, iterations, ct);
            return Project(transientMembership, iterations);
        }

        var bench = await _benchRepo.GetOrCreateDefaultAsync(await DefaultSelectorsAsync(ct), ct);
        var membership = await _benchEvaluator.EvaluateAsync(bench, iterations, ct);
        return Project(membership, iterations);
    }

    private (IReadOnlyList<int>, IReadOnlyList<int>, IReadOnlyList<IterationPath>) Project(
        BenchMembership membership, IReadOnlyList<IterationPath> requestedIterations)
        => (
            membership.QueryMatches.Select(w => w.Id).ToList(),
            membership.PinnedIds,
            membership.IterationPaths.Count > 0 ? membership.IterationPaths : requestedIterations);

    /// <summary>
    /// The selectors the default Bench is created with, composed by
    /// <see cref="DefaultBenchSelectors"/> — the single answer shared with the pin workflow, so
    /// the read path and the write path cannot disagree about what a fresh default Bench holds.
    /// </summary>
    private Task<IReadOnlyCollection<BenchSelector>> DefaultSelectorsAsync(CancellationToken ct)
        => new DefaultBenchSelectors(_trackingRepo, _userDisplayName).BuildAsync(ct);

    /// <summary>
    /// Used only when a caller supplies iterations directly and no calendar is wired up, so the
    /// sprint rule never needs to answer "which iteration covers now?".
    /// </summary>
    private sealed class NullIterationCalendar : IIterationCalendar
    {
        public Task<IReadOnlyList<IterationPath>> GetCurrentIterationsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationPath>>([]);

        public Task SaveAsync(IReadOnlyList<TeamIteration> iterations, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
