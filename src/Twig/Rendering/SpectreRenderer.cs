using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;

namespace Twig.Rendering;

/// <summary>
/// Spectre.Console implementation of <see cref="IAsyncRenderer"/>.
/// Uses <see cref="LiveDisplayContext"/> for progressive workspace rendering.
/// </summary>
internal sealed class SpectreRenderer(IAnsiConsole console, SpectreTheme theme) : IAsyncRenderer
{
    private readonly IAnsiConsole _console = console;
    private readonly SpectreTheme _theme = theme;

    /// <summary>
    /// Optional type level map for unparented item detection. Set before rendering tree views.
    /// </summary>
    internal IReadOnlyDictionary<string, int>? TypeLevelMap { get; set; }

    /// <summary>
    /// Optional parent-child map for determining expected parent type names.
    /// </summary>
    internal IReadOnlyDictionary<string, List<string>>? ParentChildMap { get; set; }

    public async Task RenderWorkspaceAsync(
        IAsyncEnumerable<WorkspaceDataChunk> data,
        int staleDays,
        bool isTeamView,
        CancellationToken ct,
        IReadOnlyList<Domain.ValueObjects.ColumnSpec>? dynamicColumns = null,
        int cacheStaleMinutes = 5)
    {
        var table = SpectreTheme.CreateWorkspaceTable(isTeamView, dynamicColumns);
        string? savedCaption = null;
        var loadingCleared = false;
        int? activeContextId = null;
        WorkspaceSections? currentSections = null;
        var dynamicCount = dynamicColumns?.Count ?? 0;
        var colCount = (isTeamView ? 5 : 4) + dynamicCount;
        var emptyRow = new string[colCount];
        for (var i = 0; i < colCount; i++) emptyRow[i] = "";
        emptyRow[0] = "[dim]Loading workspace...[/]";

        await _console.Live(table)
            .StartAsync(async ctx =>
            {
                table.AddRow(emptyRow);
                ctx.Refresh();

                await foreach (var chunk in data.WithCancellation(ct))
                {
                    // Clear the loading placeholder on first real data chunk
                    if (!loadingCleared)
                    {
                        table.Rows.Clear();
                        loadingCleared = true;
                    }

                    switch (chunk)
                    {
                        case WorkspaceDataChunk.ContextLoaded(var contextItem):
                            activeContextId = contextItem?.Id;
                            savedCaption = contextItem is not null
                                ? $"Active: #{contextItem.Id} {Markup.Escape(contextItem.Title)}"
                                : "[dim italic]No active context[/]";
                            table.Caption(new TableTitle(savedCaption));
                            ctx.Refresh();
                            break;

                        case WorkspaceDataChunk.SprintItemsLoaded { Items: var items, Sections: var sections }:
                            currentSections = sections;

                            if (sections is not null && sections.Sections.Count > 0)
                            {
                                // Mode-sectioned rendering with per-section category grouping
                                var showSectionHeaders = sections.Sections.Count > 1;
                                var sectionIndex = 0;
                                foreach (var section in sections.Sections)
                                {
                                    if (sectionIndex > 0)
                                    {
                                        var sectionSepRow = new string[colCount];
                                        for (var si = 0; si < colCount; si++) sectionSepRow[si] = "";
                                        table.AddRow(sectionSepRow);
                                    }

                                    if (showSectionHeaders)
                                    {
                                        var sectionRow = new string[colCount];
                                        sectionRow[0] = $"[bold]── {Markup.Escape(section.ModeName)} ({section.Items.Count}) ──[/]";
                                        for (var si = 1; si < colCount; si++) sectionRow[si] = "";
                                        table.AddRow(sectionRow);
                                    }

                                    var sectionCategories = GroupByStateCategory(section.Items);
                                    AddCategoryGroupRows(table, sectionCategories, activeContextId, isTeamView, dynamicColumns, colCount, cacheStaleMinutes);
                                    sectionIndex++;
                                }
                            }
                            else
                            {
                                // Flat category rendering (backward compat when no sections available)
                                var categoryGroups = GroupByStateCategory(items);
                                AddCategoryGroupRows(table, categoryGroups, activeContextId, isTeamView, dynamicColumns, colCount, cacheStaleMinutes);
                            }

                            // Compute and set progress footer
                            var sprintTotal = items.Count;
                            if (sprintTotal > 0)
                            {
                                var proposed = 0;
                                var inProgress = 0;
                                var resolved = 0;
                                var completed = 0;
                                foreach (var item in items)
                                {
                                    switch (_theme.ResolveCategory(item.State))
                                    {
                                        case StateCategory.Proposed: proposed++; break;
                                        case StateCategory.InProgress: inProgress++; break;
                                        case StateCategory.Resolved: resolved++; break;
                                        case StateCategory.Completed: completed++; break;
                                        case StateCategory.Removed: proposed++; break; // Removed bucketed with Proposed
                                        case StateCategory.Unknown: proposed++; break; // Unknown bucketed with Proposed
                                    }
                                }
                                var done = resolved + completed;
                                var doneColor = SpectreTheme.GetCategoryMarkupColor(StateCategory.Completed);
                                var ipColor = SpectreTheme.GetCategoryMarkupColor(StateCategory.InProgress);
                                var propColor = SpectreTheme.GetCategoryMarkupColor(StateCategory.Proposed);
                                var segments = new List<string>();
                                segments.Add($"[{doneColor}]{done}/{sprintTotal}[/] done");
                                if (inProgress > 0)
                                    segments.Add($"[{ipColor}]{inProgress}[/] in progress");
                                if (proposed > 0)
                                    segments.Add($"[{propColor}]{proposed}[/] proposed");
                                var caption = $"Sprint: {string.Join(" · ", segments)}";
                                savedCaption = caption;
                                table.Caption(new TableTitle(caption));
                            }

                            ctx.Refresh();
                            break;

                        case WorkspaceDataChunk.SeedsLoaded(var seeds):
                            if (seeds.Count > 0)
                            {
                                // Visual separator between sprint items and seeds
                                var seedSepRow = new string[colCount];
                                seedSepRow[0] = "[dim]───[/]";
                                seedSepRow[1] = "[dim]───[/]";
                                seedSepRow[2] = "[dim]Seeds[/]";
                                seedSepRow[3] = "[dim]───[/]";
                                for (var si = 4; si < colCount; si++) seedSepRow[si] = "[dim]───[/]";
                                table.AddRow(seedSepRow);

                                var seedIndicator = _theme.FormatSeedIndicator();

                                foreach (var seed in seeds)
                                {
                                    var staleMarker = seed.SeedCreatedAt.HasValue
                                        && seed.SeedCreatedAt.Value < DateTimeOffset.UtcNow.AddDays(-staleDays)
                                        ? " [yellow]⚠ stale[/]" : "";

                                    var seedRow = new List<string>
                                    {
                                        seed.Id < 0 ? $"[dim]{seed.Id}[/]" : seed.Id.ToString(),
                                        $"{seedIndicator} {_theme.FormatTypeBadge(seed.Type)}",
                                        Markup.Escape(seed.Title) + staleMarker,
                                        _theme.FormatState(seed.State),
                                    };

                                    if (isTeamView)
                                        seedRow.Add(Markup.Escape(seed.AssignedTo ?? "(unassigned)"));

                                    if (dynamicColumns is not null)
                                    {
                                        foreach (var col in dynamicColumns)
                                        {
                                            seed.Fields.TryGetValue(col.ReferenceName, out var fieldVal);
                                            var formatted = Formatters.FormatterHelpers.FormatFieldValue(fieldVal, col.DataType);
                                            seedRow.Add(Markup.Escape(formatted));
                                        }
                                    }

                                    table.AddRow(seedRow.ToArray());
                                }
                            }

                            // Exclusion footer
                            if (currentSections is { ExcludedItemIds.Count: > 0 })
                            {
                                var exclRow = new string[colCount];
                                var ids = string.Join(", ", currentSections.ExcludedItemIds.Select(id => $"#{id}"));
                                exclRow[0] = $"[dim]{currentSections.ExcludedItemIds.Count} excluded: {ids}[/]";
                                for (var ei = 1; ei < colCount; ei++) exclRow[ei] = "";
                                table.AddRow(exclRow);
                            }

                            ctx.Refresh();
                            break;

                        case WorkspaceDataChunk.RefreshStarted:
                            // Clear existing data rows so refreshed data replaces them
                            table.Rows.Clear();
                            table.Caption(new TableTitle("[yellow]⟳ refreshing...[/]"));
                            ctx.Refresh();
                            break;

                        case WorkspaceDataChunk.RefreshCompleted:
                            // Restore the original caption after refresh completes
                            table.Caption(new TableTitle(savedCaption ?? "[green]✓ up to date[/]"));
                            ctx.Refresh();
                            break;
                    }
                }
            });

        // Trailing newline prevents Oh My Posh prompt redraw from clipping the last row
        _console.WriteLine();
    }

