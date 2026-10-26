using System.Globalization;
using System.Text.Json;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Plan;

internal static class RejectedCreateFamilyReceiptValidator
{
    internal static bool IsValidStoredDisposition(string json, string kind, string? storedDigest,
        string? storedOpId, string? storedIdentity, string? storedRecordedAt, StagedIdentity identity,
        string recordedAt)
    {
        if (kind != PlanOutcomeKind.RejectedCreateFamily.ToString()
            || storedIdentity != identity.ToString() || storedRecordedAt != recordedAt)
            return false;
        try
        {
            var receipt = JsonSerializer.Deserialize(json, TwigJsonContext.Default.PlanOutcomeReceipt);
            return receipt is not null && receipt.Kind.ToString() == kind
                && receipt.Digest == storedDigest && receipt.OpId == storedOpId
                && receipt.PublishIdentity == identity
                && IsValid(receipt, identity, recordedAt);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsValidStoredOperationDisposition(string json, string kind, string? storedDigest,
        string? storedOpId, string? storedIdentity, string? storedRecordedAt,
        string digest, string opId, string requestJson)
    {
        if (kind != PlanOutcomeKind.RejectedCreateFamily.ToString()
            || storedDigest != digest || storedOpId != opId
            || !StagedIdentity.TryParse(storedIdentity, out var identity)
            || string.IsNullOrWhiteSpace(storedRecordedAt))
            return false;
        try
        {
            var receipt = JsonSerializer.Deserialize(json, TwigJsonContext.Default.PlanOutcomeReceipt);
            return receipt is not null && receipt.Kind.ToString() == kind
                && receipt.Digest == storedDigest && receipt.OpId == storedOpId
                && receipt.RequestJson == requestJson
                && receipt.PublishIdentity == identity
                && receipt.PublishIntentRecordedAt == storedRecordedAt
                && IsValid(receipt, identity, storedRecordedAt);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsValid(PlanOutcomeReceipt receipt, StagedIdentity? expectedIdentity = null,
        string? expectedRecordedAt = null)
    {
        if (receipt.Kind != PlanOutcomeKind.RejectedCreateFamily
            || receipt.PublishIdentity is not { } identity
            || string.IsNullOrWhiteSpace(receipt.PublishIntentRecordedAt)
            || receipt.ReplacementDigest is not null || receipt.ReplacementOpId is not null
            || receipt.Origin is null || receipt.AuthorizingOrigin != receipt.Origin
            || expectedIdentity is { } expected && expected != identity
            || expectedRecordedAt is not null && expectedRecordedAt != receipt.PublishIntentRecordedAt)
            return false;

        try
        {
            using var request = JsonDocument.Parse(receipt.RequestJson);
            if (request.RootElement.ValueKind != JsonValueKind.Object
                || !request.RootElement.TryGetProperty("kind", out var kind)
                || kind.GetString() != "publish-seed"
                || !request.RootElement.TryGetProperty("stagedIdentity", out var stagedIdentity)
                || stagedIdentity.GetString() != identity.ToString()
                || !request.RootElement.TryGetProperty("expectedFingerprint", out var expectedFingerprint)
                || expectedFingerprint.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(expectedFingerprint.GetString()))
                return false;

            var proof = JsonSerializer.Deserialize(receipt.EvidenceJson, TwigJsonContext.Default.RejectedCreateFamilyProof);
            if (proof is null || proof.Version != 1 || !Guid.TryParseExact(proof.SnapshotId, "N", out _)
                || string.IsNullOrWhiteSpace(proof.Fingerprint) || proof.HighWaterSequence < 1
                || proof.PublishIdentity != identity.ToString()
                || proof.PublishIntentRecordedAt != receipt.PublishIntentRecordedAt
                || proof.ExpectedSeedFingerprint != expectedFingerprint.GetString()
                || string.IsNullOrWhiteSpace(proof.PublishTitle)
                || string.IsNullOrWhiteSpace(proof.PublishTypeName)
                || proof.Members is null || proof.Members.Count < 1
                || proof.Members.Count > proof.HighWaterSequence)
                return false;

            var seenIntentIds = new HashSet<string>(StringComparer.Ordinal);
            var seenSequences = new HashSet<long>();
            foreach (var member in proof.Members)
            {
                if (member is null || member.Intent is not { } intent
                    || intent.Origin is null || intent.Origin.Binding is null
                    || intent.Origin.Identity is null || intent.Origin.Operation is null
                    || intent.Request is not { } nativeRequest
                    || member.Observation is not { } observation || observation.Response is null
                    || member.Receipt is not { } nativeReceipt
                    || nativeReceipt.AuthorizingOrigin is not { } authorizingOrigin
                    || authorizingOrigin.Binding is null || authorizingOrigin.Identity is null
                    || authorizingOrigin.Operation is null)
                    return false;
                var correlation = nativeRequest.SeedCorrelation;
                if (member.Sequence < 1 || member.Sequence > proof.HighWaterSequence
                    || !Guid.TryParseExact(intent.IntentId, "N", out _)
                    || !Guid.TryParseExact(observation.ObservationId, "N", out _)
                    || !Guid.TryParseExact(nativeReceipt.ReceiptId, "N", out _)
                    || !seenSequences.Add(member.Sequence)
                    || !seenIntentIds.Add(intent.IntentId)
                    || intent.Version != 1 || intent.Fingerprint != proof.Fingerprint
                    || intent.Origin.Operation.WorktreeFingerprint != proof.Fingerprint
                    || nativeRequest.EffectKind != "workitem-create" || nativeRequest.Method != "POST"
                    || !Uri.TryCreate(nativeRequest.Target, UriKind.Absolute, out var target)
                    || !ConnectionRemoteWriteAdmission.IsWithinAuthority(intent.Origin, target)
                    || correlation is null || correlation.Identity != identity
                    || correlation.IntentRecordedAt.ToString("o", CultureInfo.InvariantCulture) != proof.PublishIntentRecordedAt
                    || intent.RequestDigest != ConnectionRemoteWriteAdmission.RequestDigest(intent.Origin, nativeRequest)
                    || ConnectionRemoteWriteAdmission.SeedCreateTitle(nativeRequest.Payload) != proof.PublishTitle
                    || ConnectionRemoteWriteAdmission.SeedCreateType(nativeRequest.Target) != proof.PublishTypeName
                    || !(ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(nativeRequest.Payload, correlation)
                        || ConnectionRemoteWriteAdmission.SeedCreateHasCorrelation(nativeRequest.Payload, correlation.Tag))
                    || !PlanOriginMatches(receipt.Origin, intent.Origin)
                    || observation.Version != 1 || observation.IntentId != intent.IntentId
                    || observation.RequestDigest != intent.RequestDigest
                    || nativeReceipt.Version != 1 || nativeReceipt.IntentId != intent.IntentId
                    || nativeReceipt.RequestDigest != intent.RequestDigest || nativeReceipt.Kind != "rejected"
                    || nativeReceipt.ObservationId != observation.ObservationId
                    || observation.ObservedAt < intent.AdmittedAt
                    || nativeReceipt.RecordedAt < observation.ObservedAt
                    || !ConnectionRemoteWriteAdmission.SameAuthority(authorizingOrigin, intent.Origin)
                    || ConnectionRemoteWriteAdmission.ClassifyAcknowledgement(nativeRequest, observation.Response) != "rejected")
                    return false;

                using var observedResponse = JsonDocument.Parse(
                    JsonSerializer.Serialize(observation.Response, TwigJsonContext.Default.ConnectionRemoteWriteResponse));
                using var receiptResponse = JsonDocument.Parse(nativeReceipt.EvidenceJson);
                if (!JsonElement.DeepEquals(observedResponse.RootElement, receiptResponse.RootElement))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
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
}
