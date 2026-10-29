using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

internal interface IWorkItemAttachmentWriter
{
    Task<WorkItemAttachmentUploadReceipt> UploadAndLinkAsync(
        int workItemId, int expectedRevision, string fileName, string contentType, byte[] content, string sha256,
        string intentDigest, CancellationToken ct = default);
}