    /// <summary>
    /// Groups work items by state category in display order. Categories with no items are omitted.
    /// </summary>
    private IReadOnlyList<(StateCategory Category, IReadOnlyList<WorkItem> Items)> GroupByStateCategory(
        IReadOnlyList<WorkItem> items)
    {
        var groups = new Dictionary<StateCategory, List<WorkItem>>
        {
            [StateCategory.Proposed] = new(),
            [StateCategory.InProgress] = new(),
            [StateCategory.Resolved] = new(),
            [StateCategory.Completed] = new(),
        };

        foreach (var item in items)
        {
            var category = _theme.ResolveCategory(item.State);
            if (groups.TryGetValue(category, out var list))
                list.Add(item);
            else
                groups[StateCategory.Proposed].Add(item);
        }

        var result = new List<(StateCategory, IReadOnlyList<WorkItem>)>();
        StateCategory[] displayOrder = [StateCategory.Proposed, StateCategory.InProgress, StateCategory.Resolved, StateCategory.Completed];
        foreach (var cat in displayOrder)
        {
            if (groups[cat].Count > 0)
                result.Add((cat, groups[cat]));
        }
        return result;
    }

    /// <summary>
    /// Adds category group rows (header + items) to the workspace table.
    /// Shared by both flat and mode-sectioned rendering paths.
    /// </summary>
    private void AddCategoryGroupRows(
        Table table,
        IReadOnlyList<(StateCategory Category, IReadOnlyList<WorkItem> Items)> categoryGroups,
        int? activeContextId,
        bool isTeamView,
        IReadOnlyList<ColumnSpec>? dynamicColumns,
        int colCount,
        int cacheStaleMinutes)
    {
        var catIndex = 0;
        foreach (var (category, catItems) in categoryGroups)
        {
            if (catIndex > 0)
            {
                var sepRow = new string[colCount];
                for (var si = 0; si < colCount; si++) sepRow[si] = "[dim]────[/]";
                table.AddRow(sepRow);
            }

            var headerRow = new string[colCount];
            headerRow[0] = $"[bold dim]{SpectreTheme.FormatCategoryHeader(category)}[/]";
            headerRow[1] = "";
            headerRow[2] = $"[dim]({catItems.Count})[/]";
            for (var ci = 3; ci < colCount; ci++) headerRow[ci] = "";
            table.AddRow(headerRow);

            foreach (var item in catItems)
            {
                var isActive = activeContextId.HasValue && item.Id == activeContextId.Value;
                var marker = isActive ? "[aqua]►[/] " : "";
                var boldOpen = isActive ? "[bold]" : "";
                var boldClose = isActive ? "[/]" : "";

                var cacheAge = CacheAgeFormatter.Format(item.LastSyncedAt, cacheStaleMinutes);
                var cacheAgeMarkup = cacheAge is not null ? $" [dim]{Markup.Escape(cacheAge)}[/]" : "";

                var row = new List<string>
                {
                    $"{marker}{boldOpen}{item.Id}{boldClose}",
                    _theme.FormatTypeBadge(item.Type),
                    $"{boldOpen}{Markup.Escape(item.Title)}{boldClose}{cacheAgeMarkup}",
                    _theme.FormatState(item.State),
                };

                if (isTeamView)
                    row.Add(Markup.Escape(item.AssignedTo ?? "(unassigned)"));

                if (dynamicColumns is not null)
                {
                    foreach (var col in dynamicColumns)
                    {
                        item.Fields.TryGetValue(col.ReferenceName, out var fieldVal);
                        var formatted = Formatters.FormatterHelpers.FormatFieldValue(fieldVal, col.DataType);
                        row.Add(Markup.Escape(formatted));
                    }
                }

                table.AddRow(row.ToArray());
            }
            catIndex++;
        }
    }

