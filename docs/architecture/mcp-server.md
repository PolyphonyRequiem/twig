# MCP Server (twig-mcp)

Architecture of the Model Context Protocol server that exposes twig's
work-item cache and ADO mutation operations to IDE agents.

---

## 1. Server Architecture

### Transport & SDK

twig-mcp uses the **ModelContextProtocol** NuGet SDK over **stdio** transport.
Stdout is reserved for MCP JSON-RPC messages; all logging goes to stderr.

```
Host.CreateApplicationBuilder(args)
    .AddMcpServer(o => o.ServerInfo = new() { Name = "twig-mcp", Version = … })
    .WithStdioServerTransport()
    .WithTools<ContextTools>()
    .WithTools<ReadTools>()
    .WithTools<MutationTools>()
```

### AOT compatibility

All service registrations use explicit `sp => new …()` factory lambdas — no
reflection-based discovery. Tools are registered with `WithTools<T>()` rather
than assembly scanning. This keeps the server compatible with
`PublishAot=true` and `TrimMode=full`.

### Startup sequence

1. **Workspace guard** — `WorkspaceGuard.CheckWorkspace()` runs before the
   host is built, failing fast if no `.twig/` directory or config file exists.
2. **Configuration load** — `TwigConfiguration.Load()` reads `.twig/config`.
3. **Infrastructure registration** — `TwigServiceRegistration.AddTwigInfrastructure()`
   wires SQLite stores, HTTP client, auth provider, and ADO services.
4. **Domain service registration** — orchestrators, resolvers, and the
   MCP-specific `McpPendingChangeFlusher` are registered in `Program.cs`.
5. **Host build & run** — the generic host enters its event loop, reading MCP
   requests from stdin and writing responses to stdout.

### Version resolution

The server version is extracted from `AssemblyInformationalVersionAttribute`
(set by MinVer at build time). Build metadata after `+` is stripped so clients
see a clean semver string.

---

## 2. Workspace Guard

`WorkspaceGuard.CheckWorkspace(cwd)` returns a `(bool IsValid, string? Error,
string? TwigDir)` tuple.

Validation steps:

1. Walk from `cwd` upward looking for a `.twig/` directory
   (`WorkspaceDiscovery.FindTwigDir`).
2. Verify `.twig/config` exists inside it.

If either check fails, the server writes a diagnostic to stderr and exits
before the host starts. This prevents confusing downstream errors from
`TwigConfiguration.Load()` silently returning defaults.

The guard is extracted into its own class for unit testability.

---

## 3. Tool Catalog

twig-mcp exposes **8 tools** across three tool classes:

| Tool | Class | Description |
|------|-------|-------------|
| `twig_set` | `ContextTools` | Set active work item by ID or title pattern |
| `twig_status` | `ContextTools` | Show active item status and pending changes |
| `twig_tree` | `ReadTools` | Render focused item's parent chain + children |
| `twig_workspace` | `ReadTools` | Sprint backlog, seeds, dirty count |
| `twig_state` | `MutationTools` | Change active item state |
| `twig_update` | `MutationTools` | Update a field and push to ADO |
| `twig_note` | `MutationTools` | Add a comment (falls back to local staging) |
| `twig_sync` | `MutationTools` | Flush pending changes, then refresh cache |

All tool classes use the `[McpServerToolType]` attribute and primary
constructors for dependency injection.

### Context tools

**`twig_set(idOrPattern: string)`**

Resolves by numeric ID (cache → ADO auto-fetch) or by title pattern match
in the local cache. On success:

- Stores active ID in `IContextStore`.
- Extends working set (parent chain, 2 levels of children, links) via
  `ContextChangeService.ExtendWorkingSetAsync()` — best-effort, failures
  never fail the tool.
- Writes prompt state for shell integration.

**`twig_status()`**

Delegates to `StatusOrchestrator.GetSnapshotAsync()` which gathers the
active item, pending changes, and seeds into a single snapshot. Returns
an error if no context is set.

### Read tools

**`twig_tree(depth?: int)`**

Builds a hierarchical view:

1. Resolve active item via `ActiveItemResolver`.
2. Walk parent chain upward to root.
3. Fetch children (capped by `depth` or `config.Display.TreeDepth`).
4. Compute sibling counts for every node in the tree.
5. Best-effort link sync via `SyncCoordinator.SyncLinksAsync()`.
6. Format via `WorkTree.Build()`.

**`twig_workspace(all?: bool)`**

Returns the sprint workspace projection:

- **Context item** (nullable — no error if absent).
- **Sprint items** — filtered to current user unless `all=true`.
- **Seeds** — locally-created items not yet published.
- Iteration resolved via `IIterationService.GetCurrentIterationAsync()`.

### Mutation tools

**`twig_state(stateName: string)`**

1. Look up process configuration for the item's type.
2. Resolve target state via `StateResolver.ResolveByName()` (supports
   partial, case-insensitive matching).
3. Evaluate transition legality (`StateTransitionService.Evaluate()`).
4. Fetch remote item, apply patch with `ConflictRetryHelper`.
5. Auto-push any staged notes via `AutoPushNotesHelper`.
6. Resync cache (best-effort).

**`twig_update(field: string, value: string, format?: string)`**

- If `format == "markdown"`, the value is converted to HTML via
  `MarkdownConverter.ToHtml()` before patching.
- Fetches the remote item, patches with conflict retry, auto-pushes
  staged notes, and resyncs the cache.
