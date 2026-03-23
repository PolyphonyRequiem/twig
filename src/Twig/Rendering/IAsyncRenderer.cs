using Spectre.Console.Rendering;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;

namespace Twig.Rendering;

/// <summary>
/// Async progressive rendering pipeline. Commands yield data chunks
/// as they become available; the renderer updates the terminal incrementally.
/// Complements the synchronous <see cref="Formatters.IOutputFormatter"/> path.
/// </summary>
public interface IAsyncRenderer
{
    Task RenderWorkspaceAsync(
        IAsyncEnumerable<WorkspaceDataChunk> getWorkspaceData,
        int staleDays,
        bool isTeamView,
        CancellationToken ct,
        IReadOnlyList<Domain.ValueObjects.ColumnSpec>? dynamicColumns = null);

    Task RenderTreeAsync(
        Func<Task<WorkItem?>> getFocusedItem,
        Func<Task<IReadOnlyList<WorkItem>>> getParentChain,
        Func<Task<IReadOnlyList<WorkItem>>> getChildren,
        int maxChildren,
        int? activeId,
        CancellationToken ct,
        Func<int, Task<int?>>? getSiblingCount = null,
        Func<Task<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>>? getLinks = null);

    Task RenderStatusAsync(
        Func<Task<WorkItem?>> getItem,
        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
        CancellationToken ct,
        IReadOnlyList<Domain.ValueObjects.FieldDefinition>? fieldDefinitions = null,
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null);

    Task RenderWorkItemAsync(
        Func<Task<WorkItem?>> getItem,
        bool showDirty,
        CancellationToken ct);

    Task<(int Id, string Title)?> PromptDisambiguationAsync(
        IReadOnlyList<(int Id, string Title)> matches,
        CancellationToken ct);

    void RenderHints(IReadOnlyList<string> hints);

    /// <summary>
    /// Renders the seed view dashboard with Spectre.Console table rendering.
    /// </summary>
    Task RenderSeedViewAsync(
        Func<Task<IReadOnlyList<SeedViewGroup>>> getData,
        int totalWritableFields,
        int staleDays,
        CancellationToken ct);

    /// <summary>
    /// Cache-render-fetch-revise primitive. Renders the cached view immediately,
    /// performs a background sync, then revises the display in-place based on the result.
    /// </summary>
    Task RenderWithSyncAsync(
        Func<Task<IRenderable>> buildCachedView,
        Func<Task<SyncResult>> performSync,
        Func<SyncResult, Task<IRenderable?>> buildRevisedView,
        CancellationToken ct);
}

/// <summary>
/// Discriminated union for workspace data chunks streamed to the renderer.
/// Each variant represents a stage of workspace data becoming available.
/// </summary>
public abstract record WorkspaceDataChunk
{
    private WorkspaceDataChunk() { }

    public sealed record ContextLoaded(WorkItem? ContextItem) : WorkspaceDataChunk;
    public sealed record SprintItemsLoaded(IReadOnlyList<WorkItem> Items) : WorkspaceDataChunk;
    public sealed record SeedsLoaded(IReadOnlyList<WorkItem> Seeds) : WorkspaceDataChunk;
    public sealed record RefreshStarted : WorkspaceDataChunk;
    public sealed record RefreshCompleted : WorkspaceDataChunk;
}
