# Push-on-Write and Sync Convergence

**Epic:** #1338  
> **Status**: ✅ Done  
**Revision:** 7 — See [Revision History](#revision-history) for change log.

---

## Executive Summary

This epic transforms twig’s data flow from a fragile stage-then-save model to an immediate push-on-write model with automatic cache resync, and converges the `save` and `refresh` commands into a unified `twig sync` command. Today, `twig note` and `twig edit` stage changes locally requiring a separate `twig save`, while `twig update` and `twig state` push immediately but leave the local cache stale. Both patterns cause the local revision to diverge from ADO, producing phantom conflict warnings on subsequent operations. This epic fixes the root cause by ensuring all writes push immediately (with offline fallback) and resync the cache, then unifies the sync direction under a single `twig sync` command. It also fixes eviction-related orphaned pending_changes and makes `twig save --all` resilient to individual item failures.

---

## Background

### Current Architecture

The twig CLI has two distinct write patterns for modifying ADO work items:

1. **Immediate-push commands** (`update`, `state`): These push changes to ADO immediately via `PatchAsync`, but only `UpdateCommand` does a full resync (`FetchAsync` + `SaveAsync`) afterward. `StateCommand` applies changes to the in-memory aggregate and calls `MarkSynced(newRevision)`, which updates the revision but does NOT fetch server-side computed fields like `ChangedDate`, `ChangedBy`, and `Reason`. This causes metadata drift.

2. **Stage-then-save commands** (`note`, `edit`): These stage changes in the `pending_changes` table and mark the item dirty. Users must explicitly run `twig save` to push them to ADO. Forgetting `save` leads to stale pending changes that trigger phantom conflict warnings on `refresh`.

3. **Auto-push notes** (`AutoPushNotesHelper`): When `update` or `state` runs, it auto-pushes any pending notes via `PushAndClearAsync`. This creates an inconsistency where notes might be pushed as a side effect of a different command.

### Call-Site Audit

The following table inventories all current call sites for the services and patterns being modified:

#### `ConflictRetryHelper.PatchWithRetryAsync` Call Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SaveCommand.cs:132` | `ExecuteAsync` | Patches field changes, discards return value | Returns new rev; already followed by FetchAsync |
| `UpdateCommand.cs:67` | `ExecuteAsync` | Patches field change, discards return value | Already followed by FetchAsync resync |
| `StateCommand.cs:101` | `ExecuteAsync` | Patches state change, uses return value for `MarkSynced` | **Must add FetchAsync resync** |

#### `adoService.AddCommentAsync` Call Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SaveCommand.cs:138` | `ExecuteAsync` | Pushes notes in save loop | Stays (flusher extracts this) |
| `AutoPushNotesHelper.cs:28` | `PushAndClearAsync` | Pushes pending notes after update/state | Stays for auto-push |

#### `pendingChangeStore.AddChangeAsync` Call Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `NoteCommand.cs:64` | `ExecuteAsync` | Stages note as pending | **Push immediately for non-seeds** |
| `EditCommand.cs:91` | `ExecuteAsync` | Stages field edits as pending | **Push immediately for non-seeds** |

#### `pendingChangeStore.ClearChangesAsync` Call Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SaveCommand.cs:104` | `ExecuteAsync` | `onAcceptRemote` callback closure passed to `ConflictResolutionFlow.ResolveAsync` — clears pending changes when user accepts remote | Moves to PendingChangeFlusher |
| `SaveCommand.cs:144` | `ExecuteAsync` | Clears after successful push of field changes and notes | Moves to PendingChangeFlusher |

> **Note:** `ClearChangesByTypeAsync` (note-only variant) is also called by `AutoPushNotesHelper.cs:34` to clear just note-type changes after auto-pushing them. This call is unaffected.

#### `SyncCoordinator.DeleteByIdAsync` Eviction Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SyncCoordinator.cs:72` | `SyncItemAsync` | Deletes on "not found" | **Must also clear pending_changes** |
| `SyncCoordinator.cs:154` | `FetchStaleAndSaveAsync` | Batch eviction of deleted items | **Must also clear pending_changes** |

#### `workItemRepo.SaveAsync` (post-write resync) Call Sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SaveCommand.cs:146` | `ExecuteAsync` | After FetchAsync (full resync) | Moves to PendingChangeFlusher |
| `UpdateCommand.cs:78` | `ExecuteAsync` | After FetchAsync (full resync) | Already correct |
| `StateCommand.cs:110` | `ExecuteAsync` | Saves local aggregate, NOT fetched | **Must add FetchAsync resync** |
| `NoteCommand.cs:74` | `ExecuteAsync` | Saves dirty local (no push) | **Changes to push + resync** |
| `EditCommand.cs:110` | `ExecuteAsync` | Saves dirty local (no push) | **Changes to push + resync** |

---

## Problem Statement

1. **Cache divergence after writes**: `twig state` updates the in-memory revision via `MarkSynced(newRevision)` but does not re-fetch server-computed fields. The local cache drifts on `ChangedDate`, `ChangedBy`, `Reason`, and `StateChangeDate`. When `twig save` runs later for notes, it detects "conflicts" on these metadata fields — false positives from stale cache data.

2. **Forgotten save**: `twig note` and `twig edit` stage changes locally. Users must remember to run `twig save` — forgetting leads to stale pending changes that block `twig refresh` with phantom conflict warnings, and the changes sit orphaned until the next save.

3. **Two commands for one concept**: `twig save` (push local → remote) and `twig refresh` (pull remote → local) are separate verbs for what is conceptually a sync operation. Users must know which one to run and in what order.

4. **No way to discard pending changes**: There is no `twig discard` command to drop staged changes. Users who stage a note or edit they no longer want have no clean way to undo it without manually running `twig save` then reverting, or deleting the database.

5. **Eviction orphans**: When `SyncCoordinator` evicts a deleted item (via `DeleteByIdAsync`), it does not clean up `pending_changes` rows, leaving orphaned records that inflate `dirtyCount` and cause `twig save --all` to attempt pushes to deleted items.

6. **Save --all stops on first failure**: `twig save --all` iterates dirty items sequentially and returns immediately on the first unhandled exception (from FetchAsync, PatchAsync, or AddCommentAsync), leaving subsequent valid items unpushed. The save loop (lines 84-150) has four `continue` paths that handle known conditions gracefully, but exceptions from ADO calls are unguarded:
   - **L91 — null item**: `item is null` → log error, `hadErrors = true`, continue (item not in cache)
   - **L96 — empty pending changes**: `pending.Count == 0` → continue silently (nothing to push)
   - **L108 — conflict JSON emitted**: `ConflictOutcome.ConflictJsonEmitted` → `hadErrors = true`, continue (conflict written to file for user resolution)
   - **L111 — accepted remote / aborted**: `ConflictOutcome.AcceptedRemote or Aborted` → continue (user chose to accept remote or cancel; changes cleared)
   
   Any exception thrown between L113 (FetchAsync) and L149 (SaveAsync/resync) — including network failures, 5xx responses, or auth errors — propagates unhandled and terminates the loop immediately.

---

## Goals and Non-Goals

### Goals

- **G-1**: All edit operations (note, edit, update, state) push to ADO immediately for non-seed items
- **G-2**: Local cache stays in lockstep with ADO after every write (full FetchAsync resync)
- **G-3**: `twig sync` unifies save and refresh into a single command
- **G-4**: `save` and `refresh` become hidden aliases printing deprecation hints
- **G-5**: `PendingChangeFlusher` extracted as a reusable CLI-layer service
- **G-6**: `twig save --all` continues past individual failures
- **G-7**: Eviction cleans up pending_changes for deleted items
- **G-8**: Notes-only pending changes skip conflict resolution on metadata fields

### Non-Goals

- **NG-1**: Offline-first mode with queuing — only simple fallback to local staging with a warning
- **NG-2**: Real-time sync or webhooks — twig remains a CLI that syncs on command
- **NG-3**: Removing the pending_changes table — it is still needed for offline fallback and seeds
- **NG-4**: Changing seed behavior — seeds remain local-only until `twig seed publish`
- **NG-5**: `twig discard` command — useful ergonomic improvement but orthogonal to cache-divergence and sync convergence; to be tracked as its own issue

---

## Requirements

### Functional

- **FR-1**: `twig note` pushes immediately to ADO for non-seeds; falls back to local staging on failure
- **FR-2**: `twig edit` pushes immediately to ADO for non-seeds; falls back to local staging on failure
- **FR-3**: `twig state` resyncs local cache via FetchAsync after successful PatchAsync
- **FR-4**: `twig update` continues to resync (already does)
- **FR-5**: `twig sync` flushes offline-staged changes, then refreshes the working set
- **FR-6**: `twig save` and `twig refresh` work as hidden aliases for `twig sync`
- **FR-7**: `twig save --all` (and PendingChangeFlusher) continues past failures
- **FR-8**: SyncCoordinator eviction deletes pending_changes for evicted items
- **FR-9**: Notes-only pending changes bypass field-level conflict resolution

### Non-Functional

- **NFR-1**: No reflection — all new types added to `TwigJsonContext`
- **NFR-2**: No additional ADO API calls on failure paths
- **NFR-3**: Offline fallback must not change exit code (warning only)
- **NFR-4**: Backward compatibility — existing scripts using `save`/`refresh` continue to work

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                       CLI Commands (Twig.Commands)               │
│                                                                  │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐           │
│  │NoteCmd   │ │EditCmd   │ │StateCmd  │ │UpdateCmd │           │
│  │(push-on- │ │(push-on- │ │(+resync) │ │(already  │           │
│  │ write)   │ │ write)   │ │          │ │ resyncs) │           │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └────┬─────┘           │
│       │             │            │             │                  │
│       v             v            v             v                  │
│  ┌──────────────────────────────────────────────────┐           │
│  │              IAdoWorkItemService                  │           │
│  │    PatchAsync / AddCommentAsync / FetchAsync      │           │
│  └──────────────────────────────────────────────────┘           │
│                                                                  │
│  ┌──────────┐                                                    │
│  │SyncCmd   │──push──→ PendingChangeFlusher (CLI layer)         │
│  │(new)     │          (ConflictRetryHelper,                     │
│  │          │           ConflictResolutionFlow)                   │
│  │          │──pull──→ RefreshCommand.ExecuteAsync()             │
│  └──────────┘          (WIQL, config, identity, metadata,        │
│                         type/field sync, telemetry — 264 LoC)    │
│                                                                  │
│  ┌──────────┐ ┌──────────┐                                      │
│  │SaveCmd   │ │RefreshCmd│  (hidden aliases with deprecation     │
│  │(alias)   │ │(keeps    │   hints printed by TwigCommands       │
│  │          │ │ logic)   │   routing in Program.cs)              │
│  └──────────┘ └──────────┘                                      │
└─────────────────────────────────────────────────────────────────┘
```

> **Architectural note — RefreshOrchestrator vs. RefreshCommand:** `RefreshOrchestrator` exists as a domain-layer service (registered in DI, fully tested with 11 test cases) that encapsulates fetch → conflict detection → save → hydration → working set sync. However, `RefreshCommand` does **not** currently consume it. RefreshCommand contains 264 lines of inline logic in `ExecuteCoreAsync` including WIQL construction (with config-based area path filtering), user identity detection via `IIterationService`, global profile metadata updates (field definition hash tracking), cache freshness timestamps, type and field sync via static `ProcessTypeSyncService.SyncAsync()` / `FieldDefinitionSyncService.SyncAsync()`, ancestor hydration, and telemetry. RefreshOrchestrator lacks WIQL construction, config access, identity detection, metadata updates, timestamps, and telemetry — it only covers the fetch→save→hydrate→sync subset. Migrating RefreshCommand to delegate to RefreshOrchestrator is a worthwhile refactor but is out of scope for this epic. `SyncCommand` therefore delegates the pull phase to `RefreshCommand.ExecuteAsync()` directly (see DD-7).

### Key Components

#### 1. PendingChangeFlusher (New CLI Service)

Extracted from `SaveCommand`, owns the flush logic for pushing pending changes to ADO. Lives in the **CLI layer** (`Twig.Commands` namespace) because it depends on `ConflictRetryHelper` and `ConflictResolutionFlow`, both of which are internal static CLI classes that perform console I/O for conflict prompts.

```csharp
// src/Twig/Commands/PendingChangeFlusher.cs
namespace Twig.Commands;

public sealed class PendingChangeFlusher(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public sealed record FlushResult(
        int ItemsFlushed,
        int FieldChangesPushed,
        int NotesPushed,
        IReadOnlyList<FlushItemFailure> Failures);

    public sealed record FlushItemFailure(int ItemId, string Error);

    public async Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);

    public async Task<FlushResult> FlushAllAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}
