# Twig MCP Server — Solution Design & Implementation Plan

> **Epic:** #1484 — Twig MCP Server
> **Revision:** 9
> **Status**: 🔨 In Progress

---

## Executive Summary

AI agents lose 2–10 seconds per workflow to repeated process startup and must parse fragile, untyped JSON from twig's CLI. This plan adds a long-lived MCP server (`Twig.Mcp`) that exposes 8 core work-item management tools over stdio with typed parameters and structured JSON responses. The server reuses the existing domain and infrastructure layers, launches via `twig mcp`, and maintains full AOT compatibility.

## Background

### Current Architecture

Twig is an AOT-compiled .NET 10 CLI organized into four projects:

| Layer | Project | Responsibility |
|-------|---------|----------------|
| Domain | `Twig.Domain` | Aggregates, interfaces, services, value objects, read models |
| Infrastructure | `Twig.Infrastructure` | ADO REST clients, SQLite persistence, auth, config, JSON serialization |
| CLI Host | `Twig` (exe) | ConsoleAppFramework commands, DI composition, output formatters, rendering |
| TUI Host | `Twig.Tui` (exe) | Terminal.Gui interactive tree navigator (separate process) |

DI registration is shared via two extension methods:
- `TwigServiceRegistration.AddTwigCoreServices()` — config, paths, SQLite, repositories, stores, process config, prompt state writer
- `NetworkServiceModule.AddTwigNetworkServices()` — auth, HTTP client, ADO work-item client, ADO git client, iteration service

The CLI host (`src/Twig/Program.cs`) additionally registers command services, output formatters, rendering pipeline, hints, and telemetry. The TUI host (`src/Twig.Tui/Program.cs`) only uses `AddTwigCoreServices()` since it doesn't need network services.

### Agent Integration Pain Points

Today, AI agents interact with twig by spawning a new process per command:
1. **Process startup overhead** — Each invocation loads config, opens SQLite, builds the DI container (~200ms). Agent workflows make 10–50 twig calls.
2. **Fragile output parsing** — Agents must append `--output json`, handle mixed stdout/stderr streams, and parse untyped JSON with no schema contract.
3. **No discoverability** — Agents cannot introspect available operations; they depend on the twig-cli section in `.github/copilot-instructions.md`.
4. **No schema enforcement** — CLI parameter changes silently break agent workflows.

### Prior Art

- **TUI launcher pattern** — `TuiLauncher` (Program.cs:766–855) locates and spawns the `twig-tui` binary via adjacent-directory + PATH lookup. The MCP launcher will follow this exact pattern.
- **PendingChangeFlusher** — The CLI's flusher (`src/Twig/Commands/PendingChangeFlusher.cs`) depends on `IConsoleInput` for interactive conflict resolution and `OutputFormatterFactory` for rendering. MCP needs a headless variant.
- **TwigJsonContext** — Source-generated JSON context at `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` handles all ADO DTOs and config types. MCP will define its own `McpJsonContext` for response types to avoid circular dependencies.

### Call-Site Audit: Services MCP Will Consume

| Service / Component | Current Consumers | MCP Usage | Impact |
|---------------------|-------------------|-----------|--------|
| `ActiveItemResolver` | SetCommand, StatusCommand, TreeCommand, StateCommand, UpdateCommand, NoteCommand, WorkspaceCommand | twig.set, twig.status, twig.tree, twig.state, twig.update, twig.note | No code changes — public class, registered directly in `Twig.Mcp/Program.cs` |
| `IContextStore` | SetCommand, StatusCommand, TreeCommand, WorkspaceCommand, PromptStateWriter | twig.set, twig.status, twig.workspace | No code changes |
| `IWorkItemRepository` | All commands | All tools | No code changes |
| `IAdoWorkItemService` | StateCommand, UpdateCommand, NoteCommand, SyncCommand, RefreshOrchestrator | twig.state, twig.update, twig.note, twig.sync | No code changes |
| `IPendingChangeStore` | NoteCommand, StateCommand, SyncCommand, PendingChangeFlusher | twig.note, twig.sync | No code changes |
| `IPromptStateWriter` | SetCommand, StateCommand, UpdateCommand, NoteCommand | twig.set, twig.state, twig.update, twig.note | No code changes |
| `StatusOrchestrator` | StatusCommand | twig.status | No code changes — public class, registered directly in `Twig.Mcp/Program.cs` |
| `RefreshOrchestrator` | RefreshCommand, SyncCommand | twig.sync | No code changes — public class, registered directly in `Twig.Mcp/Program.cs` |
| `SyncCoordinator` | SetCommand, TreeCommand, WorkspaceCommand | twig.set, twig.tree | No code changes — public class, registered directly in `Twig.Mcp/Program.cs` |
| `WorkTree.Build()` | TreeCommand | twig.tree | Static method — no changes |
| `StateResolver` | StateCommand | twig.state | Static method — no changes |
| `StateTransitionService` | StateCommand | twig.state | Static method — no changes |
| `ConflictRetryHelper` | StateCommand, UpdateCommand, EditCommand, PendingChangeFlusher | twig.state, twig.update, twig.sync | **Requires relocation + visibility change** — move from CLI (`src/Twig/Commands/`) to Infrastructure (`src/Twig.Infrastructure/Ado/`), change from `internal static class` with `internal static` method to `public static class` with `public static` method (Issue 0, T-0.1) |
| `AutoPushNotesHelper` | StateCommand, UpdateCommand, EditCommand | twig.state, twig.update | **Requires relocation + visibility change** — `internal static class` in CLI (`src/Twig/Commands/AutoPushNotesHelper.cs`). Must move to Infrastructure (`src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs`) as `public static class` since `Twig.Mcp` cannot reference the CLI exe project. ~15 lines of trivial push-and-clear logic. Relocated alongside `ConflictRetryHelper` in Issue 0 (T-0.2). |
| `MarkdownConverter` | UpdateCommand | twig.update | **Requires `InternalsVisibleTo`** — `internal static class` in Infrastructure (`src/Twig.Infrastructure/Content/MarkdownConverter.cs`). MCP accesses via `InternalsVisibleTo` entry added to `Twig.Infrastructure.csproj` (Issue 1, T-1.6) |
| `FlushResult` / `FlushItemFailure` | PendingChangeFlusher, SyncCommand | twig.sync | **No relocation needed** — `McpPendingChangeFlusher` defines its own local `McpFlushSummary` record in `Twig.Mcp`. CLI types stay in CLI. |
| `IPendingChangeFlusher` | SyncCommand | N/A | MCP creates `McpPendingChangeFlusher` (does not implement this CLI interface) |
| `IConsoleInput` | StateCommand, UpdateCommand, PendingChangeFlusher | N/A | MCP auto-allows transitions |
| `WorkingSetService` | SetCommand, TreeCommand, WorkspaceCommand | twig.workspace | No code changes — public class, registered directly in `Twig.Mcp/Program.cs` |

**Key finding:** Two cross-project dependency gaps require resolution before MCP can compile — `ConflictRetryHelper` (CLI-internal) and `AutoPushNotesHelper` (CLI-internal). `FlushResult`/`FlushItemFailure` stay in the CLI — `McpPendingChangeFlusher` uses a local `McpFlushSummary` record instead. See Issue 0 (T-0.1, T-0.2) and Issue 1 (T-1.6) for details.

#### Changes Required (subset of call-site audit rows needing code changes)

| Component | Current Location | Change Required | Issue |
|-----------|-----------------|-----------------|-------|
| `ConflictRetryHelper` | `src/Twig/Commands/` (`internal static`) | Move to `src/Twig.Infrastructure/Ado/`, change to `public static` | Issue 0, T-0.1 |
| `AutoPushNotesHelper` | `src/Twig/Commands/` (`internal static`) | Move to `src/Twig.Infrastructure/Ado/`, change to `public static` | Issue 0, T-0.2 |
| `MarkdownConverter` | `src/Twig.Infrastructure/Content/` (`internal static`) | Add `InternalsVisibleTo` for `Twig.Mcp` / `Twig.Mcp.Tests` | Issue 1, T-1.6 |

**Test location decision:** After relocating `ConflictRetryHelper` and `AutoPushNotesHelper` to Infrastructure, their tests (`ConflictRetryHelperTests.cs`, `AutoPushNotesHelperTests.cs`) remain in `Twig.Cli.Tests`. Rationale: (a) the helpers are still consumed by CLI commands (primary consumers), (b) moving tests would require restructuring test project references, (c) `Twig.Cli.Tests` already has `InternalsVisibleTo` access to Infrastructure, and (d) the using directive update is the only change needed — zero behavioral impact. Tests can be migrated to `Twig.Infrastructure.Tests` in a future cleanup if desired.

## Problem Statement

The MCP server must resolve four agent integration pain points documented in the Background section:

