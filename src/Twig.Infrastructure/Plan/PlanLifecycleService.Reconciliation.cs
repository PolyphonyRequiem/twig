using System.Buffers;
using System.Text;
using System.Text.Json;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Plan;

public sealed partial class PlanLifecycleService
{
    public async Task<PlanReconciliationResult> ReconcileAsync(
        string file, string confirmedDigest, string opId, PlanOutcomeKind kind,
        ProposalAuthorization? authorization, string? replacementDigest = null,
        string? replacementOpId = null, CancellationToken ct = default)
    {
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
        if (row.OutcomeReceipt is { } existing)
            return existing.Kind == kind && existing.ReplacementDigest == replacementDigest
                && existing.ReplacementOpId == replacementOpId
                ? new PlanReconciliationResult { Settled = true, Receipt = existing }
                : RefuseSettlement("This operation already has a different immutable outcome receipt.");
        if (row.State is PlanOperationState.Applying or PlanOperationState.Applied or PlanOperationState.Verified)
            return RefuseSettlement("In-flight and already Verified operations cannot be settled by a new receipt.");

        PlanOrigin current;
        try { current = await _origin.GetOriginAsync(ct).ConfigureAwait(false); }
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
            try { outcome = await _executor.ReadbackForReceiptAsync(operation, acknowledged, ct).ConfigureAwait(false); }
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
                intent = await _publishIntent.GetIntentAsync(seed.StagedIdentity, ct).ConfigureAwait(false);
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

    private async Task<string?> ValidateOriginAsync(PlanOrigin? expected, CancellationToken ct)
    {
        if (expected is null)
            return "Proposal origin is unknown. Refusing to adopt the current actor; explicitly retire or author a fresh immutable proposal.";
        try
        {
            var current = await _origin.GetOriginAsync(ct).ConfigureAwait(false);
            return current == expected ? null
                : "Proposal origin attachment, binding, principal or selection revision changed. Refusing saved authorization/apply/resume; reconnect and author a fresh proposal.";
        }
        catch (InvalidOperationException ex) { return ex.Message; }
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
