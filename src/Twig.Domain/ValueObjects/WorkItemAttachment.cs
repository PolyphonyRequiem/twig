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
    internal const int MaximumUploadBytes = 16 * 1024 * 1024;

    internal static bool ValidFileName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255 && value is not ("." or "..")
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\');

    internal static bool ValidMediaType(string? value) => value is { Length: > 0 and <= 127 }
        && value.Count(character => character == '/') == 1
        && value.Split('/') is [var type, var subtype]
        && type.Length is > 0 and <= 64 && subtype.Length is > 0 and <= 64
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-' or '/');
}

internal sealed record WorkItemAttachmentUploadReceipt(
    int WorkItemId, int Revision, Guid AttachmentId, string FileName, long ContentLength);
