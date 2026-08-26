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
/// <c>worktree-not-registered</c>, or <c>worktree-retired</c>.
/// </para>
/// <para>
/// The claim-side and profile-cache surfaces are the T1 v1 shapes AB#739 and
/// AB#727 consume respectively. No lifecycle policy is realized here (claim
/// state transitions, reservation rules, profile-cache eviction) — those are
/// the ticket owners' business. The storage layer only offers atomic
/// primitives.
/// </para>
/// </summary>
internal interface ISystemWorktreeRegistry
{
    // ── Worktree registry (§9.4) ─────────────────────────────────────

    /// <summary>Return the registered row for the given fingerprint, or
    /// <c>null</c> when none exists. Never throws for a missing row.</summary>
    Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default);

    /// <summary>Insert or refresh the <c>connections</c> row for
    /// <paramref name="connectionRef"/>. Idempotent.</summary>
    Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default);

    /// <summary>Insert or refresh the <c>worktrees</c> row keyed by
    /// <paramref name="worktreeFingerprint"/>. Reactivates a row whose
    /// <c>retiredAt</c> is non-null. Idempotent.</summary>
    Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default);

    // ── Claims registry (§9.4, AB#739) ────────────────────────────────

    /// <summary>Insert a claim row. Fails with <c>worktree-not-registered</c>
    /// when the referenced fingerprint has no row. Storage never inspects
    /// <paramref name="recordJson"/> beyond the ≤64 KiB length bound §9.4
    /// fixes.</summary>
    Task<Result> InsertClaimAsync(string claimId, string connectionRef, string worktreeFingerprint, int workItemId, string state, string recordJson, CancellationToken ct = default);

    /// <summary>Update the <c>state</c>, <c>endedAt</c>, and <c>recordJson</c>
    /// of an existing claim. Fails silently ok when the row does not exist —
    /// AB#739 owns whether that is fatal.</summary>
    Task<Result> UpdateClaimStateAsync(string claimId, string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default);

    /// <summary>Look up a claim by primary key.</summary>
    Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default);

    /// <summary>The AB#739 "local duplicate claim rule" enforcement lookup —
    /// returns the row for <paramref name="workItemId"/> whose state is in
    /// the caller-supplied reserved set (§9.4 fixes this as
    /// <c>{ pending, active }</c>). Downstream callers MUST NOT widen the
    /// set to include released/superseded/retired rows.</summary>
    Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default);

    // ── ProfileCache (§9.4, AB#727) ───────────────────────────────────

    /// <summary>Read the cached profile row for a connection, or <c>null</c>
    /// when none exists.</summary>
    Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default);

    /// <summary>Upsert the cached profile row.</summary>
    Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default);
}

/// <summary>
/// System-store <c>worktrees</c> row projected through the registry. Only the
/// two fields AB#738 needs before an attach are surfaced —
/// <see cref="ConnectionRef"/> must match the current binding and
/// <see cref="RetiredAt"/> must be <c>null</c>. AB#739 will extend this row
/// projection when it needs the claim-side fields.
/// </summary>
internal sealed record SystemWorktreeRow(string ConnectionRef, DateTimeOffset? RetiredAt);

/// <summary>Row projection for <c>claims</c>. Exact shape per T1 §4.3.1;
/// <see cref="RecordJson"/> is passed through opaque.</summary>
internal sealed record SystemClaimRow(
    string ClaimId,
    string ConnectionRef,
    string WorktreeFingerprint,
    int WorkItemId,
    string State,
    DateTimeOffset MintedAt,
    DateTimeOffset? EndedAt,
    string RecordJson);

/// <summary>Row projection for <c>profileCache</c>. AB#727 consumes this.</summary>
internal sealed record SystemProfileCacheRow(
    string ConnectionRef,
    string ProfileIdentity,
    string ProfileVersion,
    string Payload,
    DateTimeOffset FetchedAt);
