using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// AB#746 Herdr transport adapter behaviour tests. Every test runs
/// against a synthetic <see cref="FakeHerdrHostSurface"/>; no live
/// broker, no real <c>herdr</c> process. Covers:
/// <list type="bullet">
///   <item>Identity: adapterId, capabilities, description shape (§7.1, §12.2).</item>
///   <item>Record shape: agent-driven and direct-human validate; other shapes rejected (§2.2).</item>
///   <item>§4.2 concrete Herdr status mapping and §4.3 <c>idle ↛ done</c>.</item>
///   <item>§5.1 bounded probe budget honoured; §5.2 timeout produces the named embedded observation with §5.3 Stale freshness.</item>
///   <item>§5.3 freshness computed against the observation's own <c>recordedAt</c>.</item>
///   <item>§6.1 detach never reaches close; §6.2 close runs preflight then exactly ONE close; §6.3 UNVERIFIED-safe partial-close outcome.</item>
///   <item>§1.1(c) non-interference: attach, probe, detach, close never reach an R1–R15 verb (structural conformance file).</item>
/// </list>
/// </summary>
public sealed class HerdrTransportAdapterTests
{
    private static readonly System.DateTimeOffset _fixedNow = new(2026, 8, 26, 12, 0, 0, System.TimeSpan.Zero);

    private static (HerdrTransportAdapter adapter, FakeHerdrHostSurface host, FakeTimeProvider clock) NewAdapter()
    {
        var clock = new FakeTimeProvider(_fixedNow);
        var host = new FakeHerdrHostSurface
        {
            StatusRecordedAt = _fixedNow,
            LivenessRecordedAt = _fixedNow,
        };
        return (new HerdrTransportAdapter(host, clock), host, clock);
    }

