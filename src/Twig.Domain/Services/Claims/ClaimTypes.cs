using System.Collections.Generic;

namespace Twig.Domain.Services.Claims;

/// <summary>
/// Immutable projection of one system-local claim row (AB#737 §Record shape).
/// The public shape carries every field a caller needs to render, diagnose,
/// or supersede a claim without going back through the storage seam. The
/// <see cref="CasToken"/> is opaque to callers — it exists to survive a
/// round-trip through <c>UpdateLabelAsync</c> — but not interpreted; the
/// mint/reclaim/release paths never expose the token to the surface layer.
/// </summary>
/// <remarks>
/// The record is process-agnostic: <see cref="HolderIdentity"/> is whatever
/// the runtime resolver supplied at mint time; <see cref="PrimaryScopeKind"/>
/// is the opaque discriminator the profile declared; no ADO type, state, or
/// person name is baked into the shape. AB#737 §Cross-cutting rules requires
/// no lookup by <see cref="Label"/>, <see cref="HolderDisplay"/>, or
/// <see cref="Notes"/> — every downstream code path keys on
/// <see cref="ClaimId"/> or on the
/// (<see cref="ConnectionRef"/>, <see cref="PrimaryScopeKind"/>,
/// <see cref="PrimaryScopeId"/>) tuple.
/// </remarks>
internal sealed record ClaimRecord(
    int SchemaVersion,
    string ClaimId,
    string? Label,
    string ConnectionRef,
    string PrimaryScopeId,
    string PrimaryScopeKind,
    string HolderIdentity,
    string? HolderDisplay,
    string? HolderUniqueName,
    string WorktreeFingerprint,
    string State,
    string Origin,
    int LeaseGeneration,
    System.DateTimeOffset? ExpiresAt,
    System.DateTimeOffset CreatedAt,
    System.DateTimeOffset? ActivatedAt,
    System.DateTimeOffset? ReleasedAt,
    string? SupersededByClaimId,
    string? ReleaseReason,
    string? Notes,
    string CasToken);

/// <summary>Input payload for
/// <see cref="ILocalClaimService.MintAsync(MintClaimInput, System.Threading.CancellationToken)"/>.
/// Callers never supply a <c>claimId</c> — the mint operation generates it
/// (AB#737 §Interface). <see cref="AdoProjection"/> is the abstract seam that
/// projects the resolved holder onto <c>System.AssignedTo</c>; it never leaks
/// process-specific state or type names into the claim path.</summary>
internal sealed record MintClaimInput(
    string ConnectionRef,
    string PrimaryScopeKind,
    string PrimaryScopeId,
    string WorktreeFingerprint,
    string HolderIdentity,
    string? HolderDisplay,
    string? HolderUniqueName,
    string? Label,
    string? Notes,
    IAdoClaimProjection AdoProjection);

/// <summary>Input payload for
/// <see cref="ILocalClaimService.ReclaimAsync(ReclaimClaimInput, System.Threading.CancellationToken)"/>.
/// Extends <see cref="MintClaimInput"/> with the required
/// <see cref="AllowSupersede"/> flag AB#737 §Explicit reclaim fixes:
/// <c>false</c> behaves like a fresh mint over a released or missing row;
/// <c>true</c> supersedes an existing active row for the same tuple and
/// refuses if no active row exists in this installation.</summary>
internal sealed record ReclaimClaimInput(
    string ConnectionRef,
    string PrimaryScopeKind,
    string PrimaryScopeId,
    string WorktreeFingerprint,
    string HolderIdentity,
    string? HolderDisplay,
    string? HolderUniqueName,
    string? Label,
    string? Notes,
    bool AllowSupersede,
    IAdoClaimProjection AdoProjection);

/// <summary>Input payload for
/// <see cref="ILocalClaimService.ReleaseAsync(ReleaseClaimInput, System.Threading.CancellationToken)"/>.
/// The caller supplies the <see cref="ClaimId"/> to release; the release path
/// requires the row to be <see cref="ClaimStates.Active"/> and owned by this
/// installation (AB#737 §Release). The
/// <see cref="AdoProjection"/> seam is the same one used at mint; release
/// clears <c>System.AssignedTo</c> before the local row terminalizes.</summary>
internal sealed record ReleaseClaimInput(
    string ClaimId,
    IAdoClaimProjection AdoProjection);

