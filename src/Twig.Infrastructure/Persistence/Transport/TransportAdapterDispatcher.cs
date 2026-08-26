using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §3.2 dispatch layer. Wraps <see cref="ITransportAdapterRegistry"/>
/// with the per-operation absent-capability degradation table:
/// <list type="table">
///   <listheader><term>Capability</term><description>Declared / not declared</description></listheader>
///   <item><term>StatusReporting</term><description>Invoke adapter / return unobservable observation.</description></item>
///   <item><term>LivenessProbe</term><description>Invoke adapter / return unknown-presence observation.</description></item>
///   <item><term>Detach</term><description>Invoke adapter / <see cref="Result.Ok"/> (record-level detach always available).</description></item>
///   <item><term>Close</term><description>Invoke adapter / <see cref="TransportAttachmentFailure.CloseNotSupported"/>.</description></item>
///   <item><term>PartialClose</term><description>Invoke adapter / <see cref="TransportAttachmentFailure.PartialCloseNotSupported"/>.</description></item>
/// </list>
/// <para>
/// §5.1 clamp enforcement lives here: a caller-supplied
/// <see cref="TransportProbeOptions.TimeoutMs"/> outside <c>[100, 30000]</c>
/// ms raises <see cref="TransportAttachmentFailure.ProbeBudgetInvalid"/>
/// before the adapter is invoked. §5.3 freshness rule (bounded-failure
/// observations are always stale) is enforced by the adapter for
/// timeouts; the dispatcher's <see cref="Fresh"/> helper is exposed for
/// adapter implementations that want a shared freshness computation.
/// </para>
/// <para>
/// §5.2 timeout carve-out: this dispatcher does NOT wrap adapter
/// invocations in a <see cref="System.Threading.Tasks.Task.WaitAsync(System.TimeSpan)"/>
/// budget cap. The contract says "the adapter honoured its budget" —
/// i.e. each declared adapter is responsible for bounding its own
/// probe under §5.1. The dispatcher's role is (a) validating the
/// caller-supplied clamp, (b) applying the absent-capability
/// degradation, (c) refusing to invoke an undeclared capability, and
/// (d) folding a non-cancellation adapter exception into the
/// contract-named <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/>
/// so §5.2's "never an exception" guarantee holds even when the
/// adapter code throws synchronously or asynchronously.
/// </para>
/// <para>
/// §6.1 / §6.2 tombstone coordination: <see cref="DetachAsync"/> and
/// <see cref="CloseAsync"/> take an <c>expectedRevision</c> and call
/// <see cref="ITransportAttachmentStore"/> to write the §8.2 tombstone
/// in one core-owned operation, so no caller can accidentally leave
/// the record's state and the host state out of sync. §6.1's "detach
/// tombstone is written even when the adapter fails" rule is enforced
/// here; §6.2's "close tombstone only after host-close success" rule
/// likewise.
/// </para>
/// <para>
/// §9.1 event-boundary invariant: none of the read/probe/detach paths
/// below reach <see cref="ITransportAdapter.CloseAsync"/> or
/// <see cref="ITransportAdapter.PartialCloseAsync"/>. Close and
/// PartialClose are reachable ONLY when the caller explicitly invokes
/// <see cref="CloseAsync"/> or <see cref="PartialCloseAsync"/> on this
/// dispatcher; §1.1(c) reverse invariant confirms.
/// </para>
/// </summary>
internal sealed class TransportAdapterDispatcher
{
    private readonly ITransportAdapterRegistry _registry;
    private readonly ITransportAttachmentStore _store;
    private readonly TimeProvider _clock;

