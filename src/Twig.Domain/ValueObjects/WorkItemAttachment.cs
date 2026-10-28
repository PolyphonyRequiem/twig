namespace Twig.Domain.ValueObjects;

internal sealed record WorkItemAttachment(Guid Id, string FileName, long? ReportedSize);

internal sealed record WorkItemAttachmentInventory(
    int WorkItemId, int Revision, IReadOnlyList<WorkItemAttachment> Attachments);

internal sealed record WorkItemAttachmentDownload(
    int WorkItemId, int Revision, WorkItemAttachment Attachment,
    string ContentType, byte[] Content, string Sha256);

/// <summary>Twig response bounds, not provider attachment-size guarantees.</summary>
internal static class AttachmentReadLimits
{
    internal const int MaximumInventoryBytes = 2 * 1024 * 1024;
    internal const int MaximumDownloadBytes = 16 * 1024 * 1024;
}
