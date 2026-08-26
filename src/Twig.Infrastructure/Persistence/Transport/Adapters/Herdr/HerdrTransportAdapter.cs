using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// Contract §12.2 AB#746 Herdr transport adapter. Registers under
/// <see cref="HerdrAdapterConstants.AdapterId"/> and declares every §3.3
/// optional capability:
/// <c>{ StatusReporting, LivenessProbe, Detach, Close, PartialClose }</c>.
/// <see cref="ITransportAdapter.RecordIdentity"/> and
/// <see cref="ITransportAdapter.DescribeAdapter"/> are §3.1
/// common-denominator and are NEVER members of the declared set
/// (§3.1).
/// <para>
/// Grounded host facts this adapter builds on:
/// </para>
/// <list type="bullet">
///   <item>Observation surface is poll-only:
///     <c>herdr api snapshot</c>, <c>herdr pane current --current</c>,
///     <c>herdr agent explain &lt;target&gt; --json</c>, and
///     <c>herdr agent wait &lt;target&gt; --until &lt;state&gt; --timeout &lt;ms&gt;</c>
///     for bounded blocking waits. No subscribe/event/stream/watch
///     verb, no push feed — no dedicated thread, no reconnect loop, no
///     broker event handler (§5.3, §12.2).</item>
///   <item><c>herdr agent wait</c> blocks forever when
///     <c>--timeout</c> is omitted; the adapter MUST always pass an
///     explicit <c>--timeout</c> (§5.1, §12.2). Enforced structurally
///     by delegating every blocking wait to
///     <see cref="IHerdrHostSurface"/>, whose contract fixes the same
///     rule.</item>
///   <item>Host status vocabulary is exactly
///     <c>idle|working|blocked|done|unknown</c>; mapped by table lookup
///     only (§4.2). <see cref="HerdrHostStatus.Idle"/> maps to
///     <see cref="RecordedStatus.IdleAmbiguous"/> and NEVER to
///     <see cref="RecordedStatus.Done"/> on any path (§4.3).</item>
///   <item>Ids are opaque and workspace-qualified; a moved pane gets a
///     new id, so a cached id is not authoritative (§7.4). The
///     <see cref="IHerdrHostSurface.PreflightCloseAsync"/> cross-check
///     is what §12.2 uses to catch stale coordinates before close.
///   </item>
///   <item>Close verbs are exactly one unpiped
///     <c>herdr tab close &lt;tab_id&gt;</c> /
///     <c>herdr pane close &lt;pane_id&gt;</c> — never piped, because
///     the pipeline exit status hides its failure (§12.2). The single
///     unpiped call is <see cref="IHerdrHostSurface.CloseAsync"/>.
///   </item>
///   <item>Partial-close outcome is UNVERIFIED from the read-only
///     surface, so this adapter reports
///     <see cref="TransportPartialCloseRemaining.Unknown"/> whenever it
///     cannot independently confirm (§6.3). No compensating
///     <see cref="ITransportAdapter.CloseAsync"/> is invoked from any
///     path — §1.1(c).</item>
/// </list>
/// <para>
/// Caching is not used. §5.3 permits a per-<c>hostAttachmentId</c>
/// in-process cache but does not mandate one; every invocation
/// re-polls under the §5.1 budget, so the returned observation is
/// <see cref="TransportFreshness.Fresh"/> on the OK path by
/// construction. Bounded-failure observations (timeout §5.2) are
/// reported <see cref="TransportFreshness.Stale"/> per the §5.3
/// failure-mode carve-out.
/// </para>
/// <para>
/// No Herdr concept (workspace / tab / pane triple) leaks into the core
/// contract surface — those fields live entirely in
/// <see cref="TransportAdapterTarget.AdapterContext"/> per §7.4.
/// </para>
/// </summary>
internal sealed class HerdrTransportAdapter : ITransportAdapter
{
    private static readonly IReadOnlySet<TransportCapability> _capabilities = new HashSet<TransportCapability>
    {
        TransportCapability.StatusReporting,
        TransportCapability.LivenessProbe,
        TransportCapability.Detach,
        TransportCapability.Close,
        TransportCapability.PartialClose,
    };

