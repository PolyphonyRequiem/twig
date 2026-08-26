using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Public adapter: projects <see cref="PrimaryScopeAttachmentService.ReadStatusAsync"/>
/// through the public <see cref="IAttachmentStatusProjection"/> seam. AB#738's
/// service surface is deliberately internal (deep module); this adapter is the
/// single public read-only shim the CLI and MCP consume.
/// <para>
/// Named storage failures (§8 of AB#736 — <c>layout-marker-missing</c>,
/// <c>worktree-fingerprint-drift</c>, <c>attachment-connection-mismatch</c>,
/// <c>worktree-not-registered</c>, and so on) are carried through on
/// <see cref="StatusProjection.FailureCode"/>. The surface renders the
/// identifier as a repair hint rather than falling through to "unmanaged" —
/// silently degrading a corrupted or moved managed worktree to an unmanaged
/// checkout was the exact defect this projection had to fix.
/// <see cref="OperationCanceledException"/> propagates unchanged; every other
/// exception (unlikely — the service returns Results) folds into a synthetic
/// named failure so the surface never crashes.
/// </para>
/// </summary>
internal sealed class AttachmentStatusProjectionAdapter : IAttachmentStatusProjection
{
    private readonly PrimaryScopeAttachmentService _service;

    public AttachmentStatusProjectionAdapter(PrimaryScopeAttachmentService service)
    {
        _service = service;
    }

    public async Task<StatusProjection> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var read = await _service.ReadStatusAsync(ct).ConfigureAwait(false);
            if (!read.IsSuccess)
                return new StatusProjection(true, false, null, null, null, read.Error);

            return PrimaryScopeAttachmentService.ProjectStatus(read.Value);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a caller signal, never storage state — rethrow so
            // the surface handles it the same as any other cancellation.
            throw;
        }
        catch (Exception ex)
        {
            // Every other exception folds into a synthetic named failure so the
            // status surface renders a repair hint; the service normally returns
            // Results, but a corrupt underlying store may still throw on read.
            return new StatusProjection(true, false, null, null, null,
                $"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.GetType().Name}");
        }
    }
}
