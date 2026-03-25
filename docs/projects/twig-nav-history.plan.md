# Twig Navigation History: Back / Forward / History Commands

> **Revision:** 2 — Addresses technical review feedback  
> **Status:** Draft  
> **Date:** 2026-03-25

---

## Executive Summary

This design adds chronological navigation history to the twig CLI, enabling users to traverse their context-switch path with `twig nav back`, `twig nav fore`, and `twig nav history`. Every explicit user-initiated context change — via `SetCommand.ExecuteAsync` (used by `twig set` and spatial navigation), `FlowStartCommand.ExecuteAsync` (`twig flow-start`) — records the target work item ID in a new `navigation_history` SQLite table (schema v8 → v9). A cursor-based pointer tracks the user's current position within this circular 50-entry history, enabling browser-like back/forward traversal. The `nav history` command provides an interactive Spectre.Console picker (with `--non-interactive` fallback for piped/JSON output). The feature follows the established architecture: a new `INavigationHistoryStore` domain interface, a `SqliteNavigationHistoryStore` infrastructure implementation, and a `NavigationHistoryCommands` CLI command class wired through the existing DI and command registration patterns. Negative seed IDs are persisted as-is and resolved to their published ADO IDs at read time via the existing `IPublishIdMapRepository.GetNewIdAsync()`.

---

## Background

### Current Architecture

The twig CLI follows a four-layer architecture:

1. **Domain Layer** (`Twig.Domain`) — Aggregates, interfaces, value objects, and domain services. Repositories are defined as interfaces here (e.g., `IContextStore`, `IWorkItemRepository`, `IPublishIdMapRepository`).

2. **Infrastructure Layer** (`Twig.Infrastructure`) — SQLite persistence implementations (`SqliteCacheStore`, `SqliteContextStore`, etc.) and external service adapters. `SqliteCacheStore` manages the database lifecycle: schema versioning (currently `SchemaVersion = 8`), WAL mode, and connection access. Schema mismatches trigger a full drop-and-recreate cycle.

3. **CLI Layer** (`Twig`) — Commands (e.g., `SetCommand`, `NavigationCommands`), DI modules (`CommandRegistrationModule`, `CommandServiceModule`), formatters (`IOutputFormatter`), and the Spectre.Console rendering pipeline (`IAsyncRenderer`, `SpectreRenderer`). Commands are registered on `TwigCommands` in `Program.cs` using ConsoleAppFramework `[Command("...")]` attributes.

4. **TUI Layer** (`Twig.Tui`) — Separate project; not affected by this design.

### Context Change Sites

There are three distinct sites in the codebase that call `contextStore.SetActiveWorkItemIdAsync()`:

1. **`SetCommand.ExecuteAsync`** (`src/Twig/Commands/SetCommand.cs`, line 113): Called for `twig set <id>` and all spatial navigation (`nav up/down/next/prev` via `NavigationCommands`, which delegates to `SetCommand`). This is the primary context-change path.

2. **`FlowStartCommand.ExecuteAsync`** (`src/Twig/Commands/FlowStartCommand.cs`, line 160): Called for `twig flow-start <idOrPattern>`. Sets context directly — does **not** delegate to `SetCommand` — because flow-start also performs state transitions, assignment, and branch creation in a single orchestrated flow.

3. **`HookHandlerCommand.HandlePostCheckoutAsync`** (`src/Twig/Commands/HookHandlerCommand.cs`, line 64): Called automatically by the `post-checkout` git hook (`twig _hook post-checkout`). Extracts a work item ID from the branch name and sets context. This is an **implicit, automatic** context change — not user-initiated.

### Relevant Existing Components

- **`NavigationCommands`** (`src/Twig/Commands/NavigationCommands.cs`): Handles `twig nav up/down/next/prev`. All four methods resolve the target item and call `setCommand.ExecuteAsync(targetId.ToString(), ...)` — they do not call `contextStore.SetActiveWorkItemIdAsync` directly.

- **`IContextStore`** (`src/Twig.Domain/Interfaces/IContextStore.cs`): Key-value store for context state. Stores `active_work_item_id` and arbitrary keys. Navigation history requires a different schema (ordered rows with timestamps), so it warrants a dedicated interface rather than extending `IContextStore`.

- **`IPublishIdMapRepository`** (`src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs`): Maps old seed IDs (negative) to published ADO IDs. `GetNewIdAsync(int oldId)` returns the new ID or null. This is the existing mechanism for resolving seed IDs post-publish.

- **`SqliteCacheStore`** (`src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`): Schema DDL at `SchemaVersion = 8`. `DropAllTables()` lists all table names for drop (line 108). `CreateSchema()` executes a single DDL string. Both must be updated for the new table.

- **`InitCommand`** (`src/Twig/Commands/InitCommand.cs`): `--force` flag deletes the database file at lines 69–99 (FM-008). Since the database is fully recreated, navigation history is implicitly cleared. No additional code is needed for the "clear history on init --force" requirement.

- **`SpectreRenderer.PromptDisambiguationAsync`** (`src/Twig/Rendering/SpectreRenderer.cs`, line 649): Existing AOT-safe interactive picker using `_console.Live()` with keyboard navigation. This pattern will be reused for `nav history`.

### Prior Art

The `twig stash` / `twig stash pop` commands implement a stack-based context save/restore but do not track navigation history. The `IContextStore.GetActiveWorkItemIdAsync()` / `SetActiveWorkItemIdAsync()` pair tracks current state but not history. `HookHandlerCommand` and `FlowStartCommand` both call `contextStore.SetActiveWorkItemIdAsync()` directly (bypassing `SetCommand`), establishing prior art for direct context changes outside of `SetCommand`.