    public TransportAdapterDispatcher(
        ITransportAdapterRegistry registry,
        ITransportAttachmentStore store,
        TimeProvider clock)
    {
        _registry = registry;
        _store = store;
        _clock = clock;
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.StatusReporting"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <c>Result.Ok(TransportStatusObservation { status = Unobservable,
    /// recordedAt = null, freshness = Unobservable })</c>.
    /// <para>
    /// A non-cancellation exception thrown by the adapter (synchronously
    /// or asynchronously) becomes
    /// <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/> per
    /// §5.2. Caller-driven cancellation is preserved; the adapter's own
    /// bounded-failure observation (e.g. <c>ProbeTimeout</c>) is left
    /// alone as <see cref="Result.Ok"/>.</para></summary>
    public async Task<Result<TransportStatusObservation>> ReportStatusAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct)
    {
        if (!TransportProbeBudget.IsValid(options?.TimeoutMs))
            return Result.Fail<TransportStatusObservation>(TransportAttachmentFailure.ProbeBudgetInvalid);
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess)
            return Result.Fail<TransportStatusObservation>(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.StatusReporting))
        {
            return Result.Ok(new TransportStatusObservation(
                Status: RecordedStatus.Unobservable,
                RecordedAt: null,
                Freshness: TransportFreshness.Unobservable,
                TimeoutError: null));
        }
        try
        {
            return await adapter.ReportStatusAsync(target, options, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportStatusObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.LivenessProbe"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <c>Result.Ok(TransportLivenessObservation { presence = Unknown,
    /// recordedAt = null, freshness = Unobservable })</c>.
    /// <para>
    /// A non-cancellation exception thrown by the adapter (synchronously
    /// or asynchronously) becomes
    /// <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/> per
    /// §5.2. Caller-driven cancellation is preserved; the adapter's own
    /// bounded-failure observation (e.g. <c>ProbeTimeout</c>) is left
    /// alone as <see cref="Result.Ok"/>.</para></summary>
    public async Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct)
    {
        if (!TransportProbeBudget.IsValid(options?.TimeoutMs))
            return Result.Fail<TransportLivenessObservation>(TransportAttachmentFailure.ProbeBudgetInvalid);
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess)
            return Result.Fail<TransportLivenessObservation>(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.LivenessProbe))
        {
            return Result.Ok(new TransportLivenessObservation(
                Presence: TransportLivenessPresence.Unknown,
                RecordedAt: null,
                Freshness: TransportFreshness.Unobservable,
                Error: null));
        }
        try
        {
            return await adapter.ProbeLivenessAsync(target, options, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportLivenessObservation>(TransportAttachmentFailure.ProbeAdapterFailed);
        }
    }

    /// <summary>§3.2 / §6.1 coordinating detach. On every path this
    /// method calls
    /// <see cref="ITransportAttachmentStore.DetachAsync(long, CancellationToken)"/>
    /// with <paramref name="expectedRevision"/> to write the §8.2
    /// tombstone — including when the adapter does not declare the
    /// capability AND when a declared adapter returns
    /// <see cref="TransportAttachmentFailure.DetachAdapterFailed"/>.
    /// This is the §6.1 rule verbatim: detach is idempotent from the
    /// record's perspective and the CAS revision advances even under
    /// adapter failure so a subsequent reattach cannot ABA-collide.
    /// <para>
    /// Return shape:
    /// </para>
    /// <list type="bullet">
    ///   <item>Store write failed → return the store's failure (e.g.
    ///     <c>transport-version-mismatch</c>,
    ///     <c>transport-atomic-write-failed</c>).</item>
    ///   <item>Store write succeeded, adapter failed →
    ///     <see cref="TransportAttachmentFailure.DetachAdapterFailed"/>
    ///     with the tombstone already persisted.</item>
    ///   <item>Store write succeeded, adapter succeeded / undeclared →
    ///     the store's <see cref="TransportWriteOutcome"/> as
    ///     <see cref="Result.Ok"/>.</item>
    /// </list></summary>
    public async Task<Result<TransportWriteOutcome>> DetachAsync(
        TransportAdapterTarget target,
        long expectedRevision,
        CancellationToken ct)
    {
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess)
            return Result.Fail<TransportWriteOutcome>(adapterResult.Error);
        var adapter = adapterResult.Value;

        // Adapter phase — declared capability only.
        string? adapterFailure = null;
        if (adapter.Capabilities.Contains(TransportCapability.Detach))
        {
            Result adapterOutcome;
            try
            {
                adapterOutcome = await adapter.DetachAsync(target, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                adapterOutcome = Result.Fail(TransportAttachmentFailure.DetachAdapterFailed);
            }
            if (!adapterOutcome.IsSuccess)
                adapterFailure = adapterOutcome.Error;
        }

        // Store phase — ALWAYS writes the tombstone per §6.1.
        var storeOutcome = await _store.DetachAsync(expectedRevision, ct).ConfigureAwait(false);
        if (!storeOutcome.IsSuccess)
            return storeOutcome;

        // §6.1: if the adapter failed, the tombstone still stands but
        // the caller sees the adapter failure identifier.
        if (adapterFailure is not null)
            return Result.Fail<TransportWriteOutcome>(adapterFailure);

        return storeOutcome;
    }

    /// <summary>§3.2 / §6.2 coordinating close. Runs the host-close
    /// through the adapter first; only on adapter success does the
    /// §8.2 tombstone write, per §6.2 "after a successful close, the
    /// Transport Attachment tombstone is written by §8 inside the same
    /// transaction". Explicit caller invocation only (§1.1(c), §6.2).
    /// <para>Return shape:</para>
    /// <list type="bullet">
    ///   <item>Caller's <paramref name="expectedRevision"/> disagrees
    ///     with the store's current envelope revision →
    ///     <see cref="TransportAttachmentFailure.VersionMismatch"/> BEFORE
    ///     any adapter close is attempted. §6.2 requires the host and
    ///     record state stay coupled; a stale caller MUST NOT close a
    ///     live host and then discover the CAS lost.</item>
    ///   <item>Adapter threw / returned Fail →
    ///     <see cref="TransportAttachmentFailure.CloseAdapterFailed"/>
    ///     (or the adapter's own error identifier), no tombstone.</item>
    ///   <item>Adapter succeeded → store writes the tombstone; the
    ///     store's outcome is returned.</item>
    /// </list></summary>
    public async Task<Result<TransportWriteOutcome>> CloseAsync(
        TransportAdapterTarget target,
        long expectedRevision,
        CancellationToken ct)
    {
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess)
            return Result.Fail<TransportWriteOutcome>(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.Close))
            return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.CloseNotSupported);

        // §6.2 pre-mutation CAS preflight. The dispatcher reads the
        // current envelope revision BEFORE reaching adapter.CloseAsync
        // so a stale caller cannot close a live host and then lose the
        // tombstone to a version mismatch. The store's own CAS check
        // (§8.4) still fires under the write lock and is authoritative
        // if the on-disk revision moves between here and the store
        // write; the preflight is defence-in-depth against the common
        // "caller's expectedRevision is already stale" case.
        var readResult = await _store.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!readResult.IsSuccess)
            return Result.Fail<TransportWriteOutcome>(readResult.Error);
        if (readResult.Value.Revision != expectedRevision)
            return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.VersionMismatch);

        Result adapterOutcome;
        try
        {
            adapterOutcome = await adapter.CloseAsync(target, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.CloseAdapterFailed);
        }
        if (!adapterOutcome.IsSuccess)
            return Result.Fail<TransportWriteOutcome>(adapterOutcome.Error);

        // §6.2 same-transaction tombstone on adapter success.
        return await _store.CloseAsync(expectedRevision, ct).ConfigureAwait(false);
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.PartialClose"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <see cref="TransportAttachmentFailure.PartialCloseNotSupported"/>.
    /// Explicit caller invocation only (§1.1(c), §6.3).</summary>
    public async Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(
        TransportAdapterTarget target,
        PartialCloseScope scope,
        CancellationToken ct)
    {
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess)
            return Result.Fail<TransportPartialCloseOutcome>(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.PartialClose))
            return Result.Fail<TransportPartialCloseOutcome>(TransportAttachmentFailure.PartialCloseNotSupported);
        return await adapter.PartialCloseAsync(target, scope, ct).ConfigureAwait(false);
    }

    /// <summary>§3.2 / §7.1 client-bug rail: a caller invoking a
    /// capability whose name is NOT in the §3.3 catalogue at this
    /// schema version. Return this from a surface command that reads
    /// an untrusted capability name off the wire before dispatching.
    /// NEVER raised for one of the five §3.3 capabilities themselves —
    /// those degrade per the tables above.
    /// <para>
    /// §3.3's catalogue is exhaustive and deliberately EXCLUDES the two
    /// mandatory §3.1 common-denominator capabilities
    /// (<c>RecordIdentity</c>, <c>DescribeAdapter</c>). Those are not
    /// invocable via a <c>Capabilities</c> set and MUST NEVER be
    /// persisted in one — §2.2 row 6 already rejects them at the shape
    /// validator, and this method rejects them at the dispatch rail with
    /// <see cref="TransportAttachmentFailure.CapabilityNotDeclared"/>.
    /// A caller reaching either name here is the same client-bug case
    /// as any other non-catalogue string.
    /// </para></summary>
    public static Result CheckCapabilityName(string capabilityName)
    {
        if (string.IsNullOrEmpty(capabilityName))
            return Result.Fail(TransportAttachmentFailure.CapabilityNotDeclared);
        return TransportCapabilityExtensions.TryParse(capabilityName, out _)
            ? Result.Ok()
            : Result.Fail(TransportAttachmentFailure.CapabilityNotDeclared);
    }

    /// <summary>§5.3 freshness helper for adapter implementations.
    /// Bounded-failure observations (timeout etc.) MUST return
    /// <see cref="TransportFreshness.Stale"/> regardless of
    /// <paramref name="recordedAt"/>; this method is the pure
    /// timestamp rule for the successful-observation path.</summary>
    public TransportFreshness Fresh(System.DateTimeOffset recordedAt) =>
        TransportProbeBudget.Compute(recordedAt, _clock.GetUtcNow());
}
