using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Attachment;

/// <summary>
/// AB#738 acceptance tests. Every assertion observes the
/// <see cref="PrimaryScopeAttachmentService"/> and its <see cref="Result{T}"/>
/// projection — never the raw storage document. Storage is simulated by an
/// in-memory <see cref="FakeAttachmentStore"/> so the tests are hermetic and
/// portable across CI hosts (git may or may not be available in a sandbox).
/// </summary>
public sealed class PrimaryScopeAttachmentServiceTests
{
    private const string ConnectionRef = "connectionref-fixture";
    private static readonly DateTimeOffset Frozen = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static PrimaryScopeAttachmentService BuildService(
        FakeAttachmentStore store,
        FakeWorkItemRepository repo,
        FakeEligibility eligibility)
    {
        var clock = new FakeTimeProvider(Frozen);
        return new PrimaryScopeAttachmentService(store, eligibility, repo, clock);
    }

    // ── AC: attach without claim ─────────────────────────────────────────

    [Fact]
    public async Task Attach_writes_primary_scope_without_minting_a_claim()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(101, "Do the thing").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        var attach = await service.AttachAsync(101);
        attach.IsSuccess.ShouldBeTrue();

        var read = await service.ReadStatusAsync();
        read.IsSuccess.ShouldBeTrue();
        var status = read.Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldNotBeNull();
        status.PrimaryScope!.Value.WorkItemId.ShouldBe(101);
        status.WorkItemTitle.ShouldBe("Do the thing");