---

## Problem Statement

Users navigating complex work item hierarchies frequently need to return to previously visited items. Currently, the only way to return to a prior context is `twig set <id>`, which requires remembering the exact ID. Spatial navigation (`nav up/down/next/prev`) traverses the tree structure but provides no way to retrace the chronological path of context switches. This is the CLI equivalent of having no browser back/forward buttons.

Specific pain points:
1. **No undo for context switch** — A user who navigates `set 42 → nav down → nav next → nav next` cannot quickly return to item #42.
2. **No visibility into navigation path** — Users cannot see which items they recently visited.
3. **Seed ID drift** — Negative seed IDs become invalid after `seed publish`. History entries referencing these IDs must resolve to the published ADO IDs transparently.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Record every successful context change in chronological order, capped at 50 entries.
2. **G-2**: Provide `twig nav back` and `twig nav fore` commands that traverse the history stack with browser-like semantics.
3. **G-3**: Provide `twig nav history` with an interactive picker (TTY) and `--non-interactive` flat list (piped/JSON/minimal).
4. **G-4**: Transparently remap published seed IDs in history entries using existing `IPublishIdMapRepository`.
5. **G-5**: Clear navigation history on `twig init --force` (implicit via DB deletion).
6. **G-6**: Maintain AOT compatibility — no reflection-based serialization or trim-unsafe patterns.

### Non-Goals

- **NG-1**: Per-branch history isolation — history is per-workspace (per-database), not per-git-branch.
- **NG-2**: Cross-workspace history — each `.twig/{org}/{project}/twig.db` has independent history.
- **NG-3**: Undo/redo for field edits — this feature tracks navigation (context switches), not mutations.
- **NG-4**: History persistence across `twig init --force` — deliberate reset is expected behavior.
- **NG-5**: Deduplication of consecutive identical entries — if the user runs `twig set 42` twice, two entries are recorded. (This preserves the invariant that history length equals context-switch count.)

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | Every explicit user-initiated context change — via `SetCommand.ExecuteAsync` (used by `twig set` and spatial `nav up/down/next/prev`) and `FlowStartCommand.ExecuteAsync` (`twig flow-start`) — records the work item ID and ISO 8601 UTC timestamp in navigation history. Implicit context changes from git hooks (`HookHandlerCommand`) are excluded (see DD-11). |
| FR-02 | Navigation history is a circular buffer with a maximum depth of 50 entries. When full, the oldest entry is deleted before inserting a new one. |
| FR-03 | `twig nav back` moves the history cursor one step backward (toward older entries) and sets the active context to that item. Returns exit code 1 with an error if already at the oldest entry. |
| FR-04 | `twig nav fore` moves the history cursor one step forward (toward newer entries) and sets the active context to that item. Returns exit code 1 with an error if already at the newest entry. |
| FR-05 | When navigating via `twig nav back` or `twig nav fore`, the cursor movement does NOT record a new history entry (to prevent polluting the history with back/forward traversals). |
| FR-06 | When the user performs a new `twig set` (or spatial nav) while the cursor is not at the head, all forward entries are pruned (truncated) before recording the new entry — standard browser-like behavior. |
| FR-07 | `twig nav history` displays the navigation history with the current position highlighted. Interactive picker in TTY mode; flat list with `--non-interactive` flag or when output is redirected/JSON/minimal. |
| FR-08 | Selecting an item in `twig nav history` sets the active context to that item and updates the history cursor accordingly (new entry is appended; forward entries are pruned). |
| FR-09 | History entries with negative (seed) IDs are resolved to their published ADO IDs via `IPublishIdMapRepository.GetNewIdAsync()` at read time. Entries whose seed IDs have not been published are displayed as-is (negative ID). |
| FR-10 | Navigation history is cleared when `twig init --force` deletes and recreates the database. |
| FR-11 | The `--output` flag on `nav back`, `nav fore`, and `nav history` supports human, json, and minimal formats consistent with other navigation commands. |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | History operations (record, back, forward, list) must complete in < 10ms for SQLite operations (excluding any ADO fetch triggered by `SetCommand`). |
| NFR-02 | Schema migration from v8 to v9 must be non-breaking — existing databases are rebuilt with the new table automatically via the existing `EnsureSchema()` drop-and-recreate pattern. |
| NFR-03 | All new code must be AOT-compatible (no reflection, no `SelectionPrompt<T>`, parameterized SQL only). |
| NFR-04 | Test coverage: ≥ 90% of new domain logic and ≥ 80% of new infrastructure code covered by unit tests. |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  CLI Layer (Twig)                                           │
│  ┌─────────────────────────┐ ┌───────────────────────────┐  │
│  │ Program.cs              │ │ NavigationHistoryCommands  │  │
│  │ [Command("nav back")]   │→│ BackAsync()               │  │
│  │ [Command("nav fore")]   │→│ ForeAsync()               │  │
│  │ [Command("nav history")]│→│ HistoryAsync()            │  │
│  └─────────────────────────┘ └────────────┬──────────────┘  │
│                                            │                 │
│  ┌─────────────────────┐                   │ delegates       │
│  │ SetCommand          │                   │                 │
│  │ ExecuteAsync()      │──records──→       │                 │
│  │  ↓ after success    │                   ↓                 │
│  │  historyStore       │  ┌──────────────────────────────┐  │
│  │  .RecordVisitAsync()│  │ INavigationHistoryStore      │  │
│  └─────────────────────┘  │ (Domain interface)            │  │
│                           └──────────────────────────────┘  │
│  ┌─────────────────────┐          ↑                         │
│  │ FlowStartCommand    │          │                         │
│  │ ExecuteAsync()      │──records─┘                         │
│  │  ↓ after context set│                                    │
│  │  historyStore       │                                    │
│  │  .RecordVisitAsync()│                                    │
│  └─────────────────────┘                                    │
│                                                             │
│  ┌─────────────────────┐                                    │
│  │ HookHandlerCommand  │  (NO history recording — DD-11)   │
│  │ post-checkout        │                                    │
│  │ contextStore.Set...()│                                   │
│  └─────────────────────┘                                    │
├─────────────────────────────────────────────────────────────┤
│  Domain Layer (Twig.Domain)                                 │
│  ┌──────────────────────────┐  ┌─────────────────────────┐  │
│  │ INavigationHistoryStore  │  │ NavigationHistoryEntry   │  │
│  │ RecordVisitAsync()       │  │ (record / value object)  │  │
│  │ GoBackAsync()            │  │ Id, WorkItemId, VisitedAt│  │
│  │ GoForwardAsync()         │  └─────────────────────────┘  │
│  │ GetHistoryAsync()        │                                │
│  │ GetCurrentPositionAsync()│                                │
│  │ PruneForwardAsync()      │                                │
│  │ ClearAsync()             │                                │
│  └──────────────────────────┘                                │
├─────────────────────────────────────────────────────────────┤
│  Infrastructure Layer (Twig.Infrastructure)                  │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ SqliteNavigationHistoryStore                          │   │
│  │ - navigation_history table                            │   │
│  │ - context table (nav_history_cursor key)              │   │
│  │ - Circular buffer: DELETE oldest when count > 50      │   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ SqliteCacheStore (SchemaVersion 8 → 9)               │   │
│  │ - DDL adds navigation_history table                   │   │
│  │ - DropAllTables adds "navigation_history"             │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `INavigationHistoryStore` (Domain Interface)

