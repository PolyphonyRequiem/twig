# Workspace Model Alignment — Sprint, Area, and Tracking Overhaul

> **Epic:** #2146 · **Status:** Draft · **Revision:** 1

## Executive Summary

The twig workspace model has diverged from the functional specification in `docs/specs/working-set-sync.spec.md`. The core violation: sprint items are implicitly included by calling `IIterationService.GetCurrentIterationAsync()` on every refresh, rather than requiring explicit configuration. This epic brings the implementation into alignment with the spec's core principle — **nothing in the workspace is implicit; all sources are explicitly configured by the user**.

The overhaul spans five coordinated changes: (1) a new sprint iteration management subsystem with relative reference resolution (`@Current`, `@Current±N`), (2) moving area commands under the `workspace` namespace with backward-compatible aliases, (3) migrating tracking persistence from SQLite to a file-backed JSON store that survives DB rebuilds, (4) expanding tree-tracking sync to include parent chains and link targets, and (5) integrating workspace preferences into `twig init`. Non-interactive init starts empty; interactive init prompts the user about area and sprint preferences.

---

## Background

### Current Architecture

The workspace displays work items from three sources, but the sprint source is **hardcoded**:

| Source | How It Works Today | Problem |
|--------|-------------------|---------|
| Sprint items | `RefreshCommand` calls `iterationService.GetCurrentIterationAsync()` → builds WIQL `WHERE [System.IterationPath] = '{path}'` | Single implicit iteration; user cannot add/remove sprints |
| Area paths | Stored in `.twig/config → defaults.areaPathEntries[]`; managed via `twig area add/remove/list/sync` | Commands at wrong namespace level |
| Tracked items | SQLite `tracked_items` and `excluded_items` tables via `SqliteTrackingRepository` | Lost on `twig init --force` (DB rebuild destroys tables) |

### Key Components Affected

| Component | File | Current Role |
|-----------|------|-------------|
| `RefreshCommand` | `src/Twig/Commands/RefreshCommand.cs` | Builds WIQL with hardcoded single iteration |
| `WorkspaceCommand` | `src/Twig/Commands/WorkspaceCommand.cs` | Renders workspace; calls `GetCurrentIterationAsync()` directly |
| `InitCommand` | `src/Twig/Commands/InitCommand.cs` | No sprint/area preference prompts |
| `AreaCommand` | `src/Twig/Commands/AreaCommand.cs` | Top-level `twig area` namespace |
| `TrackingCommand` | `src/Twig/Commands/TrackingCommand.cs` | Already under `workspace` namespace |
| `SqliteTrackingRepository` | `src/Twig.Infrastructure/Persistence/SqliteTrackingRepository.cs` | SQLite-backed tracking |
| `TrackingService` | `src/Twig.Domain/Services/Sync/TrackingService.cs` | Orchestrates tracking + cleanup |
| `WorkingSetService` | `src/Twig.Domain/Services/Workspace/WorkingSetService.cs` | Computes working set from single iteration |
| `RefreshOrchestrator` | `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs` | Refresh lifecycle management |
| `TwigConfiguration` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | No sprint config; `WorkspaceConfig` only has `WorkingLevel` |
| `AdoIterationService` | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Only `GetCurrentIterationAsync()`; no team timeline API |
| `TwigServiceRegistration` | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Registers `SqliteTrackingRepository` |
| `TwigJsonContext` | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | No tracking file types registered |
| `SyncCoordinator` | `src/Twig.Domain/Services/Sync/SyncCoordinator.cs` | Tree sync: root + children only |
| `TwigCommands` | `src/Twig/Program.cs` | Command routing; no `workspace area/sprint` subcommands |

### Call-Site Audit: `GetCurrentIterationAsync()`

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `RefreshCommand.cs` | `ExecuteCoreAsync` | WIQL `WHERE IterationPath = '{path}'` | Must become multi-sprint WIQL |
| `WorkspaceCommand.cs` | `ExecuteAsync` (Spectre) | `GetCurrentIterationAsync()` → `GetByIterationAsync` | Must use configured sprints |
| `WorkspaceCommand.cs` | `ExecuteSyncAsync` | Same pattern | Must use configured sprints |
| `WorkspaceCommand.cs` | stale-while-revalidate | Re-fetches fresh iteration | Must use configured sprints |
| `RefreshOrchestrator.cs` | `ApplyCleanupPolicyAsync` | For cleanup policy | Stays single (policy unchanged) |
| `WorkingSetService.cs` | `ComputeAsync` | Falls back to single iteration | Must accept multiple iterations |
| `InitCommand.cs` | `ExecuteCoreAsync` | Inline refresh WIQL | Use configured sprints or skip |
| `ReadTools.cs` (MCP) | `Workspace` tool | Sprint items query | Must use configured sprints |
| `NavigationTools.cs` (MCP) | `Sprint` tool | Iteration info display | Stays single iteration |
| `SyncCommand.cs` | `ExecuteAsync` | Delegates to `RefreshCommand` | Indirect — inherits changes |

