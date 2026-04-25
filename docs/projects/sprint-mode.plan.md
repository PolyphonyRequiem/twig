# Sprint Mode — Solution Design & Implementation Plan

| Field | Value |
|-------|-------|
| **Work Item** | #1948 — Sprint Mode |
| **Parent** | #1945 — Workspace Modes & Tracking |
| **Status** | Draft |
| **Revision** | 1 |
| **Revision Notes** | Resolved OQ-1 and OQ-4 per user input. Refresh stays `@current`-only. Using dedicated `ISprintIterationStore` (not `IWorkspaceModeStore`). Clarified #1946 decoupling strategy throughout. |

---

## Executive Summary

Sprint Mode adds multi-iteration sprint support and a dedicated `twig sprint` read-only view command to the twig CLI. Users configure workspace sprint subscriptions via `twig workspace sprint add <expr>` / `remove <expr>`, using relative iteration expressions (`@current`, `@current-1`, `@current+1`) that resolve dynamically, or absolute iteration paths. The existing `twig sprint` command is replaced with a dedicated `SprintCommand` that displays cached sprint items immediately with async background refresh, supports `--all` for team view, and emits a performance warning when sprint mode is not enabled. Sprint iteration configuration persists in SQLite via a dedicated `ISprintIterationStore` interface, and supports invalidation tracking when the current sprint changes. The `twig refresh` command is **not** modified — it continues to fetch only the current iteration.

## Background

### Current State

Today, twig's sprint functionality is tightly coupled to a single iteration path:

1. **`IIterationService.GetCurrentIterationAsync()`** calls the ADO REST API (`/_apis/work/teamsettings/iterations?$timeframe=current`) to detect the current sprint. Returns a single `IterationPath` value object.

2. **`WorkingSetService.ComputeAsync()`** accepts an optional `IterationPath` parameter. Queries `IWorkItemRepository.GetByIterationAsync(iteration)` or `GetByIterationAndAssigneeAsync(iteration, displayName)` against SQLite. Only a single iteration is ever queried.

3. **`WorkspaceCommand`** resolves the current iteration via `IIterationService`, fetches sprint items from local cache, and renders them. The `twig sprint` command delegates to `WorkspaceCommand.ExecuteAsync()` with `sprintLayout: true`.

4. **`RefreshCommand`** builds a WIQL query filtering by `[System.IterationPath] = '<current>'` to populate the cache. Only the current sprint's items are fetched. **This will NOT change** — refresh remains `@current`-only per user decision.

5. **No persistence** of sprint configuration exists — every command invocation re-resolves the current iteration from ADO.

6. **Schema version 9** (10 tables) with no `sprint_iterations` table.

### Dependency on Workspace Mode Infrastructure (#1946)

Issue #1946 defines foundational workspace mode infrastructure including `IWorkspaceModeStore`. **Per user decision, this plan uses a dedicated `ISprintIterationStore` interface to avoid coupling to unmerged work.** When #1946 lands with `IWorkspaceModeStore`, the sprint store can be refactored to delegate. The self-contained `sprint_iterations` table is created directly via schema version bump.

### Call-Site Audit: `IIterationService.GetCurrentIterationAsync()`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `WorkspaceCommand.cs` L69 | `StreamWorkspaceData()` | Resolves current iteration for sprint items | None — Workspace command unchanged |
| `WorkspaceCommand.cs` L162 | `ExecuteSyncAsync()` | Resolves current iteration for sync sprint query | None — Workspace command unchanged |
| `WorkingSetService.cs` L57 | `ComputeAsync()` | Fallback when no iteration provided | None — callers provide resolved iterations |
| `RefreshCommand.cs` L62 | `ExecuteCoreAsync()` | Builds WIQL for cache refresh | **None** — refresh stays `@current`-only |
| `AdoIterationService.cs` L48 | `GetCurrentIterationAsync()` | ADO REST call with `$timeframe=current` | None — existing implementation reused |

**Conclusion:** No existing call site needs modification. New `SprintCommand` and `SprintConfigCommand` use a new `SprintIterationResolver` that composes `IIterationService` with the sprint configuration store.