```csharp
namespace Twig.Domain.Interfaces;

/// <summary>
/// Persistent store for chronological navigation history.
/// Entries are ordered by auto-increment ID. A cursor tracks the current position.
/// </summary>
public interface INavigationHistoryStore
{
    /// <summary>
    /// Records a visit to the given work item. Prunes forward entries if the cursor
    /// is not at the head, then appends the new entry and advances the cursor.
    /// Enforces the maximum history depth by deleting the oldest entry when full.
    /// </summary>
    Task RecordVisitAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// Moves the cursor one step backward. Returns the work item ID at the new position,
    /// or null if already at the oldest entry.
    /// </summary>
    Task<int?> GoBackAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves the cursor one step forward. Returns the work item ID at the new position,
    /// or null if already at the newest entry.
    /// </summary>
    Task<int?> GoForwardAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all history entries in chronological order (oldest first),
    /// along with the current cursor position (as an entry ID).
    /// </summary>
    Task<(IReadOnlyList<NavigationHistoryEntry> Entries, int? CursorEntryId)> GetHistoryAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Clears all navigation history and resets the cursor.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}
```

#### 2. `NavigationHistoryEntry` (Domain Value Object)

```csharp
namespace Twig.Domain.ValueObjects;

/// <summary>
/// A single entry in the navigation history.
/// </summary>
public sealed record NavigationHistoryEntry(int Id, int WorkItemId, DateTimeOffset VisitedAt);
```

#### 3. `SqliteNavigationHistoryStore` (Infrastructure Implementation)

Follows the same patterns as `SqliteContextStore` and `SqlitePublishIdMapRepository`:
- Constructor accepts `SqliteCacheStore`.
- Uses `_store.GetConnection()` for the shared connection.
- Uses `_store.ActiveTransaction` for transaction enrollment.
- All SQL is parameterized.

**Cursor tracking**: The cursor is stored as a key-value pair in the `context` table: key = `"nav_history_cursor"`, value = the `id` of the current history entry (as a string). `SqliteNavigationHistoryStore` accesses the `context` table directly via SQL on `_store.GetConnection()` (the same shared connection used by `SqliteContextStore`), not through `IContextStore`. This avoids a circular dependency and keeps the store self-contained.

**Circular buffer enforcement**: After inserting a new entry, if the row count exceeds 50, delete the row with the smallest `id`.

#### 4. `NavigationHistoryCommands` (CLI Command Class)

```csharp
namespace Twig.Commands;

public sealed class NavigationHistoryCommands(
    INavigationHistoryStore historyStore,
    IPublishIdMapRepository publishIdMapRepo,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    RenderingPipelineFactory? pipelineFactory = null,
    IPromptStateWriter? promptStateWriter = null)
{
    public async Task<int> BackAsync(string outputFormat, CancellationToken ct);
    public async Task<int> ForeAsync(string outputFormat, CancellationToken ct);
    public async Task<int> HistoryAsync(bool nonInteractive, string outputFormat, CancellationToken ct);
}
```

**`BackAsync`**:
1. Call `historyStore.GoBackAsync()`.
2. If null → error "Already at oldest entry in navigation history."
3. Resolve seed ID: if workItemId < 0, call `publishIdMapRepo.GetNewIdAsync(workItemId)`. Use remapped ID if available.
4. Call `contextStore.SetActiveWorkItemIdAsync(resolvedId)` — direct context set, NOT `SetCommand.ExecuteAsync` (to avoid recording a new history entry).
5. Fetch and display the work item via `workItemRepo.GetByIdAsync()`.
6. Update prompt state if writer is available.

