using Twig.Domain.Common;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The system-tier registry seam AB#736 §9.4 fixes for the two downstream
/// tickets: AB#738 upserts the current managed worktree at init time and
/// verifies a non-retired matching row before it runs; AB#739 extends the
/// same store with claim rows keyed on the same worktree fingerprint.
/// <para>
/// Every method is atomic (§6.2 — SQLite WAL + <c>BEGIN IMMEDIATE</c>). A
/// storage failure surfaces the AB#736 §8 identifier verbatim through the
/// <see cref="Result"/> error string so the attachment service can route on
/// <c>system-store-locked</c>, <c>system-store-schema-mismatch</c>,
/// <c>worktree-not-registered</c>, <c>worktree-retired</c>,
/// <c>claim-duplicate-reserved</c>, or <c>claim-cas-mismatch</c>.
/// </para>
/// <para>
/// The claim-side and profile-cache surfaces are the T1 v1 shapes AB#739 and
/// AB#727 consume respectively. Storage carries the CAS token but does not
/// interpret it — the caller decides what the token means. No lifecycle
/// policy is realized here.
/// </para>
/// </summary>
internal interface ISystemWorktreeRegistry
{
    // ── Worktree registry (§9.4) ─────────────────────────────────────

    Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default);
    Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default);
    Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default);

    // ── Claims registry (§9.4, AB#739) ────────────────────────────────

    /// <summary>Insert a claim row with an opaque <paramref name="casToken"/>
    /// the caller will supply back on every future
    /// <see cref="UpdateClaimStateAsync"/>. Fails with
    /// <c>worktree-not-registered</c> when the fingerprint has no row;
    /// fails with <c>claim-duplicate-reserved</c> when a
    /// <c>pending</c>/<c>active</c> row already exists for the same
    /// (<paramref name="connectionRef"/>, <paramref name="workItemId"/>) —
    /// this is the T1 partial-unique-index enforcement.</summary>
    Task<Result> InsertClaimAsync(
        string claimId,
        string connectionRef,
        string worktreeFingerprint,
        string primaryScopeKind,
        int workItemId,
        string state,
        string casToken,
        string recordJson,
        CancellationToken ct = default);

    /// <summary>CAS-guarded update. The row's stored <c>cas_token</c> MUST
    /// equal <paramref name="expectedCasToken"/>; the new
    /// <paramref name="newCasToken"/> replaces it. Zero rows affected
    /// (missing claim or token mismatch) surfaces <c>claim-cas-mismatch</c>
    /// so the caller can retry against the fresh row. Storage does not
    /// interpret the token or the state — those are AB#739's concerns.</summary>
    Task<Result> UpdateClaimStateAsync(
        string claimId,
        string expectedCasToken,
        string newCasToken,
        string state,
        DateTimeOffset? endedAt,
        string recordJson,
        CancellationToken ct = default);

    Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default);
    Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, string primaryScopeKind, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default);

    /// <summary>Enumerate every row (in any state) whose composite tuple
    /// (<paramref name="connectionRef"/>,
    /// <paramref name="workItemId"/>) matches. Used exclusively by the
    /// AB#737 §Validation path when the caller wants to distinguish
    /// <c>ClaimNotFound</c> from <c>ClaimNotActive</c> — it never
    /// authorizes anything. Returned rows are unordered.</summary>
    Task<Result<IReadOnlyList<SystemClaimRow>>> FindClaimsForTupleAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default);

    /// <summary>Atomic supersession: within one storage transaction,
    /// CAS-rewrites <paramref name="predecessorClaimId"/> from
    /// <c>active</c>→<c>superseded</c> (matching
    /// <paramref name="predecessorExpectedCasToken"/>) and inserts a fresh
    /// row for <paramref name="newClaimId"/> in <c>active</c> state.
    /// Realizes AB#737 §Reclaim step 3' while honoring T1's partial unique
    /// index on
    /// <c>(connection_ref, work_item_id) WHERE state IN ('pending','active')</c>:
    /// the predecessor is superseded before the new row is inserted, so
    /// both writes co-exist inside the transaction without tripping the
    /// index. A predecessor CAS mismatch surfaces
    /// <c>claim-cas-mismatch</c>; a residual index violation on the insert
    /// surfaces <c>claim-duplicate-reserved</c>; either failure rolls the
    /// whole transaction back.</summary>
    Task<Result> SupersedeAndActivateClaimAsync(
        string newClaimId,
        string newCasToken,
        string connectionRef,
        string worktreeFingerprint,
        string primaryScopeKind,
        int workItemId,
        string newRecordJson,
        string predecessorClaimId,
        string predecessorExpectedCasToken,
        string predecessorNewCasToken,
        string predecessorRecordJson,
        DateTimeOffset transitionAt,
        CancellationToken ct = default);

    // ── Tuple operation epoch (§9.4, AB#739 durable epoch protocol) ────

    /// <summary>Reserve the next monotonic epoch for the tuple, atomically
    /// incrementing <c>tuple_epochs.current_epoch</c>. Returns the new
    /// epoch number the caller has claimed for its remote projection.
    /// Called BEFORE the ADO write so an authoritative claimId + casToken
    /// can be recorded against this epoch after local activation
    /// commits.</summary>
    Task<Result<long>> ReserveTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default);

    /// <summary>Record the caller as the tuple's winner at
    /// <paramref name="expectedEpoch"/>. CAS-conditions on
    /// <c>current_epoch = expectedEpoch</c>; a later reserver that
    /// raised the epoch causes this to fail with
    /// <c>claim-tuple-epoch-mismatch</c>. On success the winner's
    /// <paramref name="winningClaimId"/> + <paramref name="winningCasToken"/>
    /// are persisted so compensation reads know exactly what to project.</summary>
    Task<Result> CommitTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, long expectedEpoch, string winningClaimId, string winningCasToken, CancellationToken ct = default);

    /// <summary>Read the current epoch state so a compensating writer or
    /// a losing lifecycle operation can converge ADO to the authoritative
    /// winner. Missing row returns
    /// <see cref="TupleEpochRow"/> with <c>Epoch = 0</c>.</summary>

    // ── Atomic transition + epoch commit (§9.4, AB#739) ────────────────

    /// <summary>Atomic mint activation: CAS-transitions the pending
    /// claim to active AND CAS-commits the tuple epoch in the same
    /// <c>BEGIN IMMEDIATE</c> transaction. On any CAS failure the whole
    /// transaction rolls back — the row never activates unless the
    /// caller was the recorded winner at
    /// <paramref name="expectedEpoch"/>. Closes the "activate then a
    /// later reserver raised the epoch" race.
    /// <para>Failure codes: <c>claim-cas-mismatch</c> for a mismatched
    /// pending CAS token; <c>claim-tuple-epoch-mismatch</c> when the
    /// epoch moved between reserve and commit; storage failures pass
    /// through verbatim.</para></summary>
    Task<Result> ActivateClaimAndCommitEpochAsync(
        string claimId,
        string expectedCasToken,
        string newCasToken,
        DateTimeOffset activatedAt,
        string recordJson,
        string connectionRef,
        string primaryScopeKind,
        int workItemId,
        long expectedEpoch,
        CancellationToken ct = default);

    /// <summary>Atomic reclaim supersede+activate+epoch commit in one
    /// <c>BEGIN IMMEDIATE</c> transaction. Combines the predecessor
    /// active→superseded CAS, the new active insert, and the tuple
    /// epoch commit. If any of the three steps fails the whole
    /// transaction rolls back.</summary>
    Task<Result> SupersedeAndActivateClaimAndCommitEpochAsync(
        string newClaimId,
        string newCasToken,
        string connectionRef,
        string worktreeFingerprint,
        string primaryScopeKind,
        int workItemId,
        string newRecordJson,
        string predecessorClaimId,
        string predecessorExpectedCasToken,
        string predecessorNewCasToken,
        string predecessorRecordJson,
        DateTimeOffset transitionAt,
        long expectedEpoch,
        CancellationToken ct = default);

    /// <summary>Atomic release terminalization + epoch commit. Combines
    /// the active→released CAS on the claim row and the tuple epoch
    /// commit in one <c>BEGIN IMMEDIATE</c> transaction.</summary>
    Task<Result> TerminalizeClaimAndCommitEpochAsync(
        string claimId,
        string expectedCasToken,
        string newCasToken,
        DateTimeOffset endedAt,
        string recordJson,
        string connectionRef,
        string primaryScopeKind,
        int workItemId,
        long expectedEpoch,
        CancellationToken ct = default);
    Task<Result<TupleEpochRow>> GetTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default);

    // ── ProfileCache (§9.4, AB#727) ───────────────────────────────────

    Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default);
    Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default);
}