### Call-Site Audit: `ITrackingRepository`

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `TrackingService.cs` | All methods | Primary CRUD consumer | Unchanged — uses interface |
| `WorkingSetService.cs` | `ComputeAsync` | `GetAllTrackedAsync()` | Unchanged — uses interface |
| `WorkspaceContextFactory.cs` (MCP) | Factory | `new SqliteTrackingRepository()` | Switch to `FileTrackingRepository` |
| `TwigServiceRegistration.cs` | DI setup | `new SqliteTrackingRepository()` | Switch to `FileTrackingRepository` |

---

## Problem Statement

1. **Implicit sprint inclusion** — Users cannot control which sprints appear in their workspace. The refresh always queries the ADO team's current iteration, violating the spec's explicit-configuration principle.

2. **Area command namespace misalignment** — Area commands exist at `twig area` instead of `twig workspace area`, inconsistent with tracking commands already under `workspace`.

3. **Fragile tracking persistence** — Tracked items/exclusions live in SQLite tables destroyed by `twig init --force` and schema version bumps (drop-and-recreate strategy).

4. **Incomplete tree sync** — Tree-tracked items only sync children downward. The spec requires syncing parents upward and fetching link targets one level deep.

5. **Missing init integration** — `twig init` doesn't prompt for workspace preferences. Interactive init should ask; non-interactive starts empty.

---

## Goals and Non-Goals

### Goals

1. Sprint iterations explicitly configured via `workspace.sprints[]` in `.twig/config`
2. Relative references (`@Current`, `@Current±N`) resolve at refresh time against ADO team timeline
3. Sprint items appear ONLY when at least one sprint is configured; empty → no sprint items
4. Area commands at `twig workspace area` with `twig area` as deprecated aliases
5. Tracking persists in `.twig/tracking.json` (gitignored), survives DB rebuilds
6. One-time automatic migration from SQLite tracking tables to JSON
7. Tree-tracked items sync parents upward and link targets one level deep
8. Interactive `twig init` prompts for workspace preferences (area, sprint, both, neither)
9. Non-interactive `twig init` starts with empty workspace

### Non-Goals

- Multi-team sprint support (single team per workspace)
- Cleanup policy changes (existing policies stay)
- Working-set eviction policy changes
- Changes to `twig sprint` read-only view behavior
- Stash integration
- Process template assumptions
- Changes to MCP `twig_sprint` tool (stays single-iteration info)

---

## Requirements

### Functional

| ID | Requirement |
|----|------------|
| FR-1 | `twig workspace sprint add <expr>` adds a sprint expression to config |
| FR-2 | `twig workspace sprint remove <expr>` removes a sprint expression |
| FR-3 | `twig workspace sprint list` displays configured sprints with resolved paths |
| FR-4 | Relative expressions resolve against ADO team iteration timeline at refresh time |
| FR-5 | Absolute iteration paths stored and used verbatim |
| FR-6 | Refresh builds multi-sprint WIQL with OR-joined iteration clauses |
| FR-7 | No sprint items when `workspace.sprints[]` is empty |
| FR-8 | `twig workspace area add/remove/list/sync` mirror existing `twig area` behavior |
| FR-9 | Existing `twig area` commands continue as hidden deprecated aliases |
| FR-10 | Tracking data in `.twig/tracking.json` with `{ tracked: [...], excluded: [...] }` schema |
| FR-11 | First access migrates SQLite tracking data to JSON file |
| FR-12 | Tracking file uses atomic writes (temp + rename) |
| FR-13 | Tree sync fetches parents recursively upward |
| FR-14 | Tree sync fetches link targets one level deep for root item |
| FR-15 | Link metadata stored in `work_item_links`; linked items NOT fetched into working set |
| FR-16 | Interactive init prompts: area / sprint / both / neither preference |
| FR-17 | Non-interactive init starts empty; `--sprint` and `--area` flags for explicit config |

### Non-Functional

| ID | Requirement |
|----|------------|
| NFR-1 | All new types registered in `TwigJsonContext` for AOT |
| NFR-2 | No reflection; source-gen only |
| NFR-3 | Tracking file handles concurrent access (retry on lock) |
| NFR-4 | Sprint resolution adds at most one ADO API call per refresh |
| NFR-5 | Telemetry must not include iteration/area paths or work item identifiers |

---

## Proposed Design

### Architecture Overview