**`ForeAsync`**: Same pattern as `BackAsync`, using `GoForwardAsync()`.

**`HistoryAsync`**:
1. Call `historyStore.GetHistoryAsync()` to get all entries and cursor position.
2. Resolve seed IDs for all entries.
3. Enrich entries with work item titles from `workItemRepo.GetByIdAsync()` (best-effort; show ID-only if not cached).
4. If interactive (TTY + human format + `!nonInteractive`): render Spectre.Console picker via `IAsyncRenderer` extension or inline Live() prompt.
5. If non-interactive: render flat list via `IOutputFormatter` (or inline formatting).
6. On selection: set context and record new history entry (prune forward, append).

#### 5. SQLite Schema Changes

**New table** (added to `SqliteCacheStore.Ddl`):

```sql
CREATE TABLE navigation_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    work_item_id INTEGER NOT NULL,
    visited_at TEXT NOT NULL
);
```

**`SchemaVersion`**: 8 → 9.

**`DropAllTables`**: Add `"navigation_history"` to the drop list.

### Data Flow

#### Recording a Visit (SetCommand → History)

```
User runs: twig set 42
  → SetCommand.ExecuteAsync("42")
    → Resolves work item #42
    → contextStore.SetActiveWorkItemIdAsync(42)         // existing
    → historyStore.RecordVisitAsync(42)                  // NEW
      → SQL: check cursor position vs head
      → if cursor < head: DELETE FROM navigation_history WHERE id > cursor_id
      → INSERT INTO navigation_history (work_item_id, visited_at) VALUES (42, '...')
      → if count > 50: DELETE oldest row
      → UPDATE context SET value = new_id WHERE key = 'nav_history_cursor'
    → render work item, sync, hints
```

#### Recording a Visit (FlowStartCommand → History)

```
User runs: twig flow-start 42
  → FlowStartCommand.ExecuteAsync("42")
    → Resolves work item #42
    → contextStore.SetActiveWorkItemIdAsync(42)         // existing (line 160)
    → historyStore.RecordVisitAsync(42)                  // NEW
      → (same SQL as SetCommand path above)
    → state transition, assignment, branch creation, render summary
```

#### Implicit Context Change (HookHandlerCommand — NOT Recorded)

```
User runs: git checkout feature/42-fix-bug
  → post-checkout hook triggers: twig _hook post-checkout
    → HookHandlerCommand.HandlePostCheckoutAsync()
      → Extracts work item ID 42 from branch name
      → contextStore.SetActiveWorkItemIdAsync(42)       // existing (line 64)
      → (NO history recording — DD-11)
```

#### Navigating Back

```
User runs: twig nav back
  → NavigationHistoryCommands.BackAsync()
    → historyStore.GoBackAsync()
      → SELECT id FROM navigation_history WHERE id < cursor_id ORDER BY id DESC LIMIT 1
      → if none: return null → "Already at oldest entry"
      → UPDATE context SET value = prev_id WHERE key = 'nav_history_cursor'
      → return work_item_id of prev entry
    → Resolve seed ID if negative
    → contextStore.SetActiveWorkItemIdAsync(resolvedId)  // direct set, no history record
    → Display work item
```

#### Navigating Forward

```
User runs: twig nav fore
  → NavigationHistoryCommands.ForeAsync()
    → historyStore.GoForwardAsync()
      → SELECT id FROM navigation_history WHERE id > cursor_id ORDER BY id ASC LIMIT 1
      → if none: return null → "Already at newest entry"
      → UPDATE context SET value = next_id WHERE key = 'nav_history_cursor'
      → return work_item_id of next entry
    → Resolve seed ID if negative
    → contextStore.SetActiveWorkItemIdAsync(resolvedId)
    → Display work item
```

#### Viewing History

```
User runs: twig nav history
  → NavigationHistoryCommands.HistoryAsync()
    → historyStore.GetHistoryAsync()
      → SELECT id, work_item_id, visited_at FROM navigation_history ORDER BY id ASC
      → cursor = SELECT value FROM context WHERE key = 'nav_history_cursor'
    → For each entry: resolve seed IDs, fetch titles from cache
    → If TTY: Spectre picker (current entry highlighted)
    → If piped/json/minimal: flat list with → marker on current entry
    → On selection: set context + record new history entry
```

### API Contracts

#### CLI Commands

| Command | Parameters | Description |
|---------|-----------|-------------|
| `twig nav back` | `--output <format>` | Move cursor backward in history, set context |
| `twig nav fore` | `--output <format>` | Move cursor forward in history, set context |
| `twig nav history` | `--non-interactive`, `--output <format>` | Show history list with picker or flat list |

#### Output Formats

**Human (back/fore)**:
```
← #42 ● Task — Fix login bug [Active]
```

**Human (history, non-interactive)**:
```
Navigation History (5 entries):
    #100 ● Feature — User Auth [New]          2026-03-25 10:00
    #42  ● Task — Fix login bug [Active]      2026-03-25 10:05
  → #43  ● Task — Add tests [Active]          2026-03-25 10:10
    #42  ● Task — Fix login bug [Active]      2026-03-25 10:15
    #100 ● Feature — User Auth [New]          2026-03-25 10:20
```

**JSON (history)**:
```json
{
  "entries": [
    { "id": 1, "workItemId": 100, "visitedAt": "2026-03-25T10:00:00Z" },
    { "id": 2, "workItemId": 42,  "visitedAt": "2026-03-25T10:05:00Z" }
  ],
  "currentEntryId": 2
}
```

