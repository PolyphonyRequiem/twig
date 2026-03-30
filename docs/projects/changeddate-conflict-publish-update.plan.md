# Plan: ChangedDate Conflict Errors on Publish-then-Update

> **Date**: 2026-03-29
> **Status**: 🔨 In Progress
> **ADO Issue**: #1279

---

## Executive Summary

After `twig seed publish`, an immediate `twig update` or `twig state` can fail with a
412 Precondition Failed (VS403208 revision mismatch). Root-cause investigation reveals two
contributing factors: (1) the `SeedPublishOrchestrator` performs post-creation server-side
modifications (link promotion via `AddLinkAsync`, backlog ordering via `PatchAsync`) that
increment the work item's ADO revision without refreshing the local cache, and (2) ADO's
eventual consistency means server-side rules may still be processing when the next command
fetches the "latest" revision, creating a window where `FetchAsync` returns a revision that
is about to be incremented by a rule engine.

This plan recommends a two-layer fix: (a) add automatic retry-on-conflict to `PatchAsync`
callers, catching `AdoConflictException`, re-fetching the current revision, and retrying once;
and (b) refresh the local cache at the end of `SeedPublishOrchestrator.PublishAsync` to keep
the cached revision current. Together, these changes eliminate the manual
`twig refresh && Start-Sleep 3` workaround entirely. Additionally, the `ExceptionHandler`
will be extended with a dedicated FM-006 handler for 412 conflicts, providing actionable
guidance when the retry is exhausted.

The `ConflictRetryHelper` is placed in the CLI layer (`src/Twig/Commands/`) — not in
`Twig.Domain` — because it must catch `AdoConflictException` from
`Twig.Infrastructure.Ado.Exceptions`, and Domain does not reference Infrastructure
(clean architecture boundary). The retry helper is integrated into `UpdateCommand`,
`StateCommand`, and `SaveCommand`. `BranchCommand` and `FlowStartCommand` are explicitly
excluded (see Non-Goals).

Total estimated scope: ~315 LoC across 2 epics.

---

## Background

### Current Architecture

The publish-then-update flow involves two separate commands running sequentially:

```
twig seed publish <id>
  └─ SeedPublishOrchestrator.PublishAsync()
       ├─ Step 7:  CreateAsync(seed)           → ADO API POST → Rev 1
       ├─ Step 8:  FetchAsync(newId)           → GET → WorkItem(Rev 1)
       ├─ Step 10: SaveAsync(fetchedItem)      → SQLite cache (Rev 1)
       ├─ Step 11: PromoteLinksAsync(newId)     → AddLinkAsync() → PATCH (no If-Match) → Rev 2+
       └─ Step 12: TryOrderAsync(newId, parent) → FetchAsync + PatchAsync → Rev 3+
       ⚠ No final refresh — local cache frozen at Rev 1

twig update <field> <value>
  └─ UpdateCommand.ExecuteAsync()
       ├─ FetchAsync(id)                       → GET → Rev N (may be stale due to ADO rules)
       ├─ ConflictResolutionFlow.ResolveAsync() → compares local vs remote
       └─ PatchAsync(id, changes, Rev N)       → PATCH If-Match: N → 412 if ADO is at N+1
```

### Root Cause Analysis

The 412 conflict has two contributing causes:

| Cause | Mechanism | Frequency |
|-------|-----------|-----------|
| **Stale local cache** | `SeedPublishOrchestrator` saves Rev 1 to SQLite but post-publish steps (link promotion, backlog ordering) increment the server revision to Rev 3+. The local cache is never refreshed. | Every publish with links or a parent |
| **ADO eventual consistency** | ADO's server-side rule engine (auto-fill fields, board column sync, ChangedBy updates) processes asynchronously after API mutations. `FetchAsync` may return Rev N while a rule is about to commit Rev N+1. | Intermittent; window is 0–3 seconds |

The stale cache cause is deterministic and fixable. The eventual consistency cause is
inherent to ADO's architecture and can only be mitigated with retry logic.

### Current Workaround

```powershell
twig seed publish --all
twig refresh
Start-Sleep 3
twig set <id>
twig update System.AssignedTo "Daniel Green"
```

The `twig refresh` re-fetches the item (fixes the stale cache). The `Start-Sleep 3` waits
for ADO's rule engine to settle (mitigates eventual consistency). This is sufficient but
creates friction in scripted workflows.

