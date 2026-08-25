using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;


namespace Twig.Infrastructure.Plan;

/// <summary>
/// Shared plan-lifecycle service (twig plan native, wayfinder 0016). Every plan-touching
/// surface (CLI, MCP, TUI in future) routes through this one object so validation, preview,
/// apply, status and seed-descriptor semantics cannot drift.
/// <para>
/// The apply pipeline is:
/// </para>
/// <list type="number">
///   <item>Resolve the file to an absolute path (following symlinks), refuse anything
///     outside the workspace root, recompute the digest and require it to equal
///     <c>confirmedDigest</c> exactly.</item>
///   <item>Require the journal to already exist for that digest — apply NEVER imports; that
///     is preview's job and gives the user a confirmable checkpoint.</item>
///   <item>Refuse if any pending row exists at apply time; preview's earlier snapshot is a
///     UI hint, not a race window — the pending journal is re-inspected here.</item>
///   <item>Move the header and every operation Planned→Confirmed (idempotent), then walk
///     operations in declared order. For each: Confirmed→Applying (persist) → execute →
///     atomic Applying→Applied (state + applied_at + result_json in ONE row update; see
///     <see cref="IPlanJournalRepository.TryRecordAppliedAsync"/>) → readback →
///     Applied→Verified (or Failed / Indeterminate). A lost Confirmed→Applying CAS reloads
///     the row: a FRESH Applying lease (started within the last five minutes) is treated as
///     a live winner and the whole apply short-circuits with a top-level busy refusal —
///     the loser NEVER reads back and NEVER terminalises. Only Applying rows older than the
///     lease window are crash-recoverable, and recovery reads back BEFORE the atomic
///     Applied claim; failed / indeterminate outcomes terminalise directly without ever
///     stamping Applied. An executor result classified as Indeterminate triggers the same
///     readback → atomic Applied → Verified reconciliation while the row is still Applying,
///     so an ambiguously-committed response settles cleanly. The first non-Verified terminal
///     state stops the tail.</item>
///   <item>Reload the journal, complete the header Verified iff every operation ended
///     Verified, else Failed with the earliest per-op journal error propagated onto the
///     header. <see cref="PlanApplyResult.Error"/> carries either a pre-loop refusal or a
///     top-level Applying-lease busy refusal; a per-op failure leaves that field null and
///     callers read the per-operation <see cref="PlanJournalOperation.Error"/> for detail.</item>
/// </list>
/// </summary>
public sealed class PlanLifecycleService : IPlanLifecycleService
{
    private readonly PlanDocumentParser _parser;
    private readonly IPlanJournalRepository _journal;
    private readonly IPendingChangeReader _pendingReader;
    private readonly PlanOperationExecutor _executor;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IStagedIdentityRegistry _stagedRegistry;
    private readonly IPublishIdMapRepository _publishIdMap;
    private readonly IRevisionBoundAdoWorkItemService _revisionBound;

    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly WorkItemMapper _workItemMapper = new();

    private readonly TimeProvider _clock;
    private readonly PlanProcessRuleGate _ruleGate;

    /// <summary>
    /// Constructs the service. Every dependency is a Twig-shared singleton — the executor
    /// is created inline over the passed collaborators so callers only ever see the domain
    /// interfaces they already own.
    /// </summary>
    public PlanLifecycleService(
        PlanDocumentParser parser,
        IPlanJournalRepository journal,
        IPendingChangeReader pendingReader,
        IAdoWorkItemService adoService,
        IRevisionBoundAdoWorkItemService revisionBound,
        SeedPublishOrchestrator seedPublish,
        IWorkItemRepository workItemRepo,
        ISeedLinkRepository seedLinkRepo,
        IStagedIdentityRegistry stagedRegistry,
        IPublishIdMapRepository publishIdMap,
        IPublishIntentRepository publishIntent,
        TwigConfiguration config,
        TwigPaths paths,
        TimeProvider clock)
        : this(
            parser, journal, pendingReader, adoService, revisionBound, seedPublish,
            workItemRepo, seedLinkRepo, stagedRegistry, publishIdMap, publishIntent,
            config, paths, clock, ruleProvider: null)
    {
    }