1. **Amortize startup cost** — The system must maintain a long-lived process that loads config, opens SQLite, and builds the DI container once, then serves all subsequent tool calls in-process with near-zero marginal overhead.
2. **Provide typed, structured responses** — Every tool must return a JSON object with a defined schema. Error conditions must be signaled via the MCP `IsError` property, not mixed into stdout. Agents must never need `--output json` flags or stderr filtering.
3. **Enable runtime discoverability** — The server must respond to `tools/list` with all available tools, parameter schemas, and descriptions. Agents must be able to introspect capabilities without external documentation.
4. **Enforce schema contracts** — Tool parameters must be typed and validated by the MCP SDK before reaching tool logic. Parameter additions or removals must produce explicit schema errors, not silent failures.

## Goals and Non-Goals

### Goals
- **G-1:** Expose 8 Phase 1 tools (set, status, tree, workspace, state, update, note, sync) as MCP tools with typed parameters and structured JSON responses
- **G-2:** Server starts via `twig mcp` subcommand using stdio transport
- **G-3:** Reuse existing domain/infrastructure layers — zero business logic duplication
- **G-4:** Maintain full AOT compatibility (`PublishAot=true`, `TrimMode=full`, `JsonSerializerIsReflectionEnabledByDefault=false`)
- **G-5:** Add `twig-mcp` entry to `.vscode/mcp.json` for automatic Copilot integration
- **G-6:** No regression in CLI — the MCP server is purely additive
- **G-7:** Update `.github/copilot-instructions.md` twig-cli section to prefer MCP tools when server is available

### Non-Goals
- **NG-1:** HTTP/SSE transport — stdio is sufficient for local agent integration
- **NG-2:** MCP resources or prompts — tools-only for Phase 1
- **NG-3:** Interactive conflict resolution — MCP mutations auto-accept remote on conflict (headless)
- **NG-4:** Spectre.Console rendering — MCP returns structured JSON, not terminal markup
- **NG-5:** Multi-workspace support — server assumes single `.twig/config` workspace per CWD
- **NG-6:** Streaming/progressive results — tools return complete results synchronously
- **NG-7:** Git remote auto-detection — MCP does not expose git-related tools in Phase 1; deferred.

## Requirements

### Functional
- **FR-1:** `twig.set` resolves by numeric ID (via `ActiveItemResolver`) or text pattern (via `FindByPatternAsync`), updates context, syncs, writes prompt state
- **FR-2:** `twig.status` returns active item with pending changes, seeds, and context metadata
- **FR-3:** `twig.tree` returns hierarchical JSON with parent chain, focused item, children, sibling counts, and links
- **FR-4:** `twig.workspace` returns sprint items, seeds, and active context
- **FR-5:** `twig.state` resolves partial state name, validates transition via `StateTransitionService`, auto-allows backward transitions (no interactive prompt), pushes to ADO with conflict retry
- **FR-6:** `twig.update` updates a field with conflict retry, supports optional markdown format conversion
- **FR-7:** `twig.note` pushes directly for published items, stages locally for seeds
- **FR-8:** `twig.sync` flushes all pending changes (headless — no interactive conflict resolution) then refreshes via `RefreshOrchestrator`
- **FR-9:** All mutation tools call `IPromptStateWriter.WritePromptStateAsync()` after success
- **FR-10:** Tool discovery via MCP `tools/list` returns all 8 tools with typed JSON schemas
- **FR-11:** MCP server exits with a clear error message if the workspace is not initialized (no `.twig/config` in CWD)

### Non-Functional
- **NFR-1:** AOT-compatible — `PublishAot=true`, `TrimMode=full`, no reflection-based JSON
- **NFR-2:** Long-lived server process — the MCP server process stays alive until stdin closes or the host shuts down, amortizing startup cost across all tool calls
- **NFR-3:** Logging to stderr — stdout reserved for MCP protocol communication
- **NFR-4:** Graceful shutdown on stdin close
- **NFR-5:** No new external dependencies beyond `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console`

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  twig CLI (src/Twig)                                            │
│  ┌──────────┐  ┌──────────────┐                                 │
│  │ twig mcp │──│ McpLauncher  │── locates & spawns twig-mcp     │
│  └──────────┘  └──────────────┘                                 │
└─────────────────────────────────────────────────────────────────┘
                        │ Process boundary
                        ▼
┌─────────────────────────────────────────────────────────────────┐
│  twig-mcp (src/Twig.Mcp)  — MCP Server Process                 │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Program.cs — Host.CreateApplicationBuilder               │    │
│  │   AddTwigCoreServices() + AddTwigNetworkServices()       │    │
│  │   AddMcpServer().WithStdioServerTransport()              │    │
│  │     .WithTools<ContextTools>()                           │    │
│  │     .WithTools<ReadTools>()                              │    │
│  │     .WithTools<MutationTools>()                          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌──────────────────┐  ┌──────────────────┐                     │
│  │ Tools/            │  │ Services/         │                    │
│  │  ContextTools.cs  │  │  McpResultBuilder │                    │
│  │  ReadTools.cs     │  │  McpJsonContext   │                    │
│  │  MutationTools.cs │  │  McpPendingChange │                    │
│  └──────────────────┘  │  Flusher          │                    │
│          │              └──────────────────┘                     │
│          ▼                                                      │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Twig.Domain + Twig.Infrastructure  (shared libraries)    │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

The MCP server is architecturally a sibling of `Twig.Tui` — a separate executable that references `Twig.Domain` and `Twig.Infrastructure` and composes its own DI container. The CLI launcher (`twig mcp`) spawns it as a child process, following the `TuiLauncher` pattern.

### Key Components

#### 1. `Program.cs` — Host Bootstrap

```csharp
// Pseudocode — actual implementation will follow this structure
SQLitePCL.Batteries.Init();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Shared service registration (Infrastructure layer)
var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
var config = TwigConfiguration.Load(Path.Combine(twigDir, "config"));
builder.Services.AddTwigCoreServices(config);
builder.Services.AddTwigNetworkServices(config);

// Domain orchestration services (subset of CLI's CommandServiceModule —
// only services consumed by MCP tools; see DD-10 for exclusions)
// NOTE: All registrations use factory-based sp => new ...() for AOT robustness.
// ActivatorUtilities reflection-based activation may be trimmed under PublishAot=true.
builder.Services.AddSingleton<ActiveItemResolver>(sp => new ActiveItemResolver(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>()));
builder.Services.AddSingleton<ProtectedCacheWriter>(sp => new ProtectedCacheWriter(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>()));
builder.Services.AddSingleton<SyncCoordinator>(sp => new SyncCoordinator(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IWorkItemLinkRepository>(),
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));
builder.Services.AddSingleton<WorkingSetService>(sp => new WorkingSetService(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IIterationService>(),
    sp.GetRequiredService<TwigConfiguration>().User.DisplayName));
// DD-10: BacklogOrderer, SeedPublishOrchestrator, SeedReconcileOrchestrator,
// and FlowTransitionService are NOT registered — no MCP tool consumes them.
builder.Services.AddSingleton<RefreshOrchestrator>(sp => new RefreshOrchestrator(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<IIterationService>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<WorkingSetService>(),
    sp.GetRequiredService<SyncCoordinator>(),
    sp.GetRequiredService<IProcessTypeStore>(),
    sp.GetRequiredService<IFieldDefinitionStore>()));
builder.Services.AddSingleton<StatusOrchestrator>(sp => new StatusOrchestrator(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<ActiveItemResolver>(),
    sp.GetRequiredService<WorkingSetService>(),
    sp.GetRequiredService<SyncCoordinator>()));

// MCP-specific services
builder.Services.AddSingleton<McpPendingChangeFlusher>(sp => new McpPendingChangeFlusher(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<IPendingChangeStore>()));

// MCP server — WithTools<T>() is the AOT-safe generic registration (not WithToolsFromAssembly)
builder.Services.AddMcpServer(o =>
{
    o.ServerInfo = new Implementation { Name = "twig-mcp", Version = "..." };
})
    .WithStdioServerTransport()
    .WithTools<ContextTools>()
    .WithTools<ReadTools>()
    .WithTools<MutationTools>();

await builder.Build().RunAsync();
```

Key differences from TUI bootstrap:
- Uses `Host.CreateApplicationBuilder` (not raw `ServiceCollection`) for hosting integration
- Registers both core and network services (TUI only uses core)
- Registers a **subset** of domain orchestration services using factory-based `sp => new ...()` registrations for AOT robustness (DD-10: excludes `BacklogOrderer`, `SeedPublishOrchestrator`, `SeedReconcileOrchestrator`, `FlowTransitionService` — not consumed by any MCP tool)
- Adds MCP server with stdio transport
- Redirects all logging to stderr
- Does not include git remote auto-detection (see NG-7)

#### 2. `Tools/ContextTools.cs` — twig.set, twig.status

```csharp
// [McpServerToolType] is optional when using WithTools<T>() (AOT-safe generic registration).
// Included for discoverability and IDE navigation.
[McpServerToolType]
public sealed class ContextTools(
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
    StatusOrchestrator statusOrchestrator,
    IPromptStateWriter promptStateWriter)
{
    [McpServerTool(Name = "twig.set"), Description("Set the active work item by ID or title pattern")]
    public async Task<CallToolResult> Set(
        [Description("Work item ID (numeric) or title pattern (text)")] string idOrPattern,
        CancellationToken ct) { ... }

    [McpServerTool(Name = "twig.status"), Description("Show the active work item status")]
    public async Task<CallToolResult> Status(CancellationToken ct) { ... }
}
```