### Existing Infrastructure

The codebase already has the building blocks for a fix:

- **`AdoConflictException`** (`Twig.Infrastructure.Ado.Exceptions`) — Thrown on 412 by `AdoErrorHandler`, includes `ServerRevision` parsed from the error body via `TryParseRevisionFromError` regex
- **`ExceptionHandler`** (`src/Twig/Program.cs`) — Global handler with the following failure modes in order: FM-001 (`AdoOfflineException`), FM-002/003 (`AdoAuthenticationException`), FM-004 (`AdoNotFoundException`), FM-005 (`AdoBadRequestException`), FM-009 (`EditorNotFoundException`), FM-008 (`SqliteException`). **No FM-006 handler for 412 conflicts.**
- **`ConflictResolutionFlow`** — Pre-mutation conflict detection (field-level, interactive). Handles divergence between local cache and remote state before applying changes. This is *not* the same as the 412 retry — `ConflictResolutionFlow` runs *before* `PatchAsync`; the retry handles conflicts that arise *during* the PATCH.
- **`IAdoWorkItemService.FetchAsync`** (`Twig.Domain.Interfaces`) — Returns `WorkItem` (non-nullable). Always available for re-fetching current state.
- **`IAdoWorkItemService.PatchAsync`** — Returns `Task<int>` (new revision number). Uses `If-Match` header for optimistic concurrency.

### PatchAsync Call Sites (Vulnerability Inventory)

All commands that call `IAdoWorkItemService.PatchAsync` are potentially vulnerable to 412
conflicts. The complete inventory:

| File | Method / Pattern | Context | In Scope? |
|------|-----------------|---------|-----------|
| `UpdateCommand.cs` | `ExecuteAsync` — bare `PatchAsync` after `ConflictResolutionFlow` | Field update on active item | ✓ Yes |
| `StateCommand.cs` | `ExecuteAsync` — `PatchAsync` → `MarkSynced(newRevision)` | State transition on active item | ✓ Yes |
| `SaveCommand.cs` | `ExecuteAsync` — `PatchAsync` inside item loop after `ConflictResolutionFlow` | Push pending field changes | ✓ Yes |
| `BranchCommand.cs` | State transition inside `try { ... } catch (Exception) { newState = null; }` | Best-effort auto-transition during branch creation | ✗ Non-Goal (NG6) |
| `FlowStartCommand.cs` | Two chained `PatchAsync` calls (`currentRevision` threaded through) | State transition + assignment | ✗ Non-Goal (NG7) |

---

## Problem Statement

`twig seed publish` followed immediately by `twig update` (or `twig state`) fails with a
412 revision mismatch because: (1) the publish orchestrator leaves the local cache at Rev 1
while the server is at Rev 3+, and (2) ADO's eventual consistency means even a fresh
`FetchAsync` may return a revision that is about to be incremented by background rules.
There is no retry logic for 412 errors, and the `ExceptionHandler` provides no actionable
guidance for this specific failure mode.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | `twig update` / `twig state` / `twig save` automatically retry once on 412 conflict (re-fetch + re-patch, max 1 retry) |
| G2 | `SeedPublishOrchestrator` refreshes local cache after all post-publish steps complete |
| G3 | `ExceptionHandler` provides actionable FM-006 guidance when retry is exhausted |
| G4 | Publish-then-update works without manual `twig refresh` or `Start-Sleep` |
| G5 | Existing conflict resolution flow (interactive prompt) is untouched |

### Non-Goals

| ID | Non-Goal | Rationale |
|----|----------|-----------|
| NG1 | Adding Polly or a general retry framework | A single inline retry is sufficient for the 412 window |
| NG2 | Retry logic inside `AdoRestClient.PatchAsync` itself | Retry belongs at the command level where context (field changes, user intent) is available. The retry needs a re-fetch (separate HTTP call) that can't be done inside `PatchAsync`. |
| NG3 | Fixing ADO's eventual consistency | This is ADO's design; we can only mitigate |
| NG4 | Adding retry to `AddLinkAsync`, `AddCommentAsync`, or other non-PatchAsync calls | These don't use `If-Match` headers, so 412 conflicts don't apply |
| NG5 | Changing the `ConflictResolutionFlow` interactive prompt behavior | This is a pre-mutation check, separate from the 412 retry |
| NG6 | Retry in `BranchCommand` | BranchCommand's state transition is already wrapped in a catch-all `try { ... } catch (Exception) { newState = null; }` that treats *all* failures (including 412) as non-fatal. The existing behavior is correct — branch creation succeeds and the optional state transition is silently skipped on conflict. |
| NG7 | Retry in `FlowStartCommand` | FlowStartCommand chains two sequential `PatchAsync` calls threading `currentRevision` between them (state transition → assignment). Adding retry to each call is mechanically possible but adds complexity disproportionate to the risk. `FlowStartCommand` fetches the item immediately before patching, so its revision is fresh. If conflicts arise, the user receives FM-006 guidance. Consider for a follow-up if user reports indicate friction. |

