using Spectre.Console.Rendering;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;

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
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null,
        (int Done, int Total)? childProgress = null);

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
        CancellationToken ct,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null);

    /// <summary>
    /// Cache-render-fetch-revise primitive. Renders the cached view immediately,
    /// performs a background sync, then revises the display in-place based on the result.
    /// </summary>
    Task RenderWithSyncAsync(
        Func<Task<IRenderable>> buildCachedView,
        Func<Task<SyncResult>> performSync,
        Func<SyncResult, Task<IRenderable?>> buildRevisedView,
        CancellationToken ct);

    /// <summary>
    /// Renders a flow-start summary panel with state transition, branch, and context info.
    /// </summary>
    Task RenderFlowSummaryAsync(
        WorkItem item,
        string originalState,
        string? newState,
        string? branchName,
        CancellationToken ct = default);

    /// <summary>
    /// Launches the interactive tree navigator. Renders a Live tree with keyboard-driven
    /// traversal — arrow keys move cursor, Enter commits, Escape cancels.
    /// Returns the committed work item ID, or <c>null</c> if cancelled.
    /// </summary>
    /// <param name="initialState">Initial navigator state (cursor, siblings, parent chain).</param>
    /// <param name="loadNodeState">
    /// Callback to load full state (ParentChain, Children, Links, SeedLinks) for a given
    /// work item ID. The renderer never touches repositories directly.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<int?> RenderInteractiveTreeAsync(
        TreeNavigatorState initialState,
        Func<int, Task<TreeNavigatorState>> loadNodeState,
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
