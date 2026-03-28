# Plan: `twig new` — Direct ADO Creation (Bypass Seed Pipeline)

> **Date**: 2026-03-28
> **Status**: 🔨 In Progress
> **ADO Issue**: #1278
> **Supersedes**: Partial rework of #1264 (`twig-new-command.plan.md`)

---

## Executive Summary

Refactor `twig new` to call `IAdoWorkItemService.CreateAsync` directly instead of routing
through the `SeedPublishOrchestrator` seed pipeline. Today, `NewCommand` creates a
negative-ID seed, saves it to SQLite, then immediately publishes via the 13-step
orchestrator — but the seed lifecycle (ID remapping, publish-id-map, seed-link promotion,
backlog ordering, transactional rollback) is entirely unnecessary for `twig new`, which
never creates draft seeds. This plan eliminates the `SeedPublishOrchestrator` dependency
from `NewCommand` and replaces it with a direct `CreateAsync` → `FetchAsync` → `SaveAsync`
flow, reducing code complexity and removing ~30 lines of unused orchestrator integration.

---

## Background

### Current Architecture

`NewCommand` (Issue #1264) was implemented by reusing the existing seed publish pipeline:

```
NewCommand.ExecuteAsync()
  ├─ SeedFactory.CreateUnparented()      → negative-ID WorkItem (IsSeed=true)
  ├─ workItemRepo.SaveAsync(seed)        → persist to SQLite
  └─ orchestrator.PublishAsync(seed.Id)   → 13-step publish flow:
       ├─ Load seed from DB
       ├─ Validate (skipped with force=true)
       ├─ CreateAsync(seed) → ADO API
       ├─ FetchAsync(newId)
       ├─ WithIsSeed(true) ← provenance marker
       ├─ Transactional update:
       │    ├─ RecordMapping(seedId → newId)
       │    ├─ RemapId in seed_links
       │    ├─ RemapParentId in child seeds
       │    ├─ DeleteById(seedId)
       │    └─ SaveAsync(fetchedItem)
       ├─ PromoteLinksAsync(newId)
       └─ TryOrderAsync(newId, parentId)
```

**What's unnecessary for `twig new`:**

| Orchestrator Step | Why Unnecessary |
|---|---|
| Load seed from DB | We already have it in memory |
| Validate / force bypass | No validation needed for direct creation |
| RecordMapping | No negative-ID mapping needed |
| RemapId in seed_links | `twig new` never creates seed links |
| RemapParentId in child seeds | `twig new` creates unparented items |
| DeleteById(seedId) | No seed row to delete if we never saved one |
| PromoteLinksAsync | No seed links to promote |
| TryOrderAsync | No parent for backlog ordering |
| WithIsSeed(true) | Direct-created items shouldn't be marked as seeds |

The entire orchestrator roundtrip is unnecessary overhead for `twig new`.

### What This Fixes

The `NewCommand` currently:
1. **Creates an unnecessary local seed** — the negative-ID WorkItem is transient and never
   shown to the user, yet it's saved to SQLite and then deleted by the orchestrator.
2. **Depends on `SeedPublishOrchestrator`** — which pulls in 7 injected services
   (`IWorkItemRepository`, `ISeedLinkRepository`, `IPublishIdMapRepository`,
   `ISeedPublishRulesProvider`, `IUnitOfWork`, `IAdoWorkItemService`, `BacklogOrderer`),
   most of which are unused.
3. **Marks the resulting item with `IsSeed=true`** — provenance tracking that's misleading
   for items created directly via `twig new`.
4. **Has fragile error handling** — must clean up the transient seed on failure, adding
   a `DeleteByIdAsync` call that wouldn't be needed without the seed.

---

## Problem Statement

`twig new` routes through the seed pipeline (`SeedPublishOrchestrator`) despite never
needing the seed lifecycle. This adds unnecessary complexity, DI surface area, and
misleading provenance markers. The command should call the ADO API directly, matching
its intent: create an ADO item immediately.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | `NewCommand` calls `IAdoWorkItemService.CreateAsync` directly — no seed pipeline |
| G2 | No negative-ID seed is saved to SQLite before ADO creation |
| G3 | The fetched ADO item is saved with `IsSeed=false` (correct provenance) |
| G4 | `SeedPublishOrchestrator` is removed from `NewCommand`'s DI graph |
| G5 | All existing behavior preserved: `--title`, `--type`, `--area`, `--iteration`, `--description`, `--set`, `--editor`, `--output` |
| G6 | `twig seed new` → `twig seed publish` workflow is untouched |
| G7 | Tests updated and passing |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Removing `SeedFactory.CreateUnparented` (still useful for building in-memory WorkItem) |
| NG2 | Changing the `IAdoWorkItemService.CreateAsync` API signature |
| NG3 | Adding new features to `twig new` |
| NG4 | Modifying `SeedPublishOrchestrator` |

---

## Proposed Design

### Target Architecture

```
NewCommand.ExecuteAsync()
  ├─ SeedFactory.CreateUnparented()    → in-memory WorkItem (used as field container only)
  ├─ [Optional: editor flow]           → modify fields in-memory
  ├─ adoService.CreateAsync(workItem)  → POST to ADO REST API → positive ID
  ├─ adoService.FetchAsync(newId)      → GET full ADO-populated item
  └─ workItemRepo.SaveAsync(fetched)   → persist to SQLite (IsSeed=false)
```

**Key simplifications:**
- No seed saved to DB before ADO creation
- No seed counter initialization needed (negative IDs are not persisted)
- No orchestrator, no transactions, no ID remapping
- No seed cleanup on failure (nothing to clean up)
- Fetched item saved as-is (not marked IsSeed=true)

### Design Decisions

| ID | Decision | Alternatives Considered | Rationale |
|----|----------|------------------------|-----------|
| DD-01 | Keep `SeedFactory.CreateUnparented()` as field builder | Create a new `WorkItem.CreateDraft()` method | `CreateUnparented()` is a clean factory that sets Title, Type, AreaPath, IterationPath, AssignedTo. The `IsSeed=true` flag on the in-memory object doesn't matter — it's never persisted. Avoids code duplication. |
| DD-02 | Inject `IAdoWorkItemService` directly into `NewCommand` | Keep orchestrator with a "direct" mode | Direct injection is simpler, makes the dependency explicit, and removes 6 unused transitive dependencies. |
| DD-03 | Remove seed counter initialization from `NewCommand` | Keep it for safety | Seed counter init (`GetMinSeedIdAsync` + `InitializeSeedCounter`) is only needed when negative IDs will be persisted. Since we never save the seed, collisions are impossible. Removing it avoids a needless DB query. |
| DD-04 | On `CreateAsync` failure, return error without cleanup | Save seed first, clean up on failure | No seed is saved, so no cleanup needed. If `FetchAsync` fails after creation, the item exists in ADO and the user can recover via `twig refresh`. |

### Error Handling

| Scenario | Current Behavior | New Behavior |
|----------|-----------------|--------------|
| `CreateAsync` throws | Orchestrator throws → `NewCommand` deletes seed from DB → return 1 | `NewCommand` catches → return 1 with error message (no cleanup needed) |
| `FetchAsync` throws after creation | Same as above; ADO item exists but local DB has no record | Same outcome — item exists in ADO, user can `twig refresh`. We surface a specific error message noting the ADO ID. |
| `SaveAsync` throws | Orchestrator rollback (UnitOfWork) | Save failure after creation — item exists in ADO, local DB may be inconsistent. Rare edge case; `twig refresh` recovers. |

---

## Implementation Plan

### Epic 1: Refactor NewCommand to Direct ADO Creation

**Classification:** Deep (4 files, logic-heavy changes in command + tests)
**Estimated LoC:** ~200 (100 lines changed in command, 100 lines changed in tests)
**Predecessor:** None

#### Tasks

| Task | Description | File(s) | Effort |
|------|-------------|---------|--------|
| T1 | **Update `NewCommand` constructor** — Replace the `SeedPublishOrchestrator orchestrator` primary constructor parameter with `IAdoWorkItemService adoService`. Remove the `using Twig.Domain.Services` import only if `SeedFactory` (also in that namespace) is changed to a static import — otherwise keep it. The `SeedPublishResult` type is not explicitly imported in `NewCommand` (it's inferred from `orchestrator.PublishAsync()` return type), so removing the orchestrator parameter naturally eliminates all `SeedPublishResult` references. | `src/Twig/Commands/NewCommand.cs` | S |
| T2 | **Refactor `ExecuteAsync` core flow** — Remove the seed counter initialization block (the `GetMinSeedIdAsync` → `InitializeSeedCounter` call pair in the "Initialize seed counter" section). Remove the `workItemRepo.SaveAsync(seed)` pre-publish save (the "Save seed locally" section). Remove the `orchestrator.PublishAsync()` call and its surrounding `publishResult` handling. Replace with: `var newId = await adoService.CreateAsync(seed, ct)` → `var fetched = await adoService.FetchAsync(newId, ct)` → `await workItemRepo.SaveAsync(fetched, ct)`. | `src/Twig/Commands/NewCommand.cs` | M |
| T3 | **Update error handling** — Remove seed cleanup on failure (the `DeleteByIdAsync` call in the `!publishResult.IsSuccess` block). Replace with a try/catch around the `CreateAsync`/`FetchAsync`/`SaveAsync` sequence. On `CreateAsync` failure, return 1 with the exception message. On `FetchAsync` failure *after* a successful create, return 1 with a message that includes the successfully-created ADO ID (e.g., `"Created #{newId} in ADO but fetch-back failed. Run 'twig refresh' to recover."`) so the user can recover. | `src/Twig/Commands/NewCommand.cs` | S |
| T4 | **Update success output and context** — Replace all `publishResult.NewId` references with `newId` (the int returned by `CreateAsync`). Replace `publishResult.Title` with `fetched.Title` (or the original `seedTitle` variable as fallback). Update the `--set` conditional to use `newId` directly. Remove any remaining `publishResult` variable usage. After this task, `NewCommand` has zero references to `SeedPublishResult` or `SeedPublishOrchestrator`. | `src/Twig/Commands/NewCommand.cs` | S |
| T5 | **Rewrite `NewCommandTests`** — See detailed test rewrite plan below. Remove `SeedPublishOrchestrator` construction and all its mock dependencies from the test fixture (`ISeedLinkRepository`, `IPublishIdMapRepository`, `ISeedPublishRulesProvider`, `IUnitOfWork`, `BacklogOrderer`). The `IAdoWorkItemService` mock already exists in the fixture as `_adoService` — reuse it. Replace the `ArrangePublishSuccess` helper with `ArrangeCreateSuccess` that mocks `adoService.CreateAsync` → returns `newId` and `adoService.FetchAsync(newId)` → returns a built WorkItem. Update the `NewCommand` constructor call to pass `_adoService` instead of the orchestrator. **Specific test methods to rewrite:** (1) `New_ValidTitleAndType_CreatesAndPublishes` — remove assertion that `SaveAsync` receives an `IsSeed=true` seed; assert `CreateAsync` called once, `FetchAsync` called with returned ID, `SaveAsync` called once with a positive-ID `IsSeed=false` item. (2) `New_SetsAreaAndIterationFromConfig` — change `SaveAsync` assertion to verify the *fetched* item (not the seed). (3) `New_PublishFailure_CleansUpSeedAndReturns1` — rename to `New_CreateFailure_Returns1` and rewrite: mock `CreateAsync` to throw, verify exit code 1, verify `DeleteByIdAsync` is *not* called (no cleanup needed). (4) `New_NoDefaultsConfigured_FallsBackToProject` — update local `NewCommand` construction to use `_adoService` instead of orchestrator. (5) `ArrangePublishSuccess` helper — rename to `ArrangeCreateSuccess`, simplify to mock `CreateAsync`/`FetchAsync` only. (6) Constructor field cleanup — remove `_seedLinkRepo`, `_publishIdMapRepo`, `_rulesProvider`, `_unitOfWork` fields and their `Substitute.For<>()` calls. | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | L |
| T6 | **Add new tests** — (1) `New_FetchFailureAfterCreate_ReturnsErrorWithAdoId`: mock `CreateAsync` → 42, `FetchAsync` → throws `HttpRequestException`. Assert exit code 1 and stderr contains `"42"`. (2) `New_DoesNotSaveSeedBeforeCreate_AndFetchedItemSaved`: assert `workItemRepo.SaveAsync` called exactly once, with a WorkItem where `Id > 0` and `IsSeed == false`. | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | S |

#### Acceptance Criteria

1. `twig new --title "X" --type Epic` creates an ADO item with a positive ID immediately (no seed intermediate)
2. No `SeedPublishOrchestrator` in `NewCommand`'s constructor or execution
3. No negative-ID seed saved to SQLite at any point during `twig new`
4. The fetched ADO item is saved to local DB with `IsSeed=false`
5. `--set`, `--editor`, `--description`, `--area`, `--iteration`, `--output` all work as before
6. `twig seed new` → `twig seed publish` flow is completely unchanged
7. All existing tests pass; new tests cover direct creation flow

---

## Open Questions

| # | Question | Severity | Resolution |
|---|----------|----------|------------|
| 1 | Should `SeedFactory.CreateUnparented` be renamed since `NewCommand` uses it as a field container, not a seed? | Low | Out of scope (NG1). The name is accurate for all other callers. |
