namespace Twig.Mcp.Services;

/// <summary>
/// Resolves a <see cref="WorkspaceContext"/> for a tool call based on available signals.
/// Resolution order: explicit param → single-workspace default → active workspace → error.
/// Also handles cross-workspace probing for <c>twig_set</c> by numeric ID.
/// </summary>
public sealed class WorkspaceResolver(
    IWorkspaceRegistry registry,
    IWorkspaceContextFactory factory)
{
    private volatile WorkspaceKey? _activeWorkspace;

    /// <summary>
    /// Gets or sets the active workspace. Set by <c>twig_set</c> on successful resolution.
    /// Thread-safe via <see langword="volatile"/> read/write.
    /// </summary>
    public WorkspaceKey? ActiveWorkspace
    {
        get => _activeWorkspace;
        set => _activeWorkspace = value;
    }

    /// <summary>
    /// Resolves a <see cref="WorkspaceContext"/> for a standard tool call (not <c>twig_set</c>).
    /// </summary>
    /// <param name="workspace">Optional explicit workspace string (<c>"org/project"</c>).</param>
    /// <returns>The resolved <see cref="WorkspaceContext"/>.</returns>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="workspace"/> is provided but malformed.
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when the parsed workspace is not registered.
    /// </exception>
    /// <exception cref="AmbiguousWorkspaceException">
    /// Thrown when no workspace can be inferred and multiple are registered.
    /// </exception>
    public WorkspaceContext Resolve(string? workspace = null)
    {
        // 1. Explicit workspace parameter
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var key = WorkspaceKey.Parse(workspace);
            return factory.GetOrCreate(key);
        }

        // 2. Single-workspace default (backward compat)
        if (registry.IsSingleWorkspace)
        {
            return factory.GetOrCreate(registry.Workspaces[0]);
        }

        // 3. Active workspace (set by last twig_set)
        var active = _activeWorkspace;
        if (active is not null)
        {
            return factory.GetOrCreate(active);
        }

        // 4. Ambiguous — no way to infer
        throw new AmbiguousWorkspaceException(registry.Workspaces);
    }

    /// <summary>
    /// Resolves a workspace for <c>twig_set</c> by probing all registered workspaces
    /// for the given numeric work item ID. Sets the active workspace on success.
    /// </summary>
    /// <param name="id">The numeric work item ID to search for.</param>
    /// <param name="workspace">Optional explicit workspace string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved <see cref="WorkspaceContext"/>.</returns>
    public async Task<WorkspaceContext> ResolveForSetAsync(int id, string? workspace = null, CancellationToken ct = default)
    {
        // 1. Explicit workspace parameter — skip probing
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var key = WorkspaceKey.Parse(workspace);
            var ctx = factory.GetOrCreate(key);
            _activeWorkspace = key;
            return ctx;
        }

        // 2. Single-workspace — no probing needed
        if (registry.IsSingleWorkspace)
        {
            var key = registry.Workspaces[0];
            _activeWorkspace = key;
            return factory.GetOrCreate(key);
        }

        // 3. Cross-workspace probe: search caches first, then ADO
        var cacheHits = await ProbeCachesAsync(id, ct);

        if (cacheHits.Count == 1)
        {
            var key = cacheHits[0];
            _activeWorkspace = key;
            return factory.GetOrCreate(key);
        }

        if (cacheHits.Count > 1)
        {
            throw new AmbiguousWorkspaceException(id, cacheHits);
        }

        // No cache hits — try ADO fetch per workspace
        var adoHits = await ProbeAdoAsync(id, ct);

        if (adoHits.Count == 1)
        {
            var key = adoHits[0];
            _activeWorkspace = key;
            return factory.GetOrCreate(key);
        }

        if (adoHits.Count > 1)
        {
            throw new AmbiguousWorkspaceException(id, adoHits);
        }

        // Not found in any workspace
        throw new WorkItemNotFoundException(id, registry.Workspaces);
    }

    private async Task<List<WorkspaceKey>> ProbeCachesAsync(int id, CancellationToken ct)
    {
        var hits = new List<WorkspaceKey>();

        foreach (var key in registry.Workspaces)
        {
            var ctx = factory.GetOrCreate(key);
            var item = await ctx.WorkItemRepo.GetByIdAsync(id, ct);
            if (item is not null)
            {
                hits.Add(key);
            }
        }

        return hits;
    }

    private async Task<List<WorkspaceKey>> ProbeAdoAsync(int id, CancellationToken ct)
    {
        var hits = new List<WorkspaceKey>();

        foreach (var key in registry.Workspaces)
        {
            var ctx = factory.GetOrCreate(key);
            try
            {
                await ctx.AdoService.FetchAsync(id, ct);
                hits.Add(key);
            }
            catch (Exception)
            {
                // Work item not found in this workspace's ADO project — continue probing
            }
        }

        return hits;
    }
}

/// <summary>
/// Thrown when workspace resolution is ambiguous — multiple workspaces are registered
/// and no explicit selection, active workspace, or single-workspace default is available.
/// </summary>
public sealed class AmbiguousWorkspaceException : InvalidOperationException
{
    public IReadOnlyList<WorkspaceKey> AvailableWorkspaces { get; }
    public int? WorkItemId { get; }

    public AmbiguousWorkspaceException(IReadOnlyList<WorkspaceKey> availableWorkspaces)
        : base($"Multiple workspaces are registered and no workspace was specified. " +
               $"Available workspaces: {string.Join(", ", availableWorkspaces)}. " +
               $"Specify the 'workspace' parameter (format: \"org/project\").")
    {
        AvailableWorkspaces = availableWorkspaces;
    }

    public AmbiguousWorkspaceException(int workItemId, IReadOnlyList<WorkspaceKey> matchedWorkspaces)
        : base($"Work item #{workItemId} was found in multiple workspaces: " +
               $"{string.Join(", ", matchedWorkspaces)}. " +
               $"Specify the 'workspace' parameter to disambiguate.")
    {
        WorkItemId = workItemId;
        AvailableWorkspaces = matchedWorkspaces;
    }
}

/// <summary>
/// Thrown when a work item ID is not found in any registered workspace
/// during cross-workspace probing.
/// </summary>
public sealed class WorkItemNotFoundException : KeyNotFoundException
{
    public int WorkItemId { get; }
    public IReadOnlyList<WorkspaceKey> SearchedWorkspaces { get; }

    public WorkItemNotFoundException(int workItemId, IReadOnlyList<WorkspaceKey> searchedWorkspaces)
        : base($"Work item #{workItemId} was not found in any registered workspace. " +
               $"Searched: {string.Join(", ", searchedWorkspaces)}.")
    {
        WorkItemId = workItemId;
        SearchedWorkspaces = searchedWorkspaces;
    }
}