    /// <summary>
    /// Composition-root overload that additionally accepts the process-rule provider used
    /// to evaluate enabled <c>makeRequired</c> gates before a batch PATCH — see
    /// <see cref="PlanProcessRuleGate"/> and AB#673. Left internal because
    /// <see cref="IProcessRuleProvider"/> is a Domain-internal type; the public constructor
    /// above stays backward-compatible and delegates in with a null provider (which the
    /// gate treats as permit-all).
    /// </summary>
    internal PlanLifecycleService(
        PlanDocumentParser parser,
        IPlanJournalRepository journal,
        IPendingChangeReader pendingReader,
        IAdoWorkItemService adoService,
        IRevisionBoundAdoWorkItemService revisionBound,
        SeedPublishOrchestrator seedPublish,
        IWorkItemRepository workItemRepo,
        ISeedLinkRepository seedLinkRepo,
        IStagedIdentityRegistry stagedRegistry,
        IPublishIdMapRepository publishIdMap,
        IPublishIntentRepository publishIntent,
        TwigConfiguration config,
        TwigPaths paths,
        TimeProvider clock,
        IProcessRuleProvider? ruleProvider)
    {
        _parser = parser;
        _journal = journal;
        _pendingReader = pendingReader;
        _workItemRepo = workItemRepo;
        _seedLinkRepo = seedLinkRepo;
        _stagedRegistry = stagedRegistry;
        _publishIdMap = publishIdMap;
        _revisionBound = revisionBound;

        _config = config;
        _paths = paths;
        _clock = clock;
        _executor = new PlanOperationExecutor(
            adoService, revisionBound, seedPublish,
            workItemRepo, seedLinkRepo, stagedRegistry, publishIdMap, publishIntent);
        _ruleGate = new PlanProcessRuleGate(ruleProvider);
    }

    /// <inheritdoc />
    public async Task<PlanValidationResult> ValidateAsync(string file, CancellationToken ct = default)
    {
        var containment = TryResolveInsideWorkspace(file);
        if (containment.Error is { } containmentError)
            return WholeDocumentError(PlanValidationCodes.EmptyString, containmentError);

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return WholeDocumentError(PlanValidationCodes.JsonInvalid, readError);

        var parsed = _parser.Parse(text.Contents);
        return AttachWorkspaceMismatchIfAny(parsed);
    }