```
                   ┌─────────────────────────────────────────────────┐
                   │                  CLI Layer                      │
                   │                                                 │
                   │  SprintCommand   AreaCommand   TrackingCommand  │
                   │  InitCommand     WorkspaceCommand               │
                   │  RefreshCommand  SyncCommand                    │
                   └──────────────────────┬──────────────────────────┘
                                          │
                   ┌──────────────────────▼──────────────────────────┐
                   │               Domain Layer                      │
                   │                                                 │
                   │  SprintIterationResolver  (new)                 │
                   │  TrackingService          (existing)            │
                   │  WorkingSetService        (modified)            │
                   │  RefreshOrchestrator      (modified)            │
                   │  SyncCoordinator          (modified for tree)   │
                   └──────────────────────┬──────────────────────────┘
                                          │
                   ┌──────────────────────▼──────────────────────────┐
                   │            Infrastructure Layer                 │
                   │                                                 │
                   │  FileTrackingRepository   (new — tracking.json) │
                   │  AdoIterationService      (extended — timeline) │
                   │  TwigConfiguration        (extended — sprints)  │
                   │  TwigJsonContext           (extended — models)   │
                   └─────────────────────────────────────────────────┘
```

### Key Components

#### 1. Sprint Configuration Model

**`SprintEntry`** — config POCO for a sprint expression in `.twig/config`:

```csharp
public sealed class SprintEntry
{
    public string Expression { get; set; } = string.Empty;  // "@Current", "@Current-1", "Project\Sprint 5"
}
```

**`WorkspaceConfig`** — extended with `Sprints` list:

```csharp
public sealed class WorkspaceConfig
{
    public string? WorkingLevel { get; set; }
    public List<SprintEntry>? Sprints { get; set; }  // NEW
}
```

Config JSON representation:
```json
{
  "workspace": {
    "workingLevel": "Task",
    "sprints": [
      { "expression": "@Current" },
      { "expression": "@Current-1" }
    ]
  }
}
```

#### 2. Sprint Iteration Resolver

**`SprintIterationResolver`** — domain service that resolves sprint expressions to concrete `IterationPath` values at refresh time.

```csharp
public sealed class SprintIterationResolver
{
    // Resolves all configured sprint expressions against the team iteration timeline.
    // Returns empty list if no sprints configured.
    // Relative expressions resolved by finding @Current in the timeline then offsetting.
    public async Task<IReadOnlyList<IterationPath>> ResolveAsync(
        IReadOnlyList<SprintEntry> entries,
        IIterationService iterationService,
        CancellationToken ct = default);
}
```

Resolution logic:
- `@Current` → calls `GetCurrentIterationAsync()` (existing)
- `@Current±N` → calls new `GetTeamIterationsAsync()` → finds current index → offsets
- Absolute paths → parsed directly via `IterationPath.Parse()`
- Out-of-range offsets (e.g., `@Current+99`) → silently skipped with stderr warning

#### 3. Team Iterations API Extension

**`IIterationService`** — extended with new method:

```csharp
Task<IReadOnlyList<TeamIteration>> GetTeamIterationsAsync(CancellationToken ct = default);
```

**`TeamIteration`** — value object for iteration timeline entries:

```csharp
public sealed record TeamIteration(string Path, DateTimeOffset? StartDate, DateTimeOffset? EndDate);
```

**`AdoIterationService`** — implements via `GET /{project}/{team}/_apis/work/teamsettings/iterations?api-version=7.1` (no `$timeframe` filter → returns all team iterations).

#### 4. File-Backed Tracking Repository

**`FileTrackingRepository`** — implements `ITrackingRepository` backed by `.twig/tracking.json`:

```csharp
public sealed class FileTrackingRepository : ITrackingRepository
{
    private readonly string _filePath;
    private TrackingFile? _cached;

    // Lazy load on first access. Auto-migrates from SQLite if file doesn't exist.
    // Atomic writes: serialize → write temp → rename.
}
```

**`TrackingFile`** — POCO for the JSON schema:

```csharp
public sealed class TrackingFile
{
    public List<TrackingFileEntry> Tracked { get; set; } = [];
    public List<ExclusionFileEntry> Excluded { get; set; } = [];
}

public sealed class TrackingFileEntry
{
    public int Id { get; set; }
    public string Mode { get; set; } = "single";
    public string AddedAt { get; set; } = string.Empty;
}

public sealed class ExclusionFileEntry
{
    public int Id { get; set; }
    public string AddedAt { get; set; } = string.Empty;
}
```

**Migration strategy**: On first `FileTrackingRepository` access, if `tracking.json` doesn't exist:
1. Check if SQLite DB exists and has `tracked_items`/`excluded_items` tables with data
2. If yes → read all rows → write to `tracking.json` → log migration count
3. If no → create empty file (or defer creation to first write)
4. SQLite tables are left inert (no deletion; schema drop-recreate handles cleanup)

