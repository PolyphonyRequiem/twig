using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Browser;

internal sealed class BrowserAttachmentUploadModule(
    BrowserOperationModule operations,
    BenchEvaluator evaluator,
    IWorkItemRepository repository,
    IWorkItemAttachmentReader reader,
    IWorkItemAttachmentWriter writer)
{
    internal static bool Valid(AttachmentUploadPrepareRequest? request) => request is
        { Version: 1, ItemKey.WorkItemId: > 0, ExpectedRevision: > 0, ContentLength: >= 0 and <= AttachmentReadLimits.MaximumUploadBytes }
        && !string.IsNullOrWhiteSpace(request.Bench)
        && ValidPositiveDecimal(request.ExpectBench)
        && !string.IsNullOrWhiteSpace(request.ExpectIdentity)
        && AttachmentReadLimits.ValidFileName(request.FileName)
        && AttachmentReadLimits.ValidMediaType(request.ContentType)
        && ValidSha256(request.Sha256);

    internal static bool Valid(AttachmentUploadRequest? request) => request is
        { Version: 1, ItemKey.WorkItemId: > 0, ExpectedRevision: > 0, ContentLength: >= 0 and <= AttachmentReadLimits.MaximumUploadBytes }
        && !string.IsNullOrWhiteSpace(request.Bench)
        && ValidPositiveDecimal(request.ExpectBench)
        && !string.IsNullOrWhiteSpace(request.ExpectIdentity)
        && AttachmentReadLimits.ValidFileName(request.FileName)
        && AttachmentReadLimits.ValidMediaType(request.ContentType)
        && ValidSha256(request.Sha256)
        && ValidSha256(request.Confirm);

    internal async Task<AttachmentUploadResult> PrepareAsync(
        AttachmentUploadPrepareRequest request, CancellationToken ct = default)
    {
        if (!Valid(request)) return AttachmentUploadFrame.Refused("usage");

        var outcome = await operations.ExecuteExplicitAsync(request.Bench, async (capture, token) =>
        {
            var key = QualifiedKey(capture, request.ItemKey.WorkItemId);
            if (key != request.ItemKey) throw Failure("authority-invalidated");
            await ValidateTargetAsync(capture, key, token);
            var inventory = await reader.ReadAsync(key.WorkItemId, token);
            if (inventory.WorkItemId != key.WorkItemId || inventory.Revision <= 0)
                throw Failure("attachment-response-invalid");
            if (inventory.Revision != request.ExpectedRevision)
                throw Failure("attachment-stale");
            await operations.EnsureCurrentAsync(capture, token);
            var context = Context(capture, key, inventory.Revision);
            var digest = IntentDigest(context, request.FileName, request.ContentType,
                request.ContentLength, request.Sha256);
            return new AttachmentUploadResult(1, "prepared", new("native", "available"), context,
                request.FileName, request.ContentType, request.ContentLength, request.Sha256, digest, null, null, null);
        }, ct, expectedBench: request.ExpectBench, expectedBinding: request.ItemKey.BindingId,
            expectedIdentity: request.ExpectIdentity);

        if (outcome.Failure is not { } failure) return outcome.Value!;
        return AttachmentUploadFrame.Refused(ToFailureCode(failure));
    }

    internal async Task<AttachmentUploadResult> UploadAsync(
        AttachmentUploadRequest request, byte[] content, CancellationToken ct = default)
    {
        if (!Valid(request) || content.Length != request.ContentLength
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), request.Sha256, StringComparison.Ordinal))
            return AttachmentUploadFrame.Refused("file-intent-mismatch");

        var outcome = await operations.ExecuteExplicitAsync(request.Bench, async (capture, token) =>
        {
            var key = QualifiedKey(capture, request.ItemKey.WorkItemId);
            if (key != request.ItemKey) throw Failure("authority-invalidated");
            await ValidateTargetAsync(capture, key, token);
            var inventory = await reader.ReadAsync(key.WorkItemId, token);
            if (inventory.WorkItemId != key.WorkItemId || inventory.Revision <= 0)
                throw Failure("attachment-response-invalid");
            if (inventory.Revision != request.ExpectedRevision)
                throw Failure("attachment-stale");
            var context = Context(capture, key, inventory.Revision);
            var digest = IntentDigest(context, request.FileName, request.ContentType,
                request.ContentLength, request.Sha256);
            if (!FixedTimeEquals(request.Confirm, digest))
                return AttachmentUploadFrame.Refused("confirmation-mismatch");

            await operations.EnsureCurrentAsync(capture, token);
            try
            {
                var receipt = await writer.UploadAndLinkAsync(key.WorkItemId, request.ExpectedRevision,
                    request.FileName, request.ContentType, content, request.Sha256, digest, token);
                if (receipt.WorkItemId != key.WorkItemId || receipt.Revision <= request.ExpectedRevision
                    || receipt.AttachmentId == Guid.Empty || receipt.FileName != request.FileName
                    || receipt.ContentLength != request.ContentLength)
                    return AttachmentUploadFrame.Refused("attachment-link-receipt-invalid");
                return new AttachmentUploadResult(1, "uploaded", new("native", "available"), context,
                    request.FileName, request.ContentType, request.ContentLength, request.Sha256, digest,
                    receipt.AttachmentId.ToString("D"), receipt.Revision, null);
            }
            catch (AdoAttachmentUploadPartialException ex)
            {
                return new AttachmentUploadResult(1,
                    ex.LinkOutcomeUnknown ? "link-outcome-unknown" : "uploaded-unlinked",
                    new("native", ex.LinkOutcomeUnknown ? "unknown" : "partial"), context,
                    request.FileName, request.ContentType, request.ContentLength, request.Sha256, digest,
                    ex.AttachmentId.ToString("D"), null,
                    new(ex.LinkOutcomeUnknown ? "attachment-link-outcome-unknown" : "attachment-uploaded-not-linked",
                        ex.Message, "Do not retry; reconcile the item’s attachment inventory before taking another action."));
            }
            catch (AdoAttachmentUploadUnknownException ex)
            {
                return new AttachmentUploadResult(1, "upload-outcome-unknown", new("native", "unknown"), context,
                    request.FileName, request.ContentType, request.ContentLength, request.Sha256, digest,
                    null, null, new("attachment-upload-outcome-unknown", ex.Message,
                        "Do not retry; inspect fresh attachment inventory and native write evidence."));
            }
            catch (AdoException ex)
            {
                return AttachmentUploadFrame.Refused(ToAttachmentCode(ex));
            }
        }, ct, expectedBench: request.ExpectBench, expectedBinding: request.ItemKey.BindingId,
            expectedIdentity: request.ExpectIdentity);

        if (outcome.Failure is not { } failure) return outcome.Value!;
        return AttachmentUploadFrame.Refused(ToFailureCode(failure));
    }

    private async Task ValidateTargetAsync(BrowserCapture capture, QualifiedWorkItemKey key, CancellationToken ct)
    {
        var bench = await operations.ResolveExplicitEffectiveAsync(capture, ct);
        if (!(await evaluator.EvaluateAsync(bench, ct: ct)).AllIds.Contains(key.WorkItemId))
            throw Failure("bench-item-not-member");
        if (await repository.GetByIdAsync(key.WorkItemId, ct) is null)
            throw Failure("bench-item-not-cached");
    }

    private static QualifiedWorkItemKey QualifiedKey(BrowserCapture capture, int workItemId) =>
        new(capture.Origin.WorktreeRoot, capture.Origin.Binding.ConnectionRef, capture.BindingId, workItemId);

    private static AttachmentUploadContext Context(
        BrowserCapture capture, QualifiedWorkItemKey key, int revision) => new(
        key, capture.BenchId, capture.BenchName, capture.BenchContentsDigest, capture.IdentityId,
        Decimal(capture.BindingRevision), capture.SelectionSource, Decimal(capture.SelectionRevision),
        Decimal(capture.Origin.Operation.AttachmentRevision), capture.StorageGeneration, revision);

    private static string IntentDigest(
        AttachmentUploadContext context, string fileName, string contentType, int contentLength, string sha256)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteString("checkoutRoot", context.ItemKey.CheckoutRoot);
            json.WriteString("connectionId", context.ItemKey.ConnectionId);
            json.WriteString("bindingId", context.ItemKey.BindingId);
            json.WriteNumber("workItemId", context.ItemKey.WorkItemId);
            json.WriteString("benchId", context.BenchId);
            json.WriteString("benchName", context.BenchName);
            json.WriteString("benchContentsDigest", context.BenchContentsDigest);
            json.WriteString("identityId", context.IdentityId);
            json.WriteString("bindingRevision", context.BindingRevision);
            json.WriteString("selectionSource", context.SelectionSource);
            json.WriteString("selectionRevision", context.SelectionRevision);
            json.WriteString("attachmentRevision", context.AttachmentRevision);
            json.WriteString("storageGeneration", context.StorageGeneration);
            json.WriteNumber("workItemRevision", context.WorkItemRevision);
            json.WriteString("fileName", fileName);
            json.WriteString("contentType", contentType);
            json.WriteNumber("contentLength", contentLength);
            json.WriteString("sha256", sha256);
            json.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool ValidPositiveDecimal(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0 && Decimal(number) == value;

    private static bool ValidSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ToAttachmentCode(AdoException exception) => exception switch
    {
        AdoAuthenticationException => "attachment-denied",
        AdoNotFoundException => "attachment-not-found",
        AdoOfflineException => "attachment-offline",
        AdoRateLimitException => "attachment-throttled",
        _ when exception.Message.StartsWith("attachment-", StringComparison.Ordinal)
            => exception.Message.Split(':', 2)[0],
        _ => "attachment-unavailable",
    };

    private static string ToFailureCode(BrowserReadFailure failure) => failure.Kind switch
    {
        BrowserReadFailureKind.AuthorityInvalidated => "authority-invalidated",
        BrowserReadFailureKind.Canceled => "canceled",
        _ => KnownCode(failure.Message),
    };

    private static string KnownCode(string message)
    {
        var prefix = message.Split(':', 2)[0];
        return prefix is "bench-item-not-member" or "bench-item-not-cached" or "authority-invalidated"
            or "attachment-denied" or "attachment-not-found" or "attachment-offline"
            or "attachment-throttled" or "attachment-too-large" or "attachment-stale"
            or "attachment-response-invalid" or "file-intent-mismatch"
                ? prefix : "attachment-unavailable";
    }

    private static InvalidOperationException Failure(string code) =>
        new(code + ": attachment upload refused.");
}