**twig.set logic (owned by `ContextTools.Set()`):**
1. Parse `idOrPattern` — numeric → `ActiveItemResolver.ResolveByIdAsync()` (Domain), text → `IWorkItemRepository.FindByPatternAsync()` (Domain)
2. Multiple matches → return error with disambiguation list (no interactive prompt)
3. Set active ID via `IContextStore.SetActiveWorkItemIdAsync()` (Domain)
4. Best-effort sync via `SyncCoordinator.SyncItemSetAsync()` (Domain)
5. Call `IPromptStateWriter.WritePromptStateAsync()` (Domain)
6. Return item details via `McpResultBuilder.FormatWorkItem()` (MCP static)

**Deliberate deviations from CLI `SetCommand`:**
- **Working set eviction omitted** — The CLI performs `workingSetService.ComputeAsync()` + `workItemRepo.EvictExceptAsync()` on cache miss (`FetchedFromAdo` path, lines 230–234 of `SetCommand.cs`) to bound local cache size. MCP deliberately omits this. Rationale: (a) MCP has bounded session lifetime — the server runs for the duration of an agent workflow and exits on stdin close, unlike a user's ongoing CLI usage across days; (b) working set eviction is a cache size optimization, not a correctness concern; (c) `twig.sync` already refreshes the working set via `RefreshOrchestrator.SyncWorkingSetAsync()`, which implicitly handles eviction; (d) adding it would require injecting `WorkingSetService` into `ContextTools`, increasing coupling for marginal benefit. If cache growth becomes a concern in long-running MCP sessions, Phase 2 can add eviction.
- **Navigation history omitted** — The CLI calls `historyStore.RecordVisitAsync()` (line 148 of `SetCommand.cs`) to track sequential context changes for back/forward CLI commands (`twig back`, `twig forward`, `twig history`). MCP deliberately omits this. Rationale: (a) navigation history is a CLI-specific UX feature for sequential terminal usage — agents don't navigate sequentially, they set context by ID directly; (b) MCP has no back/forward tools; (c) recording phantom history entries would pollute the CLI user's navigation stack if the same workspace is used interactively afterward.

**twig.status logic (owned by `ContextTools.Status()`):**
1. Delegate to `StatusOrchestrator.GetSnapshotAsync()` (Domain — handles context resolution, pending changes, seeds, and sync coordinator internally)
2. If `StatusSnapshot.NoContext` → return `McpResultBuilder.ToError()`
3. Return snapshot via `McpResultBuilder.FormatStatus()` (MCP static)

#### 3. `Tools/ReadTools.cs` — twig.tree, twig.workspace

```csharp
[McpServerToolType]
public sealed class ReadTools(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IIterationService iterationService,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
    WorkingSetService workingSetService,
    TwigConfiguration config)  // Provided via DI — registered as singleton by AddTwigCoreServices()
{
    [McpServerTool(Name = "twig.tree"), Description("Display work item hierarchy as a tree")]
    public async Task<CallToolResult> Tree(
        [Description("Max child depth to display")] int? depth = null,
        CancellationToken ct = default) { ... }

    [McpServerTool(Name = "twig.workspace"), Description("Show sprint items, seeds, and context")]
    public async Task<CallToolResult> Workspace(
        [Description("Show all team items instead of just the current user")] bool all = false,
        CancellationToken ct = default) { ... }
}
```

**twig.tree logic (owned by `ReadTools.Tree()`):**
1. Get active item via `ActiveItemResolver.ResolveByIdAsync()` (Domain)
2. Build parent chain via `IWorkItemRepository.GetParentChainAsync()` (Domain), children via `IWorkItemRepository.GetChildrenAsync()` (Domain), sibling counts
3. Best-effort link sync via `SyncCoordinator.SyncLinksAsync(item.Id)` (Domain — fetches parent/child/related links from ADO, persists to `IWorkItemLinkRepository`, returns hydrated link list)
4. Build tree via `WorkTree.Build(item, parentChain, children, siblingCounts, links)` (Domain static)
5. Return tree JSON via `McpResultBuilder.FormatTree()` (MCP static)

**twig.workspace logic (owned by `ReadTools.Workspace()`):**
1. Get active context via `IContextStore.GetActiveWorkItemIdAsync()` (Domain, nullable)
2. Get current iteration via `IIterationService.GetCurrentIterationAsync()` (Domain)
3. Get sprint items — filtered by `config.User.DisplayName` via `IWorkItemRepository.GetByIterationAndAssigneeAsync()` (Domain) or all team items via `IWorkItemRepository.GetByIterationAsync()` (Domain) based on `all` parameter
4. Get seeds via `IWorkItemRepository.GetSeedsAsync()` (Domain)
5. Build workspace via `Workspace.Build(contextItem, sprintItems, seeds)` (Domain static)
6. Return workspace JSON via `McpResultBuilder.FormatWorkspace()` (MCP static)

#### 4. `Tools/MutationTools.cs` — twig.state, twig.update, twig.note, twig.sync

```csharp
[McpServerToolType]
public sealed class MutationTools(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    IIterationService iterationService,
    McpPendingChangeFlusher pendingChangeFlusher,
    RefreshOrchestrator refreshOrchestrator,
    IPromptStateWriter promptStateWriter)
{
    [McpServerTool(Name = "twig.state"), Description("Change the state of the active work item")]
    public async Task<CallToolResult> State(
        [Description("Target state name (full or partial, e.g. 'Active', 'Clo')")] string stateName,
        CancellationToken ct) { ... }

    [McpServerTool(Name = "twig.update"), Description("Update a field on the active work item")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. 'System.Title', 'System.Description')")] string field,
        [Description("New value for the field")] string value,
        [Description("Content format: 'markdown' converts to HTML before saving")] string? format = null,
        CancellationToken ct = default) { ... }

    [McpServerTool(Name = "twig.note"), Description("Add a note/comment to the active work item")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add")] string text,
        CancellationToken ct = default) { ... }

    [McpServerTool(Name = "twig.sync"), Description("Push pending changes to ADO and refresh the local cache")]
    public async Task<CallToolResult> Sync(CancellationToken ct = default) { ... }
}
```

**twig.state logic (owned by `MutationTools.State()`):**
1. Resolve active item via `ActiveItemResolver.GetActiveItemAsync()` (Domain)
2. Load process configuration via `IProcessConfigurationProvider.GetConfiguration()` (Domain)
3. Resolve state name via `StateResolver.ResolveByName(stateName, typeConfig.StateEntries)` (Domain static)
4. Evaluate transition via `StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState)` (Domain static)
5. **Auto-allow backward/cut transitions** — skip `RequiresConfirmation` check (DD-7: no interactive prompt; CLI uses `IConsoleInput.ReadLine()` here)
6. Fetch remote via `IAdoWorkItemService.FetchAsync()` (Domain), skip `ConflictResolutionFlow` (headless), push with `ConflictRetryHelper.PatchWithRetryAsync()` (Infrastructure static, relocated in Issue 0)
7. Auto-push pending notes via `AutoPushNotesHelper.PushAndClearAsync()` (Infrastructure static, relocated in Issue 0)
8. Re-fetch and cache update via `IAdoWorkItemService.FetchAsync()` → `IWorkItemRepository.SaveAsync()` (Domain)
9. Write prompt state via `IPromptStateWriter.WritePromptStateAsync()` (Domain)

**twig.update logic (owned by `MutationTools.Update()`):**
1. Resolve active item via `ActiveItemResolver.GetActiveItemAsync()` (Domain)
2. If `format == "markdown"`, convert via `MarkdownConverter.ToHtml()` (Infrastructure internal, accessed via `InternalsVisibleTo`)
3. Fetch remote via `IAdoWorkItemService.FetchAsync()` (Domain), skip `ConflictResolutionFlow`, push with `ConflictRetryHelper.PatchWithRetryAsync()` (Infrastructure static)
4. Auto-push pending notes via `AutoPushNotesHelper.PushAndClearAsync()` (Infrastructure static)
5. Re-fetch and cache update via `IAdoWorkItemService.FetchAsync()` → `IWorkItemRepository.SaveAsync()` (Domain)
6. Write prompt state via `IPromptStateWriter.WritePromptStateAsync()` (Domain)

**twig.note logic (owned by `MutationTools.Note()`):**
1. Resolve active item via `ActiveItemResolver.GetActiveItemAsync()` (Domain)
2. If seed → stage locally via `IPendingChangeStore.AddChangeAsync()` (Domain), apply to aggregate via `item.AddNote()` + `item.ApplyCommands()`, persist via `IWorkItemRepository.SaveAsync()` (Domain)
3. If published → push directly via `IAdoWorkItemService.AddCommentAsync()` (Domain), then clear note-type pending changes via `IPendingChangeStore.ClearChangesByTypeAsync()` (Domain)
4. Write prompt state via `IPromptStateWriter.WritePromptStateAsync()` (Domain)

