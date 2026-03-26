using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;

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
        IReadOnlyList<Domain.ValueObjects.ColumnSpec>? dynamicColumns = null)
    {
        var table = SpectreTheme.CreateWorkspaceTable(isTeamView, dynamicColumns);
        string? savedCaption = null;
        var loadingCleared = false;
        int? activeContextId = null;
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
                                : "[dim]No active context[/]";
                            table.Caption(new TableTitle(savedCaption));
                            ctx.Refresh();
                            break;

                        case WorkspaceDataChunk.SprintItemsLoaded(var items):
                            // Group items by state category
                            var categoryGroups = GroupByStateCategory(items);
                            var catIndex = 0;
                            foreach (var (category, catItems) in categoryGroups)
                            {
                                // Insert separator between category groups (not before the first)
                                if (catIndex > 0)
                                {
                                    var sepRow = new string[colCount];
                                    for (var si = 0; si < colCount; si++) sepRow[si] = "[dim]────[/]";
                                    table.AddRow(sepRow);
                                }

                                // Add category header row
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

                                    var row = new List<string>
                                    {
                                        $"{marker}{boldOpen}{item.Id}{boldClose}",
                                        _theme.FormatTypeBadge(item.Type),
                                        $"{boldOpen}{Markup.Escape(item.Title)}{boldClose}",
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

                                foreach (var seed in seeds)
                                {
                                    var staleMarker = seed.SeedCreatedAt.HasValue
                                        && seed.SeedCreatedAt.Value < DateTimeOffset.UtcNow.AddDays(-staleDays)
                                        ? " [yellow]⚠ stale[/]" : "";

                                    var seedRow = new List<string>
                                    {
                                        seed.Id < 0 ? $"[dim]{seed.Id}[/]" : seed.Id.ToString(),
                                        _theme.FormatTypeBadge(seed.Type),
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
        IRenderable treeRenderable = tree;
        if (TypeLevelMap is not null && ParentChildMap is not null
            && parentChain.Count == 0
            && !focusedItem.ParentId.HasValue
            && TypeLevelMap.TryGetValue(focusedItem.Type.Value, out var focusLevel)
            && focusLevel > 0)
        {
            var expectedParent = Formatters.HumanOutputFormatter.FindExpectedParentTypeName(
                focusedItem.Type.Value, ParentChildMap);
            var parentLabel = expectedParent ?? "a parent";
            treeRenderable = new Rows(
                new Markup($"[dim](unparented — expected under a {Markup.Escape(parentLabel)})[/]"),
                tree);
        }

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
                    var dirty = child.IsDirty ? " [yellow]•[/]" : "";
                    var effort = Formatters.FormatterHelpers.GetEffortDisplay(child);
                    var effortSuffix = effort is not null ? $" [dim]{Markup.Escape(effort)}[/]" : "";
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
        var dirty = item.IsDirty ? " [yellow]•[/]" : "";
        return $"{marker}{_theme.FormatTypeBadge(item.Type)} [bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty} {_theme.FormatState(item.State)}";
    }

    public async Task RenderStatusAsync(
        Func<Task<WorkItem?>> getItem,
        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
        CancellationToken ct,
        IReadOnlyList<Domain.ValueObjects.FieldDefinition>? fieldDefinitions = null,
        IReadOnlyList<Domain.ValueObjects.StatusFieldEntry>? statusFieldEntries = null)
    {
        var item = await getItem();
        if (item is null)
            return;

        var pending = await getPendingChanges();

        // EPIC-002: One-line summary header for quick-glance
        var summaryBadge = _theme.FormatTypeBadge(item.Type);
        var summaryState = _theme.FormatState(item.State);
        _console.MarkupLine($"#{item.Id} [aqua]●[/] {summaryBadge} {Markup.Escape(item.Type.ToString())} — {Markup.Escape(item.Title)} {summaryState}");

        // Work item detail panel
        var dirty = item.IsDirty ? " [yellow]•[/]" : "";
        var itemGrid = new Grid().AddColumn().AddColumn();
        itemGrid.AddRow("[dim]Type:[/]", _theme.FormatTypeBadge(item.Type) + " " + Markup.Escape(item.Type.ToString()));
        itemGrid.AddRow("[dim]State:[/]", _theme.FormatState(item.State));
        itemGrid.AddRow("[dim]Assigned:[/]", Markup.Escape(item.AssignedTo ?? "(unassigned)"));
        itemGrid.AddRow("[dim]Area:[/]", Markup.Escape(item.AreaPath.ToString()));
        itemGrid.AddRow("[dim]Iteration:[/]", Markup.Escape(item.IterationPath.ToString()));

        // Extended fields from the Fields dictionary
        AddExtendedFieldRows(itemGrid, item, fieldDefinitions, statusFieldEntries);

        var itemPanel = new Panel(itemGrid)
            .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
            .Border(BoxBorder.Rounded)
            .Expand();

        _console.Write(itemPanel);

        // Pending changes panel (only if there are pending changes)
        if (pending.Count > 0)
        {
            var noteCount = 0;
            var fieldCount = 0;
            foreach (var change in pending)
            {
                if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                    noteCount++;
                else
                    fieldCount++;
            }

            var changesGrid = new Grid().AddColumn().AddColumn();
            changesGrid.AddRow("[dim]Field changes:[/]", fieldCount.ToString());
            changesGrid.AddRow("[dim]Notes:[/]", noteCount.ToString());

            var changesPanel = new Panel(changesGrid)
                .Header("[bold]Pending Changes[/]")
                .Border(BoxBorder.Rounded)
                .Expand();

            _console.Write(changesPanel);
        }
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
        var dirty = showDirty && item.IsDirty ? " [yellow]•[/]" : "";
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[dim]Type:[/]", _theme.FormatTypeBadge(item.Type) + " " + Markup.Escape(item.Type.ToString()));
        grid.AddRow("[dim]State:[/]", _theme.FormatState(item.State));
        grid.AddRow("[dim]Assigned:[/]", Markup.Escape(item.AssignedTo ?? "(unassigned)"));
        grid.AddRow("[dim]Area:[/]", Markup.Escape(item.AreaPath.ToString()));
        grid.AddRow("[dim]Iteration:[/]", Markup.Escape(item.IterationPath.ToString()));

        // Stage 2: Progressively add extended fields from the Fields dictionary
        await _console.Live(new Markup("[dim]Loading...[/]"))
            .StartAsync(async ctx =>
            {
                Panel BuildPanel() => new Panel(grid)
                    .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
                    .Border(BoxBorder.Rounded)
                    .Expand();

                ctx.UpdateTarget(BuildPanel());
                ctx.Refresh();

                // Yield to allow the core panel to render before loading extended fields
                await Task.Yield();
                ct.ThrowIfCancellationRequested();

                // Extended field: Description
                if (item.Fields.TryGetValue("System.Description", out var description)
                    && !string.IsNullOrWhiteSpace(description))
                {
                    grid.AddRow("[dim]Description:[/]", Markup.Escape(TruncateField(description, 200)));
                    ctx.UpdateTarget(BuildPanel());
                    ctx.Refresh();
                }

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
            });
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

        var defLookup = new Dictionary<string, Domain.ValueObjects.FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        if (fieldDefinitions is not null)
        {
            foreach (var def in fieldDefinitions)
                defLookup[def.ReferenceName] = def;
        }

        if (statusFieldEntries is not null)
        {
            foreach (var entry in statusFieldEntries)
            {
                if (!entry.IsIncluded)
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
                    case SyncResult.UpToDate:
                        ctx.UpdateTarget(new Rows(cachedView, new Markup("[dim]✓ up to date[/]")));
                        ctx.Refresh();
                        await Task.Delay(SyncStatusDelay, ct);
                        ctx.UpdateTarget(cachedView);
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
                        ctx.UpdateTarget(displayView);
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
                        ctx.UpdateTarget(partialDisplayView);
                        ctx.Refresh();
                        break;

                    case SyncResult.Failed failed:
                        var reason = string.IsNullOrWhiteSpace(failed.Reason) ? "offline" : Markup.Escape(failed.Reason);
                        ctx.UpdateTarget(new Rows(cachedView, new Markup($"[yellow]⚠ sync failed ({reason})[/]")));
                        ctx.Refresh();
                        // Failed status persists — no delay/clear
                        break;

                    case SyncResult.Skipped:
                        ctx.UpdateTarget(new Rows(cachedView, new Markup("[dim]✓ up to date[/]")));
                        ctx.Refresh();
                        await Task.Delay(SyncStatusDelay, ct);
                        ctx.UpdateTarget(cachedView);
                        ctx.Refresh();
                        break;

                    default:
                        throw new System.Diagnostics.UnreachableException($"Unhandled SyncResult: {result.GetType().Name}");
                }
            });
    }

    public void RenderHints(IReadOnlyList<string> hints)
    {
        if (hints.Count == 0)
            return;

        foreach (var hint in hints)
        {
            _console.MarkupLine($"[dim]  hint: {Markup.Escape(hint)}[/]");
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
            _console.MarkupLine("[dim]  No seeds[/]");
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
}
