using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Server;

public sealed record EditorInputValues(FieldDto[] Fields, RelationshipIntentDto[]? Links = null, bool? Adding = null);
public sealed record EditorInputReference(string Key, long Revision);
public sealed record EditorInputWriteBody(Guid Ticket, string Key, long ExpectedRevision, Guid WriteId, EditorInputValues Values);
public sealed record EditorInputObserveBody(Guid Ticket, string Key);
public sealed record EditorInputDiscardBody(Guid Ticket, string Key, long ExpectedRevision, Guid WriteId);
public sealed record EditorInputRecoveryDto(string Key, long Revision, Guid? WriteId, string Status, EditorInputValues? Values);

internal sealed record EditorInputIdentity(Guid SourceId, string Root, string SourceBinding, string? RepositoryRoot,
    Guid BenchId, string ItemId, string Mode, Guid StoreInstance, bool PlanMode, Guid PlanId, Guid ScopeId, string? EndpointId);
internal sealed record EditorInputCapture(EditorInputIdentity Identity, string Baseline, EditorFormDto Form, RelationshipFormDto? Pair)
{
    internal string Key => InputDigest(JsonSerializer.Serialize(Identity, TransportJson.Default.EditorInputIdentity));
    internal static string InputDigest(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
internal sealed record EditorInputRecord(string Key, EditorInputIdentity Identity, string Baseline, long Revision,
    Guid WriteId, string Status, EditorInputValues? Values);
internal sealed record EditorInputDocument(int Version, Guid StoreId, Guid ProfileId, long Revision, EditorInputRecord[] Records);

public sealed partial class ViewSessions
{
    // Shares the owner profile's private directory, lease, revocation checks and atomic durable writer.
    internal EditorInputRecoveryDto ReadInputs(ViewSession session, EditorInputCapture capture)
        => Inputs(session, capture, document => Recovery(capture,
            document.Records.SingleOrDefault(record => record.Key == capture.Key), document.Revision));

    internal EditorInputRecoveryDto WriteInputs(ViewSession session, EditorInputCapture capture, long expectedRevision,
        Guid writeId, EditorInputValues? values)
        => Inputs(session, capture, document =>
        {
            var records = document.Records;
            var old = records.SingleOrDefault(record => record.Key == capture.Key);
            if (expectedRevision < 0 || expectedRevision == long.MaxValue || writeId == Guid.Empty)
                throw new HttpFailure(400, "InvalidInput", "Use an exact input revision and a new nonempty input write identity.");
            if ((old?.Revision ?? document.Revision) != expectedRevision || old?.WriteId == writeId)
                throw new HttpFailure(409, "InputRevisionConflict", "Observe the original input write and current revision before another attempt.");
            if (values is not null)
            {
                if (old is { Values: not null } && old.Baseline != capture.Baseline)
                    throw new HttpFailure(409, "InputBaselineConflict", "Explicitly discard conflicting inputs before retaining a different native baseline.");
                ValidateInputs(capture, values);
            }
            var next = new EditorInputRecord(capture.Key, capture.Identity, capture.Baseline, checked(document.Revision + 1),
                writeId, values is null ? "Discarded" : "Restored", values);
            var updated = records.Where(record => record.Key != capture.Key).Append(next).ToArray();
            if (updated.Length > 64)
            {
                var reclaimed = updated.Where(record => record.Values is null && record.Key != next.Key)
                    .MinBy(record => record.Revision);
                if (reclaimed is not null) updated = updated.Where(record => record != reclaimed).ToArray();
            }
            SaveInputs(session, document with { Revision = next.Revision, Records = updated });
            return Recovery(capture, next, next.Revision);
        });

    internal void RequireInputs(ViewSession session, EditorInputCapture? capture, EditorInputReference? reference,
        EditorInputValues submitted)
    {
        if (reference is null) return; // Existing native clients retain their original protected operation contract.
        if (capture is null || reference.Key != capture.Key)
            throw new HttpFailure(409, "InputRevisionConflict", "Open the exact input buffer before submitting its acknowledged revision.");
        var saved = ReadInputs(session, capture);
        if (saved.Status != "Restored" || saved.Revision != reference.Revision || saved.Values is null
            || !SameInputs(saved.Values, submitted))
            throw new HttpFailure(409, "InputRevisionConflict", "Submit exactly the acknowledged input revision, through its original native editor.");
    }

    internal void ConsumeInputs(ViewSession session, EditorInputCapture? capture, EditorInputReference? reference)
    {
        if (reference is null || capture is null) return;
        _ = Inputs(session, capture, document =>
        {
            var records = document.Records;
            var old = records.SingleOrDefault(record => record.Key == reference.Key);
            // A successful older operation never deletes a newer acknowledgement.
            if (old is not null && old.Revision == reference.Revision && old.Values is not null)
                SaveInputs(session, document with
                {
                    Revision = checked(document.Revision + 1),
                    Records = records.Select(record => record == old
                        ? record with { Revision = checked(document.Revision + 1), Status = "Consumed", Values = null } : record).ToArray(),
                });
            return true;
        });
    }

    private T Inputs<T>(ViewSession session, EditorInputCapture capture, Func<EditorInputDocument, T> action)
    {
        lock (gate)
        {
            RequireOwner(session);
            if (session.Expires <= time.GetUtcNow())
                throw new HttpFailure(403, "CapabilityRejected", "Authenticate a fresh owner view before accessing private inputs.");
            using var lease = ProfileLease(session);
            RequireProfile(session, ReadDocument(session));
            if (!session.DisplayContexts.Values.Any(source => source.Source.Id == capture.Identity.SourceId
                && source.Root == capture.Identity.Root
                && Bench.Domain.CatalogFingerprint.Binding(source.Source.Binding) == capture.Identity.SourceBinding)
                || session.CurrentBenchId != capture.Identity.BenchId || session.RepositoryRoot != capture.Identity.RepositoryRoot)
                throw new HttpFailure(409, "Conflict", "The exact private input source, Bench or context changed.");
            var path = InputsPath(session);
            if (!File.Exists(path)) return action(new(1, session.CatalogId, session.ProfileId, 0, []));
            if (new FileInfo(path).Length > InputDocumentBytes)
                throw new InvalidDataException("Private editor inputs exceed their bound.");
            var document = JsonSerializer.Deserialize(File.ReadAllText(path), TransportJson.Default.EditorInputDocument)
                ?? throw new InvalidDataException("Private editor inputs are invalid.");
            if (document.Version != 1 || document.StoreId != session.CatalogId || document.ProfileId != session.ProfileId
                || document.Revision < 0 || document.Revision == long.MaxValue || document.Records.Length > 64
                || document.Records.Select(record => record.Key).Distinct().Count() != document.Records.Length
                || document.Records.Any(record => record.Revision <= 0 || record.Revision > document.Revision || record.WriteId == Guid.Empty
                    || record.Key != EditorInputCapture.InputDigest(JsonSerializer.Serialize(record.Identity, TransportJson.Default.EditorInputIdentity))
                    || record.Status is not ("Restored" or "Discarded" or "Consumed") || (record.Status == "Restored") != (record.Values is not null)))
                throw new InvalidDataException("Private editor input identity is invalid.");
            return action(document);
        }
    }

    private const int InputDocumentBytes = 1024 * 1024;
    private string InputsPath(ViewSession session) => NativePath(session) + ".inputs";
    private void SaveInputs(ViewSession session, EditorInputDocument document)
    {
        if (document.Revision == long.MaxValue)
            throw new HttpFailure(409, "InputLimit", "The private input revision bound has been reached; no input write was committed.");
        if (document.Records.Length > 64)
            throw new HttpFailure(400, "InputLimit", "The profile has reached its 64 private input buffer bound.");
        var json = JsonSerializer.Serialize(document, TransportJson.Default.EditorInputDocument);
        if (Encoding.UTF8.GetByteCount(json) > InputDocumentBytes)
            throw new HttpFailure(400, "InputLimit", "Private inputs exceed their 1 MiB UTF-8 profile bound.");
        WriteAtomic(InputsPath(session), json);
    }

    private static EditorInputRecoveryDto Recovery(EditorInputCapture capture, EditorInputRecord? record, long revision)
        => record is null ? new(capture.Key, revision, null, "Empty", null)
            : record.Values is not null && record.Baseline != capture.Baseline
                ? new(capture.Key, record.Revision, record.WriteId, "Conflict", null)
                : new(capture.Key, record.Revision, record.WriteId, record.Status, record.Values);

    private static bool SameInputs(EditorInputValues left, EditorInputValues right)
        => left.Adding == right.Adding && JsonSerializer.Serialize(left.Links, TransportJson.Default.RelationshipIntentDtoArray)
            == JsonSerializer.Serialize(right.Links, TransportJson.Default.RelationshipIntentDtoArray)
            && left.Fields.Length == right.Fields.Length && left.Fields.All(field => right.Fields.Any(other =>
                field.Reference == other.Reference && field.Value.Kind == other.Value.Kind
                && field.Value.DataType == other.Value.DataType && JsonElement.DeepEquals(field.Value.Value, other.Value.Value)));

    private static void ValidateInputs(EditorInputCapture capture, EditorInputValues values)
    {
        if (values.Fields is null || values.Fields.Length > 64
            || values.Fields.Any(field => field is null || field.Value is null || string.IsNullOrEmpty(field.Reference) || field.Reference.Length > 256)
            || values.Fields.Select(field => field.Reference).Distinct().Count() != values.Fields.Length
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(values, TransportJson.Default.EditorInputValues)) > 256 * 1024)
            throw new HttpFailure(400, "InvalidInput", "Use at most 64 unique bounded raw input fields and 256 KiB UTF-8 per buffer.");
        var local = capture.Identity.Mode == "local-relationship";
        if (local ? values.Fields.Length != 0 || values.Links is not null
            : values.Adding is not null || (capture.Pair is null && values.Links is not null))
            throw new HttpFailure(400, "InvalidInput", "Raw inputs cannot cross scalar, relationship or LOCAL-only modes.");
        foreach (var field in values.Fields)
        {
            var definition = capture.Form.Fields.SingleOrDefault(value => value.Reference == field.Reference);
            if (definition is null || field.Value.Kind is "Raw"
                || field.Value.Kind != "Null" && field.Value.Kind != definition.Kind
                || field.Value.DataType != definition.Value.DataType || !RawShape(field.Value))
                throw new HttpFailure(400, "InvalidInput", "The raw field shape does not match this native editor capture.");
        }
        if (values.Links is { } links && (links.Length > 128 || capture.Pair is null || links.Any(link => link is null
            || link.EndpointId != capture.Identity.EndpointId || link.Relation is not { Length: > 0 and <= 256 }
            || !capture.Pair.Choices.Contains(link))))
            throw new HttpFailure(400, "InvalidInput", "Choose bounded raw relationship inputs for the exact explicit native endpoint.");
    }

    private static bool RawShape(FieldValueDto field) => field.Kind switch
    {
        "Null" => field.Value.ValueKind == JsonValueKind.Null,
        "Boolean" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "StringList" => field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() <= 256
            && field.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 65536),
        "Text" or "Html" or "Markdown" or "Identity" or "Integer" or "Decimal" or "Instant"
            => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Length <= 65536,
        _ => false,
    };
}

[JsonSerializable(typeof(EditorInputValues))]
[JsonSerializable(typeof(EditorInputReference))]
[JsonSerializable(typeof(EditorInputWriteBody))]
[JsonSerializable(typeof(EditorInputObserveBody))]
[JsonSerializable(typeof(EditorInputDiscardBody))]
[JsonSerializable(typeof(EditorInputRecoveryDto))]
[JsonSerializable(typeof(EditorInputIdentity))]
[JsonSerializable(typeof(EditorInputDocument))]
[JsonSerializable(typeof(RelationshipIntentDto[]))]
internal sealed partial class TransportJson;