### Call-Site Audit: `IWorkItemRepository.GetByIterationAsync()` / `GetByIterationAndAssigneeAsync()`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `WorkingSetService.cs` L60-62 | `ComputeAsync()` | Single iteration query | None — Sprint command aggregates separately |
| `WorkspaceCommand.cs` L72-74 | `StreamWorkspaceData()` | Single iteration query | None — workspace uses single iteration |
| `WorkspaceCommand.cs` L168-173 | `ExecuteSyncAsync()` | Single iteration query | None |
| `MCP ReadTools.cs` L88-90 | `Workspace()` | Single iteration query | None |
| `MCP NavigationTools.cs` L141 | `Sprint()` | Single iteration query for `twig_sprint` | Low — MCP sprint tool may be enhanced later |

**Conclusion:** Existing callers query a single iteration. The new sprint view calls `GetByIterationAsync()` once per configured iteration and unions the results.

## Problem Statement

Twig currently assumes a single, implicitly-detected sprint iteration. This creates several limitations:

1. **No multi-sprint visibility.** Users working across sprint boundaries (finishing carry-over items from last sprint while planning next sprint) cannot see items from multiple iterations simultaneously.

2. **No persistent sprint configuration.** Every command invocation re-resolves the current iteration from ADO. There is no way to say "I care about these specific sprints" and have that persist.

3. **No relative iteration expressions.** Users cannot express "current sprint minus one" — they must know and type the absolute iteration path (e.g., `Project\Sprint 5`).

4. **No sprint-specific command.** The `twig sprint` command exists but is just an alias for `WorkspaceCommand` with `sprintLayout: true`. It doesn't have sprint-specific semantics like configuration management.

5. **Cache limitations.** When sprint mode is not enabled, `twig sprint` hits the ADO API on every invocation to resolve the current iteration, then queries the local cache which may not contain items from non-current sprints.

## Goals and Non-Goals

### Goals

1. **Multi-iteration tracking** — Allow users to configure multiple sprint iterations (e.g., `@current` + `@current-1`) and see items from all of them.
2. **Relative iteration expressions** — Support `@current`, `@current-1`, `@current+1` that resolve dynamically when the current sprint changes.
3. **Absolute iteration paths** — Support explicit paths like `"Project\Sprint 5"` as pinned iterations.
4. **Persistent sprint configuration** — Store sprint iteration configuration in SQLite via dedicated `ISprintIterationStore`.
5. **`twig sprint` read-only view** — A dedicated sprint view command that shows cached items immediately, updates async, and supports `--all` for team view.
6. **Graceful degradation** — `twig sprint` works even when sprint mode is not configured, with a performance warning.
7. **Invalidation tracking** — Detect when the current sprint changes and re-resolve relative expressions.

### Non-Goals

1. **Sprint planning/creation** — We do not create or modify sprints in ADO.
2. **Sprint burndown/velocity** — No analytics or charts.
3. **Modifying the working set** — `twig sprint` is read-only; it does NOT change the workspace working set.
4. **Cross-project iterations** — Only iterations within the configured project are supported.
5. **Modifying `twig workspace`** — The existing workspace command behavior is unchanged.
6. **Changing `twig refresh`** — Refresh continues to populate the cache from the current iteration only. Multi-iteration cache population is a future enhancement.
7. **Using `IWorkspaceModeStore` from #1946** — Using dedicated `ISprintIterationStore` to avoid coupling to unmerged work.

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | `twig workspace sprint add <expr>` adds a sprint iteration expression to the workspace configuration |
| FR-2 | `twig workspace sprint remove <expr>` removes a sprint iteration expression |
| FR-3 | Relative expressions (`@current`, `@current-1`, `@current+1`) are stored as expressions and resolved at query time |
| FR-4 | Absolute iteration paths (e.g., `"Project\Sprint 5"`) are stored and used directly |
| FR-5 | Sprint iteration configuration persists in SQLite across CLI invocations |
| FR-6 | `twig sprint` shows items from all configured sprint iterations, user-scoped by default |
| FR-7 | `twig sprint --all` shows all team members' items grouped by assignee |
| FR-8 | When sprint mode is not enabled, `twig sprint` queries the current iteration on-demand with a warning |
| FR-9 | `twig sprint` shows cached data immediately, then refreshes async |
| FR-10 | When the current sprint changes, relative expressions resolve to the new iteration |
| FR-11 | `twig workspace sprint list` shows configured expressions (with resolved paths in human output) |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: no reflection, all JSON via `TwigJsonContext` |
| NFR-2 | Zero breaking changes to existing commands |
| NFR-3 | SQLite schema change via version bump (9 → 10) |
| NFR-4 | Telemetry safe: no iteration paths, project names, or user names in telemetry |
| NFR-5 | All new code must have unit tests with ≥80% coverage |

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                    CLI Layer (Twig)                       │
│  ┌──────────────────┐  ┌──────────────────────────────┐  │
│  │  SprintCommand   │  │  SprintConfigCommand          │  │
│  │  (twig sprint)   │  │  (twig workspace sprint       │  │
│  │                  │  │   add/remove/list)            │  │
│  └────────┬─────────┘  └──────────┬───────────────────┘  │
│           │                       │                      │
│  ┌────────┴───────────────────────┴───────────────────┐  │
│  │            SpectreRenderer (existing)              │  │
│  │            + IOutputFormatter (existing)           │  │
│  └────────────────────────┬───────────────────────────┘  │
└───────────────────────────┼──────────────────────────────┘
                            │