internal sealed record SystemWorktreeRow(string ConnectionRef, DateTimeOffset? RetiredAt);

/// <summary>Row projection for <c>claims</c>. Exact shape per T1 §4.3.1;
/// <see cref="RecordJson"/> is passed through opaque, <see cref="CasToken"/>
/// is the current storage-layer optimistic-concurrency stamp.</summary>
internal sealed record SystemClaimRow(
    string ClaimId,
    string ConnectionRef,
    string WorktreeFingerprint,
    string PrimaryScopeKind,
    int WorkItemId,
    string State,
    string CasToken,
    DateTimeOffset MintedAt,
    DateTimeOffset? EndedAt,
    string RecordJson);

/// <summary>Row projection for <c>tuple_epochs</c>. AB#739 durable epoch
/// protocol: every mint/reclaim/release reserves a monotonic epoch
/// before its remote projection; the eventual local commit records the
/// epoch as the winner. A compensating writer reads this row to know
/// exactly which claim is authoritative and what CAS token pinned it.</summary>
internal readonly record struct TupleEpochRow(
    long Epoch,
    string? WinningClaimId,
    string? WinningCasToken);

internal sealed record SystemProfileCacheRow(
    string ConnectionRef,
    string ProfileIdentity,
    string ProfileVersion,
    string Payload,
    DateTimeOffset FetchedAt);
