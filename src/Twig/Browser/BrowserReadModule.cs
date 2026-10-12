using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Browser;

/// <summary>Native cached Show facts and explicit selected-root sync under reusable operation admission.</summary>
internal sealed class BrowserReadModule(CommandContext ctx, BrowserOperationModule operations,
    IWorkItemRepository repository, IFieldDefinitionStore fieldDefinitions,
    IWorkItemLinkRepository links, IPendingChangeStore pendingChanges, StatusFieldConfigReader statusFields,
    SyncCoordinatorFactory syncCoordinators, IProcessConfigurationProvider processConfiguration, SpectreTheme theme)
{
    internal async Task<BrowserDetailResult> ReadDetailAsync(BrowserDetailRequest request, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Failure(BrowserReadFailureKind.Canceled, "Detail read canceled.");
        if (request.Id == 0) return Failure(BrowserReadFailureKind.Usage, "A nonzero work-item ID or negative seed alias is required.");
        if (request.Width < 1 || request.Width > 10000) return Failure(BrowserReadFailureKind.Usage, "Width must be between 1 and 10000 columns.");
        if (request.Sync && request.Id < 0) return Failure(BrowserReadFailureKind.Usage, "Local seeds cannot sync or publish from the detail viewer.");
        var result = await operations.ExecuteAsync(request.Captured, async (capture, token) =>
        {
            if (request.Sync)
            {
                await operations.EnsureCurrentAsync(capture, token);
                try { await syncCoordinators.ReadOnly.SyncLinksAsync(request.Id, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await operations.EnsureCurrentAsync(capture, token);
                    throw new BrowserReadException(BrowserReadFailureKind.Unavailable, $"Sync failed or incomplete for #{request.Id}: {ex.Message}");
                }
                await operations.EnsureCurrentAsync(capture, token);
            }
            var item = await repository.GetByIdAsync(request.Id, token)
                ?? throw new InvalidOperationException(request.Sync
                    ? $"Work item #{request.Id} could not be loaded after sync; local pending changes may protect it."
                    : $"Work item #{request.Id} is not cached. Press S or run 'twig bench detail {request.Id} --sync' to pull this item explicitly; no refresh was performed.");
            item = Copy(item);
            var definitions = (await fieldDefinitions.GetAllAsync(token)).ToArray();
            var entries = (await statusFields.ReadAsync(token))?.ToArray();
            var children = (await repository.GetChildrenAsync(item.Id, token)).Select(Copy).ToArray();
            var parent = item.ParentId.HasValue ? await repository.GetByIdAsync(item.ParentId.Value, token) : null;
            if (parent is not null) parent = Copy(parent);
            var cachedLinks = (await links.GetLinksAsync(item.Id, token)).ToArray();
            var verifiedAt = await links.GetLinksVerifiedAtAsync(item.Id, token);
            var changes = (await pendingChanges.GetChangesAsync(item.Id, token)).ToArray();
            var snapshot = new BrowserDetailSnapshot(item, parent, children, definitions, entries, cachedLinks,
                changes, processConfiguration.ComputeChildProgress(children), ctx.Config.Display.CacheStaleMinutes, theme.Snapshot());
            return new BrowserDetailObservation(capture, snapshot, verifiedAt, await snapshot.RenderAsync(request.Width, request.Ansi));
        }, ct, request.ExpectedBench, request.ExpectedBinding, request.ExpectedIdentity);
        return new(result.Value, result.Failure, result.Capture);
    }

    private static WorkItem Copy(WorkItem source)
    {
        var copy = WorkItemCopier.Copy(source, preserveDirty: true);
        foreach (var note in source.PendingNotes) copy.AddPendingNote(note);
        return copy;
    }
    private static BrowserDetailResult Failure(BrowserReadFailureKind kind, string message)
        => new(null, new(kind, message));
}