┌───────────────────────────┼──────────────────────────────┐
│                  Domain Layer (Twig.Domain)               │
│  ┌────────────────────────┴───────────────────────────┐  │
│  │           SprintIterationResolver                  │  │
│  │  - Resolves relative expressions to IterationPaths │  │
│  │  - Aggregates items across multiple iterations     │  │
│  └──┬────────────────┬────────────────┬───────────────┘  │
│     │                │                │                   │
│  ┌──┴──────────┐  ┌──┴─────────┐  ┌──┴────────────────┐ │
│  │IterationExpr│  │ISprintIter │  │IWorkItemRepository│ │
│  │(value obj)  │  │ationStore  │  │(existing)         │ │
│  └─────────────┘  └──────┬─────┘  └───────────────────┘ │
└──────────────────────────┼───────────────────────────────┘
                           │
┌──────────────────────────┼───────────────────────────────┐
│            Infrastructure Layer (Twig.Infrastructure)    │
│  ┌───────────────────────┴────────────────────────────┐  │
│  │          SqliteSprintIterationStore                 │  │
│  │  - CRUD on sprint_iterations table                 │  │
│  │  - Depends on SqliteCacheStore for connection      │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `IterationExpression` (Value Object — Domain)

A validated, immutable value object representing a sprint iteration expression. Two variants:

- **Relative**: `@current`, `@current-1`, `@current+1` — stored as the expression string, resolved at query time using `IIterationService`.
- **Absolute**: `"Project\Sprint 5"` — stored as the raw iteration path, used directly.

```
IterationExpression
├── Raw: string           // The original expression (e.g., "@current-1")
├── Kind: ExpressionKind  // Relative | Absolute
├── Offset: int           // 0 for @current, -1 for @current-1, etc. (Relative only)
├── Parse(string) → Result<IterationExpression>
└── IsRelative: bool
```

**Parsing rules:**
- `@current` → Relative, offset 0
- `@current-N` → Relative, offset -N (where N is a positive integer)
- `@current+N` → Relative, offset +N
- Anything else → Absolute, treated as a literal iteration path
- Empty/whitespace → error

**Design decision**: Relative expressions are resolved by fetching all team iterations from ADO (sorted by date), finding the current iteration's index, and applying the offset. This avoids depending on iteration naming conventions.

#### 2. `ISprintIterationStore` (Interface — Domain)

Dedicated persistence contract for sprint iteration configuration. **Per user decision**, this is a standalone interface — not part of `IWorkspaceModeStore` from #1946. When #1946 lands, this store can be refactored to delegate internally without changing callers.

```csharp
public interface ISprintIterationStore
{
    Task<IReadOnlyList<IterationExpression>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(IterationExpression expression, CancellationToken ct = default);
    Task RemoveAsync(string rawExpression, CancellationToken ct = default);
    Task<bool> ExistsAsync(string rawExpression, CancellationToken ct = default);
    Task<bool> HasAnyAsync(CancellationToken ct = default);
}
```