/// <summary>Input payload for
/// <see cref="ILocalClaimService.ValidateAsync(ClaimValidationInput, System.Threading.CancellationToken)"/>.
/// Every #739 code path that requires a claim runs this before acting; the
/// (<see cref="ConnectionRef"/>, <see cref="PrimaryScopeKind"/>,
/// <see cref="PrimaryScopeId"/>) tuple MUST byte-match the stored row. A
/// tuple disagreement is a corruption signal (
/// <see cref="ClaimValidationOutcome.TupleMismatch"/>) — Twig never infers
/// ownership from a matching partial tuple.</summary>
internal sealed record ClaimValidationInput(
    string ClaimId,
    string ConnectionRef,
    string PrimaryScopeKind,
    string PrimaryScopeId);

/// <summary>Query payload for
/// <see cref="ILocalClaimService.LookupByTupleAsync(ClaimTupleQuery, System.Threading.CancellationToken)"/>.
/// Reads at most one row in
/// { <see cref="ClaimStates.Pending"/>, <see cref="ClaimStates.Active"/> }
/// for the tuple — the reserved set AB#736 §9.4 exposes on
/// <c>FindReserved</c>. Used by mint contention diagnostics and reclaim
/// precondition checks; never widens to terminal rows.</summary>
internal sealed record ClaimTupleQuery(
    string ConnectionRef,
    string PrimaryScopeKind,
    string PrimaryScopeId);

/// <summary>Input payload for
/// <see cref="ILocalClaimService.UpdateLabelAsync(UpdateClaimLabelInput, System.Threading.CancellationToken)"/>.
/// Rewrites the label under CAS. AB#737 §Human-readable label makes label
/// updates CAS-guarded exactly like every other write; a mismatch surfaces
/// <see cref="ClaimLabelUpdateOutcome.ConcurrentClaimWrite"/>.</summary>
internal sealed record UpdateClaimLabelInput(
    string ClaimId,
    string NewLabel,
    string ExpectedCasToken);

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.MintAsync(MintClaimInput, System.Threading.CancellationToken)"/>.
/// Each concrete variant is one branch AB#737 §Named failure vocabulary
/// enumerates for the mint path; callers pattern-match. The single-arm
/// success variant carries the fully hydrated <see cref="ClaimRecord"/> so
/// the caller does not re-read after mint.</summary>
internal abstract record ClaimMintOutcome
{
    private ClaimMintOutcome() { }

    public sealed record Succeeded(ClaimRecord Claim) : ClaimMintOutcome;
    public sealed record PrimaryScopeAlreadyClaimed(string ExistingClaimId, string ExistingState) : ClaimMintOutcome;
    public sealed record AdoProjectionFailed(string Underlying) : ClaimMintOutcome;
    public sealed record ConcurrentClaimWrite(string Underlying) : ClaimMintOutcome;
    public sealed record AttachmentLinkFailed(string Underlying, ClaimRecord Claim) : ClaimMintOutcome;
    public sealed record HolderUnavailable(string Underlying) : ClaimMintOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimMintOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimMintOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimMintOutcome;
}

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.ReclaimAsync(ReclaimClaimInput, System.Threading.CancellationToken)"/>.
/// Extends the mint failure family with
/// <see cref="ClaimNotActive"/> — surfaced when <c>allowSupersede = true</c>
/// but the tuple has no active row in this installation.</summary>
internal abstract record ClaimReclaimOutcome
{
    private ClaimReclaimOutcome() { }

