using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscussionRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string Bench,
    [property: JsonRequired] string ExpectBench,
    [property: JsonRequired] string ExpectIdentity,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionCursor? Cursor);

/// <summary>Mismatch detection only. Neither the context nor its cursor authenticates a caller.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscussionContext(
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string BenchId,
    [property: JsonRequired] string BenchName,
    [property: JsonRequired] string BenchContentsDigest,
    [property: JsonRequired] string IdentityId,
    [property: JsonRequired] string BindingRevision,
    [property: JsonRequired] string SelectionSource,
    [property: JsonRequired] string SelectionRevision,
    [property: JsonRequired] string AttachmentRevision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StorageGeneration);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscussionCursor(
    [property: JsonRequired] int Version,
    [property: JsonRequired] DiscussionContext Context,
    [property: JsonRequired] string ProviderToken,
    [property: JsonRequired] string OptionProfile);

internal sealed record DiscussionCapability(string Implementation, string Availability);
internal sealed record DiscussionPaging(
    string Traversal, string Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReportedTotalCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionCursor? NextCursor,
    string Scope = "provider-page", string SnapshotConsistency = "not-guaranteed");
internal sealed record DiscussionError(string Code, string Message, string Recovery);
internal sealed record DiscussionResult(
    int Version, string Status, DiscussionCapability Capability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionContext? Context,
    IReadOnlyList<DiscussionComment> Comments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionPaging? Paging,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionError? Error);
