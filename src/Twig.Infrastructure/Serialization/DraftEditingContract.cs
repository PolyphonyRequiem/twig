using System.Text.Json.Serialization;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

/// <summary>Native observation tokens, not mutation or publication authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftEditingContext(
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] ExternalWorkItemIdentity ExternalIdentity,
    [property: JsonRequired] string BenchId,
    [property: JsonRequired] string BenchName,
    [property: JsonRequired] string BenchContentsDigest,
    [property: JsonRequired] string IdentityId,
    [property: JsonRequired] string BindingRevision,
    [property: JsonRequired] string SelectionSource,
    [property: JsonRequired] string SelectionRevision,
    [property: JsonRequired] string AttachmentRevision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StorageGeneration,
    [property: JsonRequired] string OriginDigest,
    [property: JsonRequired] string EvidenceDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftEditingChange(
    [property: JsonRequired] string FieldRef,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProposedValue);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftStageRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] DraftEditingContext NativeContext,
    [property: JsonRequired] string Generation,
    [property: JsonRequired] string BaselineRevision,
    [property: JsonRequired] string ObservedRevision,
    [property: JsonRequired] IReadOnlyList<DraftEditingChange> Changes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftRetainRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] DraftEditingContext NativeContext,
    [property: JsonRequired] string Generation,
    [property: JsonRequired] string BaselineRevision,
    [property: JsonRequired] string ObservedRevision);

internal sealed record DraftRetainedCapture(
    DraftEditingContext NativeContext, string Generation, string BaselineRevision, string ObservedRevision,
    string CurrentRevision, string Digest, string OperationId, string CanonicalJson,
    IReadOnlyList<DraftEditingEffect> Effects,
    string OriginCoverage = "editing-context-and-native-origin-digest",
    bool Authorized = false, bool CanApply = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftCaptureReview(
    [property: JsonRequired] string Digest,
    [property: JsonRequired] DraftCaptureReviewWorkspace Workspace,
    [property: JsonRequired] int WorkItemId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Generation,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] string ItemContext,
    [property: JsonRequired] string Freshness,
    [property: JsonRequired] string PublisherProvenance,
    [property: JsonRequired] IReadOnlyList<DraftEditingEffect> Effects,
    [property: JsonRequired] bool Authorized,
    [property: JsonRequired] bool CanApply);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftCaptureReviewWorkspace(
    [property: JsonRequired] string Organization,
    [property: JsonRequired] string Project);

internal sealed record DraftEditingField(string FieldRef, bool Editable, string? Reason);
internal sealed record DraftEditingEffect(string FieldRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BaselineValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProposedValue);
internal sealed record DraftEditingConflict(string FieldRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BaselineValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ObservedValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProposedValue);

/// <summary>The full ordered native form; revisions and generation never pass through a JSON number.</summary>
internal sealed record DraftEditingDocument(
    DraftEditingContext NativeContext, string Generation, string BaselineRevision, string ObservedRevision,
    string CurrentRevision, bool HasConflicts, IReadOnlyList<DraftEditingEffect> Changes,
    IReadOnlyList<DraftEditingConflict> Conflicts, IReadOnlyList<DetailControl> SystemControls,
    IReadOnlyList<DetailPage> Pages, IReadOnlyList<DraftEditingField> Fields,
    string? EligibilityIssue = null, bool Authorized = false, bool ServerFinalAuthority = true);

internal sealed record DraftEditingError(string Code, string Message, string Recovery);
internal sealed record DraftEditingResult(int Version, string Status, bool Committed,
    DraftEditingDocument? Document = null, DraftEditingError? Error = null, DraftRetainedCapture? Capture = null,
    DraftCaptureReview? Review = null);
