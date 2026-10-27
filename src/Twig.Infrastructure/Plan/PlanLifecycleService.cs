using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.GitHub;


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
public sealed partial class PlanLifecycleService : IPlanLifecycleService
{
    private readonly PlanDocumentParser _parser;
    private readonly IPlanJournalRepository _journal;
    private readonly IPendingChangeReader _pendingReader;
    private readonly PlanOperationExecutor? _executor;
    private readonly GitHubNativeRelationshipService? _nativeRelationships;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedLinkRepository? _seedLinkRepo;
    private readonly IStagedIdentityRegistry? _stagedRegistry;
    private readonly IPublishIdMapRepository? _publishIdMap;
    private readonly IRevisionBoundAdoWorkItemService? _revisionBound;
    private readonly IPublishIntentRepository? _publishIntent;
    private readonly IPlanOriginProvider? _origin;
    private readonly bool _previewOnly;

    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly WorkItemMapper _workItemMapper = new();

    private readonly TimeProvider _clock;
    private readonly PlanProcessRuleGate _ruleGate;
    private readonly string _systemRegistryPath;
    private readonly ChangeProposalReviewModelBuilder _reviewModel;
    private readonly DeclaredPolicyLoader _policyLoader;
    private readonly DeclaredPolicyEvaluator _policyEvaluator;
    private readonly IExecutionPolicyFactReader? _executionReader;

    /// <summary>
    /// Constructs the service. Every dependency is a Twig-shared singleton; the executor is
    /// created inline over those collaborators so plan readback uses the same field metadata
    /// cache as the rest of the connection.
    /// </summary>
    public PlanLifecycleService(
        PlanDocumentParser parser,
        IPlanJournalRepository journal,
        IPendingChangeReader pendingReader,
        IFieldDefinitionStore fieldDefinitionStore,
        IProviderWorkItemWriter adoService,
        IProviderRevisionCodec revisionCodec,
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
        IPlanOriginProvider origin,
        IUnitOfWork unitOfWork)
        : this(
            parser, journal, pendingReader, fieldDefinitionStore, adoService, revisionCodec, revisionBound, seedPublish,
            workItemRepo, seedLinkRepo, stagedRegistry, publishIdMap, publishIntent,
            config, paths, clock, origin, ruleProvider: null, unitOfWork)
    {
    }

    /// <summary>
    /// Composition-root overload that additionally accepts the process-rule provider used
    /// to evaluate enabled <c>makeRequired</c> gates before a batch PATCH — see
    /// <see cref="IProcessRuleProvider"/> is a Domain-internal type; the public constructor
    /// delegates in with a null provider, which the gate treats as unavailable (AB#1169), so
    /// proposals built through it preview and apply as blocked until a provider is supplied.
    /// </summary>
    internal PlanLifecycleService(
        PlanDocumentParser parser,
        IPlanJournalRepository journal,
        IPendingChangeReader pendingReader,
        IFieldDefinitionStore fieldDefinitionStore,
        IProviderWorkItemWriter adoService,
        IProviderRevisionCodec revisionCodec,
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
        IPlanOriginProvider origin,
        IProcessRuleProvider? ruleProvider,
        IUnitOfWork unitOfWork,
        IWorkItemLinkRepository? linkRepo = null,
        IWorkItemSnapshotReader? providerSnapshots = null,
        IWorkItemDiscussionReader? discussionReader = null,
        IWorkItemDiscussionWriter? discussionWriter = null,
        DiscussionAppendAdmission? discussionAdmission = null,
        string? systemRegistryPath = null)
    {
        _parser = parser;
        _journal = journal;
        _pendingReader = pendingReader;
        _workItemRepo = workItemRepo;
        _executionReader = adoService as IExecutionPolicyFactReader;
        _seedLinkRepo = seedLinkRepo;
        _stagedRegistry = stagedRegistry;
        _publishIdMap = publishIdMap;
        _revisionBound = revisionBound;
        _publishIntent = publishIntent;
        _origin = origin;
        _systemRegistryPath = systemRegistryPath ?? Path.Combine(
            Twig.Infrastructure.Auth.ConnectionBindingService.ResolveUserHome(), "system.db");

        _config = config;
        _paths = paths;
        _clock = clock;
        _executor = new PlanOperationExecutor(
            adoService, revisionCodec, fieldDefinitionStore, seedPublish,
            workItemRepo, seedLinkRepo, stagedRegistry, publishIdMap, publishIntent, pendingReader, unitOfWork,
            discussionWriter, discussionReader, discussionAdmission);
        _ruleGate = new PlanProcessRuleGate(ruleProvider);
        _reviewModel = new ChangeProposalReviewModelBuilder(workItemRepo, fieldDefinitionStore)
        {
            CapabilityDescriptor = Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet(
                config.ResolveWorkItemProviderKind(), out var capabilityDescriptor) ? capabilityDescriptor : null,
        };
        _policyLoader = new DeclaredPolicyLoader(paths.PolicyPath);
        _policyEvaluator = new DeclaredPolicyEvaluator(workItemRepo, linkRepo, providerSnapshots);
    }

    /// <summary>Composition for provider-native review without any ADO mutation or CAS boundary.</summary>
    internal PlanLifecycleService(
        PlanDocumentParser parser,
        IPlanJournalRepository journal,
        IPendingChangeReader pendingReader,
        IWorkItemRepository workItemRepo,
        TwigConfiguration config,
        TwigPaths paths,
        IWorkItemSnapshotReader providerSnapshots,
        GitHubNativeRelationshipService? nativeRelationships = null)
    {
        _parser = parser;
        _journal = journal;
        _pendingReader = pendingReader;
        _workItemRepo = workItemRepo;
        _config = config;
        _paths = paths;
        _clock = TimeProvider.System;
        _systemRegistryPath = string.Empty;
        _ruleGate = new PlanProcessRuleGate(rules: null);
        _reviewModel = new ChangeProposalReviewModelBuilder(workItemRepo)
        {
            CapabilityDescriptor = Twig.Domain.Capabilities.ProviderCapabilityCatalog.TryGet(
                config.ResolveWorkItemProviderKind(), out var capabilityDescriptor) ? capabilityDescriptor : null,
        };
        _policyLoader = new DeclaredPolicyLoader(paths.PolicyPath);
        _policyEvaluator = new DeclaredPolicyEvaluator(workItemRepo, links: null, providerSnapshots);
        _nativeRelationships = nativeRelationships;
        _origin = nativeRelationships;
        _previewOnly = nativeRelationships is null;
    }

    /// <inheritdoc />
    public async Task<PlanLatestResult> LatestAsync(CancellationToken ct = default)
    {
        var latest = await _journal.GetLatestUnresolvedAsync(ct).ConfigureAwait(false);
        if (latest is null)
            return new PlanLatestResult { Found = false };

        var containment = TryResolveInsideWorkspace(latest.File!);
        if (containment.Error is { } containmentError)
            return latest with { Error = containmentError };

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return latest with { Error = readError };

        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(text.Contents));
        if (!parsed.IsValid || parsed.Digest is null)
        {
            var issue = parsed.Issues.FirstOrDefault();
            return latest with { Error = issue?.Message ?? "The latest proposal file is invalid." };
        }
        if (!string.Equals(parsed.Digest, latest.Digest, StringComparison.Ordinal))
            return latest with
            {
                Error = $"Latest proposal file '{containment.AbsolutePath}' changed after preview: "
                    + $"journal digest {latest.Digest} does not match current digest {parsed.Digest}.",
            };