**Minimal (back/fore)**:
```
42
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Use `AUTOINCREMENT` integer ID for ordering, not timestamp | Timestamps could collide if two operations happen in the same second. Autoincrement IDs provide guaranteed monotonic ordering. |
| DD-02 | Store cursor in `context` table as key-value pair | Reuses existing infrastructure (`SqliteContextStore`). Avoids schema complexity of a separate cursor table or column. |
| DD-03 | Record history in both `SetCommand.ExecuteAsync` and `FlowStartCommand.ExecuteAsync` | These are the two explicit user-initiated context-change sites. `SetCommand` handles `twig set` and all spatial navigation (via `NavigationCommands`). `FlowStartCommand` handles `twig flow-start`, which sets context directly at line 160 without delegating to `SetCommand`. Both must record history to avoid gaps. |
| DD-04 | Back/fore set context directly (bypass `SetCommand`) | If back/fore routed through `SetCommand`, every back/forward would record a new history entry, corrupting the history stack. Direct `contextStore.SetActiveWorkItemIdAsync()` avoids this. |
| DD-05 | Resolve seed IDs at read time, not write time | Seeds may be published after the history entry is recorded. Read-time resolution via `IPublishIdMapRepository.GetNewIdAsync()` ensures the latest mapping is always used. |
| DD-06 | Prune forward entries on new navigation (browser semantics) | Standard browser back/forward behavior. If cursor is at position 3 of 5, and user navigates to a new item, positions 4–5 are deleted. Prevents confusing "branching history". |
| DD-07 | Circular buffer via DELETE of oldest row | Simpler than SQLite ring-buffer tricks. The 50-entry cap means at most one DELETE per INSERT. Performance impact is negligible. |
| DD-08 | New `INavigationHistoryStore` interface (not extending `IContextStore`) | Navigation history has different semantics (ordered collection vs. key-value). Separation follows Interface Segregation Principle and keeps `IContextStore` focused. |
| DD-09 | `SetCommand` and `FlowStartCommand` receive `INavigationHistoryStore?` (nullable) | Backward compatibility with existing tests that construct these commands without a history store. Null = history recording is silently skipped. |
| DD-10 | Back/fore bypass `SetCommand` entirely | `NavigationHistoryCommands.BackAsync`/`ForeAsync` call `historyStore.GoBackAsync()`/`GoForwardAsync()` to move the cursor, then call `contextStore.SetActiveWorkItemIdAsync()` directly to update context. They do **not** call `SetCommand.ExecuteAsync`, so no new history entry is recorded during traversal. This is the same direct-context-set pattern used by `HookHandlerCommand` (line 64) and `FlowStartCommand` (line 160). |
| DD-11 | Exclude `HookHandlerCommand` (post-checkout) from history recording | Post-checkout hook context changes are **implicit and automatic** — triggered by `git checkout`, not by a deliberate user command. Including them would pollute navigation history with every branch switch (which can be frequent during code review, rebases, etc.). Users who switch branches and want to return to their previous work item context can use `twig set` or `twig nav back` from their last explicit navigation. This keeps history focused on intentional navigation decisions. |

---

## Alternatives Considered

### A1: Extend `IContextStore` with History Methods

**Pros**: Fewer interfaces, single store.  
**Cons**: Violates ISP — `IContextStore` is a simple key-value store. Adding ordered-collection semantics bloats the interface and forces all existing mocks to stub new methods.  
**Decision**: Separate `INavigationHistoryStore` interface (DD-08).

### A2: Record History Only in `SetCommand` (Single Hook Point)

**Pros**: Single recording site, simpler implementation.  
**Cons**: `FlowStartCommand` (line 160) calls `contextStore.SetActiveWorkItemIdAsync()` directly — it does not delegate to `SetCommand`. Context changes from `twig flow-start` would not appear in history. This is a primary workflow command, not an edge case — users who `flow-start` then `nav back` would not be returned to their pre-flow-start context.  
**Decision**: Record history in both `SetCommand.ExecuteAsync` and `FlowStartCommand.ExecuteAsync` (DD-03). Accept the two-site recording cost for complete coverage of explicit user-initiated context changes.

### A3: Use Timestamp Ordering Instead of AUTOINCREMENT

**Pros**: No need for SQLite-specific `AUTOINCREMENT`.  
**Cons**: ISO 8601 string comparison is slower than integer comparison. Sub-second collisions are possible (two context switches in the same CLI invocation). Autoincrement is monotonically increasing by definition.  
**Decision**: Use `INTEGER PRIMARY KEY AUTOINCREMENT` for ordering (DD-01).

### A4: Back/Fore Route Through `SetCommand` with a "Don't Record" Flag

**Pros**: Reuses `SetCommand`'s full sync/render pipeline.  
**Cons**: Requires adding a parameter to `SetCommand.ExecuteAsync`, which has 3 parameters today and is called from ~15 sites. The rendering/sync/hints in `SetCommand` are actually desirable when navigating back/fore, but history recording must be suppressed.  
**Decision**: Back/fore bypass `SetCommand` entirely and call `contextStore.SetActiveWorkItemIdAsync` directly (DD-04, DD-10). This is the same pattern as `HookHandlerCommand` (line 64) and `FlowStartCommand` (line 160), both of which set context without `SetCommand` orchestration. For display, back/fore will do a lightweight `workItemRepo.GetByIdAsync` + `FormatWorkItem` — simpler and faster than the full `SetCommand` pipeline.

### A5: Record History in `HookHandlerCommand` (Post-Checkout)

**Pros**: Complete tracking of all context changes, including implicit ones.  
**Cons**: Post-checkout hooks fire on every `git checkout`, `git switch`, `git rebase`, `git pull` (with rebase), etc. Many of these are not intentional navigation decisions — they would pollute the history with noise. A developer reviewing three PRs would accumulate six hook-triggered history entries (checkout to branch, checkout back) that obscure their intentional navigation.  
**Decision**: Exclude `HookHandlerCommand` from history recording (DD-11). History tracks intentional navigation only.

---

## Dependencies

### External Dependencies

| Dependency | Version | Usage |
|-----------|---------|-------|
| Microsoft.Data.Sqlite | (existing) | SQLite access for new table |
| ConsoleAppFramework | (existing) | `[Command]` registration for new commands |
| Spectre.Console | (existing) | Interactive picker for `nav history` |

### Internal Dependencies

| Component | Dependency Type |
|-----------|----------------|
| `IPublishIdMapRepository` | Consumed — seed ID remapping at read time |
| `IContextStore` | Consumed — direct context set for back/fore; cursor storage in `context` table |
| `IWorkItemRepository` | Consumed — fetch work item details for display |
| `SqliteCacheStore` | Modified — schema v8 → v9, new table DDL |
| `SetCommand` | Modified — inject `INavigationHistoryStore?`, call `RecordVisitAsync` |
| `FlowStartCommand` | Modified — inject `INavigationHistoryStore?`, call `RecordVisitAsync` after context set |
| `CommandRegistrationModule` | Modified — register `NavigationHistoryCommands`, update `SetCommand` and `FlowStartCommand` factories |
| `TwigServiceRegistration` | Modified — register `INavigationHistoryStore` → `SqliteNavigationHistoryStore` |
| `Program.cs` (`TwigCommands`) | Modified — add `[Command("nav back")]`, `[Command("nav fore")]`, `[Command("nav history")]` |

### Sequencing Constraints

None — this feature is self-contained and does not depend on other in-flight work.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `SqliteCacheStore` | Schema bump (v8→v9), DDL addition, `DropAllTables` update |
| `SetCommand` | New optional dependency, one new method call after context set |
| `FlowStartCommand` | New optional dependency, one new method call after context set (line 160) |
| `Program.cs` | 3 new command registrations + help text update |
| `CommandRegistrationModule` | New DI registrations, update `SetCommand` and `FlowStartCommand` factories |
| `TwigServiceRegistration` | New DI registration |
| `HookHandlerCommand` | NOT modified — excluded by design (DD-11) |

### Backward Compatibility

- **Schema migration**: Existing v8 databases will be automatically rebuilt as v9 on next CLI invocation. This is the established pattern — all data is re-fetchable from ADO. Users will see a brief delay on first use after upgrade while the cache rebuilds via `twig refresh` or on-demand `twig set`.
- **`SetCommand` constructor**: New optional parameter (`INavigationHistoryStore? historyStore = null`) — existing callers and tests continue to work without change.
- **`FlowStartCommand` constructor**: New optional parameter (`INavigationHistoryStore? historyStore = null`) — existing callers and tests continue to work without change. The parameter is added after the existing optional parameters.
- **CLI interface**: New commands only — no existing commands are modified.

### Performance Implications

- **Recording**: One INSERT + conditional DELETE per `SetCommand` invocation. Both are single-row operations on a small table (≤ 50 rows). Expected < 1ms.
- **Back/Forward**: One SELECT + one UPDATE on the `context` table + one SELECT on `navigation_history`. Expected < 1ms.
- **History listing**: One SELECT returning ≤ 50 rows + N `GetByIdAsync` calls (cached, synchronous SQLite reads). Expected < 5ms.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema v9 rebuild loses navigation history from prior runs | Medium | Low | Expected behavior — history is ephemeral. Document in release notes. |
| Seed IDs in history become stale if seed is discarded (not published) | Low | Low | History entries referencing deleted seeds will show "Work item not found" — same as any deleted item. |
| Back/fore bypass SetCommand's working-set sync | Medium | Low | Back/fore are fast context switches. User can run `twig status` to trigger sync. Alternatively, add optional lightweight sync in a future iteration. |
| Interactive picker in `nav history` blocks on `Console.ReadKey` in non-TTY | Low | Medium | Existing guard: `RenderingPipelineFactory.Resolve()` returns `null` renderer for non-TTY. `--non-interactive` flag also available. |
| HookHandlerCommand context changes not in history (DD-11) | Low | Low | Deliberate design choice — automatic hook context changes are excluded. If a user runs `flow-start`, then git-checkouts to another branch (auto-context change), then `nav back`, they return to the flow-start item, not the branch-switch item. This is the expected UX — `nav back` retraces intentional navigation. |
| Two recording sites (SetCommand + FlowStartCommand) instead of one | Low | Low | Both sites use the same `historyStore.RecordVisitAsync()` call. Maintenance burden is minimal — the call is a single line. If a third context-change site is added in the future, it must also add the recording call (add a code comment referencing this design doc). |

---

## Open Questions

1. **[Low]** Should `twig nav back` and `twig nav fore` trigger a working-set sync (like `SetCommand` does), or should they be lightweight context switches? Current design opts for lightweight. Can be enhanced later without breaking changes.

2. **[Low]** Should consecutive duplicate entries be collapsed? (e.g., `set 42 → set 42` records two entries). Current design preserves all entries for simplicity. Could add dedup as a future enhancement.

3. **[Low]** Should `nav history` selection use `SetCommand.ExecuteAsync` (full pipeline with sync/hints) or direct context set? Current design uses `SetCommand` for full UX consistency when user actively selects from history.

4. **[Low]** Should `HookHandlerCommand` post-checkout context changes be optionally recorded in history (e.g., behind a config flag like `history.includeHooks = true`)? Current design excludes them entirely (DD-11). A config flag could be added in a future iteration if users request it.

---

## Implementation Phases

### Phase 1: Domain & Infrastructure Foundation
**Exit Criteria**: `INavigationHistoryStore` interface defined, `SqliteNavigationHistoryStore` implemented and passing unit tests, schema bumped to v9.

### Phase 2: SetCommand & FlowStartCommand Integration
**Exit Criteria**: Both `SetCommand` and `FlowStartCommand` record visits to history store. Existing tests still pass. New integration tests verify recording from both sites.

### Phase 3: Back/Forward Commands
**Exit Criteria**: `twig nav back` and `twig nav fore` work correctly with cursor movement. Edge cases (empty history, at boundary) handled. Seed ID resolution tested.

### Phase 4: History Command
**Exit Criteria**: `twig nav history` shows history list in all output formats. Interactive picker works in TTY mode. `--non-interactive` flag works.

### Phase 5: Wiring & Polish
**Exit Criteria**: Commands registered in `Program.cs`, DI wired, help text updated, end-to-end manual verification.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Interfaces/INavigationHistoryStore.cs` | Domain interface for navigation history operations |
| `src/Twig.Domain/ValueObjects/NavigationHistoryEntry.cs` | Value object representing a single history entry |
| `src/Twig.Infrastructure/Persistence/SqliteNavigationHistoryStore.cs` | SQLite implementation of `INavigationHistoryStore` |
| `src/Twig/Commands/NavigationHistoryCommands.cs` | CLI command class for back/fore/history |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteNavigationHistoryStoreTests.cs` | Unit tests for SQLite history store |
| `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | Unit tests for back/fore/history commands |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Bump `SchemaVersion` 8→9, add `navigation_history` DDL, add to `DropAllTables` |
| `src/Twig/Commands/SetCommand.cs` | Add `INavigationHistoryStore?` parameter, call `RecordVisitAsync` after context set |
| `src/Twig/Commands/FlowStartCommand.cs` | Add `INavigationHistoryStore?` parameter, call `RecordVisitAsync` after context set (line 160) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `NavigationHistoryCommands`, update `SetCommand` and `FlowStartCommand` factory lambdas |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `INavigationHistoryStore` → `SqliteNavigationHistoryStore` |
| `src/Twig/Program.cs` | Add `[Command("nav back")]`, `[Command("nav fore")]`, `[Command("nav history")]` methods + grouped help text |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | Update schema version assertion if present |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### Epic 1: Domain & Infrastructure Foundation