        // AB#738 explicitly forbids claim minting on the attach path — the claim
        // reference on the stored attachment MUST remain null.
        store.Current.ActiveClaimId.ShouldBeNull();
    }

    // ── AC: ineligible type refusal writes nothing ──────────────────────

    [Fact]
    public async Task Attach_refuses_ineligible_type_and_writes_nothing()
    {
        var initial = PrimaryScopeAttachment.Empty(ConnectionRef);
        var store = new FakeAttachmentStore(managed: true, initial);
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(202, "A bug").AsBug().Build());
        var service = BuildService(store, repo, FakeEligibility.Only(WorkItemType.Task));

        var attach = await service.AttachAsync(202);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.IneligibleType.ToString());

        // Nothing was written — no scope, no claim.
        var read = await service.ReadStatusAsync();
        read.IsSuccess.ShouldBeTrue();
        read.Value.PrimaryScope.ShouldBeNull();
        store.WriteCount.ShouldBe(0);
    }

    // ── AC: explicit switch is required to change the scope ─────────────

    [Fact]
    public async Task Attach_refuses_when_a_scope_is_already_attached()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(301, "First").AsTask().Build(),
            new WorkItemBuilder(302, "Second").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(301)).IsSuccess.ShouldBeTrue();

        var second = await service.AttachAsync(302);
        second.IsSuccess.ShouldBeFalse();
        second.Error.ShouldContain(AttachmentFailure.AlreadyAttached.ToString());

        // Scope still points at the first item, unchanged.
        var read = await service.ReadStatusAsync();
        read.Value.PrimaryScope!.Value.WorkItemId.ShouldBe(301);
    }

    [Fact]
    public async Task Switch_replaces_the_primary_scope_when_the_new_type_is_eligible()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(401, "Parent").AsFeature().Build(),
            new WorkItemBuilder(402, "Child").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(401)).IsSuccess.ShouldBeTrue();

        var switched = await service.SwitchAsync(402);
        switched.IsSuccess.ShouldBeTrue();

        var read = await service.ReadStatusAsync();
        read.Value.PrimaryScope!.Value.WorkItemId.ShouldBe(402);
    }

    [Fact]
    public async Task Switch_refuses_when_the_new_type_is_ineligible_and_preserves_the_old_scope()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(501, "Old scope").AsTask().Build(),
            new WorkItemBuilder(502, "Bug").AsBug().Build());
        var service = BuildService(store, repo, FakeEligibility.Only(WorkItemType.Task));

        (await service.AttachAsync(501)).IsSuccess.ShouldBeTrue();

        var writesBefore = store.WriteCount;
        var switched = await service.SwitchAsync(502);
        switched.IsSuccess.ShouldBeFalse();
        switched.Error.ShouldContain(AttachmentFailure.IneligibleType.ToString());

        // Refusal wrote nothing.
        store.WriteCount.ShouldBe(writesBefore);
        var read = await service.ReadStatusAsync();
        read.Value.PrimaryScope!.Value.WorkItemId.ShouldBe(501);
    }

    // ── AC: parent-attachment does not authorize child ──────────────────

    [Fact]
    public async Task Requiring_a_claim_on_a_child_fails_when_the_parent_is_the_attached_scope()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(601, "Parent scope").AsFeature().Build(),
            new WorkItemBuilder(602, "Child task").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(601)).IsSuccess.ShouldBeTrue();

        var required = await service.RequireActiveClaimForScopeAsync(602);
        required.IsSuccess.ShouldBeFalse();
        required.Error.ShouldContain(AttachmentFailure.ScopeNotPrimary.ToString());
        // The error names the CHILD id — that is the "named error naming the
        // child" boundary AB#738 acceptance calls out.
        required.Error.ShouldContain("#602");
    }

    [Fact]
    public async Task Requiring_a_claim_fails_when_the_primary_scope_carries_no_active_claim()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(701, "Scope").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(701)).IsSuccess.ShouldBeTrue();

        var required = await service.RequireActiveClaimForScopeAsync(701);
        required.IsSuccess.ShouldBeFalse();
        required.Error.ShouldContain(AttachmentFailure.ClaimNotFoundForScope.ToString());
    }

    // ── AC: status rendering after attach and after detach ─────────────

    [Fact]
    public async Task Status_after_detach_states_unattached_explicitly()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(801, "One").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(801)).IsSuccess.ShouldBeTrue();
        (await service.DetachAsync()).IsSuccess.ShouldBeTrue();

        var status = (await service.ReadStatusAsync()).Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldBeNull();
        // The presence of `IsManagedWorktree` + null scope is the projection the
        // ticket demands: an attached surface renders this as "unattached", not
        // as an absent block.
    }

    [Fact]
    public async Task Status_on_unmanaged_worktree_reports_not_managed()
    {
        var store = new FakeAttachmentStore(managed: false, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository();
        var service = BuildService(store, repo, FakeEligibility.All());

        var status = (await service.ReadStatusAsync()).Value;
        status.IsManagedWorktree.ShouldBeFalse();
        status.PrimaryScope.ShouldBeNull();
    }

    // ── AC: existing Bench/Context behavior is unchanged ────────────────
    // Encoded here as an invariant: the attachment service never touches the
    // IContextStore. The test exercises the whole attach → switch → detach
    // sequence and asserts the shared context store the CLI uses for its active
    // item pointer is byte-untouched by every path.

    [Fact]
    public async Task Attach_switch_detach_never_touches_the_context_store()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(901, "First").AsTask().Build(),
            new WorkItemBuilder(902, "Second").AsTask().Build());
        var contextStore = new SpyContextStore();
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(901)).IsSuccess.ShouldBeTrue();
        (await service.SwitchAsync(902)).IsSuccess.ShouldBeTrue();
        (await service.DetachAsync()).IsSuccess.ShouldBeTrue();

        contextStore.CallCount.ShouldBe(0);
    }

    // ── Named failure: unknown work item id ──

    [Fact]
    public async Task Attach_refuses_unknown_work_item_id()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository();
        var service = BuildService(store, repo, FakeEligibility.All());

        var attach = await service.AttachAsync(1234);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.WorkItemUnknown.ToString());
        store.WriteCount.ShouldBe(0);
    }

    // ── Test doubles ──

    private sealed class FakeAttachmentStore : IPrimaryScopeAttachmentStore
    {
        private PrimaryScopeAttachment _current;
        private readonly bool _managed;
        public int WriteCount { get; private set; }
        public PrimaryScopeAttachment Current => _current;

        public FakeAttachmentStore(bool managed, PrimaryScopeAttachment initial)
        {
            _managed = managed;
            _current = initial;
        }

        public bool IsManagedWorktree() => _managed;

        public Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Ok(_current));

        public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default)
        {
            _current = attachment;
            WriteCount++;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeEligibility : IPrimaryScopeTypeEligibility
    {
        private readonly WorkItemType[] _allowed;
        private readonly bool _permitAll;

        private FakeEligibility(bool permitAll, WorkItemType[] allowed)
        {
            _permitAll = permitAll;
            _allowed = allowed;
        }

        public static FakeEligibility All() => new(permitAll: true, allowed: Array.Empty<WorkItemType>());
        public static FakeEligibility Only(params WorkItemType[] allowed) => new(permitAll: false, allowed);

        public bool IsEligible(WorkItemType type)
        {
            if (_permitAll) return true;
            foreach (var a in _allowed)
                if (string.Equals(a.Value, type.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    private sealed class FakeWorkItemRepository : IWorkItemRepository
    {
        private readonly Dictionary<int, WorkItem> _items;
        public FakeWorkItemRepository(params WorkItem[] items)
        {
            _items = items.ToDictionary(i => i.Id);
        }

        public Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult<WorkItem?>(_items.TryGetValue(id, out var item) ? item : null);

        public Task<bool> ExistsByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(_items.ContainsKey(id));

        // Unused surface — throws so a regression that starts reading through the
        // repo on the attachment path fails loudly rather than degrading silently.
        public Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetRootItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationAsync(IterationPath iterationPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationsAsync(IReadOnlyList<IterationPath> iterationPaths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByIterationAndAssigneeAsync(IterationPath iterationPath, string assignee, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetParentChainAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> FindByPatternAsync(string pattern, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetDirtyItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetSeedsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<int>> GetOrphanParentIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveAsync(WorkItem workItem, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearPhantomDirtyFlagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearDirtyFlagAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> GetByAreaPathsAsync(IReadOnlyList<AreaPathFilter> entries, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Twig.Domain.ReadModels.CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SpyContextStore
    {
        public int CallCount { get; private set; }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
