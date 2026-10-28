using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>Remote attachment reads; never chooses or writes a local destination.</summary>
internal interface IWorkItemAttachmentReader
{
    Task<WorkItemAttachmentInventory> ReadAsync(int workItemId, CancellationToken ct = default);

    Task<WorkItemAttachmentDownload> DownloadAsync(
        int workItemId, int expectedRevision, Guid attachmentId, CancellationToken ct = default);
}