---

## Proposed Design

### Retry-on-Conflict Strategy

The retry is implemented as a CLI-layer helper (`ConflictRetryHelper`) in
`src/Twig/Commands/` that wraps the fetch → patch sequence. It lives in the CLI project
(not `Twig.Domain`) because:

1. It must catch `AdoConflictException` from `Twig.Infrastructure.Ado.Exceptions` — Domain
   does not reference Infrastructure (clean architecture boundary)
2. The retry needs to re-fetch the item (a separate HTTP call), not just resend the same request
3. The command-level context (which changes to apply) must be preserved across the retry
4. Placement alongside `AutoPushNotesHelper` follows the existing pattern for shared command helpers

```
ConflictRetryHelper.PatchWithRetryAsync(adoService, itemId, changes, initialRevision)
  ├─ Attempt 1: PatchAsync(id, changes, initialRevision)
  │   └─ Success → return newRevision
  │   └─ AdoConflictException →
  ├─ Re-fetch: FetchAsync(id) → freshItem
  ├─ Attempt 2: PatchAsync(id, changes, freshItem.Revision)
  │   └─ Success → return newRevision
  │   └─ AdoConflictException → throw (caller handles via FM-006)
  └─ Any other exception → throw immediately
```

**Max 1 retry** — A single retry covers the ADO eventual consistency window (typically
< 1 second). If the conflict persists after re-fetch + retry, it's a genuine concurrent
edit by another user, and the error should surface normally.

**Return type**: `int` — the new revision number after the successful patch.

### Post-Publish Cache Refresh

Add a final `FetchAsync` → `SaveAsync` at the end of `SeedPublishOrchestrator.PublishAsync`,
after link promotion and backlog ordering complete. This keeps the local cache at the correct
revision and eliminates the need for a manual `twig refresh` after publish.

### FM-006 Error Handling

Add explicit `AdoConflictException` handling in `ExceptionHandler` between the existing
FM-005 (`AdoBadRequestException`) handler and the FM-009 (`EditorNotFoundException`) handler:

```
error: Concurrency conflict (revision mismatch).
hint: Another change is being processed. Run 'twig refresh' and retry.
```

The complete FM handler order in `ExceptionHandler` after this change:
FM-001 → FM-002/003 → FM-004 → FM-005 → **FM-006** → FM-009 → FM-008 → generic fallback.

### Design Decisions

| ID | Decision | Alternatives Considered | Rationale |
|----|----------|------------------------|-----------|
| DD-01 | Retry at command level via helper in `src/Twig/Commands/`, not in `AdoRestClient.PatchAsync` or `Twig.Domain` | (a) Retry inside HTTP client — can't re-fetch there. (b) Polly retry policy — overkill for single retry. (c) Helper in `Twig.Domain/Services/` — **infeasible**: Domain doesn't reference Infrastructure, so it can't catch `AdoConflictException`. | Helper in CLI layer follows `AutoPushNotesHelper` pattern, has access to both Domain interfaces and Infrastructure exceptions, and keeps logic visible and testable. |
| DD-02 | Max 1 retry (not configurable) | 3 retries with backoff | A single retry handles 99% of eventual-consistency conflicts. Multi-retry suggests a genuine concurrent edit, which should surface as an error. |
| DD-03 | Refresh cache at end of `PublishAsync`, not at start of `UpdateCommand` | Refresh in UpdateCommand before fetch | Fixing the source (stale cache after publish) is cleaner than compensating downstream. UpdateCommand already fetches remote, so an extra refresh there would be redundant. |

---

## Implementation Plan

