# Seed Lifecycle Realignment — Cascade Discard, Cycle Detection, Unified Mutations

> **Epic:** #2169
> **Status**: 🔨 In Progress
> **Revision:** 0
> **Revision Notes:** Initial draft.

---

## Executive Summary

This epic enforces the seed lifecycle invariants documented in
`docs/specs/seed-lifecycle.spec.md` by closing six specific gaps between the
spec and the current implementation. Today, discarding a parent seed orphans its
children (violating H-2); `seed link` creates links without cycle detection
(violating L-4); `twig update` and `twig state` reject seeds outright instead of
routing to local mutations (violating M-1); batch publish begins without
pre-flight graph validation (violating P-6); `--link-branch` resolves repo GUIDs
from local git context instead of ADO project metadata; and negative seed IDs
could theoretically leak into ADO field values (violating I-2). This plan
decomposes those six concerns into five Issues under the epic, with PR groups
sized for reviewability.

---

## Background

### Current State

The seed subsystem is functionally complete for the happy path: seeds can be
created (`seed new`, `seed chain`), linked (`seed link`), validated
(`seed validate`), published (`seed publish`), and reconciled
(`seed reconcile`). The `SeedDependencyGraph` already implements Kahn's
topological sort for batch publish ordering, and `SeedPublishOrchestrator`
handles transactional ID remapping.

However, several invariants in the spec are not yet enforced:

| Invariant | Spec ID | Current State |
|-----------|---------|---------------|
| Cascade discard | H-2 | `SeedDiscardCommand` deletes only the target seed; children are orphaned |
| Eager cycle detection | L-4 | `SeedLinkCommand.LinkAsync` inserts links without checking for cycles |
| Unified mutation routing | M-1 | `UpdateCommand` and `StateCommand` always call ADO — seeds are rejected |
| Pre-flight graph validation | P-6 | `PublishAllAsync` publishes greedily; failures discovered mid-batch |
| Decoupled link-branch | — | `SeedPublishCommand` resolves repo GUID via `IAdoGitService` which depends on local git context |
| Negative ID escape guard | I-2 | No publish-time scan of seed field values for negative IDs |

### Call-Site Audit

The following table inventories all call sites affected by the cross-cutting
changes in this epic (IMutationProvider routing, SeedDiscardCommand cascading,
SeedDependencyGraph reuse):

| File | Method/Class | Current Usage | Impact |
|------|-------------|---------------|--------|
| `src/Twig/Commands/UpdateCommand.cs` | `ExecuteAsync` | Calls `adoService.FetchAsync` + `ConflictRetryHelper.PatchWithRetryAsync` directly | Must route through `IMutationProvider` for seed-aware branching |
| `src/Twig/Commands/StateCommand.cs` | `ExecuteAsync` | Calls `adoService.FetchAsync` + state validation + `ConflictRetryHelper.PatchWithRetryAsync` | Must route through `IMutationProvider` for seed-aware branching |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `State()` | Calls `ctx.AdoService.FetchAsync` + `ConflictRetryHelper.PatchWithRetryAsync` | Must route through `IMutationProvider` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `Update()` | Calls `ctx.AdoService.FetchAsync` + `ConflictRetryHelper.PatchWithRetryAsync` | Must route through `IMutationProvider` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `Patch()` | Calls `ctx.AdoService.FetchAsync` + `ConflictRetryHelper.PatchWithRetryAsync` | Must route through `IMutationProvider` |
| `src/Twig/Commands/SeedDiscardCommand.cs` | `ExecuteAsync` | Deletes single seed + its links | Must cascade to all descendant seeds |
| `src/Twig/Commands/SeedLinkCommand.cs` | `LinkAsync` | Inserts link without cycle check | Must load graph, add proposed edge, check for cycles before insert |
| `src/Twig.Domain/Services/Seed/SeedPublishOrchestrator.cs` | `PublishAllAsync` | Topological sort then publish; no pre-flight validation | Must validate full graph publishability before first ADO call |
| `src/Twig.Domain/Services/Seed/SeedDependencyGraph.cs` | `Sort()` | Used only by `PublishAllAsync` | Reused by `SeedLinkCommand` for eager cycle detection |
| `src/Twig/Commands/SeedPublishCommand.cs` | `ResolveBranchArtifactUriAsync` | Uses `IAdoGitService.GetProjectIdAsync/GetRepositoryIdAsync` (local git) | Must accept `--repo` arg and resolve via ADO project repos API |
| `src/Twig.Domain/Services/Navigation/BranchLinkService.cs` | `LinkBranchAsync` | Uses injected `IAdoGitService` | May need `--repo` override path |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | `AddCoreCommands` | Registers `SeedPublishCommand` with `IAdoGitService?` | May need `IMutationProvider` registrations |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | `AddTwigNetworkServices` | Registers `IAdoGitService` conditionally on git context | May need new repo-list API method |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | `AddTwigCoreServices` | Registers seed services | Must register `IMutationProvider` implementations |

