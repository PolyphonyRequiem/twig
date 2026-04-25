using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Tui.Views;

/// <summary>
/// Terminal.Gui TreeView-based navigator for the work item hierarchy.
/// Supports Vim keybindings (j/k/Enter/q) and lazy child loading.
/// </summary>
internal sealed class TreeNavigatorView : View
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IContextStore _contextStore;
    private readonly string _iconMode;
    private readonly Dictionary<string, string>? _typeIconIds;
    internal readonly TreeView<WorkItemNode> _treeView;

    /// <summary>Raised when the user selects a work item in the tree.</summary>
    public event Action<WorkItem>? WorkItemSelected;

    public TreeNavigatorView(IWorkItemRepository workItemRepo, IContextStore contextStore,
        IProcessConfigurationProvider? processConfigProvider = null,
        string iconMode = "unicode", Dictionary<string, string>? typeIconIds = null)
    {
        _workItemRepo = workItemRepo;
        _contextStore = contextStore;
        _iconMode = iconMode;
        _typeIconIds = typeIconIds;

        CanFocus = true;

        _treeView = new TreeView<WorkItemNode>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TreeBuilder = new WorkItemTreeBuilder(workItemRepo, processConfigProvider, iconMode, typeIconIds),
        };

        _treeView.SelectionChanged += OnSelectionChanged;

        // Vim keybindings: j=down, k=up, Enter=expand/select, q=quit
        _treeView.KeyDown += OnKeyDown;

        Add(_treeView);

        // When this container gets focus, forward it to the inner TreeView
        HasFocusChanged += (_, _) => { if (HasFocus) _treeView.SetFocus(); };
    }

    /// <summary>
    /// Loads the tree root from the active work item context.
    /// </summary>
    public async Task LoadRootAsync()
    {
        var activeId = await _contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null) return;

        var item = await _workItemRepo.GetByIdAsync(activeId.Value);
        if (item is null) return;

        // Walk up to find the topmost ancestor in cache
        var root = item;
        if (item.ParentId.HasValue)
        {
            var chain = await _workItemRepo.GetParentChainAsync(item.ParentId.Value);
            if (chain.Count > 0)
                root = chain[0];
        }

        var rootNode = new WorkItemNode(root, isActive: root.Id == activeId.Value,
            badge: IconSet.ResolveTypeBadge(_iconMode, root.Type.Value, _typeIconIds));
        _treeView.AddObject(rootNode);
        _treeView.Expand(rootNode);
        _treeView.SelectedObject = rootNode;

        // If the active item is deeper in the tree, expand down to it
        if (root.Id != activeId.Value)
        {
            await ExpandToActiveAsync(rootNode, activeId.Value);
        }
    }

    /// <summary>
    /// Clears and reloads the tree from the active work item context.
    /// </summary>
    public async Task ReloadAsync()
    {
        _treeView.ClearObjects();
        await LoadRootAsync();
    }

    internal async Task<bool> ExpandToActiveAsync(WorkItemNode current, int targetId)
    {
        // Expand the current node so the TreeBuilder populates its children
        _treeView.Expand(current);

        // Retrieve the TreeView's actual child nodes (created by WorkItemTreeBuilder)
        var treeChildren = _treeView.GetChildren(current).ToList();

        foreach (var childNode in treeChildren)
        {
            if (childNode.WorkItem.Id == targetId)
            {
                _treeView.SelectedObject = childNode;
                return true;
            }

            // Check if the target might be a descendant of this child
            var grandchildren = await _workItemRepo.GetChildrenAsync(childNode.WorkItem.Id);
            if (grandchildren.Count > 0 && await ExpandToActiveAsync(childNode, targetId))
                return true;
        }

        return false;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs<WorkItemNode> e)
    {
        if (e.NewValue is not null)
        {
            WorkItemSelected?.Invoke(e.NewValue.WorkItem);
        }
    }

    internal void OnKeyDown(object? sender, Terminal.Gui.Input.Key e)
    {
        // Terminal.Gui v2: use NoAlt/NoShift/NoCtrl to get the base key
        var baseKey = e.KeyCode & ~KeyCode.ShiftMask & ~KeyCode.AltMask & ~KeyCode.CtrlMask;

        switch (baseKey)
        {
            case KeyCode.J:
                // Vim: j = move down
                _treeView.AdjustSelection(1);
                e.Handled = true;
                break;

            case KeyCode.K:
                // Vim: k = move up
                _treeView.AdjustSelection(-1);
                e.Handled = true;
                break;

            case KeyCode.Enter:
                // Expand/collapse or select
                if (_treeView.SelectedObject is not null)
                {
                    if (_treeView.IsExpanded(_treeView.SelectedObject))
                        _treeView.Collapse(_treeView.SelectedObject);
                    else
                        _treeView.Expand(_treeView.SelectedObject);
                }
                e.Handled = true;
                break;

            case KeyCode.Q:
                // Vim: q = quit
                App?.RequestStop();
                e.Handled = true;
                break;
        }
    }
}

