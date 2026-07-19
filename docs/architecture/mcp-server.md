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
    .WithRequestFilters(/* catalog visibility + schema normalization */)
    .WithTools<ContextTools>()
    .WithTools<ReadTools>()
    .WithTools<MutationTools>()
    // …all tool families remain callable; tools/list is profile-filtered
```

### AOT compatibility

Tools are registered with the SDK's generic `WithTools<T>()` API rather than
assembly scanning. Catalog normalization uses `JsonDocument`/`JsonNode` and
explicit protocol models; it does not introduce reflection-based serialization.
This keeps the server compatible with `PublishAot=true` and `TrimMode=full`.

### Startup sequence

1. **Workspace discovery** — `WorkspaceRegistry.DiscoverFromCwd()` walks up
   from the launch directory and registers a repo-local split configuration
   when one exists. Starting with zero workspaces is valid.
2. **Authentication selection** — registered workspace configurations choose
   PAT or the MSAL-cache-first ADO token provider.
3. **Tool profile resolution** — `--tool-profile` overrides
   `TWIG_MCP_TOOL_PROFILE`; the default is `compact`.
4. **Service registration** — the registry, workspace context factory,
   resolver, tool families, and batch dispatcher are registered as singletons.
5. **Host build & run** — the generic host reads MCP requests from stdin and
   writes protocol responses to stdout.

### Version resolution

The server version is extracted from `AssemblyInformationalVersionAttribute`
(set by MinVer at build time). Build metadata after `+` is stripped so clients
see a clean semver string.

---

## 2. Workspace Resolution

The server can start without a workspace so clients can initialize and inspect
the catalog from any directory. `WorkspaceRegistry` discovers the nearest
repo-local `.twig/config`, loads the tracked split configuration, and registers
that workspace when present.

Every workspace-aware tool accepts an internal optional `workspace` override in
`org/project` form. The normal path is to omit it:

1. use the repo-local workspace when it is registered;
2. otherwise use the only registered workspace;
3. otherwise require an explicit override.

An explicit override remains authoritative. If it is unknown but omission would
resolve unambiguously, the error suggests retrying without `workspace`; it never
silently targets a different workspace.

---

## 3. Tool Catalog

Forty tool methods remain registered and callable. `McpToolCatalog` owns the
canonical names, visibility profiles, batch eligibility, safety annotations,
and wire-schema normalization.

### Visibility profiles

The default **compact** profile advertises ten high-frequency tools:

| Tool | Purpose |
|------|---------|
| `twig_set` | Set active context by ID or title pattern |
| `twig_show` | Read a specific work item |
| `twig_query` | Search with structured filters |
| `twig_workspace` | Read sprint/context workspace |
| `twig_cache_status` | Poll local cache health |
| `twig_find_or_create` | Deduplicating create path |
| `twig_state` | Change workflow state |
| `twig_update` | Update one field |
| `twig_note` | Add or stage a comment |
| `twig_sync` | Flush pending changes and refresh context |

Use `--tool-profile full` or `TWIG_MCP_TOOL_PROFILE=full` to advertise all
forty tools. `core` aliases `compact`; `all` aliases `full`. Profile filtering
affects discovery only: hidden tools remain callable by name so cached clients
and explicit integrations continue to work.

The compact profile intentionally hides specialized seed, tracking, process,
admin, batch, destructive, and compatibility-alias tools. The full catalog is
grouped across `ContextTools`, `ReadTools`, `MutationTools`, `NavigationTools`,
`CreationTools`, `WorkspaceTools`, `ProcessTools`, `AdminTools`, `TrackingTools`,
`BatchTools`, and `SeedTools`.

### Catalog normalization

The `tools/list` filter:

- removes the universal `verbose` property while legacy callers may still pass it;
- omits `workspace` when exactly one repo-local workspace makes it redundant;
- models optional parameters by omission rather than repeated `null` defaults;
- adds `additionalProperties: false`;
- publishes typed objects/arrays for batch graphs, patch fields, and tracking IDs;
- adds reviewed read-only, destructive, idempotent, and open-world annotations;
- preserves SDK task-support metadata while cloning each discovery record.

A matching `tools/call` filter adapts the typed wire arguments to the legacy
method parameters, preserving direct and cached callers without duplicating
tool implementations.

Successful and structured error envelopes are returned both as text content and
as MCP `structuredContent`. Twig intentionally does not publish forty separate
output schemas; every tool shares the documented response envelope.

### Batch contract

`twig_batch` accepts sequence, parallel, and step nodes with a maximum of fifty
operations and three nesting levels. Every registered non-batch tool is listed
in `McpToolCatalog.BatchableToolNames` and has an AOT-safe dispatcher case;
recursive batch calls are rejected. A catalog regression test fails if tool
registration and batch dispatch drift apart.

### Metadata budgets

Tests generate the real SDK protocol models and enforce both count and byte
budgets. The normal single-workspace compact profile is limited to ten tools /
8.5 KB; the fidelity-first full profile is limited to forty tools / 37 KB.
This measures serialized metadata rather than C# source text. Zero-workspace
startup retains workspace parameters for diagnosis, so its compact catalog is
slightly larger but remains below 10 KB.

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
| `IAuthenticationProvider` | `AdoAccessTokenProvider` or `PatAuthProvider` |
| `IAdoWorkItemService` | `AdoRestClient` |
| `IIterationService` | `AdoIterationService` |

All backed by the same SQLite database at `.twig/{org}/{project}/twig.db`.

### Domain orchestrators (registered in MCP Program.cs)

| Service | Purpose |
|---------|---------|
| `ActiveItemResolver` | Resolve active item: cache → ADO auto-fetch |
| `ProtectedCacheWriter` | Save items while protecting dirty ones |
| `SyncCoordinatorPair` | Named pair holding read-only / read-write sync coordinators |
| `ContextChangeService` | Extend working set on context change |
| `WorkingSetService` | Compute sprint items, parent chain, children |
| `RefreshOrchestrator` | Full cache refresh |
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

twig-mcp is a single stdio server process, but `twig_batch` parallel nodes may
invoke several tool workflows concurrently. Individual stores and workflows
therefore own their transaction boundaries; callers must not assume all MCP
work is globally serialized. Sequence nodes remain ordered and fail fast unless
the step opts into `onError: "continue"`.
