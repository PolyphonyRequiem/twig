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
    Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default);

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
    int WorkItemId,
    string State,
    string CasToken,
    DateTimeOffset MintedAt,
    DateTimeOffset? EndedAt,
    string RecordJson);

internal sealed record SystemProfileCacheRow(
    string ConnectionRef,
    string ProfileIdentity,
    string ProfileVersion,
    string Payload,
    DateTimeOffset FetchedAt);
