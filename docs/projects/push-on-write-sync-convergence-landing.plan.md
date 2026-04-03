# Push-on-Write and Sync Convergence — Landing

**Epic:** #1394  
**Status**: 🔨 In Progress  
**Revision:** 6 — Merged T-1363.2a/2b back to T-1363.2 (artificial task split); removed speculative `--quiet` note from DD-11.

---

## Executive Summary

This epic completes the push-on-write and sync convergence initiative by delivering the two issues deferred from the original Epic #1338: (1) automatic fast-forward of "phantom dirty" work items during refresh/sync, and (2) a `twig discard` command for dropping pending changes without pushing them. Epic #1338's core work — push-on-write for note/edit commands, PendingChangeFlusher extraction, and the `twig sync` command — landed on main via PR #10 (v0.27.0) and the feature/sync-command merge (v0.28.0). The remaining issues (#1335 and #1363) address edge-case data hygiene and user control over staged local changes.

---

## Background

### Current State

Epic #1338 delivered the following on main:

| Issue | Title | Status | Version |
|-------|-------|--------|---------|
| #1339 | Cache resync after ADO writes | ✅ Done | v0.27.0 |
| #1362 | Notes-only bypass in SaveCommand | ✅ Done | v0.27.0 |
| #1365 | SyncCoordinator eviction cleanup | ✅ Done | v0.27.0 |
| #1364 | save --all continues past failures | ✅ Done | v0.27.0 |
| #1340 | Push-on-write for note and edit | ✅ Done | v0.28.0 |
| #1341 | Extract PendingChangeFlusher | ✅ Done | v0.28.0 |
| #1342 | Converge save/refresh into twig sync | ✅ Done | v0.28.0 |

Two issues were deferred:

- **#1335** — Refresh should fast-forward items with no real pending changes
- **#1363** — `twig discard` — drop pending changes for a work item

### Dirty Tracking Architecture

Twig uses a two-layer dirty tracking system:

1. **`work_items.is_dirty` column** — Boolean flag on the work item row (INTEGER NOT NULL DEFAULT 0). Set by `WorkItem.ChangeState()`, `UpdateField()`, and `AddNote()` (lines 82–100 of WorkItem.cs). Cleared by `MarkSynced(revision)` (line 129). Restored from persistence by `SetDirty()` (internal, line 46).

2. **`pending_changes` table** — Audit log of staged mutations (FK to work_items.id). Rows have `change_type` ("field" or "note"), `field_name`, `old_value`, and `new_value`. Cleared by `ClearChangesAsync(id)` after successful push.

`SyncGuard.GetProtectedItemIdsAsync` (static utility, `Twig.Domain/Services/SyncGuard.cs`) unions both sources — items with `is_dirty = 1` OR items with rows in `pending_changes` — to determine which items should be skipped during refresh.

### Call-Site Audit: SyncGuard.GetProtectedItemIdsAsync

| File | Method | Line | Usage | Impact of Phantom Dirty Fix |
|------|--------|------|-------|-----------------------------|
| `RefreshCommand.cs` | `ExecuteCoreAsync` | ~121 | Computes `protectedIds` set for refresh skip logic | **Primary insertion point** — cleanse before this call |
| `RefreshOrchestrator.cs` | `FetchItemsAsync` | ~62 | Computes `protectedIds` for orchestrated refresh path | **Secondary insertion point** — cleanse before this call |
| `ProtectedCacheWriter.cs` | `SaveBatchProtectedAsync` | ~30 | Computes protection set at write time | No change needed — benefits from upstream cleansing |
| `ProtectedCacheWriter.cs` | `SaveProtectedAsync` | ~80 | Single-item write protection | No change needed — benefits from upstream cleansing |
| `WorkingSetService.cs` | `ComputeAsync` | ~68 | Computes dirty IDs for workspace display | No change needed — informational only |

### Call-Site Audit: IPendingChangeStore.ClearChangesAsync

| File | Method | Line | Usage | Impact of Discard |
|------|--------|------|-------|--------------------|
| `PendingChangeFlusher.cs` | `FlushAsync` | ~94 | Accept-remote conflict resolution | No change |
| `PendingChangeFlusher.cs` | `FlushAsync` | ~115 | Post-push cleanup | No change |
| `SyncCoordinator.cs` | `SyncItemAsync` | ~72 | Not-found item eviction | No change |
| `SyncCoordinator.cs` | `FetchStaleAndSaveAsync` | ~157 | Batch not-found eviction | No change |