    public async Task RenderTreeAsync(
        Func<Task<WorkItem?>> getFocusedItem,
        Func<Task<IReadOnlyList<WorkItem>>> getParentChain,
        Func<Task<IReadOnlyList<WorkItem>>> getChildren,
        int maxChildren,
        int? activeId,
        CancellationToken ct,
        Func<int, Task<int?>>? getSiblingCount = null,
        Func<Task<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>>? getLinks = null)
    {
        // Stage 1: Load focused item and parent chain
        var focusedItem = await getFocusedItem();
        if (focusedItem is null)
            return;

        var parentChain = await getParentChain();

        // Build the Spectre Tree rooted at the topmost parent (or focused item if no parents).
        // focusContainer is the IHasTreeNodes where children should be appended.
        var (tree, focusContainer) = await BuildSpectreTreeAsync(focusedItem, parentChain, activeId, getSiblingCount);

        // EPIC-005: Unparented banner for tree view
        var treeRenderable = ApplyUnparentedBanner(tree, focusedItem, parentChain);

        // Stage 2: Render tree immediately (parent chain + focused item), then add children
        await _console.Live(treeRenderable)
            .StartAsync(async ctx =>
            {
                ctx.Refresh();

                // Stage 3: Load children progressively
                var children = await getChildren();
                var displayCount = Math.Min(children.Count, maxChildren);
                var hasMore = children.Count > maxChildren;

                for (var i = 0; i < displayCount; i++)
                {
                    var child = children[i];
                    var activeMarker = (activeId.HasValue && child.Id == activeId.Value)
                        ? "[aqua]●[/] " : "";
                    var dirty = child.IsDirty ? " [yellow]✎[/]" : "";
                    var effort = Formatters.FormatterHelpers.GetEffortDisplay(child);
                    var effortSuffix = effort is not null ? $" [dim]{Markup.Escape(effort)}[/]" : "";
                    // Spectre Tree owns its connector glyphs (├──/└──); use a colored │ prefix in the label instead
                    var stateColor = _theme.GetStateCategoryMarkupColor(child.State);
                    var label = $"[{stateColor}]│[/] {activeMarker}{_theme.FormatTypeBadge(child.Type)} #{child.Id} {Markup.Escape(child.Title)}{dirty} {_theme.FormatState(child.State)}{effortSuffix}";
                    focusContainer.AddNode(label);
                    ctx.Refresh();
                }

                if (hasMore)
                {
                    focusContainer.AddNode($"[dim]... and {children.Count - maxChildren} more[/]");
                    ctx.Refresh();
                }

                // Links section — fetch and render non-hierarchy links for focused item
                if (getLinks is not null)
                {
                    try
                    {
                        var links = await getLinks();
                        if (links.Count > 0)
                        {
                            focusContainer.AddNode("[dim]┊[/]");
                            var linksNode = focusContainer.AddNode("[blue]⇄[/] [dim]Links[/]");
                            for (var li = 0; li < links.Count; li++)
                            {
                                var link = links[li];
                                var linkLabel = $"[blue]{Markup.Escape(link.LinkType)}[/]: #{link.TargetId}";
                                linksNode.AddNode(linkLabel);
                            }
                            ctx.Refresh();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort — don't fail tree rendering */ }
                }
            });

        _console.WriteLine();
    }

    /// <summary>
    /// Builds a Spectre.Console <see cref="Tree"/> from the parent chain and focused item,
    /// optionally appending dimmed sibling count indicators (<c>...N</c>) at the same tree
    /// level as the annotated node. Null counts (root nodes) are omitted, consistent with
    /// <see cref="Formatters.HumanOutputFormatter"/>. Returns the tree and the
    /// <see cref="IHasTreeNodes"/> where children should be appended.
    /// </summary>
    internal async Task<(Tree Tree, IHasTreeNodes FocusContainer)> BuildSpectreTreeAsync(
        WorkItem focusedItem, IReadOnlyList<WorkItem> parentChain, int? activeId,
        Func<int, Task<int?>>? getSiblingCount)
    {
        if (parentChain.Count == 0)
        {
            var tree = new Tree(FormatFocusedNode(focusedItem, activeId));
            // Focused item is tree root — sibling count added as child (Spectre API limitation)
            if (getSiblingCount is not null && focusedItem.ParentId.HasValue)
            {
                var count = await getSiblingCount(focusedItem.Id);
                if (count.HasValue)
                    tree.AddNode(FormatSiblingCount(count.Value));
            }
            return (tree, tree);
        }

        var root = parentChain[0];
        var tree2 = new Tree(FormatParentNode(root));
        IHasTreeNodes container = tree2;

        // Root parent — sibling count added as child of tree root (Spectre API limitation)
        if (getSiblingCount is not null && root.ParentId.HasValue)
        {
            var rootCount = await getSiblingCount(root.Id);
            if (rootCount.HasValue)
                container.AddNode(FormatSiblingCount(rootCount.Value));
        }

        for (var i = 1; i < parentChain.Count; i++)
        {
            var parentNode = container.AddNode(FormatParentNode(parentChain[i]));
            // Sibling count at same level as node (added to container, not parentNode)
            if (getSiblingCount is not null && parentChain[i].ParentId.HasValue)
            {
                var count = await getSiblingCount(parentChain[i].Id);
                if (count.HasValue)
                    container.AddNode(FormatSiblingCount(count.Value));
            }
            container = parentNode;
        }

        var focusNode = container.AddNode(FormatFocusedNode(focusedItem, activeId));
        // Sibling count at same level as focused item (added to container, sibling of focusNode)
        if (getSiblingCount is not null && focusedItem.ParentId.HasValue)
        {
            var focusCount = await getSiblingCount(focusedItem.Id);
            if (focusCount.HasValue)
                container.AddNode(FormatSiblingCount(focusCount.Value));
        }
        return (tree2, focusNode);
    }

    internal static string FormatSiblingCount(int count) =>
        $"[dim]...{count}[/]";

    internal string FormatParentNode(WorkItem item) =>
        $"{_theme.FormatTypeBadge(item.Type)} [dim]{Markup.Escape(item.Title)}[/] {_theme.FormatState(item.State)}";

    internal string FormatFocusedNode(WorkItem item, int? activeId)
    {
        var marker = (activeId.HasValue && item.Id == activeId.Value) ? "[aqua]●[/] " : "";
        var dirty = item.IsDirty ? " [yellow]✎[/]" : "";
        return $"{marker}{_theme.FormatTypeBadge(item.Type)} [bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty} {_theme.FormatState(item.State)}";
    }

    private IRenderable ApplyUnparentedBanner(Tree tree, WorkItem focusedItem, IReadOnlyList<WorkItem> parentChain)
    {
        if (TypeLevelMap is null || ParentChildMap is null
            || parentChain.Count != 0
            || focusedItem.ParentId.HasValue
            || !TypeLevelMap.TryGetValue(focusedItem.Type.Value, out var focusLevel)
            || focusLevel == 0)
            return tree;
        var expectedParent = Formatters.HumanOutputFormatter.FindExpectedParentTypeName(
            focusedItem.Type.Value, ParentChildMap);
        return new Rows(
            new Markup($"[dim](unparented — expected under a {Markup.Escape(expectedParent ?? "a parent")})[/]"),
            tree);
    }

    /// <summary>
    /// Builds the complete tree view as a composite <see cref="IRenderable"/> without
    /// writing to the console. Used by <see cref="Commands.TreeCommand"/> to compose the tree
    /// view inside a <see cref="RenderWithSyncAsync"/> Live region, preventing the degenerate
    /// empty-cached-view pattern.
    /// </summary>
    internal async Task<IRenderable> BuildTreeViewAsync(
        WorkItem focusedItem,
        IReadOnlyList<WorkItem> parentChain,
        IReadOnlyList<WorkItem> children,
        int maxChildren,
        int? activeId,
        Func<int, Task<int?>>? getSiblingCount = null,
        IReadOnlyList<Domain.ValueObjects.WorkItemLink>? links = null,
        int cacheStaleMinutes = 5)
    {
        var (tree, focusContainer) = await BuildSpectreTreeAsync(focusedItem, parentChain, activeId, getSiblingCount);

        // Cache-age on focused node
        var focusCacheAge = CacheAgeFormatter.Format(focusedItem.LastSyncedAt, cacheStaleMinutes);
        if (focusCacheAge is not null)
        {
            // Append cache-age as a dimmed sibling node below the focus
            focusContainer.AddNode($"[dim]{Markup.Escape(focusCacheAge)}[/]");
        }

        // Add children with cache-age indicators on stale items
        var displayCount = Math.Min(children.Count, maxChildren);
        var hasMore = children.Count > maxChildren;

        for (var i = 0; i < displayCount; i++)
        {
            var child = children[i];
            var activeMarker = (activeId.HasValue && child.Id == activeId.Value)
                ? "[aqua]●[/] " : "";
            var dirty = child.IsDirty ? " [yellow]●[/]" : "";
            var effort = Formatters.FormatterHelpers.GetEffortDisplay(child);
            var effortSuffix = effort is not null ? $" [dim]{Markup.Escape(effort)}[/]" : "";
            var stateColor = _theme.GetStateCategoryMarkupColor(child.State);

            var childCacheAge = CacheAgeFormatter.Format(child.LastSyncedAt, cacheStaleMinutes);
            var childCacheAgeMarkup = childCacheAge is not null ? $" [dim]{Markup.Escape(childCacheAge)}[/]" : "";

            var label = $"[{stateColor}]│[/] {activeMarker}{_theme.FormatTypeBadge(child.Type)} #{child.Id} {Markup.Escape(child.Title)}{dirty} {_theme.FormatState(child.State)}{effortSuffix}{childCacheAgeMarkup}";
            focusContainer.AddNode(label);
        }

        if (hasMore)
        {
            focusContainer.AddNode($"[dim]... and {children.Count - maxChildren} more[/]");
        }

        // Links section
        if (links is { Count: > 0 })
        {
            focusContainer.AddNode("[dim]┊[/]");
            var linksNode = focusContainer.AddNode("[blue]⇄[/] [dim]Links[/]");
            foreach (var link in links)
            {
                var linkLabel = $"[blue]{Markup.Escape(link.LinkType)}[/]: #{link.TargetId}";
                linksNode.AddNode(linkLabel);
            }
        }

        // EPIC-005: Unparented banner for tree view
        return ApplyUnparentedBanner(tree, focusedItem, parentChain);
    }

    /// <summary>
    /// Builds the complete status view as a composite <see cref="IRenderable"/> without
    /// writing to the console. Used by <see cref="StatusCommand"/> to compose the status
    /// view inside a <see cref="RenderWithSyncAsync"/> Live region, preventing border
    /// corruption when the sync indicator clears.
    /// </summary>
    internal async Task<Spectre.Console.Rendering.IRenderable> BuildStatusViewAsync(
        WorkItem item,
        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
        IReadOnlyList<Domain.ValueObjects.FieldDefinition>? fieldDefinitions = null,
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null,
        (int Done, int Total)? childProgress = null,
        IReadOnlyList<Domain.ValueObjects.WorkItemLink>? links = null,
        WorkItem? parent = null,
        IReadOnlyList<WorkItem>? children = null,
        int cacheStaleMinutes = 5)
    {
        var pending = await getPendingChanges();

        // Cache-age indicator (FR-02, FR-03)
        var cacheAge = CacheAgeFormatter.Format(item.LastSyncedAt, cacheStaleMinutes);
        var cacheAgeMarkup = cacheAge is not null ? $" [dim]{Markup.Escape(cacheAge)}[/]" : "";

        var summaryBadge = _theme.FormatTypeBadge(item.Type);
        var summaryState = _theme.FormatState(item.State);
        var summaryMarkup = new Markup($"#{item.Id} [aqua]●[/] {summaryBadge} {Markup.Escape(item.Type.ToString())} — {Markup.Escape(item.Title)} {summaryState}{cacheAgeMarkup}");

        // Work item detail panel — dirty indicator uses ● (DD-03)
        var dirty = item.IsDirty ? " [yellow]●[/]" : "";
        var itemGrid = new Grid().AddColumn().AddColumn();
        itemGrid.AddRow("[dim]Type:[/]", _theme.FormatTypeBadge(item.Type) + " " + Markup.Escape(item.Type.ToString()));
        itemGrid.AddRow("[dim]State:[/]", _theme.FormatState(item.State));
        itemGrid.AddRow("[dim]Assigned:[/]", Markup.Escape(item.AssignedTo ?? "(unassigned)"));
        itemGrid.AddRow("[dim]Area:[/]", Markup.Escape(item.AreaPath.ToString()));
        itemGrid.AddRow("[dim]Iteration:[/]", Markup.Escape(item.IterationPath.ToString()));

        // Extended fields from the Fields dictionary
        AddExtendedFieldRows(itemGrid, item, fieldDefinitions, statusFieldEntries);

        if (childProgress is { Total: > 0 } cp)
        {
            // useAnsi: false — Spectre markup handles coloring; raw ANSI codes would be corrupted by Markup.Escape
            var progressBar = Formatters.FormatterHelpers.BuildProgressBar(cp.Done, cp.Total, useAnsi: false);
            if (!string.IsNullOrEmpty(progressBar))
            {
                var escaped = Markup.Escape(progressBar);
                // Complete bars use [green] without outer [dim] to match the bright green ANSI path
                if (Formatters.FormatterHelpers.IsProgressComplete(cp.Done, cp.Total))
                    itemGrid.AddRow("[dim]Progress:[/]", $"[green]{escaped}[/]");
                else
                    itemGrid.AddRow("[dim]Progress:[/]", $"[dim]{escaped}[/]");
            }
        }

        // Dirty-state summary using DirtyStateSummary (FR-04, FR-05, FR-06)
        var dirtySummary = DirtyStateSummary.Build(pending);
        if (dirtySummary is not null)
        {
            itemGrid.AddRow("", $"[yellow]{Markup.Escape(dirtySummary)}[/]");
            itemGrid.AddRow("", "[dim](unsaved — run 'twig save' to push)[/]");
        }

        // Relationships section — hierarchy + non-hierarchy links
        var hasRelationships = parent is not null || children is { Count: > 0 } || links is { Count: > 0 };
        if (hasRelationships)
        {
            itemGrid.AddRow("", ""); // visual spacer
            itemGrid.AddRow("[dim]⇄ Relationships:[/]", "");

            if (parent is not null)
                itemGrid.AddRow("", $"[dim]Parent:[/] {_theme.FormatTypeBadge(parent.Type)} #{parent.Id} {Markup.Escape(parent.Title)}");

            if (children is { Count: > 0 })
            {
                foreach (var child in children)
                {
                    var childState = _theme.FormatState(child.State);
                    itemGrid.AddRow("", $"[dim]Child:[/]  {_theme.FormatTypeBadge(child.Type)} #{child.Id} {Markup.Escape(child.Title)} {childState}");
                }
            }

            if (links is { Count: > 0 })
            {
                foreach (var link in links)
                    itemGrid.AddRow("", $"[blue]{Markup.Escape(link.LinkType)}[/]: #{link.TargetId}");
            }
        }

        IRenderable panelContent = itemGrid;
        if (item.Fields.TryGetValue("System.Description", out var rawDescription))
        {
            var descriptionMarkup = Formatters.FormatterHelpers.HtmlToSpectreMarkup(rawDescription);
            if (!string.IsNullOrWhiteSpace(descriptionMarkup))
                panelContent = new Rows(itemGrid,
                    new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim"),
                    new Markup(descriptionMarkup));
        }

        var itemPanel = new Panel(panelContent)
            .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}{cacheAgeMarkup}")
            .Border(BoxBorder.Rounded)
            .Expand();

        return new Rows(summaryMarkup, itemPanel);
    }

    public async Task RenderStatusAsync(
        Func<Task<WorkItem?>> getItem,
        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
        CancellationToken ct,
        IReadOnlyList<Domain.ValueObjects.FieldDefinition>? fieldDefinitions = null,
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null,
        (int Done, int Total)? childProgress = null,
        IReadOnlyList<Domain.ValueObjects.WorkItemLink>? links = null,
        WorkItem? parent = null,
        IReadOnlyList<WorkItem>? children = null,
        int cacheStaleMinutes = 5)
    {
        var item = await getItem();
        if (item is null)
            return;

        var view = await BuildStatusViewAsync(
            item,
            getPendingChanges, fieldDefinitions, statusFieldEntries, childProgress, links, parent, children,
            cacheStaleMinutes: cacheStaleMinutes);
        _console.Write(view);
    }

    public async Task RenderWorkItemAsync(
        Func<Task<WorkItem?>> getItem,
        bool showDirty,
        CancellationToken ct)
    {
        // Stage 1: Load work item (core fields are available immediately)
        var item = await getItem();
        if (item is null)
            return;

        // Build initial panel with core fields only (type, state, assigned, area, iteration)
        var dirty = showDirty && item.IsDirty ? " [yellow]●[/]" : "";
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[dim]Type:[/]", _theme.FormatTypeBadge(item.Type) + " " + Markup.Escape(item.Type.ToString()));
        grid.AddRow("[dim]State:[/]", _theme.FormatState(item.State));
        grid.AddRow("[dim]Assigned:[/]", Markup.Escape(item.AssignedTo ?? "(unassigned)"));
        grid.AddRow("[dim]Area:[/]", Markup.Escape(item.AreaPath.ToString()));
        grid.AddRow("[dim]Iteration:[/]", Markup.Escape(item.IterationPath.ToString()));

        // Stage 2: Progressively add extended fields from the Fields dictionary
        IRenderable? descriptionSection = null;

        await _console.Live(new Markup("[dim]Loading...[/]"))
            .StartAsync(async ctx =>
            {
                Panel BuildPanel()
                {
                    IRenderable panelContent = descriptionSection is not null
                        ? new Rows(grid,
                            new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim"),
                            descriptionSection)
                        : grid;

                    return new Panel(panelContent)
                        .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
                        .Border(BoxBorder.Rounded)
                        .Expand();
                }

                ctx.UpdateTarget(BuildPanel());
                ctx.Refresh();

                // Yield to allow the core panel to render before loading extended fields
                await Task.Yield();
                ct.ThrowIfCancellationRequested();

                // Extended field: History (latest comment)
                if (item.Fields.TryGetValue("System.History", out var history)
                    && !string.IsNullOrWhiteSpace(history))
                {
                    grid.AddRow("[dim]History:[/]", Markup.Escape(TruncateField(history, 200)));
                    ctx.UpdateTarget(BuildPanel());
                    ctx.Refresh();
                }

                // Extended field: Tags
                if (item.Fields.TryGetValue("System.Tags", out var tags)
                    && !string.IsNullOrWhiteSpace(tags))
                {
                    grid.AddRow("[dim]Tags:[/]", Markup.Escape(TruncateField(tags, 200)));
                    ctx.UpdateTarget(BuildPanel());
                    ctx.Refresh();
                }

                // Extended field: Description (full-width section below grid)
                if (item.Fields.TryGetValue("System.Description", out var description))
                {
                    var markup = Formatters.FormatterHelpers.HtmlToSpectreMarkup(description);
                    if (!string.IsNullOrWhiteSpace(markup))
                    {
                        descriptionSection = new Markup(markup);
                        ctx.UpdateTarget(BuildPanel());
                        ctx.Refresh();
                    }
                }
            });

        _console.WriteLine();
    }

    // Core fields excluded from extended display (already shown as dedicated rows)
    private static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.WorkItemType", "System.Title", "System.State",
        "System.AssignedTo", "System.IterationPath", "System.AreaPath",
        "System.Rev", "System.TeamProject",
    };

