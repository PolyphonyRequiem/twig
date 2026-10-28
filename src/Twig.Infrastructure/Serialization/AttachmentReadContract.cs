using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachmentReadRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string Action,
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string Bench,
    [property: JsonRequired] string ExpectBench,
    [property: JsonRequired] string ExpectIdentity,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ExpectedRevision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? AttachmentId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachmentReadContext(
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string BenchId,
    [property: JsonRequired] string BenchName,
    [property: JsonRequired] string BenchContentsDigest,
    [property: JsonRequired] string IdentityId,
    [property: JsonRequired] string BindingRevision,
    [property: JsonRequired] string SelectionSource,
    [property: JsonRequired] string SelectionRevision,
    [property: JsonRequired] string AttachmentRevision,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StorageGeneration,
    [property: JsonRequired] int WorkItemRevision);

internal sealed record AttachmentReadCapability(string Implementation, string Availability);
internal sealed record AttachmentReadError(string Code, string Message, string Recovery);
internal sealed record AttachmentReadEntry(string Id, string FileName, string? ReportedSize);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachmentReadResult(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string Status,
    [property: JsonRequired] AttachmentReadCapability Capability,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] AttachmentReadContext? Context,
    [property: JsonRequired] IReadOnlyList<AttachmentReadEntry> Attachments,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] AttachmentReadEntry? Attachment,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ContentType,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ContentLength,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] AttachmentReadError? Error);

internal sealed record AttachmentReadOutcome(AttachmentReadResult Result, byte[]? Content);

internal static class AttachmentReadFrame
{
    private static readonly byte[] Magic = "TWIGATT1"u8.ToArray();
    internal const int MaximumHeaderBytes = 65536;

    internal static async Task WriteAsync(
        Stream output, AttachmentReadResult result, byte[]? content, CancellationToken ct = default)
    {
        var header = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            result, TwigJsonContext.Default.AttachmentReadResult);
        if (header.Length > MaximumHeaderBytes)
            throw new InvalidDataException("Attachment response metadata exceeds the frame limit.");
        var bytes = content ?? [];
        if (bytes.Length > AttachmentReadLimits.MaximumDownloadBytes
            || result.Status == "download" && result.ContentLength != bytes.LongLength
            || result.Status is not ("inventory" or "download" or "refused")
            || result.Status != "download" && (bytes.Length != 0 || result.ContentLength is not null))
            throw new InvalidDataException("Attachment response metadata does not match its binary content.");
        var length = new byte[4];
        await output.WriteAsync(Magic, ct).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32BigEndian(length, header.Length);
        await output.WriteAsync(length, ct).ConfigureAwait(false);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await output.WriteAsync(length, ct).ConfigureAwait(false);
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static AttachmentReadResult Refused(string code, bool unsupported = false) => new(
        1, "refused", new(unsupported ? "unsupported" : "native", "unavailable"),
        null, [], null, null, null, null,
        new(code, "Attachment read refused (" + code + ").",
            "Recheck the explicit native item, Bench and connection before requesting a new inventory."));
}