    private static readonly IReadOnlySet<TransportAdapterRole> _supportedRoles = new HashSet<TransportAdapterRole>
    {
        TransportAdapterRole.Worktree,
        TransportAdapterRole.Agent,
        TransportAdapterRole.Terminal,
    };

    private readonly IHerdrHostSurface _host;
    private readonly TimeProvider _clock;

    public HerdrTransportAdapter(IHerdrHostSurface host, TimeProvider clock)
    {
        _host = host;
        _clock = clock;
    }

    public string AdapterId => HerdrAdapterConstants.AdapterId;
    public IReadOnlySet<TransportCapability> Capabilities => _capabilities;

    public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request)
    {
        // §3.1 common-denominator — echo the caller-supplied identity into
        // a §2.1 record. The adapter never DISCOVERS an id; every field
        // was recorded by the caller.
        //
        // The Herdr adapter accepts the two §2.2 shapes it can produce:
        //   • agent-driven — worktree + agent + optional terminal.
        //   • direct-human — worktree + terminal, no agent.
        // Every input target's adapterId is asserted to be "herdr" so
        // a caller mixing null / windows-terminal fields into a Herdr
        // record is caught here as an orphan-terminal (§2.2 row 4).

        if (!IsHerdrTarget(request.WorktreeTarget))
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.OrphanTerminal);
        if (request.AgentTarget is not null && !IsHerdrTarget(request.AgentTarget))
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.OrphanTerminal);
        if (request.TerminalTarget is not null && !IsHerdrTarget(request.TerminalTarget))
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.OrphanTerminal);

        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                WorktreeFingerprint: request.WorktreeFingerprint,
                Target: request.WorktreeTarget),
            Agent: request.AgentTarget is null
                ? null
                : new TransportAgentPayload(
                    Target: request.AgentTarget,
                    SessionKind: request.AgentSessionKind ?? string.Empty,
                    RecordedStatus: request.AgentRecordedStatus,
                    RecordedAt: request.AgentRecordedAt,
                    Capabilities: request.AgentCapabilities),
            Terminal: request.TerminalTarget is null
                ? null
                : new TransportTerminalPayload(
                    Target: request.TerminalTarget,
                    Capabilities: request.TerminalCapabilities));

        // Shape validator sanity: the record MUST match one of the two
        // §2.2 shapes. The named §11 identifier surfaces verbatim so
        // callers branch on it without parsing prose.
        var shape = TransportShapeValidator.ValidateRecord(record);
        if (!shape.IsSuccess)
            return Result.Fail<TransportAttachmentRecord>(shape.Error);
        return Result.Ok(record);
    }

    public AdapterDescription DescribeAdapter() => new(
        AdapterId: HerdrAdapterConstants.AdapterId,
        DisplayName: HerdrAdapterConstants.DisplayName,
        AdapterVersion: HerdrAdapterConstants.AdapterVersion,
        Capabilities: _capabilities,
        SupportedRoles: _supportedRoles,
        HumanReadable: "Herdr transport adapter — polls herdr api snapshot / pane current / agent explain under a bounded budget; close is a single unpiped herdr {tab|pane} close.");

    public async Task<Result<TransportStatusObservation>> ReportStatusAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct)
    {
        if (!TryBuildLocator(target, out var locator))
            return Result.Fail<TransportStatusObservation>(TransportAttachmentFailure.ProbeAdapterFailed);

        var budgetMs = TransportProbeBudget.ResolveStatusBudget(options);
        HerdrStatusReadout readout;
        try
        {
            readout = await _host.QueryStatusAsync(locator, budgetMs, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // §5.2 — adapter internal failure that could not produce a
            // bounded observation at all. Dispatch-level failure the
            // caller branches on via Result.
            return Result.Fail<TransportStatusObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }

        switch (readout.Outcome)
        {
            case HerdrOperationOutcome.Ok:
                // §4.2 table lookup only. §4.3 — Idle → IdleAmbiguous,
                // NEVER Done. Every mapping goes through this switch.
                var mapped = MapHostStatus(readout.Status);
                return Result.Ok(new TransportStatusObservation(
                    Status: mapped,
                    RecordedAt: readout.RecordedAt,
                    // §5.3 successful-observation path uses the pure
                    // timestamp rule; no cache, so recordedAt was
                    // stamped just now and is Fresh by construction.
                    Freshness: TransportProbeBudget.Compute(readout.RecordedAt, _clock.GetUtcNow()),
                    TimeoutError: null));
            case HerdrOperationOutcome.Timeout:
                // §5.2 — bounded-failure observation embedded inside
                // Result.Ok. §5.3 carve-out: Freshness = Stale
                // regardless of RecordedAt.
                return Result.Ok(new TransportStatusObservation(
                    Status: RecordedStatus.Unknown,
                    RecordedAt: _clock.GetUtcNow(),
                    Freshness: TransportFreshness.Stale,
                    TimeoutError: TransportAttachmentFailure.ProbeTimeout));
            case HerdrOperationOutcome.Failed:
            default:
                return Result.Fail<TransportStatusObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }
    }

    public async Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct)
    {
        if (!TryBuildLocator(target, out var locator))
            return Result.Fail<TransportLivenessObservation>(TransportAttachmentFailure.ProbeAdapterFailed);

        var budgetMs = TransportProbeBudget.ResolveLivenessBudget(options);
        HerdrLivenessReadout readout;
        try
        {
            readout = await _host.QueryLivenessAsync(locator, budgetMs, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportLivenessObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }

        switch (readout.Outcome)
        {
            case HerdrOperationOutcome.Ok:
                return Result.Ok(new TransportLivenessObservation(
                    Presence: readout.Presence,
                    RecordedAt: readout.RecordedAt,
                    Freshness: TransportProbeBudget.Compute(readout.RecordedAt, _clock.GetUtcNow()),
                    Error: null));
            case HerdrOperationOutcome.Timeout:
                // §5.2 — Presence = Error, Error = probe-timeout,
                // §5.3 carve-out Freshness = Stale.
                return Result.Ok(new TransportLivenessObservation(
                    Presence: TransportLivenessPresence.Error,
                    RecordedAt: _clock.GetUtcNow(),
                    Freshness: TransportFreshness.Stale,
                    Error: TransportAttachmentFailure.ProbeTimeout));
            case HerdrOperationOutcome.Failed:
            default:
                return Result.Fail<TransportLivenessObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }
    }

    public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct)
    {
        // §6.1 / §12.2 — detach is a Twig-side cache drop only. This
        // adapter caches nothing (§5.3 permits but does not mandate an
        // in-process cache), so detach is a bookkeeping no-op that
        // succeeds unconditionally. §1.1(c) forbids any reach into
        // Close / PartialClose from here — none is present.
        _ = target;
        _ = ct;
        return Task.FromResult(Result.Ok());
    }

    public async Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct)
    {
        // §12.2 — exactly one unpiped `herdr tab close <tab_id>` (or
        // `herdr pane close <pane_id>`), after the AGENTS.md preflight
        // cross-check on workspace/tab/pane ids. Reachable only via
        // explicit caller invocation (§1.1(c), §6.2) — the dispatcher
        // gates on the capability set before we get here.
        if (!TryBuildLocator(target, out var locator))
            return Result.Fail(TransportAttachmentFailure.CloseAdapterFailed);

        HerdrPreflightReadout preflight;
        try
        {
            preflight = await _host.PreflightCloseAsync(locator, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail(TransportAttachmentFailure.CloseAdapterFailed);
        }

        // Any preflight failure — surface failure or "coordinates no
        // longer resolve" — refuses the close. §7.4's "moved pane gets
        // a NEW id" is the invariant this guards.
        if (preflight.Outcome != HerdrOperationOutcome.Ok || !preflight.Confirmed)
            return Result.Fail(TransportAttachmentFailure.CloseAdapterFailed);

        HerdrCloseReadout close;
        try
        {
            close = await _host.CloseAsync(locator, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail(TransportAttachmentFailure.CloseAdapterFailed);
        }

        return close.Outcome == HerdrOperationOutcome.Ok
            ? Result.Ok()
            : Result.Fail(TransportAttachmentFailure.CloseAdapterFailed);
    }

    public async Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(
        TransportAdapterTarget target,
        PartialCloseScope scope,
        CancellationToken ct)
    {
        if (!TryBuildLocator(target, out var locator))
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);

        // §7.4 — the scoped locator is what the close will address.
        // Unknown ScopeKind values are refused OUTRIGHT: the adapter
        // MUST NOT fall through to the parent target and mutate an
        // unexpected host (§6.3, §7.4). Only the two adapter-defined
        // scope kinds Herdr owns ("pane", "tab") are honoured.
        if (!TryScopeToLocator(locator, scope, out var scopedLocator))
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);

        // §7.4 mandated preflight cross-check on the FULLY SCOPED
        // locator's live records before issuing a scoped close. Using
        // the parent locator alone lets a stale/unrelated pane id
        // through as long as the parent tab still resolves; §12.2 says
        // "workspace/tab/pane cross-check" — the pane belongs in the
        // preflight when we're closing a pane.
        HerdrPreflightReadout preflight;
        try
        {
            preflight = await _host.PreflightCloseAsync(scopedLocator, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);
        }
        if (preflight.Outcome != HerdrOperationOutcome.Ok || !preflight.Confirmed)
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);

        // A partial close is scoped by `PartialCloseScope`; the
        // adapter honours its ScopeKind + ScopeId verbatim. The single
        // unpiped close call is directed at the scoped locator the
        // scope-builder produced above.
        HerdrCloseReadout close;
        try
        {
            close = await _host.CloseAsync(scopedLocator, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);
        }

        if (close.Outcome != HerdrOperationOutcome.Ok)
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseAdapterFailed);

        // §6.3 UNVERIFIED-safe: observedRemaining is Subset / None ONLY
        // when the adapter independently confirms via
        // ObservePartialCloseRemainingAsync. On any confirmation
        // failure (surface error OR the surface itself returning
        // "unknown"), the outcome MUST be Unknown. A caller receiving
        // Unknown MUST NOT compensate — §6.3 explicitly permits a
        // leaked pane over a destructive assumption.
        HerdrRemainingReadout remaining;
        try
        {
            remaining = await _host.ObservePartialCloseRemainingAsync(locator, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Confirmation failed — emit the UNVERIFIED-safe outcome.
            return Result.Ok(new TransportPartialCloseOutcome(
                Attempted: true,
                ObservedRemaining: TransportPartialCloseRemaining.Unknown,
                Error: null));
        }

        var summary = remaining.Outcome == HerdrOperationOutcome.Ok
            ? MapRemaining(remaining.Summary)
            : TransportPartialCloseRemaining.Unknown;

        return Result.Ok(new TransportPartialCloseOutcome(
            Attempted: true,
            ObservedRemaining: summary,
            Error: null));
    }

    /// <summary>§4.2 concrete Herdr mapping. §4.3 forbids any Idle →
    /// Done inference on any path, including cached / stale reuse; the
    /// switch below is the ONLY mapping site.</summary>
    internal static RecordedStatus MapHostStatus(HerdrHostStatus status) => status switch
    {
        HerdrHostStatus.Idle => RecordedStatus.IdleAmbiguous,
        HerdrHostStatus.Working => RecordedStatus.Working,
        HerdrHostStatus.Blocked => RecordedStatus.Blocked,
        HerdrHostStatus.Done => RecordedStatus.Done,
        HerdrHostStatus.Unknown => RecordedStatus.Unknown,
        _ => RecordedStatus.Unknown,
    };

    private static TransportPartialCloseRemaining MapRemaining(HerdrRemainingSummary summary) => summary switch
    {
        HerdrRemainingSummary.Subset => TransportPartialCloseRemaining.Subset,
        HerdrRemainingSummary.None => TransportPartialCloseRemaining.None,
        HerdrRemainingSummary.Unknown => TransportPartialCloseRemaining.Unknown,
        _ => TransportPartialCloseRemaining.Unknown,
    };

    private static bool IsHerdrTarget(TransportAdapterTarget target) =>
        string.Equals(target.AdapterId, HerdrAdapterConstants.AdapterId, System.StringComparison.Ordinal);

    /// <summary>Build the adapter-internal locator from
    /// <see cref="TransportAdapterTarget.AdapterContext"/> per §7.4.
    /// Rejects a target that names a non-Herdr adapter (defence in
    /// depth — the registry already routed us here) or that omits the
    /// mandatory <c>workspace</c> key (§12.2 mandate). No Herdr concept
    /// leaks out of the adapter through this locator.</summary>
    private static bool TryBuildLocator(TransportAdapterTarget target, out HerdrTargetLocator locator)
    {
        locator = default!;
        if (!IsHerdrTarget(target))
            return false;
        if (!target.AdapterContext.TryGetValue(HerdrAdapterContextKeys.Workspace, out var workspace) || string.IsNullOrEmpty(workspace))
            return false;
        target.AdapterContext.TryGetValue(HerdrAdapterContextKeys.Tab, out var tab);
        target.AdapterContext.TryGetValue(HerdrAdapterContextKeys.Pane, out var pane);
        target.AdapterContext.TryGetValue(HerdrAdapterContextKeys.AgentTarget, out var agentTarget);
        locator = new HerdrTargetLocator(
            Workspace: workspace,
            Tab: string.IsNullOrEmpty(tab) ? null : tab,
            Pane: string.IsNullOrEmpty(pane) ? null : pane,
            AgentTarget: string.IsNullOrEmpty(agentTarget) ? null : agentTarget,
            HostAttachmentIdKind: target.HostAttachmentIdKind,
            HostAttachmentId: target.HostAttachmentId);
        return true;
    }

    /// <summary>§7.4 <see cref="PartialCloseScope"/> → adapter-internal
    /// locator for the scoped close, returning <c>false</c> when
    /// <see cref="PartialCloseScope.ScopeKind"/> is not an
    /// adapter-defined Herdr scope kind. Refusing an unknown kind up
    /// front (rather than falling through to the parent locator) is
    /// what stops a stale/foreign <c>ScopeId</c> from being closed as
    /// if it belonged to the parent target — §6.3's UNVERIFIED-safe
    /// stance forbids that mutation.</summary>
    private static bool TryScopeToLocator(HerdrTargetLocator parent, PartialCloseScope scope, out HerdrTargetLocator scoped)
    {
        scoped = default!;
        var kind = scope.ScopeKind;
        string? tab = parent.Tab;
        string? pane = parent.Pane;
        string hostAttachmentIdKind;
        if (string.Equals(kind, "pane", System.StringComparison.Ordinal))
        {
            pane = scope.ScopeId;
            hostAttachmentIdKind = HerdrAdapterConstants.HostAttachmentIdKindPane;
        }
        else if (string.Equals(kind, "tab", System.StringComparison.Ordinal))
        {
            tab = scope.ScopeId;
            hostAttachmentIdKind = HerdrAdapterConstants.HostAttachmentIdKindTab;
        }
        else
        {
            return false;
        }
        if (string.IsNullOrEmpty(scope.ScopeId))
            return false;
        scoped = new HerdrTargetLocator(
            Workspace: parent.Workspace,
            Tab: tab,
            Pane: pane,
            AgentTarget: parent.AgentTarget,
            HostAttachmentIdKind: hostAttachmentIdKind,
            HostAttachmentId: scope.ScopeId);
        return true;
    }
}