#### 3. `SqliteSprintIterationStore` (Infrastructure)

SQLite implementation of `ISprintIterationStore`. Reads/writes to the `sprint_iterations` table:

```sql
CREATE TABLE sprint_iterations (
    expression TEXT PRIMARY KEY,
    kind TEXT NOT NULL  -- 'relative' or 'absolute'
);
```

This table is added directly to `SqliteCacheStore.Ddl` with a schema version bump (9 → 10). No dependency on #1946 schema.

#### 4. `SprintIterationResolver` (Domain Service)

Resolves configured iteration expressions to concrete `IterationPath` values and aggregates work items:

```csharp
public sealed class SprintIterationResolver(
    ISprintIterationStore sprintStore,
    IIterationService iterationService,
    IWorkItemRepository workItemRepo,
    string? userDisplayName)
{
    // Resolves all configured expressions to IterationPaths
    Task<IReadOnlyList<IterationPath>> ResolveConfiguredIterationsAsync(CancellationToken ct)

    // Fetches work items across all resolved iterations (union, deduplicated by ID)
    Task<IReadOnlyList<WorkItem>> GetSprintItemsAsync(bool allUsers, CancellationToken ct)

    // Resolves a single relative expression to an IterationPath
    Task<IterationPath?> ResolveRelativeAsync(int offset, CancellationToken ct)
}
```

**Resolution algorithm for relative expressions:**
1. Call `IIterationService.GetCurrentIterationAsync()` to get the current iteration path.
2. Fetch all team iterations from ADO (new method: `IIterationService.GetAllIterationsAsync()`).
3. Sort iterations by start date.
4. Find the index of the current iteration in the sorted list.
5. Apply the offset (e.g., `@current-1` → index - 1).
6. Return the resolved `IterationPath`, or `null` if out of bounds.

#### 5. `SprintCommand` (CLI Command)

A new dedicated command class for `twig sprint`. Unlike the current delegation to `WorkspaceCommand`, this owns sprint-specific logic:

```csharp
public sealed class SprintCommand(
    ISprintIterationStore sprintStore,
    SprintIterationResolver resolver,
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IIterationService iterationService,
    IProcessTypeStore processTypeStore,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    ActiveItemResolver activeItemResolver,
    RenderingPipelineFactory? pipelineFactory = null)
{
    Task<int> ExecuteAsync(
        string outputFormat, bool all, bool noLive, bool noRefresh,
        CancellationToken ct)
}
```

**Behavior:**
- If sprint mode is enabled (iterations configured): query cached items for all configured iterations, show immediately.
- If sprint mode is NOT enabled: call `IIterationService.GetCurrentIterationAsync()`, query on-demand, emit warning.
- Always read-only — does not modify the working set.
- Uses existing `SprintHierarchy` read model for hierarchical rendering.
- Async path: shows cached data first, then refreshes from ADO in background.

#### 6. `SprintConfigCommand` (CLI Command)

Manages sprint iteration configuration:

```csharp
public sealed class SprintConfigCommand(
    ISprintIterationStore sprintStore,
    SprintIterationResolver resolver,
    OutputFormatterFactory formatterFactory)
{
    Task<int> AddAsync(string expression, string outputFormat, CancellationToken ct)
    Task<int> RemoveAsync(string expression, string outputFormat, CancellationToken ct)
    Task<int> ListAsync(string outputFormat, CancellationToken ct)
}
```

### Data Flow

#### `twig workspace sprint add @current-1`
1. Parse `"@current-1"` → `IterationExpression { Kind: Relative, Offset: -1 }`
2. Validate: check `ISprintIterationStore.ExistsAsync("@current-1")` → reject duplicates
3. Persist: `ISprintIterationStore.AddAsync(expression)`
4. Confirm: "Added sprint iteration: @current-1"

#### `twig sprint`
1. Check `ISprintIterationStore.HasAnyAsync()` → determines if sprint mode is enabled
2. **If enabled:**
   a. `SprintIterationResolver.ResolveConfiguredIterationsAsync()` → resolves all expressions to `IterationPath[]`
   b. For each resolved path: `IWorkItemRepository.GetByIterationAndAssigneeAsync(path, user)` (or `GetByIterationAsync` for `--all`)
   c. Union results, deduplicate by ID
   d. Build `SprintHierarchy` from union
   e. Render via Spectre (async) or formatter (sync)
