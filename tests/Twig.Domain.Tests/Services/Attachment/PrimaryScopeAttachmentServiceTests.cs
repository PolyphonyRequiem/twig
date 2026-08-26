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
/// The system-store registry seam (§9.4) is fed by a <see cref="FakeSystemRegistry"/>
/// preloaded with a matching worktree row so the T1 initialization contract
/// passes; §9.5 refusals are exercised as their own cases below.
/// </summary>
public sealed class PrimaryScopeAttachmentServiceTests
{
    private const string ConnectionRef = "connectionref-fixture";
    private const string WorktreeFingerprint = "{\"gitCommonDir\":\"/wt/.git\",\"worktreeGitDir\":\"/wt/.git\",\"worktreeRoot\":\"/wt\"}";
    private const string WorktreeRoot = "/wt";
    private static readonly DateTimeOffset Frozen = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static PrimaryScopeAttachmentService BuildService(
        FakeAttachmentStore store,
        FakeWorkItemRepository repo,
        FakeEligibility eligibility,
        FakeSystemRegistry? registry = null)
    {
        var clock = new FakeTimeProvider(Frozen);
        registry ??= FakeSystemRegistry.WithRegisteredWorktree(WorktreeFingerprint, ConnectionRef);
        var fingerprint = new FakeFingerprintProvider(new WorktreeFingerprintContext(WorktreeFingerprint, ConnectionRef, WorktreeRoot));
        var urlBuilder = new FakeUrlBuilder("fixture-org", "fixture-project");
        return new PrimaryScopeAttachmentService(store, eligibility, repo, registry, fingerprint, urlBuilder, clock);
    }

    // ── AC: attach without claim ─────────────────────────────────────────

    [Fact]
    public async Task Attach_writes_primary_scope_without_minting_a_claim()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(101, "Do the thing").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        var attach = await service.AttachAsync(101);
        attach.IsSuccess.ShouldBeTrue(attach.Error);

        var read = await service.ReadStatusAsync();
        read.IsSuccess.ShouldBeTrue();
        var status = read.Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldNotBeNull();
        status.PrimaryScope!.Value.WorkItemId.ShouldBe(101);
        status.WorkItemTitle.ShouldBe("Do the thing");

        // AB#738 explicitly forbids claim minting on the attach path — the claim
        // reference on the stored attachment MUST remain null.
        store.Current.ActiveClaim.ShouldBeNull();
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

    // ── AC: fail-closed eligibility ─────────────────────────────────────