### Epic 1: Conflict Retry Helper + Command Integration

**Classification:** Deep (7 source files, logic changes in helper + 3 commands + exception handler)
**Estimated LoC:** ~240
**Predecessor:** None

This epic introduces the `ConflictRetryHelper`, integrates it into `UpdateCommand`,
`StateCommand`, and `SaveCommand`, and adds FM-006 error handling.

#### Tasks

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | **Create `ConflictRetryHelper`** — New `internal static class` in `Twig.Commands` namespace with method `PatchWithRetryAsync(IAdoWorkItemService adoService, int itemId, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct)`. Catches `AdoConflictException` on first attempt, calls `adoService.FetchAsync(itemId)` to get fresh item, retries `PatchAsync` once with `freshItem.Revision`. Returns `int` (new revision). On second failure, rethrows. Non-conflict exceptions propagate immediately without retry. | `src/Twig/Commands/ConflictRetryHelper.cs` | M |
| T2 | **Integrate into `UpdateCommand`** — In `ExecuteAsync`, replace the bare `adoService.PatchAsync(local.Id, changes, remote.Revision)` call with `ConflictRetryHelper.PatchWithRetryAsync(...)`. The existing `FetchAsync` + `SaveAsync` post-patch refresh is unaffected. | `src/Twig/Commands/UpdateCommand.cs` | S |
| T3 | **Integrate into `StateCommand`** — In `ExecuteAsync`, replace the bare `adoService.PatchAsync(item.Id, changes, remote.Revision)` call with `ConflictRetryHelper.PatchWithRetryAsync(...)`. Use the returned revision for the subsequent `MarkSynced` call. | `src/Twig/Commands/StateCommand.cs` | S |
| T4 | **Integrate into `SaveCommand`** — In the `ExecuteAsync` item loop, replace the bare `adoService.PatchAsync(item.Id, fieldChanges, remote.Revision)` call (inside the `if (fieldChanges.Count > 0)` block) with `ConflictRetryHelper.PatchWithRetryAsync(...)`. The existing post-patch `FetchAsync` + `SaveAsync` already refreshes the cache. | `src/Twig/Commands/SaveCommand.cs` | S |
| T5 | **Add FM-006 to `ExceptionHandler`** — In the `Handle` method in `Program.cs`, add an `AdoConflictException` case **after** the FM-005 `AdoBadRequestException` handler and **before** the FM-009 `EditorNotFoundException` handler. Output: `"error: Concurrency conflict (revision mismatch)."` followed by `"hint: Another change is being processed. Run 'twig refresh' and retry."` Return exit code 1. | `src/Twig/Program.cs` | S |
| T6 | **Unit tests for `ConflictRetryHelper`** — New test class `ConflictRetryHelperTests` in `Twig.Cli.Tests.Commands` with: (1) `PatchSucceeds_NoRetry_ReturnsRevision` — mock `PatchAsync` succeeds on first call; verify `PatchAsync` called once and `FetchAsync` not called. (2) `PatchConflict_RetrySucceeds_ReturnsNewRevision` — mock `PatchAsync` throws `AdoConflictException` on first call, succeeds on second; verify `FetchAsync` called once for re-fetch and correct revision returned. (3) `PatchConflict_RetryFails_ThrowsAdoConflictException` — both `PatchAsync` calls throw `AdoConflictException`; verify exception propagates and `FetchAsync` called once. (4) `NonConflictException_NoRetry_Throws` — `PatchAsync` throws `AdoBadRequestException`; verify no retry, no `FetchAsync`, exception propagates. | `tests/Twig.Cli.Tests/Commands/ConflictRetryHelperTests.cs` | M |
| T7 | **Update `UpdateCommandTests`** — Add test `Update_ConflictOnPatch_RetriesSuccessfully`: configure `PatchAsync` to throw `AdoConflictException(serverRev: 3)` on first call and return `4` on second. Configure `FetchAsync` to return items appropriately. Verify exit code 0 and `PatchAsync` received 2 calls. Add test `Update_ConflictExhausted_Returns1`: both `PatchAsync` calls throw; verify exit code 1. | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | S |
| T8 | **Update `ExceptionFilterTests`** — Add test `Handle_AdoConflictException_Returns1WithHint`: create `AdoConflictException(serverRev: 7)`, call `ExceptionHandler.Handle(ex, stderr)`, verify exit code 1 and `stderr.ToString()` contains both "revision mismatch" and "twig refresh". | `tests/Twig.Cli.Tests/Commands/ExceptionFilterTests.cs` | S |

