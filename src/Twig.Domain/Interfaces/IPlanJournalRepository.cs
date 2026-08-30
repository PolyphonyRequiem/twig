using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Durable store for plan journals. Implemented over the same pending.db that carries
/// publish intents and staged changes, so recovery of a partially-applied plan needs no
/// extra file. Every operation-level state transition is conditional on the caller's
/// expected <c>fromState</c> — that is the single lifecycle guard for plan apply.
/// </summary>
public interface IPlanJournalRepository
{
    /// <summary>
    /// Imports a validated plan as a new journal in state <see cref="PlanOperationState.Planned"/>.
    /// Every operation row starts in <c>Planned</c> with its canonical per-operation JSON in
    /// <c>RequestJson</c>.
    /// <para>
    /// The three artifact arguments — <paramref name="plan"/>, <paramref name="canonicalJson"/>,
    /// and <paramref name="digest"/> — are cryptographically bound at the boundary. Before any
    /// row is written the implementation MUST recompute the canonical form of
    /// <paramref name="canonicalJson"/>, verify the SHA-256 of those bytes matches
    /// <paramref name="digest"/>, and cross-check that the fully-parsed canonical document is
    /// structurally identical to <paramref name="plan"/> — workspace, operation count, and per
    /// subtype payload (fields / relations / staged identity / fingerprint / expected revision
    /// / work item id) all included. Any mismatch is rejected — the journal cannot be permitted
    /// to record a plan identity the file no longer represents.
    /// </para>
    /// <para>
    /// Re-import of the same digest is idempotent: the persisted canonical JSON is compared
    /// against the incoming <paramref name="canonicalJson"/>; equal returns the existing
    /// journal, unequal (a doctored digest) is refused. The insertion itself is transactional
    /// against concurrent same-digest importers, so two racing callers observe the same row
    /// rather than a primary-key exception.
    /// </para>
    /// </summary>
    Task<PlanJournal> ImportAsync(
        PlanDefinition plan,
        string canonicalJson,
        string digest,
        string sourcePath,
        DateTimeOffset previewedAt,
        CancellationToken ct = default);

    /// <summary>Returns the journal for a digest, or null when none exists.</summary>
    Task<PlanJournal?> GetAsync(string digest, CancellationToken ct = default);

    /// <summary>
    /// AB#832: returns every digest journaled against <paramref name="sourcePath"/>, oldest
    /// preview first. Empty when the path has never been previewed.
    /// <para>
    /// The journal is keyed by digest, so this is the inverse lookup: it answers "what
    /// transactions has this path carried?" rather than "what does this content describe?".
    /// A path with more than one digest, or with a digest other than the one its bytes
    /// currently hash to, has been overwritten — plan files are single-use, so the same path
    /// legitimately carries exactly one digest for its whole life.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> GetDigestsBySourcePathAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// Records the authorization that gated this proposal's apply, plus the canonical semantic
    /// review model exactly as the authorizer was shown it (design record T2 §5.3).
    /// <para>
    /// <b>First authorization wins.</b> An apply resumed after an interrupted run re-presents the
    /// same digest, and the fact that matters for audit is the authorization that originally
    /// released the proposal — not the moment a crashed run was picked back up. Implementations
    /// therefore write these columns only while they are still unset, and a later call on an
    /// already-authorized digest is a no-op rather than an overwrite.
    /// </para>
    /// <para>
    /// <paramref name="reviewModelJson"/> is stored beside, never instead of, the journal's
    /// canonical JSON: the latter is what was authorized, this is what the authorizer saw.
    /// </para>
    /// </summary>
    Task RecordAuthorizationAsync(
        string digest,
        ProposalAuthorization authorization,
        string reviewModelJson,
        CancellationToken ct = default);

    /// <summary>Marks the top-level plan as confirmed and stamps <paramref name="confirmedAt"/>.</summary>
    Task ConfirmAsync(string digest, DateTimeOffset confirmedAt, CancellationToken ct = default);