```

> **API surface note — no `FlushWorkTreeAsync`:** The flusher intentionally does NOT include a `FlushWorkTreeAsync` method. Work-tree scoping (active item + children, intersected with dirty IDs) is a concern of the calling command, not the flusher. `SaveCommand` and `FlowDoneCommand` each resolve the scope themselves (via `ActiveItemResolver` + `GetChildrenAsync` + `GetDirtyItemIdsAsync`) and pass the resulting ID list to `FlushAsync(IReadOnlyList<int>)`. This keeps the flusher's API minimal and avoids injecting `ActiveItemResolver` or `IWorkItemRepository.GetChildrenAsync` into a service whose responsibility is "push pending changes."

**Key behaviors:**
- Iterates items, collects failures, continues past individual errors (FR-7)
- Notes-only items skip conflict resolution — push notes directly (FR-9)
- After each successful push: `ClearChangesAsync` → `FetchAsync` → `SaveAsync` (resync)
- Returns structured `FlushResult` — caller formats output
- Calls `ConflictRetryHelper.PatchWithRetryAsync` for field changes (static, CLI-layer)
- Calls `ConflictResolutionFlow.ResolveAsync` for field conflicts (static, CLI-layer)

> **NFR-1 note — TwigJsonContext exemption:** `FlushResult` and `FlushItemFailure` are internal data-flow records returned to calling commands (SaveCommand, SyncCommand, FlowDoneCommand) for formatting. They are never serialized to JSON — callers read their properties and format human/minimal/json output using `OutputFormatterFactory`. They do NOT require `[JsonSerializable]` registration in `TwigJsonContext`. If a future command needs to serialize them (e.g., for structured JSON output), the records must be registered at that time.

#### 2. SyncCommand (New CLI Command)

Replaces both `save` and `refresh`:

```
twig sync              # flush offline changes + refresh working set
twig sync --force      # flush + overwrite any remaining conflicts
twig sync <id>         # flush + refresh single item
```

Internally calls `PendingChangeFlusher.FlushAllAsync()` for the push phase, then delegates to `RefreshCommand.ExecuteAsync()` for the pull phase. SyncCommand injects both `PendingChangeFlusher` and `RefreshCommand` via DI. This delegates all refresh logic (WIQL, config, identity, metadata, type/field sync, telemetry) to the existing RefreshCommand without duplication. See DD-7 for rationale.

#### 3. Modified NoteCommand (Push-on-Write)

For non-seed items: calls `adoService.AddCommentAsync`, then `FetchAsync` + `SaveAsync` for resync. On failure: falls back to staging locally with a warning.

#### 4. Modified EditCommand (Push-on-Write)

For non-seed items: launches editor, diffs changes, calls `PatchAsync` for field changes, then `FetchAsync` + `SaveAsync` for resync. On failure: falls back to staging locally.

> **New dependency — `IConsoleInput`:** EditCommand currently does not inject `IConsoleInput` (it uses `IEditorLauncher` for user interaction). Push-on-write adds conflict resolution before patching, which requires `ConflictResolutionFlow.ResolveAsync` — and that method requires `IConsoleInput` for interactive conflict prompts (accept local / accept remote / abort). This injection is necessary to match the pattern used by `SaveCommand` and `UpdateCommand`.

#### 5. Modified StateCommand (Add Resync)

After successful `PatchWithRetryAsync`, add `FetchAsync` + `SaveAsync` instead of local-only `MarkSynced`. This picks up all server-computed fields.

### Data Flow: Push-on-Write (Note Example)

```
User runs: twig note "Fix applied"
                │
                v
        Is item a seed? ──yes──> Stage locally (existing behavior)
                │
               no
                │
                v
        AddCommentAsync(id, text) ──failure──> Stage locally + warn
                │                               (ADO never received
                │                                the change; safe
                │                                to retry via save)
              success
                │
                v
        FetchAsync(id)  ──failure──> Warn: "Note pushed but cache
                │                     may be stale. Run twig sync."
                │                     Do NOT stage locally (note is
                │                     already in ADO — staging would
                │                     cause double-push on next save).
              success                 Return success exit code.
                │
                v
        SaveAsync(updated)  →  Print success