**Goal**: Define the domain interface and value object, implement the SQLite persistence layer, bump schema to v9.

**Prerequisites**: None.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `INavigationHistoryStore` interface in Domain | `src/Twig.Domain/Interfaces/INavigationHistoryStore.cs` | DONE |
| E1-T2 | IMPL | Create `NavigationHistoryEntry` record in Domain | `src/Twig.Domain/ValueObjects/NavigationHistoryEntry.cs` | DONE |
| E1-T3 | IMPL | Implement `SqliteNavigationHistoryStore` with circular buffer logic | `src/Twig.Infrastructure/Persistence/SqliteNavigationHistoryStore.cs` | DONE |
| E1-T4 | IMPL | Update `SqliteCacheStore`: bump `SchemaVersion` to 9, add `navigation_history` DDL, update `DropAllTables` | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | DONE |
| E1-T5 | TEST | Write unit tests for `SqliteNavigationHistoryStore`: record, back, forward, circular buffer, prune forward | `tests/Twig.Infrastructure.Tests/Persistence/SqliteNavigationHistoryStoreTests.cs` | DONE |
| E1-T6 | TEST | Update `SqliteCacheStoreTests` for schema version 9 if applicable | `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `INavigationHistoryStore` interface compiles and matches the contract in this design
- [x] `NavigationHistoryEntry` record is defined with `Id`, `WorkItemId`, `VisitedAt`
- [x] `SqliteNavigationHistoryStore` passes all unit tests
- [x] Circular buffer enforces 50-entry maximum
- [x] Forward pruning works when cursor is not at head
- [x] Schema version is 9 and database creates successfully

### Epic 2: SetCommand & FlowStartCommand Integration

**Goal**: Hook `SetCommand.ExecuteAsync` and `FlowStartCommand.ExecuteAsync` to record navigation history on every successful explicit context change.

**Prerequisites**: Epic 1 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Add `INavigationHistoryStore?` optional parameter to `SetCommand` constructor | `src/Twig/Commands/SetCommand.cs` | DONE |
| E2-T2 | IMPL | Call `historyStore.RecordVisitAsync(item.Id)` after `contextStore.SetActiveWorkItemIdAsync` in `SetCommand.ExecuteAsync` (line 113) | `src/Twig/Commands/SetCommand.cs` | DONE |
| E2-T3 | IMPL | Add `INavigationHistoryStore?` optional parameter to `FlowStartCommand` constructor | `src/Twig/Commands/FlowStartCommand.cs` | DONE |
| E2-T4 | IMPL | Call `historyStore.RecordVisitAsync(item.Id)` after `contextStore.SetActiveWorkItemIdAsync` in `FlowStartCommand.ExecuteAsync` (line 160) | `src/Twig/Commands/FlowStartCommand.cs` | DONE |
| E2-T5 | IMPL | Update `SetCommand` DI registration to pass `INavigationHistoryStore` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DONE |
| E2-T6 | IMPL | Update `FlowStartCommand` DI registration to pass `INavigationHistoryStore` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DONE |
| E2-T7 | IMPL | Register `INavigationHistoryStore` → `SqliteNavigationHistoryStore` in `TwigServiceRegistration` | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE |
| E2-T8 | TEST | Add test: `SetCommand` records history entry on successful set | `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | DONE |
| E2-T9 | TEST | Add test: `FlowStartCommand` records history entry on successful flow-start | `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | DONE |
| E2-T10 | TEST | Verify existing `SetCommand` and `FlowStartCommand` tests pass (null history store) | `tests/Twig.Cli.Tests/Commands/` | DONE |

**Acceptance Criteria**:
- [x] `SetCommand` constructor accepts optional `INavigationHistoryStore?`
- [x] `FlowStartCommand` constructor accepts optional `INavigationHistoryStore?`
- [x] Every successful `SetCommand.ExecuteAsync` call records a history entry
- [x] Every successful `FlowStartCommand.ExecuteAsync` call records a history entry
- [x] Existing tests pass without modification (null history store path)
- [x] DI wiring is correct — `SqliteNavigationHistoryStore` resolves from container

### Epic 3: Back/Forward Commands

**Goal**: Implement `twig nav back` and `twig nav fore` with cursor movement and seed ID resolution.

**Prerequisites**: Epic 2 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Create `NavigationHistoryCommands` with `BackAsync` and `ForeAsync` | `src/Twig/Commands/NavigationHistoryCommands.cs` | TO DO |
| E3-T2 | IMPL | Implement seed ID resolution in back/fore using `IPublishIdMapRepository.GetNewIdAsync` | `src/Twig/Commands/NavigationHistoryCommands.cs` | TO DO |
| E3-T3 | IMPL | Register `NavigationHistoryCommands` in `CommandRegistrationModule` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| E3-T4 | IMPL | Add `[Command("nav back")]` and `[Command("nav fore")]` to `Program.cs` | `src/Twig/Program.cs` | TO DO |
| E3-T5 | TEST | Unit tests: back from various positions, back at boundary, empty history | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |
| E3-T6 | TEST | Unit tests: fore from various positions, fore at boundary | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |
| E3-T7 | TEST | Unit tests: seed ID resolution in back/fore | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig nav back` moves cursor backward and sets context
- [ ] `twig nav fore` moves cursor forward and sets context
- [ ] Returns error at boundaries (oldest/newest)
- [ ] Negative seed IDs are resolved to published ADO IDs
- [ ] Back/fore do NOT record new history entries
- [ ] Prompt state is updated after context change