**twig.sync logic (owned by `MutationTools.Sync()`):**
1. Flush all via `McpPendingChangeFlusher.FlushAllAsync()` (MCP service — headless, no interactive conflict resolution)
2. Resolve current iteration via `IIterationService.GetCurrentIterationAsync()` (Domain)
3. Refresh via `RefreshOrchestrator.SyncWorkingSetAsync(iteration)` (Domain — computes working set, syncs items from ADO, evicts stale cache entries) + `RefreshOrchestrator.HydrateAncestorsAsync()` (Domain — iteratively fetches orphan parent IDs up to 5 levels)
4. Return summary JSON via `McpResultBuilder.FormatFlushSummary()` (MCP static)

#### 5. `Services/McpResultBuilder.cs` — Result Formatting + JSON Context

A `static` class that converts domain objects into MCP `CallToolResult` instances containing structured JSON. Since it has no state or injected dependencies, it requires no DI registration — tools call static methods directly.

**Serialization approach (two-pronged — not contradictory):**

1. **`Utf8JsonWriter` (complex hierarchical output):** `Format*` methods like `FormatTree()`, `FormatWorkspace()`, and `FormatWorkItem()` build JSON strings directly with `Utf8JsonWriter` — no intermediate DTO types. This matches the CLI's `JsonOutputFormatter` pattern and avoids creating redundant record types for deeply nested response structures.

2. **`McpJsonContext` (simple flat types):** A `[JsonSerializable]` source-generated context for local types that benefit from `JsonSerializer.Serialize()` — specifically `McpFlushSummary` and simple error/summary payloads. Co-located in `McpResultBuilder.cs` to keep tightly coupled code together.

```csharp
// SDK types: ModelContextProtocol.Protocol.TextContentBlock, CallToolResult
// Note: CallToolResult.IsError is bool? (nullable). null = implicit success.
internal static class McpResultBuilder
{
    public static CallToolResult ToResult(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    public static CallToolResult ToError(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    public static CallToolResult FormatWorkItem(WorkItem item, ...) { ... }
    public static CallToolResult FormatStatus(StatusSnapshot snapshot) { ... }
    public static CallToolResult FormatTree(WorkTree tree, ...) { ... }
    public static CallToolResult FormatWorkspace(Workspace workspace, ...) { ... }
    // ... other format methods
}
```

#### 6. `Services/McpPendingChangeFlusher.cs` — Headless Flush

A simplified version of `PendingChangeFlusher` that:
- Skips `ConflictResolutionFlow.ResolveAsync()` (no `IConsoleInput`)
- On conflict: accepts remote revision and retries (auto-resolve)
- Does not use `OutputFormatterFactory` (MCP tools handle their own output)
- Returns `McpFlushSummary` (a local record defined in `Twig.Mcp`) for the caller to format
- Does **not** implement `IPendingChangeFlusher` (that interface has CLI-specific `outputFormat` parameters)

#### 7. `McpLauncher` — CLI Command

Added to `src/Twig/Program.cs` as a new command handler:

```csharp
// In TwigCommands class (no [Command] attribute needed per codebase convention):
public Task<int> Mcp() => Task.FromResult(McpLauncher.Launch());
```

`McpLauncher` follows the `TuiLauncher` pattern:
1. Look for `twig-mcp` in the same directory as the running binary
2. Fall back to PATH
3. Spawn the process with stdin/stdout inherited (for stdio transport)
4. Wait for exit, propagate exit code

### Data Flow

**Tool invocation flow (e.g., `twig.set 12345`):**

> **Note:** Only `twig.set` is shown in detail. All other tools follow the same pattern: resolve context → delegate to domain service → format via `McpResultBuilder` → return `CallToolResult` over stdio.