```

> **DD-8 edge case — post-push resync failure:** There are two distinct failure modes in the push-on-write flow. (1) **Push failure** (AddCommentAsync or PatchAsync fails): the change never reached ADO, so staging locally for later retry is correct. (2) **Resync failure** (FetchAsync fails after successful push): the change IS in ADO but the local cache is now stale. Staging locally in this case would cause the change to be re-pushed on the next `twig save` or `twig sync`, creating duplicates (double comments or double field patches). Instead, the command prints a warning to stderr suggesting `twig sync` and returns exit code 0 (the user's intent — pushing the change — succeeded). The stale cache self-heals on the next `twig sync` or `twig refresh`.

### Data Flow: twig sync

```
User runs: twig sync [--force]
                │
                v
        PendingChangeFlusher.FlushAllAsync()
        ┌─ For each dirty item:
        │   ├─ Notes-only? → Push notes directly (skip conflict resolution)
        │   ├─ Mixed/fields? → ConflictResolutionFlow.ResolveAsync
        │   │                  → ConflictRetryHelper.PatchWithRetryAsync
        │   │                  → AddCommentAsync (for notes)
        │   ├─ ClearChangesAsync → FetchAsync → SaveAsync (resync)
        │   └─ On failure: record in FlushResult.Failures, continue
        └─ Return FlushResult
                │
                v
        RefreshCommand.ExecuteAsync(outputFormat, force)
        ┌─ Build WIQL from config (area paths, iteration)
        ├─ Fetch sprint items via adoService
        ├─ Fetch active item (if out of sprint)
        ├─ Fetch children
        ├─ ProtectedCacheWriter: save (skip dirty unless --force)
        ├─ HydrateAncestorsAsync (up to 5 levels)
        ├─ SyncCoordinator.SyncWorkingSetAsync
        ├─ ProcessTypeSyncService.SyncAsync (static)
        ├─ FieldDefinitionSyncService.SyncAsync (static)
        ├─ Detect user identity (IIterationService)
        ├─ Update global profile metadata (field def hash)
        ├─ Set last_refreshed_at timestamp
        └─ Telemetry
                │
                v
        Report combined results (flush summary + refresh summary)
