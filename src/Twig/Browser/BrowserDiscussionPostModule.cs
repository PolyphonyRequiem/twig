using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Browser;

/// <summary>Prepares native proposal bytes under fresh explicit admission. No file, journal or provider write.</summary>
internal sealed class BrowserDiscussionPostModule(
    BrowserOperationModule operations, BenchEvaluator evaluator, IWorkItemRepository repository)
{
    internal async Task<DiscussionPostPreparation> PrepareAsync(DiscussionPostRequest request, CancellationToken ct)
    {
        if (request is not { Version: 1, Context.ItemKey.WorkItemId: > 0, Text: not null }
            || request.Format is not ("plain" or "markdown") || string.IsNullOrWhiteSpace(request.Text)
            || !long.TryParse(request.Context.BenchId, NumberStyles.None, CultureInfo.InvariantCulture, out var benchId)
            || benchId <= 0 || Decimal(benchId) != request.Context.BenchId
            || string.IsNullOrWhiteSpace(request.Context.IdentityId))
            return Refused("usage");
        string body;
        try { body = DiscussionPostBodyFormatter.Format(request.Format, request.Text); }
        catch (ArgumentException) { return Refused("usage"); }
        var expected = request.Context;
        var result = await operations.ExecuteExplicitAsync(expected.BenchId, async (capture, token) =>
        {
            var key = new QualifiedWorkItemKey(capture.Origin.WorktreeRoot,
                capture.Origin.Binding.ConnectionRef, capture.BindingId, expected.ItemKey.WorkItemId);
            var context = new DiscussionContext(key, capture.BenchId, capture.BenchName, capture.BenchContentsDigest,
                capture.IdentityId, Decimal(capture.BindingRevision), capture.SelectionSource,
                Decimal(capture.SelectionRevision), Decimal(capture.Origin.Operation.AttachmentRevision), capture.StorageGeneration);
            if (context != expected)
                throw new BrowserReadException(BrowserReadFailureKind.AuthorityInvalidated, "Discussion target changed.");
            var bench = await operations.ResolveExplicitEffectiveAsync(capture, token);
            if (!(await evaluator.EvaluateAsync(bench, ct: token)).AllIds.Contains(key.WorkItemId))
                throw new InvalidOperationException("bench-item-not-member");
            if (await repository.GetByIdAsync(key.WorkItemId, token) is null)
                throw new InvalidOperationException("bench-item-not-cached");
            var definition = new PlanDefinition
            {
                Version = 1,
                Workspace = new PlanWorkspace
                {
                    Organization = capture.Origin.Operation.Organization,
                    Project = capture.Origin.Operation.Project,
                },
                Operations = [new AppendCommentOperation
                {
                    Id = "discussion-" + Guid.NewGuid().ToString("N"),
                    WorkItemId = key.WorkItemId, SavedBenchId = context.BenchId,
                    ExpectedBenchContentsDigest = context.BenchContentsDigest, ExpectedIdentityId = context.IdentityId,
                    Format = request.Format, Text = request.Text, EffectiveBody = body,
                }],
            };
            var parsed = new PlanDocumentParser().Parse(PlanDocumentWriter.Write(definition));
            if (!parsed.IsValid) throw new InvalidOperationException("Native discussion proposal validation failed.");
            return new DiscussionPostPreparation(1, "prepared", context, parsed.CanonicalJson, parsed.Digest, null);
        }, ct, expectedBench: expected.BenchId, expectedBinding: expected.ItemKey.BindingId,
            expectedIdentity: expected.IdentityId);
        if (result.Failure is not { } failure) return result.Value!;
        return Refused(failure.Kind switch
        {
            BrowserReadFailureKind.AuthorityInvalidated => "authority-invalidated",
            BrowserReadFailureKind.Canceled => "canceled",
            _ when failure.Message is "bench-item-not-member" or "bench-item-not-cached" => failure.Message,
            _ => "discussion-prepare-unavailable",
        });
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static DiscussionPostPreparation Refused(string code) => new(1, "refused", null, null, null,
        new(code, "Discussion preparation refused (" + code + "). No comment was posted.",
            "Read the explicitly qualified discussion again before preparing a new proposal."));
}
