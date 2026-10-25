using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

internal interface IWorkItemDiscussionWriter
{
    Task<DiscussionPostReceipt> AppendDiscussionCommentAsync(
        int workItemId, string effectiveBody, CancellationToken ct = default);
}