    /// <summary>
    /// Adds extended field rows to a grid for status display.
    /// Shows populated Fields with display names resolved from field definitions.
    /// </summary>
    private static void AddExtendedFieldRows(
        Grid grid, WorkItem item,
        IReadOnlyList<Domain.ValueObjects.FieldDefinition>? fieldDefinitions,
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null)
    {
        if (item.Fields.Count == 0)
            return;

        var defLookup = fieldDefinitions?.ToDictionary(d => d.ReferenceName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Domain.ValueObjects.FieldDefinition>(StringComparer.OrdinalIgnoreCase);

        if (statusFieldEntries is not null)
        {
            foreach (var entry in statusFieldEntries)
            {
                if (!entry.IsIncluded)
                    continue;
                if (string.Equals(entry.ReferenceName, "System.Description", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!item.Fields.TryGetValue(entry.ReferenceName, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                var displayName = defLookup.TryGetValue(entry.ReferenceName, out var def)
                    ? def.DisplayName
                    : Domain.Services.ColumnResolver.DeriveDisplayName(entry.ReferenceName);
                var dataType = def?.DataType ?? "string";
                var formatted = Formatters.FormatterHelpers.FormatFieldValue(value, dataType, maxWidth: 60);

                if (!string.IsNullOrWhiteSpace(formatted))
                    grid.AddRow($"[dim]{Markup.Escape(displayName)}:[/]", Markup.Escape(formatted));
            }
            return;
        }

        var count = 0;
        foreach (var kvp in item.Fields)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            if (CoreFields.Contains(kvp.Key))
                continue;
            if (string.Equals(kvp.Key, "System.Description", StringComparison.OrdinalIgnoreCase))
                continue;
            if (count >= 10)
                break;

            var displayName = defLookup.TryGetValue(kvp.Key, out var def2)
                ? def2.DisplayName
                : Domain.Services.ColumnResolver.DeriveDisplayName(kvp.Key);
            var dataType = def2?.DataType ?? "string";
            var formatted = Formatters.FormatterHelpers.FormatFieldValue(kvp.Value, dataType, maxWidth: 60);

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                grid.AddRow($"[dim]{Markup.Escape(displayName)}:[/]", Markup.Escape(formatted));
                count++;
            }
        }
    }

    /// <summary>
    /// Truncates a field value to the specified maximum length, appending "…" if truncated.
    /// Strips HTML tags for clean display.
    /// </summary>
    internal static string TruncateField(string value, int maxLength)
    {
        // Strip basic HTML tags (ADO fields often contain HTML)
        var stripped = StripHtmlTags(value).Trim();
        if (stripped.Length <= maxLength)
            return stripped;
        return stripped[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Removes HTML tags from a string using a simple regex-free approach.
    /// Handles unclosed '&lt;' by treating it as a literal character when no
    /// matching '&gt;' is found before the next '&lt;' or end-of-string.
    /// </summary>
    internal static string StripHtmlTags(string input)
    {
        var result = new System.Text.StringBuilder(input.Length);
        var buffer = new System.Text.StringBuilder();
        var inTag = false;
        foreach (var c in input)
        {
            if (c == '<')
            {
                if (inTag)
                {
                    // Previous '<' had no matching '>' — flush it as literal
                    result.Append(buffer);
                    buffer.Clear();
                }
                inTag = true;
                buffer.Append(c);
                continue;
            }
            if (c == '>' && inTag)
            {
                // Matched tag — discard buffer contents (the tag)
                inTag = false;
                buffer.Clear();
                continue;
            }
            if (inTag)
            {
                buffer.Append(c);
            }
            else
            {
                result.Append(c);
            }
        }
        // If we ended while inside an unclosed '<', flush buffered content as literal
        if (inTag)
            result.Append(buffer);
        return result.ToString();
    }

    /// <summary>
    /// Shows an interactive selection prompt using <see cref="LiveDisplayContext"/>.
    /// Uses <c>System.Console.ReadKey</c> for keyboard input (AOT-safe — avoids
    /// <c>SelectionPrompt&lt;T&gt;</c> which produces IL2067 trim warnings).
    /// <para>
    /// <b>Cancellation note</b>: <paramref name="ct"/> is checked at each loop
    /// iteration boundary and also cancels the <c>Task.Run</c> wrapper around
    /// <c>ReadKey</c>. If the token fires while <c>ReadKey</c> is blocking, the
    /// resulting <see cref="OperationCanceledException"/> is caught and the prompt
    /// exits cleanly (returning <c>null</c>).
    /// </para>
    /// </summary>
    public async Task<(int Id, string Title)?> PromptDisambiguationAsync(
        IReadOnlyList<(int Id, string Title)> matches,
        CancellationToken ct)
    {
        if (matches.Count == 0)
            return null;

        var selectedIndex = 0;
        var filterText = "";
        var filtered = new List<(int Id, string Title)>(matches);
        (int Id, string Title)? result = null;
        var done = false;

        // Custom Live()-based selection prompt (AOT-safe fallback — SelectionPrompt<T>
        // produces IL2067 trim warnings via TypeConverterHelper, see ITEM-001A spike).
        await _console.Live(new Markup("[dim]Loading...[/]"))
            .StartAsync(async ctx =>
            {
                while (!done && !ct.IsCancellationRequested)
                {
                    ctx.UpdateTarget(BuildSelectionRenderable(filtered, selectedIndex, filterText));
                    ctx.Refresh();

                    ConsoleKeyInfo key;
                    try
                    {
                        key = await Task.Run(() => System.Console.ReadKey(true), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Token fired while ReadKey was blocking — exit cleanly
                        done = true;
                        break;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (selectedIndex > 0) selectedIndex--;
                            break;
                        case ConsoleKey.DownArrow:
                            if (selectedIndex < filtered.Count - 1) selectedIndex++;
                            break;
                        case ConsoleKey.Enter:
                            if (filtered.Count > 0)
                                result = filtered[selectedIndex];
                            done = true;
                            break;
                        case ConsoleKey.Escape:
                            done = true;
                            break;
                        case ConsoleKey.Backspace:
                            if (filterText.Length > 0)
                            {
                                filterText = filterText[..^1];
                                ApplyFilter(matches, filterText, ref filtered, ref selectedIndex);
                            }
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar))
                            {
                                filterText += key.KeyChar;
                                ApplyFilter(matches, filterText, ref filtered, ref selectedIndex);
                            }
                            break;
                    }
                }
            });

        _console.WriteLine();

        return result;
    }

    private static void ApplyFilter(
        IReadOnlyList<(int Id, string Title)> allMatches,
        string filter,
        ref List<(int Id, string Title)> filtered,
        ref int selectedIndex)
    {
        filtered = string.IsNullOrEmpty(filter)
            ? new List<(int Id, string Title)>(allMatches)
            : allMatches
                .Where(m => m.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || m.Id.ToString().Contains(filter, StringComparison.Ordinal))
                .ToList();
        selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, filtered.Count - 1));
    }

    internal static IRenderable BuildSelectionRenderable(
        IReadOnlyList<(int Id, string Title)> items,
        int selectedIndex,
        string filterText)
    {
        var rows = new List<IRenderable>
        {
            new Markup("[bold]Multiple matches — select one:[/]")
        };

        if (!string.IsNullOrEmpty(filterText))
            rows.Add(new Markup($"[dim]Filter: {Markup.Escape(filterText)}[/]"));

        if (items.Count == 0)
        {
            rows.Add(new Markup("[yellow]No items match filter[/]"));
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var (id, title) = items[i];
                var prefix = i == selectedIndex ? "[aqua]❯[/] " : "  ";
                var style = i == selectedIndex ? "bold" : "dim";
                rows.Add(new Markup($"{prefix}[{style}]#{id} {Markup.Escape(title)}[/]"));
            }
        }

        rows.Add(new Markup("[dim]↑/↓ navigate · Enter select · Esc cancel · type to filter[/]"));

        return new Rows(rows);
    }