```
Agent → MCP Client → stdio → MCP Server → ContextTools.Set()
  → ActiveItemResolver.ResolveByIdAsync(12345)
  → IContextStore.SetActiveWorkItemIdAsync(12345)
  → SyncCoordinator.SyncItemSetAsync([12345, ...parentIds])
  → IPromptStateWriter.WritePromptStateAsync()
  → McpResultBuilder.FormatWorkItem(item)
  → CallToolResult { Content: [{ text: "{ id: 12345, ... }" }] }
  → stdio → MCP Client → Agent
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Separate `Twig.Mcp` project | Host lifecycle conflict — MCP uses `Host.CreateApplicationBuilder` + `RunAsync()` which blocks; CLI uses `ConsoleApp.Create()` + `Run()`. Cannot embed both in one process. |
| DD-2 | `McpLauncher` mirrors `TuiLauncher` | Consistent spawn pattern. Adjacent binary + PATH lookup, process exit code propagation. Proven pattern. |
| DD-3 | `McpResultBuilder` is a `static` class (not DI-registered) | It has no mutable state and no injected dependencies — purely a formatting utility. Static methods avoid 3 constructor parameters across tool classes and one DI registration. |
| DD-4 | `McpJsonContext` co-located in `McpResultBuilder.cs` | `TwigJsonContext` in Infrastructure handles ADO DTOs. `McpJsonContext` covers the small set of local types needing `JsonSerializer` support (e.g., `McpFlushSummary`, simple error payloads). Complex hierarchical output uses `Utf8JsonWriter` directly. Co-location avoids splitting tightly coupled code. |
| DD-5 | Tools return `CallToolResult` directly | The `IsError` property (`bool?` — nullable) controls MCP error signaling. Returning `string` loses this control. `null` = implicit success, `true` = tool error visible to LLM. |
| DD-6 | `McpPendingChangeFlusher` (headless) | CLI's `PendingChangeFlusher` requires `IConsoleInput` for interactive conflict prompts. MCP is headless — auto-accept remote on conflict. |
| DD-7 | Auto-allow backward state transitions | CLI prompts "Continue? [y/N]" for backward/cut transitions. MCP assumes agent intent is deliberate — auto-allow all valid transitions. |
| DD-8 | All mutation tools call `IPromptStateWriter` | Keeps `.twig/prompt.json` synchronized so CLI commands run after MCP mutations see correct state. |
| DD-9 | `InternalsVisibleTo` for `Twig.Mcp` in Domain and Infrastructure | Follows existing pattern (`Twig.Tui` in Domain, `Twig` CLI in both). Enables access to `MarkdownConverter` (internal in Infrastructure) without making it public. Preferred over making `MarkdownConverter` public since it is an implementation detail of the Infrastructure layer. |
| DD-10 | Register only MCP-consumed domain services | CLI's `CommandServiceModule` registers 10+ domain services. MCP only needs 6: `ActiveItemResolver`, `ProtectedCacheWriter`, `SyncCoordinator`, `WorkingSetService`, `RefreshOrchestrator`, `StatusOrchestrator`. Excluded: `BacklogOrderer`, `SeedPublishOrchestrator`, `SeedReconcileOrchestrator`, `FlowTransitionService` — none consumed by any of the 8 MCP tools. All registrations use explicit factory lambdas (`sp => new ...()`) for AOT robustness — `ActivatorUtilities` reflection-based activation may be trimmed. |
| DD-11 | Relocate `AutoPushNotesHelper` to Infrastructure alongside `ConflictRetryHelper` | `AutoPushNotesHelper` is `internal static` in the CLI project. Both `twig.state` and `twig.update` MCP tools call it for auto-pushing pending notes after state/field changes. Inlining ~15 lines would duplicate logic across CLI and MCP. Relocation to Infrastructure makes it available to both hosts. |
| DD-12 | Accept singleton `HttpClient` DNS caching limitation | `NetworkServiceModule` registers `services.AddSingleton<HttpClient>()` with a comment "singleton is acceptable for short-lived CLI process." The MCP server is long-lived, so stale DNS entries could theoretically cause connection failures if ADO endpoints rotate. However: (a) ADO REST endpoints (`dev.azure.com`) use stable DNS, (b) `SocketsHttpHandler` default `PooledConnectionLifetime` is `Timeout.InfiniteTimeSpan` but connections are recycled on errors, (c) switching to `IHttpClientFactory` requires modifying shared Infrastructure — a cross-cutting change out of scope for Phase 1. **Mitigation:** Document the limitation; if DNS issues arise, the agent restarts the MCP server process. Phase 2 can introduce `IHttpClientFactory` if needed. |

## Alternatives Considered

### Alternative 1: Embedded MCP Host vs. Separate Process (DD-1)

**Option A (chosen): Separate `Twig.Mcp` executable** — A new project that compiles to `twig-mcp`, spawned by `twig mcp` via `McpLauncher` (mirrors `TuiLauncher` pattern).

**Option B (rejected): Embed MCP server in the CLI process** — Add MCP hosting directly to `src/Twig/Program.cs`, activated via a `twig mcp` command that switches from ConsoleAppFramework to MCP hosting mode.

| Criterion | Separate Process (A) | Embedded (B) |
|-----------|---------------------|-------------|
| Host lifecycle | Clean — MCP uses `Host.CreateApplicationBuilder().Build().RunAsync()` which blocks until shutdown | Conflict — ConsoleAppFramework's `ConsoleApp.Create().Run()` already owns the process lifecycle; embedding a second host requires manual lifecycle coordination |
| DI container | Own container, clean composition | Shared container risks service lifetime conflicts (MCP needs long-lived singletons, CLI commands are transient) |
| AOT binary size | Two smaller binaries | One larger binary with MCP SDK linked even for non-MCP commands |
| Complexity | Moderate — launcher pattern well-established via `TuiLauncher` | High — must prevent ConsoleAppFramework from processing MCP stdio as command input |
| Debugging | Slightly harder (two processes) | Easier (single process) |

**Decision rationale:** The host lifecycle conflict is a hard blocker. ConsoleAppFramework parses stdin as CLI arguments; MCP requires stdin as a protocol transport. Separating them avoids a fragile multiplexing layer. The `TuiLauncher` pattern proves the separate-process approach works reliably.

### Alternative 2: `Utf8JsonWriter` vs. DTO Records with Source-Gen Serialization (DD-4)

**Option A (chosen): `Utf8JsonWriter` for complex hierarchical output** — `Format*` methods like `FormatTree()`, `FormatWorkspace()`, and `FormatWorkItem()` build JSON strings directly with `Utf8JsonWriter`. Simple flat types (e.g., `McpFlushSummary`) use `McpJsonContext` source-gen serialization.

**Option B (rejected): DTO record types for all outputs** — Define `TreeResponse`, `WorkspaceResponse`, `StatusResponse`, etc. as record types, register all in `McpJsonContext`, serialize via `JsonSerializer.Serialize()`.

| Criterion | Utf8JsonWriter (A) | DTO Records (B) |
|-----------|-------------------|-----------------|
| Type count | Zero new types for complex output (uses domain objects directly) | 10-15 new record types to mirror domain structure |
| AOT safety | Inherently AOT-safe — no reflection | Requires `[JsonSerializable]` for each type in `McpJsonContext`; nested types need explicit entries |
| Schema evolution | Change writer code — no type rename cascade | Rename types, update `[JsonSerializable]`, update tests |
| Consistency | Matches CLI's `JsonOutputFormatter` pattern exactly | Different pattern from CLI — creates maintenance divergence |
| IDE discoverability | Lower — JSON structure visible only at runtime or in tests | Higher — record types serve as documentation |
| Code verbosity | Higher — manual `WriteStartObject()`/`WritePropertyName()` calls | Lower — `JsonSerializer.Serialize(dto, McpJsonContext.Default.TreeResponse)` |

**Decision rationale:** The CLI already uses `Utf8JsonWriter` for its JSON output formatter, establishing the pattern. Complex response structures (tree with nested parent chain, children, sibling counts, links) would require 10+ record types just to shuttle data from domain objects to JSON. The two-pronged approach — `Utf8JsonWriter` for complex hierarchies, `McpJsonContext` for simple flat types — balances pragmatism with type safety.

## Dependencies

### External Packages (new)
| Package | Version | Purpose |
|---------|---------|---------|
| `ModelContextProtocol` | Latest stable (≥1.0.0) | Official C# MCP SDK — server, tools, stdio transport. Version 1.2.0 was specified in the Epic description but is unverifiable as a future version; use the latest stable release available at implementation time. |
| `Microsoft.Extensions.Hosting` | 10.0.x | Generic host for MCP server lifecycle (.NET 10, per OQ-1 resolution) |
| `Microsoft.Extensions.Logging.Console` | 10.0.x | Logging to stderr (.NET 10) |

### Internal Dependencies
| Project | Dependency |
|---------|------------|
| `Twig.Mcp` → `Twig.Domain` | Domain interfaces, services, aggregates, value objects |
| `Twig.Mcp` → `Twig.Infrastructure` | Service registration, ADO clients, SQLite, auth, config |
| `Twig` → (spawn) `Twig.Mcp` | `McpLauncher` spawns `twig-mcp` binary |

### Modified Existing Projects
| Project | Change |
|---------|--------|
| `Directory.Packages.props` | Add `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console` versions; bump `Microsoft.Extensions.DependencyInjection` from 9.0.3 to 10.0.x to align with .NET 10 hosting dependencies (OQ-1 resolved). Evaluate bumping `Microsoft.Data.Sqlite` from 9.0.14 to 10.0.x for alignment (see Risks table). |
| `src/Twig.Domain/Twig.Domain.csproj` | Add `InternalsVisibleTo` for `Twig.Mcp` and `Twig.Mcp.Tests` |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Add `InternalsVisibleTo` for `Twig.Mcp` and `Twig.Mcp.Tests` |

## Impact Analysis

### Components Affected
- **Issue 0 refactoring** — Moves `ConflictRetryHelper` and `AutoPushNotesHelper` to Infrastructure, updates `using` in 5 call sites (`StateCommand`, `UpdateCommand`, `EditCommand`, `PendingChangeFlusher`, `ConflictRetryHelperTests`, `AutoPushNotesHelperTests`) (~60-80 LoC changed, zero behavioral change)
- **New project `Twig.Mcp`** — New executable, ~450-600 LoC (includes `McpFlushSummary` local record)
- **New test project `Twig.Mcp.Tests`** — Unit tests, ~300-400 LoC
- **`Twig.Domain.csproj`** — `InternalsVisibleTo` entries for `Twig.Mcp` and `Twig.Mcp.Tests` (using simple `<InternalsVisibleTo Include="..." />` format matching existing entries)
- **`Twig.Infrastructure.csproj`** — new `ConflictRetryHelper.cs` (relocated as `public static`) + `InternalsVisibleTo` entries for `Twig.Mcp` and `Twig.Mcp.Tests` (using verbose `<AssemblyAttribute>` format matching existing entries)
- **`src/Twig/Program.cs`** — New `McpLauncher` class (~60 lines) + `twig mcp` command registration (~5 lines)
- **`Twig.slnx`** — Add 2 new project references
- **`Directory.Packages.props`** — Add 3 new package versions + bump `Microsoft.Extensions.DependencyInjection` from 9.0.3 to 10.0.x (aligns with .NET 10 hosting)
- **`.vscode/mcp.json`** — Add `twig-mcp` server entry
- **`.github/copilot-instructions.md`** — Update twig-cli section to prefer MCP when available
- **`publish-local.ps1`** — Add `Twig.Mcp` publish alongside CLI. Note: `Twig.Tui` was never added to this script despite having the same architectural role as a separate executable — the MCP addition establishes the multi-project publish pattern that TUI can optionally adopt later.

### Backward Compatibility
Fully backward compatible. The MCP server is purely additive:
- CLI behavior unchanged — all existing commands work identically
- No interface changes — all domain/infrastructure interfaces unchanged
- No data model changes — same SQLite database schema
- MCP server shares the same `.twig/` workspace — reads and writes are compatible

### Performance
- **Startup:** MCP server loads once (~200ms), then all tool calls are in-process
- **Per-tool overhead:** ~0ms (no process spawn, no DI rebuild, no config reload)
- **Expected improvement:** 10-50x reduction in aggregate overhead for multi-call workflows
- **Memory:** One additional process (~20-40MB) when MCP server is running

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `ModelContextProtocol` SDK AOT incompatibility | Low | High | SDK explicitly supports AOT (`<IsAotCompatible>true</IsAotCompatible>` on net8.0+). Verify with `dotnet publish` before merging Issue 1. |
| `Microsoft.Extensions.DependencyInjection` version bump breaks existing projects | Medium | Medium | T-1.4 bumps M.E.DI from 9.0.3 to 10.0.x (required by M.E.Hosting 10.0.x and MCP SDK). Run full `dotnet test` across all projects after the version bump. Pin exact 10.0.x version in `Directory.Packages.props`. |
| `Microsoft.Data.Sqlite` version alignment with .NET 10 hosting stack | Medium | Medium | `Microsoft.Data.Sqlite` 9.0.14 depends on M.E.DI 9.0.x transitively. Bumping M.E.DI to 10.0.x may produce binding redirect issues or warnings. **Mitigation:** During T-1.4, also evaluate bumping `Microsoft.Data.Sqlite` to 10.0.x for alignment. If 10.0.x is unavailable, verify 9.0.14 resolves cleanly with M.E.DI 10.0.x via `dotnet restore --force` and build verification. |
| Singleton `HttpClient` DNS caching in long-lived process | Low | Low | `NetworkServiceModule` registers `HttpClient` as singleton — appropriate for short-lived CLI but potentially problematic for a long-lived MCP server if DNS entries rotate. ADO endpoints are stable in practice. See DD-12 for full analysis. Restart MCP server as mitigation. |
| Headless conflict resolution causes data loss | Low | Medium | `McpPendingChangeFlusher` logs conflicts and returns error status. Agent can retry after `twig sync`. |
| MCP server doesn't gracefully shutdown | Low | Low | `Host.RunAsync()` handles `SIGTERM` and stdin close. Verified by Issue 1 AOT publish acceptance criteria. |
| JSON schema drift between CLI and MCP outputs | Medium | Low | MCP outputs are independent of CLI. Both consume the same domain objects. Drift is acceptable since MCP schemas are the source of truth for agents. |
| Long-lived SQLite connection stale after schema migration | Low | Medium | If the CLI runs `twig init --force` while MCP server is running, the DB schema may be rebuilt under the server's open connection. Mitigation: MCP server detects `SchemaWasRebuilt` on startup; for mid-session rebuilds, agent restarts the MCP server. Documented in copilot-instructions.md. |

## Security Considerations

The MCP server introduces no new attack surface:

- **Transport:** stdio-only — no network listener, no open ports. Communication is confined to the parent process's stdin/stdout pipes.
- **Authentication:** Inherits the spawning process's credentials. ADO access uses the same PAT/credential chain as the CLI (`~/.twig/config`). No additional authentication mechanism is introduced.
- **Authorization:** No privilege escalation. MCP tools perform the same operations as CLI commands, using the same ADO REST API with the same user identity and permission set.
- **Data exposure:** Tool responses contain the same work-item data the user already has access to via CLI. No data is sent to third-party services.
- **Process isolation:** The MCP server runs as a separate child process. It shares the same `.twig/` workspace directory and SQLite database as the CLI — no new persistence or IPC mechanisms.

## Open Questions

### Resolved

| ID | Question | Resolution | Affected Decisions |
|----|----------|------------|--------------------|
| OQ-1 | **Should new packages (`Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console`, `Microsoft.Extensions.DependencyInjection`) target .NET 10 or stay on .NET 9?** The MCP SDK targets `netstandard2.0`/`net8.0`/`net9.0`/`net10.0`, so it works with either. The hosting stack (`Microsoft.Extensions.Hosting`) offers 10.0.x builds that align with the project's .NET 10 TFM. Choosing 9.0.x avoids cross-major alignment risk with `Microsoft.Data.Sqlite` 9.0.14; choosing 10.0.x aligns with the project's target framework and avoids carrying mixed-major extension packages. | **Resolved: .NET 10 everywhere.** Per user input, all new packages use 10.0.x versions. `Microsoft.Extensions.DependencyInjection` is also bumped from 9.0.3 to 10.0.x across the solution to maintain alignment. `Microsoft.Data.Sqlite` version alignment is tracked as a risk (see Risks table) and evaluated during T-1.4. | DD-10 (factory registrations), T-1.4 (version bump), Dependencies table |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/ConflictRetryHelper.cs` | `ConflictRetryHelper` relocated from CLI layer, changed to `public static class` with `public static` method (Issue 0) |
| `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs` | `AutoPushNotesHelper` relocated from CLI layer, changed to `public static class` with `public static` method (Issue 0, T-0.2) |
| `src/Twig.Mcp/Twig.Mcp.csproj` | Project file with AOT settings, package refs, project refs |
| `src/Twig.Mcp/Program.cs` | Host bootstrap, DI, MCP server registration |
| `src/Twig.Mcp/Tools/ContextTools.cs` | `twig.set`, `twig.status` tool implementations |
| `src/Twig.Mcp/Tools/ReadTools.cs` | `twig.tree`, `twig.workspace` tool implementations |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `twig.state`, `twig.update`, `twig.note`, `twig.sync` tool implementations |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Static result formatter (`Utf8JsonWriter` for complex output) + `McpJsonContext` (source-gen serialization for simple flat types) |
| `src/Twig.Mcp/Services/McpPendingChangeFlusher.cs` | Headless pending change flusher (no `IConsoleInput`) |
| `tests/Twig.Mcp.Tests/Twig.Mcp.Tests.csproj` | Test project file |
| `tests/Twig.Mcp.Tests/Tools/ContextToolsTests.cs` | Unit tests for twig.set, twig.status |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTests.cs` | Unit tests for twig.tree, twig.workspace |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsTests.cs` | Unit tests for twig.state, twig.update, twig.note, twig.sync |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Unit tests for result formatting |
| `tests/Twig.Mcp.Tests/Services/McpPendingChangeFlusherTests.cs` | Unit tests for headless flusher |
| `tests/Twig.Cli.Tests/Commands/McpLauncherTests.cs` | Unit tests for `McpLauncher` (mirrors `TuiLauncherTests.cs` pattern) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/PendingChangeFlusher.cs` | Update `using` for `ConflictRetryHelper` namespace (`Twig.Commands` → `Twig.Infrastructure.Ado`) |
| `src/Twig/Commands/EditCommand.cs` | Update `using` directives: `Twig.Commands` → `Twig.Infrastructure.Ado` for `ConflictRetryHelper` and `AutoPushNotesHelper` |
| `src/Twig/Commands/StateCommand.cs` | Update `using` directives for `ConflictRetryHelper` and `AutoPushNotesHelper` namespaces |
| `src/Twig/Commands/UpdateCommand.cs` | Update `using` directives for `ConflictRetryHelper` and `AutoPushNotesHelper` namespaces |
| `tests/Twig.Cli.Tests/Commands/ConflictRetryHelperTests.cs` | Update `using` directive for `ConflictRetryHelper` namespace |
| `tests/Twig.Cli.Tests/Commands/AutoPushNotesHelperTests.cs` | Update `using` directive for `AutoPushNotesHelper` namespace |
| `src/Twig.Domain/Twig.Domain.csproj` | Add `InternalsVisibleTo` entries for `Twig.Mcp` and `Twig.Mcp.Tests` (simple `<InternalsVisibleTo Include>` format) |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Add `InternalsVisibleTo` entries for `Twig.Mcp` and `Twig.Mcp.Tests` (verbose `<AssemblyAttribute>` format) |
| `Twig.slnx` | Add `Twig.Mcp` and `Twig.Mcp.Tests` project references |
| `Directory.Packages.props` | Add `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console` package versions; bump `Microsoft.Extensions.DependencyInjection` from 9.0.3 to 10.0.x (aligns with .NET 10 hosting dependencies) |
| `src/Twig/Program.cs` | Add `McpLauncher` class + `twig mcp` command registration |
| `.vscode/mcp.json` | Add `twig-mcp` server entry |
| `.github/copilot-instructions.md` | Update twig-cli section to document MCP preference — when `twig-mcp` server is configured, prefer MCP tool calls over CLI commands. Add concrete content: tool names (`twig.set`, `twig.status`, `twig.tree`, `twig.workspace`, `twig.state`, `twig.update`, `twig.note`, `twig.sync`), parameter summaries, and guidance on when to use MCP vs CLI |
| `publish-local.ps1` | Add `Twig.Mcp` publish alongside CLI (establishes multi-project publish pattern — `Twig.Tui` was never added to this script despite identical architectural role; see Impact Analysis note) |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/ConflictRetryHelper.cs` | Relocated to `src/Twig.Infrastructure/Ado/ConflictRetryHelper.cs` with visibility change to `public static` (Issue 0, T-0.1) |
| `src/Twig/Commands/AutoPushNotesHelper.cs` | Relocated to `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs` with visibility change to `public static` (Issue 0, T-0.2) |

