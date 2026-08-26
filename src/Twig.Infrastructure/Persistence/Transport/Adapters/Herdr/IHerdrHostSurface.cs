using System.Threading;
using System.Threading.Tasks;

namespace Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// The Herdr adapter's grounded observation surface, expressed as an
/// abstraction so the adapter never spawns a live process from a unit
/// test and so §5.1's poll-only obligation is honoured by construction.
/// <para>
/// Every method below MUST satisfy:
/// </para>
/// <list type="number">
///   <item>Bounded — the implementation MUST complete within the
///     supplied budget for status / liveness / preflight paths, and
///     never omit <c>--timeout</c> when it does invoke a Herdr blocking
///     primitive (§5.1's "MUST always pass an explicit
///     <c>--timeout</c>" rule). Missing that guard is a §12.2 MUST-NOT.
///   </item>
///   <item>Named — timeouts become an <c>Outcome</c> of
///     <see cref="HerdrOperationOutcome.Timeout"/>, adapter/host
///     failures become <see cref="HerdrOperationOutcome.Failed"/>, and
///     successful reads become <see cref="HerdrOperationOutcome.Ok"/>.
///     The surface NEVER throws for a Herdr-side error; it names it.
///   </item>
///   <item>Read-only for observation paths — <see cref="QueryStatusAsync"/>
///     and <see cref="QueryLivenessAsync"/> MUST NOT reach any
///     mutating Herdr verb (§9.1 R11–R15). Close is the only mutation,
///     issued only from <see cref="CloseAsync"/> in a single unpiped
///     call (§12.2).</item>
/// </list>
/// </summary>
internal interface IHerdrHostSurface
{
    /// <summary>§5.1 bounded snapshot query for status. Callers must
    /// clamp <paramref name="budgetMs"/> before calling — this method
    /// does not validate. Under the hood the implementation reaches
    /// <c>herdr api snapshot</c> / <c>herdr pane current --current</c> /
    /// <c>herdr agent explain &lt;target&gt; --json</c> per §12.2. It
    /// NEVER reaches <c>herdr agent wait</c> without <c>--timeout</c>,
    /// and NEVER reaches any mutating verb.</summary>
    Task<HerdrStatusReadout> QueryStatusAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct);

    /// <summary>§5.1 bounded snapshot query for liveness. Same bounds
    /// as status; the difference is which Herdr surface is polled
    /// (<c>pane current</c> / <c>agent explain</c>).</summary>
    Task<HerdrLivenessReadout> QueryLivenessAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct);

    /// <summary>§12.2 mandated preflight cross-check on
    /// workspace/tab/pane ids from
    /// <see cref="TransportAdapterTarget.AdapterContext"/> against a
    /// fresh <c>herdr api snapshot</c>. Moved panes get a new id and
    /// stale caches MUST be caught here — the close never fires when
    /// the target no longer exists at the recorded coordinates.
    /// <para>
    /// This preflight is invoked ONLY from
    /// <see cref="ITransportAdapter.CloseAsync"/> and
    /// <see cref="ITransportAdapter.PartialCloseAsync"/>. §1.1(c)
    /// forbids any implicit reach from a probe, detach, or read path.
    /// </para></summary>
    Task<HerdrPreflightReadout> PreflightCloseAsync(HerdrTargetLocator target, CancellationToken ct);

    /// <summary>§12.2 close — exactly one unpiped
    /// <c>herdr tab close &lt;tab_id&gt;</c> or
    /// <c>herdr pane close &lt;pane_id&gt;</c>. Piping a mutating
    /// Herdr verb is forbidden because the pipeline exit status hides
    /// its failure; implementations MUST NOT wrap this in a shell
    /// pipeline. Reachable only via
    /// <see cref="ITransportAdapter.CloseAsync"/> or
    /// <see cref="ITransportAdapter.PartialCloseAsync"/>.</summary>
    Task<HerdrCloseReadout> CloseAsync(HerdrTargetLocator target, CancellationToken ct);

    /// <summary>§6.3 partial-close confirmation. Herdr's partial-close
    /// outcome is UNVERIFIED from the read-only surface, so this
    /// method returns <see cref="HerdrOperationOutcome.Failed"/> or a
    /// <see cref="HerdrRemainingSummary"/> that carries "unknown"
    /// whenever the adapter cannot independently confirm the
    /// post-state. Implementations MUST NOT assert a value they cannot
    /// observe (§6.3).</summary>
    Task<HerdrRemainingReadout> ObservePartialCloseRemainingAsync(HerdrTargetLocator parent, CancellationToken ct);
}