### The Phantom Dirty Problem (#1335)

With push-on-write, most changes push immediately. The only time pending_changes accumulate is during offline fallback. However, several edge cases can leave items in a "phantom dirty" state:

- **Case A**: `is_dirty = 1` but zero `pending_changes` rows. Occurs when a command sets `IsDirty = true` but the process fails before writing to `pending_changes`, or when `pending_changes` were cleared but the item wasn't resynced from ADO.
- **Case B**: `pending_changes` rows exist but their values match the current remote state (stale changes already applied via another path). This is a rarer edge case that is out of scope for this epic.

Phantom dirty items are permanently "stuck" — they're skipped by every refresh cycle (because `SyncGuard` protects them), so they never get updated. The fix is to detect and cleanse phantom dirty items before the SyncGuard check.

### The Missing Discard Escape Hatch (#1363)

Currently there is no way to drop pending changes without pushing them. If a user stages changes locally (via offline fallback) and then decides they don't want them, the only recourse is manual SQLite manipulation. This was discovered when items had stuck notes due to metadata conflicts (#1362) — the only way to clean up was `sqlite3` direct SQL. `twig discard` provides the CLI-native escape hatch.

---

## Problem Statement

1. **Phantom dirty items never refresh**: Items with `is_dirty = 1` but zero pending changes are permanently skipped by every sync/refresh cycle. Users see stale data and conflict warnings for items that have no actual pending mutations.

2. **No CLI escape hatch for pending changes**: Users cannot drop staged local changes without manual SQLite access. This is a usability gap that causes friction when changes are stuck or unwanted.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Items with `is_dirty = 1` and zero `pending_changes` rows are automatically cleansed during sync/refresh, allowing them to be refreshed from ADO.
2. **G-2**: `twig discard <id>` drops all pending changes for a specific published work item and clears its dirty flag.
3. **G-3**: `twig discard --all` drops all pending changes across all published items.
4. **G-4**: Discard requires confirmation (bypass with `--yes`).
5. **G-5**: Discard reports what was dropped (notes count, field edits count, item IDs).
6. **G-6**: Discard supports `--output json` for scripting.

### Non-Goals

- **NG-1**: Detecting stale pending changes where values match the remote state (Case B above). This is a future enhancement requiring per-change remote comparison.
- **NG-2**: Selective discard (e.g., discard only notes, or discard a specific field change). The MVP discards all changes for an item.
- **NG-3**: Undo/redo for discard. Once discarded, changes are gone.
- **NG-4**: Discarding seed changes. Seeds use `twig seed discard` for deletion. `twig discard` operates only on published items.

---

## Requirements

### Functional

| ID | Requirement | Issue |
|----|-------------|-------|
| FR-1 | Refresh/sync automatically clears `is_dirty` on items with zero `pending_changes` rows | #1335 |
| FR-2 | Phantom dirty cleansing runs before `SyncGuard` protected-set computation | #1335 |
| FR-3 | `twig discard <id>` removes all `pending_changes` rows for the item and clears `is_dirty` | #1363 |
| FR-4 | `twig discard --all` removes all `pending_changes` rows and clears all `is_dirty` flags (published items only) | #1363 |
| FR-5 | Confirmation prompt before discard; `--yes` bypasses it | #1363 |
| FR-6 | Reports discarded counts (notes, field edits) per item | #1363 |
| FR-7 | `--output json` produces structured JSON output | #1363 |
| FR-8 | No-op message when nothing to discard | #1363 |
| FR-9 | Error when target item not found in cache | #1363 |
| FR-10 | Error when target item is a seed (direct to `twig seed discard`) | #1363 |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | Phantom dirty cleansing uses a single atomic SQL query (no per-item round-trips) |
| NFR-2 | Discard for `--all` uses bulk SQL operations (not per-item loop) |
| NFR-3 | All new types use `sealed` classes/records |
| NFR-4 | No reflection-based JSON (Utf8JsonWriter for JSON output) |
| NFR-5 | TreatWarningsAsErrors compliance |
| NFR-6 | Telemetry: safe properties only (command name, duration, counts — no IDs, names, or content) |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      CLI Command Layer                          │
│                                                                 │
│  SyncCommand                      DiscardCommand (NEW)          │
│  ├─ PendingChangeFlusher          ├─ IPendingChangeStore        │
│  └─ RefreshCommand ───┐           ├─ IWorkItemRepository       │
│                       │           ├─ IConsoleInput              │
│                       │           └─ IPromptStateWriter?        │
│  RefreshCommand       │                                         │
│  ├─ phantom dirty ────┤                                         │
│  │   cleansing (NEW)  │                                         │
│  ├─ SyncGuard         │                                         │
│  ├─ ProtectedCacheWriter                                        │
│  └─ ...               │                                         │
└───────────────────────┼─────────────────────────────────────────┘
                        │