    /// <inheritdoc />
    public async Task<PlanPreviewResult> PreviewAsync(string file, CancellationToken ct = default)
    {
        var containment = TryResolveInsideWorkspace(file);
        if (containment.Error is { } containmentError)
            return InvalidPreview(WholeDocumentError(PlanValidationCodes.EmptyString, containmentError));

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return InvalidPreview(WholeDocumentError(PlanValidationCodes.JsonInvalid, readError));

        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(text.Contents));
        var pending = await _pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false);

        if (!parsed.IsValid || parsed.Plan is null || parsed.CanonicalJson is null || parsed.Digest is null)
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan?.Operations ?? [],
                Issues = parsed.Issues,
                Workspace = parsed.Plan?.Workspace,
                PendingChanges = pending,
                CanApply = false,
            };

        // Idempotent import — a matching digest returns the existing journal, a mismatched
        // one throws (the ledger refuses to co-sign a doctored file). We hoist that as a
        // preview-level issue rather than an unhandled exception.
        PlanJournal? journal;
        try
        {
            journal = await _journal.ImportAsync(
                parsed.Plan,
                parsed.CanonicalJson,
                parsed.Digest,
                containment.AbsolutePath!,
                _clock.GetUtcNow(),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var issue = new PlanValidationIssue
            {
                Code = PlanValidationCodes.JsonInvalid,
                Path = string.Empty,
                Message = ex.Message,
            };
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues = [.. parsed.Issues, issue],
                Workspace = parsed.Plan.Workspace,
                PendingChanges = pending,
                CanApply = false,
            };
        }

        return new PlanPreviewResult
        {
            Digest = journal.Digest,
            Operations = parsed.Plan.Operations,
            Issues = parsed.Issues,
            Workspace = parsed.Plan.Workspace,
            PendingChanges = pending,
            CanApply = pending.Count == 0,
        };
    }

    /// <inheritdoc />
    public async Task<PlanApplyResult> ApplyAsync(string file, string confirmedDigest, CancellationToken ct = default)
    {
        var containment = TryResolveInsideWorkspace(file);
        if (containment.Error is { } containmentError)
            return TopLevelApplyFailure(confirmedDigest, containmentError);

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return TopLevelApplyFailure(confirmedDigest, readError);

        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(text.Contents));
        if (!parsed.IsValid || parsed.Plan is null || parsed.CanonicalJson is null || parsed.Digest is null)
            return TopLevelApplyFailure(
                confirmedDigest,
                parsed.Issues.Count > 0 ? parsed.Issues[0].Message : "Plan is invalid.");

        if (!string.Equals(parsed.Digest, confirmedDigest, StringComparison.Ordinal))
            return TopLevelApplyFailure(
                confirmedDigest,
                $"File digest {parsed.Digest} does not match confirmed digest {confirmedDigest}.");

        // Fresh zero-pending check at apply time; preview's snapshot is a UI hint, not a
        // race window. Any row present here refuses the apply.
        var pending = await _pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false);
        if (pending.Count > 0)
            return TopLevelApplyFailure(
                confirmedDigest,
                $"Refusing to apply while {pending.Count} pending change(s) are staged.");

        // Apply requires an existing preview journal. Import is preview's job.
        var journal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
        if (journal is null)
            return TopLevelApplyFailure(
                confirmedDigest,
                "No preview journal exists for this plan. Run `twig plan preview` first.");

        var now = _clock.GetUtcNow();

        // Per-op Planned→Confirmed for every row (idempotent — the transition returns false
        // on rows that already advanced, which we deliberately swallow here because we
        // immediately rehydrate below and route on the actual persisted state). Header
        // ConfirmAsync only touches the header row.
        foreach (var opRow in journal.Operations)
            await _journal.TryTransitionOperationAsync(
                parsed.Digest, opRow.OpId,
                PlanOperationState.Planned, PlanOperationState.Confirmed, now, ct).ConfigureAwait(false);

        if (journal.State == PlanOperationState.Planned)
            await _journal.ConfirmAsync(parsed.Digest, now, ct).ConfigureAwait(false);

        // Rehydrate — we now iterate against the confirmed state.
        journal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Journal disappeared mid-apply.");

        var byId = new Dictionary<string, PlanOperationDefinition>(StringComparer.Ordinal);
        foreach (var opDef in parsed.Plan.Operations)
            byId[opDef.Id] = opDef;

        var stopTail = false;
        string? busyMessage = null;

        foreach (var row in journal.Operations)
        {
            if (stopTail)
                break;
            if (!byId.TryGetValue(row.OpId, out var opDef))
            {
                stopTail = true;
                await _journal.SaveOperationErrorAsync(
                    parsed.Digest, row.OpId,
                    $"Journal operation {row.OpId} has no matching plan operation.",
                    PlanOperationState.Failed, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                continue;
            }

            var step = await StepOperationAsync(parsed.Digest, row, opDef, ct).ConfigureAwait(false);
            if (step.Busy)
            {
                // Two disjoint sources: a live Applying lease held by another actor, or
                // (AB#673 review) a retryable precondition from the process-rule gate —
                // the cached source revision does not match the batch and evaluating the
                // enabled makeRequired rule set against a stale snapshot is unsafe. In
                // BOTH cases: do NOT read back, do NOT terminalise, and do NOT touch the
                // header. Return a top-level refusal so the caller retries — for a lease
                // once the winner completes (or the 5-minute lease expires); for a stale
                // cache after `twig refresh` reloads the source at the expected revision.
                // The plan row stays Confirmed so re-apply resumes the same journal.
                busyMessage = step.BusyMessage;
                stopTail = true;
                break;
            }
            if (step.State != PlanOperationState.Verified)
                stopTail = true;
        }

        if (busyMessage is not null)
            return TopLevelApplyFailure(parsed.Digest, busyMessage);

        // Re-load once so the returned rows carry the final journal state, including tail
        // rows we deliberately left untouched. The header completion below uses the actual
        // per-row error (not a synthesized message) so the ledger's terminal record and its
        // top-level error name the same event.
        journal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Journal disappeared mid-apply.");

        var allVerified = true;
        string? firstRowError = null;
        foreach (var row in journal.Operations)
        {
            if (row.State != PlanOperationState.Verified)
            {
                allVerified = false;
                firstRowError ??= row.Error;
                break;
            }
        }

        var terminal = allVerified ? PlanOperationState.Verified : PlanOperationState.Failed;
        await _journal.CompleteAsync(
            parsed.Digest, terminal, _clock.GetUtcNow(),
            allVerified ? null : firstRowError, ct).ConfigureAwait(false);

        // PlanApplyResult.Error is reserved for pre-loop refusals (top-level). A failure
        // inside the per-op loop is reported by the per-operation Error and is null here —
        // callers walk Operations for detail.
        return new PlanApplyResult
        {
            Digest = parsed.Digest,
            Operations = journal.Operations,
            Failed = !allVerified,
            Error = null,
        };
    }

    /// <inheritdoc />
    public async Task<PlanStatusResult?> StatusAsync(string file, CancellationToken ct = default)
    {
        // Input validation mirrors ValidateAsync's ordering: path guard → read → parse →
        // workspace guard. Any failure returns a non-null PlanStatusResult carrying the
        // structured Issues; only a valid, in-workspace, workspace-matching document with a
        // digest and no journal returns null.
        var containment = TryResolveInsideWorkspace(file);
        if (containment.Error is { } containmentError)
            return StatusWithIssue(PlanValidationCodes.EmptyString, containmentError);

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return StatusWithIssue(PlanValidationCodes.JsonInvalid, readError);

        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(text.Contents));
        if (!parsed.IsValid || parsed.Digest is null)
            return new PlanStatusResult
            {
                Issues = parsed.Issues,
                Found = false,
                Digest = parsed.Digest,
            };

        var journal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
        if (journal is null)
            return null;

        return new PlanStatusResult
        {
            Found = true,
            Digest = journal.Digest,
            State = journal.State,
            Operations = journal.Operations,
            Error = journal.Error,
        };
    }

    /// <inheritdoc />
    public async Task<PlanSeedDescriptor?> DescribeSeedAsync(int seedId, CancellationToken ct = default)
    {
        // Descriptor is for STAGED seeds only. A positive id belongs to a published item —
        // there is no drift for a plan to defend against.
        if (seedId >= 0)
            return null;

        if (!StagedAlias.TryFrom(seedId, out var alias))
            return null;

        var identity = await _stagedRegistry.FindByAliasAsync(alias, ct).ConfigureAwait(false);
        if (identity is null)
            return null;

        // If the register already handed this alias to a published mapping, the seed
        // is no longer staged; refuse to describe it (0016 §7).
        var published = await _publishIdMap.GetNewIdAsync(identity.Value, ct).ConfigureAwait(false);
        if (published.HasValue)
            return null;

        var seed = await _workItemRepo.GetByIdAsync(alias.Value, ct).ConfigureAwait(false);
        if (seed is null || !seed.IsSeed || seed.StagedIdentity != identity)
            return null;

        var links = await _seedLinkRepo.GetLinksForItemAsync(alias.Value, ct).ConfigureAwait(false);
        var fingerprint = await SeedFingerprintCalculator
            .ComputeAsync(seed, links, _stagedRegistry, _publishIdMap, ct)
            .ConfigureAwait(false);

        return new PlanSeedDescriptor
        {
            Identity = identity.Value,
            Alias = alias,
            Fingerprint = fingerprint,
            Title = seed.Title,
            Type = seed.Type.Value,
        };
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Soft lease on <see cref="PlanOperationState.Applying"/>. A winner is expected to
    /// drive the row to a terminal state in well under this window; a lost CAS observing an
    /// Applying row younger than this refuses the concurrent apply outright (reading back
    /// or terminalising would race the winner's own writes and could poison a valid apply).
    /// Only an Applying row older than the window is treated as crash-recoverable.
    /// </summary>
    private static readonly TimeSpan ApplyingLeaseWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Result of a single step through the per-operation pipeline. <see cref="Busy"/> is
    /// the umbrella "do not touch the row, do not complete the header, short-circuit with a
    /// top-level refusal" signal. Two disjoint sources produce it:
    /// <list type="bullet">
    /// <item>A live Applying lease held by another actor — a busy retry when the winner
    ///   completes.</item>
    /// <item>A retryable precondition from the process-rule gate (AB#673 review) — the
    ///   local cache is at a different revision than the batch expects and the rule set has
    ///   an enabled <c>makeRequired</c> candidate that would be mis-evaluated on the stale
    ///   snapshot. The plan row stays Confirmed; a cache refresh + re-apply retries.</item>
    /// </list>
    /// Both surface via <see cref="Busy"/> because the top-level handling is identical: a
    /// top-level refusal, no journal terminalisation, no header completion.
    /// </summary>
    private readonly record struct StepResult(PlanOperationState State, string? BusyMessage)
    {
        public bool Busy => BusyMessage is not null;

        public static StepResult Terminal(PlanOperationState state) => new(state, null);

        public static StepResult Lease(string message) => new(PlanOperationState.Applying, message);

        /// <summary>
        /// Retryable precondition (AB#673): row remains at Confirmed on the caller's side
        /// — this method never persists any state transition for a precondition.
        /// </summary>
        public static StepResult NeedsRefresh(string message)
            => new(PlanOperationState.Confirmed, message);
    }

    /// <summary>
    /// Drives a single operation row through Confirmed→Applying→Applied→Verified/Failed/
    /// Indeterminate. A lost CAS (concurrent advance, or a recovered row already past the
    /// state we expected) reloads the actual row and routes off that — the service never
    /// acts on the state it wished it had. Recovery from Applying reads back BEFORE any
    /// Applied claim: a proven effect walks Applying → Applied (atomic, with result) →
    /// Verified; failed or indeterminate outcomes terminalise directly with no Applied
    /// stamp. Observing a fresh Applying lease returns <see cref="StepResult.Lease"/>
    /// without touching the row at all.
    /// </summary>
    private async Task<StepResult> StepOperationAsync(
        string digest,
        PlanJournalOperation row,
        PlanOperationDefinition opDef,
        CancellationToken ct)
    {
        // Fast paths: already terminal.
        if (row.State is PlanOperationState.Verified or PlanOperationState.Failed
            or PlanOperationState.Indeterminate)
            return StepResult.Terminal(row.State);

        var currentState = row.State;

        if (currentState == PlanOperationState.Confirmed)
        {
            // Runtime process-gate outcome (AB#673):
            //   • Refused → terminal Failed BEFORE the wire attempt. The row never claims
            //     Applying and the executor is never called for this op.
            //   • NeedsRefresh → retryable precondition. The cached source is at a
            //     revision the rule engine cannot honestly evaluate against; leave the row
            //     at Confirmed, do NOT terminalise, and surface a top-level refusal so a
            //     `twig refresh` + `twig plan apply` re-runs the same journal.
            var gate = await EvaluateProcessRuleGateAsync(opDef, ct).ConfigureAwait(false);
            if (gate.IsRefused)
            {
                return StepResult.Terminal(await MarkTerminalAsync(
                    digest, row.OpId, gate.Message!, PlanOperationState.Failed, ct)
                    .ConfigureAwait(false));
            }
            if (gate.IsRefreshRequired)
            {
                return StepResult.NeedsRefresh(gate.Message!);
            }

            // Persist Applying BEFORE the ADO call — the transition is the durable evidence
            // that the operation was attempted.
            var moved = await _journal.TryTransitionOperationAsync(
                digest, row.OpId,
                PlanOperationState.Confirmed, PlanOperationState.Applying,
                _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (!moved)
            {
                // Another actor advanced this row while we were choosing — reload and
                // route off the persisted state. If the reload observes a fresh Applying
                // lease held by the winner, this returns busy without any readback or
                // termination.
                return await ResumeFromObservedRowAsync(digest, row.OpId, opDef, ct).ConfigureAwait(false);
            }

            var applyResult = await _executor.ExecuteAsync(opDef, ct).ConfigureAwait(false);

            // Applied / MappedPublish: atomic Applying → Applied+result. Failed: terminal
            // Failed. Indeterminate: readback while still Applying — a proven effect still
            // walks Applied+Verified via the atomic record; anything else terminalises.
            return applyResult.Outcome switch
            {
                PlanExecutionOutcome.Applied or PlanExecutionOutcome.MappedPublish
                    => await RecordAppliedAndVerifyAsync(digest, row.OpId, opDef, applyResult, ct)
                        .ConfigureAwait(false),
                PlanExecutionOutcome.Failed
                    => StepResult.Terminal(await MarkTerminalAsync(
                        digest, row.OpId, applyResult.Error!, PlanOperationState.Failed, ct)
                        .ConfigureAwait(false)),
                _ => await ResolveIndeterminateExecuteAsync(digest, row.OpId, opDef, applyResult, ct)
                        .ConfigureAwait(false),
            };
        }

        if (currentState == PlanOperationState.Applying)
        {
            // Observed Applying at loop entry. Only rows older than the lease window are
            // crash-recoverable; a fresh Applying row is a live winner and we refuse.
            if (IsFreshApplyingLease(row))
                return StepResult.Lease(BusyLeaseMessage(row));

            // Stale Applying → crash-recovery: read back BEFORE claiming Applied.
            return await RecoverFromApplyingAsync(digest, row.OpId, opDef, ct).ConfigureAwait(false);
        }

        if (currentState == PlanOperationState.Applied)
        {
            // Applied recovery is verify-only: no readback→Applied claim, just readback +
            // Applied → Verified. The atomic record already stamped applied_at and result.
            return await FinalizeAppliedAsync(digest, row.OpId, opDef, default, ct).ConfigureAwait(false);
        }

        // Planned: prior confirmation loop should have moved this to Confirmed. Guard: treat
        // as Confirmed and step again in a single turn.
        if (currentState == PlanOperationState.Planned)
        {
            await _journal.TryTransitionOperationAsync(
                digest, row.OpId,
                PlanOperationState.Planned, PlanOperationState.Confirmed, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            return await ResumeFromObservedRowAsync(digest, row.OpId, opDef, ct).ConfigureAwait(false);
        }

        return StepResult.Terminal(currentState);
    }

    /// <summary>
    /// Reload the row and resume from its actual persisted state. Terminal states are
    /// returned as-is; a fresh Applying lease returns busy; otherwise the row re-enters
    /// <see cref="StepOperationAsync"/> so the ordinary pipeline drives it to conclusion.
    /// </summary>
    private async Task<StepResult> ResumeFromObservedRowAsync(
        string digest,
        string opId,
        PlanOperationDefinition opDef,
        CancellationToken ct)
    {
        var refreshed = await _journal.GetAsync(digest, ct).ConfigureAwait(false);
        var refreshedRow = refreshed?.Operations.FirstOrDefault(o => o.OpId == opId);
        if (refreshedRow is null)
            return StepResult.Terminal(await MarkTerminalAsync(
                digest, opId, "Row vanished after CAS reload.",
                PlanOperationState.Failed, ct).ConfigureAwait(false));

        if (refreshedRow.State == PlanOperationState.Applying && IsFreshApplyingLease(refreshedRow))
            return StepResult.Lease(BusyLeaseMessage(refreshedRow));

        return await StepOperationAsync(digest, refreshedRow, opDef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Crash-recovery arm for a row observed in <see cref="PlanOperationState.Applying"/>
    /// past the lease window. Readback classifies the actual server state; only a verified
    /// effect walks Applying → Applied (atomic, with the readback's canonical
    /// <see cref="PlanReadbackOutcome.ResultJson"/> — never NULL — so a recovered Verified
    /// row still exposes a result to CLI/MCP status) → Verified. Failed / indeterminate
    /// outcomes terminalise directly with no Applied stamp — an Applied row that never
    /// reached Verified would misrepresent an operation that never actually landed.
    /// </summary>
    private async Task<StepResult> RecoverFromApplyingAsync(
        string digest,
        string opId,
        PlanOperationDefinition opDef,
        CancellationToken ct)
    {
        var outcome = await _executor.ReadbackAsync(opDef, default, ct).ConfigureAwait(false);
        if (outcome.Ok)
        {
            // Atomic Applying → Applied with the readback's canonical result. The readback
            // observed the actual server state, so its ResultJson (batch/link revision,
            // delete marker, publish-seed identity/publishedId) is the authoritative
            // reconstruction of what the winning path would have recorded.
            var recorded = await _journal.TryRecordAppliedAsync(
                digest, opId, outcome.ResultJson, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (!recorded)
            {
                // Another actor already advanced past Applying — route off the persisted
                // state (possibly a live winner holding a lease).
                return await ResumeFromObservedRowAsync(digest, opId, opDef, ct).ConfigureAwait(false);
            }

            var verified = await _journal.TryTransitionOperationAsync(
                digest, opId,
                PlanOperationState.Applied, PlanOperationState.Verified,
                _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);

            // Lost the Applied → Verified CAS — reload and route off the persisted state
            // rather than claiming a state the ledger does not hold.
            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        return StepResult.Terminal(await MarkTerminalAsync(
            digest, opId, outcome.Error ?? (outcome.Deterministic ? "failed" : "indeterminate"),
            outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Winning-execute path: atomic Applying → Applied (with executor's result JSON), then
    /// readback + Applied → Verified. The atomic record replaces a former
    /// transition-then-save pair that carried a crash window in which the ledger could
    /// record state=Applied with result_json still NULL.
    /// </summary>
    private async Task<StepResult> RecordAppliedAndVerifyAsync(
        string digest,
        string opId,
        PlanOperationDefinition opDef,
        PlanExecutionResult applyResult,
        CancellationToken ct)
    {
        var recorded = await _journal.TryRecordAppliedAsync(
            digest, opId, applyResult.ResultJson, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (!recorded)
        {
            // Lost the atomic Applying → Applied CAS. Reload and route off the persisted
            // state; never assume the row is where our winning-execute branch expected it.
            return await ResumeFromObservedRowAsync(digest, opId, opDef, ct).ConfigureAwait(false);
        }

        return await FinalizeAppliedAsync(digest, opId, opDef, applyResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executor classified this write as indeterminate — the wire outcome couldn't be
    /// settled from what came back. While the row is still Applying, read back the server
    /// state: a proven effect promotes the row via the same atomic Applying → Applied path
    /// the winning execute uses; a non-proven readback terminalises the row directly.
    /// </summary>
    private async Task<StepResult> ResolveIndeterminateExecuteAsync(
        string digest,
        string opId,
        PlanOperationDefinition opDef,
        PlanExecutionResult applyResult,
        CancellationToken ct)
    {
        var outcome = await _executor.ReadbackAsync(opDef, applyResult, ct).ConfigureAwait(false);
        if (outcome.Ok)
        {
            // The executor could not classify, so its ResultJson is null; the readback that
            // proved the effect carries the canonical shape and stamps result_json here.
            var recorded = await _journal.TryRecordAppliedAsync(
                digest, opId, applyResult.ResultJson ?? outcome.ResultJson, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (!recorded)
                return await ResumeFromObservedRowAsync(digest, opId, opDef, ct).ConfigureAwait(false);

            var verified = await _journal.TryTransitionOperationAsync(
                digest, opId,
                PlanOperationState.Applied, PlanOperationState.Verified,
                _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);
            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        // Readback couldn't prove the effect — the executor's classification stands.
        // Terminalise directly from Applying with NO Applied stamp; the executor's original
        // error message wins when readback declined to name a determinate one.
        var error = outcome.Error ?? applyResult.Error ?? "indeterminate";
        var finalState = outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate;
        return StepResult.Terminal(await MarkTerminalAsync(digest, opId, error, finalState, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Readback + Applied → Verified terminal for a row already in <see cref="PlanOperationState.Applied"/>.
    /// No readback → Applied claim; the row is already there. A lost Applied → Verified CAS
    /// reloads and routes off the persisted state.
    /// </summary>
    private async Task<StepResult> FinalizeAppliedAsync(
        string digest,
        string opId,
        PlanOperationDefinition opDef,
        PlanExecutionResult applyResult,
        CancellationToken ct)
    {
        var outcome = await _executor.ReadbackAsync(opDef, applyResult, ct).ConfigureAwait(false);
        if (outcome.Ok)
        {
            var verified = await _journal.TryTransitionOperationAsync(
                digest, opId,
                PlanOperationState.Applied, PlanOperationState.Verified,
                _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);

            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        return StepResult.Terminal(await MarkTerminalAsync(
            digest, opId, outcome.Error ?? (outcome.Deterministic ? "failed" : "indeterminate"),
            outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate, ct)
            .ConfigureAwait(false));
    }

    private bool IsFreshApplyingLease(PlanJournalOperation row)
    {
        if (row.State != PlanOperationState.Applying) return false;
        if (row.StartedAt is not { } startedAt) return false;
        return _clock.GetUtcNow() - startedAt < ApplyingLeaseWindow;
    }

    private static string BusyLeaseMessage(PlanJournalOperation row) =>
        $"Plan is currently being applied by another actor (operation '{row.OpId}' entered "
        + $"Applying at {row.StartedAt:o}). Retry once the winner completes or the "
        + $"{(int)ApplyingLeaseWindow.TotalMinutes}-minute lease expires.";

    private async Task<PlanOperationState> ObserveActualStateAsync(
        string digest, string opId, CancellationToken ct)
    {
        var refreshed = await _journal.GetAsync(digest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Journal disappeared mid-apply.");
        var row = refreshed.Operations.FirstOrDefault(o => o.OpId == opId);
        if (row is null)
            throw new InvalidOperationException($"Operation {opId} vanished from the journal.");
        return row.State;
    }

    private async Task<PlanOperationState> MarkTerminalAsync(
        string digest,
        string opId,
        string error,
        PlanOperationState finalState,
        CancellationToken ct)
    {
        await _journal.SaveOperationErrorAsync(digest, opId, error, finalState, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        return finalState;
    }

    /// <summary>
    /// Consults the process-rule gate for a <see cref="BatchOperation"/>. Returns
    /// <see cref="PlanProcessRuleGateOutcome.Ok"/> for non-batch kinds. Otherwise fetches
    /// the authoritative expected-revision snapshot, maps it to a work item, and defers to
    /// <see cref="PlanProcessRuleGate.EvaluateAsync(BatchOperation, Twig.Domain.Aggregates.WorkItem, CancellationToken)"/>,
    /// which distinguishes a rule refusal (terminal Failed) from a stale-cache or snapshot
    /// load precondition (retryable — the caller MUST leave the row at Confirmed). See
    /// <see cref="PlanProcessRuleGate"/> for the class-level rationale, AB#673.
    /// </summary>
    private async Task<PlanProcessRuleGateOutcome> EvaluateProcessRuleGateAsync(
        PlanOperationDefinition opDef,
        CancellationToken ct)
    {
        if (opDef is not BatchOperation batch) return PlanProcessRuleGateOutcome.Ok;

        WorkItemSnapshot snapshot;
        try
        {
            snapshot = await _revisionBound.FetchAtRevisionAsync(batch.WorkItemId, batch.ExpectedRevision, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlanProcessRuleGateOutcome.RequiresRefresh(
                $"Unable to load authoritative snapshot for work item {batch.WorkItemId} at revision {batch.ExpectedRevision}: {ex.Message}");
        }

        var source = _workItemMapper.Map(snapshot);
        return await _ruleGate.EvaluateAsync(batch, source, ct).ConfigureAwait(false);
    }


    // ── Path / workspace guards ────────────────────────────────────────────

    private readonly record struct Containment(string? AbsolutePath, string? Error);

    /// <summary>
    /// Resolves <paramref name="file"/> to an absolute path (following symlinks for the
    /// file and the workspace root when they exist) and refuses anything that is not a
    /// segment-bounded descendant of the workspace root. Segment-aware means
    /// <c>/repo/.twig-evil</c> is not considered inside <c>/repo/.twig</c>. Equality is
    /// OS-sensitive throughout — Windows and macOS default filesystems compare paths
    /// case-insensitively, every other Unix filesystem case-sensitively.
    /// </summary>
    private Containment TryResolveInsideWorkspace(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return new Containment(null, "File path is required.");

        string full;
        try
        {
            full = ResolveFinalPath(Path.GetFullPath(file));
        }
        catch (Exception ex)
        {
            return new Containment(null, $"Invalid file path: {ex.Message}");
        }

        var root = ResolveFinalPath(Path.GetFullPath(_paths.RepoRoot));
        if (!IsInside(root, full))
            return new Containment(null, $"File '{file}' is outside the workspace root '{root}'.");

        return new Containment(full, null);
    }

    /// <summary>
    /// Resolves every path component to its real target, walking symlinks one directory
    /// segment at a time. A symlinked intermediate directory that points outside the
    /// workspace must not slip past containment just because the FINAL segment is not
    /// itself a link — <see cref="File.ResolveLinkTarget(string, bool)"/> alone only
    /// follows the leaf. Missing segments are preserved as-is so callers can still
    /// error-report a not-yet-created plan file. Purely managed APIs: portable across
    /// Windows, macOS, and Linux.
    /// </summary>
    private static string ResolveFinalPath(string absolute)
        => ResolveComponents(absolute, depth: 0);

    private const int MaxSymlinkDepth = 40;

    private static string ResolveComponents(string absolute, int depth)
    {
        if (depth > MaxSymlinkDepth)
            return absolute;

        absolute = Path.GetFullPath(absolute);
        var root = Path.GetPathRoot(absolute) ?? string.Empty;
        if (string.IsNullOrEmpty(root))
            root = Path.DirectorySeparatorChar.ToString();

        var rel = absolute.Length > root.Length ? absolute[root.Length..] : string.Empty;
        var parts = rel.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);

            string? link = null;
            try
            {
                if (File.Exists(current))
                    link = File.ResolveLinkTarget(current, returnFinalTarget: false)?.FullName;
                else if (Directory.Exists(current))
                    link = Directory.ResolveLinkTarget(current, returnFinalTarget: false)?.FullName;
                else
                {
                    // Missing segment: preserve the remainder verbatim so callers still
                    // get a stable absolute path for error reporting.
                    for (var j = i + 1; j < parts.Length; j++)
                        current = Path.Combine(current, parts[j]);
                    return current;
                }
            }
            catch (IOException) { link = null; }
            catch (UnauthorizedAccessException) { link = null; }

            if (link is null) continue;

            if (!Path.IsPathRooted(link))
            {
                var parent = Path.GetDirectoryName(current) ?? current;
                link = Path.GetFullPath(Path.Combine(parent, link));
            }

            // Recurse so symlinks whose target still contains symlinked components
            // (or points at another symlink) resolve fully before we continue walking.
            current = ResolveComponents(link, depth + 1);
        }

        return current;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
            return true;
        var normalized = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalized, PathComparison);
    }

    private readonly record struct FileText(string Contents, string? Error);

    private static async Task<FileText> ReadFileAsync(string absolutePath, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);
            return new FileText(Encoding.UTF8.GetString(bytes), null);
        }
        catch (Exception ex)
        {
            return new FileText(string.Empty, $"Failed to read plan file: {ex.Message}");
        }
    }

    private PlanValidationResult AttachWorkspaceMismatchIfAny(PlanValidationResult parsed)
    {
        if (parsed.Plan is null) return parsed;
        var ws = parsed.Plan.Workspace;
        if (string.Equals(ws.Organization, _config.Organization, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ws.Project, _config.Project, StringComparison.OrdinalIgnoreCase))
            return parsed;

        var mismatch = new PlanValidationIssue
        {
            Code = PlanValidationCodes.EmptyString,
            Path = "/workspace",
            Message =
                $"Plan workspace {ws.Organization}/{ws.Project} does not match active workspace "
                + $"{_config.Organization}/{_config.Project}.",
        };
        return new PlanValidationResult
        {
            Issues = [.. parsed.Issues, mismatch],
            Plan = null,
            CanonicalJson = null,
            Digest = null,
        };
    }

    private static PlanValidationResult WholeDocumentError(string code, string message) => new()
    {
        Issues =
        [
            new PlanValidationIssue { Code = code, Path = string.Empty, Message = message },
        ],
        Plan = null,
        CanonicalJson = null,
        Digest = null,
    };

    private static PlanPreviewResult InvalidPreview(PlanValidationResult v) => new()
    {
        Digest = v.Digest,
        Operations = [],
        Issues = v.Issues,
        Workspace = v.Plan?.Workspace,
        PendingChanges = [],
        CanApply = false,
    };

    private static PlanApplyResult TopLevelApplyFailure(string digest, string message) => new()
    {
        Digest = digest,
        Operations = [],
        Failed = true,
        Error = message,
    };

    private static PlanStatusResult StatusWithIssue(string code, string message) => new()
    {
        Issues = [new PlanValidationIssue { Code = code, Path = string.Empty, Message = message }],
        Found = false,
    };
}