    private static TransportAdapterTarget PaneTarget(
        string workspace = "w3",
        string tab = "w3:t1",
        string pane = "w3:p1",
        string? agentTarget = null) => new(
        Role: TransportAdapterRole.Agent,
        AdapterId: HerdrAdapterConstants.AdapterId,
        HostAttachmentId: pane,
        HostAttachmentIdKind: HerdrAdapterConstants.HostAttachmentIdKindPane,
        AdapterContext: new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            [HerdrAdapterContextKeys.Workspace] = workspace,
            [HerdrAdapterContextKeys.Tab] = tab,
            [HerdrAdapterContextKeys.Pane] = pane,
            [HerdrAdapterContextKeys.AgentTarget] = agentTarget ?? string.Empty,
        });

    private static TransportAdapterTarget TabTarget(string workspace = "w3", string tab = "w3:t1") => new(
        Role: TransportAdapterRole.Terminal,
        AdapterId: HerdrAdapterConstants.AdapterId,
        HostAttachmentId: tab,
        HostAttachmentIdKind: HerdrAdapterConstants.HostAttachmentIdKindTab,
        AdapterContext: new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            [HerdrAdapterContextKeys.Workspace] = workspace,
            [HerdrAdapterContextKeys.Tab] = tab,
        });

    // ---------------------------------------------------------------
    // §7.1, §12.2 — identity, capabilities, DescribeAdapter shape.
    // ---------------------------------------------------------------

    [Fact]
    public void AdapterId_is_lowercase_herdr()
    {
        var (adapter, _, _) = NewAdapter();
        adapter.AdapterId.ShouldBe("herdr");
    }

    [Fact]
    public void Declares_all_five_optional_capabilities()
    {
        var (adapter, _, _) = NewAdapter();
        adapter.Capabilities.ShouldBe(new HashSet<TransportCapability>
        {
            TransportCapability.StatusReporting,
            TransportCapability.LivenessProbe,
            TransportCapability.Detach,
            TransportCapability.Close,
            TransportCapability.PartialClose,
        }, ignoreOrder: true);
    }

    [Fact]
    public void DescribeAdapter_declares_all_five_optional_capabilities_and_excludes_mandatory_names()
    {
        var (adapter, _, _) = NewAdapter();
        var description = adapter.DescribeAdapter();
        description.AdapterId.ShouldBe("herdr");
        description.DisplayName.ShouldBe(HerdrAdapterConstants.DisplayName);
        description.AdapterVersion.ShouldBe(HerdrAdapterConstants.AdapterVersion);
        // §12.2 — Herdr declares every §3.3 OPTIONAL capability. All
        // five names MUST be present in the declared set.
        description.Capabilities.ShouldContain(TransportCapability.StatusReporting);
        description.Capabilities.ShouldContain(TransportCapability.LivenessProbe);
        description.Capabilities.ShouldContain(TransportCapability.Detach);
        description.Capabilities.ShouldContain(TransportCapability.Close);
        description.Capabilities.ShouldContain(TransportCapability.PartialClose);
        description.Capabilities.Count.ShouldBe(5);
        // §3.1 — the two mandatory common-denominator names
        // (RecordIdentity, DescribeAdapter) NEVER appear in a declared
        // Capabilities set. The persisted wire form is a string set
        // (§2.1 / §2.2 row 6); assert neither literal appears when the
        // declared enum values are projected to their wire strings.
        var wireNames = description.Capabilities
            .Select(c => c.ToWire())
            .ToHashSet(System.StringComparer.Ordinal);
        wireNames.ShouldNotContain(TransportCapabilityExtensions.RecordIdentity);
        wireNames.ShouldNotContain(TransportCapabilityExtensions.DescribeAdapter);
        description.SupportedRoles.ShouldContain(TransportAdapterRole.Worktree);
        description.SupportedRoles.ShouldContain(TransportAdapterRole.Agent);
        description.SupportedRoles.ShouldContain(TransportAdapterRole.Terminal);
    }

    // ---------------------------------------------------------------
    // §4.2 concrete mapping / §4.3 idle → idle-ambiguous (never done).
    // ---------------------------------------------------------------

    [Fact]
    public void MapHostStatus_maps_idle_to_idle_ambiguous()
        => HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Idle).ShouldBe(RecordedStatus.IdleAmbiguous);

    [Fact]
    public void MapHostStatus_maps_working_to_working()
        => HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Working).ShouldBe(RecordedStatus.Working);

    [Fact]
    public void MapHostStatus_maps_blocked_to_blocked()
        => HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Blocked).ShouldBe(RecordedStatus.Blocked);

    [Fact]
    public void MapHostStatus_maps_done_to_done()
        => HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Done).ShouldBe(RecordedStatus.Done);

    [Fact]
    public void MapHostStatus_maps_unknown_to_unknown()
        => HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Unknown).ShouldBe(RecordedStatus.Unknown);

    [Fact]
    public void MapHostStatus_never_maps_idle_to_done()
    {
        // §4.3 — the authorization-neutrality safeguard. Belt-and-suspenders
        // for a code review scanning the fixed lookup for the wrong branch.
        HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Idle).ShouldNotBe(RecordedStatus.Done);
        HerdrTransportAdapter.MapHostStatus(HerdrHostStatus.Idle).ShouldBe(RecordedStatus.IdleAmbiguous);
    }

    [Fact]
    public async Task ReportStatus_maps_idle_to_idle_ambiguous_and_marks_fresh_on_ok_path()
    {
        var (adapter, host, _) = NewAdapter();
        host.StatusOutcome = HerdrOperationOutcome.Ok;
        host.StatusValue = HerdrHostStatus.Idle;
        host.StatusRecordedAt = _fixedNow;
        var result = await adapter.ReportStatusAsync(PaneTarget(agentTarget: "w3:a1"), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(RecordedStatus.IdleAmbiguous);
        result.Value.RecordedAt.ShouldBe(_fixedNow);
        result.Value.Freshness.ShouldBe(TransportFreshness.Fresh);
        result.Value.TimeoutError.ShouldBeNull();
    }

    // ---------------------------------------------------------------
    // §5.1 bounded budget / §5.2 timeout / §5.3 freshness + carve-out.
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReportStatus_passes_default_status_budget_to_host_when_options_null()
    {
        var (adapter, host, _) = NewAdapter();
        await adapter.ReportStatusAsync(PaneTarget(), options: null, CancellationToken.None);
        host.StatusCalls.ShouldHaveSingleItem();
        host.StatusCalls[0].budget.ShouldBe(TransportProbeBudget.StatusReportingDefaultMs);
    }

    [Fact]
    public async Task ProbeLiveness_passes_default_liveness_budget_to_host_when_options_null()
    {
        var (adapter, host, _) = NewAdapter();
        await adapter.ProbeLivenessAsync(PaneTarget(), options: null, CancellationToken.None);
        host.LivenessCalls.ShouldHaveSingleItem();
        host.LivenessCalls[0].budget.ShouldBe(TransportProbeBudget.LivenessProbeDefaultMs);
    }

    [Fact]
    public async Task ReportStatus_honours_caller_supplied_budget_override()
    {
        var (adapter, host, _) = NewAdapter();
        await adapter.ReportStatusAsync(PaneTarget(), new TransportProbeOptions(TimeoutMs: 250), CancellationToken.None);
        host.StatusCalls[0].budget.ShouldBe(250);
    }

    [Fact]
    public async Task ReportStatus_timeout_returns_named_embedded_observation_marked_stale()
    {
        var (adapter, host, _) = NewAdapter();
        host.StatusOutcome = HerdrOperationOutcome.Timeout;
        var result = await adapter.ReportStatusAsync(PaneTarget(), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(); // §5.2 — embedded observation, not Result.Fail.
        result.Value.Status.ShouldBe(RecordedStatus.Unknown);
        result.Value.TimeoutError.ShouldBe(TransportAttachmentFailure.ProbeTimeout);
        // §5.3 carve-out: bounded-failure observation MUST report Stale.
        result.Value.Freshness.ShouldBe(TransportFreshness.Stale);
    }

    [Fact]
    public async Task ProbeLiveness_timeout_returns_error_presence_with_probe_timeout_and_stale()
    {
        var (adapter, host, _) = NewAdapter();
        host.LivenessOutcome = HerdrOperationOutcome.Timeout;
        var result = await adapter.ProbeLivenessAsync(PaneTarget(), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Presence.ShouldBe(TransportLivenessPresence.Error);
        result.Value.Error.ShouldBe(TransportAttachmentFailure.ProbeTimeout);
        result.Value.Freshness.ShouldBe(TransportFreshness.Stale);
    }

    [Fact]
    public async Task ReportStatus_adapter_failure_returns_probe_adapter_failed_at_result_shell()
    {
        var (adapter, host, _) = NewAdapter();
        host.ThrowFromStatus = () => throw new System.InvalidOperationException("boom");
        var result = await adapter.ReportStatusAsync(PaneTarget(), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ProbeLiveness_adapter_failure_returns_probe_adapter_failed_at_result_shell()
    {
        var (adapter, host, _) = NewAdapter();
        host.ThrowFromLiveness = () => throw new System.InvalidOperationException("boom");
        var result = await adapter.ProbeLivenessAsync(PaneTarget(), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ReportStatus_marks_stale_when_recordedAt_is_older_than_fresh_window()
    {
        var (adapter, host, clock) = NewAdapter();
        host.StatusOutcome = HerdrOperationOutcome.Ok;
        host.StatusValue = HerdrHostStatus.Working;
        // Older than TransportProbeBudget.FreshWindowMs (2000 ms) — the
        // §5.3 timestamp rule then reports the OK observation as Stale.
        var older = _fixedNow - System.TimeSpan.FromMilliseconds(TransportProbeBudget.FreshWindowMs + 1);
        host.StatusRecordedAt = older;
        clock.Now = _fixedNow;
        var result = await adapter.ReportStatusAsync(PaneTarget(), options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.RecordedAt.ShouldBe(older);
        result.Value.Freshness.ShouldBe(TransportFreshness.Stale);
    }

    // ---------------------------------------------------------------
    // §6.1 Detach — Twig-side no-op, never reaches close.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Detach_is_a_noop_that_never_touches_host()
    {
        var (adapter, host, _) = NewAdapter();
        var result = await adapter.DetachAsync(PaneTarget(), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        host.PreflightCalls.ShouldBeEmpty();
        host.CloseCalls.ShouldBeEmpty();
        host.StatusCalls.ShouldBeEmpty();
        host.LivenessCalls.ShouldBeEmpty();
        host.RemainingCalls.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------
    // §6.2 Close — preflight then exactly one unpiped close.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Close_runs_preflight_before_issuing_close()
    {
        var (adapter, host, _) = NewAdapter();
        var result = await adapter.CloseAsync(TabTarget(), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        host.PreflightCalls.ShouldHaveSingleItem();
        host.CloseCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Close_refuses_when_preflight_reports_ids_no_longer_resolve()
    {
        var (adapter, host, _) = NewAdapter();
        host.PreflightOutcome = HerdrOperationOutcome.Ok;
        host.PreflightConfirmed = false;
        var result = await adapter.CloseAsync(TabTarget(), CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.CloseAdapterFailed);
        // §7.4 — moved pane gets a new id, so a stale coordinate MUST
        // NOT reach a close.
        host.CloseCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Close_refuses_when_preflight_surface_fails()
    {
        var (adapter, host, _) = NewAdapter();
        host.PreflightOutcome = HerdrOperationOutcome.Failed;
        var result = await adapter.CloseAsync(TabTarget(), CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.CloseAdapterFailed);
        host.CloseCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Close_reports_close_adapter_failed_when_host_close_fails()
    {
        var (adapter, host, _) = NewAdapter();
        host.CloseOutcome = HerdrOperationOutcome.Failed;
        var result = await adapter.CloseAsync(TabTarget(), CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.CloseAdapterFailed);
    }

    [Fact]
    public async Task Close_issues_exactly_one_close_call_for_the_given_target()
    {
        var (adapter, host, _) = NewAdapter();
        var target = TabTarget();
        var result = await adapter.CloseAsync(target, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        host.CloseCalls.Count.ShouldBe(1);
        host.CloseCalls[0].HostAttachmentId.ShouldBe(target.HostAttachmentId);
        host.CloseCalls[0].HostAttachmentIdKind.ShouldBe(HerdrAdapterConstants.HostAttachmentIdKindTab);
    }

    // ---------------------------------------------------------------
    // §6.3 PartialClose — UNVERIFIED-safe outcome.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PartialClose_returns_unknown_when_confirmation_surface_returns_unknown()
    {
        var (adapter, host, _) = NewAdapter();
        host.RemainingOutcome = HerdrOperationOutcome.Ok;
        host.RemainingSummary = HerdrRemainingSummary.Unknown;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope(ScopeKind: "pane", ScopeId: "w3:p9", Reason: PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Attempted.ShouldBeTrue();
        result.Value.ObservedRemaining.ShouldBe(TransportPartialCloseRemaining.Unknown);
    }

    [Fact]
    public async Task PartialClose_returns_unknown_when_confirmation_surface_fails()
    {
        var (adapter, host, _) = NewAdapter();
        host.RemainingOutcome = HerdrOperationOutcome.Failed;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope(ScopeKind: "pane", ScopeId: "w3:p9", Reason: PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ObservedRemaining.ShouldBe(TransportPartialCloseRemaining.Unknown);
    }

    [Fact]
    public async Task PartialClose_returns_unknown_when_confirmation_throws()
    {
        var (adapter, host, _) = NewAdapter();
        host.ThrowFromRemaining = () => throw new System.InvalidOperationException("network");
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope(ScopeKind: "pane", ScopeId: "w3:p9", Reason: PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ObservedRemaining.ShouldBe(TransportPartialCloseRemaining.Unknown);
    }

    [Fact]
    public async Task PartialClose_reports_subset_only_when_surface_independently_confirms()
    {
        var (adapter, host, _) = NewAdapter();
        host.RemainingOutcome = HerdrOperationOutcome.Ok;
        host.RemainingSummary = HerdrRemainingSummary.Subset;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope("pane", "w3:p9", PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.Value.ObservedRemaining.ShouldBe(TransportPartialCloseRemaining.Subset);
    }

    [Fact]
    public async Task PartialClose_reports_partial_close_adapter_failed_on_close_failure()
    {
        var (adapter, host, _) = NewAdapter();
        host.CloseOutcome = HerdrOperationOutcome.Failed;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope("pane", "w3:p9", PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.PartialCloseAdapterFailed);
    }

    [Fact]
    public async Task PartialClose_refuses_when_preflight_fails()
    {
        var (adapter, host, _) = NewAdapter();
        host.PreflightConfirmed = false;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope("pane", "w3:p9", PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.PartialCloseAdapterFailed);
        host.CloseCalls.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------
    // Defect 2 (Spec-axis final review) — §7.4 scoped preflight.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PartialClose_preflights_the_fully_scoped_locator_not_the_parent()
    {
        // §7.4 mandates workspace/tab/pane cross-check before ANY
        // close. A stale/foreign pane id that survives inside a live
        // parent tab MUST be caught here — so preflight has to see the
        // scoped pane, not the parent alone.
        var (adapter, host, _) = NewAdapter();
        await adapter.PartialCloseAsync(
            TabTarget(workspace: "w3", tab: "w3:t1"),
            new PartialCloseScope(ScopeKind: "pane", ScopeId: "w3:p-scoped", Reason: PartialCloseReason.UserRequested),
            CancellationToken.None);
        host.PreflightCalls.ShouldHaveSingleItem();
        var seen = host.PreflightCalls[0];
        seen.Pane.ShouldBe("w3:p-scoped");
        seen.HostAttachmentId.ShouldBe("w3:p-scoped");
        seen.HostAttachmentIdKind.ShouldBe(HerdrAdapterConstants.HostAttachmentIdKindPane);
    }

    [Fact]
    public async Task PartialClose_refuses_unknown_scope_kind_without_touching_host()
    {
        // §6.3 / §7.4 — an unknown ScopeKind MUST NOT fall through to
        // the parent locator. The whole point of a scoped close is
        // that the scope identifies WHICH host slice we mutate; an
        // unknown scope refuses OUTRIGHT.
        var (adapter, host, _) = NewAdapter();
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope(ScopeKind: "window", ScopeId: "w3:x1", Reason: PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.PartialCloseAdapterFailed);
        // The host MUST be untouched — no preflight, no close.
        host.PreflightCalls.ShouldBeEmpty();
        host.CloseCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PartialClose_valid_scope_preflights_then_closes_exactly_once()
    {
        // Third path: a legitimate scoped close. Preflight fires once
        // against the scoped locator; close fires exactly once against
        // the same.
        var (adapter, host, _) = NewAdapter();
        host.RemainingSummary = HerdrRemainingSummary.Subset;
        var scope = new PartialCloseScope(ScopeKind: "pane", ScopeId: "w3:p-scoped", Reason: PartialCloseReason.UserRequested);
        var result = await adapter.PartialCloseAsync(TabTarget(), scope, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        host.PreflightCalls.Count.ShouldBe(1);
        host.CloseCalls.Count.ShouldBe(1);
        host.CloseCalls[0].HostAttachmentId.ShouldBe("w3:p-scoped");
        host.CloseCalls[0].HostAttachmentIdKind.ShouldBe(HerdrAdapterConstants.HostAttachmentIdKindPane);
    }

    [Fact]
    public async Task PartialClose_scoped_preflight_failure_refuses_without_calling_close()
    {
        // §7.4 destructive-bug prevention: the scoped preflight caught
        // "stale/foreign scope id inside a still-live parent" case.
        // A failing preflight MUST NOT reach the close call.
        var (adapter, host, _) = NewAdapter();
        host.PreflightConfirmed = false;
        var result = await adapter.PartialCloseAsync(
            TabTarget(),
            new PartialCloseScope("pane", "w3:p-stale", PartialCloseReason.UserRequested),
            CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.PartialCloseAdapterFailed);
        host.CloseCalls.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------
    // §2.2 shape produced by RecordIdentity — accepted rows only.
    // ---------------------------------------------------------------

    [Fact]
    public void RecordIdentity_produces_direct_human_shape_when_agent_null()
    {
        var (adapter, _, _) = NewAdapter();
        var worktree = TabTarget(workspace: "w3", tab: "w3:t1") with { Role = TransportAdapterRole.Worktree };
        var terminal = TabTarget(workspace: "w3", tab: "w3:t1") with { Role = TransportAdapterRole.Terminal };
        var request = new RecordIdentityRequest(
            WorktreeFingerprint: "fp",
            WorktreeTarget: worktree,
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: terminal,
            AgentCapabilities: new HashSet<TransportCapability>(),
            TerminalCapabilities: new HashSet<TransportCapability>
            {
                TransportCapability.StatusReporting,
                TransportCapability.LivenessProbe,
                TransportCapability.Detach,
                TransportCapability.Close,
                TransportCapability.PartialClose,
            },
            AgentRecordedStatus: RecordedStatus.Unknown,
            AgentRecordedAt: _fixedNow);
        var result = adapter.RecordIdentity(request);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Agent.ShouldBeNull();
        result.Value.Worktree.ShouldNotBeNull();
        result.Value.Terminal.ShouldNotBeNull();
    }

    [Fact]
    public void RecordIdentity_produces_agent_driven_shape_when_agent_present()
    {
        var (adapter, _, _) = NewAdapter();
        var worktree = TabTarget() with { Role = TransportAdapterRole.Worktree };
        var agent = new TransportAdapterTarget(
            Role: TransportAdapterRole.Agent,
            AdapterId: HerdrAdapterConstants.AdapterId,
            HostAttachmentId: "w3:a1",
            HostAttachmentIdKind: HerdrAdapterConstants.HostAttachmentIdKindPane,
            AdapterContext: new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                [HerdrAdapterContextKeys.Workspace] = "w3",
                [HerdrAdapterContextKeys.AgentTarget] = "w3:a1",
            });
        var caps = new HashSet<TransportCapability>
        {
            TransportCapability.StatusReporting,
            TransportCapability.LivenessProbe,
            TransportCapability.Detach,
            TransportCapability.Close,
            TransportCapability.PartialClose,
        };
        var request = new RecordIdentityRequest(
            WorktreeFingerprint: "fp",
            WorktreeTarget: worktree,
            AgentTarget: agent,
            AgentSessionKind: "codex",
            TerminalTarget: null,
            AgentCapabilities: caps,
            TerminalCapabilities: new HashSet<TransportCapability>(),
            AgentRecordedStatus: RecordedStatus.IdleAmbiguous,
            AgentRecordedAt: _fixedNow);
        var result = adapter.RecordIdentity(request);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Agent.ShouldNotBeNull();
        result.Value.Agent!.RecordedStatus.ShouldBe(RecordedStatus.IdleAmbiguous);
    }

    [Fact]
    public void RecordIdentity_rejects_a_target_with_a_foreign_adapter_id()
    {
        var (adapter, _, _) = NewAdapter();
        var foreignTerminal = new TransportAdapterTarget(
            Role: TransportAdapterRole.Terminal,
            AdapterId: "windows-terminal",
            HostAttachmentId: "42",
            HostAttachmentIdKind: "wt-window-integer",
            AdapterContext: new Dictionary<string, string>());
        var worktree = TabTarget() with { Role = TransportAdapterRole.Worktree };
        var request = new RecordIdentityRequest(
            WorktreeFingerprint: "fp",
            WorktreeTarget: worktree,
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: foreignTerminal,
            AgentCapabilities: new HashSet<TransportCapability>(),
            TerminalCapabilities: new HashSet<TransportCapability>(),
            AgentRecordedStatus: RecordedStatus.Unknown,
            AgentRecordedAt: _fixedNow);
        var result = adapter.RecordIdentity(request);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.OrphanTerminal);
    }

    // ---------------------------------------------------------------
    // Target parsing — workspace mandatory (§12.2), no Herdr concept
    // leaks into the core surface (locator lives inside adapter only).
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReportStatus_returns_probe_adapter_failed_when_target_lacks_workspace_key()
    {
        var (adapter, _, _) = NewAdapter();
        var target = new TransportAdapterTarget(
            Role: TransportAdapterRole.Agent,
            AdapterId: HerdrAdapterConstants.AdapterId,
            HostAttachmentId: "w3:p1",
            HostAttachmentIdKind: HerdrAdapterConstants.HostAttachmentIdKindPane,
            AdapterContext: new Dictionary<string, string>()); // no workspace key
        var result = await adapter.ReportStatusAsync(target, options: null, CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }
}

/// <summary>Minimal <see cref="TimeProvider"/> stub for freshness tests
/// — mutable <c>Now</c> so a single test can advance the clock past the
/// §5.3 fresh window and see Stale.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public System.DateTimeOffset Now { get; set; }
    public FakeTimeProvider(System.DateTimeOffset now) { Now = now; }
    public override System.DateTimeOffset GetUtcNow() => Now;
}
