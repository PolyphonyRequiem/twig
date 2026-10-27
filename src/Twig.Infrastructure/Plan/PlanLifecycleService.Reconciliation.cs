using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Plan;

public sealed partial class PlanLifecycleService
{
    public async Task<PlanReconciliationResult> ReconcileAsync(
        string file, string confirmedDigest, string opId, PlanOutcomeKind kind,
        ProposalAuthorization? authorization, string? replacementDigest = null,
        string? replacementOpId = null, CancellationToken ct = default)
    {
        if (_previewOnly || _origin is null || _executor is null || _publishIntent is null)
            return RefuseSettlement("This provider composition is review-only; native outcome reconciliation is unavailable.");

        var parsed = await ValidateAsync(file, ct).ConfigureAwait(false);
        if (!parsed.IsValid || parsed.Plan is null || parsed.Digest != confirmedDigest)
            return RefuseSettlement("Original immutable proposal and confirmed digest do not match.");
        if (!Enum.IsDefined(kind)) return RefuseSettlement("Unknown native outcome kind.");
        var decision = ProposalAuthorizationGate.Evaluate(authorization, confirmedDigest, _steering.Resolve());
        if (!decision.Authorized) return RefuseSettlement(decision.Refusal!);
        if (string.IsNullOrWhiteSpace(authorization!.Rationale))
            return RefuseSettlement("Explicit native reconciliation requires an authorizer rationale.");
        var journal = await _journal.GetAsync(confirmedDigest, ct).ConfigureAwait(false);
        var row = journal?.Operations.FirstOrDefault(op => op.OpId == opId);
        var operation = parsed.Plan.Operations.FirstOrDefault(op => op.Id == opId);
        if (journal is null || row is null || operation is null)
            return RefuseSettlement("Original digest/operation is not journaled.");
        if (kind != PlanOutcomeKind.Superseded && (replacementDigest is not null || replacementOpId is not null))
            return RefuseSettlement("Replacement selectors are only valid for supersession.");
        if (kind == PlanOutcomeKind.RejectedCreateFamily)
            return await ReconcileRejectedCreateFamilyAsync(
                file, confirmedDigest, opId, parsed.Plan, journal, row, operation, authorization!, ct)
                .ConfigureAwait(false);
        if (row.OutcomeReceipt is { } existing)
            return existing.Kind == kind && existing.ReplacementDigest == replacementDigest
                && existing.ReplacementOpId == replacementOpId
                ? new PlanReconciliationResult { Settled = true, Receipt = existing }
                : RefuseSettlement("This operation already has a different immutable outcome receipt.");
        if (row.State is PlanOperationState.Applying or PlanOperationState.Applied or PlanOperationState.Verified)
            return RefuseSettlement("In-flight and already Verified operations cannot be settled by a new receipt.");

        PlanOrigin current;
        try { current = await _origin!.GetOriginAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { return RefuseSettlement(ex.Message); }
        if (journal.Origin is { } origin &&
            (origin.WorktreeFingerprint != current.WorktreeFingerprint || origin.ConnectionRef != current.ConnectionRef))
            return RefuseSettlement("The proposal belongs to another originating attachment or endpoint.");

        string evidence;
        PlanJournalOperation? replacementRow = null;
        PublishIntent? intent = null;
        if (kind == PlanOutcomeKind.Retired)
        {
            if (row.State is not (PlanOperationState.Planned or PlanOperationState.Confirmed)
                || row.StartedAt is not null || row.AppliedAt is not null || row.VerifiedAt is not null
                || row.ResultJson is not null || row.Error is not null)
                return RefuseSettlement("Native evidence cannot prove this operation was never admitted. Its outcome remains unresolved.");
            evidence = "{\"neverAdmitted\":true,\"mutationIssued\":false}";
        }
        else
        {
            if (journal.Origin is null || journal.Origin != current)
                return RefuseSettlement("Changed or unknown origin cannot acquire the current actor. Retire a never-applied operation or review a fresh proposal.");
            if (kind == PlanOutcomeKind.Readback && row.State is not (PlanOperationState.Failed or PlanOperationState.Indeterminate))
                return RefuseSettlement("Readback reconciliation requires a historical failed or indeterminate attempt.");
            if (kind == PlanOutcomeKind.Superseded)
            {
                if (string.IsNullOrWhiteSpace(replacementDigest) || string.IsNullOrWhiteSpace(replacementOpId)
                    || replacementDigest == confirmedDigest)
                    return RefuseSettlement("Supersession requires a distinct Verified replacement digest and operation.");
                var replacement = await _journal.GetAsync(replacementDigest, ct).ConfigureAwait(false);
                replacementRow = replacement?.Operations.FirstOrDefault(op => op.OpId == replacementOpId);
                if (replacement is null || replacement.Origin != journal.Origin
                    || replacementRow?.State != PlanOperationState.Verified
                    || replacementRow.VerifiedAt is null || replacementRow.ResultJson is null
                    || replacementRow.VerifiedAt < journal.PreviewedAt
                    || !SameExpectedEffect(row.RequestJson, replacementRow.RequestJson))
                    return RefuseSettlement("Replacement is not Verified evidence for this exact original effect and authority. Unrelated success cannot settle it.");
            }
            var acknowledged = replacementRow?.ResultJson ?? row.ResultJson;
            PlanReadbackOutcome outcome;
            try { outcome = await _executor!.ReadbackForReceiptAsync(operation, acknowledged, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return RefuseSettlement("Authoritative receipt readback is unavailable; the original outcome remains unknown. " + ex.Message);
            }
            if (!outcome.Ok || outcome.ResultJson is null)
                return RefuseSettlement(outcome.Error ?? "Fresh authoritative readback did not establish the expected effect.");
            if (replacementRow is null && !ReadbackIsAttributable(operation, acknowledged, outcome.ResultJson))
                return RefuseSettlement("Readback lacks an attributable outcome revision. Matching newer state alone does not settle an unknown attempt.");
            if (operation is PublishSeedOperation seed)
            {
                intent = await _publishIntent!.GetIntentAsync(seed.StagedIdentity, ct).ConfigureAwait(false);
                if (intent is null)
                    return RefuseSettlement("No original durable publish intent can be bound to this seed outcome.");
            }
            evidence = SerializeSettlementEvidence(outcome.ResultJson, replacementRow?.ResultJson);
        }

        var receipt = new PlanOutcomeReceipt
        {
            ReceiptId = Guid.NewGuid().ToString("N"), Digest = confirmedDigest, OpId = opId,
            Kind = kind, RequestJson = row.RequestJson, Origin = journal.Origin,
            AuthorizingOrigin = current, Authorization = authorization,
            EvidenceJson = evidence, ReplacementDigest = replacementDigest, ReplacementOpId = replacementOpId,
            PublishIdentity = intent?.Identity,
            PublishIntentRecordedAt = intent?.RecordedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!await _journal.TryAppendReceiptAsync(receipt, row.State, row.ResultJson, ct).ConfigureAwait(false))
            return RefuseSettlement("Native settlement lost its durable CAS fence. Execution admission or other evidence won; reload without replaying.");
        return new PlanReconciliationResult { Settled = true, Receipt = receipt };
    }

    private async Task<PlanReconciliationResult> ReconcileRejectedCreateFamilyAsync(
        string file, string digest, string opId, PlanDefinition plan, PlanJournal journal,
        PlanJournalOperation row, PlanOperationDefinition operation, ProposalAuthorization authorization,
        CancellationToken ct)
    {
        if (operation is not PublishSeedOperation seed)
            return RefuseSettlement("Rejected-create-family evidence can settle only the original publish-seed operation.");
        if (_publishIdMap is null)
            return RefuseSettlement("The original publish-id map repository is unavailable; preserve the unknown outcome.");
        if (row.OutcomeReceipt is { } existing)
        {
            if (journal.Origin is null
                || existing.Digest != digest || existing.OpId != opId
                || existing.RequestJson != row.RequestJson
                || existing.Origin != journal.Origin || existing.AuthorizingOrigin != journal.Origin
                || !RejectedCreateFamilyReceiptValidator.IsValid(existing, seed.StagedIdentity))
                return RefuseSettlement("The existing rejected-create-family receipt is not bound to this immutable journal operation; preserve the original journal and local intent.");

            PublishIntent? settledIntent;
            try { settledIntent = await _publishIntent!.GetIntentAsync(seed.StagedIdentity, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return RefuseSettlement("The original publish intent is unavailable for receipt binding: " + ex.Message); }
            if (settledIntent is null || settledIntent.Identity != seed.StagedIdentity
                || settledIntent.PublishedId is not null
                || existing.PublishIntentRecordedAt
                    != settledIntent.RecordedAt.ToString("o", CultureInfo.InvariantCulture))
                return RefuseSettlement("The existing rejected-create-family receipt does not match the original durable publish intent.");

            return new PlanReconciliationResult { Settled = true, Receipt = existing };
        }
        if (journal.Origin is null || row.State is not (PlanOperationState.Failed or PlanOperationState.Indeterminate)
            || row.StartedAt is null || row.AppliedAt is not null || row.VerifiedAt is not null
            || row.ResultJson is not null)
            return RefuseSettlement("Rejected-create-family reconciliation requires the original failed or indeterminate publish attempt with no positive outcome.");

        if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out var failure))
            return RefuseSettlement("A stable originating worktree is required for rejected-create-family reconciliation: " + failure);

        IDisposable fence;
        try { fence = ConnectionOperationGate.Acquire(anchor.WorktreeRoot, exclusive: true); }
        catch (InvalidOperationException ex) { return RefuseSettlement(ex.Message); }
        using (fence)
        {
            PlanOrigin current;
            try { current = await _origin!.GetOriginAsync(ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { return RefuseSettlement(ex.Message); }
            if (current != journal.Origin || current.WorktreeRoot != anchor.WorktreeRoot)
                return RefuseSettlement("The original proposal authority or attached worktree changed; negative evidence cannot acquire a different actor.");

            PublishIntent? intent;
            try { intent = await _publishIntent!.GetIntentAsync(seed.StagedIdentity, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return RefuseSettlement("The exact durable publish intent is unavailable: " + ex.Message); }
            if (intent is null || intent.Identity != seed.StagedIdentity || intent.PublishedId is not null
                || intent.CompletedAt is not null)
                return RefuseSettlement("The exact original open publish intent is missing or already has a positive outcome.");
            var recordedAt = intent.RecordedAt.ToString("o", CultureInfo.InvariantCulture);
            if (await _publishIdMap.GetNewIdAsync(seed.StagedIdentity, ct).ConfigureAwait(false) is not null)
                return RefuseSettlement("A positive publish-id map already exists; rejected-family evidence cannot override it.");

            using var registry = new SqliteSystemWorktreeRegistry(_systemRegistryPath, TimeProvider.System);
            Result<PlanReconciliationResult> settled;
            try
            {
                settled = await registry.ExecuteWithConnectionRemoteWriteSnapshotAsync(
                    current.WorktreeFingerprint,
                    async snapshot =>
                    {
                        var family = new List<RejectedCreateFamilyProofMember>();
                        var expectedCorrelation = new SeedPublishCorrelation(seed.StagedIdentity, intent.RecordedAt);
                        foreach (var member in snapshot.Members)
                        {
                            var nativeIntent = member.History.Intent;
                            var correlation = nativeIntent.Request.SeedCorrelation;
                            if (correlation?.Identity != seed.StagedIdentity)
                            {
                                if (nativeIntent.Request.EffectKind == "workitem-create"
                                    && (ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(
                                            nativeIntent.Request.Payload, expectedCorrelation)
                                        || ConnectionRemoteWriteAdmission.SeedCreateHasCorrelation(
                                            nativeIntent.Request.Payload, expectedCorrelation.Tag)))
                                    return Result.Fail<PlanReconciliationResult>(
                                        "A native create carries the original staged identity marker without matching structured correlation; preserve the unknown outcome.");
                                continue;
                            }
                            if (nativeIntent.Request.EffectKind != "workitem-create"
                                || correlation.IntentRecordedAt.ToString("o", CultureInfo.InvariantCulture) != recordedAt)
                                return Result.Fail<PlanReconciliationResult>(
                                    "The exact staged identity has a native create outside the original intent correlation; preserve the unknown outcome.");
                            if (nativeIntent.RequestDigest != ConnectionRemoteWriteAdmission.RequestDigest(
                                    nativeIntent.Origin, nativeIntent.Request)
                                || nativeIntent.Fingerprint != current.WorktreeFingerprint
                                || nativeIntent.Origin.Operation.WorktreeFingerprint != current.WorktreeFingerprint
                                || !PlanOriginMatches(current, nativeIntent.Origin)
                                || nativeIntent.Request.Method != "POST"
                                || !Uri.TryCreate(nativeIntent.Request.Target, UriKind.Absolute, out var target)
                                || !ConnectionRemoteWriteAdmission.IsWithinAuthority(nativeIntent.Origin, target)
                                || ConnectionRemoteWriteAdmission.SeedCreateTitle(nativeIntent.Request.Payload) != intent.Title
                                || ConnectionRemoteWriteAdmission.SeedCreateType(nativeIntent.Request.Target) != intent.TypeName
                                || !(ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(
                                         nativeIntent.Request.Payload, correlation)
                                     || ConnectionRemoteWriteAdmission.SeedCreateHasCorrelation(
                                         nativeIntent.Request.Payload, correlation.Tag)))
                                return Result.Fail<PlanReconciliationResult>(
                                    "A native create in the exact correlation family does not match the original intent, request, or authority.");

                            var history = member.History;
                            if (history.Receipt is not { } nativeReceipt
                                || nativeReceipt.Kind != "rejected"
                                || history.Observations.Count != 1
                                || nativeReceipt.ObservationId != history.Observations[0].ObservationId
                                || nativeReceipt.IntentId != nativeIntent.IntentId
                                || nativeReceipt.RequestDigest != nativeIntent.RequestDigest
                                || !ConnectionRemoteWriteAdmission.SameAuthority(
                                    nativeReceipt.AuthorizingOrigin, nativeIntent.Origin)
                                || ConnectionRemoteWriteAdmission.ClassifyAcknowledgement(
                                    nativeIntent.Request, history.Observations[0].Response) != "rejected")
                                return Result.Fail<PlanReconciliationResult>(
                                    "Every admitted create in the exact correlation family must have one classifier-backed definitive rejection; an unknown or positive sibling remains unresolved.");
                            var observation = history.Observations[0];
                            if (observation.ObservedAt < nativeIntent.AdmittedAt
                                || nativeReceipt.RecordedAt < observation.ObservedAt)
                                return Result.Fail<PlanReconciliationResult>(
                                    "Native rejection chronology is inconsistent; preserve the original unknown outcome.");
                            family.Add(new(member.Sequence, nativeIntent, observation, nativeReceipt));
                        }

                        if (family.Count == 0)
                            return Result.Fail<PlanReconciliationResult>(
                                "No admitted native create belongs to the exact staged identity and recorded-at correlation.");

                        var proof = new RejectedCreateFamilyProof(
                            1, snapshot.SnapshotId, snapshot.Fingerprint, snapshot.HighWaterSequence,
                            seed.StagedIdentity.ToString(), recordedAt, seed.ExpectedFingerprint,
                            intent.Title, intent.TypeName, family);
                        var receipt = new PlanOutcomeReceipt
                        {
                            ReceiptId = Guid.NewGuid().ToString("N"),
                            Digest = digest,
                            OpId = opId,
                            Kind = PlanOutcomeKind.RejectedCreateFamily,
                            RequestJson = row.RequestJson,
                            Origin = journal.Origin,
                            AuthorizingOrigin = current,
                            Authorization = authorization,
                            EvidenceJson = JsonSerializer.Serialize(
                                proof, TwigJsonContext.Default.RejectedCreateFamilyProof),
                            PublishIdentity = seed.StagedIdentity,
                            PublishIntentRecordedAt = recordedAt,
                        };
                        if (!RejectedCreateFamilyReceiptValidator.IsValid(
                                receipt, seed.StagedIdentity, recordedAt))
                            return Result.Fail<PlanReconciliationResult>(
                                "The complete native family failed negative receipt validation; the original outcome remains unknown.");
                        var appended = await _journal.TryAppendRejectedCreateFamilyReceiptAsync(
                            receipt, row.State, row.ResultJson, seed.StagedIdentity, intent.RecordedAt, ct)
                            .ConfigureAwait(false);
                        return appended
                            ? Result.Ok(new PlanReconciliationResult { Settled = true, Receipt = receipt })
                            : Result.Fail<PlanReconciliationResult>(
                                "The local receipt/intent CAS lost or no longer matches the exact open publish intent; reload without replaying.");
                    }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                or JsonException or SqliteException)
            {
                return RefuseSettlement("Rejected-create-family evidence could not be atomically captured and settled: " + ex.Message);
            }
            if (!settled.IsSuccess) return RefuseSettlement(settled.Error);
            return settled.Value;
        }
    }

    private static bool PlanOriginMatches(PlanOrigin planOrigin, ResolvedConnectionBinding nativeOrigin)
        => planOrigin.WorktreeFingerprint == nativeOrigin.Operation.WorktreeFingerprint
            && planOrigin.ConnectionRef == nativeOrigin.Binding.ConnectionRef
            && planOrigin.BindingId == nativeOrigin.Binding.BindingId
            && planOrigin.BindingRevision == nativeOrigin.Binding.Revision
            && planOrigin.IdentityId == nativeOrigin.Identity.IdentityId
            && planOrigin.Method == nativeOrigin.Identity.Method
            && planOrigin.CredentialRef == nativeOrigin.Identity.CredentialRef
            && planOrigin.TenantId == nativeOrigin.Identity.TenantId
            && planOrigin.ObjectId == nativeOrigin.Identity.ObjectId
            && planOrigin.Issuer == nativeOrigin.Identity.Issuer;

    private async Task<string?> ValidateOriginAsync(PlanOrigin? expected, CancellationToken ct)
    {
        if (expected is null)
            return "Proposal origin is unknown. Refusing to adopt the current actor; explicitly retire or author a fresh immutable proposal.";
        try
        {
            var current = await _origin!.GetOriginAsync(ct).ConfigureAwait(false);
            return current == expected ? null
                : "Proposal origin attachment, binding, principal or selection revision changed. Refusing saved authorization/apply/resume; reconnect and author a fresh proposal.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException
            or HttpRequestException or JsonException or GitHub.GitHubCredentialUnavailableException)
        {
            return _nativeRelationships is null ? ex.Message
                : "Provider target or authenticated actor observation is unavailable; original proposal authority cannot be confirmed.";
        }
    }

    internal static bool SameExpectedEffect(string original, string replacement)
    {
        using var first = JsonDocument.Parse(original);
        using var second = JsonDocument.Parse(replacement);
        // A fresh replacement has its own immutable operation id and current CAS revision.
        // Every original effect must remain identical; a Verified replacement batch may also
        // carry required gate fields. Extra replacement effects never erase an original field.
        var firstCount = 0;
        var secondCount = 0;
        foreach (var property in first.RootElement.EnumerateObject())
        {
            if (property.Name is "id" or "expectedRevision") continue;
            firstCount++;
            if (!second.RootElement.TryGetProperty(property.Name, out var value)) return false;
            if (property.Name == "fields" && property.Value.ValueKind == JsonValueKind.Object
                && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in property.Value.EnumerateObject())
                    if (!value.TryGetProperty(field.Name, out var replacementField)
                        || !JsonElement.DeepEquals(field.Value, replacementField)) return false;
            }
            else if (!JsonElement.DeepEquals(property.Value, value)) return false;
        }
        foreach (var property in second.RootElement.EnumerateObject())
            if (property.Name is not ("id" or "expectedRevision")) secondCount++;
        if (firstCount != secondCount) return false;
        return true;
    }

    private static bool ReadbackIsAttributable(PlanOperationDefinition operation, string? acknowledged, string readback)
    {
        if (operation is PublishSeedOperation)
            return true; // The seed publisher binds its durable identity, fingerprint, map and intent.
        if (acknowledged is null) return false;
        using var proof = JsonDocument.Parse(acknowledged);
        using var observed = JsonDocument.Parse(readback);
        if (operation is AppendCommentOperation append)
            return proof.RootElement.TryGetProperty("workItemId", out var target)
                && target.ValueKind == JsonValueKind.Number && target.GetInt32() == append.WorkItemId
                && proof.RootElement.TryGetProperty("commentId", out var commentId)
                && commentId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(commentId.GetString())
                && proof.RootElement.TryGetProperty("body", out var body)
                && body.ValueKind == JsonValueKind.String && body.GetString() == append.EffectiveBody
                && proof.RootElement.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String && format.GetString() == "html"
                && observed.RootElement.TryGetProperty("commentId", out var observedId)
                && observedId.ValueKind == JsonValueKind.String && observedId.GetString() == commentId.GetString();
        if (operation is DeleteOperation deleted)
            return proof.RootElement.TryGetProperty("deleted", out var id) && id.ValueKind == JsonValueKind.Number
                && id.GetInt32() == deleted.WorkItemId;
        return proof.RootElement.TryGetProperty("rev", out var revision) && revision.TryGetInt32(out var expected)
            && observed.RootElement.TryGetProperty("revision", out var actual) && actual.TryGetInt32(out var current)
            && expected == current && expected > PlanOperationExecutor.ExpectedRevision(operation);
    }

    private static string SerializeSettlementEvidence(string readback, string? replacement)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("verifiedReadback");
        writer.WriteRawValue(readback);
        if (replacement is not null)
        {
            writer.WritePropertyName("verifiedReplacement");
            writer.WriteRawValue(replacement);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static PlanReconciliationResult RefuseSettlement(string error) => new() { Error = error };
}
