using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Public adapter: projects <see cref="PrimaryScopeAttachmentService.ReadStatusAsync"/>
/// through the public <see cref="IAttachmentStatusProjection"/> seam. AB#738's
/// service surface is deliberately internal (deep module); this adapter is the
/// single public read-only shim the CLI and MCP consume.
/// <para>
/// Errors are collapsed to "not managed" here: the projection is presentational,
/// and a failed underlying read (drift, layout marker missing, etc.) is
/// indistinguishable from an unmanaged checkout from the human's point of view.
/// The service itself surfaces the named failures for write paths; the read
/// surface is best-effort so a stale worktree never breaks status rendering.
/// </para>
/// </summary>
internal sealed class AttachmentStatusProjection : IAttachmentStatusProjection
{
    private readonly PrimaryScopeAttachmentService _service;

    public AttachmentStatusProjection(PrimaryScopeAttachmentService service)
    {
        _service = service;
    }

    public async Task<StatusProjection> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var read = await _service.ReadStatusAsync(ct).ConfigureAwait(false);
            if (!read.IsSuccess)
                return new StatusProjection(false, false, null, null, null);

            var status = read.Value;
            if (!status.IsManagedWorktree)
                return new StatusProjection(false, false, null, null, null);
            if (status.PrimaryScope is not { } scope)
                return new StatusProjection(true, false, null, null, null);
            return new StatusProjection(true, true, scope.WorkItemId, status.WorkItemTitle, status.WorkItemType);
        }
        catch
        {
            return new StatusProjection(false, false, null, null, null);
        }
    }
}