---

## Problem Statement

The seed lifecycle spec defines 24 invariants across 5 categories (Identity,
Hierarchy, Link, Publish, Mutation). Six of these are not enforced by the current
implementation, creating three classes of problems:

1. **Data integrity** — Discarding a parent seed orphans children, leaving broken
   `ParentId` references that require manual `seed reconcile`. Negative seed IDs
   could theoretically appear in ADO field values if a user sets a field to a
   seed ID string.

2. **Graph safety** — Links can create cycles that are only detected at publish
   time, wasting user effort in composing invalid graphs. Batch publish starts
   creating ADO items before validating that the entire graph is publishable.

3. **Usability** — `twig update` and `twig state` refuse to operate on seeds,
   forcing users to use the separate `seed edit` command for field changes and
   providing no state management for seeds at all. The `--link-branch` flag
   requires local git context, which is unavailable in CI/CD and MCP scenarios.

---

## Goals and Non-Goals

### Goals

1. **H-2 enforcement** — `seed discard` cascades to all descendant seeds, with
   user confirmation showing the full discard plan.
2. **L-4 enforcement** — `seed link` eagerly detects cycles and hard-rejects
   cyclic links at creation time.
3. **M-1 enforcement** — `twig update` and `twig state` transparently route to
   local seed mutations when the active item is a seed.
4. **P-6 enforcement** — `seed publish --all` validates the full dependency graph
   before making any ADO API calls.
5. **Link-branch decoupling** — `--link-branch` resolves repo GUID from ADO
   project repo list using `--repo` name instead of local git context.
6. **I-2 enforcement** — Publish-time validation scans seed field values for
   negative IDs that would leak sentinel values to ADO.

### Non-Goals

- MCP tool parity for seed-specific operations (discard, link, publish) —
  tracked separately.
- Schema changes to `seed_links` or `work_items` tables.
- Undo/rollback for cascade discard.
- Cycle detection for published ADO work item links (only seed links).
- Multi-workspace seed operations.

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `seed discard <id>` must find all descendant seeds via recursive `ParentId` traversal and delete them along with the target |
| FR-2 | `seed discard` must display the full discard plan (target + N descendants) before prompting for confirmation |
| FR-3 | `seed link` must load the full seed link graph, add the proposed edge, and reject with error if a cycle is detected |
| FR-4 | `twig update <field> <value>` must apply field changes locally when the active item is a seed (no ADO API calls) |
| FR-5 | `twig state <name>` must apply state changes locally when the active item is a seed (no ADO API calls) |
| FR-6 | `seed publish --all` must validate that all seeds are publishable and all parent references resolve before creating any ADO items |
| FR-7 | `seed publish --all --link-branch <branch> --repo <name>` must resolve the repo GUID from the ADO project's repository list |
| FR-8 | Publish-time validation must reject seeds whose field values contain negative integer patterns matching seed ID format |
| FR-9 | Cascade discard must delete all `seed_links` referencing any discarded seed ID |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | Cycle detection must complete in O(V+E) time where V is seed count and E is link count |
| NFR-2 | All changes must be AOT-compatible (no reflection) |
| NFR-3 | New types must be registered in `TwigJsonContext` if serialized |
| NFR-4 | Telemetry must not include seed IDs, field names, or field values |
| NFR-5 | All new public methods must have XML doc comments |

---

## Proposed Design

### Architecture Overview

The design introduces one new abstraction (`IMutationProvider`) and extends three
existing components (`SeedDiscardCommand`, `SeedLinkCommand`,
`SeedPublishOrchestrator`) with the invariant enforcement required by the spec.