#### 5. Multi-Sprint WIQL Builder

`RefreshCommand` WIQL construction changes from:
```sql
WHERE [System.IterationPath] = '{singlePath}'
```
To:
```sql
WHERE ([System.IterationPath] = '{path1}' OR [System.IterationPath] = '{path2}')
```

When no sprints configured, the iteration clause is omitted entirely. The area path clause remains unchanged.

#### 6. Tree Sync Expansion

`TrackingService.SyncTrackedTreesAsync()` enhanced with two new phases:

**Phase: Parents Up** — after syncing the root item, walk `ParentId` upward until null:
```
for each tree-tracked root:
  1. SyncItemAsync(root)           ← existing
  2. SyncParentsUpAsync(root)      ← NEW: fetch parent chain recursively
  3. SyncChildrenAsync(root)       ← existing
  4. SyncRootLinksAsync(root)      ← NEW: fetch link targets one level
```

**Phase: Root Links** — fetch link targets for the root item only:
- Query `work_item_links` WHERE `source_id = root.Id`
- For each link target: `SyncItemAsync(targetId)` — materialized into cache
- Link targets are NOT recursed further (one level deep only)

#### 7. Init Workspace Preferences

Interactive mode adds a workspace preference prompt after the existing workspace mode prompt:

```
Workspace sources — what should be included in your workspace?
  1. Sprint only (@Current)
  2. Area paths only (sync from team)
  3. Both sprint and area paths
  4. Neither (start empty, configure later)
Choose [1-4] (4):
```

Default is `4` (neither / start empty) per user input.

Non-interactive mode:
- `--sprint @Current` → adds sprint entry
- `--area sync` → runs area sync
- No flags → starts empty (no sprints, no areas)

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Sprint config in `.twig/config`, not SQLite | Config is the right home for user-configured workspace sources. Matches area path storage pattern. Survives DB rebuild. |
| DD-2 | Store expressions (not resolved paths) | `@Current` auto-resolves when sprints roll over. No manual config rotation needed. |
| DD-3 | Tracking in JSON file, not SQLite | User-local state must survive `init --force`. File is gitignored. Matches spec. |
| DD-4 | Atomic file writes for tracking.json | Prevents corruption from interrupted writes. Standard pattern: write temp → rename. |
| DD-5 | `ITrackingRepository` interface unchanged | `FileTrackingRepository` is a drop-in replacement. No domain/service changes needed. |
| DD-6 | SQLite tracking tables left inert | Schema drop-recreate handles cleanup. No explicit migration code to remove them. |
| DD-7 | Team iterations fetched once per refresh | Cached within `SprintIterationResolver` call. Single API call resolves all relative expressions. |
| DD-8 | Non-interactive init starts empty | Per user input. Only user in the system; explicit configuration preferred. |
| DD-9 | Interactive init default is "neither" | User is the only consumer; start empty and configure explicitly. |
| DD-10 | Link targets materialized into cache | Spec says "fetch link targets ONE level deep" — items are saved to work_items table for display. |
| DD-11 | Area command aliases use `[Hidden]` attribute | Backward compat without cluttering help text. Same pattern as `save`/`refresh`. |

---

## Alternatives Considered

### Sprint Storage: SQLite vs Config

The earlier `sprint-mode.plan.md` stored sprint expressions in a `sprint_iterations` SQLite table (which still exists in the schema, unused). Moving to `.twig/config` was chosen because:
- **Pro config**: Matches the area path storage pattern (consistency), survives DB rebuilds, can be version-controlled
- **Pro SQLite**: Transactional, better for high-frequency writes
- **Decision**: Config wins — sprint expressions change infrequently (user adds/removes interactively)

### Tracking Storage: File vs SQLite with Backup

An alternative was keeping SQLite but backing up tracking data before `init --force`. File-backed was chosen because:
- **Pro file**: Naturally gitignored, simple schema, no migration coordination with schema bumps
- **Pro SQLite backup**: Zero code change for reads; only backup/restore logic needed
- **Decision**: File wins — cleaner separation of concerns, spec explicitly calls for `.twig/tracking.json`

---

## Dependencies

### External

- ADO REST API: `GET /{project}/{team}/_apis/work/teamsettings/iterations` (all iterations, no timeframe filter) — already used with `$timeframe=current`; removing filter returns full timeline
- `work_item_links` table exists in schema v10 (no schema bump needed)

### Internal

- `ITrackingRepository` interface — unchanged, new implementation only
- `IIterationService` interface — extended with one new method
- `TwigConfiguration` / `WorkspaceConfig` — extended with `Sprints` list
- `TwigJsonContext` — new type registrations

### Sequencing

- Issue 1 (Sprint Infrastructure) must complete before Issue 3 (Multi-Sprint Refresh) and Issue 6 (Init)
- Issues 2, 4, 5 are independent and can proceed in parallel