```

### Design Decisions

**DD-1: FetchAsync after every write, not MarkSynced**
Using `FetchAsync` + `SaveAsync` after every ADO write ensures all server-computed fields (ChangedDate, ChangedBy, Reason, StateChangeDate) are captured. The cost is one extra API call per write, but this eliminates the entire class of metadata-drift bugs.

**DD-2: Offline fallback via try-catch, not network-check**
Rather than pre-checking connectivity, we attempt the ADO call and catch failures. On failure, we fall back to staging locally. This is simpler and handles transient failures, timeouts, and auth errors uniformly.

**DD-3: PendingChangeFlusher in CLI layer, not Domain**
The flusher must call `ConflictRetryHelper.PatchWithRetryAsync` and `ConflictResolutionFlow.ResolveAsync`, both of which are `internal static` classes in the `Twig.Commands` namespace (CLI layer). `ConflictResolutionFlow` performs console I/O for interactive conflict prompts — it is inherently a CLI concern. Placing the flusher in the Domain layer would create a circular dependency (Domain → CLI) or require introducing abstraction interfaces for conflict resolution that would add ceremony with no proven benefit. The flusher lives in `src/Twig/Commands/PendingChangeFlusher.cs` alongside `SaveCommand`, which currently owns the same logic inline. Both `SyncCommand` and `FlowDoneCommand` (its consumers) are also in the CLI layer.

**DD-4: Notes-only bypass uses change-type filtering**
When all pending changes for an item are of type "note", skip `ConflictResolutionFlow.ResolveAsync` entirely. Notes are additive (comments) and don't conflict with field changes. This fixes #1362.

**DD-5: FlushResult is a record, not formatted output**
The flusher returns data; the command formats the final summary. This enables reuse from `SyncCommand`, `FlowDoneCommand`, and future commands. Note: the flusher does perform formatting internally during interactive conflict resolution (passing `outputFormat` to `ConflictResolutionFlow.ResolveAsync` to display conflicting field values). `OutputFormatterFactory` in the constructor and the `outputFormat` parameter on `FlushAsync`/`FlushAllAsync` are both for this conflict-display path only — the flush summary (success/failure counts, per-item errors) is always returned as structured `FlushResult` for callers to render.

**DD-6: Save/Refresh as hidden aliases, not removed**
Using `[Hidden]` attribute (same pattern as nav aliases `up`, `down`, `next`, `prev`, etc.) preserves backward compatibility while guiding users to `sync`. Deprecation hints are printed in the `TwigCommands` routing layer in `Program.cs` (not inside the command classes), so internal callers (like SyncCommand delegating to RefreshCommand) do not trigger the hint.

**DD-7: SyncCommand delegates to RefreshCommand, not RefreshOrchestrator**
`RefreshOrchestrator` was designed to encapsulate the domain-layer subset of refresh logic (fetch → conflict detection → save → hydration → working set sync). However, `RefreshCommand.ExecuteCoreAsync` contains 264 lines of additional logic that RefreshOrchestrator does not cover: WIQL construction with config-based area path filtering, user identity detection, global profile metadata updates (field definition hash tracking), cache freshness timestamps (`last_refreshed_at`), static calls to `ProcessTypeSyncService.SyncAsync()` and `FieldDefinitionSyncService.SyncAsync()`, and telemetry emission. Migrating all of this into RefreshOrchestrator would be a significant refactor (~200 LoC moved, new dependencies on `TwigConfiguration`, `TwigPaths`, `IGlobalProfileStore`, `ITelemetryClient`) that is orthogonal to this epic's goals. Instead, SyncCommand injects `RefreshCommand` directly and calls `ExecuteAsync(outputFormat, force, ct)` for the pull phase. This reuses all existing logic with zero duplication. The future refactoring of RefreshCommand to delegate to RefreshOrchestrator is tracked as a separate concern.

**DD-8: Two distinct failure modes in push-on-write**
Push-on-write has two failure points that require different recovery strategies: (1) **Push failure** — `AddCommentAsync` / `PatchAsync` throws before reaching ADO. The change never left the client, so falling back to local staging (`pendingChangeStore.AddChangeAsync`) is safe. On the next `twig save` or `twig sync`, the staged change will be retried. (2) **Resync failure** — push succeeded but `FetchAsync` fails afterward. The change IS in ADO. Staging locally would cause a double-push (duplicate comment or re-applied field patch). Instead, the command warns to stderr ("pushed but cache may be stale — run twig sync") and returns exit code 0. The stale cache self-heals on next sync/refresh.

---

## Alternatives Considered

### DD-1: FetchAsync-per-write vs. alternatives

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **FetchAsync after every write** (chosen) | Captures all server-computed fields; simple; no infrastructure changes | One extra API call per write (~200ms) | Chosen — correctness over latency |
| MarkSynced with PatchAsync return value | No extra API call | Misses server-computed fields (ChangedDate, ChangedBy, Reason, StateChangeDate) — this is the root cause of metadata drift | Rejected — causes the exact bug we are fixing |
| Event-driven invalidation (webhooks) | Real-time notification of remote changes | Requires running a listener process; infeasible for a CLI tool; complex infrastructure | Rejected — wrong architecture for a CLI |
| Cache TTL with lazy re-fetch | Amortizes fetch cost across multiple writes | Doesn't guarantee immediate consistency; leaves window for phantom conflicts | Rejected — doesn't solve the problem |

### DD-2: Offline fallback strategy

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Try-catch on ADO failure** (chosen) | Zero overhead when online; handles all failure modes (network, auth, timeout, 5xx) uniformly | Relies on exception handling for control flow | Chosen — simplest and most robust |
| Pre-check connectivity (ping endpoint) | Avoids exception path when offline | Extra API call adds latency; doesn't prevent mid-request failures; false positives on flaky networks | Rejected — adds cost without solving the problem |
| Explicit `--offline` flag | User explicitly chooses mode | Cumbersome; users forget; error-prone | Rejected — defeats the purpose of seamless fallback |

### DD-3: PendingChangeFlusher extraction strategy

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Extract to CLI-layer service** (chosen) | Can directly call ConflictRetryHelper + ConflictResolutionFlow; single source of truth; testable | Domain layer can't use it (but nothing in Domain needs it) | Chosen — matches dependency reality |
| Keep inline in SaveCommand, delegate from SyncCommand | Fewer files | SyncCommand would depend on SaveCommand's formatting and exit-code logic; awkward coupling | Rejected — coupling |
| Extract to Domain with abstraction interfaces | Cleaner layer boundary | Requires IConflictRetryService + IConflictResolutionService interfaces; ConflictResolutionFlow does Console.Write — bad abstraction leak; ceremony with no benefit | Rejected — over-engineering |

### DD-7: SyncCommand pull-phase delegation strategy

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Delegate to `RefreshCommand.ExecuteAsync()`** (chosen) | Zero duplication; reuses all 264 lines of refresh logic including WIQL, config, identity, metadata, type/field sync, telemetry; SyncCommand is ~80 LoC | SyncCommand depends on a CLI command class rather than a domain service; output format coupling | Chosen — pragmatic; avoids massive refactor |
| Delegate to `RefreshOrchestrator` | Cleaner domain-layer delegation | RefreshOrchestrator lacks ~200 lines of logic that RefreshCommand has: WIQL construction, config-based area path filtering, user identity detection, global profile metadata, timestamps, telemetry. Would require expanding RefreshOrchestrator significantly. | Rejected — out of scope for this epic |
| Duplicate RefreshCommand logic in SyncCommand | No dependency on RefreshCommand | ~200 LoC duplication; two places to maintain; high bug risk | Rejected — DRY violation |
| Extract shared `RefreshCoreService` | Clean shared abstraction | Requires moving 264 lines of RefreshCommand.ExecuteCoreAsync into a new service, adding dependencies on TwigConfiguration, TwigPaths, IGlobalProfileStore, ITelemetryClient; effectively a RefreshCommand refactor | Rejected — orthogonal refactor; candidate for follow-up |

### DD-4: Notes-only bypass strategy

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Change-type filtering on pending changes** (chosen) | Simple string check on "note" type; zero false positives; no schema changes; works with existing `GetPendingChangesAsync` return type | Relies on consistent `"note"` type string | Chosen — minimal and correct |
| Skip conflict resolution for all items below a revision threshold | Could batch-skip multiple items | Complex heuristic; false negatives on items with mixed note + field changes; revision threshold is arbitrary | Rejected — too fragile |
| Always run conflict resolution but auto-accept remote for notes | Consistent code path | Unnecessary API call to fetch remote; user sees conflict prompt flash for pure-note items; defeats the purpose | Rejected — overhead with no benefit |

### DD-5: FlushResult as record vs. formatted output

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Return structured `FlushResult` record** (chosen) | Enables reuse from SyncCommand, FlowDoneCommand, future commands; each caller formats differently; testable without output parsing | Caller must map FlushResult to output | Chosen — separation of concerns |
| Return formatted string | Simpler flusher API | Every consumer gets the same format; SyncCommand can't combine flush output with refresh output; untestable without string matching | Rejected — rigid coupling |
| Return exit code only | Simplest API | Loses failure details; SyncCommand can't report which items failed | Rejected — insufficient information |

### DD-6: Deprecation strategy for save/refresh

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Hidden aliases with deprecation hint** (chosen) | Backward compatible; existing scripts keep working; `[Hidden]` removes from help output; hint in `TwigCommands` routing layer (not command class) so internal callers (SyncCommand→RefreshCommand) don't trigger it | Maintenance of alias routing | Chosen — zero breakage, clear migration path |
| Remove save/refresh entirely | Clean API surface | Breaking change for all users and scripts; no migration period | Rejected — too disruptive |
| Keep both with no deprecation | No user confusion | Permanent API surface bloat; users never learn about sync | Rejected — defers the problem indefinitely |

### DD-8: Post-push resync failure handling

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Warn + return success** (chosen) | User intent succeeded (change pushed); stale cache self-heals on next sync; no double-push risk | User may not notice warning; cache stays stale until next sync | Chosen — correctness: change is in ADO, staging would duplicate |
| Fall back to staging locally | Consistent with push-failure path | Causes double-push: note already in ADO, staging causes re-push on next save/sync; duplicated comments or field patches | Rejected — creates the bug it tries to prevent |
| Retry FetchAsync immediately | May succeed on transient failure | Adds latency; still may fail; complicates control flow | Rejected — marginal benefit; sync self-heals |
| Return failure exit code | Signals something went wrong | Misleading: the user's operation (push) succeeded; scripts would treat success as failure | Rejected — wrong semantic |

---

## Dependencies

### Internal
- `RefreshCommand` — reused by `SyncCommand` for the pull phase (full 264-line refresh implementation)
- `RefreshOrchestrator` — exists in DI but is NOT consumed by RefreshCommand or SyncCommand in this epic; retained as-is for future RefreshCommand refactoring
- `ConflictRetryHelper` / `ConflictResolutionFlow` — reused by `PendingChangeFlusher` (all CLI layer)
- `AutoPushNotesHelper` — **retained as-is** in this epic. `StateCommand` and `UpdateCommand` call `PushAndClearAsync` to auto-push pending notes after their ADO writes. Push-on-write (#1340) makes this less relevant for `NoteCommand` (notes push directly), but `AutoPushNotesHelper` still serves the edge case where a user stages a note via `twig note`, then runs `twig update` or `twig state` before saving — the helper pushes the staged note as a side effect. Removing or deprecating it is deferred until push-on-write is proven in production.

### External
- No new NuGet packages required
- No infrastructure changes (SQLite schema unchanged)

### Sequencing
1. **#1339** (resync after writes) must come first — it establishes the FetchAsync resync pattern
2. **#1362** (notes-only bypass) can be done early — independent fix
3. **#1365** (eviction cleanup) can be done early — independent fix
4. **#1364** (save --all continues past failures) should come before #1341 extraction
5. **#1340** (push-on-write) depends on #1339
6. **#1341** (extract flusher) depends on #1339, #1364, #1362
7. **#1342** (sync command) depends on #1341

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|------------|------|
| `StateCommand` | Modified (add resync) | Low — additive change |
| `NoteCommand` | Modified (push-on-write + offline fallback) | Medium — behavior change |
| `EditCommand` | Modified (push-on-write + offline fallback + new `IConsoleInput` dep) | Medium — behavior change |
| `SaveCommand` | Refactored (delegate to PendingChangeFlusher) | Medium — structural change |
| `FlowDoneCommand` | Modified (use PendingChangeFlusher) | Low — delegate change |
| `RefreshCommand` | Unmodified internally; `[Hidden]` + deprecation hint applied at TwigCommands routing in Program.cs | Low — routing change only |
| `SyncCoordinator` | Modified (eviction cleanup via IPendingChangeStore) | Low — additive change |
| `Program.cs` / `TwigCommands` | Modified (add sync verb, hide save/refresh with deprecation hints) | Low — additive |
| `CommandRegistrationModule` | Modified (register SyncCommand) | Low — additive |
| `CommandServiceModule` | Modified (register PendingChangeFlusher, update SyncCoordinator DI) | Low — additive |
| `AutoPushNotesHelper` | **Retained as-is** — still used by StateCommand and UpdateCommand | None — no changes |

### Backward Compatibility

- `twig save` continues to work (hidden alias with deprecation hint to stderr)
- `twig refresh` continues to work (hidden alias with deprecation hint to stderr)
- `twig note` behavior changes: push instead of stage — but the net effect is the same (note gets pushed), just without needing `twig save`
- `twig edit` behavior changes: push instead of stage — same consideration
- Exit codes unchanged (except: push-success + resync-failure returns 0, per DD-8)
- Output format unchanged (human/json/minimal)

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Push-on-write note failure loses user's text | Low | High | Offline fallback stages locally; text is never lost |
| Post-push resync failure leaves stale cache | Low | Medium | Warn to stderr; suggest `twig sync`; do NOT re-stage (DD-8); self-heals on next sync |
| Extra FetchAsync call per write slows commands | Medium | Low | One extra API call per write (~200ms); acceptable for correctness |
| Notes-only bypass misidentifies change types | Low | Medium | Strict string comparison on "note" change type; comprehensive tests |
| Hidden aliases confuse users expecting old behavior | Low | Low | Print deprecation hint on first use |
| PendingChangeFlusher conflict resolution blocks on stdin | Low | Medium | Same interactive flow as current SaveCommand; no regression |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `twig sync` accept `--save-only` / `--refresh-only` flags for users who want partial sync? | Low | Defer — can add later if needed |
| 2 | Should `twig edit` on non-seeds fetch remote before launching editor (to show freshest values)? | Low | Nice-to-have; can add in follow-up |

> **Resolved (was OQ-2 in v1):** "Should offline-staged changes show a persistent indicator in `twig status`?" — Already visible: dirty items show in status with the existing `is_dirty` flag. No additional work needed.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/PendingChangeFlusher.cs` | CLI-layer service extracting flush logic from SaveCommand |
| `src/Twig/Commands/SyncCommand.cs` | New `twig sync` command implementation |
| `tests/Twig.Cli.Tests/Commands/PendingChangeFlusherTests.cs` | Unit tests for PendingChangeFlusher |
| `tests/Twig.Cli.Tests/Commands/SyncCommandTests.cs` | Unit tests for SyncCommand |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/StateCommand.cs` | Add FetchAsync resync after PatchAsync (replace local MarkSynced) |
| `src/Twig/Commands/NoteCommand.cs` | Push-on-write for non-seeds, offline fallback, inject `IAdoWorkItemService` |
| `src/Twig/Commands/EditCommand.cs` | Push-on-write for non-seeds, offline fallback, inject `IAdoWorkItemService` and `IConsoleInput` (for `ConflictResolutionFlow`) |
| `src/Twig/Commands/SaveCommand.cs` | Delegate to PendingChangeFlusher; formatting layer only |
| `src/Twig/Commands/FlowDoneCommand.cs` | Use PendingChangeFlusher instead of SaveCommand |
| `src/Twig/Commands/RefreshCommand.cs` | No internal changes; deprecation hint added at TwigCommands routing layer in Program.cs |
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Clear pending_changes on eviction (inject `IPendingChangeStore`) |
| `src/Twig/Program.cs` (`TwigCommands`) | Add `sync` verb; add `[Hidden]` to `Save`/`Refresh` methods; print deprecation hints before delegating |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register PendingChangeFlusher; update SyncCoordinator DI (add `IPendingChangeStore`) |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register SyncCommand |
| `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | Update tests for push-on-write behavior |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Update tests for push-on-write behavior |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Add tests for FetchAsync resync |
| `tests/Twig.Cli.Tests/Commands/SaveCommandScopingTests.cs` | Update to test PendingChangeFlusher delegation |
| `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | Add tests for eviction pending_changes cleanup |

---

## ADO Work Item Structure

### Issue #1339: Resync local cache after ADO writes

**Goal:** After any successful ADO write, fetch the item's updated revision and save it to local cache, keeping the local rev in lockstep with ADO.

**Prerequisites:** None (first in sequence)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1339.1 | Add FetchAsync resync to StateCommand — replace lines 109-110 (`MarkSynced` at L109, `SaveAsync` at L110) with FetchAsync + SaveAsync pattern matching UpdateCommand. Lines 107-108 (`ChangeState`, `ApplyCommands`) are also removed since the re-fetched item already reflects the new state. [satisfies G-2, FR-3] | `StateCommand.cs` | ~30 LoC |
| T-1339.2 | Add tests for StateCommand resync — verify FetchAsync called after successful state transition, verify cache item has server-computed fields [satisfies FR-3] | `StateCommandTests.cs` | ~60 LoC |


**Acceptance Criteria:**
- [ ] After `twig state <name>`, local cache rev matches remote rev
- [ ] After `twig update <field> <value>`, local cache rev matches remote rev (verify existing)
- [ ] No additional ADO API calls on failure paths
- [ ] Server-computed fields (ChangedDate, ChangedBy, Reason) are captured in local cache

---

### Issue #1362: Save fails with conflicts on metadata-only drift when only notes pending

**Goal:** When a work item has only staged notes (no field edits), save should push notes without field-level conflict checks, since notes are additive comments.

**Prerequisites:** None (independent fix)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1362.1 | Add notes-only detection in SaveCommand flush loop — before `ConflictResolutionFlow.ResolveAsync`, check if all pending changes are notes. If so, skip conflict resolution and push notes directly. [satisfies G-8, FR-9] | `SaveCommand.cs` | ~25 LoC |
| T-1362.2 | Add tests for notes-only bypass — test that notes-only items skip conflict resolution even when remote revision has drifted [satisfies FR-9] | `SaveCommandScopingTests.cs` | ~50 LoC |

**Acceptance Criteria:**
- [ ] `twig save <id>` with notes-only pending changes succeeds even when metadata fields have drifted
- [ ] No conflict prompt shown when the only pending changes are notes
- [ ] Metadata-only drift is silently accepted (remote wins via FetchAsync resync)
- [ ] Mixed changes (notes + fields) still go through conflict resolution

---

### Issue #1365: SyncCoordinator eviction should clean up pending_changes

**Goal:** When SyncCoordinator evicts a deleted item, also delete its pending_changes rows to prevent orphaned records.

**Prerequisites:** None (independent fix)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1365.1 | Inject `IPendingChangeStore` into SyncCoordinator (both constructors) and clear pending_changes on eviction — add `IPendingChangeStore` to both constructors; update DI registration in `CommandServiceModule.cs`; in `SyncItemAsync` (line 72) and `FetchStaleAndSaveAsync` (line 154), call `pendingChangeStore.ClearChangesAsync(id)` before `DeleteByIdAsync` [satisfies G-7, FR-8] | `SyncCoordinator.cs`, `CommandServiceModule.cs` | ~35 LoC |
| T-1365.2 | Add tests for eviction cleanup — verify `ClearChangesAsync` called before `DeleteByIdAsync` for "not found" items [satisfies FR-8] | `SyncCoordinatorTests.cs` | ~50 LoC |

**Acceptance Criteria:**
- [ ] Evicting a deleted item also removes its pending_changes rows
- [ ] dirtyCount does not include evicted items
- [ ] `twig save --all` does not attempt to push changes for evicted items

---

### Issue #1364: twig save --all should continue past failures

**Goal:** `twig save --all` should attempt every dirty item, collect failures, and report them at the end instead of stopping at the first error.

**Prerequisites:** None (independent fix, but benefits from being done before #1341 extraction)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1364.1 | Wrap per-item save logic in try-catch — wrap lines 98-149 (the per-item ADO operations: FetchAsync, ConflictResolutionFlow, PatchWithRetryAsync, AddCommentAsync, ClearChangesAsync, resync) in a try-catch. On catch: log error to stderr via `_stderr`, set `hadErrors = true`, continue to next item. [satisfies G-6, FR-7] | `SaveCommand.cs` | ~40 LoC |
| T-1364.2 | Add tests for continue-on-failure behavior — test: item 1 fails (ADO exception), item 2 succeeds → exit code 1, both attempted. Test: all succeed → exit code 0. [satisfies G-6, FR-7] | `SaveCommandScopingTests.cs` | ~80 LoC |

**Acceptance Criteria:**
- [ ] `twig save --all` continues past individual item failures
- [ ] Summary reports successes and failures separately
- [ ] Exit code reflects whether any failures occurred
- [ ] Individual `twig save <id>` behavior unchanged (still fails immediately)

---

### Issue #1340: Push-on-write for note and edit commands

**Goal:** For non-seed work items, `twig note` and `twig edit` push changes immediately to ADO instead of staging locally. Offline fallback stages locally with a warning.

**Prerequisites:** #1339 (resync pattern established)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1340.1 | Add IAdoWorkItemService to NoteCommand — add `IAdoWorkItemService adoService` constructor parameter (IWorkItemRepository already injected) [satisfies G-1] | `NoteCommand.cs` | ~5 LoC |
| T-1340.2 | Implement push-on-write for NoteCommand — after preparing note text: if `!item.IsSeed`, try `adoService.AddCommentAsync(id, text)` then `FetchAsync` + `SaveAsync`. On push failure, fall back to staging + warning. On post-push resync failure (FetchAsync fails after successful AddCommentAsync), warn to stderr and return success — do NOT stage locally (see DD-8). For seeds, keep existing behavior. [satisfies G-1, G-2, FR-1] | `NoteCommand.cs` | ~40 LoC |
| T-1340.3 | Add IAdoWorkItemService and IConsoleInput to EditCommand — add `IAdoWorkItemService adoService` and `IConsoleInput consoleInput` constructor parameters. `IConsoleInput` is needed because push-on-write requires conflict resolution via `ConflictResolutionFlow.ResolveAsync`, which prompts the user interactively when local/remote revisions diverge. [satisfies G-1] | `EditCommand.cs` | ~5 LoC |
| T-1340.4 | Implement push-on-write for EditCommand — after parsing changes: if `!item.IsSeed`, fetch remote, resolve conflicts via `ConflictResolutionFlow.ResolveAsync(consoleInput)`, call `PatchAsync` + `FetchAsync` + `SaveAsync`. On push failure, fall back to staging. On post-push resync failure, warn and return success (DD-8). For seeds, keep existing behavior. [satisfies G-1, G-2, FR-2] | `EditCommand.cs` | ~60 LoC |
| T-1340.5 | Update NoteCommand tests — add tests: non-seed pushes immediately, seed still stages, push failure falls back to staging with warning, post-push resync failure warns but does not stage [satisfies FR-1] | `NoteCommandTests.cs` | ~80 LoC |
| T-1340.6 | Update EditCommand tests — add tests: non-seed pushes immediately, seed still stages, push failure falls back to staging, post-push resync failure warns but does not stage [satisfies FR-2] | `EditSaveCommandTests.cs` | ~80 LoC |

**Acceptance Criteria:**
- [ ] `twig note "text"` on non-seed calls AddCommentAsync and resyncs cache
- [ ] `twig edit` on non-seed calls PatchAsync and resyncs cache
- [ ] Offline failure falls back to local staging with warning
- [ ] Seeds are unaffected (staging behavior preserved)
- [ ] No pending_changes left after successful push

---

### Issue #1341: Extract PendingChangeFlusher from SaveCommand

**Goal:** Extract a reusable CLI-layer service that owns the flush logic, replacing inline code in SaveCommand and FlowDoneCommand.

**Prerequisites:** #1339 (resync pattern), #1364 (continue-on-failure), #1362 (notes-only bypass)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1341.1 | Create PendingChangeFlusher CLI service — extract SaveCommand's per-item flush loop. Include FlushResult record, FlushItemFailure record. Constructor takes `IWorkItemRepository`, `IAdoWorkItemService`, `IPendingChangeStore`, `IConsoleInput`, `OutputFormatterFactory`, `TextWriter? stderr`. Methods: `FlushAsync(IReadOnlyList<int>)`, `FlushAllAsync`. Incorporate notes-only bypass (from #1362) and continue-on-failure (from #1364). Calls ConflictRetryHelper and ConflictResolutionFlow directly (both CLI-layer statics). [satisfies G-5, FR-7, FR-9] | `PendingChangeFlusher.cs` | ~130 LoC |
| T-1341.2 | Register PendingChangeFlusher in DI — add singleton registration with factory lambda (consistent with ActiveItemResolver, SyncCoordinator, RefreshOrchestrator). Must pass `TextWriter` for stderr. [satisfies G-5] | `CommandServiceModule.cs` | ~10 LoC |
| T-1341.3 | Refactor SaveCommand to delegate — for `--all` mode, call `PendingChangeFlusher.FlushAllAsync()`. For single-item or work-tree scoping modes, resolve the target IDs (via `ActiveItemResolver` + `GetChildrenAsync` + `GetDirtyItemIdsAsync` intersection, same as current L61-72 logic), then call `FlushAsync(ids)`. SaveCommand becomes a thin formatting layer that reads FlushResult and writes to stdout/stderr. [satisfies G-5] | `SaveCommand.cs` | ~80 LoC (net reduction) |
| T-1341.4 | Refactor FlowDoneCommand to use flusher — resolve root + children IDs via `workItemRepo.GetChildrenAsync(activeId)`, then **intersect with dirty IDs** from `pendingChangeStore.GetDirtyItemIdsAsync()` (matching current SaveCommand L61-72 pattern: `dirtySet.Contains(activeId) \|\| children.Any(c => dirtySet.Contains(c.Id))`). Pass the filtered ID list to `PendingChangeFlusher.FlushAsync(ids)`. This intersection ensures only dirty items within the work tree are flushed — without it, the flusher would attempt to flush non-dirty items and find no pending changes (harmless but wasteful). Format output from FlushResult. [satisfies G-5] | `FlowDoneCommand.cs` | ~30 LoC |
| T-1341.5 | Write PendingChangeFlusher unit tests — test: flush single item, flush all, continue-on-failure, notes-only bypass, conflict resolution delegation [satisfies G-5, FR-7, FR-9] | `PendingChangeFlusherTests.cs` | ~150 LoC |
| T-1341.6 | Update SaveCommand tests — update to verify delegation to flusher, test formatting of FlushResult [satisfies G-5] | `SaveCommandScopingTests.cs` | ~60 LoC |

**Acceptance Criteria:**
- [ ] PendingChangeFlusher.FlushAsync pushes field changes and notes, resyncs cache
- [ ] FlushAllAsync processes all dirty items, continues past failures
- [ ] SaveCommand computes work-tree scope externally and delegates to FlushAsync(ids)
- [ ] SaveCommand delegates entirely to PendingChangeFlusher (thin formatting layer)
- [ ] FlowDoneCommand resolves root + children, intersects with dirty IDs, delegates to FlushAsync(ids)
- [ ] FlushResult includes structured failure information

---

### Issue #1342: Converge save and refresh into twig sync

**Goal:** New `twig sync` command that flushes offline changes and refreshes the working set. `save` and `refresh` become hidden aliases.

**Prerequisites:** #1341 (PendingChangeFlusher extracted)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1342.1 | Create SyncCommand — new command with params: `targetId`, `force`, `outputFormat`. Injects `PendingChangeFlusher` and `RefreshCommand` via DI. Push phase: calls `PendingChangeFlusher.FlushAllAsync()` (or `FlushAsync([targetId])` for scoped). Pull phase: calls `RefreshCommand.ExecuteAsync(outputFormat, force, ct)` directly — this reuses all 264 lines of refresh logic (WIQL, config, identity, metadata, type/field sync, telemetry) without duplication (see DD-7). Reports combined results: flush summary then refresh output. [satisfies G-3, FR-5] | `SyncCommand.cs` | ~80 LoC |
| T-1342.2 | Register SyncCommand in DI — add `services.AddSingleton<SyncCommand>()` with factory lambda [satisfies G-3] | `CommandRegistrationModule.cs` | ~15 LoC |
| T-1342.3 | Add `sync` verb to TwigCommands — add `Sync` method routing to SyncCommand [satisfies G-3] | `Program.cs` | ~5 LoC |
| T-1342.4 | Make `save` a hidden alias — add `[Hidden]` to `TwigCommands.Save` method in Program.cs; print deprecation hint to stderr (`"hint: 'twig save' is deprecated. Use 'twig sync' instead."`) BEFORE calling `SaveCommand.ExecuteAsync`. The hint is in the routing layer, not inside SaveCommand, so internal callers (PendingChangeFlusher, SyncCommand) are unaffected. [satisfies G-4, FR-6] | `Program.cs` | ~10 LoC |
| T-1342.5 | Make `refresh` a hidden alias — add `[Hidden]` to `TwigCommands.Refresh` method in Program.cs; print deprecation hint to stderr BEFORE calling `RefreshCommand.ExecuteAsync`. The hint is in the routing layer so SyncCommand's internal delegation to RefreshCommand does NOT trigger the hint. [satisfies G-4, FR-6] | `Program.cs` | ~10 LoC |
| T-1342.6 | Write SyncCommand tests — test: sync flushes then refreshes, sync --force passes force to RefreshCommand, sync <id> scopes flush to single item, deprecation hints on save/refresh aliases [satisfies G-3, G-4, FR-5, FR-6] | `SyncCommandTests.cs` | ~150 LoC |

**Acceptance Criteria:**
- [ ] `twig sync` flushes offline changes then refreshes
- [ ] `twig sync --force` bypasses conflicts
- [ ] `twig sync <id>` scopes to single item
- [ ] `twig save` still works but prints deprecation hint
- [ ] `twig refresh` still works but prints deprecation hint
- [ ] No breaking changes to existing scripts

---

### Deferred: Issue #1335 — Refresh should fast-forward items with no real pending changes

> **Status: Deferred.** With push-on-write established (#1340), notes push immediately and don't accumulate in pending_changes. `twig sync` also flushes any offline-staged notes before refreshing, so the scenario this targets is largely eliminated. The notes-only bypass (#1362) already prevents conflict-resolution false positives. Revisit if users still hit protected-cache skips on notes-only items after this epic ships.

### Out of Scope: Issue #1363 — twig discard (drop pending changes)

> **Status: Tracked separately.** A new `twig discard` command to drop staged pending changes for a work item. Listed as NG-5 — it is a useful ergonomic improvement but orthogonal to cache-divergence and sync convergence. Tracked as its own issue (#1363).

---

## PR Groups

> **PR Group type classifications:**
> - **Deep** — Few files, complex logic changes. Requires careful review of control flow, error handling, and edge cases. Reviewer should trace data flow end-to-end.
> - **Wide** — Many files, mechanical/repetitive changes (e.g., renames, interface additions, pattern propagation). Reviewer can spot-check representative files.

### PR Group 1: Cache resync and independent fixes
**Issues:** #1339, #1362, #1365, #1364  
**Tasks:** T-1339.1–2, T-1362.1–2, T-1365.1–2, T-1364.1–2  
**Type:** Deep  
**Estimated LoC:** ~340  
**Files:** ~8 (StateCommand, SaveCommand, SyncCoordinator, CommandServiceModule, + test files)  
**Rationale:** Four independent fixes that share a theme (correctness of cache and conflict handling). Each is small and surgical. Grouping them provides a coherent "fix the foundation" PR that's easy to review together.  
**Successors:** PR Group 2, PR Group 3

### PR Group 2: Push-on-write for note and edit
**Issues:** #1340  
**Tasks:** T-1340.1–6  
**Type:** Deep  
**Estimated LoC:** ~270  
**Files:** ~4 (NoteCommand, EditCommand, + test files)  
**Rationale:** Push-on-write is the core behavioral change. Grouping note and edit together ensures the push-on-write pattern is implemented consistently. Requires careful review of the two failure modes (push failure vs. resync failure per DD-8).  
**Successors:** PR Group 3

### PR Group 3: Extract PendingChangeFlusher and converge sync
**Issues:** #1341, #1342  
**Tasks:** T-1341.1–6, T-1342.1–6  
**Type:** Deep  
**Estimated LoC:** ~710  
**Files:** ~10 (PendingChangeFlusher, SyncCommand, SaveCommand refactor, FlowDoneCommand, Program.cs, + test files)  
**Rationale:** These two issues are tightly coupled — the SyncCommand depends on PendingChangeFlusher. Reviewing them together shows the full "converge" story. The net LoC is moderate because SaveCommand shrinks significantly. T-1342.1 is simpler than originally estimated (~80 LoC vs ~120 LoC) because SyncCommand delegates to RefreshCommand.ExecuteAsync() rather than reimplementing refresh logic.  
**Successors:** None

---

## References

- Epic #1338: Push-on-Write and Sync Convergence
- Issue #1339: Resync local cache after ADO writes
- Issue #1340: Push-on-write for note and edit commands
- Issue #1341: Extract PendingChangeFlusher from SaveCommand
- Issue #1342: Converge save and refresh into twig sync
- Issue #1362: Save command fails with conflicts on metadata-only drift
- Issue #1364: twig save --all should continue past failures
- Issue #1365: SyncCoordinator eviction should clean up pending_changes
- Issue #1335: Refresh fast-forward — **deferred** (superseded by push-on-write + sync flush-before-refresh)
- Issue #1363: twig discard — **out of scope** (tracked separately, orthogonal to sync convergence)

---

## Revision History

| Rev | Changes |
|-----|---------|
| 8 | Plan-level reduction pass. Removed T-1339.3 (optional regression test for already-correct UpdateCommand behavior — no behavioral change, no regression to prevent). Merged T-1365.1 and T-1365.2 into single task (both are trivial changes to SyncCoordinator.cs, 35 LoC combined). Clarified DD-5 to explain that `OutputFormatterFactory` and `outputFormat` params are used for conflict-display formatting during the flush loop, not for the final FlushResult summary. Updated PR Group 1 task references and LoC estimate (~380 → ~340). |
|-----|---------|
| 7 | Addressed review feedback (tech=89, read=92). **Issue 1 (Critical):** Resolved FlushWorkTreeAsync inconsistency — removed FlushWorkTreeAsync from PendingChangeFlusher API surface; callers (SaveCommand, FlowDoneCommand) compute work-tree scope externally and pass IDs to FlushAsync; updated T-1341.3, T-1341.4, and #1341 acceptance criteria. **Issue 2:** Expanded Problem Statement §6 to enumerate all 4 `continue` paths in the save loop (null item L91, empty pending L96, conflict-JSON L108, accepted-remote/aborted L111). **Issue 3:** Added Alternatives Considered tables for DD-4, DD-5, DD-6, DD-8 (previously inline-only). **Issue 4:** Added NFR-1 exemption note for FlushResult/FlushItemFailure — internal data-flow records, not serialized, no TwigJsonContext registration needed. **Issue 5:** Expanded T-1341.4 to document dirty-intersection filter (GetDirtyItemIdsAsync intersected with work-tree children). **Issue 6:** Corrected StateCommand line references in T-1339.1 (MarkSynced at L109, SaveAsync at L110). **Issue 7:** Moved revision changelog to Revision History section at bottom. |
| 6 | Addressed review feedback (tech=88 → target 90+, read=87 → target 90+). Fixed RefreshOrchestrator/RefreshCommand delegation gap: SyncCommand now delegates pull phase to RefreshCommand.ExecuteAsync (not RefreshOrchestrator). Corrected data flow diagram method names. Added post-push resync failure edge case handling (DD-8). Added TextWriter to PendingChangeFlusher constructor. Added RefreshCommand.cs to Modified Files. Defined PR Group type classifications. Resolved AutoPushNotesHelper disposition. Documented IConsoleInput injection rationale for EditCommand. Added requirement traceability (FR-X, G-X) to all task descriptions. |
| 5 | Addressed review feedback (tech=85, read=84). Added call-site audit tables. Expanded Design Decisions section. Added DD-7 (SyncCommand delegates to RefreshCommand). Added DD-8 (two failure modes). Added Alternatives Considered tables for DD-1, DD-2, DD-3, DD-7. |
| 4 | Added notes-only bypass design (DD-4). Added FlushResult record design (DD-5). |
| 3 | Added eviction cleanup design (#1365). Added save --all continue-on-failure (#1364). |
| 2 | Added push-on-write design for NoteCommand and EditCommand (#1340). |
| 1 | Initial draft — resync after writes, PendingChangeFlusher extraction, SyncCommand convergence. |

---

## Completion

**Completed:** 2026-04-02

**Summary:** All 7 issues (#1339, #1362, #1365, #1364, #1340, #1341, #1342) were implemented across 3 PR groups. PR Group 1 (Cache resync and independent fixes) was merged via GitHub PR #10. PR Groups 2 and 3 were implemented on local feature branches (`feature/push-on-write`, `feature/sync-command`) with 9 and 24 commits respectively, totaling 2,774 lines of changes across 39 files. The epic transitioned to Done in ADO on 2026-04-01.

**Artifacts:**
- GitHub PR #10 (merged 2026-04-01T22:42:53Z): Cache resync and independent fixes
- Local branch `feature/push-on-write`: Push-on-write for NoteCommand and EditCommand (9 commits, 487 insertions)
- Local branch `feature/sync-command`: PendingChangeFlusher extraction and SyncCommand convergence (24 commits, 2,287 insertions)
- Tag: v0.26.0

**Note:** Feature branches `feature/push-on-write` and `feature/sync-command` contain completed implementation code that was not pushed to origin or merged to main via GitHub PRs. These branches need to be pushed, PR'd, and merged to fully land the feature code on main.