    [Fact]
    public async Task Attach_refuses_when_profile_eligibility_is_unavailable()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(203, "Any type").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.Unavailable());

        var attach = await service.AttachAsync(203);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.EligibilityUnavailable.ToString());
        // Silent permit-all was the defect: nothing may be written.
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

    // ── AC: claim linkage preserved across scope-only writes ────────────

    [Fact]
    public async Task Switch_preserves_full_active_claim_including_mint_timestamp()
    {
        var mintedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var attachmentWithClaim = new PrimaryScopeAttachment(
            ConnectionRef,
            PrimaryScope: new PrimaryScope(701, "https://dev.azure.com/o/p/_workitems/edit/701", Frozen.AddDays(-3)),
            ActiveClaim: new ActiveClaimReference("claim-01H...", mintedAt));
        var store = new FakeAttachmentStore(managed: true, attachmentWithClaim);
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(701, "Old").AsTask().Build(),
            new WorkItemBuilder(702, "New").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        var switched = await service.SwitchAsync(702);
        switched.IsSuccess.ShouldBeTrue(switched.Error);

        // The stored ActiveClaim block MUST survive byte-identical across a
        // scope-only write — claim id AND its original mint timestamp.
        store.Current.ActiveClaim.ShouldNotBeNull();
        store.Current.ActiveClaim!.Value.ClaimId.ShouldBe("claim-01H...");
        store.Current.ActiveClaim!.Value.MintedAt.ShouldBe(mintedAt);
    }

    [Fact]
    public async Task Detach_preserves_active_claim_reference()
    {
        var mintedAt = new DateTimeOffset(2025, 8, 9, 10, 11, 12, TimeSpan.Zero);
        var initial = new PrimaryScopeAttachment(
            ConnectionRef,
            PrimaryScope: new PrimaryScope(801, "https://dev.azure.com/o/p/_workitems/edit/801", Frozen.AddDays(-1)),
            ActiveClaim: new ActiveClaimReference("claim-XYZ", mintedAt));
        var store = new FakeAttachmentStore(managed: true, initial);
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(801, "S").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.DetachAsync()).IsSuccess.ShouldBeTrue();

        store.Current.PrimaryScope.ShouldBeNull();
        store.Current.ActiveClaim.ShouldNotBeNull();
        store.Current.ActiveClaim!.Value.ClaimId.ShouldBe("claim-XYZ");
        store.Current.ActiveClaim!.Value.MintedAt.ShouldBe(mintedAt);
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

    // ── AC: system-store registration is enforced (§9.5 step 5) ─────────

    [Fact]
    public async Task Attach_refuses_when_the_worktree_is_not_registered_in_the_system_store()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(901, "A").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All(), FakeSystemRegistry.Empty());

        var attach = await service.AttachAsync(901);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.WorktreeNotRegistered.ToString());
        store.WriteCount.ShouldBe(0);
    }

    [Fact]
    public async Task Attach_refuses_when_the_worktree_is_retired()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(902, "A").AsTask().Build());
        var registry = FakeSystemRegistry.WithRetiredWorktree(WorktreeFingerprint, ConnectionRef, Frozen.AddDays(-1));
        var service = BuildService(store, repo, FakeEligibility.All(), registry);

        var attach = await service.AttachAsync(902);
        attach.IsSuccess.ShouldBeFalse();
        attach.Error.ShouldContain(AttachmentFailure.WorktreeRetired.ToString());
        store.WriteCount.ShouldBe(0);
    }

    [Fact]
    public async Task Successful_attach_does_not_reupsert_the_worktree_row()
    {
        // Registration is a managed-init concern; a successful attach MUST NOT
        // reach around it to bootstrap a row. Silent post-attach bootstrap was
        // exactly the review-blocker this refactor removes.
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(903, "A").AsTask().Build());
        var registry = FakeSystemRegistry.WithRegisteredWorktree(WorktreeFingerprint, ConnectionRef);
        var service = BuildService(store, repo, FakeEligibility.All(), registry);

        (await service.AttachAsync(903)).IsSuccess.ShouldBeTrue();

        registry.UpsertedFingerprints.ShouldBeEmpty();
    }

    // ── AC: registry gate is enforced on reads too ─────────────────────

    [Fact]
    public async Task ReadStatus_surfaces_worktree_not_registered_when_registry_is_empty()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository();
        var service = BuildService(store, repo, FakeEligibility.All(), FakeSystemRegistry.Empty());

        var status = (await service.ReadStatusAsync()).Value;
        status.FailureCode.ShouldNotBeNull();
        status.FailureCode.ShouldContain(AttachmentStorageFailure.WorktreeNotRegistered);
    }

    [Fact]
    public async Task RequireActiveClaim_refuses_when_worktree_is_not_registered()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(905, "A").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All(), FakeSystemRegistry.Empty());

        var required = await service.RequireActiveClaimForScopeAsync(905);
        required.IsSuccess.ShouldBeFalse();
        required.Error.ShouldContain(AttachmentFailure.WorktreeNotRegistered.ToString());
    }

    // ── AC: origin-bearing work item URL ───────────────────────────────

    [Fact]
    public async Task Attach_writes_origin_bearing_work_item_url()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(904, "Scoped").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(904)).IsSuccess.ShouldBeTrue();

        // Not the opaque workitem:<id> shape — must include organization and project.
        var url = store.Current.PrimaryScope!.Value.WorkItemUrl;
        url.ShouldContain("fixture-org");
        url.ShouldContain("fixture-project");
        url.ShouldContain("904");
    }

    // ── AC: status rendering after attach and after detach ─────────────

    [Fact]
    public async Task Status_after_detach_states_unattached_explicitly()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(new WorkItemBuilder(1801, "One").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(1801)).IsSuccess.ShouldBeTrue();
        (await service.DetachAsync()).IsSuccess.ShouldBeTrue();

        var status = (await service.ReadStatusAsync()).Value;
        status.IsManagedWorktree.ShouldBeTrue();
        status.PrimaryScope.ShouldBeNull();
        status.FailureCode.ShouldBeNull();
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

    [Fact]
    public async Task Status_surfaces_named_storage_failure_on_the_public_projection()
    {
        // A store that reads with a §8 identifier surfaces through the public
        // adapter as a failure code — silent degradation to "unmanaged" is the
        // defect the projection now refuses.
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef))
        {
            NextReadFailure = AttachmentStorageFailure.WorktreeFingerprintDrift,
        };
        var repo = new FakeWorkItemRepository();
        var service = BuildService(store, repo, FakeEligibility.All());

        var adapter = new AttachmentStatusProjectionAdapter(service);
        var proj = await adapter.ReadAsync();
        proj.FailureCode.ShouldBe(AttachmentStorageFailure.WorktreeFingerprintDrift);
    }

    [Fact]
    public async Task Status_projection_rethrows_cancellation()
    {
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef))
        {
            ThrowOnRead = new OperationCanceledException(),
        };
        var repo = new FakeWorkItemRepository();
        var service = BuildService(store, repo, FakeEligibility.All());
        var adapter = new AttachmentStatusProjectionAdapter(service);

        await Should.ThrowAsync<OperationCanceledException>(() => adapter.ReadAsync());
    }

    // ── AC: context mutation truly does not touch the primary scope ─────

    [Fact]
    public async Task Setting_active_context_leaves_the_primary_scope_byte_identical()
    {
        // The old test observed a spy that was never wired into any code path
        // so it was guaranteed to pass. This drives the real IContextStore
        // mutation across the same worktree and asserts the attachment record
        // remains byte-identical — the invariant the ticket demands.
        var store = new FakeAttachmentStore(managed: true, PrimaryScopeAttachment.Empty(ConnectionRef));
        var repo = new FakeWorkItemRepository(
            new WorkItemBuilder(9001, "Scope").AsTask().Build(),
            new WorkItemBuilder(9002, "Other").AsTask().Build());
        var service = BuildService(store, repo, FakeEligibility.All());

        (await service.AttachAsync(9001)).IsSuccess.ShouldBeTrue();
        var afterAttach = store.Current;

        // Drive the shared context store the CLI uses for its active-item
        // pointer. The attachment record must remain byte-identical: neither
        // scope, connection, nor claim reference may move.
        var contextStore = new InMemoryContextStore();
        await contextStore.SetActiveWorkItemIdAsync(9002);
        (await contextStore.GetActiveWorkItemIdAsync()).ShouldBe(9002);

        store.Current.ShouldBe(afterAttach);
        // And a second context mutation still leaves the record untouched.
        await contextStore.ClearActiveWorkItemIdAsync();
        (await contextStore.GetActiveWorkItemIdAsync()).ShouldBeNull();
        store.Current.ShouldBe(afterAttach);
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
        public string? NextReadFailure { get; set; }
        public Exception? ThrowOnRead { get; set; }

        public FakeAttachmentStore(bool managed, PrimaryScopeAttachment initial)
        {
            _managed = managed;
            _current = initial;
        }

        public bool IsManagedWorktree() => _managed;

        public Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default)
        {
            if (ThrowOnRead is not null)
                throw ThrowOnRead;
            if (NextReadFailure is not null)
                return Task.FromResult(Result.Fail<PrimaryScopeAttachment>(NextReadFailure));
            return Task.FromResult(Result.Ok(_current));
        }

        public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default)
        {
            _current = attachment;
            WriteCount++;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> LinkClaimAsync(string claimId, DateTimeOffset mintedAt, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> UnlinkClaimAsync(string expectedClaimId, CancellationToken ct = default) => Task.FromResult(Result.Ok());
    }

    private sealed class FakeEligibility : IPrimaryScopeTypeEligibility
    {
        private readonly WorkItemType[] _allowed;
        private readonly bool _permitAll;
        private readonly bool _unavailable;

        private FakeEligibility(bool permitAll, bool unavailable, WorkItemType[] allowed)
        {
            _permitAll = permitAll;
            _unavailable = unavailable;
            _allowed = allowed;
        }

        public static FakeEligibility All() => new(permitAll: true, unavailable: false, allowed: Array.Empty<WorkItemType>());
        public static FakeEligibility Only(params WorkItemType[] allowed) => new(permitAll: false, unavailable: false, allowed);
        public static FakeEligibility Unavailable() => new(permitAll: false, unavailable: true, allowed: Array.Empty<WorkItemType>());

        public Result<bool> Evaluate(WorkItemType type)
        {
            if (_unavailable)
                return Result.Fail<bool>(AttachmentStorageFailure.EligibilityUnavailable);
            if (_permitAll) return Result.Ok(true);
            foreach (var a in _allowed)
                if (string.Equals(a.Value, type.Value, StringComparison.OrdinalIgnoreCase))
                    return Result.Ok(true);
            return Result.Ok(false);
        }
    }

    private sealed class FakeSystemRegistry : ISystemWorktreeRegistry
    {
        private readonly Dictionary<string, SystemWorktreeRow> _rows;
        public List<string> UpsertedFingerprints { get; } = new();

        private FakeSystemRegistry(Dictionary<string, SystemWorktreeRow> rows) { _rows = rows; }

        public static FakeSystemRegistry Empty() => new(new Dictionary<string, SystemWorktreeRow>(StringComparer.Ordinal));

        public static FakeSystemRegistry WithRegisteredWorktree(string fingerprint, string connectionRef)
        {
            var rows = new Dictionary<string, SystemWorktreeRow>(StringComparer.Ordinal)
            {
                [fingerprint] = new SystemWorktreeRow(connectionRef, RetiredAt: null),
            };
            return new FakeSystemRegistry(rows);
        }

        public static FakeSystemRegistry WithRetiredWorktree(string fingerprint, string connectionRef, DateTimeOffset retiredAt)
        {
            var rows = new Dictionary<string, SystemWorktreeRow>(StringComparer.Ordinal)
            {
                [fingerprint] = new SystemWorktreeRow(connectionRef, retiredAt),
            };
            return new FakeSystemRegistry(rows);
        }

        public Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok<SystemWorktreeRow?>(_rows.TryGetValue(worktreeFingerprint, out var row) ? row : null));

        public Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());

        public Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
        {
            UpsertedFingerprints.Add(worktreeFingerprint);
            _rows[worktreeFingerprint] = new SystemWorktreeRow(connectionRef, RetiredAt: null);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> InsertClaimAsync(string claimId, string connectionRef, string worktreeFingerprint, int workItemId, string state, string casToken, string recordJson, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> UpdateClaimStateAsync(string claimId, string expectedCasToken, string newCasToken, string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<IReadOnlyList<SystemClaimRow>>> FindClaimsForTupleAsync(string connectionRef, int workItemId, CancellationToken ct = default) => Task.FromResult(Result.Ok<IReadOnlyList<SystemClaimRow>>(Array.Empty<SystemClaimRow>()));
        public Task<Result> SupersedeAndActivateClaimAsync(string newClaimId, string newCasToken, string connectionRef, string worktreeFingerprint, int workItemId, string newRecordJson, string predecessorClaimId, string predecessorExpectedCasToken, string predecessorNewCasToken, string predecessorRecordJson, DateTimeOffset transitionAt, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemProfileCacheRow?>(null));
        public Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default) => Task.FromResult(Result.Ok());
    }

    private sealed class FakeFingerprintProvider : IWorktreeFingerprintProvider
    {
        private readonly WorktreeFingerprintContext _context;
        public FakeFingerprintProvider(WorktreeFingerprintContext context) { _context = context; }
        public WorktreeFingerprintContext CurrentFingerprint => _context;
    }

    private sealed class FakeUrlBuilder : IPrimaryScopeUrlBuilder
    {
        private readonly string _org;
        private readonly string _project;
        public FakeUrlBuilder(string org, string project) { _org = org; _project = project; }
        public string BuildWorkItemUrl(int workItemId) =>
            $"https://dev.azure.com/{_org}/{_project}/_workitems/edit/{workItemId}";
    }

    private sealed class InMemoryContextStore : IContextStore
    {
        private int? _activeId;
        private readonly Dictionary<string, string> _values = new();

        public Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default) => Task.FromResult(_activeId);
        public Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default) { _activeId = id; return Task.CompletedTask; }
        public Task ClearActiveWorkItemIdAsync(CancellationToken ct = default) { _activeId = null; return Task.CompletedTask; }
        public Task<string?> GetValueAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task SetValueAsync(string key, string value, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