3. **If not enabled:**
   a. Emit warning: "Sprint mode isn't enabled. Run 'twig workspace sprint add @current' for faster lookups."
   b. `IIterationService.GetCurrentIterationAsync()` → single iteration
   c. Query items for that iteration
   d. Render

#### `twig sprint --all`
Same as above, but:
- Skip user-scoping filter (no `assignee` clause)
- Items grouped by assignee in `SprintHierarchy`

#### `twig workspace sprint list`
1. `ISprintIterationStore.GetAllAsync()` → all configured expressions
2. For human output: resolve each expression to show the resolved path alongside
3. For JSON output: emit raw expressions only

### Design Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| DD-1 | **Separate `SprintCommand` vs reusing `WorkspaceCommand`** | `WorkspaceCommand` is already 340 lines with complex branching. Sprint-specific logic (multi-iteration resolution, configuration checks, warnings) would push it past maintainable size. Workspace and sprint views have different semantics. |
| DD-2 | **Store expressions, not resolved paths** | Relative expressions must resolve dynamically. Storing `@current-1` means it automatically resolves to the correct iteration when the sprint changes. |
| DD-3 | **Resolve relative via all-iterations list** | ADO provides no "relative iteration" API. Fetching the full iteration list and computing offsets is the only reliable approach. |
| DD-4 | **New `IIterationService.GetAllIterationsAsync()`** | Existing `GetCurrentIterationAsync()` only returns the current iteration. All iterations (sorted by date) are needed for offset resolution. Natural extension of the existing interface. |
| DD-5 | **Read-only sprint command** | Per spec, `twig sprint` does NOT change the working set. Safe for frequent use without side effects. |
| DD-6 | **Warning when sprint mode not enabled** | Users who haven't configured sprint iterations get a functional but slower experience. Warning educates about configuration option. |
| DD-7 | **Dedicated `ISprintIterationStore`** | Per user decision, avoids coupling to unmerged #1946 work. Clean focused interface. When #1946 lands with `IWorkspaceModeStore`, the store implementation can delegate internally. |
| DD-8 | **Refresh unchanged** | Per user decision, `twig refresh` stays `@current`-only. Multi-iteration cache population is a future enhancement. Items from non-current sprints will only appear in cache if they were previously fetched. |
| DD-9 | **Schema version 10** | Self-contained schema bump. If #1946 also bumps schema, the version number is coordinated at merge time. |

## Alternatives Considered

### 1. Extend `WorkspaceCommand` Instead of Creating `SprintCommand`

**Pros:** Less code, reuses existing async/sync rendering paths.
**Cons:** `WorkspaceCommand` is already 340 lines with complex branching. Adding multi-iteration resolution, configuration checks, and sprint-specific warnings would push it past maintainable size.
**Decision:** Create a new `SprintCommand` that reuses shared infrastructure but owns its own flow.

### 2. Store Resolved Iteration Paths Instead of Expressions

**Pros:** Simpler resolution — just read paths from DB.
**Cons:** When the current sprint changes, all relative expressions become stale. Users would need to manually re-add `@current` every sprint.
**Decision:** Store expression strings and resolve at query time.

### 3. Use IContextStore (KV) Instead of a Dedicated Table

**Pros:** No schema change — store JSON-serialized list in the `context` table.
**Cons:** Loses queryability, atomicity of add/remove, and uniqueness constraints.
**Decision:** Use a dedicated `sprint_iterations` table with proper constraints.

### 4. Use IWorkspaceModeStore from #1946

**Pros:** Consistent abstraction if #1946 is implemented first.
**Cons:** Creates hard dependency on unmerged work. If #1946's API changes, this work breaks.
**Decision:** Per user input — use dedicated `ISprintIterationStore`. Refactor to delegate when #1946 lands.

## Dependencies

### External Dependencies

| Dependency | Purpose |
|------------|---------|
| ADO REST API: `/_apis/work/teamsettings/iterations` | Fetch all iterations for relative expression resolution (without `$timeframe` filter) |
| SQLite (existing) | Persist sprint iteration configuration |
| Spectre.Console (existing) | Async rendering for sprint view |

