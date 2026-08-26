using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Public projection over the internal primary-scope attachment service. The
/// service itself is internal (it is a DI-only implementation detail); this
/// interface is what public surfaces — the CLI's <c>twig show</c>-family, the
/// MCP status tool — resolve when they need to render the primary scope block
/// on the status projection.
/// <para>
/// The result payload carries only presentation-ready fields. Missing surface
/// (unmanaged worktree or unattached) is signalled by the two booleans on
/// <see cref="StatusProjection"/>; a named storage failure (§8 of AB#736 —
/// layout marker missing, drift, connection mismatch, etc.) is carried through
/// on <see cref="StatusProjection.FailureCode"/> so the surface renders the
/// repair hint rather than silently degrading to "unmanaged".
/// </para>
/// </summary>
public interface IAttachmentStatusProjection
{
    Task<StatusProjection> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Public status payload for the AB#738 attachment surface. A plain immutable
/// class so it stays PublicAPI-friendly across every downstream reference; a
/// record would generate op_Equality, deconstruct, and a clone method that would
/// need per-member public-API tracking without adding contract value here.
/// </summary>
public sealed class StatusProjection
{
    public StatusProjection(
        bool isManagedWorktree,
        bool hasPrimaryScope,
        int? primaryScopeWorkItemId,
        string? primaryScopeTitle,
        string? primaryScopeType,
        string? failureCode = null)
    {
        IsManagedWorktree = isManagedWorktree;
        HasPrimaryScope = hasPrimaryScope;
        PrimaryScopeWorkItemId = primaryScopeWorkItemId;
        PrimaryScopeTitle = primaryScopeTitle;
        PrimaryScopeType = primaryScopeType;
        FailureCode = failureCode;
    }

    public bool IsManagedWorktree { get; }
    public bool HasPrimaryScope { get; }
    public int? PrimaryScopeWorkItemId { get; }
    public string? PrimaryScopeTitle { get; }
    public string? PrimaryScopeType { get; }
    /// <summary>
    /// Named storage failure identifier when the underlying read raised one
    /// (AB#736 §8 — e.g. <c>layout-marker-missing</c>,
    /// <c>worktree-fingerprint-drift</c>, <c>attachment-connection-mismatch</c>,
    /// <c>worktree-not-registered</c>). <c>null</c> on the success paths.
    /// Surfaces MUST render this when non-null so the operator sees the repair
    /// hint; converting it to "unmanaged" would hide corruption.
    /// </summary>
    public string? FailureCode { get; }
}

/// <summary>
/// Surface-neutral mutation seam for the primary-scope attachment: attach,
/// switch, detach, read, and require-active-claim. Exposed publicly so any
/// surface — a CLI verb, an MCP tool, a TUI panel, an integration test —
/// drives the same behavior through DI without inventing a public command
/// name. AB#739 will consume the same interface: minting a claim reads the
/// current attachment and requires a matching primary scope.
/// <para>
/// Every mutation returns a <see cref="Result"/>; every failure carries a
/// named identifier through <see cref="Result.Error"/> (either an
/// <see cref="Services.Attachment.AttachmentFailure"/> code or a raw AB#736
/// §8 storage identifier). Cancellation propagates as
/// <see cref="System.OperationCanceledException"/> — the surface owns retry
/// and reporting.
/// </para>
/// </summary>
public interface IPrimaryScopeAttachmentService
{
    /// <summary>Attach the given work item as the primary scope. Refuses when
    /// the checkout is not a managed worktree, when a scope is already
    /// attached (require explicit <see cref="SwitchAsync"/>), when the work
    /// item is unknown, and when the profile's type allow-set is unavailable
    /// or rejects the type. No claim is minted. Nothing is written on any
    /// refusal path.</summary>
    Task<Result> AttachAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Explicit switch: replace the current primary scope with a new
    /// one. The eligibility gate still runs; the new scope MUST be
    /// eligible. Preserves the current active-claim reference untouched.</summary>
    Task<Result> SwitchAsync(int newWorkItemId, CancellationToken ct = default);

    /// <summary>Clear the primary scope. Idempotent — a no-op when nothing
    /// is attached. Preserves the current active-claim reference (AB#739
    /// owns claim lifecycle).</summary>
    Task<Result> DetachAsync(CancellationToken ct = default);

    /// <summary>Fail-loud validation used by callers that require an active
    /// claim for a specific scope. Emits <c>ScopeNotPrimary</c> when the
    /// requested scope is not this worktree's primary scope and
    /// <c>ClaimNotFoundForScope</c> when the primary scope carries no active
    /// claim reference. Never inspects a claim registry; that read is
    /// AB#739's.</summary>
    Task<Result> RequireActiveClaimForScopeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Read the current status projection. Same shape as
    /// <see cref="IAttachmentStatusProjection.ReadAsync"/> — surfaces bind
    /// either interface as they prefer.</summary>
    Task<StatusProjection> ReadStatusAsync(CancellationToken ct = default);
}