/// <summary>
/// Adapter-internal locator built from
/// <see cref="TransportAdapterTarget.AdapterContext"/>. Every close /
/// probe path routes through this so the "moved pane gets a new id"
/// invariant (§7.4) is honoured — the ids inside are opaque strings the
/// adapter never rewrites.
/// </summary>
internal sealed record HerdrTargetLocator(
    string Workspace,
    string? Tab,
    string? Pane,
    string? AgentTarget,
    string HostAttachmentIdKind,
    string HostAttachmentId);

/// <summary>Outcome of a bounded Herdr operation. Fixed three-value
/// enum so the adapter can branch by table.</summary>
internal enum HerdrOperationOutcome
{
    /// <summary>Bounded read succeeded within budget.</summary>
    Ok = 0,

    /// <summary>Bounded read exceeded its budget (§5.2). Returned as a
    /// named observation, never an exception.</summary>
    Timeout = 1,

    /// <summary>Bounded read completed abnormally: adapter code threw
    /// or the underlying Herdr command exited non-zero for a
    /// non-timeout reason. Surfaced as
    /// <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/>
    /// / <see cref="TransportAttachmentFailure.PartialCloseAdapterFailed"/>
    /// / <see cref="TransportAttachmentFailure.CloseAdapterFailed"/>
    /// depending on the caller.</summary>
    Failed = 2,
}

/// <summary>Grounded Herdr status vocabulary from
/// <c>local://host-surfaces.md</c>. Table-mapped to
/// <see cref="RecordedStatus"/> by §4.2 — §4.3 forbids any inference
/// from <see cref="Idle"/> to <see cref="RecordedStatus.Done"/> on any
/// path.</summary>
internal enum HerdrHostStatus
{
    Idle = 0,
    Working = 1,
    Blocked = 2,
    Done = 3,
    Unknown = 4,
}

/// <summary>Bounded status readout. When <see cref="Outcome"/> is
/// <see cref="HerdrOperationOutcome.Ok"/>, <see cref="Status"/> carries
/// the host value that §4.2 will map. Otherwise §5.2 dictates the
/// bounded-failure observation the adapter emits.</summary>
internal readonly record struct HerdrStatusReadout(
    HerdrOperationOutcome Outcome,
    HerdrHostStatus Status,
    System.DateTimeOffset RecordedAt);

/// <summary>Bounded liveness readout. Presence is populated only when
/// <see cref="Outcome"/> is <see cref="HerdrOperationOutcome.Ok"/>.
/// </summary>
internal readonly record struct HerdrLivenessReadout(
    HerdrOperationOutcome Outcome,
    TransportLivenessPresence Presence,
    System.DateTimeOffset RecordedAt);

/// <summary>Preflight cross-check outcome. <see cref="Confirmed"/> is
/// <c>true</c> when the workspace/tab/pane ids still resolve to the
/// same target; <c>false</c> means moved or gone — the caller MUST
/// refuse the close per §12.2.</summary>
internal readonly record struct HerdrPreflightReadout(
    HerdrOperationOutcome Outcome,
    bool Confirmed);

/// <summary>Close readout. Success is the pure OK path; any failure is
/// <see cref="HerdrOperationOutcome.Failed"/>. Timeout is not applied
/// to close — the caller invoked it explicitly and the host is
/// expected to complete or fail.</summary>
internal readonly record struct HerdrCloseReadout(
    HerdrOperationOutcome Outcome);

/// <summary>Partial-close remaining-observation readout. See
/// <see cref="HerdrRemainingSummary"/>; <see cref="Summary"/> is
/// meaningful only when <see cref="Outcome"/> is
/// <see cref="HerdrOperationOutcome.Ok"/>.</summary>
internal readonly record struct HerdrRemainingReadout(
    HerdrOperationOutcome Outcome,
    HerdrRemainingSummary Summary);

/// <summary>The three post-partial-close observation states §6.3 fixes.
/// <see cref="Unknown"/> is the UNVERIFIED-safe fallback the adapter
/// MUST return when it cannot independently confirm.</summary>
internal enum HerdrRemainingSummary
{
    Unknown = 0,
    Subset = 1,
    None = 2,
}
