using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Browser;

/// <summary>One explicitly admitted remote page, with no persistence or active-item selection.</summary>
internal sealed class BrowserDiscussionModule(
    BrowserOperationModule operations, BenchEvaluator evaluator,
    IWorkItemRepository repository, IWorkItemDiscussionReader reader)
{
    internal const string OptionProfile = "ado-comments-v1-top50-asc-deleted-none";

    internal static bool Valid(DiscussionRequest? request) => request is
        { Version: 1, ItemKey.WorkItemId: > 0 } && !string.IsNullOrWhiteSpace(request.Bench)
        && long.TryParse(request.ExpectBench, NumberStyles.None, CultureInfo.InvariantCulture, out var bench)
        && bench > 0 && Decimal(bench) == request.ExpectBench && !string.IsNullOrWhiteSpace(request.ExpectIdentity)
        && (request.Cursor is null || request.Cursor is { Version: 1, Context: not null, OptionProfile: OptionProfile }
            && DiscussionReadLimits.ValidToken(request.Cursor.ProviderToken) && request.Cursor.ProviderToken is not null);

    internal async Task<DiscussionResult> ReadAsync(DiscussionRequest request, CancellationToken ct = default)
    {
        if (!Valid(request)) return Refused("usage");
        var result = await operations.ExecuteExplicitAsync(request.Bench, async (capture, token) =>
        {
            var key = new QualifiedWorkItemKey(capture.Origin.WorktreeRoot,
                capture.Origin.Binding.ConnectionRef, capture.BindingId, request.ItemKey.WorkItemId);
            if (key != request.ItemKey) throw Failure("authority-invalidated");
            var context = new DiscussionContext(key, capture.BenchId, capture.BenchName,
                capture.BenchContentsDigest, capture.IdentityId, Decimal(capture.BindingRevision),
                capture.SelectionSource, Decimal(capture.SelectionRevision),
                Decimal(capture.Origin.Operation.AttachmentRevision), capture.StorageGeneration);
            if (request.Cursor is { } cursor && cursor.Context != context)
                throw Failure("authority-invalidated");
            var bench = await operations.ResolveExplicitEffectiveAsync(capture, token);
            if (!(await evaluator.EvaluateAsync(bench, ct: token)).AllIds.Contains(key.WorkItemId))
                throw Failure("bench-item-not-member");
            if (await repository.GetByIdAsync(key.WorkItemId, token) is null)
                throw Failure("bench-item-not-cached");
            await operations.EnsureCurrentAsync(capture, token);
            WorkItemDiscussionPage page;
            try { page = await reader.ReadDiscussionAsync(key.WorkItemId, request.Cursor?.ProviderToken, token); }
            catch (AdoException ex)
            {
                throw Failure(ex switch
                {
                    AdoAuthenticationException => "discussion-denied",
                    AdoNotFoundException => "discussion-not-found",
                    AdoOfflineException => "discussion-offline",
                    AdoRateLimitException => "discussion-throttled",
                    _ when ex.Message.StartsWith("discussion-response-too-large:", StringComparison.Ordinal) => "discussion-response-too-large",
                    _ => "discussion-response-invalid",
                });
            }
            var next = page.ContinuationToken is { } value ? new DiscussionCursor(1, context, value, OptionProfile) : null;
            return new DiscussionResult(1, "read", new("native", "available"), context, page.Comments,
                new(next is null ? "exhausted" : "more", Decimal(page.Count),
                    page.ReportedTotalCount is { } total ? Decimal(total) : null, next), null);
        }, ct, expectedBench: request.ExpectBench, expectedBinding: request.ItemKey.BindingId,
            expectedIdentity: request.ExpectIdentity);
        if (result.Failure is not { } failure) return result.Value!;
        var code = failure.Kind switch
        {
            BrowserReadFailureKind.AuthorityInvalidated => "authority-invalidated",
            BrowserReadFailureKind.Canceled => "canceled",
            _ => KnownCode(failure.Message),
        };
        return Refused(code);
    }

    private static string KnownCode(string message)
    {
        var prefix = message.Split(':', 2)[0];
        return prefix is "bench-item-not-member" or "bench-item-not-cached" or "authority-invalidated"
            or "discussion-denied" or "discussion-not-found" or "discussion-offline"
            or "discussion-throttled" or "discussion-response-too-large" or "discussion-response-invalid"
                ? prefix : "discussion-unavailable";
    }

    private static InvalidOperationException Failure(string code) => new(code + ": discussion read refused.");
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static DiscussionResult Refused(string code) => new(1, "refused",
        code == "provider-unsupported" ? new("unsupported", "unavailable") : new("native", "unavailable"),
        null, [], null, new(code, "Discussion read refused (" + code + ").",
            "Recheck the explicit native item, Bench and connection before requesting a new page."));
}