---

## Impact Analysis

### Backward Compatibility

| Change | Impact | Mitigation |
|--------|--------|------------|
| No implicit sprint items | Existing workspaces will show empty sprint section after upgrade | Hint: "No sprints configured. Run `twig workspace sprint add @Current`" |
| Area commands moved | `twig area add` still works | Hidden aliases preserved; deprecation warning on stderr |
| Tracking file migration | One-time automatic migration | Transparent; log message on migration |

### Performance

| Change | Impact |
|--------|--------|
| `GetTeamIterationsAsync()` | One additional API call per refresh when relative expressions exist |
| File-based tracking | Negligible — small JSON file, lazy loaded, cached in memory |
| Parent chain sync | Additional API calls proportional to hierarchy depth (typically 2-4 levels) |
| Link target sync | One batch fetch for root links per tree-tracked item |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Team iteration API returns unexpected order | Low | Medium | Sort by start date; validate current iteration presence |
| Tracking migration loses data | Low | High | Migration is additive (read SQLite → write JSON); SQLite data left intact |
| Multi-sprint WIQL performance with many sprints | Low | Low | Practical limit is 3-5 sprints; WIQL OR-clauses are well-optimized |
| Concurrent tracking file access (MCP + CLI) | Medium | Medium | Atomic writes + retry-on-lock pattern |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should the `sprint_iterations` SQLite table DDL be removed from schema v10 or left inert? | Low | Leave inert — no cost, avoids schema bump |
| 2 | Should `twig workspace sprint list` show resolution errors inline or skip silently? | Low | Show inline with warning prefix |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/TeamIteration.cs` | Value object for iteration timeline entry |
| `src/Twig.Domain/ValueObjects/SprintEntry.cs` | Config POCO for sprint expression |
| `src/Twig.Domain/Services/Workspace/SprintIterationResolver.cs` | Resolves sprint expressions to IterationPath values |
| `src/Twig.Infrastructure/Persistence/FileTrackingRepository.cs` | File-backed ITrackingRepository implementation |
| `src/Twig.Infrastructure/Persistence/TrackingFile.cs` | POCO models for tracking.json schema |
| `src/Twig/Commands/SprintCommand.cs` | CLI command for `workspace sprint add/remove/list` |
| `tests/Twig.Domain.Tests/Services/Workspace/SprintIterationResolverTests.cs` | Unit tests for sprint resolution |
| `tests/Twig.Infrastructure.Tests/Persistence/FileTrackingRepositoryTests.cs` | Unit tests for file tracking |
| `tests/Twig.Cli.Tests/Commands/SprintCommandTests.cs` | CLI integration tests for sprint commands |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `Sprints` list to `WorkspaceConfig`; add `SprintEntry` class; add `SetValue` cases for `workspace.sprints` |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Register `SprintEntry`, `List<SprintEntry>`, `TrackingFile`, `TrackingFileEntry`, `ExclusionFileEntry` |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Replace `SqliteTrackingRepository` with `FileTrackingRepository` in DI |
| `src/Twig.Infrastructure/Config/TwigPaths.cs` | Add `TrackingFilePath` property (`{TwigDir}/tracking.json`) |
| `src/Twig.Domain/Interfaces/IIterationService.cs` | Add `GetTeamIterationsAsync()` method |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Implement `GetTeamIterationsAsync()` |
| `src/Twig/Commands/RefreshCommand.cs` | Replace single-iteration WIQL with multi-sprint WIQL from resolver |
| `src/Twig/Commands/WorkspaceCommand.cs` | Replace `GetCurrentIterationAsync()` calls with configured-sprint resolution |
| `src/Twig/Commands/InitCommand.cs` | Add workspace preference prompts; add `--sprint`/`--area` flags; non-interactive starts empty |
| `src/Twig/Program.cs` | Add `workspace sprint add/remove/list` commands; add `workspace area add/remove/list/sync` aliases; update `GroupedHelp` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `SprintCommand` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `SprintIterationResolver` |
| `src/Twig.Domain/Services/Sync/TrackingService.cs` | Add parent-up sync and root-link sync to `SyncTrackedTreesAsync()` |
| `src/Twig.Domain/Services/Sync/SyncCoordinator.cs` | Add `SyncParentChainAsync()` method |
| `src/Twig.Domain/Services/Workspace/WorkingSetService.cs` | Accept multiple iteration paths |
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `GetByIterationsAsync()` for multi-iteration query |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement `GetByIterationsAsync()` |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Use configured sprints for workspace tool |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Switch to `FileTrackingRepository` |

---

## ADO Work Item Structure

**Epic:** #2146 — Workspace Model Alignment

### Issue 1: Sprint Iteration Infrastructure

**Goal:** Establish the foundational sprint configuration model, iteration resolver, and team iteration API integration so that sprint iterations can be explicitly configured and resolved.

**Prerequisites:** None (foundational)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 1.1 | Create `SprintEntry` config model and extend `WorkspaceConfig` with `Sprints` list | `TwigConfiguration.cs`, `TwigJsonContext.cs` | S |
| 1.2 | Create `TeamIteration` value object | `TeamIteration.cs` | S |
| 1.3 | Add `GetTeamIterationsAsync()` to `IIterationService` and implement in `AdoIterationService` | `IIterationService.cs`, `AdoIterationService.cs`, `TwigJsonContext.cs` | M |
| 1.4 | Create `SprintIterationResolver` domain service with relative expression parsing | `SprintIterationResolver.cs`, tests | M |
| 1.5 | Create `SprintCommand` (add/remove/list) under `twig workspace sprint` | `SprintCommand.cs`, `Program.cs`, `CommandRegistrationModule.cs`, tests | M |
| 1.6 | Add `SetValue` cases for `workspace.sprints` in `TwigConfiguration` | `TwigConfiguration.cs` | S |

**Acceptance Criteria:**
- [ ] `twig workspace sprint add @Current` persists to config
- [ ] `twig workspace sprint add @Current-1` persists to config
- [ ] `twig workspace sprint add "Project\Sprint 5"` persists with absolute path
- [ ] `twig workspace sprint remove @Current` removes the entry
- [ ] `twig workspace sprint list` shows configured sprints
- [ ] `SprintIterationResolver` correctly resolves `@Current`, `@Current±N`, and absolute paths
- [ ] `GetTeamIterationsAsync()` returns ordered team iterations from ADO

### Issue 2: Area Command Namespace Migration

**Goal:** Move area commands under `twig workspace area` while preserving backward compatibility via hidden aliases.

**Prerequisites:** None (independent)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 2.1 | Add `workspace area add/remove/list/sync` command registrations in `Program.cs` | `Program.cs` | S |
| 2.2 | Mark existing `area add/remove/list/sync` as `[Hidden]` deprecation aliases | `Program.cs` | S |
| 2.3 | Update `GroupedHelp` known commands and help text | `Program.cs` | S |
| 2.4 | Add deprecation warning to old `area` command aliases | `AreaCommand.cs` or wrapper | S |
| 2.5 | Update command examples for area commands | `CommandExamples.cs` | S |

**Acceptance Criteria:**
- [ ] `twig workspace area add <path>` works identically to old `twig area add <path>`
- [ ] `twig area add <path>` still works but is hidden from help
- [ ] `twig workspace area list/remove/sync` all functional
- [ ] Help text shows area commands under Workspace section

### Issue 3: Multi-Sprint Refresh and Workspace Integration

**Goal:** Replace the hardcoded single-iteration refresh with multi-sprint WIQL that uses all configured sprints, and update the workspace display accordingly.

**Prerequisites:** Issue 1 (Sprint Infrastructure)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 3.1 | Refactor `RefreshCommand.ExecuteCoreAsync()` to use `SprintIterationResolver` | `RefreshCommand.cs` | M |
| 3.2 | Build multi-sprint WIQL with OR-joined iteration clauses; skip iteration clause when empty | `RefreshCommand.cs` | M |
| 3.3 | Update `WorkspaceCommand` to use resolved sprints instead of `GetCurrentIterationAsync()` | `WorkspaceCommand.cs` | L |
| 3.4 | Add `GetByIterationsAsync()` to `IWorkItemRepository` and implement in SQLite | `IWorkItemRepository.cs`, `SqliteWorkItemRepository.cs` | M |
| 3.5 | Update `WorkingSetService.ComputeAsync()` to accept multiple iteration paths | `WorkingSetService.cs` | M |
| 3.6 | Update MCP `twig_workspace` tool to use configured sprints | `ReadTools.cs` (MCP), `WorkspaceContextFactory.cs` | M |
| 3.7 | Update `InitCommand` inline refresh to use configured sprints (or skip if empty) | `InitCommand.cs` | S |

**Acceptance Criteria:**
- [ ] Refresh with `@Current` + `@Current-1` fetches items from both sprints
- [ ] Empty `workspace.sprints[]` → no sprint items in workspace or refresh
- [ ] Workspace view shows items from all configured sprints
- [ ] MCP workspace tool returns items from configured sprints
- [ ] Init inline refresh respects configured sprint (or skips when empty)

### Issue 4: Tracking Persistence Migration

**Goal:** Move tracking data from SQLite tables to `.twig/tracking.json` so it survives DB rebuilds.

**Prerequisites:** None (independent)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 4.1 | Create `TrackingFile`, `TrackingFileEntry`, `ExclusionFileEntry` POCO models | `TrackingFile.cs`, `TwigJsonContext.cs` | S |
| 4.2 | Implement `FileTrackingRepository` with atomic writes and lazy loading | `FileTrackingRepository.cs` | L |
| 4.3 | Add `TrackingFilePath` property to `TwigPaths` | `TwigPaths.cs` | S |
| 4.4 | Implement one-time SQLite→JSON migration in `FileTrackingRepository` constructor | `FileTrackingRepository.cs` | M |
| 4.5 | Update DI registration: replace `SqliteTrackingRepository` with `FileTrackingRepository` | `TwigServiceRegistration.cs` | S |
| 4.6 | Update MCP `WorkspaceContextFactory` to use `FileTrackingRepository` | `WorkspaceContextFactory.cs` | S |
| 4.7 | Write comprehensive tests for FileTrackingRepository including migration | Tests | M |

**Acceptance Criteria:**
- [ ] `twig workspace track <id>` persists to `tracking.json`
- [ ] `twig init --force` does NOT lose tracking data
- [ ] Existing SQLite tracking data migrated transparently
- [ ] Concurrent CLI + MCP access doesn't corrupt the file
- [ ] All existing tracking tests pass with new implementation

### Issue 5: Tree Tracking Sync Expansion

**Goal:** Extend tree-tracked item sync to fetch parents upward and link targets one level deep.

**Prerequisites:** None (independent)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 5.1 | Add `SyncParentChainAsync(int rootId)` to `SyncCoordinator` | `SyncCoordinator.cs` | M |
| 5.2 | Update `TrackingService.SyncTrackedTreesAsync()` to call parent-up sync after root sync | `TrackingService.cs` | S |
| 5.3 | Add root-link sync: query `work_item_links` for root, fetch link targets via `SyncItemAsync` | `TrackingService.cs` | M |
| 5.4 | Store link metadata (source, target, type) in `work_item_links` table (already exists) | `TrackingService.cs`, `IWorkItemLinkRepository.cs` | S |
| 5.5 | Write tests for parent-up sync and link-target sync | Tests | M |

**Acceptance Criteria:**
- [ ] Tree-tracking a child item syncs all ancestors up to root
- [ ] Tree-tracking syncs successor/predecessor/related links one level for root
- [ ] Link targets are materialized in the cache (viewable via `twig show`)
- [ ] Link metadata stored in `work_item_links` table
- [ ] Non-root items' links are stored as metadata only (no target fetch)

### Issue 6: Init Integration

**Goal:** Add workspace preference prompts to interactive `twig init` and `--sprint`/`--area` flags for non-interactive mode.

**Prerequisites:** Issue 1 (Sprint Infrastructure)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 6.1 | Add workspace preference prompt to interactive init (area/sprint/both/neither) | `InitCommand.cs` | M |
| 6.2 | Add `--sprint <expr>` and `--area <path>` flags to non-interactive init | `InitCommand.cs`, `Program.cs` | M |
| 6.3 | Remove automatic area sync and inline refresh from default init flow | `InitCommand.cs` | M |
| 6.4 | Non-interactive mode starts empty unless flags provided | `InitCommand.cs` | S |
| 6.5 | Write tests for init preference flows | Tests | M |

**Acceptance Criteria:**
- [ ] Interactive init shows "Sprint / Area / Both / Neither" prompt (default: Neither)
- [ ] Choosing "Sprint" adds `@Current` to `workspace.sprints[]`
- [ ] Choosing "Area" runs area sync from team settings
- [ ] Choosing "Both" does both
- [ ] Choosing "Neither" skips both (empty workspace)
- [ ] Non-interactive `twig init org proj` starts with empty workspace
- [ ] Non-interactive `twig init org proj --sprint @Current --area sync` configures both

---

## PR Groups

### PG-1: Sprint Infrastructure + Area Refactor

**Type:** Deep  
**Issues:** Issue 1 (Sprint Infrastructure) + Issue 2 (Area Namespace Migration)  
**Estimated LoC:** ~1,200  
**Estimated Files:** ~20  
**Successor:** PG-2

**Contents:**
- `SprintEntry` config model + `WorkspaceConfig` extension
- `TeamIteration` value object
- `GetTeamIterationsAsync()` API method
- `SprintIterationResolver` domain service
- `SprintCommand` (add/remove/list)
- `workspace area` command aliases + hidden `area` deprecation
- All unit tests for sprint resolution and area migration
- `TwigJsonContext` registrations for new config types

**Review focus:** Sprint expression parsing correctness, iteration timeline resolution edge cases, backward compatibility of area aliases.

### PG-2: Refresh Integration + Tracking Migration + Tree Sync + Init

**Type:** Wide  
**Issues:** Issue 3 (Multi-Sprint Refresh) + Issue 4 (Tracking Migration) + Issue 5 (Tree Sync) + Issue 6 (Init)  
**Estimated LoC:** ~1,600  
**Estimated Files:** ~30  
**Predecessor:** PG-1

**Contents:**
- Multi-sprint WIQL in `RefreshCommand`
- `WorkspaceCommand` multi-sprint rendering
- `GetByIterationsAsync()` repository method
- `WorkingSetService` multi-iteration support
- `FileTrackingRepository` + `TrackingFile` models
- SQLite→JSON migration logic
- DI registration changes
- Tree sync parent-up + link-target expansion
- Init workspace preference prompts
- Init `--sprint`/`--area` flags
- MCP workspace tool updates
- All unit + integration tests

**Review focus:** Multi-sprint WIQL correctness, tracking migration data integrity, atomic file write safety, init flow UX.

---

## References

- [Working Set & Sync — Functional Specification](../specs/working-set-sync.spec.md)
- [ADO Team Iterations API](https://learn.microsoft.com/en-us/rest/api/azure/devops/work/iterations/list)
- [TwigJsonContext AOT serialization pattern](../../src/Twig.Infrastructure/Serialization/TwigJsonContext.cs)
- [Sprint Mode Plan (superseded)](sprint-mode.plan.md)
- [Manual Track/Untrack Overlay Plan (superseded)](manual-track-untrack-overlay.plan.md)
- [Area Mode Plan](area-mode.plan.md)

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Depends On | Type | Est. LoC | Est. Files |
|-------|------|----------------|------------|------|----------|------------|
| PG-1 | `PG-1-sprint-infra-area-refactor` | Issue 1 (1.1–1.6), Issue 2 (2.1–2.5) | — | Deep | ~1,200 | ~20 |
| PG-2 | `PG-2-refresh-tracking-treesync-init` | Issue 3 (3.1–3.7), Issue 4 (4.1–4.7), Issue 5 (5.1–5.5), Issue 6 (6.1–6.5) | PG-1 | Wide | ~1,600 | ~30 |

### Execution Order

**PG-1 → PG-2** (strictly sequential)

**PG-1: Sprint Infrastructure + Area Refactor** is foundational and must land first. It introduces all the types and services that PG-2 depends on: `SprintEntry`, `WorkspaceConfig.Sprints`, `TeamIteration`, `SprintIterationResolver`, and `SprintCommand`. It also completes the area namespace migration (Issues 1 and 2), which are independent of each other but both self-contained within PG-1. No deferred types or partial interfaces — PG-1 builds, passes linting, and tests green in isolation.

**PG-2: Refresh Integration + Tracking Migration + Tree Sync + Init** is the consumer layer. It wires up `SprintIterationResolver` into `RefreshCommand`, `WorkspaceCommand`, and `InitCommand` (Issue 3 and 6, dependent on PG-1), and independently delivers `FileTrackingRepository` + SQLite→JSON migration (Issue 4) and tree sync parent/link expansion (Issue 5). Although Issues 4 and 5 have no dependency on PG-1, grouping them here keeps the PR size healthy and avoids a third merge round. PG-2 requires PG-1 to be merged before its branch is created.

### Validation Strategy

#### PG-1
- `dotnet build` on `Twig.slnx` — must have zero errors and zero warnings
- `dotnet test tests/Twig.Domain.Tests` — `SprintIterationResolverTests` all green
- `dotnet test tests/Twig.Infrastructure.Tests` — `AdoIterationService` team-iterations tests green
- `dotnet test tests/Twig.Cli.Tests` — `SprintCommandTests` all green
- Manual smoke: `twig workspace sprint add @Current` → config persists; `twig workspace sprint list` → displays entry
- Manual smoke: `twig area add <path>` still works with hidden-alias deprecation warning on stderr
- Manual smoke: `twig workspace area list` shows area entries

#### PG-2
- `dotnet build` on `Twig.slnx` — zero errors, zero warnings
- `dotnet test tests/Twig.Domain.Tests` — `WorkingSetServiceTests`, `TrackingServiceTests` all green
- `dotnet test tests/Twig.Infrastructure.Tests` — `FileTrackingRepositoryTests` all green (including migration path)
- `dotnet test tests/Twig.Cli.Tests` — init flow tests all green
- Manual smoke: fresh workspace → `twig workspace sprint add @Current` → `twig refresh` → sprint items appear
- Manual smoke: empty `workspace.sprints[]` → `twig refresh` → no sprint items in workspace
- Manual smoke: `twig init --force` → `tracking.json` preserved; no tracking data loss
- Manual smoke: interactive `twig init` shows workspace preference prompt; choosing "Neither" starts empty
- Manual smoke: `twig workspace track <id>` → persists to `.twig/tracking.json`; MCP workspace tool returns same data