### Internal Dependencies

| Dependency | Purpose |
|------------|---------|
| `IIterationService` | Current iteration detection + new `GetAllIterationsAsync()` method |
| `IWorkItemRepository` | Query items by iteration path |
| `SprintHierarchy` read model | Hierarchical rendering with assignee groups |
| `IOutputFormatter` | Existing formatters for JSON/human/minimal output |
| `SpectreRenderer` | Async rendering pipeline |

### Sequencing Constraints

1. **No dependency on #1946.** Sprint iteration table is self-contained via schema version bump.
2. **No dependency on #1949 (Area Mode), #1950 (Recent Mode), or #1951 (Dashboard Rendering).**
3. Tasks within this issue have internal sequencing: T1→T2→T3→T4→T5→T6 (domain model → persistence → API → resolver → commands).

## Impact Analysis

### Components Affected

| Component | Impact | Description |
|-----------|--------|-------------|
| `IIterationService` | Modified | Add `GetAllIterationsAsync()` method |
| `AdoIterationService` | Modified | Implement `GetAllIterationsAsync()` |
| `SqliteCacheStore` | Modified | Schema version bump (9→10) + new table DDL |
| `TwigServiceRegistration` | Modified | Register `ISprintIterationStore`, `SprintIterationResolver` |
| `Program.cs` | Modified | Register new commands, rewire `twig sprint` to `SprintCommand` |
| `HintEngine` | Modified | Add sprint-specific hints |
| `TwigJsonContext` | Possibly modified | If new DTOs needed (iteration list response already registered) |

### Backward Compatibility

- **Schema version bump**: Existing databases will be rebuilt on first run (all tables dropped and recreated, per `SqliteCacheStore.EnsureSchema()`). Users lose cache but it self-heals via `twig sync`.
- **Existing `twig sprint` command**: Currently delegates to `WorkspaceCommand`. Will be redirected to new `SprintCommand`. Output format is compatible (same `SprintHierarchy` rendering).
- **Existing `twig workspace`**: Completely unchanged.
- **Existing `twig refresh`**: Completely unchanged.

### Performance Implications