## ADO Work Item Structure

> **Effort legend:** S < 2h, M 2–4h, L 4–8h

### Issue 0: Shared Layer Refactoring (Prerequisite)

**Goal:** Resolve two cross-project dependency gaps that would otherwise block `Twig.Mcp` from compiling: relocate `ConflictRetryHelper` and `AutoPushNotesHelper` to Infrastructure. `FlushResult`/`FlushItemFailure` stay in the CLI — `McpPendingChangeFlusher` defines its own local `McpFlushSummary` record instead. Domain orchestration services (`ActiveItemResolver`, `StatusOrchestrator`, etc.) are public and registered directly in `Twig.Mcp/Program.cs` — no shared extraction module is needed.

**Prerequisites:** None

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-0.1 | Move `ConflictRetryHelper` from CLI to Infrastructure and update all call sites (see sub-steps below). | See sub-steps below | S |
| T-0.2 | Move `AutoPushNotesHelper` from CLI to Infrastructure (see sub-steps below). | See sub-steps below | S |

**T-0.1 sub-steps:**
1. Create `src/Twig.Infrastructure/Ado/ConflictRetryHelper.cs` — copy class from `src/Twig/Commands/ConflictRetryHelper.cs`, update namespace from `Twig.Commands` to `Twig.Infrastructure.Ado`, change visibility from `internal static class` / `internal static` method to `public static class` / `public static` method (public because it is a general-purpose utility consumed by multiple host projects).
2. Delete `src/Twig/Commands/ConflictRetryHelper.cs`.
3. Update `using` directives in **all 4 CLI call sites**: `StateCommand.cs`, `UpdateCommand.cs`, `EditCommand.cs`, `PendingChangeFlusher.cs` — change `Twig.Commands` → `Twig.Infrastructure.Ado`.
4. Update `using` in test file `tests/Twig.Cli.Tests/Commands/ConflictRetryHelperTests.cs`.
5. Verify: all dependencies (`IAdoWorkItemService`, `FieldChange`, `AdoConflictException`) are already in Domain/Infrastructure — no additional changes needed.