        return latest with { File = containment.AbsolutePath };
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
    public Task<PlanPreviewResult> PreviewAsync(string file, CancellationToken ct = default)
        => PreviewCoreAsync(file, expectedDigest: null, ct);

    /// <inheritdoc />
    public Task<PlanPreviewResult> PreviewExpectedAsync(string file, string expectedDigest, CancellationToken ct = default)
        => PreviewCoreAsync(file, expectedDigest, ct);

    private async Task<PlanPreviewResult> PreviewCoreAsync(string file, string? expectedDigest, CancellationToken ct)
    {
        var containment = TryResolveInsideWorkspace(file);
        if (containment.Error is { } containmentError)
            return InvalidPreview(WholeDocumentError(PlanValidationCodes.EmptyString, containmentError));

        var text = await ReadFileAsync(containment.AbsolutePath!, ct).ConfigureAwait(false);
        if (text.Error is { } readError)
            return InvalidPreview(WholeDocumentError(PlanValidationCodes.JsonInvalid, readError));

        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(text.Contents));

        if (!parsed.IsValid || parsed.Plan is null || parsed.CanonicalJson is null || parsed.Digest is null)
        {
            var pendingOnInvalid = await _pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false);
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan?.Operations ?? [],
                Issues = parsed.Issues,
                Workspace = parsed.Plan?.Workspace,
                PendingChanges = pendingOnInvalid,
                CanApply = false,
            };
        }
        if (expectedDigest is not null
            && !string.Equals(parsed.Digest, expectedDigest, StringComparison.Ordinal))
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues =
                [
                    new PlanValidationIssue
                    {
                        Code = PlanValidationCodes.DigestMismatch,
                        Path = string.Empty,
                        Message = $"Proposal digest {parsed.Digest} does not match expected digest {expectedDigest}. Refusing preview.",
                    },
                ],
                Workspace = parsed.Plan.Workspace,
                PendingChanges = [],
                CanApply = false,
            };

        var pending = await _pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false);

        if (parsed.Plan.Version == 2 && !SupportsProviderPlan(parsed.Plan))
        {
            var providerPolicyDecisions = await EvaluateDeclaredPolicyAsync(parsed.Plan, ct).ConfigureAwait(false);
            var blocker = new PlanValidationIssue
            {
                Code = PlanValidationCodes.UnsupportedApplyVersion,
                Path = "/version",
                Message = "Provider operations require exact-revision authority unless every operation is a supported GitHub relationship explicitly choosing observed-non-atomic concurrency.",
            };
            PlanValidationIssue[] blockedIssues = [.. parsed.Issues, blocker];
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues = blockedIssues,
                Workspace = parsed.Plan.Workspace,
                PendingChanges = pending,
                CanApply = false,
                ReviewModel = await _reviewModel.BuildAsync(
                    parsed.Plan, parsed.Digest, blockedIssues, pending, ct: ct,
                    policyDecisions: providerPolicyDecisions)
                    .ConfigureAwait(false),
            };
        }

        if (_previewOnly || (_nativeRelationships is not null && parsed.Plan.Version != 2))
        {
            var issue = new PlanValidationIssue
            {
                Code = PlanValidationCodes.UnsupportedApplyVersion,
                Path = "/version",
                Message = "This provider composition supports review-only provider-qualified version 2 proposals; legacy ADO-id proposals are not previewed here.",
            };
            var blockedIssues = new[] { issue };
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues = blockedIssues,
                Workspace = parsed.Plan.Workspace,
                PendingChanges = pending,
                CanApply = false,
                ReviewModel = await _reviewModel.BuildAsync(
                    parsed.Plan, parsed.Digest, blockedIssues, pending, ct: ct)
                    .ConfigureAwait(false),
            };
        }

        // AB#832: refuse before the journal is touched. Importing here would register a SECOND
        // transaction against a path that already carries one — the precise state that makes a
        // later `plan status` on this path resolve someone else's work as if it were this
        // file's. Unlike the import refusal below, no review model is built: the document is
        // not reviewable-but-rejected, it is unusable at this path, and the only correct next
        // action is to re-author it at a fresh sequence.
        var previewReplacement = await DetectSourceReplacementAsync(
            containment.AbsolutePath!, parsed.Digest, ct).ConfigureAwait(false);
        if (previewReplacement is not null)
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues = [.. parsed.Issues, ReplacementIssue(previewReplacement)],
                Workspace = parsed.Plan.Workspace,
                PendingChanges = pending,
                CanApply = false,
            };

        var capturedJournal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
        if (capturedJournal?.DraftCapture is not null)
            return new PlanPreviewResult
            {
                Digest = parsed.Digest, Operations = parsed.Plan.Operations, Workspace = parsed.Plan.Workspace,
                PendingChanges = pending, CanApply = false,
                Issues = [await CaptureIssueAsync(capturedJournal, containment.AbsolutePath!, ct).ConfigureAwait(false)],
            };

        // Idempotent import — a matching digest returns the existing journal, a mismatched
        // one throws (the ledger refuses to co-sign a doctored file). We hoist that as a
        // preview-level issue rather than an unhandled exception.
        PlanJournal? journal;
        PlanOrigin previewOrigin;
        try
        {
            previewOrigin = await _origin!.GetOriginAsync(ct).ConfigureAwait(false);
            journal = await _journal.ImportAsync(
                parsed.Plan,
                parsed.CanonicalJson,
                parsed.Digest,
                containment.AbsolutePath!,
                _clock.GetUtcNow(),
                ct, origin: previewOrigin).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var issue = new PlanValidationIssue
            {
                Code = PlanValidationCodes.JsonInvalid,
                Path = string.Empty,
                Message = ex.Message,
            };
            var failedIssues = new List<PlanValidationIssue>(parsed.Issues) { issue };
            return new PlanPreviewResult
            {
                Digest = parsed.Digest,
                Operations = parsed.Plan.Operations,
                Issues = failedIssues,
                Workspace = parsed.Plan.Workspace,
                PendingChanges = pending,
                CanApply = false,
                // The document parsed into a real proposal, so it is still reviewable even
                // though the journal refused it. Withholding the model here would leave the
                // reviewer with an error string and no description of what was proposed.
                ReviewModel = await _reviewModel.BuildAsync(
                    parsed.Plan, parsed.Digest, failedIssues, pending, ct: ct)
                    .ConfigureAwait(false),
            };
        }

        var originError = journal.Operations.Any(static op => op.OutcomeReceipt is not null)
            ? "Proposal contains explicit native outcome receipts; settled intents cannot be replayed."
            : journal.Origin is null
                ? "Proposal origin is unknown. Retire it explicitly or author a fresh immutable proposal."
                : journal.Origin != previewOrigin
                    ? "Proposal origin changed. Re-preview cannot adopt the current actor; author a fresh immutable proposal."
                    : null;
        var previewIssues = originError is null ? parsed.Issues
            : [.. parsed.Issues, new PlanValidationIssue { Code = "native-settlement", Path = string.Empty, Message = originError }];
        var policyDecisions = journal.State == PlanOperationState.Planned
            ? await EvaluatePolicyAsync(parsed.Plan, ct).ConfigureAwait(false)
            : [];
        var previewCanApply = pending.Count == 0 && originError is null && !policyDecisions.Any(static d => d.IsBlocking);

        return new PlanPreviewResult
        {
            Digest = journal.Digest,
            Operations = parsed.Plan.Operations,
            Issues = previewIssues,
            Workspace = parsed.Plan.Workspace,
            PendingChanges = pending,
            CanApply = previewCanApply,
            ReviewModel = await _reviewModel.BuildAsync(
                parsed.Plan, journal.Digest, previewIssues, pending, ct: ct,
                policyDecisions: policyDecisions)
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Evaluates repository-declared rules and runtime process-rule gates against the
    /// proposal. No policy file means no repository-policy decisions; a policy or process
    /// precondition that cannot be evaluated yields a blocking <c>unavailable</c> decision.
    /// </summary>
    private async Task<IReadOnlyList<PolicyDecision>> EvaluatePolicyAsync(PlanDefinition plan, CancellationToken ct)
    {
        var decisions = new List<PolicyDecision>(
            await EvaluateDeclaredPolicyAsync(plan, ct).ConfigureAwait(false));
        decisions.AddRange(await EvaluateProcessRuleDecisionsAsync(plan, ct).ConfigureAwait(false));
        return decisions;
    }

    private async Task<IReadOnlyList<PolicyDecision>> EvaluateDeclaredPolicyAsync(
        PlanDefinition plan, CancellationToken ct, bool forExecution = false)
    {
        var decisions = new List<PolicyDecision>();
        var loaded = await _policyLoader.LoadAsync(ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure)
            decisions.Add(failure);
        else if (loaded.Policy is { } policy)
        {
            if (!forExecution)
                decisions.AddRange(await _policyEvaluator.EvaluateAsync(policy, plan, ct).ConfigureAwait(false));
            else if (_executionReader is null)
                decisions.Add(NativePolicyFactsUnavailable());
            else
            {
                try
                {
                    decisions.AddRange(await _policyEvaluator
                        .EvaluateForExecutionAsync(policy, plan, _executionReader, ct).ConfigureAwait(false));
                }
                catch (Exception error) when (error is Twig.Infrastructure.Ado.Exceptions.AdoException
                    or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    decisions.Add(NativePolicyFactsUnavailable());
                }
            }
        }
        return decisions;
    }

    private static PolicyDecision NativePolicyFactsUnavailable() => new()
    {
        RuleId = "policy-facts",
        Kind = "native-facts",
        Outcome = PolicyOutcome.Unavailable,
        Reason = "Current native policy facts are unavailable or incomplete; cached observations cannot authorize this write.",
    };

    private async Task<string?> RemainingPolicyRefusalAsync(
        string digest, string operationId, CancellationToken ct)
    {
        var journal = await _journal.GetAsync(digest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Journal disappeared before the policy gate.");
        var parsed = _parser.Parse(journal.CanonicalJson);
        if (!parsed.IsValid || parsed.Plan is null || parsed.Digest != digest)
            return "The recorded proposal cannot establish the remaining policy scope; no write was attempted.";
        var current = journal.Operations.Single(row => row.OpId == operationId);
        if (current.State is not (PlanOperationState.Planned or PlanOperationState.Confirmed))
            return null;
        if (journal.Operations.Any(row => row.Ordinal < current.Ordinal
            && row.State != PlanOperationState.Verified))
            return "An earlier operation must be reconciled before evaluating or attempting the outstanding tail.";

        var states = journal.Operations.ToDictionary(row => row.OpId, row => row.State, StringComparer.Ordinal);
        var remaining = parsed.Plan with
        {
            Operations = parsed.Plan.Operations.Where(operation => states.TryGetValue(operation.Id, out var state)
                && state is PlanOperationState.Planned or PlanOperationState.Confirmed).ToArray(),
        };
        var decisions = await EvaluateDeclaredPolicyAsync(remaining, ct, forExecution: true).ConfigureAwait(false);
        var blocking = decisions.Where(static decision => decision.IsBlocking).ToArray();
        return blocking.Length == 0 ? null : "Declared policy refuses outstanding operations: "
            + string.Join("; ", blocking.Select(static decision =>
                $"[{decision.RuleId}] {decision.OutcomeName}: {decision.Reason}"));
    }

    private async Task<IReadOnlyList<PolicyDecision>> EvaluateProcessRuleDecisionsAsync(
        PlanDefinition plan, CancellationToken ct)
    {
        var decisions = new List<PolicyDecision>();
        var carry = new Dictionary<int, WorkItemSnapshot>();
        var carryFailures = new Dictionary<int, string>();
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            switch (plan.Operations[index])
            {
                case BatchOperation batch:
                    if (carryFailures.TryGetValue(batch.WorkItemId, out var carryFailure))
                    {
                        decisions.Add(ProcessRuleDecision(
                            batch, PolicyOutcome.Unavailable, carryFailure));
                        continue;
                    }

                    var evaluation = await EvaluateProcessRuleGateAsync(batch, carry, ct).ConfigureAwait(false);
                    if (!evaluation.Outcome.IsOk)
                    {
                        var fields = evaluation.Outcome.Fields;
                        var reason = evaluation.Outcome.Message
                            ?? "Process required-field rules could not be evaluated.";
                        if (fields is { Count: > 0 })
                            reason += $" Required fields: {string.Join(", ", fields)}.";

                        decisions.Add(ProcessRuleDecision(
                            batch,
                            evaluation.Outcome.IsRefused ? PolicyOutcome.Refuse : PolicyOutcome.Unavailable,
                            reason));
                        continue;
                    }

                    if (evaluation.Snapshot is { } snapshot)
                    {
                        carry[batch.WorkItemId] = _workItemMapper.ProjectFields(
                            snapshot, batch.Fields, batch.ExpectedRevision + 1);
                        carryFailures.Remove(batch.WorkItemId);
                    }
                    break;

                case AddLinkOperation add when HasLaterBatch(plan, index, add.WorkItemId):
                    await ProjectLinkAsync(
                        add.WorkItemId, add.ExpectedRevision, add.Relation, add.OtherId, added: true)
                        .ConfigureAwait(false);
                    break;

                case RemoveLinkOperation remove when HasLaterBatch(plan, index, remove.WorkItemId):
                    await ProjectLinkAsync(
                        remove.WorkItemId, remove.ExpectedRevision, remove.Relation, remove.OtherId, added: false)
                        .ConfigureAwait(false);
                    break;
            }
        }

        return decisions;

        async Task ProjectLinkAsync(
            int workItemId,
            int expectedRevision,
            string relation,
            int otherId,
            bool added)
        {
            WorkItemSnapshot source;
            if (TryGetCarriedPreOp(carry, workItemId, expectedRevision, out var carried))
            {
                source = carried;
            }
            else
            {
                try
                {
                    source = await _revisionBound!.FetchAtRevisionAsync(
                        workItemId, expectedRevision, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    carryFailures[workItemId] =
                        $"Unable to load authoritative snapshot for work item {workItemId} at revision "
                        + $"{expectedRevision}: {ex.Message}";
                    return;
                }
            }

            carry[workItemId] = ProjectPostLinkSnapshot(
                source, relation, otherId, expectedRevision + 1, added);
            carryFailures.Remove(workItemId);
        }
    }

    private static bool HasLaterBatch(PlanDefinition plan, int operationIndex, int workItemId) =>
        plan.Operations.Skip(operationIndex + 1)
            .OfType<BatchOperation>()
            .Any(batch => batch.WorkItemId == workItemId);

    private static PolicyDecision ProcessRuleDecision(
        BatchOperation batch, PolicyOutcome outcome, string reason) =>
        new()
        {
            RuleId = "process-rule-gate",
            Kind = "process-required-fields",
            Outcome = outcome,
            Reason = reason,
            WorkItemId = batch.WorkItemId,
            OpId = batch.Id,
        };

    /// <inheritdoc />
    public async Task<PlanApplyResult> ApplyAsync(
        string file,
        string confirmedDigest,
        CancellationToken ct = default)
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

        if (_previewOnly || (_executor is null && _nativeRelationships is null))
            return TopLevelApplyFailure(
                confirmedDigest,
                "This provider composition is review-only. No confirmation, journal mutation, or provider operation was attempted.");

        if ((parsed.Plan.Version == 2 && !SupportsProviderPlan(parsed.Plan))
            || (_nativeRelationships is not null && parsed.Plan.Version != 2))
            return TopLevelApplyFailure(
                confirmedDigest,
                "Plan version 2 is reviewable but cannot be applied by this lifecycle. No confirmation, journal mutation, or provider operation was attempted.");

        if (!string.Equals(parsed.Digest, confirmedDigest, StringComparison.Ordinal))
            return TopLevelApplyFailure(
                confirmedDigest,
                $"File digest {parsed.Digest} does not match confirmed digest {confirmedDigest}.");

        // AB#832: re-checked here rather than trusted from preview, because apply re-reads the
        // file and a replacement can land in between. The digest equality just above cannot
        // catch it — the confirmed digest was computed from these same replaced bytes.
        var applyReplacement = await DetectSourceReplacementAsync(
            containment.AbsolutePath!, parsed.Digest, ct).ConfigureAwait(false);
        if (applyReplacement is not null)
            return TopLevelApplyFailure(confirmedDigest, ReplacementIssue(applyReplacement).Message);

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

        if (journal.DraftCapture is not null)
            return TopLevelApplyFailure(confirmedDigest,
                "Draft-captured proposals require the separate native Save path.");

        return await ApplyPreparedAsync(parsed, journal, pending, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Native retained-draft consumption bound to the exact retained digest and native origin.
    /// No proposal file is exported or substituted for the captured intent.
    /// </summary>
    internal async Task<PlanApplyResult> ApplyCapturedDraftAsync(
        string confirmedDigest, CancellationToken ct = default)
    {
        if (_previewOnly || _executor is null || _origin is null
            || _journal is not SqlitePlanJournalRepository journals)
            return TopLevelApplyFailure(confirmedDigest, "This composition cannot consume native draft captures.");
        var origin = await _origin.GetOriginAsync(ct).ConfigureAwait(false);
        var (journal, issue) = await journals.ReadCaptureForConsumptionAsync(confirmedDigest, origin, ct)
            .ConfigureAwait(false);
        if (journal is null || issue is not null)
            return TopLevelApplyFailure(confirmedDigest,
                issue?.Message ?? "No retained native draft capture exists for this digest.");
        var parsed = AttachWorkspaceMismatchIfAny(_parser.Parse(journal.CanonicalJson));
        if (!parsed.IsValid || parsed.Plan is null || parsed.Plan.Version != 1
            || parsed.Digest != confirmedDigest)
            return TopLevelApplyFailure(confirmedDigest, "The retained native draft proposal is invalid for this workspace.");
        var pending = await _pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false);
        if (pending.Count > 0)
            return TopLevelApplyFailure(confirmedDigest, $"Refusing to apply while {pending.Count} pending change(s) are staged.");
        var result = await ApplyPreparedAsync(parsed, journal, pending, ct).ConfigureAwait(false);
        if (!result.Failed)
        {
            try
            {
                var settlement = await journals.SettleVerifiedCaptureAsync(confirmedDigest, ct).ConfigureAwait(false);
                result = result with { DraftSettlement = settlement.Status, DraftSettlementError = settlement.Error };
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or JsonException or InvalidDataException)
            {
                result = result with
                {
                    DraftSettlement = "retained",
                    DraftSettlementError = "Remote operations are verified, but local draft settlement failed. "
                        + "Inspect the native journal and retained draft; do not replay the mutation. " + ex.Message,
                };
            }
        }
        return result;
    }

    private async Task<PlanApplyResult> ApplyPreparedAsync(
        PlanValidationResult parsed, PlanJournal journal, IReadOnlyList<PendingChangeDetail> pending,
        CancellationToken ct)
    {
        if (parsed.Plan is null || parsed.Digest is null)
            throw new InvalidOperationException("Apply requires a validated proposal.");
        var confirmedDigest = parsed.Digest;
        if (journal.Operations.Any(static row => row.OutcomeReceipt is not null))
            return TopLevelApplyFailure(confirmedDigest, "Proposal contains an explicit native outcome receipt; settled intents cannot be applied or resumed.");
        var originError = await ValidateOriginAsync(journal.Origin, ct).ConfigureAwait(false);
        if (originError is not null)
            return TopLevelApplyFailure(confirmedDigest, originError);

        // The first review uses current native facts. Each new write separately checks
        // the outstanding projection, without replaying or reauthorizing verified effects.
        IReadOnlyList<PolicyDecision> policyDecisions = [];
        if (journal.State == PlanOperationState.Planned && journal.Operations.All(static row =>
            row.State is PlanOperationState.Planned or PlanOperationState.Confirmed))
        {
            policyDecisions = await EvaluateDeclaredPolicyAsync(parsed.Plan, ct, forExecution: true).ConfigureAwait(false);
            var blocking = policyDecisions.Where(static d => d.IsBlocking).ToArray();
            if (blocking.Length > 0)
                return TopLevelApplyFailure(
                    confirmedDigest,
                    "Declared policy refuses this proposal: "
                    + string.Join("; ", blocking.Select(static d => $"[{d.RuleId}] {d.OutcomeName}: {d.Reason}")));
        }

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

        // AB#721 authoritative-snapshot carry-forward. Each verified same-item batch
        // projects the post-op authoritative state (revision + fields overlaid) into this
        // map so a subsequent gate on the same work item within THIS apply reads the
        // effective state produced by prior verified operations — never the local cache
        // and never a redundant `_revisionBound.FetchAtRevisionAsync` round-trip. The map
        // is per-apply: it is discarded on completion or on any busy short-circuit, so a
        // resumed apply reloads authoritative snapshots from scratch.
        var carry = new Dictionary<int, WorkItemSnapshot>();

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

            var step = await StepOperationAsync(parsed.Digest, row, opDef, carry, ct).ConfigureAwait(false);
            if (step.UnsettledAcknowledgment is { } acknowledgment)
            {
                var observed = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
                return new PlanApplyResult
                {
                    Digest = parsed.Digest,
                    Operations = observed?.Operations ?? [],
                    Failed = true,
                    Error = $"acknowledged-effect-unsettled: operation {row.OpId} was acknowledged remotely but its local recording fence was lost."
                        + (observed is null ? " The current journal is unavailable; no current operation rows can be reported." : string.Empty)
                        + " Do not retry the mutation. Inspect the native remote-write receipt and reconcile the winning journal row.",
                    UnsettledAcknowledgments = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [row.OpId] = acknowledgment,
                    },
                };
            }
            if (step.Busy)
            {
                // Two disjoint sources produce a busy short-circuit:
                //   • a live Applying lease held by another actor — retry once the winner
                //     completes (or the 5-minute lease expires);
                //   • an authoritative-snapshot precondition from the process-rule gate —
                //     the at-revision server snapshot required for evaluation could not
                //     be loaded, so no rule decision can be trusted.
                // In BOTH cases: do NOT read back, do NOT terminalise, and do NOT touch
                // the header. Return a top-level refusal so the caller retries; the plan
                // row stays Confirmed and re-apply resumes the same journal.
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

        var replacement = await DetectSourceReplacementAsync(
            containment.AbsolutePath!, parsed.Digest, ct).ConfigureAwait(false);

        var journal = await _journal.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
        if (journal is null)
            // AB#832: "no journal" is only an honest answer when nothing was ever journaled at
            // this path. When something was, the file was replaced, and returning null here
            // reports a clobbered audit trail as a plan that was simply never previewed.
            return replacement is null
                ? null
                : new PlanStatusResult
                {
                    Found = false,
                    Digest = parsed.Digest,
                    Replacement = replacement,
                };

        return new PlanStatusResult
        {
            Issues = journal.DraftCapture is null ? [] : [await CaptureIssueAsync(journal, containment.AbsolutePath!, ct).ConfigureAwait(false)],
            Found = true,
            Digest = journal.Digest,
            State = journal.State,
            Operations = journal.Operations,
            Error = journal.Error,
            Origin = journal.Origin,
            Replacement = replacement,
        };
    }

    private async Task<PlanValidationIssue> CaptureIssueAsync(PlanJournal journal, string sourcePath, CancellationToken ct)
    {
        if (_journal is not Persistence.SqlitePlanJournalRepository native)
            return new() { Code = "draft-capture-missing-evidence", Path = string.Empty,
                Message = "The native local capture observation store is unavailable." };
        // Both sides pass the same containment resolution: a capture recorded through a
        // checkout alias (a junction, or macOS /var -> /private/var) stays readable, while a
        // retained path that resolves outside the workspace or to a different file is refused.
        var retained = TryResolveInsideWorkspace(journal.SourcePath);
        if (retained.Error is { } retainedError)
            return new() { Code = "draft-capture-stale", Path = string.Empty,
                Message = "The retained native capture source path is not usable in this workspace: " + retainedError };
        if (!native.SourcePathComparer.Equals(retained.AbsolutePath, sourcePath))
            return new() { Code = "draft-capture-stale", Path = string.Empty,
                Message = "The file is not at the retained native capture source path." };
        if (_origin is not { } origin)
            return new() { Code = "draft-capture-stale", Path = string.Empty,
                Message = "Native capture origin is unavailable in this provider composition." };
        try
        {
            return await native.CaptureIssueAsync(journal,
                await origin.GetOriginAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new() { Code = "draft-capture-stale", Path = string.Empty,
                Message = "Native capture origin is unavailable or changed: " + ex.Message };
        }
    }

    /// <summary>
    /// AB#832: reports whether the file at <paramref name="absolutePath"/> has been replaced
    /// since a transaction was journaled against that path.
    /// <para>
    /// The discriminator is the existence of journaled digests for this path OTHER than the
    /// one its bytes currently hash to. The repository owns the host-specific source-path
    /// identity rule, so this lookup naturally collapses casing aliases on case-insensitive
    /// filesystems while keeping Linux paths distinct.
    /// </para>
    /// </summary>
    private async Task<PlanSourceReplacement?> DetectSourceReplacementAsync(
        string absolutePath,
        string? currentDigest,
        CancellationToken ct)
    {
        var journaled = _journal is Persistence.SqlitePlanJournalRepository native
            ? JournaledDigestsForPhysicalPath(native, absolutePath, ct)
            : await _journal.GetDigestsBySourcePathAsync(absolutePath, ct).ConfigureAwait(false);
        if (journaled is not { Count: > 0 })
            return null;

        var superseded = new List<string>(journaled.Count);
        var currentJournaled = false;
        foreach (var digest in journaled)
        {
            if (currentDigest is not null && string.Equals(digest, currentDigest, StringComparison.Ordinal))
                currentJournaled = true;
            else
                superseded.Add(digest);
        }

        // Every journaled digest for this path is the current one: the ordinary, honest case of
        // a file previewed once (or re-previewed unchanged).
        if (superseded.Count == 0)
            return null;

        return new PlanSourceReplacement
        {
            SourcePath = absolutePath,
            CurrentDigest = currentDigest,
            SupersededDigests = superseded,
            CurrentDigestJournaled = currentJournaled,
        };
    }

    /// <summary>
    /// Digests journaled for the physical file at <paramref name="absolutePath"/>, in
    /// <c>previewed_at, digest</c> order. Exact source-path rows always count. A native draft
    /// capture recorded through a checkout alias (before captures retained the canonical path)
    /// counts only when its retained path still resolves inside the workspace to this same
    /// file; a retained path that is invalid, unresolvable or outside the workspace is skipped,
    /// never merged. Journal rows are never rewritten. Cost is proportional to the number of
    /// distinct captured paths, each resolved against the filesystem once per call — the
    /// resolution cache deliberately does not outlive this call, because a link can be
    /// retargeted between calls.
    /// </summary>
    private IReadOnlyList<string> JournaledDigestsForPhysicalPath(
        Persistence.SqlitePlanJournalRepository native,
        string absolutePath,
        CancellationToken ct)
    {
        var resolved = new Dictionary<string, bool>(StringComparer.Ordinal);
        var digests = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in native.GetSourcePathCandidates(absolutePath))
        {
            if (!native.SourcePathComparer.Equals(candidate.SourcePath, absolutePath))
            {
                if (!resolved.TryGetValue(candidate.SourcePath, out var samePhysicalFile))
                {
                    ct.ThrowIfCancellationRequested();
                    var containment = TryResolveInsideWorkspace(candidate.SourcePath);
                    samePhysicalFile = containment.Error is null
                        && native.SourcePathComparer.Equals(containment.AbsolutePath, absolutePath);
                    resolved[candidate.SourcePath] = samePhysicalFile;
                }

                if (!samePhysicalFile)
                    continue;
            }

            if (seen.Add(candidate.Digest))
                digests.Add(candidate.Digest);
        }

        return digests;
    }

    private static PlanValidationIssue ReplacementIssue(PlanSourceReplacement replacement)
    {
        var superseded = string.Join(", ", replacement.SupersededDigests);
        var message = replacement.CurrentDigestJournaled
            ? $"Plan file '{replacement.SourcePath}' has carried more than one transaction "
              + $"(journaled: {superseded}, and its current digest {replacement.CurrentDigest}). "
              + "Plan files are single-use; author the next transaction at a fresh sequence."
            : $"Plan file '{replacement.SourcePath}' was replaced after it was previewed "
              + $"(journaled: {superseded}; its current bytes hash to {replacement.CurrentDigest}). "
              + "Plan files are single-use; author the next transaction at a fresh sequence.";

        return new PlanValidationIssue
        {
            Code = PlanValidationCodes.SourceReplaced,
            Path = string.Empty,
            Message = message,
        };
    }

    /// <inheritdoc />
    public async Task<PlanSeedDescriptor?> DescribeSeedAsync(int seedId, CancellationToken ct = default)
    {
        if (_stagedRegistry is null || _publishIdMap is null || _seedLinkRepo is null)
            return null;

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
    /// <item>An authoritative-snapshot precondition from the process-rule gate — the
    ///   at-revision server snapshot the rule set needs to evaluate a batch could not be
    ///   loaded (transient network failure). The plan row stays Confirmed; the caller
    ///   re-applies the same digest once ADO is reachable.</item>
    /// </list>
    /// Both surface via <see cref="Busy"/> because the top-level handling is identical: a
    /// top-level refusal, no journal terminalisation, no header completion.
    /// </summary>
    private readonly record struct StepResult(
        PlanOperationState State, string? BusyMessage, string? UnsettledAcknowledgment = null)
    {
        public bool Busy => BusyMessage is not null;

        public static StepResult Terminal(PlanOperationState state) => new(state, null);

        public static StepResult Lease(string message) => new(PlanOperationState.Applying, message);

        /// <summary>
        /// Retryable precondition (AB#673, AB#719): row remains at Confirmed on the
        /// caller's side — this method never persists any state transition for a
        /// precondition.
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
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        if (row.OutcomeReceipt is not null)
            return StepResult.NeedsRefresh("This operation has been explicitly settled; native admission refuses replay.");
        var journal = await _journal.GetAsync(digest, ct).ConfigureAwait(false);
        var originError = await ValidateOriginAsync(journal?.Origin, ct).ConfigureAwait(false);
        if (originError is not null)
            return StepResult.NeedsRefresh(originError);
        // Fast paths: already terminal.
        if (row.State is PlanOperationState.Verified or PlanOperationState.Failed
            or PlanOperationState.Indeterminate)
            return StepResult.Terminal(row.State);

        var currentState = row.State;

        if (currentState == PlanOperationState.Confirmed)
        {
            // Runtime process-gate outcome (AB#673, AB#719, AB#721):
            //   • Refused → terminal Failed BEFORE the wire attempt. The row never claims
            //     Applying and the executor is never called for this op.
            //   • NeedsRefresh → retryable precondition. The authoritative expected-
            //     revision snapshot required to evaluate the rule set could not be
            //     loaded. Leave the row at Confirmed, do NOT terminalise, and surface a
            //     top-level refusal so the caller retries the same digest.
            var gate = await EvaluateProcessRuleGateAsync(opDef, carry, ct).ConfigureAwait(false);
            if (gate.Outcome.IsRefused)
            {
                return StepResult.Terminal(await MarkTerminalAsync(
                    digest, row.OpId, gate.Outcome.Message!, PlanOperationState.Failed, ct,
                    gate.Outcome.Fields is { } missingFields
                        ? PlanOperationExecutor.SerializeMissingFields(opDef, missingFields, gate.Snapshot?.Revision)
                        : null)
                    .ConfigureAwait(false));
            }
            if (gate.Outcome.IsRefreshRequired)
            {
                return StepResult.NeedsRefresh(gate.Outcome.Message!);
            }

            var policyRefusal = await RemainingPolicyRefusalAsync(digest, row.OpId, ct).ConfigureAwait(false);
            if (policyRefusal is not null)
                return StepResult.NeedsRefresh(policyRefusal);

            // Persist Applying BEFORE the ADO call — the transition is the durable evidence
            // that the operation was attempted.
            bool moved;
            if (journal?.DraftCapture is not null)
            {
                if (_journal is not SqlitePlanJournalRepository captures || journal.Origin is null)
                    return StepResult.NeedsRefresh("Native captured-draft admission is unavailable.");
                var admission = await captures.TryStartCapturedOperationAsync(
                    digest, row.OpId, journal.Origin, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                if (admission.Error is not null)
                    return StepResult.NeedsRefresh(admission.Error);
                moved = admission.Started;
            }
            else
                moved = await _journal.TryTransitionOperationAsync(
                    digest, row.OpId,
                    PlanOperationState.Confirmed, PlanOperationState.Applying,
                    _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (!moved)
            {
                // Another actor advanced this row while we were choosing — reload and
                // route off the persisted state. If the reload observes a fresh Applying
                // lease held by the winner, this returns busy without any readback or
                // termination.
                return await ResumeFromObservedRowAsync(digest, row.OpId, opDef, carry, ct).ConfigureAwait(false);
            }

            var applyResult = _nativeRelationships is not null && opDef is ProviderMutationOperation providerOperation
                ? await _nativeRelationships.ExecuteAsync(providerOperation, journal?.Origin, ct).ConfigureAwait(false)
                : await _executor!.ExecuteAsync(opDef, ct).ConfigureAwait(false);

            // Applied / MappedPublish: atomic Applying → Applied+result. Failed: terminal
            // Failed. Indeterminate: readback while still Applying — a proven effect still
            // walks Applied+Verified via the atomic record; anything else terminalises.
            var stepResult = applyResult.Outcome switch
            {
                PlanExecutionOutcome.Applied or PlanExecutionOutcome.MappedPublish
                    => await RecordAppliedAndVerifyAsync(digest, row.OpId, opDef, applyResult, carry, ct)
                        .ConfigureAwait(false),
                PlanExecutionOutcome.Failed
                    => StepResult.Terminal(await MarkTerminalAsync(
                        digest, row.OpId, applyResult.Error!, PlanOperationState.Failed, ct, applyResult.ResultJson)
                        .ConfigureAwait(false)),
                _ => await ResolveIndeterminateExecuteAsync(digest, row.OpId, opDef, applyResult, carry, ct)
                        .ConfigureAwait(false),
            };

            // AB#721: only a clean Verified operation with the executor's own NewRevision
            // may advance the carry. A verified batch projects its authored field overlay;
            // a verified link advances an existing same-item snapshot (and parent state when
            // applicable). Crash-recovery and indeterminate-then-readback paths deliberately
            // do not populate because the executor never proved its own write shape landed.
            if (stepResult.State == PlanOperationState.Verified
                && applyResult.NewRevision is int newRev)
            {
                switch (opDef)
                {
                    case BatchOperation completedBatch when gate.Snapshot is not null:
                        carry[completedBatch.WorkItemId] =
                            _workItemMapper.ProjectFields(gate.Snapshot, completedBatch.Fields, newRev);
                        break;
                    case AddLinkOperation add when TryGetCarriedPreOp(carry, add.WorkItemId, add.ExpectedRevision, out var addPreOp):
                        carry[add.WorkItemId] = ProjectPostLinkSnapshot(addPreOp, add.Relation, add.OtherId, newRev, added: true);
                        break;
                    case RemoveLinkOperation remove when TryGetCarriedPreOp(carry, remove.WorkItemId, remove.ExpectedRevision, out var removePreOp):
                        carry[remove.WorkItemId] = ProjectPostLinkSnapshot(removePreOp, remove.Relation, remove.OtherId, newRev, added: false);
                        break;
                }
            }

            return stepResult;
        }

        if (currentState == PlanOperationState.Applying)
        {
            // Observed Applying at loop entry. Only rows older than the lease window are
            // crash-recoverable; a fresh Applying row is a live winner and we refuse.
            if (IsFreshApplyingLease(row))
                return StepResult.Lease(BusyLeaseMessage(row));

            // Stale Applying → crash-recovery: read back BEFORE claiming Applied.
            return await RecoverFromApplyingAsync(digest, row.OpId, opDef, carry, ct).ConfigureAwait(false);
        }

        if (currentState == PlanOperationState.Applied)
        {
            // Applied recovery is verify-only: no readback→Applied claim, just readback +
            // Applied → Verified. The atomic record already stamped applied_at and result.
            var acknowledged = new PlanExecutionResult(
                PlanExecutionOutcome.Applied, row.ResultJson, null, null, null);
            return await FinalizeAppliedAsync(digest, row.OpId, opDef, acknowledged, carry, ct).ConfigureAwait(false);
        }

        // Planned: prior confirmation loop should have moved this to Confirmed. Guard: treat
        // as Confirmed and step again in a single turn.
        if (currentState == PlanOperationState.Planned)
        {
            await _journal.TryTransitionOperationAsync(
                digest, row.OpId,
                PlanOperationState.Planned, PlanOperationState.Confirmed, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            return await ResumeFromObservedRowAsync(digest, row.OpId, opDef, carry, ct).ConfigureAwait(false);
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
        Dictionary<int, WorkItemSnapshot> carry,
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

        return await StepOperationAsync(digest, refreshedRow, opDef, carry, ct).ConfigureAwait(false);
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
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        var outcome = await ReadbackOperationAsync(digest, opDef, default, ct).ConfigureAwait(false);
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
                return await ResumeFromObservedRowAsync(digest, opId, opDef, carry, ct).ConfigureAwait(false);
            }

            var verified = await PromoteAppliedToVerifiedAsync(digest, opId, opDef, outcome, ct)
                .ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);

            // Lost the Applied → Verified CAS — reload and route off the persisted state
            // rather than claiming a state the ledger does not hold.
            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        return StepResult.Terminal(await MarkTerminalAsync(
            digest, opId, outcome.Error ?? (outcome.Deterministic ? "failed" : "indeterminate"),
            outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate, ct, outcome.ResultJson)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// The SINGLE Applied → Verified promotion (AB#754/755). The readback's warning detail —
    /// server-generated or ADO-canonicalized differences the refreshed read proved harmless —
    /// is passed INTO the conditional transition, so it lands in the same row update that
    /// performs the CAS. There is no separate pre-CAS write to strand text on a row whose
    /// transition is subsequently lost. All three lifecycle paths (winning apply,
    /// indeterminate reconciliation, stale-Applying recovery) route through here, so warning
    /// carry-through cannot drift between them any more than the comparison policy can.
    /// </summary>
    private Task<bool> PromoteAppliedToVerifiedAsync(
        string digest,
        string opId,
        PlanOperationDefinition operation,
        PlanReadbackOutcome outcome,
        CancellationToken ct)
        => _journal.TryTransitionOperationAsync(
            digest, opId,
            PlanOperationState.Applied, PlanOperationState.Verified,
            _clock.GetUtcNow(), ct, outcome.Warning,
            operation is BatchOperation ? outcome.ResultJson : null);

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
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        var recorded = await _journal.TryRecordAppliedAsync(
            digest, opId, applyResult.ResultJson, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (!recorded)
        {
            if (applyResult.Outcome == PlanExecutionOutcome.Applied && applyResult.ResultJson is { } acknowledgment)
            {
                var observed = await _journal.GetAsync(digest, ct).ConfigureAwait(false);
                var winner = observed?.Operations.FirstOrDefault(row => row.OpId == opId);
                if (winner is null || winner.State is not (PlanOperationState.Applied or PlanOperationState.Verified)
                    || !ContainsAcknowledgment(winner.ResultJson, acknowledgment))
                    return new StepResult(winner?.State ?? PlanOperationState.Indeterminate, null, acknowledgment);
            }
            // Lost the atomic Applying → Applied CAS. Reload and route off the persisted
            // state; never assume the row is where our winning-execute branch expected it.
            return await ResumeFromObservedRowAsync(digest, opId, opDef, carry, ct).ConfigureAwait(false);
        }

        return await FinalizeAppliedAsync(digest, opId, opDef, applyResult, carry, ct).ConfigureAwait(false);
    }

    internal static bool ContainsAcknowledgment(string? recordedJson, string acknowledgedJson)
    {
        if (recordedJson is null) return false;
        try
        {
            using var recorded = JsonDocument.Parse(recordedJson);
            using var acknowledged = JsonDocument.Parse(acknowledgedJson);
            if (recorded.RootElement.ValueKind != JsonValueKind.Object
                || acknowledged.RootElement.ValueKind != JsonValueKind.Object
                || !acknowledged.RootElement.EnumerateObject().Any())
                return JsonElement.DeepEquals(recorded.RootElement, acknowledged.RootElement);
            foreach (var property in acknowledged.RootElement.EnumerateObject())
                if (!recorded.RootElement.TryGetProperty(property.Name, out var value)
                    || !JsonElement.DeepEquals(value, property.Value))
                    return false;
            return true;
        }
        catch (JsonException)
        {
            // Unreadable evidence cannot settle this acknowledgment; return it to the caller.
            return false;
        }
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
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        var outcome = await ReadbackOperationAsync(digest, opDef, applyResult, ct).ConfigureAwait(false);
        if (outcome.Ok)
        {
            // The executor could not classify, so its ResultJson is null; the readback that
            // proved the effect carries the canonical shape and stamps result_json here.
            var recorded = await _journal.TryRecordAppliedAsync(
                digest, opId, outcome.ResultJson ?? applyResult.ResultJson, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (!recorded)
                return await ResumeFromObservedRowAsync(digest, opId, opDef, carry, ct).ConfigureAwait(false);

            var verified = await PromoteAppliedToVerifiedAsync(digest, opId, opDef, outcome, ct)
                .ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);
            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        // Readback couldn't prove the effect — the executor's classification stands.
        // Terminalise directly from Applying with NO Applied stamp; the executor's original
        // error message wins when readback declined to name a determinate one.
        var error = outcome.Error ?? applyResult.Error ?? "indeterminate";
        var finalState = outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate;
        return StepResult.Terminal(await MarkTerminalAsync(digest, opId, error, finalState, ct, outcome.ResultJson)
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
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        var outcome = await ReadbackOperationAsync(digest, opDef, applyResult, ct).ConfigureAwait(false);
        outcome = outcome with { ResultJson = ComposeReadbackEvidence(applyResult.ResultJson, outcome.ResultJson) };
        if (outcome.Ok)
        {
            var verified = await PromoteAppliedToVerifiedAsync(digest, opId, opDef, outcome, ct)
                .ConfigureAwait(false);
            if (verified) return StepResult.Terminal(PlanOperationState.Verified);

            return StepResult.Terminal(await ObserveActualStateAsync(digest, opId, ct).ConfigureAwait(false));
        }

        return StepResult.Terminal(await MarkTerminalAsync(
            digest, opId, outcome.Error ?? (outcome.Deterministic ? "failed" : "indeterminate"),
            outcome.Deterministic ? PlanOperationState.Failed : PlanOperationState.Indeterminate, ct, outcome.ResultJson)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Enriches the acknowledgment, never reconstructing it from a later readback.
    /// Only readback-owned properties are replaced; all other acknowledgment properties
    /// survive, including fields unknown to this version. Non-object legacy evidence is
    /// retained verbatim rather than discarded or treated as a successful acknowledgment.
    /// The composed payload is persisted by the existing guarded terminal transition.
    /// </summary>
    private static string? ComposeReadbackEvidence(string? acknowledgedJson, string? readbackJson)
    {
        if (acknowledgedJson is null) return readbackJson;
        if (readbackJson is null) return acknowledgedJson;

        using var readback = JsonDocument.Parse(readbackJson);
        JsonDocument? acknowledged = null;
        try
        {
            acknowledged = JsonDocument.Parse(acknowledgedJson);
        }
        catch (JsonException)
        {
            // Old opaque result strings remain evidence even when they cannot be merged.
        }

        using (acknowledged)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            var original = acknowledged?.RootElement;
            if (original is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (var property in obj.EnumerateObject())
                {
                    if (!IsReadbackProperty(property.Name) || !readback.RootElement.TryGetProperty(property.Name, out _))
                        property.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString("acknowledgedResultJson", acknowledgedJson);
            }
            foreach (var property in readback.RootElement.EnumerateObject())
            {
                if (IsReadbackProperty(property.Name)
                    || original is not { ValueKind: JsonValueKind.Object } originalObject
                    || !originalObject.TryGetProperty(property.Name, out _))
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private static bool IsReadbackProperty(string name)
        => name is "revision" or "diagnostics" or "fieldEvidence" or "failureDetail";

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
        CancellationToken ct,
        string? resultJson = null)
    {
        await _journal.SaveOperationErrorAsync(digest, opId, error, finalState, _clock.GetUtcNow(), ct, resultJson)
            .ConfigureAwait(false);
        return finalState;
    }

    /// <summary>
    /// One evaluation of the process-rule gate for a plan operation. <see cref="Snapshot"/>
    /// is the authoritative pre-op snapshot the outcome was decided on (batch ops only) so
    /// the lifecycle can project a post-op state into the same-item carry-forward map once
    /// the row lands Verified.
    /// </summary>
    private readonly record struct GateEvaluation(
        PlanProcessRuleGateOutcome Outcome,
        WorkItemSnapshot? Snapshot);

    /// <summary>
    /// Consults the process-rule gate for a <see cref="BatchOperation"/>. Returns
    /// <see cref="PlanProcessRuleGateOutcome.Ok"/> for non-batch kinds. Otherwise resolves
    /// the authoritative expected-revision snapshot — preferring a same-item entry the
    /// current apply has already carried forward from a prior verified operation (AB#721)
    /// and falling back to <see cref="IRevisionBoundAdoWorkItemService.FetchAtRevisionAsync"/>
    /// — maps it to a work item, and defers to
    /// <see cref="PlanProcessRuleGate.EvaluateAsync(BatchOperation, Twig.Domain.Aggregates.WorkItem, CancellationToken)"/>.
    /// A load failure surfaces as <see cref="PlanProcessRuleGateOutcome.RequiresRefresh(string)"/>
    /// — the caller MUST leave the row at Confirmed and return a top-level busy refusal.
    /// The gate never falls back to the filtered local cache: either the authoritative
    /// snapshot is available (fresh or carried), or the apply is retryable.
    /// </summary>
    private async Task<GateEvaluation> EvaluateProcessRuleGateAsync(
        PlanOperationDefinition opDef,
        Dictionary<int, WorkItemSnapshot> carry,
        CancellationToken ct)
    {
        if (opDef is not BatchOperation batch)
            return new GateEvaluation(PlanProcessRuleGateOutcome.Ok, null);

        // Same-item carry-forward: a snapshot projected from a prior verified operation on
        // this work item at exactly the current batch's expected revision IS the
        // authoritative pre-op state. Consulting the executor's own product is stronger
        // than a fresh round-trip — no cache and no drift window.
        if (carry.TryGetValue(batch.WorkItemId, out var carried)
            && carried.Revision == batch.ExpectedRevision)
        {
            var carriedSource = _workItemMapper.Map(carried);
            var carriedOutcome = await _ruleGate.EvaluateAsync(batch, carriedSource, ct).ConfigureAwait(false);
            return new GateEvaluation(carriedOutcome, carried);
        }

        WorkItemSnapshot snapshot;
        try
        {
            snapshot = await _revisionBound!.FetchAtRevisionAsync(batch.WorkItemId, batch.ExpectedRevision, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GateEvaluation(
                PlanProcessRuleGateOutcome.RequiresRefresh(
                    $"Unable to load authoritative snapshot for work item {batch.WorkItemId} at revision {batch.ExpectedRevision}: {ex.Message}"),
                null);
        }

        var source = _workItemMapper.Map(snapshot);
        var outcome = await _ruleGate.EvaluateAsync(batch, source, ct).ConfigureAwait(false);
        return new GateEvaluation(outcome, snapshot);
    }



    private static bool TryGetCarriedPreOp(
        IReadOnlyDictionary<int, WorkItemSnapshot> carry,
        int workItemId,
        int expectedRevision,
        out WorkItemSnapshot preOp)
    {
        if (carry.TryGetValue(workItemId, out var carried)
            && carried.Revision == expectedRevision)
        {
            preOp = carried;
            return true;
        }

        preOp = null!;
        return false;
    }

    private static WorkItemSnapshot ProjectPostLinkSnapshot(
        WorkItemSnapshot preOp,
        string relation,
        int otherId,
        int newRevision,
        bool added)
    {
        var projected = new Dictionary<string, string?>(preOp.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["System.Rev"] = newRevision.ToString(CultureInfo.InvariantCulture),
        };
        var parentId = preOp.ParentId;
        if (string.Equals(relation, "parent", StringComparison.OrdinalIgnoreCase))
        {
            parentId = added ? otherId : null;
            if (added)
                projected["System.Parent"] = otherId.ToString(CultureInfo.InvariantCulture);
            else
                projected.Remove("System.Parent");
        }

        return preOp with
        {
            Revision = newRevision,
            ParentId = parentId,
            Fields = projected,
        };
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

        var resolvedFile = CanonicalPathResolver.Resolve(file);
        if (resolvedFile.Reason is { } fileRefusal)
            return new Containment(null, $"Invalid file path: {fileRefusal}");

        var resolvedRoot = CanonicalPathResolver.Resolve(_paths.RepoRoot);
        if (resolvedRoot.Reason is { } rootRefusal)
            return new Containment(null, $"Workspace root could not be resolved: {rootRefusal}");

        var full = resolvedFile.Path!;
        var root = resolvedRoot.Path!;
        if (!IsInside(root, full))
            return new Containment(null, $"File '{file}' is outside the workspace root '{root}'.");

        return new Containment(full, null);
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

    private bool SupportsProviderPlan(PlanDefinition plan) =>
        _nativeRelationships is not null && plan.Operations.Count > 0
        && plan.Operations.All(GitHubNativeRelationshipService.Supports);

    private async Task<PlanReadbackOutcome> ReadbackOperationAsync(
        string digest, PlanOperationDefinition operation, PlanExecutionResult applied, CancellationToken ct)
    {
        if (_nativeRelationships is not null && operation is ProviderMutationOperation provider)
        {
            var journal = await _journal.GetAsync(digest, ct).ConfigureAwait(false);
            return await _nativeRelationships.ReadbackAsync(provider, journal?.Origin, ct).ConfigureAwait(false);
        }
        return await _executor!.ReadbackAsync(operation, applied, ct).ConfigureAwait(false);
    }

    private PlanValidationResult AttachWorkspaceMismatchIfAny(PlanValidationResult parsed)
    {
        if (parsed.Plan is null) return parsed;
        var ws = parsed.Plan.Workspace;
        var organization = _nativeRelationships is not null ? _config.GitHub?.Host : _config.Organization;
        var project = _nativeRelationships is not null ? _config.GitHub?.Repository : _config.Project;
        if (string.Equals(ws.Organization, organization, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ws.Project, project, StringComparison.OrdinalIgnoreCase))
            return parsed;

        var mismatch = new PlanValidationIssue
        {
            Code = PlanValidationCodes.EmptyString,
            Path = "/workspace",
            Message =
                $"Plan workspace {ws.Organization}/{ws.Project} does not match active workspace "
                + $"{organization}/{project}.",
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