```
┌──────────────────────────────────────────────────┐
│                  Command Layer                    │
│  UpdateCommand ─┐                                │
│  StateCommand  ─┤─► IMutationProvider            │
│  MCP tools     ─┘     │                          │
│                   ┌───┴───┐                      │
│                   │ Seed? │                      │
│                   └───┬───┘                      │
│              yes ─┘       └─ no                  │
│    SeedMutationProvider    AdoMutationProvider    │
│    (local SQLite write)    (ADO API + conflict)   │
├──────────────────────────────────────────────────┤
│               Domain Services                     │
│  SeedDiscardOrchestrator  (cascade delete)        │
│  SeedDependencyGraph      (cycle detection reuse) │
│  SeedPublishOrchestrator  (pre-flight validation) │
│  SeedIdEscapeValidator    (I-2 negative ID scan)  │
├──────────────────────────────────────────────────┤
│              Infrastructure                       │
│  IAdoGitService           (repo-list resolution)  │
│  SqliteWorkItemRepository (descendant query)       │
└──────────────────────────────────────────────────┘
```

### Key Components

#### 1. IMutationProvider Interface (New)

```csharp
public interface IMutationProvider
{
    Task<MutationResult> UpdateFieldAsync(
        WorkItem item, string field, string value, bool append, CancellationToken ct);
    Task<MutationResult> ChangeStateAsync(
        WorkItem item, string newState, CancellationToken ct);
}
```

Two implementations:
- **`AdoMutationProvider`** — Wraps the existing ADO fetch → conflict resolution →
  patch → auto-push-notes → resync flow from `UpdateCommand`/`StateCommand`.
- **`SeedMutationProvider`** — Writes field/state changes directly to the local
  `work_items` row via `IWorkItemRepository.SaveAsync()`. No ADO calls, no
  conflict resolution, no pending change tracking.

**Routing:** The command layer (and MCP tools) checks `item.IsSeed` to select the
provider. This is not a DI-level decision — both providers are registered, and
the command picks the right one per invocation.

#### 2. SeedDiscardOrchestrator (New)

Encapsulates cascade-discard logic at the domain layer:

```csharp
public sealed class SeedDiscardOrchestrator(
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo)
{
    public async Task<SeedDiscardPlan> BuildDiscardPlanAsync(int seedId, CancellationToken ct);
    public async Task ExecuteDiscardAsync(SeedDiscardPlan plan, CancellationToken ct);
}
```

`BuildDiscardPlanAsync` loads all seeds, traverses `ParentId` chains to find
descendants, and returns a plan object listing all seeds to be discarded.
`ExecuteDiscardAsync` deletes links and seed rows for the entire subtree.

#### 3. Cycle Detection in SeedLinkCommand

Reuses `SeedDependencyGraph.Sort()` — after loading the current graph and adding
the proposed edge in-memory, if `cyclicIds` is non-empty, the link is rejected.
This adds a new static method to `SeedDependencyGraph`:

```csharp
public static bool WouldCreateCycle(
    IReadOnlyList<WorkItem> seeds,
    IReadOnlyList<SeedLink> existingLinks,
    SeedLink proposedLink);
```

#### 4. Pre-Flight Validation in SeedPublishOrchestrator

Before the publish loop, `PublishAllAsync` validates:
1. All seeds pass `SeedValidator.Validate()` (unless `--force`)
2. All `ParentId` references resolve to either a published item or another seed
   in the batch
3. No cycles in the dependency graph
4. No negative IDs in seed field values (I-2)

If any validation fails, the method returns a `SeedPublishBatchResult` with
errors and **zero ADO API calls**.

#### 5. Negative ID Escape Guard (SeedIdEscapeValidator)

A static validator that scans a seed's field values for patterns matching
negative integer seed IDs:

```csharp
public static class SeedIdEscapeValidator
{
    public static IReadOnlyList<SeedValidationFailure> Validate(
        WorkItem seed, IReadOnlySet<int> allSeedIds);
}
```

Checks each field value string for negative integers that exist in the current
seed ID set. This catches cases where a user accidentally sets a field value
like `"-1"` which is also a seed ID.

#### 6. Link-Branch Decoupling

