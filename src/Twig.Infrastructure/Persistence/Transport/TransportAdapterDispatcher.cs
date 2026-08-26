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
/// degradation, and (c) refusing to invoke an undeclared capability;
/// wrapping the adapter's Task in a race would double-time-out and
/// silently rewrite the adapter's named observation.
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
    private readonly TimeProvider _clock;

    public TransportAdapterDispatcher(ITransportAdapterRegistry registry, TimeProvider clock)
    {
        _registry = registry;
        _clock = clock;
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.StatusReporting"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <c>Result.Ok(TransportStatusObservation { status = Unobservable,
    /// recordedAt = null, freshness = Unobservable })</c>.</summary>
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
        return await adapter.ReportStatusAsync(target, options, ct).ConfigureAwait(false);
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.LivenessProbe"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <c>Result.Ok(TransportLivenessObservation { presence = Unknown,
    /// recordedAt = null, freshness = Unobservable })</c>.</summary>
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
        return await adapter.ProbeLivenessAsync(target, options, ct).ConfigureAwait(false);
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.Detach"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <see cref="Result.Ok"/> (record-level detach always available;
    /// §6.1). Caller writes the tombstone via
    /// <see cref="ITransportAttachmentStore.DetachAsync"/>.</summary>
    public async Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct)
    {
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess) return Result.Fail(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.Detach))
            return Result.Ok();
        return await adapter.DetachAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>§3.2 dispatch for <see cref="TransportCapability.Close"/>.
    /// Adapter declared → invoke. Not declared → return
    /// <see cref="TransportAttachmentFailure.CloseNotSupported"/>.
    /// Explicit caller invocation only (§1.1(c), §6.2).</summary>
    public async Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct)
    {
        var adapterResult = _registry.Resolve(target.AdapterId);
        if (!adapterResult.IsSuccess) return Result.Fail(adapterResult.Error);
        var adapter = adapterResult.Value;
        if (!adapter.Capabilities.Contains(TransportCapability.Close))
            return Result.Fail(TransportAttachmentFailure.CloseNotSupported);
        return await adapter.CloseAsync(target, ct).ConfigureAwait(false);
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
    /// those degrade per the tables above.</summary>
    public static Result CheckCapabilityName(string capabilityName)
    {
        if (string.IsNullOrEmpty(capabilityName))
            return Result.Fail(TransportAttachmentFailure.CapabilityNotDeclared);
        if (TransportCapabilityExtensions.IsCommonDenominator(capabilityName))
            return Result.Ok(); // §3.1 common denominator — never dispatched, always implemented.
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
