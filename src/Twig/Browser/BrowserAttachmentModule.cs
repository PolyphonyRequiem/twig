using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Browser;

internal sealed class BrowserAttachmentModule(
    BrowserOperationModule operations,
    BenchEvaluator evaluator,
    IWorkItemRepository repository,
    IWorkItemAttachmentReader reader)
{
    internal static bool Valid(AttachmentReadRequest? request) => request is
        { Version: 1, ItemKey.WorkItemId: > 0 }
        && (request.Action is "inventory" or "download")
        && !string.IsNullOrWhiteSpace(request.Bench)
        && long.TryParse(request.ExpectBench, NumberStyles.None, CultureInfo.InvariantCulture, out var bench)
        && bench > 0 && Decimal(bench) == request.ExpectBench
        && !string.IsNullOrWhiteSpace(request.ExpectIdentity)
        && (request.Action == "inventory"
            ? request.ExpectedRevision is null && request.AttachmentId is null
            : request.ExpectedRevision is > 0 && request.AttachmentId is { } id && id != Guid.Empty);

    internal async Task<AttachmentReadOutcome> ReadAsync(
        AttachmentReadRequest request, CancellationToken ct = default)
    {
        if (!Valid(request)) return Refused("usage");

        var outcome = await operations.ExecuteExplicitAsync(request.Bench, async (capture, token) =>
        {
            var key = new QualifiedWorkItemKey(capture.Origin.WorktreeRoot,
                capture.Origin.Binding.ConnectionRef, capture.BindingId, request.ItemKey.WorkItemId);
            if (key != request.ItemKey) throw Failure("authority-invalidated");

            var bench = await operations.ResolveExplicitEffectiveAsync(capture, token);
            if (!(await evaluator.EvaluateAsync(bench, ct: token)).AllIds.Contains(key.WorkItemId))
                throw Failure("bench-item-not-member");
            if (await repository.GetByIdAsync(key.WorkItemId, token) is null)
                throw Failure("bench-item-not-cached");
            await operations.EnsureCurrentAsync(capture, token);

            try
            {
                if (request.Action == "inventory")
                {
                    var inventory = await reader.ReadAsync(key.WorkItemId, token);
                    if (inventory.WorkItemId != key.WorkItemId || inventory.Revision <= 0)
                        throw Failure("attachment-response-invalid");
                    var context = Context(capture, key, inventory.Revision);
                    return new AttachmentReadOutcome(new(1, "inventory", new("native", "available"),
                        context, inventory.Attachments.Select(Project).ToArray(), null, null, null, null, null), null);
                }

                var download = await reader.DownloadAsync(key.WorkItemId, request.ExpectedRevision!.Value,
                    request.AttachmentId!.Value, token);
                if (download.WorkItemId != key.WorkItemId || download.Revision != request.ExpectedRevision.Value
                    || download.Content.Length > AttachmentReadLimits.MaximumDownloadBytes
                    || download.Attachment.Id != request.AttachmentId.Value)
                    throw Failure("attachment-response-invalid");
                var contextForDownload = Context(capture, key, download.Revision);
                return new AttachmentReadOutcome(new(1, "download", new("native", "available"),
                    contextForDownload, [], Project(download.Attachment), download.ContentType,
                    download.Content.LongLength, download.Sha256, null), download.Content);
            }
            catch (AdoException ex)
            {
                throw Failure(ex switch
                {
                    AdoAuthenticationException => "attachment-denied",
                    AdoNotFoundException => "attachment-not-found",
                    AdoOfflineException => "attachment-offline",
                    AdoRateLimitException => "attachment-throttled",
                    _ when ex.Message.StartsWith("attachment-", StringComparison.Ordinal)
                        => ex.Message.Split(':', 2)[0],
                    _ => "attachment-unavailable",
                });
            }
        }, ct, expectedBench: request.ExpectBench, expectedBinding: request.ItemKey.BindingId,
            expectedIdentity: request.ExpectIdentity);

        if (outcome.Failure is not { } failure) return outcome.Value!;
        var code = failure.Kind switch
        {
            BrowserReadFailureKind.AuthorityInvalidated => "authority-invalidated",
            BrowserReadFailureKind.Canceled => "canceled",
            _ => KnownCode(failure.Message),
        };
        return Refused(code);
    }

    private static AttachmentReadContext Context(
        BrowserCapture capture, QualifiedWorkItemKey key, int revision) => new(
        key, capture.BenchId, capture.BenchName, capture.BenchContentsDigest, capture.IdentityId,
        Decimal(capture.BindingRevision), capture.SelectionSource, Decimal(capture.SelectionRevision),
        Decimal(capture.Origin.Operation.AttachmentRevision), capture.StorageGeneration, revision);

    private static string KnownCode(string message)
    {
        var prefix = message.Split(':', 2)[0];
        return prefix is "bench-item-not-member" or "bench-item-not-cached" or "authority-invalidated"
            or "attachment-denied" or "attachment-not-found" or "attachment-offline"
            or "attachment-throttled" or "attachment-too-large" or "attachment-stale"
            or "attachment-not-linked" or "attachment-response-invalid" or "attachment-redirect-refused"
            or "attachment-size-mismatch"
                ? prefix : "attachment-unavailable";
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static AttachmentReadEntry Project(WorkItemAttachment attachment) =>
        new(attachment.Id.ToString("D"), attachment.FileName,
            attachment.ReportedSize?.ToString(CultureInfo.InvariantCulture));
    private static InvalidOperationException Failure(string code) => new(code + ": attachment read refused.");

    internal static AttachmentReadOutcome Refused(string code, bool unsupported = false) =>
        new(AttachmentReadFrame.Refused(code, unsupported), null);
}