    /// <summary>
    /// Enriched selection renderable with type badge and state color when available.
    /// </summary>
    internal static IRenderable BuildSelectionRenderable(
        IReadOnlyList<(int Id, string Title, string? TypeName, string? State)> items,
        int selectedIndex,
        string filterText,
        SpectreTheme theme)
    {
        var rows = new List<IRenderable>
        {
            new Markup("[bold]Multiple matches — select one:[/]")
        };

        if (!string.IsNullOrEmpty(filterText))
            rows.Add(new Markup($"[dim]Filter: {Markup.Escape(filterText)}[/]"));

        if (items.Count == 0)
        {
            rows.Add(new Markup("[yellow]No items match filter[/]"));
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var (id, title, typeName, state) = items[i];
                var prefix = i == selectedIndex ? "[aqua]❯[/] " : "  ";
                var style = i == selectedIndex ? "bold" : "dim";

                var badgeMarkup = "";
                if (typeName is not null)
                {
                    var parseResult = Domain.ValueObjects.WorkItemType.Parse(typeName);
                    if (parseResult.IsSuccess)
                        badgeMarkup = theme.FormatTypeBadge(parseResult.Value) + " ";
                }

                var stateMarkup = "";
                if (state is not null)
                    stateMarkup = " " + theme.FormatState(state);

                rows.Add(new Markup($"{prefix}{badgeMarkup}[{style}]#{id} {Markup.Escape(title)}[/]{stateMarkup}"));
            }
        }