- On double conflict, returns an error suggesting `twig.sync`.

**`twig_note(text: string)`**

Attempts to push the comment to ADO immediately. On any failure (network,
auth, server error), the note is staged locally in `IPendingChangeStore`
for later flushing. The response includes `isPending: boolean` so the
caller knows whether the note reached ADO.

**`twig_sync()`**

Two-phase operation:

1. **Push** — `McpPendingChangeFlusher.FlushAllAsync()` processes every
   dirty item (see §5).
2. **Pull** — Resyncs the active item, its parent chain, and its children
   from ADO via `SyncCoordinator.SyncItemSetAsync()`.

---

## 4. Shared Domain Layer

twig-mcp reuses the same domain and infrastructure libraries as the CLI.
The MCP `Program.cs` composes services from two layers:

### Infrastructure layer (`TwigServiceRegistration.AddTwigInfrastructure`)

Registered once; shared with CLI:

| Service | Implementation |
|---------|----------------|
| `IWorkItemRepository` | `SqliteWorkItemRepository` |
| `IContextStore` | `SqliteContextStore` |
| `IPendingChangeStore` | `SqlitePendingChangeStore` |
| `IFieldDefinitionStore` | `SqliteFieldDefinitionStore` |
| `IWorkItemLinkRepository` | `SqliteWorkItemLinkRepository` |
| `IProcessTypeStore` | `SqliteProcessTypeStore` |
| `INavigationHistoryStore` | `SqliteNavigationHistoryStore` |
| `IUnitOfWork` | `SqliteUnitOfWork` |
| `IAuthenticationProvider` | `AzCliAuthProvider` or `PatAuthProvider` |
| `IAdoWorkItemService` | `AdoRestClient` |
| `IIterationService` | `AdoIterationService` |

All backed by the same SQLite database at `.twig/{org}/{project}/twig.db`.

### Domain orchestrators (registered in MCP Program.cs)

| Service | Purpose |
|---------|---------|
| `ActiveItemResolver` | Resolve active item: cache → ADO auto-fetch |
| `ProtectedCacheWriter` | Save items while protecting dirty ones |
| `SyncCoordinatorFactory` | Create read-only / read-write sync coordinators |
| `ContextChangeService` | Extend working set on context change |
| `WorkingSetService` | Compute sprint items, parent chain, children |
| `RefreshOrchestrator` | Full cache refresh |
| `StatusOrchestrator` | Gather complete status snapshot |
| `McpPendingChangeFlusher` | Flush pending changes (MCP-specific) |

### Services NOT registered in MCP

These CLI-only services are excluded per design decision DD-10:

- `BacklogOrderer`
- `SeedPublishOrchestrator`
- `SeedReconcileOrchestrator`
- `FlowTransitionService`

---

## 5. Pending Change Flushing

### McpPendingChangeFlusher

`Twig.Mcp/Services/McpPendingChangeFlusher.cs` — the MCP-specific variant
of the CLI's pending change flusher. Key differences from the CLI version:

- **No user input** — headless; auto-accepts remote on conflict.
- **No output formatter** — tools format their own responses.
- **Continue on failure** — one item's failure doesn't block others.

### FlushAllAsync algorithm

```
for each dirty item ID:
  1. Load item from cache (skip if not found)
  2. Load pending changes (skip if empty)
  3. Separate field changes from notes
  4. If field changes exist:
     - Fetch remote item
     - Patch via ConflictRetryHelper (one automatic retry on 412)
  5. Push each note as an ADO comment
  6. Clear pending state for the item
  7. Resync item from ADO into cache
  on failure → record { workItemId, reason } and continue
```

Returns `McpFlushSummary { Flushed, Failed, Failures[] }`.

Notes are always pushed *after* field changes because they are additive
(ADO comments) and cannot conflict — they skip `ConflictRetryHelper`
entirely.

### AutoPushNotesHelper

A lighter-weight helper used by `twig.state` and `twig.update`. After a
successful field patch, it pushes any staged notes for the same item and
clears the "note" entries from the pending change store. This piggybacks
on the known-good network connection established by the field patch.

### McpResultBuilder

`Twig.Mcp/Services/McpResultBuilder.cs` — static helper that formats
domain objects into JSON `CallToolResult` responses. Uses `Utf8JsonWriter`
directly (no reflection, AOT-safe).

Key formatters:

| Method | Used by |
|--------|---------|
| `FormatWorkItemWithWorkingSet` | `twig_set` |
| `FormatStatus` | `twig_status` |
| `FormatTree` | `twig_tree` |
| `FormatWorkspace` | `twig_workspace` |
| `FormatStateChange` | `twig_state` |
| `FormatFieldUpdate` | `twig_update` |
| `FormatNoteAdded` | `twig_note` |
| `FormatFlushSummary` | `twig_sync` |

### Prompt state writer

`IPromptStateWriter.WritePromptStateAsync()` is called after every tool
invocation. It writes a small state file that shell integrations (e.g. a
custom prompt) can read to display the active work item context.

---

## 6. Concurrency & Threading Model

twig-mcp runs as a single-process event loop. The MCP SDK reads requests
from stdin sequentially, dispatches tool calls, and writes responses to
stdout. There is no request parallelism — this matches the single-writer
design of the underlying SQLite database (WAL mode, but only one
connection).

Domain services like `ActiveItemResolver` and field definition caches use
lazy-initialised tasks. These are explicitly **not thread-safe** — this is
acceptable because the MCP event loop serialises all tool invocations.
