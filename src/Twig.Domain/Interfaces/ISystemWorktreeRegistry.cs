using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The system-tier registry seam AB#736 §9.4 fixes for the two downstream
/// tickets: AB#738 upserts the current managed worktree after a successful
/// attach and verifies a non-retired matching row before it runs; AB#739
/// extends the same store with claim rows keyed on the same worktree
/// fingerprint. Only the surface that AB#738 needs is exposed here; the
/// claim-side surface AB#739 owns is not carved yet, but the SQLite backing
/// is shared so adding it is a schema-migration change, not a new store.
/// <para>
/// Every method is atomic (§6.2 — SQLite WAL + <c>BEGIN IMMEDIATE</c>). A
/// storage failure surfaces the AB#736 §8 identifier verbatim through the
/// <see cref="Result"/> error string so the attachment service can route on
/// <c>system-store-locked</c>, <c>system-store-schema-mismatch</c>,
/// <c>worktree-not-registered</c>, or <c>worktree-retired</c>.
/// </para>
/// </summary>
internal interface ISystemWorktreeRegistry
{
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
}

/// <summary>
/// System-store <c>worktrees</c> row projected through the registry. Only the
/// two fields AB#738 needs before an attach are surfaced —
/// <see cref="ConnectionRef"/> must match the current binding and
/// <see cref="RetiredAt"/> must be <c>null</c>. AB#739 will extend this row
/// projection when it needs the claim-side fields.
/// </summary>
internal sealed record SystemWorktreeRow(string ConnectionRef, DateTimeOffset? RetiredAt);