/// <summary>
/// Tree node wrapping a <see cref="WorkItem"/> for display in TreeView.
/// </summary>
internal sealed class WorkItemNode
{
    public WorkItem WorkItem { get; }
    public bool IsActive { get; }
    private readonly string _badge;

    public WorkItemNode(WorkItem workItem, bool isActive = false, string? badge = null)
    {
        WorkItem = workItem;
        IsActive = isActive;
        _badge = badge ?? (workItem.Type.Value.Length > 0
            ? workItem.Type.Value[0].ToString().ToUpperInvariant()
            : "■");
    }

    public override string ToString()
    {
        var marker = IsActive ? "► " : "  ";
        var result = $"{marker}{_badge} #{WorkItem.Id} [{WorkItem.Type}] {WorkItem.Title} ({WorkItem.State})";

        if (!string.IsNullOrEmpty(WorkItem.AssignedTo))
            result += $" → {WorkItem.AssignedTo}";

        if (WorkItem.IsDirty)
            result += " •";

        return result;
    }
}

/// <summary>
/// Lazy tree builder that loads children from <see cref="IWorkItemRepository"/> on demand.
/// Uses <see cref="IProcessConfigurationProvider"/> to determine which types are leaf nodes.
/// </summary>
internal sealed class WorkItemTreeBuilder : ITreeBuilder<WorkItemNode>
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IProcessConfigurationProvider? _processConfigProvider;
    private readonly string _iconMode;
    private readonly Dictionary<string, string>? _typeIconIds;

    public WorkItemTreeBuilder(IWorkItemRepository workItemRepo,
        IProcessConfigurationProvider? processConfigProvider = null,
        string iconMode = "unicode", Dictionary<string, string>? typeIconIds = null)
    {
        _workItemRepo = workItemRepo;
        _processConfigProvider = processConfigProvider;
        _iconMode = iconMode;
        _typeIconIds = typeIconIds;

        // Pre-warm the config cache to avoid sync-over-async deadlocks on the UI thread.
        // CanExpand is called synchronously by TreeView; if the provider's underlying
        // store hasn't been cached yet, the sync .GetResult() would deadlock on
        // Terminal.Gui's SynchronizationContext.
        // Suppress transient/expected exceptions to prevent unobserved task exception
        // noise on the GC finalizer thread. OutOfMemoryException is intentionally NOT
        // caught — if the process is out of memory, a clean crash is preferable to
        // silent corruption. The difference between catch (Exception) and bare catch {}
        // is about non-CLS-compliant native exceptions, not SystemException subclasses.
        if (processConfigProvider is not null)
            Task.Run(() => { try { processConfigProvider.GetConfiguration(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } });
    }

    public bool SupportsCanExpand => true;

    public bool CanExpand(WorkItemNode model)
    {
        // Use process configuration to determine leaf types when available
        if (_processConfigProvider is not null)
        {
            try
            {
                var config = _processConfigProvider.GetConfiguration();
                if (config.TypeConfigs.TryGetValue(model.WorkItem.Type, out var typeConfig))
                {
                    return typeConfig.AllowedChildTypes.Count > 0;
                }
            }
            catch (Exception)
            {
                // Fall through to hardcoded default if config unavailable.
                // Unlike the pre-warm task, this catch is on the UI thread path where
                // we have a meaningful fallback; non-CLS-compliant exceptions (from
                // native code) are not caught by catch (Exception) and will propagate.
            }
        }

        // Fallback: data preservation over cosmetics — unknown types are assumed expandable
        return true;
    }

    public IEnumerable<WorkItemNode> GetChildren(WorkItemNode model)
    {
        // Use Task.Run to avoid deadlocks with Terminal.Gui's SynchronizationContext
        var children = Task.Run(() => _workItemRepo.GetChildrenAsync(model.WorkItem.Id))
            .GetAwaiter().GetResult();

        return children.Select(c => new WorkItemNode(c,
            badge: IconSet.ResolveTypeBadge(_iconMode, c.Type.Value, _typeIconIds)));
    }
}