**T-0.2 sub-steps:**
1. Create `src/Twig.Infrastructure/Ado/AutoPushNotesHelper.cs` — copy class from `src/Twig/Commands/AutoPushNotesHelper.cs`, update namespace from `Twig.Commands` to `Twig.Infrastructure.Ado`, change visibility from `internal static class` / `internal static` method to `public static class` / `public static` method.
2. Delete `src/Twig/Commands/AutoPushNotesHelper.cs`.
3. Update `using` directives in **3 CLI call sites**: `StateCommand.cs`, `UpdateCommand.cs`, `EditCommand.cs` — add `using Twig.Infrastructure.Ado;` (may already be present from T-0.1).
4. Update `using` in test file `tests/Twig.Cli.Tests/Commands/AutoPushNotesHelperTests.cs`.
5. Verify: all dependencies (`IPendingChangeStore`, `IAdoWorkItemService`) are already in Domain — no additional changes needed.

**Acceptance Criteria:**
- [ ] `ConflictRetryHelper` compiles from Infrastructure as `public static class` with `public static` method, no dependency issues (FR-5, FR-6, FR-8)
- [ ] `AutoPushNotesHelper` compiles from Infrastructure as `public static class` with `public static` method, no dependency issues (FR-5, FR-6, DD-11)
- [ ] All existing tests pass (zero behavioral change)
- [ ] No new warnings introduced

---

### Issue 1: Project Scaffold & Host Bootstrap

**Goal:** Create the `Twig.Mcp` project with AOT-compatible MCP server bootstrap, DI registration, `twig mcp` launcher command, and test project scaffold.