- **Relative expression resolution** adds one ADO API call per `twig sprint` invocation (fetching all iterations). Cached per CLI invocation via lazy initialization on `AdoIterationService`.
- **Multi-iteration queries** execute one SQLite query per configured iteration. For typical configs (2-3 iterations), this is negligible.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema version conflicts with parallel work (#1946) | Medium | Low | Coordinate schema version at merge time; both use the same bump pattern |
| ADO iteration list API may not return all iterations for large projects | Low | Medium | Limit to recent iterations if needed; add pagination support later |
| Items from non-current sprints not in cache (since refresh is unchanged) | Medium | Medium | `twig sprint` queries local cache first, shows warning if cache miss; users can `twig query --iterationPath` to populate |
| MCP sprint tool gets out of sync with CLI | Low | Low | Defer MCP enhancement; existing `twig_sprint` still works for current iteration |

## Open Questions

| # | Question | Severity | Status | Resolution |
|---|----------|----------|--------|------------|
| 1 | Should `twig refresh` be updated to populate cache for all configured sprint iterations? | Moderate | **Resolved** | No — refresh stays `@current`-only per user decision. Multi-iteration cache population is a future enhancement. |
| 2 | How should relative expression resolution handle the case where no iterations exist in ADO (fresh project)? | Low | Open | Return error result; `twig sprint` shows "No iterations configured in ADO." |
| 3 | Should `twig workspace sprint list` show resolved iteration paths alongside expressions? | Low | Open | Yes for human output, raw expressions only for JSON output. |
| 4 | When #1946 lands, should sprint iterations be managed via `IWorkspaceModeStore` or dedicated `ISprintIterationStore`? | Moderate | **Resolved** | Use dedicated `ISprintIterationStore` for now. Refactor to delegate to `IWorkspaceModeStore` when #1946 lands. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/IterationExpression.cs` | Validated value object for sprint iteration expressions (`@current`, `@current-1`, absolute paths) |
| `src/Twig.Domain/Interfaces/ISprintIterationStore.cs` | Persistence contract for sprint iteration configuration |
| `src/Twig.Domain/Services/SprintIterationResolver.cs` | Resolves expressions to `IterationPath` values and aggregates items across iterations |
| `src/Twig.Infrastructure/Persistence/SqliteSprintIterationStore.cs` | SQLite implementation of `ISprintIterationStore` |
| `src/Twig/Commands/SprintCommand.cs` | Dedicated `twig sprint` command with multi-iteration support and async rendering |
| `src/Twig/Commands/SprintConfigCommand.cs` | `twig workspace sprint add/remove/list` subcommands |
| `tests/Twig.Domain.Tests/ValueObjects/IterationExpressionTests.cs` | Unit tests for expression parsing and validation |
| `tests/Twig.Domain.Tests/Services/SprintIterationResolverTests.cs` | Unit tests for expression resolution and multi-iteration aggregation |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteSprintIterationStoreTests.cs` | Unit tests for SQLite store CRUD operations |
| `tests/Twig.Cli.Tests/Commands/SprintCommandTests.cs` | Unit tests for sprint command behavior (enabled/disabled, warnings, team view) |
| `tests/Twig.Cli.Tests/Commands/SprintConfigCommandTests.cs` | Unit tests for sprint config add/remove/list |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IIterationService.cs` | Add `GetAllIterationsAsync()` method signature returning `IReadOnlyList<IterationInfo>` |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Implement `GetAllIterationsAsync()` — call iterations API without `$timeframe` filter, sort by start date |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Add `sprint_iterations` table DDL, bump schema version 9→10, add table to `DropAllTables` list |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `ISprintIterationStore` → `SqliteSprintIterationStore`, `SprintIterationResolver` |
| `src/Twig/Program.cs` | Register `SprintCommand`, `SprintConfigCommand`; rewire `twig sprint` to `SprintCommand`; add `twig workspace sprint` subcommands |
| `src/Twig/Hints/HintEngine.cs` | Add sprint-specific hints (team view, sprint mode not enabled) |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `IterationInfo` if exposed through JSON output |

## ADO Work Item Structure

Since #1948 is an **Issue**, we define **Tasks** under it directly.

### Issue: #1948 — Sprint Mode

**Goal:** Implement sprint-related functionality in twig including multi-iteration support, relative iteration expressions, persistent sprint configuration, and a dedicated `twig sprint` read-only view command.

**Prerequisites:** None (self-contained; #1946 dependency decoupled per user decision)

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T1 | **IterationExpression value object & ISprintIterationStore interface** — Create `IterationExpression` value object with parsing for `@current`, `@current±N`, and absolute paths. Create `ExpressionKind` enum. Define `ISprintIterationStore` interface with CRUD methods. Create `IterationInfo` record for the all-iterations return type. Write comprehensive unit tests for expression parsing edge cases. | `src/Twig.Domain/ValueObjects/IterationExpression.cs`, `src/Twig.Domain/Interfaces/ISprintIterationStore.cs`, `tests/Twig.Domain.Tests/ValueObjects/IterationExpressionTests.cs` | ~200 LoC |
| T2 | **SqliteSprintIterationStore & schema update** — Implement `SqliteSprintIterationStore` with full CRUD (add, remove, exists, getAll, hasAny). Add `sprint_iterations` table to `SqliteCacheStore` DDL, add to `DropAllTables` list, and bump schema version 9→10. Register `ISprintIterationStore` in `TwigServiceRegistration`. Write unit tests exercising all CRUD operations and edge cases. | `src/Twig.Infrastructure/Persistence/SqliteSprintIterationStore.cs`, `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`, `src/Twig.Infrastructure/TwigServiceRegistration.cs`, `tests/Twig.Infrastructure.Tests/Persistence/SqliteSprintIterationStoreTests.cs` | ~300 LoC |
| T3 | **IIterationService.GetAllIterationsAsync() & implementation** — Add `GetAllIterationsAsync()` returning `IReadOnlyList<IterationInfo>` to `IIterationService`. Implement in `AdoIterationService` by calling `/_apis/work/teamsettings/iterations` without `$timeframe` filter. Parse response dates, sort by start date ascending. Add lazy caching. | `src/Twig.Domain/Interfaces/IIterationService.cs`, `src/Twig.Domain/ValueObjects/IterationExpression.cs` (add `IterationInfo` record here or in separate file), `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | ~100 LoC |
| T4 | **SprintIterationResolver domain service** — Implement resolution of relative expressions using all-iterations list + offset computation. Implement multi-iteration item aggregation with deduplication by work item ID. Handle edge cases: no iterations in ADO, out-of-bounds offset, mixed absolute/relative expressions. Write comprehensive unit tests with NSubstitute mocks. | `src/Twig.Domain/Services/SprintIterationResolver.cs`, `tests/Twig.Domain.Tests/Services/SprintIterationResolverTests.cs` | ~350 LoC |
| T5 | **SprintCommand (twig sprint)** — Dedicated sprint view command with sync rendering path (async Spectre deferred). Multi-iteration support when sprint mode enabled, single-iteration fallback with warning when not enabled. `--all` flag for team view. Uses existing `SprintHierarchy` and `IOutputFormatter`. Wire in `Program.cs`, replacing current delegation to `WorkspaceCommand`. Write unit tests for both enabled/disabled paths. | `src/Twig/Commands/SprintCommand.cs`, `src/Twig/Program.cs`, `tests/Twig.Cli.Tests/Commands/SprintCommandTests.cs` | ~400 LoC |
| T6 | **SprintConfigCommand (twig workspace sprint add/remove/list)** — Sprint configuration management subcommands. Validate expressions via `IterationExpression.Parse()`, check for duplicates on add, handle not-found on remove. `list` shows configured expressions with resolved paths in human output. Wire in `Program.cs`. Add sprint-specific hints to `HintEngine`. Write unit tests. | `src/Twig/Commands/SprintConfigCommand.cs`, `src/Twig/Program.cs`, `src/Twig/Hints/HintEngine.cs`, `tests/Twig.Cli.Tests/Commands/SprintConfigCommandTests.cs` | ~350 LoC |

**Acceptance Criteria:**
- [ ] Multiple iterations can be tracked simultaneously (e.g., `@current` + `@current-1`)
- [ ] Relative expressions resolve correctly when sprint changes
- [ ] `twig sprint` works both with and without sprint mode (with performance warning)
- [ ] Team view via `--all` shows all assignees' items grouped by assignee
- [ ] Sprint iteration configuration persists across CLI invocations
- [ ] All new types are AOT-compatible (no reflection)
- [ ] Unit tests cover parsing, resolution, persistence, and command behavior
- [ ] `twig refresh` is NOT modified
- [ ] Schema version bumped to 10

## PR Groups

### PG-1: Domain Foundation (T1 + T2 + T3)
**Type:** Deep
**Tasks:** T1 (IterationExpression + ISprintIterationStore), T2 (SqliteSprintIterationStore + schema), T3 (GetAllIterationsAsync)
**Description:** Introduces the core domain model and persistence layer for sprint iteration expressions. Includes the value object, interface, SQLite store, schema update, and the new `IIterationService` method. This is the foundation all other work depends on.
**Estimated LoC:** ~600
**Files:** ~9 (4 new production, 2 new test, 3 modified)
**Successor:** PG-2

### PG-2: Sprint Commands & Resolver (T4 + T5 + T6)
**Type:** Deep
**Tasks:** T4 (SprintIterationResolver), T5 (SprintCommand), T6 (SprintConfigCommand)
**Description:** Implements the domain service for resolving iteration expressions and aggregating items, plus both CLI commands (`twig sprint` and `twig workspace sprint add/remove/list`). Includes hint engine updates and all command tests.
**Estimated LoC:** ~1100
**Files:** ~10 (3 new production, 3 new test, 4 modified)
**Predecessor:** PG-1

**Total Estimated LoC:** ~1700
**Total Files:** ~19

## References

- [ADO Team Settings Iterations API](https://learn.microsoft.com/en-us/rest/api/azure/devops/work/iterations/list)
- Issue #1946 — Workspace Mode Infrastructure (future refactor target)
- Issue #1945 — Workspace Modes & Tracking (parent epic)