#### Acceptance Criteria

1. `twig update`, `twig state`, and `twig save` automatically retry once on 412 conflict without user intervention
2. If retry succeeds, the command completes normally (exit code 0)
3. If retry fails, FM-006 error message is displayed with actionable hint
4. Existing interactive conflict resolution (`ConflictResolutionFlow`) is not affected
5. All existing tests pass; new tests cover retry success, retry exhaustion, and non-conflict exceptions

---

### Epic 2: Post-Publish Cache Refresh

**Classification:** Deep (2 files, logic change in orchestrator + tests)
**Estimated LoC:** ~75
**Predecessor:** Epic 1 (advisory — not a hard dependency)

Epic 2 depends on Epic 1 in an advisory capacity: the retry logic should exist first so the
post-publish refresh is additive, not load-bearing. However, the two epics touch disjoint
files and can be **implemented in parallel** if needed. The dependency is about *testing
confidence* — with both layers in place, the publish-then-update scenario is covered even
if the refresh fails or ADO's eventual consistency delays the revision.

This epic adds a final `FetchAsync` → `SaveAsync` at the end of `SeedPublishOrchestrator.PublishAsync`
to keep the local cache at the correct revision after link promotion and backlog ordering.

#### Tasks

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | **Add post-publish refresh to `SeedPublishOrchestrator.PublishAsync`** — After `TryOrderAsync` (step 12) and before returning the result (step 13), add: `var refreshed = await _adoService.FetchAsync(newId, ct); await _workItemRepo.SaveAsync(refreshed, ct);`. This replaces the Rev 1 cached item with the current server revision (Rev 3+ after links and ordering). Wrap in `try-catch` so refresh failure is non-fatal (the item was published successfully; stale cache is tolerable since Epic 1's retry covers it). Log a warning on failure if a logger is available. | `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | S |
| T2 | **Update `SeedPublishOrchestratorTests`** — Add test `PublishAsync_RefreshesCacheAfterPostPublishSteps`: mock `FetchAsync` to return items with incrementing revisions; verify `SaveAsync` is called a second time (after the initial transactional save) with the higher-revision item. Add test `PublishAsync_RefreshFailure_StillReturnsSuccess`: configure `FetchAsync` to succeed on the first call (during step 8) but throw on the second call (the refresh); verify `PublishAsync` still returns `SeedPublishStatus.Created`. | `tests/Twig.Domain.Tests/Services/SeedPublishOrchestratorTests.cs` | M |

#### Acceptance Criteria

1. After `twig seed publish`, the local cache has the current server revision (not Rev 1)
2. `twig update` immediately after `twig seed publish` works without `twig refresh` or `Start-Sleep`
3. Refresh failure does not cause publish to fail (non-fatal best-effort)
4. All existing orchestrator tests pass; new tests cover refresh and refresh-failure scenarios

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Retry mask genuine concurrent edits | Low | Medium | Max 1 retry. If the second attempt also conflicts, it's a real concurrent edit and the error surfaces via FM-006 with actionable guidance. |
| Post-publish refresh adds latency to `twig seed publish` | Low | Low | One additional `FetchAsync` (~100ms) is negligible relative to the `CreateAsync` + `PromoteLinksAsync` + `TryOrderAsync` calls already in the publish flow. |
| FlowStartCommand conflicts not covered | Low | Low | FlowStartCommand fetches immediately before patching, so its revision is fresh. NG7 documents the exclusion. FM-006 provides fallback guidance. |
| Helper placement in CLI layer limits reuse | Low | Low | Only CLI commands call `PatchAsync`. If `Twig.Tui` needs the same logic, the helper can be extracted to a shared layer or duplicated (it's ~30 lines). |

---

## Success Criteria

1. `twig seed publish <id> && twig update System.AssignedTo "Daniel Green"` succeeds without any intermediate `twig refresh` or `Start-Sleep`
2. The retry is invisible to the user on success (no extra output)
3. On retry exhaustion, the error message is actionable (FM-006)
4. No behavioral change for users who don't hit 412 conflicts
5. All tests pass, including new retry tests and orchestrator refresh tests