Add `--repo` parameter to `seed publish`. When `--repo` is specified, resolve the
repository GUID by calling a new `IAdoGitService.GetRepositoryIdByNameAsync(repoName)`
method that queries the ADO project's repository list API instead of using the
pre-configured repository from local git context.

### Data Flow

#### Cascade Discard Flow
```
seed discard -3
  → SeedDiscardOrchestrator.BuildDiscardPlanAsync(-3)
    → Load all seeds
    → BFS/DFS from -3 via ParentId
    → Return plan: [-3, -4, -5] (target + 2 descendants)
  → Display plan, prompt confirmation
  → SeedDiscardOrchestrator.ExecuteDiscardAsync(plan)
    → For each ID: delete seed_links, delete work_item row
```

#### Eager Cycle Detection Flow
```
seed link -1 -2 --type blocks
  → Load all seeds + all seed_links
  → Construct proposed link (-1, -2, "blocks")
  → SeedDependencyGraph.WouldCreateCycle(seeds, links, proposed)
    → Build graph with proposed edge included
    → Run Kahn's algorithm
    → If cyclicIds non-empty → return true (cycle detected)
  → If cycle: reject with error message listing cyclic IDs
  → If no cycle: insert link
```

#### Unified Mutation Flow (Update)
```
twig update System.Title "New Name"
  → Resolve active item
  → item.IsSeed?
    → Yes: SeedMutationProvider.UpdateFieldAsync(item, field, value)
      → item.SetField(field, value)
      → workItemRepo.SaveAsync(item)
      → Return MutationResult.Success
    → No: AdoMutationProvider.UpdateFieldAsync(item, field, value)
      → Existing flow: fetch → conflict resolution → patch → notes → resync
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Two concrete `IMutationProvider` implementations rather than strategy pattern | Simple `if/else` routing is clearer than factory/strategy for exactly two branches; avoids over-abstraction |
| Reuse `SeedDependencyGraph` for cycle detection rather than DFS | Algorithm already exists and is proven; avoids maintaining two graph implementations |
| Domain-layer `SeedDiscardOrchestrator` rather than inline in command | Cascade logic is complex enough to warrant its own testable unit; command stays thin |
| Pre-flight validation returns errors, doesn't throw | Consistent with existing `SeedPublishBatchResult` pattern; callers check `HasErrors` |
| `--repo` flag on `seed publish` rather than global config | Not all workspaces have a single repo; per-invocation override is more flexible |
| Negative ID scan checks field values, not field names | Field names are system-defined; user-controlled values are where leakage occurs |

---

## Alternatives Considered

### Mutation Routing via DI Instead of Runtime Check

**Alternative:** Register `IMutationProvider` at DI time based on active item
type, using a factory that creates the right implementation.

**Pros:** Cleaner separation; command doesn't need to know about seeds.
**Cons:** The active item isn't known at DI registration time — it's resolved
per-invocation. Would require a factory of factories. Over-engineered for two
branches.

**Decision:** Runtime `item.IsSeed` check is simpler and explicit.

### Cycle Detection via DFS Instead of Kahn's

**Alternative:** Implement a separate DFS cycle detection that runs only on the
proposed edge without computing the full topological sort.

**Pros:** Potentially faster for single-edge checks (no need to sort all nodes).
**Cons:** `SeedDependencyGraph.Sort` already does cycle detection. Maintaining a
second algorithm increases code and test surface. The seed count is always small
(typically <50), so performance is irrelevant.

**Decision:** Reuse existing Kahn's implementation.

---

## Dependencies

### Internal
- `SeedDependencyGraph` — reused for cycle detection (no changes to algorithm)
- `SeedPublishOrchestrator` — extended with pre-flight validation
- `IWorkItemRepository` — new `GetDescendantSeedsAsync` method or in-memory traversal
- `IAdoGitService` — new method for repo-list resolution

### External
- ADO Git REST API `GET /_apis/git/repositories` — for `--repo` name resolution
- No new NuGet packages required

### Sequencing
- Issue 1 (IMutationProvider) has no dependencies and can start immediately
- Issue 2 (Cascade Discard) has no dependencies and can start immediately
- Issue 3 (Cycle Detection) has no dependencies and can start immediately
- Issue 4 (Pre-flight + I-2) depends on cycle detection patterns from Issue 3
- Issue 5 (Link-Branch Decoupling) has no dependencies and can start immediately

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `IMutationProvider` changes break existing `twig update`/`twig state` behavior for ADO items | Medium | High | Wrap existing logic in `AdoMutationProvider` with zero behavior change; extensive test coverage of ADO path |
| Cascade discard accidentally deletes too many seeds | Low | High | Display full discard plan with confirmation; `--yes` only skips prompt, not validation |
| Cycle detection false positives (e.g., non-directional links counted as edges) | Low | Medium | Only directional link types create graph edges; `Related` is excluded from cycle checks |
| Negative ID regex in field values produces false positives | Low | Low | Check against actual seed ID set, not arbitrary negative integers; only flag values that match a known seed ID |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `SeedMutationProvider` validate state transitions against process config, or allow arbitrary state strings for seeds? | Low | Seeds don't have process config association at creation time; recommend allowing any state string for seeds — validation occurs at publish |
| 2 | Should `--repo` default to the workspace-configured repository name if omitted, or require explicit specification? | Low | Recommend defaulting to configured repo name (from `resolvedRepository` in `NetworkServiceModule`), with `--repo` as override |
| 3 | Should cascade discard also cascade to seeds linked via `seed_links` (not just `ParentId`), e.g., successor chains? | Low | Spec H-2 only mentions ParentId-based cascading; recommend ParentId-only for now, with link-based cascading as future enhancement |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IMutationProvider.cs` | Interface for unified mutation routing (update field, change state) |
| `src/Twig.Domain/ValueObjects/MutationResult.cs` | Result type for mutation operations (Success/Error with message) |
| `src/Twig.Domain/Services/Seed/SeedMutationProvider.cs` | Local-only mutation provider for seeds (writes to SQLite) |
| `src/Twig.Domain/Services/Mutation/AdoMutationProvider.cs` | ADO mutation provider wrapping existing fetch→patch→resync flow |
| `src/Twig.Domain/Services/Seed/SeedDiscardOrchestrator.cs` | Cascade discard logic: builds plan, executes subtree deletion |
| `src/Twig.Domain/ValueObjects/SeedDiscardPlan.cs` | Value object listing seeds scheduled for cascade discard |
| `src/Twig.Domain/Services/Seed/SeedIdEscapeValidator.cs` | Scans seed field values for negative ID leakage (I-2) |
| `tests/Twig.Domain.Tests/Services/Seed/SeedDiscardOrchestratorTests.cs` | Unit tests for cascade discard |
| `tests/Twig.Domain.Tests/Services/Seed/SeedMutationProviderTests.cs` | Unit tests for seed mutation provider |
| `tests/Twig.Domain.Tests/Services/Seed/SeedIdEscapeValidatorTests.cs` | Unit tests for negative ID escape guard |
| `tests/Twig.Domain.Tests/Services/Seed/SeedDependencyGraphCycleTests.cs` | Unit tests for eager cycle detection |
| `tests/Twig.Cli.Tests/Commands/SeedDiscardCommandCascadeTests.cs` | CLI-layer tests for cascade discard behavior |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/UpdateCommand.cs` | Route to `IMutationProvider` based on `item.IsSeed`; skip ADO flow for seeds |
| `src/Twig/Commands/StateCommand.cs` | Route to `IMutationProvider` based on `item.IsSeed`; skip ADO flow for seeds |
| `src/Twig/Commands/SeedDiscardCommand.cs` | Use `SeedDiscardOrchestrator` for cascade delete; display plan with descendant count |
| `src/Twig/Commands/SeedLinkCommand.cs` | Add cycle detection before inserting link; load graph and check via `SeedDependencyGraph.WouldCreateCycle` |
| `src/Twig/Commands/SeedPublishCommand.cs` | Add `--repo` parameter; use repo-list resolution for link-branch |
| `src/Twig.Domain/Services/Seed/SeedDependencyGraph.cs` | Add `WouldCreateCycle` static method reusing Kahn's algorithm |
| `src/Twig.Domain/Services/Seed/SeedPublishOrchestrator.cs` | Add pre-flight validation in `PublishAllAsync`; integrate `SeedIdEscapeValidator` |
| `src/Twig.Domain/Services/Seed/SeedValidator.cs` | Integrate `SeedIdEscapeValidator` into validation pipeline |
| `src/Twig.Domain/Interfaces/IAdoGitService.cs` | Add `GetRepositoryIdByNameAsync(string repoName)` method |
| `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | Implement `GetRepositoryIdByNameAsync` via ADO repos list API |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `SeedMutationProvider`, `SeedDiscardOrchestrator` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `AdoMutationProvider`; wire mutation providers into commands |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Route `State()` and `Update()` through `IMutationProvider` for seed support |
| `src/Twig/Program.cs` | Add `--repo` parameter to `SeedPublish` command definition |
| `tests/Twig.Domain.Tests/Services/Seed/SeedDependencyGraphTests.cs` | Add tests for `WouldCreateCycle` method |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Add tests for seed mutation routing |
| `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Add tests for seed mutation routing |
| `tests/Twig.Cli.Tests/Commands/SeedPublishCommandTests.cs` | Add tests for `--repo` flag and pre-flight validation |

---

## ADO Work Item Structure

**Epic:** #2169 — Seed Lifecycle Realignment

### Issue 1: Unified Mutation Provider (M-1)

**Goal:** Introduce `IMutationProvider` so `twig update` and `twig state` work
transparently on both seeds and ADO items.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 1.1 | Define `IMutationProvider` interface and `MutationResult` value object | `IMutationProvider.cs`, `MutationResult.cs` | S |
| 1.2 | Implement `SeedMutationProvider` (local SQLite field/state writes) | `SeedMutationProvider.cs`, `SeedMutationProviderTests.cs` | M |
| 1.3 | Extract `AdoMutationProvider` from existing `UpdateCommand`/`StateCommand` logic | `AdoMutationProvider.cs` | M |
| 1.4 | Refactor `UpdateCommand` to route through `IMutationProvider` | `UpdateCommand.cs`, `UpdateCommandTests.cs` | M |
| 1.5 | Refactor `StateCommand` to route through `IMutationProvider` | `StateCommand.cs`, `StateCommandTests.cs` | M |
| 1.6 | Update MCP `MutationTools.State()` and `Update()` to route through `IMutationProvider` | `MutationTools.cs` | M |
| 1.7 | Register providers in DI (`TwigServiceRegistration.cs`, `CommandRegistrationModule.cs`) | DI modules | S |

**Acceptance Criteria:**
- [ ] `twig update System.Title "X"` on a seed updates the local row (no ADO call)
- [ ] `twig state Active` on a seed updates the local row (no ADO call)
- [ ] `twig update` on an ADO item behaves identically to current behavior
- [ ] `twig state` on an ADO item behaves identically to current behavior
- [ ] MCP `twig_update` and `twig_state` route correctly for seeds
- [ ] All existing `UpdateCommand` and `StateCommand` tests pass

### Issue 2: Cascade Discard (H-2)

**Goal:** Discarding a parent seed cascades to all descendant seeds.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 2.1 | Create `SeedDiscardPlan` value object and `SeedDiscardOrchestrator` with `BuildDiscardPlanAsync` | `SeedDiscardPlan.cs`, `SeedDiscardOrchestrator.cs` | M |
| 2.2 | Implement `ExecuteDiscardAsync` with link cleanup and row deletion | `SeedDiscardOrchestrator.cs`, `SeedDiscardOrchestratorTests.cs` | M |
| 2.3 | Refactor `SeedDiscardCommand` to use orchestrator; display plan with descendant count | `SeedDiscardCommand.cs`, `SeedDiscardCommandCascadeTests.cs` | M |
| 2.4 | Register `SeedDiscardOrchestrator` in DI | `TwigServiceRegistration.cs`, `CommandRegistrationModule.cs` | S |

**Acceptance Criteria:**
- [ ] Discarding a parent seed also deletes all child and grandchild seeds
- [ ] Discard plan shows target + descendant count before confirmation
- [ ] `--yes` skips confirmation but still cascades
- [ ] Seed links referencing any discarded ID are deleted
- [ ] Active context is cleared if the discarded seed was active

### Issue 3: Eager Cycle Detection (L-4)

**Goal:** `seed link` rejects cyclic links at creation time.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 3.1 | Add `WouldCreateCycle` static method to `SeedDependencyGraph` | `SeedDependencyGraph.cs`, `SeedDependencyGraphCycleTests.cs` | M |
| 3.2 | Integrate cycle detection into `SeedLinkCommand.LinkAsync` | `SeedLinkCommand.cs` | S |
| 3.3 | Add unit tests for cycle scenarios (direct, transitive, self-loop, ParentId edges) | `SeedDependencyGraphCycleTests.cs` | M |

**Acceptance Criteria:**
- [ ] `seed link -1 -2 --type blocks` followed by `seed link -2 -1 --type blocks` rejects second link with cycle error
- [ ] Self-referencing links (`seed link -1 -1`) are rejected
- [ ] Transitive cycles across 3+ nodes are detected
- [ ] ParentId-based edges are included in the cycle graph
- [ ] Non-directional link types (Related) do not create cycle-detection edges
- [ ] Error message lists the cyclic seed IDs

### Issue 4: Pre-Flight Validation & Negative ID Guard (P-6, I-2)

**Goal:** Batch publish validates the full graph before starting; negative
seed IDs in field values are caught.

**Prerequisites:** Issue 3 (reuses cycle detection patterns)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 4.1 | Implement `SeedIdEscapeValidator` static validator | `SeedIdEscapeValidator.cs`, `SeedIdEscapeValidatorTests.cs` | S |
| 4.2 | Add pre-flight validation phase to `SeedPublishOrchestrator.PublishAllAsync` | `SeedPublishOrchestrator.cs` | M |
| 4.3 | Integrate `SeedIdEscapeValidator` into pre-flight validation | `SeedPublishOrchestrator.cs` | S |
| 4.4 | Add tests for pre-flight rejection (broken parents, cycles, I-2 violations) | `SeedPublishOrchestratorTests.cs` | M |

**Acceptance Criteria:**
- [ ] `seed publish --all` with a seed whose ParentId points to a non-existent item fails before any ADO call
- [ ] `seed publish --all` with cyclic seeds fails before any ADO call
- [ ] A seed with field value `"-3"` where `-3` is a seed ID fails validation
- [ ] `--force` bypasses validation checks (but not cycle detection)
- [ ] Error messages are actionable (list specific seed IDs and issues)

### Issue 5: Link-Branch Decoupling & `--repo` Flag

**Goal:** `--link-branch` resolves repo GUID from ADO project repos API using
`--repo` name instead of local git context.

**Prerequisites:** None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| 5.1 | Add `GetRepositoryIdByNameAsync` to `IAdoGitService` and implement in `AdoGitClient` | `IAdoGitService.cs`, `AdoGitClient.cs` | M |
| 5.2 | Add `--repo` parameter to `SeedPublishCommand` and `Program.cs` | `SeedPublishCommand.cs`, `Program.cs` | S |
| 5.3 | Refactor `ResolveBranchArtifactUriAsync` to use repo-name-based resolution | `SeedPublishCommand.cs` | M |
| 5.4 | Add tests for `--repo` resolution and fallback behavior | `SeedPublishCommandTests.cs` | M |

**Acceptance Criteria:**
- [ ] `seed publish --all --link-branch feat/x --repo my-repo` resolves repo GUID from ADO
- [ ] When `--repo` is omitted, falls back to workspace-configured repository
- [ ] When repo name cannot be resolved, emits warning and skips linking (non-fatal)
- [ ] No dependency on local git context for repo GUID resolution

---

## PR Groups

### PG-1: Unified Mutation Provider (Deep)

**Issues:** Issue 1 (tasks 1.1–1.7)
**Classification:** Deep — few files, complex behavioral routing
**Estimated LoC:** ~800
**Estimated Files:** ~15

**Scope:** Introduces `IMutationProvider`, `SeedMutationProvider`,
`AdoMutationProvider`, refactors `UpdateCommand`, `StateCommand`, and MCP
`MutationTools` to route through the provider. Includes DI registration and
tests.

**Successor:** None (independent)

### PG-2: Cascade Discard + Cycle Detection (Deep)

**Issues:** Issue 2 (tasks 2.1–2.4), Issue 3 (tasks 3.1–3.3)
**Classification:** Deep — focused domain logic with graph algorithms
**Estimated LoC:** ~600
**Estimated Files:** ~12

**Scope:** `SeedDiscardOrchestrator` with cascade delete, `SeedDependencyGraph.WouldCreateCycle`,
`SeedLinkCommand` cycle detection integration, `SeedDiscardCommand` refactor.
These two issues share the dependency graph component and are naturally
cohesive.

**Successor:** PG-3

### PG-3: Pre-Flight Validation + Link-Branch Decoupling (Wide)

**Issues:** Issue 4 (tasks 4.1–4.4), Issue 5 (tasks 5.1–5.4)
**Classification:** Wide — touches many files, each change is mechanical
**Estimated LoC:** ~500
**Estimated Files:** ~12

**Scope:** `SeedIdEscapeValidator`, pre-flight validation in
`SeedPublishOrchestrator`, `--repo` flag, `GetRepositoryIdByNameAsync`,
`ResolveBranchArtifactUriAsync` refactor.

**Predecessor:** PG-2 (depends on cycle detection for pre-flight validation)

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | unified-mutation-provider | Issue 1 (1.1–1.7) | None | Deep |
| PG-2 | cascade-discard-cycle-detection | Issue 2 (2.1–2.4), Issue 3 (3.1–3.3) | None | Deep |
| PG-3 | preflight-validation-link-branch | Issue 4 (4.1–4.4), Issue 5 (5.1–5.4) | PG-2 | Wide |

### Execution Order

PG-1 and PG-2 are fully independent and may be implemented in parallel. Both
touch distinct file sets (PG-1: `UpdateCommand`, `StateCommand`, `MutationTools`,
new mutation providers; PG-2: `SeedDiscardCommand`, `SeedLinkCommand`,
`SeedDependencyGraph`, new discard orchestrator). DI module files
(`TwigServiceRegistration.cs`, `CommandRegistrationModule.cs`) are modified by
both groups, but the changes are additive registrations that do not conflict.

PG-3 must follow PG-2 because `SeedPublishOrchestrator`'s pre-flight validation
calls `SeedDependencyGraph.WouldCreateCycle`, which is introduced in PG-2. Issue 5
within PG-3 is independently implementable but is bundled with Issue 4 due to
their shared touchpoint in `SeedPublishCommand` and `SeedPublishCommandTests`.

### Validation Strategy

**PG-1 — Unified Mutation Provider**
- Build passes after refactoring `UpdateCommand` and `StateCommand` to use `IMutationProvider`
- All pre-existing `UpdateCommandTests` and `StateCommandTests` continue to pass via the `AdoMutationProvider` path
- New tests in `SeedMutationProviderTests` cover seed field/state mutations
- MCP tool routing verified via `MutationTools` tests
- Integration smoke test: `twig update`/`twig state` on an ADO item produces identical behavior to pre-change

**PG-2 — Cascade Discard + Cycle Detection**
- Build passes after adding `WouldCreateCycle` and `SeedDiscardOrchestrator`
- `SeedDependencyGraphCycleTests` cover direct, transitive, self-loop, and ParentId edge scenarios
- `SeedDiscardOrchestratorTests` cover single seed, tree, and link cleanup scenarios
- CLI cascade tests verify plan display and `--yes` skip behavior
- No regressions in existing `SeedDependencyGraphTests` or `SeedLinkCommandTests`

**PG-3 — Pre-Flight Validation + Link-Branch Decoupling**
- Requires PG-2 merged to branch before implementation (for `WouldCreateCycle`)
- `SeedIdEscapeValidatorTests` validate regex/ID set matching for I-2 scenarios
- `SeedPublishOrchestratorTests` cover broken-parent, cycle, and I-2 pre-flight rejections
- `SeedPublishCommandTests` cover `--repo` resolution, fallback, and unresolvable-repo warning
- End-to-end: `seed publish --all` on an invalid graph returns errors without any ADO calls

---

## References

- `docs/specs/seed-lifecycle.spec.md` — Authoritative seed lifecycle specification
- `docs/specs/mutation-commands.spec.md` — Mutation command specification
- `docs/projects/mutation-commands-realignment.plan.md` — Prior mutation work
- `docs/projects/seed-publish-link-branch.plan.md` — Prior link-branch work