    public sealed record Succeeded(ClaimRecord NewClaim, ClaimRecord? SupersededClaim) : ClaimReclaimOutcome;
    public sealed record PrimaryScopeAlreadyClaimed(string ExistingClaimId, string ExistingState) : ClaimReclaimOutcome;
    public sealed record ClaimNotActive(string CurrentState) : ClaimReclaimOutcome;
    public sealed record AdoProjectionFailed(string Underlying) : ClaimReclaimOutcome;
    public sealed record ConcurrentClaimWrite(string Underlying) : ClaimReclaimOutcome;
    public sealed record AttachmentLinkFailed(string Underlying, ClaimRecord Claim) : ClaimReclaimOutcome;
    public sealed record HolderUnavailable(string Underlying) : ClaimReclaimOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimReclaimOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimReclaimOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimReclaimOutcome;
}

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.ReleaseAsync(ReleaseClaimInput, System.Threading.CancellationToken)"/>.
/// AB#737 §Release ordering: ADO clear runs before local terminalization. A
/// failed ADO clear leaves the row <see cref="ClaimStates.Active"/> so the
/// operator observes both the ADO error and the still-active claim rather
/// than a phantom-released row.</summary>
internal abstract record ClaimReleaseOutcome
{
    private ClaimReleaseOutcome() { }

    public sealed record Succeeded(ClaimRecord Released) : ClaimReleaseOutcome;
    public sealed record ClaimNotFound(string ClaimId) : ClaimReleaseOutcome;
    public sealed record ClaimNotActive(string ClaimId, string CurrentState) : ClaimReleaseOutcome;
    public sealed record ReleaseAdoProjectionFailed(string Underlying) : ClaimReleaseOutcome;
    public sealed record ConcurrentClaimWrite(string Underlying) : ClaimReleaseOutcome;
    public sealed record AttachmentUnlinkFailed(string Underlying, ClaimRecord Released) : ClaimReleaseOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimReleaseOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimReleaseOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimReleaseOutcome;
}

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.ValidateAsync(ClaimValidationInput, System.Threading.CancellationToken)"/>.
/// Every #739 code path that touches a claim runs this before acting. The
/// call is 100% offline: it consults the local registry only, never a
/// network, never <c>System.AssignedTo</c>, and never any branch or PR
/// state.</summary>
internal abstract record ClaimValidationOutcome
{
    private ClaimValidationOutcome() { }

    public sealed record Succeeded(ClaimRecord Claim) : ClaimValidationOutcome;
    public sealed record ClaimNotFound(string ClaimId) : ClaimValidationOutcome;
    public sealed record ClaimNotActive(string ClaimId, string CurrentState) : ClaimValidationOutcome;
    public sealed record TupleMismatch(ClaimRecord Claim) : ClaimValidationOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimValidationOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimValidationOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimValidationOutcome;
}

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.LookupByTupleAsync(ClaimTupleQuery, System.Threading.CancellationToken)"/>.
/// Returns at most one row in <c>{ pending, active }</c>. The
/// <see cref="NotFound"/> variant is a first-class success signal — the
/// tuple is unclaimed.</summary>
internal abstract record ClaimLookupOutcome
{
    private ClaimLookupOutcome() { }

    public sealed record Found(ClaimRecord Claim) : ClaimLookupOutcome;
    public sealed record NotFound() : ClaimLookupOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimLookupOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimLookupOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimLookupOutcome;
}

/// <summary>Discriminated outcome of
/// <see cref="ILocalClaimService.UpdateLabelAsync(UpdateClaimLabelInput, System.Threading.CancellationToken)"/>.
/// Never changes state; only the <see cref="ClaimRecord.Label"/> and
/// <see cref="ClaimRecord.CasToken"/>.</summary>
internal abstract record ClaimLabelUpdateOutcome
{
    private ClaimLabelUpdateOutcome() { }

    public sealed record Succeeded(ClaimRecord Claim) : ClaimLabelUpdateOutcome;
    public sealed record ClaimNotFound(string ClaimId) : ClaimLabelUpdateOutcome;
    public sealed record ConcurrentClaimWrite(string Underlying) : ClaimLabelUpdateOutcome;
    public sealed record SchemaDrift(int SchemaVersion) : ClaimLabelUpdateOutcome;
    public sealed record StorageUnavailable(string Underlying) : ClaimLabelUpdateOutcome;
    public sealed record InvalidRequest(string Reason) : ClaimLabelUpdateOutcome;
}
