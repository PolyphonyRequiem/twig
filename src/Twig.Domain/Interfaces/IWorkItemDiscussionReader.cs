using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>One remote discussion page, independent of mirror reads and comment publication.</summary>
internal interface IWorkItemDiscussionReader
{
    Task<WorkItemDiscussionPage> ReadDiscussionAsync(int id, string? continuationToken, CancellationToken ct = default);

    Task<DiscussionComment?> ReadDiscussionCommentAsync(int workItemId, string commentId, CancellationToken ct = default);
}
