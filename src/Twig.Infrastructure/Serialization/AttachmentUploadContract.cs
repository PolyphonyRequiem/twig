using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachmentUploadPrepareRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string Bench,
    [property: JsonRequired] string ExpectBench,
    [property: JsonRequired] string ExpectIdentity,
    [property: JsonRequired] int ExpectedRevision,
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string ContentType,
    [property: JsonRequired] int ContentLength,
    [property: JsonRequired] string Sha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachmentUploadRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] QualifiedWorkItemKey ItemKey,
    [property: JsonRequired] string Bench,
    [property: JsonRequired] string ExpectBench,
    [property: JsonRequired] string ExpectIdentity,
    [property: JsonRequired] int ExpectedRevision,
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string ContentType,
    [property: JsonRequired] int ContentLength,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string Confirm);

internal sealed record AttachmentUploadCapability(string Implementation, string Availability);
internal sealed record AttachmentUploadError(string Code, string Message, string Recovery);
internal sealed record AttachmentUploadContext(
    QualifiedWorkItemKey ItemKey, string BenchId, string BenchName, string BenchContentsDigest,
    string IdentityId, string BindingRevision, string SelectionSource, string SelectionRevision,
    string AttachmentRevision, string? StorageGeneration, int WorkItemRevision);
internal sealed record AttachmentRemoteWriteEvidence(
    string IntentDigest, int WorkItemId, int ExpectedRevision, string FileName, string ContentType,
    long ContentLength, string Sha256, string? AttachmentId, string? AttachmentUrl = null);
internal sealed record AttachmentUploadResult(
    int Version, string Status, AttachmentUploadCapability Capability, AttachmentUploadContext? Context,
    string? FileName, string? ContentType, int? ContentLength, string? Sha256, string? IntentDigest,
    string? AttachmentId, int? NewWorkItemRevision, AttachmentUploadError? Error);

internal sealed record AttachmentUploadFrameData(AttachmentUploadRequest Request, byte[] Content);

internal static class AttachmentUploadFrame
{
    private static readonly byte[] Magic = "TWIGUPL1"u8.ToArray();
    internal const int MaximumHeaderBytes = 65536;
    internal const int MaximumPrepareInputBytes = 262144;
    private static readonly TwigJsonContext StrictJson = new(new JsonSerializerOptions(TwigJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    });

    internal static async Task<AttachmentUploadPrepareRequest> ReadPrepareRequestAsync(
        Stream input, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumPrepareInputBytes)
                throw new InvalidDataException("Attachment upload prepare request is too large.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(parsed.RootElement);
        return parsed.RootElement.Deserialize(StrictJson.AttachmentUploadPrepareRequest)
            ?? throw new JsonException("Attachment upload prepare request is empty.");
    }

    internal static async Task<AttachmentUploadFrameData> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(input, magic, ct).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid attachment upload frame.");
        var integer = new byte[4];
        await ReadExactlyAsync(input, integer, ct).ConfigureAwait(false);
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(integer);
        if (headerLength is <= 0 or > MaximumHeaderBytes) throw new InvalidDataException("Invalid attachment upload header length.");
        var header = new byte[headerLength];
        await ReadExactlyAsync(input, header, ct).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(header, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(parsed.RootElement);
        var request = parsed.RootElement.Deserialize(StrictJson.AttachmentUploadRequest)
            ?? throw new JsonException("Attachment upload frame has no request.");
        await ReadExactlyAsync(input, integer, ct).ConfigureAwait(false);
        var contentLength = BinaryPrimitives.ReadInt32BigEndian(integer);
        if (contentLength < 0 || contentLength > AttachmentReadLimits.MaximumUploadBytes
            || contentLength != request.ContentLength)
            throw new InvalidDataException("Attachment upload frame content length does not match the request.");
        var content = new byte[contentLength];
        await ReadExactlyAsync(input, content, ct).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Unexpected trailing bytes in attachment upload frame.");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(sha256, request.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Attachment upload frame content does not match its SHA-256.");
        return new(request, content);
    }

    internal static async Task WriteAsync(
        Stream output, AttachmentUploadRequest request, byte[] content, CancellationToken ct = default)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(request, TwigJsonContext.Default.AttachmentUploadRequest);
        if (request.Version != 1 || header.Length > MaximumHeaderBytes || content.Length != request.ContentLength
            || content.Length > AttachmentReadLimits.MaximumUploadBytes
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), request.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Attachment upload frame does not match its captured file intent.");
        var length = new byte[4];
        await output.WriteAsync(Magic, ct).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32BigEndian(length, header.Length);
        await output.WriteAsync(length, ct).ConfigureAwait(false);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        BinaryPrimitives.WriteInt32BigEndian(length, content.Length);
        await output.WriteAsync(length, ct).ConfigureAwait(false);
        await output.WriteAsync(content, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Truncated attachment upload frame.");
            offset += read;
        }
    }

    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate attachment upload property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicates(child);
    }

    internal static AttachmentUploadResult Refused(string code, bool unsupported = false) => new(
        1, "refused", new(unsupported ? "unsupported" : "native", "unavailable"), null,
        null, null, null, null, null, null, null,
        new(code, "Attachment upload refused (" + code + ").",
            "Read fresh qualified attachment inventory and prepare a new explicit upload intent."));
}