        rows.Add(new Markup("[dim]↑/↓ navigate · Enter select · Esc cancel · type to filter[/]"));

        return new Rows(rows);
    }

    /// <summary>
    /// Delay before clearing transient sync status messages. Exposed as internal for test overrides.
    /// </summary>
    internal TimeSpan SyncStatusDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public async Task RenderWithSyncAsync(
        Func<Task<IRenderable>> buildCachedView,
        Func<Task<SyncResult>> performSync,
        Func<SyncResult, Task<IRenderable?>> buildRevisedView,
        CancellationToken ct)
    {
        var cachedView = await buildCachedView();

        await _console.Live(cachedView)
            .StartAsync(async ctx =>
            {
                ctx.Refresh();

                // Show syncing indicator below cached data
                ctx.UpdateTarget(new Rows(cachedView, new Markup("[dim]⟳ syncing...[/]")));
                ctx.Refresh();

                var result = await performSync();

                switch (result)
                {
                    case SyncResult.UpToDate or SyncResult.Skipped:
                        ctx.UpdateTarget(new Rows(cachedView, new Markup("[dim]✓ up to date[/]")));
                        ctx.Refresh();
                        await Task.Delay(SyncStatusDelay, ct);
                        ctx.UpdateTarget(new Rows(cachedView, new Text(" ")));
                        ctx.Refresh();
                        break;

                    case SyncResult.Updated updated:
                        var revisedView = await buildRevisedView(updated);
                        var displayView = revisedView ?? cachedView;
                        var countLabel = updated.ChangedCount == 1
                            ? "1 item updated"
                            : $"{updated.ChangedCount} items updated";
                        ctx.UpdateTarget(new Rows(displayView, new Markup($"[green]✓ {countLabel}[/]")));
                        ctx.Refresh();
                        await Task.Delay(SyncStatusDelay, ct);
                        ctx.UpdateTarget(new Rows(displayView, new Text(" ")));
                        ctx.Refresh();
                        break;

                    case SyncResult.PartiallyUpdated partial:
                        var partialRevisedView = await buildRevisedView(partial);
                        var partialDisplayView = partialRevisedView ?? cachedView;
                        var savedLabel = partial.SavedCount == 1
                            ? "1 item updated"
                            : $"{partial.SavedCount} items updated";
                        var failedLabel = partial.Failures.Count == 1
                            ? "1 failed"
                            : $"{partial.Failures.Count} failed";
                        ctx.UpdateTarget(new Rows(partialDisplayView, new Markup($"[yellow]⚠ {savedLabel}, {failedLabel}[/]")));
                        ctx.Refresh();
                        await Task.Delay(SyncStatusDelay, ct);
                        ctx.UpdateTarget(new Rows(partialDisplayView, new Text(" ")));
                        ctx.Refresh();
                        break;

                    case SyncResult.Failed failed:
                        var reason = string.IsNullOrWhiteSpace(failed.Reason) ? "offline" : Markup.Escape(failed.Reason);
                        ctx.UpdateTarget(new Rows(cachedView, new Markup($"[yellow]⚠ sync failed ({reason})[/]")));
                        ctx.Refresh();
                        // Failed status persists — no delay/clear
                        break;

                    default:
                        throw new System.Diagnostics.UnreachableException($"Unhandled SyncResult: {result.GetType().Name}");
                }
            });

        _console.WriteLine();
    }


    public Task RenderFlowSummaryAsync(
        WorkItem item,
        string originalState,
        string? newState,
        string? branchName,
        CancellationToken ct = default)
    {
        // Success header
        _console.MarkupLine($"[green]✓[/] [bold]Flow started for #{item.Id} — {Markup.Escape(item.Title)}[/]");

        // Build summary grid
        var grid = new Grid().AddColumn().AddColumn();

        if (newState is not null)
        {
            var oldColor = _theme.GetStateCategoryMarkupColor(originalState);
            var newColor = _theme.GetStateCategoryMarkupColor(newState);
            grid.AddRow("[dim]State:[/]",
                $"[{oldColor}]{Markup.Escape(originalState)}[/] [green]→[/] [{newColor}]{Markup.Escape(newState)}[/]");
        }
        else
        {
            var stateColor = _theme.GetStateCategoryMarkupColor(originalState);
            grid.AddRow("[dim]State:[/]", $"[{stateColor}]{Markup.Escape(originalState)}[/]");
        }

        if (branchName is not null)
            grid.AddRow("[dim]Branch:[/]", Markup.Escape(branchName));

        grid.AddRow("[dim]Context:[/]", $"set to #{item.Id}");

        var panel = new Panel(grid)
            .Header("[bold]Summary[/]")
            .Border(BoxBorder.Rounded);

        _console.Write(panel);

        return Task.CompletedTask;
    }

    public void RenderHints(IReadOnlyList<string> hints)
    {
        if (hints.Count == 0)
            return;

        foreach (var hint in hints)
        {
            _console.MarkupLine($"[yellow]→[/] [dim]hint: {Markup.Escape(hint)}[/]");
        }
    }

    public async Task RenderSeedViewAsync(
        Func<Task<IReadOnlyList<Domain.ReadModels.SeedViewGroup>>> getData,
        int totalWritableFields,
        int staleDays,
        CancellationToken ct,
        IReadOnlyDictionary<int, IReadOnlyList<Domain.ValueObjects.SeedLink>>? links = null)
    {
        var groups = await getData();

        var totalSeeds = 0;
        foreach (var g in groups)
            totalSeeds += g.Seeds.Count;

        _console.MarkupLine($"[bold]Seeds ({totalSeeds})[/]");
        _console.Write(new Rule().RuleStyle("dim"));

        if (totalSeeds == 0)
        {
            _console.MarkupLine("[dim italic]  No seeds[/]");
            return;
        }

        foreach (var group in groups)
        {
            _console.WriteLine();
            if (group.Parent is not null)
            {
                var parentBadge = _theme.FormatTypeBadge(group.Parent.Type);
                _console.MarkupLine($"  [bold]Parent:[/] #{group.Parent.Id} {parentBadge} {Markup.Escape(group.Parent.Type.ToString())} — {Markup.Escape(group.Parent.Title)}");
            }
            else
            {
                _console.MarkupLine("  [bold]Orphan Seeds[/]");
            }

            var table = new Table()
                .Border(TableBorder.None)
                .HideHeaders()
                .AddColumn(new TableColumn("ID").RightAligned())
                .AddColumn(new TableColumn("Type"))
                .AddColumn(new TableColumn("Title"))
                .AddColumn(new TableColumn("Age").RightAligned())
                .AddColumn(new TableColumn("Fields").RightAligned())
                .AddColumn(new TableColumn("Status"));

            foreach (var seed in group.Seeds)
            {
                var badge = _theme.FormatTypeBadge(seed.Type);
                var age = Formatters.HumanOutputFormatter.FormatSeedAge(seed.SeedCreatedAt);
                var filled = Formatters.HumanOutputFormatter.CountNonEmptyFields(seed);
                var staleWarning = Formatters.HumanOutputFormatter.IsStaleSeed(seed, staleDays) ? "[red]⚠ stale[/]" : "";

                // Build link annotations for this seed
                var linkText = "";
                if (links is not null && links.TryGetValue(seed.Id, out var seedLinks))
                {
                    var annotations = new List<string>();
                    foreach (var link in seedLinks)
                    {
                        var annotation = Formatters.HumanOutputFormatter.FormatLinkAnnotation(seed.Id, link);
                        annotations.Add($"[cyan]→ {Markup.Escape(annotation)}[/]");
                    }
                    if (annotations.Count > 0)
                        linkText = string.Join("  ", annotations);
                }

                var statusCol = staleWarning;
                if (!string.IsNullOrEmpty(linkText))
                    statusCol = string.IsNullOrEmpty(statusCol) ? linkText : $"{statusCol}  {linkText}";

                table.AddRow(
                    $"{seed.Id}",
                    $"{badge} {Markup.Escape(seed.Type.ToString())}",
                    Markup.Escape(seed.Title),
                    $"[dim]{Markup.Escape(age)}[/]",
                    $"[dim]{filled}/{totalWritableFields} fields[/]",
                    statusCol);
            }

            _console.Write(table);
        }
    }

    // ── Interactive tree navigator ──────────────────────────────────

    /// <inheritdoc />
    public async Task<int?> RenderInteractiveTreeAsync(
        TreeNavigatorState initialState,
        Func<int, Task<TreeNavigatorState>> loadNodeState,
        CancellationToken ct)
    {
        var state = initialState;
        int? result = null;
        var done = false;
        var singleColumn = _console.Profile.Width < 80;
        string? linkError = null;

        await _console.Live(new Markup("[dim]Loading...[/]"))
            .StartAsync(async ctx =>
            {
                while (!done && !ct.IsCancellationRequested)
                {
                    var treePanel = BuildInteractiveTreeRenderable(state, _theme);
                    IRenderable renderable;
                    if (singleColumn)
                    {
                        renderable = treePanel;
                    }
                    else
                    {
                        IRenderable previewPanel;
                        if (linkError is not null)
                        {
                            previewPanel = new Panel(new Markup(linkError))
                                .Header("[bold]Preview[/]")
                                .Border(BoxBorder.Rounded)
                                .Expand();
                        }
                        else
                        {
                            previewPanel = BuildPreviewPanel(
                                state.CursorItem, state.Links, state.SeedLinks, _theme,
                                state.LinkJumpIndex);
                        }
                        renderable = new Columns(treePanel, previewPanel);
                    }

                    ctx.UpdateTarget(renderable);
                    ctx.Refresh();

                    ConsoleKeyInfo key;
                    try
                    {
                        key = await Task.Run(() => System.Console.ReadKey(true), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        done = true;
                        break;
                    }

                    var action = ProcessKey(key, state);
                    linkError = null;
                    switch (action)
                    {
                        case NavigatorAction.CursorMoved:
                            if (state.CursorItem is not null)
                            {
                                var newState = await loadNodeState(state.CursorItem.Id);
                                state.UpdateNodeData(newState.Children, newState.Links, newState.SeedLinks);
                            }
                            break;

                        case NavigatorAction.NeedExpand:
                            if (state.CursorItem is not null)
                            {
                                var expanded = await loadNodeState(state.CursorItem.Id);
                                state.Expand(expanded.Children);
                            }
                            break;

                        case NavigatorAction.FilterUpdated:
                            if (state.CursorItem is not null)
                            {
                                var filterState = await loadNodeState(state.CursorItem.Id);
                                state.UpdateNodeData(filterState.Children, filterState.Links, filterState.SeedLinks);
                            }
                            break;

                        case NavigatorAction.FilterCleared:
                            if (state.CursorItem is not null)
                            {
                                var restoredState = await loadNodeState(state.CursorItem.Id);
                                state.UpdateNodeData(restoredState.Children, restoredState.Links, restoredState.SeedLinks);
                            }
                            break;

                        case NavigatorAction.LinkJump:
                            var combinedLinks = state.GetCombinedLinks();
                            if (state.LinkJumpIndex >= 0 && state.LinkJumpIndex < combinedLinks.Count)
                            {
                                var targetId = combinedLinks[state.LinkJumpIndex].TargetId;
                                var jumpState = await loadNodeState(targetId);
                                if (jumpState.CursorItem is null)
                                {
                                    linkError = $"[yellow]Item #{targetId} not in cache. Run 'twig sync' to fetch.[/]";
                                }
                                else
                                {
                                    state = jumpState;
                                }
                            }
                            break;

                        case NavigatorAction.DrillDown:
                            if (state.Children.Count > 0)
                            {
                                var firstChildId = state.Children[0].Id;
                                state = await loadNodeState(firstChildId);
                            }
                            break;

                        case NavigatorAction.NavigateToParent:
                            if (state.ParentChain.Count > 0)
                            {
                                var parentItem = state.ParentChain[^1];
                                state = await loadNodeState(parentItem.Id);
                            }
                            break;

                        case NavigatorAction.Committed:
                            done = true;
                            result = state.CursorItem?.Id;
                            break;

                        case NavigatorAction.Cancelled:
                            done = true;
                            result = null;
                            break;
                    }
                }
            });

        _console.WriteLine();

        return result;
    }

    /// <summary>
    /// Action returned by <see cref="ProcessKey"/> indicating what the render loop should do next.
    /// </summary>
    internal enum NavigatorAction
    {
        /// <summary>No action needed — key was ignored or state already updated.</summary>
        None,
        /// <summary>Cursor moved to a different item — caller should load node data.</summary>
        CursorMoved,
        /// <summary>Children collapsed — no async work needed.</summary>
        Collapsed,
        /// <summary>Right arrow on unexpanded node — caller should load and expand children.</summary>
        NeedExpand,
        /// <summary>Enter pressed — commit current item.</summary>
        Committed,
        /// <summary>Escape pressed (outside filter mode) — cancel navigation.</summary>
        Cancelled,
        /// <summary>Filter cleared via Escape or Backspace-to-empty — caller should reload node data for the restored cursor item.</summary>
        FilterCleared,
        /// <summary>Filter text changed — caller should reload node data for cursor item.</summary>
        FilterUpdated,
        /// <summary>Tab/Shift+Tab — caller should load the link target and re-root.</summary>
        LinkJump,
        /// <summary>Right arrow on expanded node — caller should re-center on first child.</summary>
        DrillDown,
        /// <summary>Left arrow with no children — caller should re-center on parent.</summary>
        NavigateToParent,
    }

    /// <summary>
    /// Pure synchronous key dispatch: mutates <paramref name="state"/> and returns the
    /// <see cref="NavigatorAction"/> the caller should perform (e.g. load data, exit loop).
    /// Extracted from the render loop for testability.
    /// </summary>
    internal static NavigatorAction ProcessKey(ConsoleKeyInfo key, TreeNavigatorState state)
    {
        // ── Filter mode intercepts ──────────────────────────────────
        if (state.IsFilterMode)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    state.ClearFilter();
                    return NavigatorAction.FilterCleared;

                case ConsoleKey.Enter:
                    state.AcceptFilter();
                    return NavigatorAction.CursorMoved;

                case ConsoleKey.Backspace:
                    if (state.FilterText.Length > 0)
                    {
                        var newText = state.FilterText[..^1];
                        if (newText.Length == 0)
                        {
                            state.ClearFilter();
                            return NavigatorAction.FilterCleared;
                        }
                        state.ApplyFilter(newText);
                        return NavigatorAction.FilterUpdated;
                    }
                    state.ClearFilter();
                    return NavigatorAction.FilterCleared;

                case ConsoleKey.UpArrow:
                case ConsoleKey.DownArrow:
                    break; // Fall through to normal navigation

                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        state.ApplyFilter(state.FilterText + key.KeyChar);
                        return NavigatorAction.FilterUpdated;
                    }
                    return NavigatorAction.None;
            }
        }

        // ── Normal (navigate) mode ──────────────────────────────────
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.K when key.Modifiers == 0:
                var prevId = state.CursorItem?.Id;
                state.MoveCursorUp();
                return state.CursorItem?.Id != prevId
                    ? NavigatorAction.CursorMoved
                    : NavigatorAction.None;

            case ConsoleKey.DownArrow:
            case ConsoleKey.J when key.Modifiers == 0:
                var prevIdDown = state.CursorItem?.Id;
                state.MoveCursorDown();
                return state.CursorItem?.Id != prevIdDown
                    ? NavigatorAction.CursorMoved
                    : NavigatorAction.None;

            case ConsoleKey.LeftArrow:
                if (state.Children.Count > 0)
                {
                    state.Collapse();
                    return NavigatorAction.Collapsed;
                }
                return state.ParentChain.Count > 0
                    ? NavigatorAction.NavigateToParent
                    : NavigatorAction.None;

            case ConsoleKey.RightArrow:
                if (state.Children.Count == 0 && state.CursorItem is not null)
                    return NavigatorAction.NeedExpand;
                return state.Children.Count > 0
                    ? NavigatorAction.DrillDown
                    : NavigatorAction.None;

            case ConsoleKey.Enter:
                return NavigatorAction.Committed;

            case ConsoleKey.Escape:
                return NavigatorAction.Cancelled;

            case ConsoleKey.Tab:
                var link = (key.Modifiers & ConsoleModifiers.Shift) != 0
                    ? state.ReverseLinkJump()
                    : state.AdvanceLinkJump();
                return link.HasValue ? NavigatorAction.LinkJump : NavigatorAction.None;

            default:
                // Enter filter mode on printable characters
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    state.ApplyFilter(state.FilterText + key.KeyChar);
                    return NavigatorAction.FilterUpdated;
                }
                return NavigatorAction.None;
        }
    }

    /// <summary>
    /// Builds the interactive tree panel from navigator state.
    /// Shows dimmed parent chain, bold cursor with ❯ marker, children with type badges, and status bar.
    /// </summary>
    internal static IRenderable BuildInteractiveTreeRenderable(TreeNavigatorState state, SpectreTheme theme)
    {
        var rows = new List<IRenderable>();

        // Build tree with parent chain as dimmed ancestors
        IRenderable treeContent;
        if (state.ParentChain.Count > 0)
        {
            var root = state.ParentChain[0];
            var tree = new Tree($"[dim]{theme.FormatTypeBadge(root.Type)} {Markup.Escape(root.Title)}[/]");
            IHasTreeNodes container = tree;

            for (var i = 1; i < state.ParentChain.Count; i++)
            {
                var ancestor = state.ParentChain[i];
                container = container.AddNode(
                    $"[dim]{theme.FormatTypeBadge(ancestor.Type)} {Markup.Escape(ancestor.Title)}[/]");
            }

            // Add visible siblings at cursor level
            AddSiblingsToTree(container, state, theme);
            treeContent = tree;
        }
        else if (state.VisibleSiblings.Count > 0)
        {
            // No parent chain — siblings are all root-level peers (flat list, not a tree)
            var siblingRows = new List<IRenderable>();
            for (var i = 0; i < state.VisibleSiblings.Count; i++)
            {
                var sibling = state.VisibleSiblings[i];
                var label = FormatSiblingLabel(sibling, i, state, theme);
                siblingRows.Add(new Markup(label));

                if (state.CursorIndex == i && state.Children.Count > 0)
                {
                    // Show children indented under cursor
                    foreach (var child in state.Children)
                    {
                        var effort = Formatters.FormatterHelpers.GetEffortDisplay(child);
                        var effortSuffix = effort is not null ? $" [dim]{Markup.Escape(effort)}[/]" : "";
                        var childLabel = $"    {theme.FormatTypeBadge(child.Type)} #{child.Id} {Markup.Escape(child.Title)} {theme.FormatState(child.State)}{effortSuffix}";
                        siblingRows.Add(new Markup(childLabel));
                    }
                }
            }

            treeContent = new Rows(siblingRows);
        }
        else
        {
            treeContent = new Markup("[dim italic]No items to display[/]");
        }

        rows.Add(treeContent);

        // Sibling count hint
        if (state.VisibleSiblings.Count > 10)
        {
            rows.Add(new Markup($"[dim]...{state.VisibleSiblings.Count} total[/]"));
        }

        // Status bar
        rows.Add(new Rule().RuleStyle("dim"));
        if (state.IsFilterMode)
        {
            rows.Add(new Markup($"Filter: {Markup.Escape(state.FilterText)}_"));
        }
        else
        {
            rows.Add(new Markup("[dim]↑↓ navigate · ←→ collapse/expand · Enter select · Tab link · Esc exit[/]"));
        }

        return new Panel(new Rows(rows))
            .Header("[bold]Tree[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static string FormatSiblingLabel(WorkItem item, int index, TreeNavigatorState state, SpectreTheme theme)
    {
        var isCursor = index == state.CursorIndex;
        var marker = isCursor ? "[aqua]❯[/] " : "  ";
        var style = isCursor ? "bold" : "default";
        return $"{marker}{theme.FormatTypeBadge(item.Type)} [{style}]#{item.Id} {Markup.Escape(item.Title)}[/] {theme.FormatState(item.State)}";
    }

    private static void AddSiblingsToTree(IHasTreeNodes container, TreeNavigatorState state, SpectreTheme theme)
    {
        for (var i = 0; i < state.VisibleSiblings.Count; i++)
        {
            var sibling = state.VisibleSiblings[i];
            var label = FormatSiblingLabel(sibling, i, state, theme);
            var node = container.AddNode(label);
            if (state.CursorIndex == i)
                AddChildrenToNode(node, state, theme);
        }
    }

    private static void AddChildrenToNode(IHasTreeNodes parentNode, TreeNavigatorState state, SpectreTheme theme)
    {
        for (var i = 0; i < state.Children.Count; i++)
        {
            var child = state.Children[i];
            var effort = Formatters.FormatterHelpers.GetEffortDisplay(child);
            var effortSuffix = effort is not null ? $" [dim]{Markup.Escape(effort)}[/]" : "";
            var childLabel = $"  {theme.FormatTypeBadge(child.Type)} #{child.Id} {Markup.Escape(child.Title)} {theme.FormatState(child.State)}{effortSuffix}";
            parentNode.AddNode(childLabel);
        }
    }

    /// <summary>
    /// Builds the preview panel showing metadata for the currently selected work item.
    /// Shows type, state, assigned, iteration, effort, and links.
    /// When <paramref name="linkJumpIndex"/> is &gt;= 0, the link at that position in the
    /// combined (links + seedLinks) list is highlighted with <c>[aqua]</c>.
    /// </summary>
    internal static Panel BuildPreviewPanel(
        WorkItem? item,
        IReadOnlyList<WorkItemLink> links,
        IReadOnlyList<SeedLink> seedLinks,
        SpectreTheme theme,
        int linkJumpIndex = -1)
    {
        if (item is null)
        {
            return new Panel(new Markup("[dim italic]No item selected[/]"))
                .Header("[bold]Preview[/]")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        // Truncate raw title before escaping to avoid cutting into escape sequences
        var rawTitle = item.Title;
        var displayTitle = rawTitle.Length > 56 ? rawTitle[..56] + "..." : rawTitle;
        var headerTitle = $"#{item.Id} {Markup.Escape(displayTitle)}";

        var rows = new List<IRenderable>();

        // Metadata grid
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Type[/]", $"{theme.FormatTypeBadge(item.Type)} {Markup.Escape(item.Type.ToString())}");
        grid.AddRow("[bold]State[/]", theme.FormatState(item.State));
        grid.AddRow("[bold]Assigned[/]", item.AssignedTo is not null
            ? Markup.Escape(item.AssignedTo)
            : "[dim]unassigned[/]");

        // Iteration — last segment only
        var iterationValue = item.IterationPath.Value;
        var lastBackslash = iterationValue.LastIndexOf('\\');
        var iterationDisplay = lastBackslash >= 0
            ? iterationValue[(lastBackslash + 1)..]
            : iterationValue;
        grid.AddRow("[bold]Iteration[/]", Markup.Escape(iterationDisplay));

        // Effort — only if present
        var effort = Formatters.FormatterHelpers.GetEffortDisplay(item);
        if (effort is not null)
            grid.AddRow("[bold]Effort[/]", Markup.Escape(effort));

        rows.Add(grid);

        // Links section
        if (links.Count > 0 || seedLinks.Count > 0)
        {
            rows.Add(new Rule().RuleStyle("dim"));
            rows.Add(new Markup("[bold]Links:[/]"));

            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (linkJumpIndex == i)
                    rows.Add(new Markup($"  [aqua]{Markup.Escape(link.LinkType)}: #{link.TargetId}[/]"));
                else
                    rows.Add(new Markup($"  {Markup.Escape(link.LinkType)}: #{link.TargetId}"));
            }

            for (var i = 0; i < seedLinks.Count; i++)
            {
                var sl = seedLinks[i];
                var combinedIndex = links.Count + i;
                if (linkJumpIndex == combinedIndex)
                    rows.Add(new Markup($"  [aqua]{Markup.Escape(sl.LinkType)}(seed): #{sl.TargetId}[/]"));
                else
                    rows.Add(new Markup($"  [dim]{Markup.Escape(sl.LinkType)}(seed)[/]: #{sl.TargetId}"));
            }
        }

        return new Panel(new Rows(rows))
            .Header($"[bold]{headerTitle}[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }
}