┌───────────────────────┼─────────────────────────────────────────┐
│                  Domain Interfaces                               │
│                                                                 │
│  IWorkItemRepository                                            │
│  ├─ (existing methods)                                          │
│  ├─ ClearPhantomDirtyFlagsAsync() → int         (NEW, #1335)   │
│  └─ ClearDirtyFlagAsync(int id)                 (NEW, #1363)   │
│                                                                 │
│  IPendingChangeStore                                            │
│  ├─ (existing methods)                                          │
│  ├─ ClearAllChangesAsync() → int                (NEW, #1363)   │
│  └─ GetChangeSummaryAsync(int id) → (int Notes, int FieldEdits) (NEW, #1363)   │
└─────────────────────────────────────────────────────────────────┘
                        │
┌───────────────────────┼─────────────────────────────────────────┐
│              Infrastructure (SQLite)                             │
│                                                                 │
│  SqliteWorkItemRepository                                       │
│  ├─ ClearPhantomDirtyFlagsAsync():                              │
│  │    UPDATE work_items SET is_dirty = 0                        │
│  │    WHERE is_dirty = 1                                        │
│  │    AND id NOT IN (SELECT DISTINCT work_item_id               │
│  │                   FROM pending_changes)                      │
│  │    AND is_seed = 0                                           │
│  │                                                              │
│  └─ ClearDirtyFlagAsync(id):                                   │
│       UPDATE work_items SET is_dirty = 0 WHERE id = @id        │
│                                                                 │
│  SqlitePendingChangeStore                                       │
│  ├─ ClearAllChangesAsync():                                    │
│  │    DELETE FROM pending_changes                               │
│  │    WHERE work_item_id NOT IN (SELECT id FROM work_items      │
│  │                               WHERE is_seed = 1)            │
│  │                                                              │
│  └─ GetChangeSummaryAsync(id):                                 │
│       SELECT change_type, COUNT(*) FROM pending_changes         │
│       WHERE work_item_id = @id GROUP BY change_type            │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. Phantom Dirty Cleansing (Issue #1335)

A single new method on `IWorkItemRepository`:

```csharp
/// <summary>
/// Clears is_dirty on published items that have no corresponding
/// pending_changes rows. Returns the number of items cleansed.
/// </summary>
Task<int> ClearPhantomDirtyFlagsAsync(CancellationToken ct = default);
```

SQL implementation:
```sql
UPDATE work_items SET is_dirty = 0
WHERE is_dirty = 1
  AND is_seed = 0
  AND id NOT IN (SELECT DISTINCT work_item_id FROM pending_changes);
```

This is called at the start of `RefreshCommand.ExecuteCoreAsync` (line ~119), before `SyncGuard.GetProtectedItemIdsAsync` (line ~121). Since `SyncCommand` delegates to `RefreshCommand`, it gets this cleansing for free. The same cleansing call is also added to `RefreshOrchestrator.FetchItemsAsync` (line ~60, before its SyncGuard call at line ~62) to cover the orchestrated refresh path used in integration tests and the TUI.

#### 2. DiscardCommand (Issue #1363)

A new command class combining two established patterns: **SeedDiscardCommand** for the discard UX flow (fetch → validate → confirm → delete), and **SaveCommand / NoteCommand** for the mutation-tracking pattern (`IPromptStateWriter` for prompt state refresh after mutations). The additional `ITelemetryClient` dependency follows the standard telemetry pattern used by StatusCommand, RefreshCommand, and other instrumented commands:

```csharp
public sealed class DiscardCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    ITelemetryClient? telemetryClient = null,
    IPromptStateWriter? promptStateWriter = null)
{
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool all = false,
        bool yes = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}
```

**Parameter exclusivity guard (DD-10)**: If both `id` and `--all` are provided, `DiscardCommand` returns an error: `"Cannot specify both <id> and --all."` This is validated as the first guard before any database access, following a fail-fast pattern. If neither `id` nor `--all` is provided, the command also returns an error: `"Specify a work item ID or --all."` See DD-10 for rationale.

**`IPromptStateWriter`**: Discard is a mutating command — it removes pending changes and clears dirty flags — so the local prompt state file (`.twig/prompt.json`) may become stale. Following the established pattern used by SaveCommand, NoteCommand, EditCommand, SetCommand, and RefreshCommand, DiscardCommand calls `promptStateWriter.WritePromptStateAsync()` after a successful discard to keep prompt state consistent. The parameter is optional (nullable) to support unit-test construction without the infrastructure dependency.

**Single-item flow** (`twig discard <id>`):
1. Look up item in cache. Return error if not found or is a seed.
2. Get pending changes for the item via `GetChangeSummaryAsync(id)`.
3. **Early-return guard** — evaluate pending-change count and dirty state:
   - **(a) Phantom-dirty path**: Zero pending changes AND `is_dirty = 1` → clear the dirty flag via `ClearDirtyFlagAsync(id)` and report: `"Cleared stale dirty flag for #ID (no pending changes)."` Skip confirmation — no user data is being destroyed. Return 0.
   - **(b) No-op path**: Zero pending changes AND `is_dirty = 0` → report: `"Nothing to discard for #ID."` Return 0.
   - **(c) Continue**: Has pending changes → proceed to step 4.
4. Count notes and field edits from pending changes.
5. Prompt for confirmation (unless `--yes`).
6. `ClearChangesAsync(id)` — remove all pending_changes rows.
7. `ClearDirtyFlagAsync(id)` — clear `is_dirty` flag.
8. Report: `"Discarded N notes and M field edits for #ID."`

**All-items flow** (`twig discard --all`):
1. Get all dirty item IDs (from pending_changes + work_items).
2. Exclude seeds.
3. Return no-op if none.
4. Aggregate counts per item for reporting.
5. Prompt for confirmation (unless `--yes`).
6. `ClearAllChangesAsync()` — remove all non-seed pending_changes.
7. `ClearPhantomDirtyFlagsAsync()` — clear all now-phantom dirty flags (equivalent to clearing all non-seed dirty flags, since step 6 removed all their pending changes).
8. Report: "Discarded N notes and M field edits across K items."

#### 3. Infrastructure Methods

New methods on existing interfaces:

**`IPendingChangeStore`:**
- `ClearAllChangesAsync(CancellationToken)` — Deletes all pending_changes for non-seed items. Returns count of deleted rows.
- `GetChangeSummaryAsync(int workItemId, CancellationToken)` — Returns `(int Notes, int FieldEdits)` counts for reporting (ValueTuple; no separate record type needed).

**`IWorkItemRepository`:**
- `ClearPhantomDirtyFlagsAsync(CancellationToken)` — Atomic SQL clearing phantom dirty flags. Returns count.
- `ClearDirtyFlagAsync(int id, CancellationToken)` — Clears `is_dirty` for a single item.

### Data Flow

#### Fast-Forward During Refresh

```
twig sync / twig refresh
  │
  ├─ (sync: flush pending changes first)
  │
  ├─ ClearPhantomDirtyFlagsAsync()       ← NEW: cleanse phantom dirty
  │   └─ Returns count (logged if > 0)
  │
  ├─ SyncGuard.GetProtectedItemIdsAsync() ← now returns fewer items
  │
  ├─ ProtectedCacheWriter.SaveBatchProtectedAsync()
  │   └─ Previously-phantom items are no longer skipped
  │
  └─ (rest of refresh unchanged)
```

#### Discard Flow

```
twig discard 1294
  │
  ├─ Parameter guard: id + --all → error; neither → error
  │
  ├─ Lookup item #1294 in cache
  │   ├─ Not found → error
  │   └─ Is seed → error ("use twig seed discard")
  │
  ├─ GetChangeSummaryAsync(1294)
  │   └─ Early-return guard:
  │       ├─ (a) {notes: 0, field: 0} AND is_dirty:
  │       │   └─ ClearDirtyFlagAsync(1294)
  │       │       └─ "Cleared stale dirty flag for #1294 (no pending changes)."
  │       ├─ (b) {notes: 0, field: 0} AND NOT is_dirty:
  │       │   └─ "Nothing to discard for #1294."
  │       └─ (c) {notes: 2, field: 1} → continue
  │
  ├─ "Discard 2 notes and 1 field edit for #1294? (y/N)"
  │   ├─ N → "Discard cancelled."
  │   └─ y → continue
  │
  ├─ ClearChangesAsync(1294)
  ├─ ClearDirtyFlagAsync(1294)
  │
  └─ "Discarded 2 notes and 1 field edit for #1294."
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Single atomic SQL for phantom dirty cleansing | Avoids per-item round-trips (NFR-1). A subquery against `pending_changes` is efficient with the existing `work_item_id` index. |
| DD-2 | Cleansing runs in `RefreshCommand`, not `SyncGuard` | SyncGuard should remain a pure query with no side effects. RefreshCommand owns the lifecycle. |
| DD-3 | Seed items excluded from all bulk operations | Seeds' pending changes define their content. Clearing them would destroy uncommitted seed data. `twig seed discard` exists for seed deletion. |
| DD-4 | DiscardCommand in CLI layer (not domain service) | Follows existing pattern (SeedDiscardCommand). No complex domain logic — just DB cleanup and CLI output. |
| DD-5 | Utf8JsonWriter for JSON output | AOT-compatible (no reflection). Matches pattern used by SyncCommand. |
| DD-6 | `GetChangeSummaryAsync` for reporting | Avoids loading full pending_change rows just to count them. Single GROUP BY query. |
| DD-7 | No transaction wrapping for single-item discard | See **DD-7 Detail** below. |
| DD-8 | `ClearAllChangesAsync` intentionally cleans orphaned `pending_changes` rows | `ClearAllChangesAsync` uses `WHERE work_item_id NOT IN (SELECT id FROM work_items WHERE is_seed = 1)` — this deletes orphaned `pending_changes` rows whose `work_item_id` no longer exists in `work_items`. This is an intentional side effect: orphaned rows are dead weight left by evictions or other data cleanup, and `--all` is the appropriate time to sweep them. After this call, `ClearPhantomDirtyFlagsAsync` handles dirty-flag cleanup (all remaining non-seed dirty items now have zero pending changes). |
| DD-9 | `IPromptStateWriter` in DiscardCommand | Follows the established pattern for all mutating commands (SaveCommand, NoteCommand, EditCommand, SetCommand, RefreshCommand). Prompt state reflects dirty-item counts, so discarding changes invalidates it. |
| DD-10 | Explicit mutual exclusivity guard for `<id>` vs `--all` | If both are provided, the command fails fast with `"Cannot specify both <id> and --all."` If neither is provided, fails with `"Specify a work item ID or --all."` This avoids ambiguous behavior where `--all` might silently override `<id>`. The guard runs before any database access, following the fail-fast principle. ConsoleAppFramework cannot enforce this at the framework level, so the command validates it at the top of `ExecuteAsync`. |
| DD-11 | No output truncation for `twig discard --all` | The `--all` flow reports per-item discard counts inline. Truncation is unnecessary because: (1) the typical dirty-item population is < 20 (push-on-write means changes rarely accumulate), (2) `--output json` provides machine-parseable output for programmatic consumers who might have larger sets, and (3) adding pagination to a destructive confirmation prompt would add complexity disproportionate to the use case. |

#### DD-7 Detail: No Transaction Wrapping for Single-Item Discard

Single-item discard executes two sequential SQL statements (`ClearChangesAsync` then `ClearDirtyFlagAsync`) without an explicit transaction wrapper. The five key considerations:

- **Crash semantics**: If the process crashes between the two statements, the item is left with `is_dirty = 1` but zero `pending_changes` rows — a phantom dirty state.
- **Safety net**: Issue #1335's `ClearPhantomDirtyFlagsAsync` (which runs on every sync/refresh) automatically cleanses phantom dirty items on the next cycle.
- **Epic cohesion**: This explicit dependency on #1335 is why both issues belong to the same epic and why PR Group 1 is a prerequisite for PR Group 2.
- **Simplicity**: Wrapping in a transaction would require exposing `IUnitOfWork` to the command layer and coordinating across two different stores (`IPendingChangeStore` + `IWorkItemRepository`), adding complexity for a crash window measured in microseconds.
- **Precedent**: `PendingChangeFlusher.FlushAsync` already uses the same pattern — sequential calls to `ClearChangesAsync` then `SaveAsync` without transaction wrapping (lines ~94–115).

---

## Alternatives Considered

### Transaction Wrapping for Single-Item Discard (rejected — DD-7)

**Approach**: Wrap `ClearChangesAsync` + `ClearDirtyFlagAsync` in an explicit SQLite transaction via a new `IUnitOfWork` abstraction.

**Pros:**
- Eliminates the theoretical crash-window that could leave phantom dirty state.
- Strict atomicity guarantees.

**Cons:**
- Requires a new `IUnitOfWork` or `ITransactionScope` interface — none exists in the codebase today.
- Coordinates across two stores (`IPendingChangeStore` + `IWorkItemRepository`), both of which hold their own connection references.
- The crash window is measured in microseconds between two sequential in-process SQLite calls.
- `PendingChangeFlusher.FlushAsync` already uses the same non-transactional pattern without issues.

**Decision**: Rejected. The phantom dirty cleansing safety net (#1335) provides automatic recovery on the next sync/refresh, making explicit transaction wrapping disproportionately complex. If a future epic introduces cross-store transactions for other reasons, discard could adopt it opportunistically.

### Domain Service Layer for Discard Logic (rejected — DD-4)

**Approach**: Create a `DiscardService` in `Twig.Domain/Services/` to encapsulate the discard orchestration logic, with `DiscardCommand` as a thin CLI adapter.

**Pros:**
- Follows the "thin controller" pattern; domain logic stays in the domain layer.
- Could be reused by hypothetical future TUI discard flows.

**Cons:**
- The discard logic is primarily CLI ceremony (confirmation prompts, output formatting, telemetry) with minimal domain logic — just two SQL calls.
- `SeedDiscardCommand` sets the precedent: discard commands live in the CLI layer when the logic is straightforward DB cleanup + CLI output.
- Introducing a domain service would add a file, an interface, and DI registration for no reuse benefit today.

**Decision**: Rejected. Following the `SeedDiscardCommand` precedent, keep logic in the command class. If a TUI discard flow emerges, extract at that point.

---

## Dependencies

### Internal
- `IPendingChangeStore` interface — adding new methods
- `IWorkItemRepository` interface — adding new methods
- `SqlitePendingChangeStore` — implementing new methods
- `SqliteWorkItemRepository` — implementing new methods
- `RefreshCommand` — integrating phantom dirty cleansing
- `CommandRegistrationModule` — registering DiscardCommand
- `Program.cs` (TwigCommands) — routing `twig discard`

### External
- No new external dependencies

### Sequencing
- **PR Group 1 (#1335) must merge before PR Group 2 (#1363).** See [PR Groups](#pr-groups) for the detailed dependency rationale.

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `IWorkItemRepository` | Interface expansion (2 new methods) | Low — additive, no existing method changes |
| `IPendingChangeStore` | Interface expansion (2 new methods) | Low — additive |
| `SqliteWorkItemRepository` | 2 new method implementations | Low — standard SQL operations |
| `SqlitePendingChangeStore` | New method implementations | Low — standard SQL operations |
| `RefreshCommand` | Add 3-line cleansing call at start of ExecuteCoreAsync | Low — no behavioral change for items with real changes |
| `RefreshOrchestrator` | Add 3-line cleansing call at start of FetchItemsAsync | Low — parallel to RefreshCommand fix |
| `Program.cs` | New `Discard` method in TwigCommands | Low — additive |
| `CommandRegistrationModule` | Register DiscardCommand | Low — additive |

### Backward Compatibility

- **No breaking changes.** All additions are new methods and new command.
- Phantom dirty cleansing is transparent — items that were previously stuck will now refresh correctly. This is a bug fix, not a behavioral change.
- `twig discard` is a new verb with no overlap with existing commands.

### Performance

- `ClearPhantomDirtyFlagsAsync` adds one SQL query per refresh (~1ms). Negligible.
- `twig discard` is a user-invoked command — no performance sensitivity.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Phantom dirty cleansing accidentally clears items that should stay dirty | Low | High | SQL WHERE clause explicitly checks `NOT IN (pending_changes)` AND `is_seed = 0`. Test with items in all states. |
| `twig discard --all` destroys wanted changes | Low | Medium | Confirmation prompt required. `--yes` flag clearly documented as bypass. Verbose reporting of what was discarded. |
| Interface expansion breaks test mocks | Low | Low | NSubstitute mocks auto-handle new methods (return default). Only tests that explicitly verify the new methods need updates. |

---

## Open Questions

All design decisions for this epic are resolved. The one deferred area is **stale-change detection** (Case B from the Background section): items whose `pending_changes` rows match the current remote state. This is explicitly out of scope (NG-1) and would require per-change remote comparison logic — a meaningful design effort better suited to a future epic once the foundational dirty-tracking hygiene from this epic is in place. If Case B proves common in practice, telemetry from `twig discard` usage (item counts, frequency) will inform whether to prioritize it.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/DiscardCommand.cs` | `twig discard` command implementation |
| `tests/Twig.Cli.Tests/Commands/DiscardCommandTests.cs` | Tests for DiscardCommand |
| `tests/Twig.Infrastructure.Tests/Persistence/PhantomDirtyCleansingTests.cs` | Tests for phantom dirty cleansing SQL via SqliteWorkItemRepository |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `ClearPhantomDirtyFlagsAsync`, `ClearDirtyFlagAsync` (2 new methods) |
| `src/Twig.Domain/Interfaces/IPendingChangeStore.cs` | Add `ClearAllChangesAsync`, `GetChangeSummaryAsync` |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement 2 new methods |
| `src/Twig.Infrastructure/Persistence/SqlitePendingChangeStore.cs` | Implement 2 new methods |
| `src/Twig/Commands/RefreshCommand.cs` | Add phantom dirty cleansing call (~3 lines) before SyncGuard at line ~119 |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Add phantom dirty cleansing call (~3 lines) before SyncGuard at line ~60 |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `DiscardCommand` |
| `src/Twig/Program.cs` | Add `Discard` method to `TwigCommands` |

---

## ADO Work Item Structure

### Issue #1335: Refresh should fast-forward items with no real pending changes

**Goal**: Items with `is_dirty = 1` but zero `pending_changes` rows are automatically cleansed during sync/refresh, so they're no longer blocked from receiving remote updates.

**Prerequisites**: None — independent of #1363.

**Tasks:**

| Task ID | Description | Files | Effort | FRs Covered |
|---------|-------------|-------|--------|-------------|
| T-1335.1 | Add `ClearPhantomDirtyFlagsAsync()` to `IWorkItemRepository` interface and `SqliteWorkItemRepository` implementation. Single atomic SQL UPDATE with NOT IN subquery. | `IWorkItemRepository.cs`, `SqliteWorkItemRepository.cs` | Small | FR-1, NFR-1 |
| T-1335.2 | Integrate cleansing into `RefreshCommand.ExecuteCoreAsync` (line ~119, before SyncGuard) and `RefreshOrchestrator.FetchItemsAsync` (line ~60, before SyncGuard). Call `ClearPhantomDirtyFlagsAsync()` at both sites. Log count to stderr if > 0. | `RefreshCommand.cs`, `RefreshOrchestrator.cs` | Small | FR-2 |
| T-1335.3 | Unit tests using in-memory SQLite: phantom dirty items are cleansed; items with real pending changes are untouched; seed items are never cleansed; cleansing count is accurate. Tests live in Infrastructure.Tests because they verify actual SQL behavior against a real SQLite database (following the pattern in `SqliteWorkItemRepositoryTests.cs` and `SqlitePendingChangeStoreTests.cs`). | `PhantomDirtyCleansingTests.cs` (Infrastructure.Tests/Persistence/) | Small | FR-1, FR-2 |

**Acceptance Criteria:**
- [ ] Items with `is_dirty = 1` and zero `pending_changes` rows are automatically cleared during refresh
- [ ] Items with real `pending_changes` rows remain protected (not cleared)
- [ ] Seed items are never affected by phantom dirty cleansing
- [ ] Cleansing runs before SyncGuard in both `twig sync` and `twig refresh` paths (RefreshCommand) and in `RefreshOrchestrator` (TUI/integration path)
- [ ] Informational log message when items are cleansed

---

### Issue #1363: twig discard — drop pending changes for a work item

**Goal**: Add a `twig discard` command that drops staged pending changes (notes, field edits) for a specific work item or all items, providing a CLI-native escape hatch for stuck or unwanted local changes.

**Prerequisites**: PR Group 1 (#1335) must land first — the `--all` flow reuses `ClearPhantomDirtyFlagsAsync()`, and DD-7's crash-safety argument depends on it.

**Tasks:**

| Task ID | Description | Files | Effort | FRs Covered |
|---------|-------------|-------|--------|-------------|
| T-1363.1 | Add infrastructure methods: `IPendingChangeStore.ClearAllChangesAsync()`, `IPendingChangeStore.GetChangeSummaryAsync()` (returns `(int Notes, int FieldEdits)` ValueTuple), `IWorkItemRepository.ClearDirtyFlagAsync()`. Note: `--all` reuses `ClearPhantomDirtyFlagsAsync()` (from #1335) for dirty-flag cleanup — no separate `ClearAllDirtyFlagsAsync` needed. | `IPendingChangeStore.cs`, `SqlitePendingChangeStore.cs`, `IWorkItemRepository.cs`, `SqliteWorkItemRepository.cs` | Small | FR-3, FR-4, FR-6, NFR-2, NFR-3 |
| T-1363.2 | Create `DiscardCommand` class. **(1)** Parameter exclusivity guard: both `id` and `--all` → error; neither → error (DD-10). **(2)** Single-item flow: fetch → seed guard → early-return guard with three branches (phantom-dirty / no-op / continue) → summary → confirm → clear → report. **(3)** All-items flow: aggregate dirty items → exclude seeds → confirm → bulk clear → report. **(4)** Confirmation prompt via `IConsoleInput.ReadLine()` (following SeedDiscardCommand pattern); `--yes` bypasses. **(5)** Human-readable output via `OutputFormatterFactory`. **(6)** JSON output via `Utf8JsonWriter` — structure: `{ "items": [{"id", "notes", "fieldEdits"}], "totalNotes", "totalFieldEdits", "totalItems" }`. **(7)** Telemetry via `ITelemetryClient?.TrackEvent("CommandExecuted", ...)` with Stopwatch — safe properties only: `command="discard"`, `exit_code`, `output_format`, `duration_ms`, `item_count`, `used_all`. **(8)** `IPromptStateWriter` call after successful discard (see DD-9). | `DiscardCommand.cs` | Medium | FR-3–FR-10, NFR-4, NFR-6 |
| T-1363.3 | Register `DiscardCommand` in `CommandRegistrationModule` and add `Discard` method to `TwigCommands` in `Program.cs`. | `CommandRegistrationModule.cs`, `Program.cs` | Small | — |
| T-1363.4 | Unit tests: discard single item clears changes + dirty; discard --all clears everything (non-seed); phantom-dirty-only item gets distinct messaging; confirmation prompt cancels on 'n'; --yes bypasses prompt; no-op when nothing to discard; seed rejection; JSON output structure; item-not-found error; mutual exclusivity guard (id + --all → error; neither → error). | `DiscardCommandTests.cs` | Medium | FR-3–FR-10, DD-10 |

**Acceptance Criteria:**
- [ ] `twig discard <id>` removes all `pending_changes` rows for that item and clears `is_dirty`
- [ ] `twig discard <id>` on a phantom-dirty item (dirty flag but no pending changes) clears the dirty flag and reports distinct messaging
- [ ] `twig discard --all` clears all non-seed `pending_changes` and all non-seed `is_dirty` flags
- [ ] Confirmation prompt shown unless `--yes` is passed
- [ ] `--output json` produces structured JSON output
- [ ] No-op message when there are no pending changes to discard
- [ ] Error message when item not found in cache
- [ ] Error message when item is a seed (directs to `twig seed discard`)
- [ ] Reports discarded counts: "Discarded N notes and M field edits for #ID"
- [ ] Error when both `<id>` and `--all` are specified; error when neither is specified

---

## PR Groups

### PR Group 1: Fast-forward phantom dirty items

**Issues**: #1335  
**Tasks**: T-1335.1, T-1335.2, T-1335.3  
**Type**: Deep (few files, targeted behavior change in refresh path)  
**Estimated LoC**: ~220 (implementation ~70, tests ~150)  
**Files**: ~6
**Successors**: PR Group 2 (compile-time dependency on `ClearPhantomDirtyFlagsAsync`)  

### PR Group 2: twig discard command

**Issues**: #1363  
**Tasks**: T-1363.1, T-1363.2, T-1363.3, T-1363.4  
**Type**: Deep (new command with infrastructure methods and comprehensive tests)  
**Estimated LoC**: ~700 (implementation ~300, tests ~400)  
**Files**: ~9  
**Prerequisites**: PR Group 1 must be merged first  
**Successors**: None

**Execution Order**: PR Group 1 → PR Group 2. PR Group 2's `--all` flow calls `ClearPhantomDirtyFlagsAsync()` (introduced by PR Group 1), creating a compile-time dependency. DD-7's crash-safety argument also relies on this method running during sync/refresh to automatically recover from partial discard failures. Development can proceed in parallel, but PR Group 2 cannot merge until PR Group 1 lands.

---

## References

- [Push-on-Write and Sync Convergence (Epic #1338)](push-on-write-sync-convergence.plan.md) — Original plan document (Revision 7, Done)
- ADO Work Items: [#1394](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1394) (Epic), [#1335](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1335), [#1363](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1363)