**Prerequisites:** Issue 0

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-1.1 | Create `Twig.Mcp.csproj` with AOT settings (`PublishAot`, `TrimMode=full`, `InvariantGlobalization`, `JsonSerializerIsReflectionEnabledByDefault=false`), package references (`ModelContextProtocol`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console`, `Microsoft.Data.Sqlite`, `SQLitePCLRaw.bundle_e_sqlite3`), and project references (`Twig.Domain`, `Twig.Infrastructure`) | `src/Twig.Mcp/Twig.Mcp.csproj` | S |
| T-1.2 | Implement `Program.cs` — workspace guard (exits with clear error if `.twig/config` is missing, FR-11), `SQLitePCL.Batteries.Init()`, host setup with stderr-only logging (`builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`), DI registrations (`AddTwigCoreServices` + `AddTwigNetworkServices` + domain orchestration subset using factory lambdas per DD-10 + `McpPendingChangeFlusher`), MCP server registration (`AddMcpServer` with stdio transport and all three tool types), `await builder.Build().RunAsync()` | `src/Twig.Mcp/Program.cs` | L |
| T-1.3 | Add `McpLauncher` to CLI — mirrors `TuiLauncher` pattern (adjacent binary + PATH, stdin/stdout passthrough for stdio transport). Register `twig mcp` command in `TwigCommands` (no `[Command]` attribute per convention). | `src/Twig/Program.cs` | S |
| T-1.4 | Update solution and package management — add projects to `Twig.slnx`, add package versions to `Directory.Packages.props` (`ModelContextProtocol`, `Microsoft.Extensions.Hosting` 10.0.x, `Microsoft.Extensions.Logging.Console` 10.0.x). **Bump `Microsoft.Extensions.DependencyInjection` from 9.0.3 to 10.0.x** to align with .NET 10 hosting dependencies (OQ-1 resolved: .NET 10 everywhere). Verify `dotnet restore` succeeds across all projects. | `Twig.slnx`, `Directory.Packages.props` | S |
| T-1.5 | Create `Twig.Mcp.Tests` project scaffold with xUnit, Shouldly, NSubstitute references | `tests/Twig.Mcp.Tests/Twig.Mcp.Tests.csproj` | S |
| T-1.6 | Add `InternalsVisibleTo` entries for `Twig.Mcp` and `Twig.Mcp.Tests` to both `.csproj` files. **Use each file's existing format:** `Twig.Domain.csproj` uses the simple `<InternalsVisibleTo Include="..." />` form; `Twig.Infrastructure.csproj` uses the verbose `<AssemblyAttribute>` form. Required for MCP to access internal types such as `MarkdownConverter` (Infrastructure). | `src/Twig.Domain/Twig.Domain.csproj`, `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | S |
| T-1.7 | Add `McpLauncherTests.cs` — mirrors `TuiLauncherTests.cs` pattern: verify `Launch()` returns exit code 1 when `twig-mcp` binary is not found, verify error message includes `twig-mcp` binary name. | `tests/Twig.Cli.Tests/Commands/McpLauncherTests.cs` | S |

**Acceptance Criteria:**
- [ ] `dotnet build` succeeds for `Twig.Mcp` with zero warnings (NFR-1)
- [ ] `dotnet publish -r win-x64 -c Release` produces AOT binary with no trimming warnings (NFR-1, G-4)
- [ ] `twig mcp` command launches the MCP server process (G-2)
- [ ] MCP server starts, listens on stdio, responds to `initialize` handshake (NFR-2, NFR-4)
- [ ] MCP server exits with clear error if `.twig/config` is missing (FR-11)
- [ ] `InternalsVisibleTo` entries present in both `Twig.Domain.csproj` and `Twig.Infrastructure.csproj` for `Twig.Mcp` and `Twig.Mcp.Tests` (DD-9, FR-6: enables `MarkdownConverter` access for twig.update)
- [ ] `dotnet restore` succeeds across all projects after `Microsoft.Extensions.DependencyInjection` version bump to 10.0.x (NFR-1)
- [ ] `McpLauncherTests` pass — binary-not-found returns exit code 1, error message includes `twig-mcp` (mirrors `TuiLauncherTests`)
- [ ] Test project builds and links correctly

---

### Issue 2: McpResultBuilder, McpJsonContext & McpPendingChangeFlusher

**Goal:** Build the result formatting service, source-generated JSON context, and headless pending change flusher that all tools depend on.

**Prerequisites:** Issue 1 (project must compile); Issue 0 (`ConflictRetryHelper` and `AutoPushNotesHelper` in Infrastructure)

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-2.1 | Implement `McpResultBuilder` as a `static` class with `ToResult(json)`, `ToError(message)`, and typed `Format*` methods. `Format*` methods use `Utf8JsonWriter` directly for complex hierarchical output (matching CLI's `JsonOutputFormatter` pattern). Define `McpJsonContext` with `[JsonSerializable]` attributes for local types needing `JsonSerializer.Serialize()` (e.g., `McpFlushSummary`, error/summary payloads) in the same file. | `src/Twig.Mcp/Services/McpResultBuilder.cs` | L |
| T-2.2 | Implement `McpPendingChangeFlusher` — headless flush that skips `ConflictResolutionFlow`, auto-accepts remote on conflict retry via `ConflictRetryHelper` (now in Infrastructure), returns `McpFlushSummary` (local record in `Twig.Mcp` — `FlushResult`/`FlushItemFailure` stay in CLI). Does not implement `IPendingChangeFlusher`. | `src/Twig.Mcp/Services/McpPendingChangeFlusher.cs` | M |
| T-2.3 | Unit tests for `McpResultBuilder` — verify JSON structure, error formatting, round-trip with `McpJsonContext` | `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | M |
| T-2.4 | Unit tests for `McpPendingChangeFlusher` — verify headless flush, conflict auto-resolve, failure collection | `tests/Twig.Mcp.Tests/Services/McpPendingChangeFlusherTests.cs` | M |

**Acceptance Criteria:**
- [ ] `McpResultBuilder` produces valid JSON for each domain object type (G-1)
- [ ] `McpJsonContext` compiles with source generation, no reflection warnings (NFR-1, G-4)
- [ ] `McpPendingChangeFlusher` flushes without `IConsoleInput` dependency (FR-8)
- [ ] All unit tests pass

---

### Issue 3: Context Tools (twig.set, twig.status)

**Goal:** Implement the two context management tools that set active item and display current status.

**Prerequisites:** Issue 2

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-3.1 | Implement `ContextTools.Set` — dual-path resolution (numeric ID via `ActiveItemResolver.ResolveByIdAsync`, text pattern via `FindByPatternAsync`), context update, parent chain hydration, best-effort sync, prompt state write | `src/Twig.Mcp/Tools/ContextTools.cs` | M |
| T-3.2 | Implement `ContextTools.Status` — delegate to `StatusOrchestrator.GetSnapshotAsync()`, format via `McpResultBuilder.FormatStatus()` | `src/Twig.Mcp/Tools/ContextTools.cs` | S |
| T-3.3 | Unit tests for `twig.set` — happy path (numeric ID), happy path (pattern), multiple matches (error with list), not found (error), prompt state called | `tests/Twig.Mcp.Tests/Tools/ContextToolsTests.cs` | M |
| T-3.4 | Unit tests for `twig.status` — happy path, no context (error) | `tests/Twig.Mcp.Tests/Tools/ContextToolsTests.cs` | S |

**Acceptance Criteria:**
- [ ] `twig.set` resolves work items by both numeric ID and text pattern (FR-1)
- [ ] `twig.set` returns error with disambiguation list on multiple matches (FR-1)
- [ ] `twig.status` returns full status snapshot as structured JSON (FR-2)
- [ ] `IPromptStateWriter.WritePromptStateAsync()` called after successful `twig.set` (FR-9)
- [ ] All tests pass (T-3.3, T-3.4)

---

### Issue 4: Read Tools (twig.tree, twig.workspace)

**Goal:** Implement read-only tools for hierarchy navigation and workspace overview.

**Prerequisites:** Issue 2

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-4.1 | Implement `ReadTools.Tree` — resolve active item, build parent chain, children, sibling counts, link sync, `WorkTree.Build()`, format via `McpResultBuilder.FormatTree()` | `src/Twig.Mcp/Tools/ReadTools.cs` | M |
| T-4.2 | Implement `ReadTools.Workspace` — get context, current iteration, sprint items (filtered by assignee or all), seeds, `Workspace.Build()`, format via `McpResultBuilder.FormatWorkspace()` | `src/Twig.Mcp/Tools/ReadTools.cs` | M |
| T-4.3 | Unit tests for `twig.tree` — happy path, no active item (error), children rendering, depth limit | `tests/Twig.Mcp.Tests/Tools/ReadToolsTests.cs` | M |
| T-4.4 | Unit tests for `twig.workspace` — happy path, with seeds, all-team mode | `tests/Twig.Mcp.Tests/Tools/ReadToolsTests.cs` | S |

**Acceptance Criteria:**
- [ ] `twig.tree` returns hierarchical JSON matching `WorkTree.Build()` output (FR-3)
- [ ] `twig.workspace` returns sprint items, seeds, and context metadata (FR-4)
- [ ] Optional `depth` parameter respected for tree display (FR-3)
- [ ] Optional `all` parameter switches between user-scoped and team-scoped workspace (FR-4)
- [ ] All tests pass

---

### Issue 5: Mutation Tools (twig.state, twig.update, twig.note, twig.sync)

**Goal:** Implement the four mutation tools that modify work item state, fields, notes, and sync data.

**Prerequisites:** Issue 2

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-5.1 | Implement `MutationTools.State` — resolve active item, state name resolution via `StateResolver.ResolveByName()`, transition validation via `StateTransitionService.Evaluate()`, auto-allow backward, push via `ConflictRetryHelper.PatchWithRetryAsync()`, auto-push notes via `AutoPushNotesHelper.PushAndClearAsync()` (both relocated to Infrastructure in Issue 0), re-fetch, prompt state | `src/Twig.Mcp/Tools/MutationTools.cs` | L |
| T-5.2 | Implement `MutationTools.Update` — resolve active item, optional markdown conversion, push via `ConflictRetryHelper.PatchWithRetryAsync()`, auto-push notes via `AutoPushNotesHelper.PushAndClearAsync()`, re-fetch, prompt state | `src/Twig.Mcp/Tools/MutationTools.cs` | M |
| T-5.3 | Implement `MutationTools.Note` — resolve active item, seed vs. published branching, direct push or local stage, prompt state | `src/Twig.Mcp/Tools/MutationTools.cs` | S |
| T-5.4 | Implement `MutationTools.Sync` — delegate to `McpPendingChangeFlusher.FlushAllAsync()` then `RefreshOrchestrator`, return summary | `src/Twig.Mcp/Tools/MutationTools.cs` | M |
| T-5.5 | Unit tests for `twig.state` — happy path, partial name resolution, backward auto-allow, invalid transition (error), already in state | `tests/Twig.Mcp.Tests/Tools/MutationToolsTests.cs` | M |
| T-5.6 | Unit tests for `twig.update` — happy path, markdown conversion, conflict retry | `tests/Twig.Mcp.Tests/Tools/MutationToolsTests.cs` | M |
| T-5.7 | Unit tests for `twig.note` — published item (direct push), seed (local stage) | `tests/Twig.Mcp.Tests/Tools/MutationToolsTests.cs` | S |
| T-5.8 | Unit tests for `twig.sync` — happy path, flush failures | `tests/Twig.Mcp.Tests/Tools/MutationToolsTests.cs` | S |

**Acceptance Criteria:**
- [ ] `twig.state` resolves partial state names and validates transitions (FR-5)
- [ ] Backward transitions auto-allowed without interactive prompt (FR-5, DD-7)
- [ ] `twig.update` supports inline value and markdown format conversion (FR-6)
- [ ] `twig.note` pushes directly for published items, stages for seeds (FR-7)
- [ ] `twig.sync` flushes and refreshes without interactive prompts (FR-8)
- [ ] `IPromptStateWriter.WritePromptStateAsync()` called after all mutation tools (FR-9)
- [ ] All tests pass

---

### Issue 6: Configuration & Skill Update

**Goal:** Add IDE integration configuration and update documentation for MCP preference.

**Prerequisites:** Issue 1

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T-6.1 | Add `twig-mcp` entry to `.vscode/mcp.json` with stdio transport config pointing to `twig mcp` command | `.vscode/mcp.json` | S |
| T-6.2 | Update `.github/copilot-instructions.md` to document MCP preference. The twig-cli section (line 75) currently references the `twig-cli` skill for CLI commands. Add a new **MCP Server** subsection with: (a) MCP tool names and parameter summaries for all 8 tools (`twig.set`, `twig.status`, `twig.tree`, `twig.workspace`, `twig.state`, `twig.update`, `twig.note`, `twig.sync`), (b) guidance to prefer MCP tool calls when the `twig-mcp` server is configured in `.vscode/mcp.json`, (c) note that all mutation tools auto-push and update prompt state — no separate `twig save` needed, (d) fallback guidance: use CLI commands if MCP server is unavailable. | `.github/copilot-instructions.md` | S |
| T-6.3 | Update `publish-local.ps1` to publish both `Twig` and `Twig.Mcp` projects | `publish-local.ps1` | S |

**Acceptance Criteria:**
- [ ] `.vscode/mcp.json` contains valid `twig-mcp` server configuration (G-5)
- [ ] `.github/copilot-instructions.md` documents MCP tool names, parameter schemas, and preference guidance (G-7)
- [ ] `publish-local.ps1` produces both `twig` and `twig-mcp` binaries

---

## PR Groups

PR groups cluster tasks for reviewable pull requests, sized for ≤2000 LoC and ≤50 files. They are a cross-cutting overlay — not a 1:1 mapping to the ADO Issue hierarchy.

| PR Group | Issues/Tasks Included | Type | Est. LoC | Est. Files | Predecessors |
|----------|----------------------|------|----------|------------|-------------|
| **PR-1: Shared Layer Refactoring** | Issue 0 (T-0.1, T-0.2) | **wide** — mechanical moves across many files, minimal logic | ~80 | ~10 | None |
| **PR-2: Project Scaffold & Bootstrap** | Issue 1 (T-1.1 through T-1.7) | **deep** — host setup, DI wiring, AOT verification, launcher tests | ~400 | ~9 | PR-1 |
| **PR-3: Services & Read Tools** | Issue 2 (T-2.1 through T-2.4) + Issue 4 (T-4.1 through T-4.4) | **deep** — result formatting, headless flusher, read-only tool implementations | ~600 | ~8 | PR-2 |
| **PR-4: Context & Mutation Tools + Config** | Issue 3 (T-3.1 through T-3.4) + Issue 5 (T-5.1 through T-5.8) + Issue 6 (T-6.1 through T-6.3) | **deep** — all stateful tool implementations with conflict retry, state transitions; config/doc updates included as feature completion | ~780 | ~9 | PR-3 |

**Execution order:** PR-1 → PR-2 → PR-3 → PR-4 (strictly sequential — each PR builds on the previous).

**Rationale:**
- **PR-1** is isolated refactoring with zero behavioral change — safe to merge independently and unblock all downstream work.
- **PR-2** establishes the project and build infrastructure. Must verify AOT before tool implementation begins.
- **PR-3** combines services (Issue 2) with read tools (Issue 4) because read tools are the primary consumers of `McpResultBuilder`. Reviewing them together provides better context.
- **PR-4** combines context tools (Issue 3) with mutation tools (Issue 5) — both are stateful tool implementations that share the `ActiveItemResolver` + `ConflictRetryHelper` + `AutoPushNotesHelper` patterns. Issue 6 config/doc updates (~80 LoC, 3 files) are included here as the natural feature-completion step rather than a separate PR.

## References

- [MCP C# SDK Documentation](https://github.com/modelcontextprotocol/csharp-sdk)
- [MCP C# SDK API Reference](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.html)
- [ModelContextProtocol NuGet](https://www.nuget.org/packages/ModelContextProtocol)
- [MCP Specification](https://modelcontextprotocol.io/specification)
- [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework)