### Epic 4: History Command

**Goal**: Implement `twig nav history` with interactive picker and `--non-interactive` mode.

**Prerequisites**: Epic 3 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Implement `HistoryAsync` in `NavigationHistoryCommands` with non-interactive output | `src/Twig/Commands/NavigationHistoryCommands.cs` | TO DO |
| E4-T2 | IMPL | Implement interactive Spectre.Console picker for TTY history selection | `src/Twig/Commands/NavigationHistoryCommands.cs` | TO DO |
| E4-T3 | IMPL | Add `[Command("nav history")]` to `Program.cs` | `src/Twig/Program.cs` | TO DO |
| E4-T4 | TEST | Unit tests: history non-interactive output, empty history, seed ID resolution | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |
| E4-T5 | TEST | Unit tests: history JSON and minimal output formats | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig nav history` shows all entries with current position marked
- [ ] Interactive picker works in TTY mode (Spectre.Console Live)
- [ ] `--non-interactive` flag shows flat list
- [ ] JSON output includes entries array and currentEntryId
- [ ] Selecting an item sets context and records new history entry
- [ ] Empty history shows informative message

### Epic 5: Wiring & Polish

**Goal**: Final integration, help text update, and end-to-end verification.

**Prerequisites**: Epics 3 and 4 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | Update grouped help text in `Program.cs` to include nav back/fore/history | `src/Twig/Program.cs` | TO DO |
| E5-T2 | IMPL | Add backward-compat aliases if needed (e.g., bare `back`/`fore` like `up`/`down`) | `src/Twig/Program.cs` | TO DO |
| E5-T3 | TEST | End-to-end integration test: set → set → back → fore → history | `tests/Twig.Cli.Tests/Commands/NavigationHistoryCommandTests.cs` | TO DO |
| E5-T4 | TEST | Verify `twig init --force` clears history (database deletion) | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] Help text shows nav back/fore/history in Navigation section
- [ ] Full build succeeds with no warnings
- [ ] All existing tests pass
- [ ] All new tests pass
- [ ] End-to-end: set A → set B → back → verify A → fore → verify B → history shows both

---

## References

- Existing navigation commands: `src/Twig/Commands/NavigationCommands.cs`
- SetCommand (primary hook point): `src/Twig/Commands/SetCommand.cs` (line 113)
- FlowStartCommand (secondary hook point): `src/Twig/Commands/FlowStartCommand.cs` (line 160)
- HookHandlerCommand (excluded — DD-11): `src/Twig/Commands/HookHandlerCommand.cs` (line 64)
- Schema management: `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`
- Seed ID remapping: `src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs`
- Interactive picker pattern: `src/Twig/Rendering/SpectreRenderer.cs` (line 649)
- DI registration: `src/Twig.Infrastructure/TwigServiceRegistration.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs`
- ConsoleAppFramework command registration: `src/Twig/Program.cs` (TwigCommands class)
- Existing plan document format: `docs/projects/twig-tree-enhancements.plan.md`
