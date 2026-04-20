# MCP Multi-Workspace Support

> **Status**: 🔨 In Progress
> **Work Item**: [#1754](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1754) — twig-mcp: support multiple workspaces per process
> **Type**: Issue
> **Revision**: 0 (initial draft)

---

## Executive Summary

`twig-mcp` currently pins to a single `(org, project)` workspace at process startup: `WorkspaceGuard.CheckWorkspace()` runs once, loads one `.twig/config`, and registers a single `TwigConfiguration` / `SqliteCacheStore` / `IContextStore` / `IAdoWorkItemService` as singletons in the DI container. Every subsequent tool call is bound to that workspace. When a VS Code session spans multiple repositories targeting different ADO projects, the MCP process cannot serve requests for any workspace other than the one in its startup CWD — even when the target workspace's `.twig/{org}/{project}/twig.db` exists on disk.

This plan introduces a **workspace-scoped service resolution layer** that allows a single `twig-mcp` process to serve tool calls against any registered `(org, project)` workspace. Each tool call accepts an optional `workspace` parameter (format: `"org/project"`), resolves or infers the target workspace, and acquires a scoped set of services (DB connection, ADO client, context store, etc.) keyed to that workspace. A `WorkspaceRegistry` discovers workspaces from the on-disk `.twig/{org}/{project}/` layout, a `WorkspaceContextFactory` creates per-workspace service bundles on demand and caches them for the process lifetime, and a `WorkspaceContextAccessor` tracks the per-call active workspace. Backward compatibility is preserved: when exactly one workspace is configured, all tools behave identically to today with no new parameters required.

## Background

### Current Architecture

The MCP server's startup flow is strictly single-workspace:

1. `Program.cs` calls `WorkspaceGuard.CheckWorkspace(Directory.GetCurrentDirectory())` — walks up the directory tree to find `.twig/`, validates `.twig/config` exists.
2. Loads `TwigConfiguration` from `.twig/config` — extracts `Organization` and `Project`.
3. `TwigPaths.BuildPaths()` computes the DB path as `.twig/{org}/{project}/twig.db`.
4. `AddTwigCoreServices()` registers all persistence as singletons bound to that single DB.
5. `AddTwigNetworkServices()` creates an `AdoRestClient` bound to that single `(org, project)`.
6. Domain services (`ActiveItemResolver`, `SyncCoordinator`, `StatusOrchestrator`, etc.) are singletons that transitively depend on the single `IWorkItemRepository`, `IContextStore`, and `IAdoWorkItemService`.

The on-disk layout already anticipates multi-workspace: `.twig/{org}/{project}/twig.db` is segregated by `(org, project)`. But the runtime wiring is entirely singleton.

### Call-Site Audit — Workspace-Bound Singleton Services

Every service registered in `Program.cs` and `TwigServiceRegistration.cs` that transitively depends on `TwigConfiguration`, `TwigPaths`, `SqliteCacheStore`, or `IAdoWorkItemService` is workspace-bound and must be scoped per-workspace in the new design.

| Service | File | Current Registration | Workspace Dependency |
|---------|------|---------------------|---------------------|
| `TwigConfiguration` | `TwigServiceRegistration.cs:48-58` | Singleton | Root — org, project, auth, display settings |
| `TwigPaths` | `TwigServiceRegistration.cs:61-65` | Singleton | DB path derived from config org/project |
| `SqliteCacheStore` | `TwigServiceRegistration.cs:70-76` | Singleton | DB connection bound to `TwigPaths.DbPath` |
| `IWorkItemRepository` | `TwigServiceRegistration.cs:78` | Singleton | Via `SqliteCacheStore` |
| `IContextStore` | `TwigServiceRegistration.cs:79` | Singleton | Via `SqliteCacheStore` |
| `INavigationHistoryStore` | `TwigServiceRegistration.cs:80` | Singleton | Via `SqliteCacheStore` |
| `IPendingChangeStore` | `TwigServiceRegistration.cs:81` | Singleton | Via `SqliteCacheStore` |
| `IUnitOfWork` | `TwigServiceRegistration.cs:82` | Singleton | Via `SqliteCacheStore` |
| `IProcessTypeStore` | `TwigServiceRegistration.cs:85` | Singleton | Via `SqliteCacheStore` |
| `IProcessConfigurationProvider` | `TwigServiceRegistration.cs:86` | Singleton | Via `IProcessTypeStore` |
| `IFieldDefinitionStore` | `TwigServiceRegistration.cs:87` | Singleton | Via `SqliteCacheStore` |
| `IWorkItemLinkRepository` | `TwigServiceRegistration.cs:88` | Singleton | Via `SqliteCacheStore` |
| `ISeedLinkRepository` | `TwigServiceRegistration.cs:89` | Singleton | Via `SqliteCacheStore` |
| `IPublishIdMapRepository` | `TwigServiceRegistration.cs:90` | Singleton | Via `SqliteCacheStore` |
| `IPromptStateWriter` | `TwigServiceRegistration.cs:111-116` | Singleton | Via `IContextStore`, `TwigConfiguration`, `TwigPaths` |
| `IAdoWorkItemService` | `NetworkServiceModule.cs:43-51` | Singleton | `AdoRestClient` bound to `(org, project)` |
| `IIterationService` | `NetworkServiceModule.cs:68-78` | Singleton | `AdoIterationService` bound to `(org, project, team)` |
| `IAuthenticationProvider` | `NetworkServiceModule.cs:30-36` | Singleton | Auth method from config |
| `ActiveItemResolver` | `Program.cs:42-44` | Singleton | Via `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService` |
| `ProtectedCacheWriter` | `Program.cs:47-49` | Singleton | Via `IWorkItemRepository`, `IPendingChangeStore` |
| `SyncCoordinatorFactory` | `Program.cs:53-64` | Singleton | Via `IWorkItemRepository`, `IAdoWorkItemService`, etc. |
| `SyncCoordinator` | `Program.cs:67` | Singleton | Via `SyncCoordinatorFactory` |
| `ContextChangeService` | `Program.cs:69-74` | Singleton | Via `IWorkItemRepository`, `IAdoWorkItemService`, `SyncCoordinator` |
| `WorkingSetService` | `Program.cs:76-81` | Singleton | Via `IContextStore`, `IWorkItemRepository`, `TwigConfiguration` |
| `RefreshOrchestrator` | `Program.cs:85-92` | Singleton | Via `IContextStore`, `IWorkItemRepository`, `IAdoWorkItemService` |
| `StatusOrchestrator` | `Program.cs:94-100` | Singleton | Via `IContextStore`, `IWorkItemRepository`, etc. |
| `McpPendingChangeFlusher` | `Program.cs:103-106` | Singleton | Via `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore` |

**Workspace-agnostic services** (safe as global singletons):
| Service | Reason |
|---------|--------|
| `HttpClient` | Connection pool; shared across all workspaces |
| `IAuthenticationProvider` | Auth method is per-user, not per-workspace (az CLI / PAT) |
| `IGitService` | Local git CLI operations |
| `IGlobalProfileStore` | Global `~/.twig/profiles/` |
| `ITelemetryClient` | No workspace data in telemetry |

### Tool Classes — DI Injection Audit

Each MCP tool class injects workspace-bound services via constructor DI:

| Tool Class | Injected Services | Impact |
|------------|------------------|--------|
| `ContextTools` | `IWorkItemRepository`, `IContextStore`, `ActiveItemResolver`, `StatusOrchestrator`, `IPromptStateWriter`, `ContextChangeService` | All workspace-bound |
| `ReadTools` | `IWorkItemRepository`, `IContextStore`, `IIterationService`, `ActiveItemResolver`, `SyncCoordinator`, `TwigConfiguration` | All workspace-bound |
| `MutationTools` | `ActiveItemResolver`, `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IPromptStateWriter`, `McpPendingChangeFlusher`, `SyncCoordinator` | All workspace-bound |

## Problem Statement

1. **Single-workspace lock-in.** The MCP process is bound to one `(org, project)` at startup. If an agent works across repositories targeting different ADO projects, every tool call after the first is served by the wrong workspace, producing "not found" errors for valid work item IDs.

2. **CWD-based discovery is fragile.** `WorkspaceGuard.CheckWorkspace(cwd)` walks up from the process CWD to find `.twig/`. The MCP process is typically launched once by VS Code with a fixed CWD — subsequent tool calls from agents operating in other directories have no way to influence workspace selection.

3. **All services are singleton.** Every workspace-bound service (`SqliteCacheStore`, `AdoRestClient`, `IContextStore`, etc.) is registered as a global singleton. There is no per-request scoping mechanism. The MCP tool DI model (constructor injection into `[McpServerToolType]` classes) does not naturally support request-scoped lifetimes.

## Goals and Non-Goals

### Goals

1. **Multi-workspace resolution.** A single `twig-mcp` process can serve tool calls against any `(org, project)` workspace that has a `.twig/{org}/{project}/twig.db` on disk.
2. **Per-tool-call workspace selection.** Each tool call can specify `workspace` (format: `"org/project"`) to target a specific workspace explicitly.
3. **Ambient workspace inference.** When no explicit `workspace` is provided: for `twig_set <id>`, probe all registered workspaces to find the item; for other tools, use the workspace associated with the current active context item.
4. **Backward compatibility.** When exactly one workspace exists, behavior is identical to today — no new parameters needed, no config migration.
5. **Workspace-scoped isolation.** Each workspace's DB connection, context store, pending changes, and ADO client are fully independent.
6. **Test coverage.** Unit tests cover multi-workspace resolution, cross-workspace `twig_set`, per-workspace independence, and single-workspace backward compat.

### Non-Goals

1. **Concurrent writes across workspaces.** Thread-safety of `ProtectedCacheWriter` / `SqliteCacheStore` for truly concurrent multi-workspace writes. SQLite WAL handles reader-writer concurrency within a single DB; cross-DB atomicity is not needed. Deferred to follow-up if the MCP SDK introduces concurrent tool dispatch.
2. **CLI multi-workspace UX.** The `twig` CLI remains single-workspace; this is a separate Issue.
3. **Workspace CRUD.** No `twig_add_workspace` or `twig_remove_workspace` tools. Workspaces are discovered from the existing on-disk `.twig/{org}/{project}/` layout, created by `twig init`.
4. **Cross-tenant ID resolution.** ADO work item IDs are globally unique within a tenant. Multi-tenant lookup (probing different ADO tenants) is out of scope.

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | Each MCP tool schema includes an optional `workspace` parameter (format `"org/project"`). |
| FR-2 | `WorkspaceRegistry` discovers available workspaces by scanning `.twig/{org}/{project}/config` paths. |
| FR-3 | `WorkspaceContextFactory` creates and caches per-workspace service bundles (`SqliteCacheStore`, `IWorkItemRepository`, `IContextStore`, `IPendingChangeStore`, `IAdoWorkItemService`, `IIterationService`, domain orchestrators). |
| FR-4 | `twig_set <id>` with no `workspace` probes all registered workspaces (cache lookup first, then ADO fetch) to find the work item. |
| FR-5 | `twig_status`, `twig_tree`, `twig_workspace`, `twig_state`, `twig_update`, `twig_note`, `twig_sync`, `twig_discard` resolve workspace from: (a) explicit `workspace` param, (b) the workspace where the current active context item resides, or (c) the single registered workspace (backward compat). |
| FR-6 | `twig_status` and `twig_workspace` responses include a `workspace` field identifying the active `"org/project"`. |
| FR-7 | `WorkspaceGuard` in "ambient mode" succeeds if any `.twig/{org}/{project}/config` exists, even when top-level `.twig/config` is absent. |
| FR-8 | A new `twig_list_workspaces` tool returns the list of registered workspaces and their status. |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | Workspace service bundles are lazily created on first access and cached for the process lifetime. |
| NFR-2 | No breaking changes to the MCP tool API — existing callers that omit `workspace` continue to work. |
| NFR-3 | AOT-compatible: no reflection, all JSON via `TwigJsonContext`, factory-based DI. |
| NFR-4 | DB connections are pooled per workspace (single `SqliteCacheStore` per `(org, project)`). |

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    MCP Tool Layer                        │
│  ContextTools / ReadTools / MutationTools                │
│  ── each tool call passes optional `workspace` param ── │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│               WorkspaceResolver                          │
│  Resolves (org, project) from:                          │
│  1. Explicit `workspace` param                          │
│  2. Active context item → workspace mapping             │
│  3. Single-workspace default                            │
│  4. Cross-workspace ID probe (twig_set only)            │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│            WorkspaceContextFactory                       │
│  Creates + caches WorkspaceContext per (org, project):  │
│  ┌─────────────────────────────────────────────┐        │
│  │ WorkspaceContext                             │        │
│  │  ├ TwigConfiguration                        │        │
│  │  ├ TwigPaths                                │        │
│  │  ├ SqliteCacheStore                         │        │
│  │  ├ IWorkItemRepository                      │        │
│  │  ├ IContextStore                            │        │
│  │  ├ IPendingChangeStore                      │        │
│  │  ├ IAdoWorkItemService                      │        │
│  │  ├ IIterationService                        │        │
│  │  ├ IProcessConfigurationProvider            │        │
│  │  ├ ActiveItemResolver                       │        │
│  │  ├ SyncCoordinatorFactory                   │        │
│  │  ├ ContextChangeService                     │        │
│  │  ├ StatusOrchestrator                       │        │
│  │  ├ McpPendingChangeFlusher                  │        │
│  │  └ IPromptStateWriter                       │        │
│  └─────────────────────────────────────────────┘        │
└──────────────────────┬──────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│              WorkspaceRegistry                           │
│  Discovers (org, project) pairs from disk:              │
│  .twig/{org}/{project}/config                           │
│  Caches the list; provides key-based lookup.            │
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkspaceKey` (value object)

A record type encapsulating `(string Org, string Project)` with proper equality, hash code, and a `"org/project"` string format for tool parameters and display.

**File:** `src/Twig.Mcp/Services/WorkspaceKey.cs`

```csharp
public sealed record WorkspaceKey(string Org, string Project)
{
    public override string ToString() => $"{Org}/{Project}";
    
    public static WorkspaceKey Parse(string value) { ... }
    public static bool TryParse(string? value, out WorkspaceKey? key) { ... }
}
```

#### 2. `WorkspaceRegistry`

Discovers available workspaces by scanning `.twig/{org}/{project}/config` on disk. Provides lookup and enumeration. Immutable after construction.

**File:** `src/Twig.Mcp/Services/WorkspaceRegistry.cs`

**Responsibilities:**
- On construction, scan the `.twig/` directory for `{org}/{project}/config` files
- Parse each config to extract `Organization` and `Project`
- Expose `IReadOnlyList<WorkspaceKey> Workspaces`
- Expose `TwigConfiguration GetConfig(WorkspaceKey key)`
- Expose `bool IsSingleWorkspace` for backward-compat fast-path

**Discovery algorithm:**
```
for each dir in .twig/*/
  for each subdir in dir/*/
    if File.Exists(subdir/config):
      load TwigConfiguration from subdir/config
      if config.Organization and config.Project are set:
        register WorkspaceKey(config.Organization, config.Project)
```

Also supports a fallback: if `.twig/config` exists at the top level (legacy layout), register that as a workspace too.

#### 3. `WorkspaceContext`

A bundle of per-workspace services. Created by `WorkspaceContextFactory` and cached for the process lifetime. Implements `IDisposable` (disposes the `SqliteCacheStore`).

**File:** `src/Twig.Mcp/Services/WorkspaceContext.cs`

```csharp
public sealed class WorkspaceContext : IDisposable
{
    public WorkspaceKey Key { get; }
    public TwigConfiguration Config { get; }
    public TwigPaths Paths { get; }
    public IWorkItemRepository WorkItemRepo { get; }
    public IContextStore ContextStore { get; }
    public IPendingChangeStore PendingChangeStore { get; }
    public IAdoWorkItemService AdoService { get; }
    public IIterationService IterationService { get; }
    public IProcessConfigurationProvider ProcessConfigProvider { get; }
    public ActiveItemResolver ActiveItemResolver { get; }
    public SyncCoordinatorFactory SyncCoordinatorFactory { get; }
    public ContextChangeService ContextChangeService { get; }
    public StatusOrchestrator StatusOrchestrator { get; }
    public WorkingSetService WorkingSetService { get; }
    public McpPendingChangeFlusher Flusher { get; }
    public IPromptStateWriter PromptStateWriter { get; }
    // ... constructor, dispose
}
```

#### 4. `WorkspaceContextFactory`

Creates `WorkspaceContext` instances from `WorkspaceKey`, wiring up all per-workspace services. Caches by key using `ConcurrentDictionary<WorkspaceKey, WorkspaceContext>`.

**File:** `src/Twig.Mcp/Services/WorkspaceContextFactory.cs`

**Key design decisions:**
- Shares global singletons: `HttpClient`, `IAuthenticationProvider`
- Creates per-workspace: `SqliteCacheStore`, all repos/stores, ADO clients, domain services
- Factory method mirrors the registration pattern in `TwigServiceRegistration` + `NetworkServiceModule` + `Program.cs`, but constructs objects directly instead of using DI
- Thread-safe via `ConcurrentDictionary.GetOrAdd` with `Lazy<WorkspaceContext>`

#### 5. `WorkspaceResolver`

Resolves a `WorkspaceContext` for a given tool call based on available signals.

**File:** `src/Twig.Mcp/Services/WorkspaceResolver.cs`

**Resolution algorithm:**
1. If `workspace` param is provided → parse and look up
2. If `WorkspaceRegistry.IsSingleWorkspace` → use the only workspace
3. Read a global "active workspace" from an in-memory field (set by `twig_set`)
4. Error: ambiguous, prompt caller to specify `workspace`

**Special case for `twig_set <numericId>`:**
1. If `workspace` is provided → use it
2. Probe each registered workspace's cache (`IWorkItemRepository.GetByIdAsync`)
3. If found in exactly one → use that workspace
4. If found in none → probe via ADO (`IAdoWorkItemService.FetchAsync`) per workspace
5. If found in exactly one → use that workspace
6. If ambiguous (found in multiple) → return error listing matches

#### 6. Active Workspace Tracking

A simple in-memory `WorkspaceKey?` field on `WorkspaceResolver` (or a dedicated `ActiveWorkspaceStore`). Set whenever `twig_set` succeeds. Read by all other tools for ambient resolution.

### Data Flow — `twig_set <id>` with workspace resolution

```
1. Tool receives (idOrPattern, workspace?)
2. WorkspaceResolver resolves WorkspaceContext:
   a. workspace="myorg/myproject" → factory.GetOrCreate("myorg/myproject")
   b. workspace=null, id=61826159 → probe all workspaces:
      - ws1.WorkItemRepo.GetByIdAsync(61826159) → null
      - ws2.WorkItemRepo.GetByIdAsync(61826159) → found! → use ws2
3. ActiveItemResolver.ResolveByIdAsync(id) using ws2's services
4. ws2.ContextStore.SetActiveWorkItemIdAsync(id)
5. ActiveWorkspaceStore.SetActive(ws2.Key)
6. Return result with workspace field
```

### Data Flow — `twig_status` (no explicit workspace)

```
1. Tool receives (workspace?)
2. WorkspaceResolver resolves:
   a. workspace=null → check ActiveWorkspaceStore → ws2
3. Use ws2.StatusOrchestrator.GetSnapshotAsync()
4. Return result with workspace field
```

### Design Decisions

**DD-1: Why not DI scopes?** The MCP SDK's `[McpServerToolType]` classes receive constructor-injected services resolved from the root container. The SDK does not create per-request DI scopes. We could intercept tool invocation with middleware, but the SDK's internal dispatch is opaque. Instead, we use an explicit `WorkspaceContextFactory` pattern — tool methods call `resolver.Resolve(workspace)` to get a `WorkspaceContext` and use its services directly.

**DD-2: Why `WorkspaceContext` bundle vs. per-service factory?** A bundle (one object with all services) is simpler than injecting N factories. Tool methods need ~6-8 services; passing them all individually from a context accessor would be noisy. The bundle also ensures consistency — all services for a given tool call reference the same workspace.

**DD-3: Tool class refactoring approach.** Rather than fundamentally changing how tools receive services, each tool class gets a `WorkspaceResolver` injected (singleton). Each method calls `resolver.Resolve(workspace)` to get a `WorkspaceContext`, then uses that context's services. This keeps the `[McpServerToolType]` / `[McpServerTool]` pattern intact. The existing constructor-injected services (`IWorkItemRepository`, etc.) become a **default workspace** fallback — but in practice, tools will route through the resolver.

**DD-4: Active workspace is in-memory, not persisted.** The "active workspace" (last workspace used by `twig_set`) is stored in-memory on the `WorkspaceResolver` instance. It is lost on process restart, which is fine — MCP processes are long-lived and tools always have the `workspace` param available. If no active workspace and multiple are registered, the tool returns an error prompting the caller to specify.

**DD-5: Workspace discovery at startup.** `WorkspaceRegistry` scans the `.twig/` directory once at startup. New workspaces created by `twig init` in other terminals during the process lifetime are not discovered. This is acceptable because MCP processes are restarted when VS Code is reloaded. A `twig_list_workspaces` tool can expose the current registry state.

## Alternatives Considered

### A. DI Scoped Services with MCP Middleware

**Approach:** Register workspace-bound services as `Scoped`, create a DI scope per tool call using MCP SDK middleware, set the workspace key on a scoped context object.

**Pros:** Leverages standard DI patterns; tool classes inject interfaces normally.
**Cons:** The MCP SDK (`ModelContextProtocol.Server`) uses `[McpServerToolType]` with reflection-free source generation. There is no documented middleware pipeline for intercepting tool dispatch and wrapping in a scope. Would require forking or monkey-patching the SDK's `McpServer` dispatcher. High risk for AOT compatibility.

**Decision:** Rejected — too tightly coupled to SDK internals.

### B. One MCP Process per Workspace

**Approach:** Launch separate `twig-mcp` processes per repo/workspace. VS Code's `.vscode/mcp.json` would list multiple entries.

**Pros:** Zero architectural change; each process is single-workspace as today.
**Cons:** Process overhead (N MCP processes); `.vscode/mcp.json` must be maintained per workspace; agents cannot cross-workspace resolve IDs; does not solve the agent-works-in-different-CWD problem.

**Decision:** Rejected — does not address the core problem.

## Dependencies

- **ModelContextProtocol SDK:** No middleware/scope support needed; tools use explicit resolver pattern. Compatible with current SDK version.
- **SQLite:** WAL mode already supports concurrent readers + single writer per DB. Multi-workspace means multiple DB files, each with independent connections. No contention.
- **Existing Infrastructure:** `TwigPaths.ForContext()` and `TwigConfiguration.Load()` already support multi-context paths. No infrastructure changes needed.

## Impact Analysis

### Modified Components

- **`Twig.Mcp/Program.cs`**: Simplified — removes single-workspace `WorkspaceGuard`, replaces singleton domain service registrations with `WorkspaceRegistry`, `WorkspaceContextFactory`, `WorkspaceResolver` singletons.
- **`Twig.Mcp/WorkspaceGuard.cs`**: Refactored to support "ambient mode" — returns valid when any registered workspace exists, even without top-level `.twig/config`.
- **`Twig.Mcp/Tools/ContextTools.cs`**: All methods gain `workspace` parameter; route through `WorkspaceResolver`.
- **`Twig.Mcp/Tools/ReadTools.cs`**: Same pattern.
- **`Twig.Mcp/Tools/MutationTools.cs`**: Same pattern.
- **`Twig.Mcp/Services/McpResultBuilder.cs`**: `FormatStatus` and `FormatWorkspace` gain `workspace` field in output.

### Backward Compatibility

- **Existing callers omitting `workspace`**: Fully supported. `WorkspaceResolver` falls back to active workspace or single-workspace default.
- **MCP tool schema**: New optional `workspace` parameter on all tools is additive; no existing parameters change.
- **On-disk format**: No changes to `.twig/config` or `.twig/{org}/{project}/twig.db` schemas.
- **DI registrations**: `TwigServiceRegistration.AddTwigCoreServices()` and `NetworkServiceModule.AddTwigNetworkServices()` are unchanged — they're used by CLI and TUI. MCP's `Program.cs` stops calling them for the default workspace and instead uses `WorkspaceContextFactory`.

### Performance

- Lazy workspace context creation: no overhead until a workspace is first accessed.
- `ConcurrentDictionary` lookup: O(1) per tool call.
- Additional DB connections: one `SqliteCacheStore` per active workspace. Typical usage: 1-3 workspaces.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| MCP SDK changes tool dispatch, breaking constructor injection assumptions | Low | Medium | Pin SDK version; tool classes use resolver pattern that's SDK-agnostic |
| Workspace discovery misses workspaces with unusual `.twig/` layouts | Low | Low | Discovery falls through to top-level `.twig/config` legacy path; `twig_list_workspaces` tool for debugging |
| Memory growth from cached `WorkspaceContext` instances | Low | Low | Typical: 1-3 workspaces. Each context holds one SQLite connection + service objects. ~50KB per context. |
| Thread safety: MCP SDK dispatches tool calls concurrently | Medium | Medium | `ConcurrentDictionary` for context cache; SQLite WAL for DB access; `ActiveWorkspaceStore` uses `Volatile.Read`/`Volatile.Write` or `lock`. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `twig_set` cross-workspace probe also search by title pattern, or only numeric IDs? | Low | Title patterns are inherently workspace-scoped (each DB has different items). Numeric IDs are globally unique in ADO. Recommend: numeric-only cross-workspace probe, title search within current workspace only. |
| 2 | Should the `workspace` parameter format be `"org/project"` or structured `{ org, project }`? | Low | String format `"org/project"` is simpler for MCP callers (agents). Structured would require a JSON object parameter which MCP tool schemas may not support well. Recommend: string format. |
| 3 | Should we support a `TWIG_MCP_WORKSPACES` environment variable as an alternative to on-disk discovery? | Low | On-disk discovery covers all cases where `twig init` has been run. Env var would be an optimization for CI/CD scenarios not relevant to MCP. Recommend: defer. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Mcp/Services/WorkspaceKey.cs` | Value object for `(org, project)` workspace identity |
| `src/Twig.Mcp/Services/WorkspaceRegistry.cs` | On-disk workspace discovery — scans `.twig/{org}/{project}/config` |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Per-workspace service bundle (DB, repos, stores, ADO client, domain services) |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Creates and caches `WorkspaceContext` per `WorkspaceKey` |
| `src/Twig.Mcp/Services/WorkspaceResolver.cs` | Per-tool-call workspace resolution (explicit → active → single-workspace → probe) |
| `src/Twig.Mcp/Tools/WorkspaceTools.cs` | `twig_list_workspaces` tool |
| `tests/Twig.Mcp.Tests/Services/WorkspaceKeyTests.cs` | Unit tests for `WorkspaceKey` parsing, equality, formatting |
| `tests/Twig.Mcp.Tests/Services/WorkspaceRegistryTests.cs` | Unit tests for workspace discovery from disk layout |
| `tests/Twig.Mcp.Tests/Services/WorkspaceResolverTests.cs` | Unit tests for resolution algorithm (explicit, active, single, probe) |
| `tests/Twig.Mcp.Tests/Services/WorkspaceContextFactoryTests.cs` | Unit tests for context creation, caching, disposal |
| `tests/Twig.Mcp.Tests/Tools/MultiWorkspaceSetTests.cs` | Integration tests: `twig_set` cross-workspace ID lookup |
| `tests/Twig.Mcp.Tests/Tools/MultiWorkspaceIsolationTests.cs` | Integration tests: two workspaces, independent state |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Program.cs` | Replace singleton workspace-bound service registrations with `WorkspaceRegistry`, `WorkspaceContextFactory`, `WorkspaceResolver` singletons. Refactor `WorkspaceGuard` call to ambient mode. Register `WorkspaceTools`. |
| `src/Twig.Mcp/WorkspaceGuard.cs` | Add `CheckWorkspaceAmbient()` method: succeeds when any `.twig/{org}/{project}/config` exists. Keep existing `CheckWorkspace()` for backward compat. |
| `src/Twig.Mcp/Tools/ContextTools.cs` | Add optional `workspace` param to `Set()` and `Status()`. Resolve `WorkspaceContext` via injected `WorkspaceResolver`. Use context's services instead of constructor-injected singletons. |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Add optional `workspace` param to `Tree()` and `Workspace()`. Same resolver pattern. |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add optional `workspace` param to `State()`, `Update()`, `Note()`, `Discard()`, `Sync()`. Same resolver pattern. |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `workspace` field to `FormatStatus()`, `FormatWorkspace()`, `FormatWorkItemWithWorkingSet()` outputs. |
| `src/Twig.Mcp/Services/McpPendingChangeFlusher.cs` | No interface change; instances are per-workspace via `WorkspaceContext`. |
| `tests/Twig.Mcp.Tests/ProgramBootstrapTests.cs` | Update DI composition test to validate `WorkspaceRegistry` + `WorkspaceContextFactory` registration. Add ambient-mode `WorkspaceGuard` tests. |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsTestBase.cs` | Update `CreateSut()` to accept/inject `WorkspaceResolver` with mock contexts. |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTestBase.cs` | Same pattern update. |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsTestBase.cs` | Same pattern update. |

## ADO Work Item Structure

This work is tracked under Issue [#1754](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1754). The following Tasks break down the implementation:

### Issue #1754: twig-mcp: support multiple workspaces per process

**Goal:** Enable a single `twig-mcp` process to serve tool calls against multiple `(org, project)` workspaces concurrently.

**Prerequisites:** None — this is a standalone Issue.

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|----------------|
| T1 | **WorkspaceKey value object + tests** — Create `WorkspaceKey` record with `Parse`, `TryParse`, `ToString`, equality. Unit tests for parsing edge cases (slashes, whitespace, case normalization). | `WorkspaceKey.cs`, `WorkspaceKeyTests.cs` | ~80 LoC |
| T2 | **WorkspaceRegistry + tests** — Implement on-disk workspace discovery. Scan `.twig/{org}/{project}/config`, load configs, expose `Workspaces`, `GetConfig()`, `IsSingleWorkspace`. Support legacy top-level `.twig/config` fallback. Unit tests with temp directory fixtures. | `WorkspaceRegistry.cs`, `WorkspaceRegistryTests.cs` | ~200 LoC |
| T3 | **WorkspaceContext + WorkspaceContextFactory + tests** — Define `WorkspaceContext` bundle class. Implement `WorkspaceContextFactory` with `ConcurrentDictionary<WorkspaceKey, Lazy<WorkspaceContext>>` caching, factory method that wires all services. Shares global `HttpClient` and `IAuthenticationProvider`. Tests verify creation, caching, and disposal. | `WorkspaceContext.cs`, `WorkspaceContextFactory.cs`, `WorkspaceContextFactoryTests.cs` | ~350 LoC |
| T4 | **WorkspaceResolver + active workspace tracking + tests** — Resolution algorithm: explicit param → active workspace → single-workspace default → error. Cross-workspace probe for `twig_set`. In-memory active workspace tracking. Tests for all resolution paths. | `WorkspaceResolver.cs`, `WorkspaceResolverTests.cs` | ~300 LoC |
| T5 | **Refactor tool classes for workspace resolution** — Add `workspace` optional param to all tool methods. Inject `WorkspaceResolver`. Route through `WorkspaceContext` services. Update `McpResultBuilder` to include `workspace` in outputs. Add `WorkspaceTools` (`twig_list_workspaces`). | `ContextTools.cs`, `ReadTools.cs`, `MutationTools.cs`, `McpResultBuilder.cs`, `WorkspaceTools.cs` | ~400 LoC |
| T6 | **Refactor Program.cs + WorkspaceGuard ambient mode** — Replace singleton domain-service registrations with `WorkspaceRegistry`, `WorkspaceContextFactory`, `WorkspaceResolver`. Add `WorkspaceGuard.CheckWorkspaceAmbient()`. Update bootstrap tests. | `Program.cs`, `WorkspaceGuard.cs`, `ProgramBootstrapTests.cs` | ~250 LoC |
| T7 | **Multi-workspace integration tests** — End-to-end tests: two workspaces with in-memory DBs, `twig_set` cross-workspace ID lookup, per-workspace isolation (independent active items, pending changes), backward-compat single-workspace test. Update test base classes. | `MultiWorkspaceSetTests.cs`, `MultiWorkspaceIsolationTests.cs`, `ContextToolsTestBase.cs`, `ReadToolsTestBase.cs`, `MutationToolsTestBase.cs` | ~400 LoC |

**Total estimated LoC: ~1,980**

#### Acceptance Criteria

- [ ] From any CWD, `twig_set <id>` resolves a work item in any registered workspace
- [ ] `twig_status` and `twig_workspace` report the workspace associated with the active context item
- [ ] Existing single-workspace setups continue to work with no config migration required
- [ ] Two registered workspaces can maintain independent active context items and pending changes
- [ ] `twig_set <id>` with ambiguous multi-workspace match returns a clear error listing matched workspaces
- [ ] `WorkspaceGuard` ambient mode does not hard-fail when top-level `.twig/config` is absent but registered workspaces exist
- [ ] `twig_list_workspaces` returns all discovered workspaces
- [ ] All new code is AOT-compatible (no reflection, source-gen JSON)

## PR Groups

### PG-1: Workspace Infrastructure (T1 → T4)

**Type:** Deep
**Tasks:** T1, T2, T3, T4
**Description:** Core workspace abstractions — value objects, registry, context factory, resolver. All new files with self-contained tests. No existing code modified.
**Files:** ~8 new files
**Estimated LoC:** ~930
**Successors:** PG-2

### PG-2: Tool Integration + Bootstrap (T5 → T7)

**Type:** Wide
**Tasks:** T5, T6, T7
**Description:** Integrate workspace resolution into all existing tool classes, refactor `Program.cs` bootstrap, and add multi-workspace integration tests. Modifies existing files.
**Files:** ~11 modified files, ~3 new files
**Estimated LoC:** ~1,050
**Predecessors:** PG-1