    /// <summary>
    /// Conditional operation-state transition. Returns true iff exactly one row moved from
    /// <paramref name="fromState"/> to <paramref name="toState"/>. Callers use this as their
    /// concurrency guard — a false result means another actor already advanced the row.
    /// Timestamps are stamped by kind of transition (StartedAt when entering Applying, etc.).
    /// <para>
    /// AB#754/755: <paramref name="warning"/> carries non-fatal normalization detail — the
    /// server-generated or ADO-canonicalized differences a readback proved harmless — and is
    /// written by the SAME conditional UPDATE that performs the transition. Writing it here
    /// rather than as a preceding call is deliberate: a separate pre-CAS write can strand
    /// warning text on a row whose transition was then lost and which another actor
    /// terminalised as Failed/Indeterminate. Passing <c>null</c> preserves any warning already
    /// recorded; it never erases one.
    /// </para>
    /// </summary>
    Task<bool> TryTransitionOperationAsync(
        string digest,
        string opId,
        PlanOperationState fromState,
        PlanOperationState toState,
        DateTimeOffset timestamp,
        CancellationToken ct = default,
        string? warning = null);

    /// <summary>
    /// Records the outcome of an apply attempt: writes <paramref name="resultJson"/> onto an
    /// <see cref="PlanOperationState.Applied"/> row. Does NOT change the operation's state
    /// and does NOT stamp any timestamp column — the Applied → Verified transition, via
    /// <see cref="TryTransitionOperationAsync"/>, is the sole writer of <c>verified_at</c>.
    /// <para>
    /// The update is state-gated to <see cref="PlanOperationState.Applied"/>: a result is a fact
    /// about an apply that already succeeded, and there is no meaningful outcome to record for
    /// a row still in Planned / Confirmed / Applying (apply has not committed) or already
    /// terminal (Verified / Failed / Indeterminate — the outcome is settled and immutable). All
    /// such rows are left strictly untouched.
    /// </para>
    /// <para>
    /// Ordering contract: the caller MUST invoke <c>SaveOperationResultAsync</c> BEFORE the
    /// Applied → Verified transition. A transition-first sequence terminalises the row while
    /// its <c>result_json</c> is still null, and the subsequent save call is a no-op — the
    /// result is permanently lost from the ledger. This mirrors the "record intent before the
    /// call, record the outcome before terminalising" shape the rest of the durable store uses.
    /// </para>
    /// </summary>
    Task SaveOperationResultAsync(
        string digest,
        string opId,
        string? resultJson,
        CancellationToken ct = default);

    /// <summary>
    /// Atomic Applying → Applied with result. Compare-and-transitions a row in
    /// <see cref="PlanOperationState.Applying"/> to <see cref="PlanOperationState.Applied"/>
    /// AND stamps <c>applied_at</c> AND writes <paramref name="resultJson"/> in a single row
    /// update. Returns <c>true</c> iff exactly one Applying row moved.
    /// <para>
    /// This is the ONLY correct way to record an Applied-with-result outcome. A split
    /// <see cref="TryTransitionOperationAsync"/> + <see cref="SaveOperationResultAsync"/>
    /// sequence carries a crash window between the two writes in which the ledger records
    /// state=<c>Applied</c> with <c>result_json</c> still NULL; a subsequent recovery walking
    /// through <see cref="FinalizeAppliedAsync"/>-style paths cannot reconstruct the outcome
    /// from that gap. Callers that already have proof of the effect (executor success, or a
    /// readback that classified the row as Verified) MUST use this method.
    /// </para>
    /// <para>
    /// Rows in any state other than <see cref="PlanOperationState.Applying"/> — including
    /// terminal states — are left strictly untouched. A lost race returns <c>false</c> and
    /// the caller reloads and routes off the actual persisted state.
    /// </para>
    /// </summary>
    Task<bool> TryRecordAppliedAsync(
        string digest,
        string opId,
        string? resultJson,
        DateTimeOffset appliedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records a terminal failure for an operation and moves it to <paramref name="finalState"/>
    /// (<see cref="PlanOperationState.Failed"/> or <see cref="PlanOperationState.Indeterminate"/>).
    /// </summary>
    Task SaveOperationErrorAsync(
        string digest,
        string opId,
        string error,
        PlanOperationState finalState,
        DateTimeOffset timestamp,
        CancellationToken ct = default);

    /// <summary>
    /// Moves the top-level plan to a terminal state and stamps <paramref name="completedAt"/>.
    /// </summary>
    Task CompleteAsync(
        string digest,
        PlanOperationState finalState,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken ct = default);
}
