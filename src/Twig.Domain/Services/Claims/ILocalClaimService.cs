using System;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Claims;

/// <summary>
/// Deep-module surface for the local claim lifecycle (AB#737 §Interface
/// consumed by #739). The interface is deliberately surface-neutral: mint,
/// reclaim, release, validate, lookup, and label-update — no CLI verb, MCP
/// tool, prompt caller, or test fixture invents a public claim command; all
/// resolve this service through DI and pattern-match the discriminated
/// outcome types.
/// <para>
/// Every method is atomic on the local storage layer (AB#737 §Concurrency —
/// CAS-guarded writes). Mint/reclaim/release run the T2 ordering exactly:
/// local pending reservation → ADO projection → CAS activation → attachment
/// linkage on the mint/reclaim path; ADO clear → local terminalization →
/// attachment unlink on the release path. Failure at any stage surfaces the
/// named lifecycle outcome AB#737 §Named failure vocabulary enumerates and
/// leaves the registry / attachment in a consistent state: no ADO write
/// without a matching local record, no attachment reference to a
/// non-active row.
/// </para>
/// <para>
/// Validation is 100% offline — no network, no <c>System.AssignedTo</c>
/// read, no branch/PR lookup. A claim reached through the attachment either
/// matches the stored row byte-exact and is
/// <see cref="ClaimStates.Active"/>, or every command that depends on it
/// fails loud. Branch/PR links are irrelevant by construction: the design
/// contract has no field for either.
/// </para>
/// </summary>
internal interface ILocalClaimService
{
    Task<ClaimMintOutcome> MintAsync(MintClaimInput input, CancellationToken ct = default);

    Task<ClaimReclaimOutcome> ReclaimAsync(ReclaimClaimInput input, CancellationToken ct = default);

    Task<ClaimReleaseOutcome> ReleaseAsync(ReleaseClaimInput input, CancellationToken ct = default);

    Task<ClaimValidationOutcome> ValidateAsync(ClaimValidationInput input, CancellationToken ct = default);

    Task<ClaimLookupOutcome> LookupByTupleAsync(ClaimTupleQuery query, CancellationToken ct = default);

    Task<ClaimLabelUpdateOutcome> UpdateLabelAsync(UpdateClaimLabelInput input, CancellationToken ct = default);
}

/// <summary>Opaque high-entropy identifier generator seam. AB#737 §Canonical
/// identifier requires the value never be derived from label, holder, work
/// item id, or any other business fact; a ULID (Crockford-base32) or UUIDv4
/// implementation satisfies the contract. Kept as a seam so a test can
/// substitute a deterministic sequence.</summary>
internal interface IClaimIdGenerator
{
    string NewClaimId();
}

/// <summary>Opaque monotonically-fresh CAS token generator seam. AB#737
/// §CAS token requires every write to mint a new value that differs from
/// every prior value on the same row. A ULID or UUIDv7 is a fine
/// realization; readers MUST NOT interpret the token.</summary>
internal interface IClaimCasTokenGenerator
{
    string NewCasToken();
}

/// <summary>Runtime-resolved claim holder seam. Supplies the (identity,
/// displayName) pair the mint/reclaim path captures into the claim record
/// and forwards to <see cref="IAdoClaimProjection.ProjectHolderAsync"/>. The
/// resolver is authoritative — no lifecycle path infers holder from OS
/// username, ambient identity, or a config default.</summary>
internal interface IClaimHolderResolver
{
    Task<Result<ClaimHolderDescriptor>> ResolveAsync(CancellationToken ct = default);
}
