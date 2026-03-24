# Plan 3 of 3: Quality Gates, Publish Pipeline, and Backlog Ordering

> **Revision**: 3  
> **Date**: 2026-03-24  
> **Status**: Draft  
> **Revision Notes**: Addresses technical review feedback (score 80/100). Fixes three critical issues (ParentId in dependency graph, ParentId remapping, transactional delete+remap), one significant issue (topological sort link type coverage), and two minor issues (IsSeed init-only copy, reconcile stale-reference discovery). Updated DD-07, DD-10, DD-11 and added Epic 3 tasks for new mechanisms.

---

## Executive Summary

This plan completes the Twig seed lifecycle by adding a configurable validation gate, a publish pipeline that pushes local seeds to Azure DevOps, backlog ordering on publish, a reconciliation command for partial-publish recovery, and ADO link creation for virtual seed links. Together, these capabilities transform seeds from local drafts into full ADO work items while preserving link relationships and placing items near their siblings in the backlog. The design extends the existing `IAdoWorkItemService`, `ISeedLinkRepository`, and command infrastructure established in Plans 1 and 2, following the same Native AOT, source-gen JSON, and process-agnostic patterns used throughout the codebase.

---

## Background

### Current State

Plans 1 and 2 established a local-first seed workflow:

- **Seeds** are `WorkItem` aggregates with `IsSeed=true` and negative sentinel IDs (decremented via `Interlocked.Decrement`). They are created by `WorkItem.CreateSeed()`, stored in the `work_items` SQLite table, and edited via `SeedEditorFormat` section-header format.
- **Virtual links** (`SeedLink` records) track relationships between seeds (or seed-to-real item) in the `seed_links` SQLite table. Link types include `parent-child`, `blocks`/`blocked-by`, `depends-on`/`depended-on-by`, and `related`. `SeedLinkCommand` stores only single-direction links (no reverse links created).
- **Commands**: `seed new`, `seed edit`, `seed discard`, `seed view`, `seed link`/`unlink`/`links`, `seed chain`, and `next`/`prev` navigation.
- **ADO integration**: `IAdoWorkItemService.CreateAsync(WorkItem)` already pushes a seed to ADO and returns the new positive ID. `AdoRestClient.CreateAsync` calls `MapSeedToCreatePayload(seed, _orgUrl, seed.ParentId)`, which serializes Title, AreaPath, IterationPath, and the parent link relation (using `ParentRelationType = "System.LinkTypes.Hierarchy-Reverse"`). **Critically, `seed.ParentId` is passed directly to the API** — if ParentId is negative (references another seed), this will produce an invalid ADO URL.
- **WorkItem properties**: `ParentId` and `IsSeed` are both `init`-only. The existing `WithSeedFields` method creates a copy preserving all properties. No method exists to change `ParentId` or `IsSeed` on an existing instance.
- **IUnitOfWork / ITransaction**: The codebase already has a transactional pattern — `SqliteUnitOfWork` wraps `SqliteTransaction` via `BeginAsync`/`CommitAsync`/`RollbackAsync`. Used in `SaveBatchAsync` and elsewhere.
- **No publish pipeline exists** — seeds live locally forever until manually discarded. No validation, no link promotion, no backlog ordering.

### What Changed

User feedback established that seeds need a "promote to ADO" path with validation gates to prevent incomplete items from being published. The user confirmed that **all populated fields should be included** when publishing (not just Title/Area/Iteration), resolving a key open question about field scope.

---

## Problem Statement

Seeds are currently write-only local drafts with no path to become real ADO work items. Users must manually create equivalent items in ADO, defeating the purpose of the local-first drafting workflow. Specifically:

1. **No validation** — a seed with only a title can be "published" with no guardrails, creating low-quality ADO items.
2. **No publish command** — `IAdoWorkItemService.CreateAsync` exists but no CLI command orchestrates the full lifecycle (validate → create → fetch-back → remap links → order backlog).
3. **Virtual links are stranded** — seed_links have no promotion path to real ADO relations.
4. **No backlog ordering** — published items land at arbitrary positions in the ADO backlog.
5. **No recovery** — if publish is interrupted mid-batch, the system has no way to detect or repair partial state.
6. **ParentId can reference unpublished seeds** — `WorkItem.ParentId` may hold a negative ID (another seed) from context-based creation. Publishing such a seed without first publishing its parent would send an invalid negative ID to ADO.

---

## Goals and Non-Goals

### Goals

1. **G1**: Configurable publish rules stored in `.twig/seed-rules.json` with sensible defaults (title required).
2. **G2**: `twig seed validate [<id>]` command that checks seeds against publish rules with pass/fail output.
3. **G3**: `twig seed publish <id> [--all] [--force] [--dry-run]` command that orchestrates the full publish pipeline.
4. **G4**: All populated seed fields are included in the ADO create payload (user-confirmed design decision).
5. **G5**: Virtual seed links are promoted to real ADO links when both endpoints have positive IDs.
6. **G6**: Published items receive a StackRank/BacklogPriority value near their parent's siblings (best-effort).
7. **G7**: `twig seed reconcile` command repairs orphaned links and stale ID references using a persistent publish ID map.
8. **G8**: ID remapping in `seed_links` and `work_items.parent_id` ensures partial publishes leave consistent state.
9. **G9**: Dependency graph includes both seed_links and ParentId edges so `--all` publishes parents before children.

### Non-Goals

- **NG1**: Bi-directional sync (ADO to seed). Seeds are one-way: local to ADO.
- **NG2**: Undo/rollback of published items (once in ADO, it stays).
- **NG3**: Parallel/concurrent publishing (sequential only, to respect ADO rate limits).
- **NG4**: Custom rule logic beyond field-presence checks (no expressions, no cross-field validation).
- **NG5**: Publish to multiple ADO organizations or projects.

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | Publish rules are loaded from `.twig/seed-rules.json` with a fallback to default (title-only) |
| FR-02 | Rules support field-presence checks by reference name and structural checks (has-parent) |
| FR-03 | `seed validate` outputs per-seed pass/fail with per-rule detail |
| FR-04 | `seed publish <id>` creates the work item in ADO with all populated fields |
| FR-05 | `seed publish` fetches the created item back and replaces the local seed row |
| FR-06 | `seed publish` promotes virtual links to ADO relations when both endpoints are positive |
| FR-07 | `seed publish --all` publishes in topological order based on dependency graph (seed_links + ParentId) |
| FR-08 | `seed publish --dry-run` validates and reports without calling ADO |
| FR-09 | `seed publish --force` skips validation |
| FR-10 | Backlog ordering sets StackRank/BacklogPriority near parent siblings (best-effort) |
| FR-11 | `seed reconcile` repairs stale/orphaned seed_links entries using the publish ID map |
| FR-12 | ID remapping updates all seed_links references and child seed ParentId values from old negative ID to new positive ID |
| FR-13 | Already-published seeds (positive ID) are skipped gracefully |
| FR-14 | Circular dependency in the graph is detected and reported as an error |
| FR-15 | Local SQLite operations during publish (delete old seed, save new item, remap IDs) are wrapped in a single transaction |
| FR-16 | A publish ID map (old negative ID → new positive ID) is persisted for reconcile stale-reference repair |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Native AOT compatible — no reflection, source-gen JSON for seed-rules.json |
| NFR-02 | Process-agnostic — dynamically detect StackRank vs BacklogPriority from field_definitions |
| NFR-03 | Publish failures are atomic per-seed — link failure is a warning, not a rollback |
| NFR-04 | Sequential publish with no parallelism to avoid ADO rate limits |
| NFR-05 | Both SpectreRenderer and HumanOutputFormatter rendering paths |

---

## Proposed Design

### Architecture Overview

```
+------------------------------------------------------+
|                     CLI Layer (Twig)                  |
|  +--------------+ +--------------+ +---------------+ |
|  | SeedValidate | | SeedPublish  | |SeedReconcile  | |
|  | Command      | | Command      | |Command        | |
|  +------+-------+ +------+-------+ +------+--------+ |
|         |                |                |           |
|         v                v                v           |
|  +----------------------------------------------------+
|  |              SeedPublishOrchestrator                |
|  |  (validate → create → fetch → remap → order)       |
|  +------------------------+---------------------------+ |
|                           |                             |
+---------------------------+-----------------------------+
|                  Domain Layer                           |
|  +--------------+ +-----+-----+ +--------------------+ |
|  |SeedPublish   | |SeedLink   | |BacklogOrderer      | |
|  |RulesValidator| |Promoter   | |                    | |
|  +------+-------+ +-----+-----+ +------+-------------+ |
|         |               |              |                |
|  +------+-----+---------+---+----------+-------+-------+
|  | SeedDep.   | SeedLinkType | WorkItem        |       |
|  | Graph      | Mapper       | .WithParentId() |       |
|  | (+ParentId | (+blocks     | .WithIsSeed()   |       |
|  |  edges)    |  +dep-on-by) |                 |       |
|  +------+-----+------+-------+--------+-------+       |
|         |             |                |                |
|  +------+-------------+----------------+----------------+
|  |           Interfaces (contracts)                     |
|  | ISeedPublishRulesProvider, IAdoWorkItemService,      |
|  | ISeedLinkRepository, IWorkItemRepository,            |
|  | IFieldDefinitionStore, IUnitOfWork                   |
|  +------------------------+-----------------------------+
|                           |                             |
+---------------------------+-----------------------------+
|               Infrastructure Layer                      |
|  +------------------------------------------------------+
|  | FileSeedPublishRulesProvider (seed-rules.json)        |
|  | AdoRestClient (+AddLinkAsync)                        |
|  | SqliteSeedLinkRepository (+RemapIdAsync)              |
|  | SqliteWorkItemRepository (+RemapParentIdAsync)        |
|  | AdoResponseMapper (+MapSeedFieldsToPayload)          |
|  | publish_id_map table (old_id → new_id)               |
|  +------------------------------------------------------+
+----------------------------------------------------------+
```

### Key Components

#### 1. SeedPublishRules (Domain Model)

```csharp
// src/Twig.Domain/ValueObjects/SeedPublishRules.cs
public sealed class SeedPublishRules
{
    public IReadOnlyList<string> RequiredFields { get; init; } = [];
    public bool RequireParent { get; init; }
    
    public static SeedPublishRules Default => new()
    {
        RequiredFields = ["System.Title"],
        RequireParent = false,
    };
}
```

The JSON file format (`.twig/seed-rules.json`):
```json
{
  "requiredFields": ["System.Title", "System.Description"],
  "requireParent": true
}
```

`RequiredFields` lists field reference names that must have non-empty values. `RequireParent` is a structural check that the seed has a `ParentId` set. Default rules (when no file exists) require only `System.Title`.

#### 2. ISeedPublishRulesProvider (Domain Interface)

```csharp
// src/Twig.Domain/Interfaces/ISeedPublishRulesProvider.cs
public interface ISeedPublishRulesProvider
{
    Task<SeedPublishRules> GetRulesAsync(CancellationToken ct = default);
}
```

#### 3. FileSeedPublishRulesProvider (Infrastructure)

```csharp
// src/Twig.Infrastructure/Config/FileSeedPublishRulesProvider.cs
internal sealed class FileSeedPublishRulesProvider : ISeedPublishRulesProvider
```

Loads `.twig/seed-rules.json` using `TwigJsonContext` source-gen. Returns `SeedPublishRules.Default` if the file does not exist. Throws `TwigConfigurationException` on malformed JSON (same pattern as `TwigConfiguration.LoadAsync`).

#### 4. SeedValidator (Domain Service)

```csharp
// src/Twig.Domain/Services/SeedValidator.cs
public static class SeedValidator
{
    public static SeedValidationResult Validate(WorkItem seed, SeedPublishRules rules);
}

// src/Twig.Domain/ValueObjects/SeedValidationResult.cs
public sealed class SeedValidationResult
{
    public int SeedId { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool Passed => Failures.Count == 0;
    public IReadOnlyList<SeedValidationFailure> Failures { get; init; } = [];
}

public readonly record struct SeedValidationFailure(string Rule, string Message);
```

Validation logic:
- For each field in `RequiredFields`: check `seed.Title` for `System.Title`, `seed.Fields[refName]` for others. Missing/empty = failure.
- If `RequireParent` and `seed.ParentId` is null = failure.

#### 5. WorkItem Copy Helpers (Domain Aggregate)

Two new copy methods on `WorkItem` to handle init-only property mutation during publish:

```csharp
// Added to src/Twig.Domain/Aggregates/WorkItem.cs

/// <summary>
/// Returns a copy with a different ParentId. Used during batch publish
/// to update child seeds after their parent seed is published.
/// </summary>
public WorkItem WithParentId(int? newParentId)
{
    var copy = new WorkItem
    {
        Id = Id, Type = Type, Title = Title, State = State,
        AssignedTo = AssignedTo, IterationPath = IterationPath,
        AreaPath = AreaPath, ParentId = newParentId,
        IsSeed = IsSeed, SeedCreatedAt = SeedCreatedAt,
        LastSyncedAt = LastSyncedAt,
    };
    if (Revision > 0) copy.MarkSynced(Revision);
    copy.ImportFields(Fields);
    if (IsDirty) copy.SetDirty();
    return copy;
}

/// <summary>
/// Returns a copy with IsSeed set to the given value. Used after FetchAsync
/// to mark a fetched-back item as seed-originated.
/// </summary>
public WorkItem WithIsSeed(bool isSeed)
{
    var copy = new WorkItem
    {
        Id = Id, Type = Type, Title = Title, State = State,
        AssignedTo = AssignedTo, IterationPath = IterationPath,
        AreaPath = AreaPath, ParentId = ParentId,
        IsSeed = isSeed, SeedCreatedAt = SeedCreatedAt,
        LastSyncedAt = LastSyncedAt,
    };
    if (Revision > 0) copy.MarkSynced(Revision);
    copy.ImportFields(Fields);
    return copy;
}
```

**Rationale**: `ParentId` and `IsSeed` are `init`-only on the sealed `WorkItem` class. The `WithSeedFields` method already establishes the copy pattern. `WithParentId` is needed to update child seeds' parent references after a parent seed is published. `WithIsSeed` is needed because `FetchAsync` returns a `WorkItem` with `IsSeed=false` (ADO doesn't know about seeds), but we need to preserve the `IsSeed=true` flag for provenance tracking.

#### 6. SeedPublishOrchestrator (Domain Service)

The core publish pipeline lives in the domain layer as a service that accepts interface dependencies. This keeps it testable and independent of the CLI layer.

```csharp
// src/Twig.Domain/Services/SeedPublishOrchestrator.cs
public sealed class SeedPublishOrchestrator
{
    // Dependencies: IAdoWorkItemService, IWorkItemRepository, ISeedLinkRepository,
    //               ISeedPublishRulesProvider, IFieldDefinitionStore,
    //               SeedLinkPromoter, BacklogOrderer, IUnitOfWork

    public Task<SeedPublishResult> PublishAsync(int seedId, bool force, bool dryRun, CancellationToken ct);
    public Task<SeedPublishBatchResult> PublishAllAsync(bool force, bool dryRun, CancellationToken ct);
}
```

**Single-seed publish flow** (`PublishAsync`):
1. Load seed from `IWorkItemRepository.GetByIdAsync(seedId)`.
2. Guard: if `seedId > 0`, return already-published skip result.
3. Guard: if seed is null or `!IsSeed`, return error.
4. Guard: if `seed.ParentId < 0`, return error — parent seed must be published first (batch publish handles this automatically; single-seed publish requires the user to publish the parent first).
5. Unless `force`, run `SeedValidator.Validate(seed, rules)`. If failures, return validation-failed result.
6. If `dryRun`, return dry-run result (what would be published).
7. Call `IAdoWorkItemService.CreateAsync(seed)` → returns `newId` (positive).
8. Fetch back: `IAdoWorkItemService.FetchAsync(newId)` → full ADO-populated `WorkItem`.
9. Mark provenance: `fetchedItem = fetchedItem.WithIsSeed(true)` to preserve seed origin.
10. **Transactional local update** (via `IUnitOfWork`):
    a. Record publish mapping: insert `(seedId, newId)` into `publish_id_map` table.
    b. Remap ID in seed_links: `ISeedLinkRepository.RemapIdAsync(seedId, newId)`.
    c. Remap ParentId in child seeds: `IWorkItemRepository.RemapParentIdAsync(seedId, newId)` — updates any `work_items` rows where `parent_id = seedId` to `parent_id = newId`.
    d. Delete old seed row: `IWorkItemRepository.DeleteByIdAsync(seedId)`.
    e. Save new item: `IWorkItemRepository.SaveAsync(fetchedItem)`.
    f. Commit transaction.
11. Promote links: for each `SeedLink` involving `newId` (now positive), if the other endpoint is also positive → call `IAdoWorkItemService.AddLinkAsync(...)`. If still negative → leave for later.
12. Best-effort backlog ordering (see BacklogOrderer below).
13. Return success result with `(oldId, newId, title, linkWarnings)`.

**Key change from v2**: Steps 10a–10f are wrapped in a single SQLite transaction via `IUnitOfWork.BeginAsync()`. This eliminates the data loss window where a crash between delete and remap could leave orphaned seed_link references. The remap happens *before* the delete, so even if the transaction is interrupted before commit, no data is lost — the old seed row still exists with its original ID.

**Batch publish flow** (`PublishAllAsync`):
1. Load all seeds via `GetSeedsAsync()`.
2. Load all seed_links via `GetAllSeedLinksAsync()`.
3. Build dependency graph (seed_links + ParentId edges) and topological sort. Detect cycles → error with clear message.
4. For each seed in topological order:
   a. Re-load the seed from SQLite (may have been updated by a prior publish's `RemapParentIdAsync`).
   b. Call `PublishAsync` per-seed. Note: `PublishAsync` step 10c (`RemapParentIdAsync`) will update child seeds in the DB, so later seeds in the order will see the correct positive ParentId.
5. Aggregate results.

**Why re-load**: After publishing parent seed -1 (→ #42), `RemapParentIdAsync(-1, 42)` updates all `work_items` rows where `parent_id = -1`. Child seed -2 (which had `ParentId=-1`) now has `parent_id=42` in the DB. Re-loading seed -2 before its publish picks up this change, so `CreateAsync` sends the correct positive parent ID to ADO.

#### 6a. Topological Sort for --all

For `--all`, the dependency graph includes **both** seed_links edges and ParentId edges:

**seed_links edges** — directional dependency relationships:
- `depends-on`: source depends on target → edge from source to target (publish target first)
- `blocked-by`: source is blocked by target → edge from source to target (publish target first)
- `blocks`: source blocks target → edge from target to source (publish source first)
- `depended-on-by`: source is depended-on-by target → edge from target to source (publish source first)
- `parent-child` and `related`: no ordering implication (excluded from graph)

**ParentId edges** — structural parent dependency:
- For each seed where `ParentId < 0` (parent is another seed), add edge from child to parent (publish parent first).
- This is critical because `AdoRestClient.CreateAsync` passes `seed.ParentId` directly to `MapSeedToCreatePayload`, which constructs a parent link URL. A negative ParentId would produce an invalid ADO URL, causing a 400 or 404 error.

**Sort behavior**:
- Seeds with no incoming edges publish first.
- Circular dependencies are detected via standard cycle-detection during topological sort and reported as an error (refuse to publish the cycle, still publish non-cyclic seeds).
- Seeds not connected by dependency links publish in creation order (`SeedCreatedAt`).

#### 7. AddLinkAsync on IAdoWorkItemService

```csharp
// Added to IAdoWorkItemService
Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);
```

Implementation in `AdoRestClient`: PATCH on the source work item adding a relation:
```json
[
  {
    "op": "add",
    "path": "/relations/-",
    "value": {
      "rel": "<adoLinkType>",
      "url": "https://dev.azure.com/{org}/_apis/wit/workitems/{targetId}"
    }
  }
]
```

**SeedLinkType to ADO relation type mapping** (in `SeedLinkTypeMapper`):

| SeedLinkType | ADO Relation Type |
|---|---|
| `parent-child` | `System.LinkTypes.Hierarchy-Forward` |
| `blocks` | `System.LinkTypes.Dependency-Forward` |
| `blocked-by` | `System.LinkTypes.Dependency-Reverse` |
| `depends-on` | `System.LinkTypes.Dependency-Reverse` |
| `depended-on-by` | `System.LinkTypes.Dependency-Forward` |
| `related` | `System.LinkTypes.Related` |

Note: `parent-child` links are already handled by `MapSeedToCreatePayload` via the parent relation. The link promoter skips `parent-child` links where the source is the child (already set at creation) to avoid duplicates.

#### 8. RemapIdAsync on ISeedLinkRepository

```csharp
// Added to ISeedLinkRepository
Task RemapIdAsync(int oldId, int newId, CancellationToken ct = default);
```

Implementation: two UPDATE statements:
```sql
UPDATE seed_links SET source_id = @newId WHERE source_id = @oldId;
UPDATE seed_links SET target_id = @newId WHERE target_id = @oldId;
```

#### 9. RemapParentIdAsync on IWorkItemRepository

```csharp
// Added to IWorkItemRepository
Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default);
```

Implementation: single UPDATE statement:
```sql
UPDATE work_items SET parent_id = @newParentId WHERE parent_id = @oldParentId;
```

**Rationale**: After publishing parent seed -1 (→ #42), child seeds still hold `parent_id = -1` in the SQLite `work_items` table. This bulk update fixes all children at once. Combined with the re-load in the batch flow (step 4a above), this ensures child seeds are published with the correct positive ParentId.

#### 10. Publish ID Map Table

A new `publish_id_map` SQLite table tracks the mapping from old negative seed IDs to new positive ADO IDs:

```sql
CREATE TABLE IF NOT EXISTS publish_id_map (
    old_id INTEGER PRIMARY KEY,
    new_id INTEGER NOT NULL,
    published_at TEXT NOT NULL
);
```

Populated during publish step 10a. Used by the reconcile command to detect stale references — if a seed_link references a negative ID that exists in `publish_id_map`, the link can be repaired (remapped to the new positive ID) rather than deleted as orphaned.

A new `IPublishIdMapRepository` interface (or methods on `ISeedLinkRepository`) provides:
```csharp
Task RecordMappingAsync(int oldId, int newId, CancellationToken ct = default);
Task<int?> GetNewIdAsync(int oldId, CancellationToken ct = default);
Task<IReadOnlyList<(int OldId, int NewId)>> GetAllMappingsAsync(CancellationToken ct = default);
```

#### 11. MapSeedToCreatePayload Enhancement

Currently `AdoResponseMapper.MapSeedToCreatePayload` only serializes Title, AreaPath, IterationPath, and parent link. Per the user's confirmation ("all populated fields"), this must be extended to include every non-empty field in `seed.Fields`:

```csharp
// For each field in seed.Fields where value is non-empty:
foreach (var (refName, value) in seed.Fields)
{
    if (string.IsNullOrEmpty(value)) continue;
    // Skip fields already handled (System.Title, System.AreaPath, System.IterationPath)
    if (IsAlreadyHandled(refName)) continue;
    operations.Add(new AdoPatchOperation
    {
        Op = "add",
        Path = $"/fields/{refName}",
        Value = JsonValue.Create(value),
    });
}
```

Read-only and computed fields (System.Id, System.Rev, System.CreatedDate, etc.) are filtered using the same excluded-fields set from `SeedEditorFormat`, extracted to a shared constant.

#### 12. BacklogOrderer (Domain Service)

```csharp
// src/Twig.Domain/Services/BacklogOrderer.cs
public sealed class BacklogOrderer
{
    // Dependencies: IAdoWorkItemService, IFieldDefinitionStore

    public Task<bool> TryOrderAsync(int itemId, int? parentId, CancellationToken ct);
}
```

Flow:
1. Detect ordering field: query `IFieldDefinitionStore` for `Microsoft.VSTS.Common.StackRank` or `Microsoft.VSTS.Common.BacklogPriority`. If neither exists, return false.
2. If `parentId` is null, return false.
3. Fetch siblings: `IAdoWorkItemService.FetchChildrenAsync(parentId)`.
4. Find the maximum ordering field value among siblings.
5. Set the new item's ordering field to `maxValue + 1.0`.
6. Call `IAdoWorkItemService.PatchAsync(itemId, [orderFieldChange], revision)`.
7. Return true. On any exception, log warning and return false (best-effort).

Process detection: Agile uses `Microsoft.VSTS.Common.StackRank` (double). Scrum/CMMI use `Microsoft.VSTS.Common.BacklogPriority` (double). We detect which exists in `field_definitions`.

#### 13. SeedReconcileOrchestrator (Domain Service)

```csharp
// src/Twig.Domain/Services/SeedReconcileOrchestrator.cs
public sealed class SeedReconcileOrchestrator
{
    // Dependencies: ISeedLinkRepository, IWorkItemRepository, IPublishIdMapRepository

    public Task<SeedReconcileResult> ReconcileAsync(CancellationToken ct);
}
```

Reconciliation logic:
1. Load all seed_links.
2. Load all publish ID mappings from `publish_id_map`.
3. For each link, check if both source and target exist in `work_items`:
   - If an endpoint ID is negative and no seed with that ID exists:
     a. Check `publish_id_map` for a mapping → if found, remap the stale reference to the new positive ID (this handles the interrupted-publish case).
     b. If no mapping exists → orphaned link (seed was discarded), delete it.
   - If both endpoints are positive → the link should already be an ADO relation, optionally verify and remove from seed_links.
4. Also check `work_items.parent_id` for stale negative references:
   - Query `SELECT id, parent_id FROM work_items WHERE parent_id < 0`.
   - For each, check `publish_id_map` → if found, update `parent_id` to the new positive ID.
   - If no mapping → log warning (parent seed was discarded without publishing).
5. Report summary: links repaired, links removed, parent IDs fixed, warnings.

#### 14. CLI Commands

**SeedValidateCommand** (`src/Twig/Commands/SeedValidateCommand.cs`):
```
twig seed validate [<id>]
```
- With ID: validate one seed, detailed output, exit 0/1.
- Without ID: validate all seeds, summary table, exit 0 if all pass.

**SeedPublishCommand** (`src/Twig/Commands/SeedPublishCommand.cs`):
```
twig seed publish <id> [--all] [--force] [--dry-run]
```
- Delegates to `SeedPublishOrchestrator`.
- `--all` ignores `<id>` arg and publishes all.
- `--dry-run` shows plan without executing.
- `--force` skips validation.

**SeedReconcileCommand** (`src/Twig/Commands/SeedReconcileCommand.cs`):
```
twig seed reconcile
```
- Delegates to `SeedReconcileOrchestrator`.
- Reports repairs and removals.

### Data Flow: Single Seed Publish

```
User: twig seed publish -5
  |
  v
SeedPublishCommand
  | 1. Load seed #-5 from SQLite
  | 2. Guard: ParentId=-3 is negative → error "publish parent first"
  |    (or if parent already published, ParentId > 0 → proceed)
  | 3. Load publish rules from .twig/seed-rules.json
  | 4. SeedValidator.Validate(seed, rules) → pass/fail
  | 5. (fail? → error exit)
  | 6. AdoRestClient.CreateAsync(seed) → newId=42
  |    ⇒ POST /{project}/_apis/wit/workitems/$Task
  |      with all populated fields + parent link
  | 7. AdoRestClient.FetchAsync(42) → full WorkItem
  | 8. fetchedItem.WithIsSeed(true) → preserve provenance
  | 9. BEGIN TRANSACTION:
  |    a. publish_id_map: insert (-5 → 42)
  |    b. seed_links: RemapIdAsync(-5, 42)
  |    c. work_items: RemapParentIdAsync(-5, 42)
  |    d. Delete seed #-5
  |    e. Save item #42 (IsSeed=true)
  |    COMMIT
  | 10. For each seed_link involving #42:
  |     if other endpoint > 0 → AddLinkAsync(42, other, type)
  | 11. BacklogOrderer.TryOrderAsync(42, parentId)
  |     ⇒ Fetch siblings, compute StackRank, PatchAsync
  |
  v
Output: "Published seed #-5 as #42: My Task Title"
```

### Data Flow: Batch Publish (--all)

```
Seeds: [-1 child of -2], [-2 depends-on -3], [-3] (no deps)

1. Build graph:
   - ParentId edge: -1 → -2 (child depends on parent)
   - seed_links edge: -2 → -3 (depends-on)
2. Topological sort: [-3, -2, -1]
3. Publish -3 → #100. Remap -3→100 in seed_links + parent_id.
4. Re-load -2 (ParentId unchanged, no children of -3). Publish -2 → #101.
   Remap -2→101. seed_links: -2→-3 now 101→100 → AddLinkAsync.
   RemapParentId: any seed with parent_id=-2 now has parent_id=101.
5. Re-load -1 (ParentId now =101 from step 4's remap). Publish -1 → #102.
   CreateAsync sends parent=101 (correct!). Remap -1→102.
```

### API Contracts

#### IAdoWorkItemService (additions)

```csharp
// New method
Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);
```

#### ISeedLinkRepository (additions)

```csharp
// New method
Task RemapIdAsync(int oldId, int newId, CancellationToken ct = default);
```

#### IWorkItemRepository (additions)

```csharp
// New method — bulk-updates parent_id for all seeds referencing the old parent
Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default);
```

#### IPublishIdMapRepository (new interface)

```csharp
// src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs
public interface IPublishIdMapRepository
{
    Task RecordMappingAsync(int oldId, int newId, CancellationToken ct = default);
    Task<int?> GetNewIdAsync(int oldId, CancellationToken ct = default);
    Task<IReadOnlyList<(int OldId, int NewId)>> GetAllMappingsAsync(CancellationToken ct = default);
}
```

#### WorkItem (additions)

```csharp
// New copy methods on WorkItem
public WorkItem WithParentId(int? newParentId);
public WorkItem WithIsSeed(bool isSeed);
```

#### IOutputFormatter (additions)

```csharp
// New methods for validate and publish output
string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results);
string FormatSeedPublishResult(SeedPublishResult result);
string FormatSeedPublishBatchResult(SeedPublishBatchResult result);
string FormatSeedReconcileResult(SeedReconcileResult result);
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Publish rules in `.twig/seed-rules.json`, not in TwigConfiguration | Separation of concerns — rules are project-specific publishing policy, not user config. Avoids bloating the config schema. |
| DD-02 | All populated fields sent on create (per user confirmation) | Users expect the ADO item to match what they drafted. Current `MapSeedToCreatePayload` only sends Title/Area/Iteration, which loses description, effort, tags, etc. |
| DD-03 | Sequential publish for --all, no parallelism | ADO rate limits (429 responses) would complicate parallel publishing. Sequential is simpler and sufficient for typical seed counts (<50). |
| DD-04 | `IsSeed=true` preserved on published items via `WithIsSeed(true)` | Enables provenance tracking — the local cache remembers which items originated as seeds. `FetchAsync` returns `IsSeed=false` since ADO doesn't track this, so `WithIsSeed` creates a copy with the flag set. |
| DD-05 | Parent-child links skipped during link promotion | `MapSeedToCreatePayload` already sets the parent relation during creation. Re-adding via link promotion would duplicate it. |
| DD-06 | BacklogOrderer is best-effort, failure is non-fatal | StackRank interpolation is inherently fragile. A failed ordering attempt should not prevent the item from being published. |
| DD-07 | Topological sort considers `depends-on`, `blocked-by`, `blocks`, `depended-on-by`, and negative `ParentId` edges | All four directional link types imply publish ordering. `blocks` and `depended-on-by` were missing in v2 — since `SeedLinkCommand` stores only single-direction links, the sort must interpret both forward and reverse types. ParentId < 0 is critical because `CreateAsync` sends it directly to ADO. `parent-child` seed_links and `related` links do not imply ordering. |
| DD-08 | Orchestrator in Domain layer (not CLI) | Keeps business logic testable. CLI commands are thin wrappers. Follows existing pattern (`FlowTransitionService`, `RefreshOrchestrator`). |
| DD-09 | Excluded fields for create payload use a shared list | `SeedEditorFormat` already maintains `ExcludedFields`. Extract to a shared constant for reuse in `MapSeedToCreatePayload`. |
| DD-10 | Transactional delete+remap via IUnitOfWork | v2 had a data loss window between delete (step 8) and remap (step 9). Wrapping in a single SQLite transaction ensures atomicity. Remap is done before delete within the transaction so even a partial commit can't orphan links. |
| DD-11 | Publish ID map (`publish_id_map` table) for reconcile repair | v2's reconcile had no discovery mechanism for stale references (once the old seed row was deleted, the old→new mapping was lost). A persistent map enables reconcile to repair rather than just delete. |

---

## Alternatives Considered

### Rules in TwigConfiguration vs. Separate File

**Alternative**: Store publish rules inside `.twig/config` under a `seed.publishRules` section.

**Pros**: Single config file, no new file format.
**Cons**: Bloats the main config. Rules are a project-level policy concern, not a user preference. Separate file allows easy sharing/templating.

**Decision**: Separate `.twig/seed-rules.json` file.

### Orchestrator in CLI vs. Domain Layer

**Alternative**: Put the publish pipeline directly in `SeedPublishCommand`.

**Pros**: Simpler, fewer files.
**Cons**: Untestable without full CLI harness. Violates the existing pattern where domain services hold business logic (see `FlowTransitionService`, `RefreshOrchestrator`, `SyncCoordinator`).

**Decision**: Domain-layer orchestrator, consistent with existing patterns.

### Parallel vs. Sequential Batch Publish

**Alternative**: Publish seeds in parallel using `Task.WhenAll` for faster throughput.

**Pros**: Faster for large batches.
**Cons**: ADO rate limiting (429), order-dependent link resolution, and error recovery all become significantly harder. Typical seed counts are small (<50).

**Decision**: Sequential publish.

---

## Dependencies

### External Dependencies

| Dependency | Purpose |
|---|---|
| Azure DevOps REST API v7.1 | Work item creation, link addition, field patching |
| System.Text.Json source gen | AOT-compatible serialization for seed-rules.json |
| Spectre.Console | Rich terminal output for validation/publish results |

### Internal Dependencies

| Dependency | Status |
|---|---|
| Plan 1 (Seeds + Subcommands) | Complete |
| Plan 2 (Virtual Links + Chain) | Complete |
| `IAdoWorkItemService.CreateAsync` | Exists |
| `IAdoWorkItemService.FetchAsync` | Exists |
| `IAdoWorkItemService.PatchAsync` | Exists |
| `ISeedLinkRepository` | Exists (needs `RemapIdAsync` addition) |
| `IWorkItemRepository` | Exists (needs `RemapParentIdAsync` addition) |
| `IUnitOfWork` / `SqliteUnitOfWork` | Exists (used for transactional publish) |
| `AdoResponseMapper.MapSeedToCreatePayload` | Exists (needs field expansion) |
| Field definitions in SQLite | Exists |

### Sequencing Constraints

Epics must be implemented in order: Rules → Validate → Publish (Core + ParentId Remap + Transaction) → Link Creation → Link Promotion + Batch → Backlog Ordering → Reconcile → CLI Wiring.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|---|---|
| `WorkItem` | New `WithParentId()` and `WithIsSeed()` copy methods |
| `IAdoWorkItemService` | New `AddLinkAsync` method |
| `AdoRestClient` | Implements `AddLinkAsync` |
| `AdoResponseMapper` | `MapSeedToCreatePayload` expanded to include all fields |
| `ISeedLinkRepository` | New `RemapIdAsync` method |
| `SqliteSeedLinkRepository` | Implements `RemapIdAsync` |
| `IWorkItemRepository` | New `RemapParentIdAsync` method |
| `SqliteWorkItemRepository` | Implements `RemapParentIdAsync` |
| `SqliteCacheStore` | New `publish_id_map` table in schema |
| `IOutputFormatter` | New formatting methods for validate/publish/reconcile |
| `HumanOutputFormatter` | Implements new formatting methods |
| `SpectreRenderer` / `IAsyncRenderer` | New rendering for validate/publish output |
| `TwigJsonContext` | New `[JsonSerializable]` for `SeedPublishRules` |
| `Program.cs` | New command routing for validate/publish/reconcile |
| `CommandRegistrationModule` | DI registration for new commands |
| `CommandServiceModule` | DI registration for orchestrators |

### Backward Compatibility

- **Fully backward compatible**. No existing commands, data formats, or APIs change behavior.
- Seeds created before this plan can be published without modification.
- The `seed_links` table schema is unchanged (`RemapIdAsync` uses UPDATE, not schema migration).
- New `publish_id_map` table requires a schema version bump in `SqliteCacheStore.SchemaVersion` (currently 7 → 8). Existing DBs will be rebuilt on first run (same as existing schema migration pattern).

### Performance Implications

- Each `seed publish` makes 2-4 ADO API calls (create, fetch, optional link add, optional StackRank patch). Batch publish is O(n) API calls.
- `BacklogOrderer` adds 1 `FetchChildrenAsync` + 1 `PatchAsync` per publish -- optional and best-effort.
- No impact on non-publish commands.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| StackRank interpolation collisions | Medium | Low | Best-effort with graceful degradation. If patch fails, log warning and continue. |
| ADO rate limiting during --all publish | Low | Medium | Sequential publishing with no parallelism. Future: add configurable delay between items. |
| Interrupted batch publish leaves partial state | Medium | Medium | Transactional local updates (DD-10) + publish_id_map (DD-11) ensure consistent state. Reconcile command repairs any residual issues. |
| Circular dependencies in seed_links + ParentId | Low | Low | Topological sort detects cycles and refuses to publish with a clear error message. Non-cyclic seeds still publish. |
| Field rejection by ADO (read-only or invalid) | Medium | Low | Filter out known read-only fields using `FieldDefinition.IsReadOnly` and `SeedEditorFormat.ExcludedFields`. Wrap CreateAsync with error handling that reports which field was rejected. |
| Negative ParentId sent to ADO | Medium | High | Single-seed publish guards against ParentId < 0 (step 4). Batch publish includes ParentId in dependency graph so parents always publish first. |
| Schema version bump drops existing cache | Low | Medium | Existing pattern — schema rebuild is expected on version bump. Seeds in `work_items` will be lost, but they are drafts. Users should publish seeds before updating Twig. |

---

## Open Questions

1. **[Low]** Should `seed publish --all` support a `--delay <ms>` option for rate-limit-sensitive organizations? (Can be added later without design changes.)

2. **[Low]** Should the reconcile command also verify that promoted ADO links actually exist (by fetching the work item with relations)? This would add API calls but provide stronger consistency guarantees. (Can be deferred to a follow-up.)

3. **[Low]** Should `seed-rules.json` support per-type rules (e.g., different required fields for User Story vs. Bug)? The current design uses a flat list. Per-type rules could be added as a backward-compatible extension later.

---

## Implementation Phases

### Phase 1: Publish Rules + Validation (Epic 1–2)
**Exit criteria**: `twig seed validate` works against configurable rules with pass/fail output.

### Phase 2: Core Publish Pipeline (Epic 3–4)
**Exit criteria**: `twig seed publish <id>` creates an ADO item with all fields, fetches it back, replaces the local row with transactional safety, remaps seed_link IDs and child ParentIds, and records the publish ID map.

### Phase 3: Link Promotion + Batch (Epic 5–6)
**Exit criteria**: Virtual links are promoted to ADO relations. `--all` publishes in topological order including ParentId edges. Child seeds re-load to pick up remapped ParentId.

### Phase 4: Backlog Ordering + Reconcile (Epic 7–8)
**Exit criteria**: Published items land near siblings in the backlog. `twig seed reconcile` uses publish_id_map to repair stale links and parent references.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/SeedPublishRules.cs` | Publish rules domain model |
| `src/Twig.Domain/ValueObjects/SeedValidationResult.cs` | Validation result model |
| `src/Twig.Domain/ValueObjects/SeedPublishResult.cs` | Publish result models (single + batch) |
| `src/Twig.Domain/ValueObjects/SeedReconcileResult.cs` | Reconcile result model |
| `src/Twig.Domain/Interfaces/ISeedPublishRulesProvider.cs` | Interface for loading publish rules |
| `src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs` | Interface for publish old→new ID mapping |
| `src/Twig.Domain/Services/SeedValidator.cs` | Validation logic |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | Core publish pipeline |
| `src/Twig.Domain/Services/SeedLinkPromoter.cs` | Virtual-to-ADO link promotion |
| `src/Twig.Domain/Services/SeedLinkTypeMapper.cs` | SeedLinkType-to-ADO relation mapping |
| `src/Twig.Domain/Services/BacklogOrderer.cs` | Best-effort backlog positioning |
| `src/Twig.Domain/Services/SeedReconcileOrchestrator.cs` | Link repair/cleanup using publish_id_map |
| `src/Twig.Domain/Services/SeedDependencyGraph.cs` | Topological sort for --all (seed_links + ParentId) |
| `src/Twig.Infrastructure/Config/FileSeedPublishRulesProvider.cs` | Loads seed-rules.json |
| `src/Twig.Infrastructure/Persistence/SqlitePublishIdMapRepository.cs` | SQLite impl of IPublishIdMapRepository |
| `src/Twig/Commands/SeedValidateCommand.cs` | CLI command: validate |
| `src/Twig/Commands/SeedPublishCommand.cs` | CLI command: publish |
| `src/Twig/Commands/SeedReconcileCommand.cs` | CLI command: reconcile |
| `tests/Twig.Domain.Tests/Services/SeedValidatorTests.cs` | Validator unit tests |
| `tests/Twig.Domain.Tests/Services/SeedPublishOrchestratorTests.cs` | Orchestrator tests |
| `tests/Twig.Domain.Tests/Services/SeedDependencyGraphTests.cs` | Topo sort tests (incl. ParentId edges) |
| `tests/Twig.Domain.Tests/Services/SeedLinkTypeMapperTests.cs` | Mapping tests |
| `tests/Twig.Domain.Tests/Services/BacklogOrdererTests.cs` | Ordering tests |
| `tests/Twig.Domain.Tests/Services/SeedReconcileOrchestratorTests.cs` | Reconcile tests |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | WithParentId/WithIsSeed tests |
| `tests/Twig.Infrastructure.Tests/Config/FileSeedPublishRulesProviderTests.cs` | Rules loading tests |
| `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientLinkTests.cs` | AddLinkAsync tests |
| `tests/Twig.Infrastructure.Tests/Persistence/SqlitePublishIdMapRepositoryTests.cs` | Publish ID map tests |
| `tests/Twig.Cli.Tests/Commands/SeedValidateCommandTests.cs` | Validate command tests |
| `tests/Twig.Cli.Tests/Commands/SeedPublishCommandTests.cs` | Publish command tests |
| `tests/Twig.Cli.Tests/Commands/SeedReconcileCommandTests.cs` | Reconcile command tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Add `WithParentId(int?)` and `WithIsSeed(bool)` copy methods |
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add `AddLinkAsync` method |
| `src/Twig.Domain/Interfaces/ISeedLinkRepository.cs` | Add `RemapIdAsync` method |
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Add `RemapParentIdAsync` method |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Implement `AddLinkAsync` |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Expand `MapSeedToCreatePayload` to include all populated fields |
| `src/Twig.Infrastructure/Persistence/SqliteSeedLinkRepository.cs` | Implement `RemapIdAsync` |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Implement `RemapParentIdAsync` |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Add `publish_id_map` table to schema, bump `SchemaVersion` to 8 |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable(typeof(SeedPublishRules))]` |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add validation/publish/reconcile formatting methods |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement new formatting methods |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement new formatting methods |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement new formatting methods |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Add seed validation/publish rendering |
| `src/Twig/Rendering/SpectreRenderer.cs` | Implement new rendering methods |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register new commands |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register orchestrators |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `ISeedPublishRulesProvider`, `IPublishIdMapRepository` |
| `src/Twig/Program.cs` | Add command routing for validate/publish/reconcile |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### Epic 1: Publish Rules Configuration

**Goal**: Define the publish rules domain model, interface, and infrastructure provider so seeds can be validated against configurable rules.

**Status**: DONE ✓ (Completed 2026-03-24)

**Prerequisites**: None.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `SeedPublishRules` domain model with `RequiredFields` and `RequireParent` properties, plus `Default` static property | `src/Twig.Domain/ValueObjects/SeedPublishRules.cs` | DONE |
| E1-T2 | IMPL | Create `ISeedPublishRulesProvider` interface in Domain | `src/Twig.Domain/Interfaces/ISeedPublishRulesProvider.cs` | DONE |
| E1-T3 | IMPL | Create `FileSeedPublishRulesProvider` that loads `.twig/seed-rules.json` using source-gen JSON, falls back to defaults | `src/Twig.Infrastructure/Config/FileSeedPublishRulesProvider.cs` | DONE |
| E1-T4 | IMPL | Add `[JsonSerializable(typeof(SeedPublishRules))]` to `TwigJsonContext` | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| E1-T5 | IMPL | Register `ISeedPublishRulesProvider` in `TwigServiceRegistration.AddTwigCoreServices()` | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE |
| E1-T6 | TEST | Unit tests for `SeedPublishRules.Default`, `FileSeedPublishRulesProvider` (file exists, file missing, malformed JSON) | `tests/Twig.Infrastructure.Tests/Config/FileSeedPublishRulesProviderTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `SeedPublishRules.Default` returns title-only required field
- [x] `FileSeedPublishRulesProvider` loads from JSON when file exists
- [x] `FileSeedPublishRulesProvider` returns defaults when file missing
- [x] `FileSeedPublishRulesProvider` throws `TwigConfigurationException` on bad JSON
- [x] `SeedPublishRules` is registered in `TwigJsonContext` for AOT

---

### Epic 2: Seed Validator + Validate Command

**Goal**: Implement validation logic and the `twig seed validate` command.

**Prerequisites**: Epic 1.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Create `SeedValidationResult` and `SeedValidationFailure` value objects | `src/Twig.Domain/ValueObjects/SeedValidationResult.cs` | TO DO |
| E2-T2 | IMPL | Create `SeedValidator` static class with `Validate(WorkItem, SeedPublishRules)` method | `src/Twig.Domain/Services/SeedValidator.cs` | TO DO |
| E2-T3 | IMPL | Add `FormatSeedValidation` to `IOutputFormatter` and implement in `HumanOutputFormatter`, `JsonOutputFormatter`, `MinimalOutputFormatter` | formatters | TO DO |
| E2-T4 | IMPL | Create `SeedValidateCommand` with single-seed and all-seeds modes | `src/Twig/Commands/SeedValidateCommand.cs` | TO DO |
| E2-T5 | IMPL | Register `SeedValidateCommand` in DI and add routing in `Program.cs` | DI + Program.cs | TO DO |
| E2-T6 | TEST | Unit tests for `SeedValidator` | `tests/Twig.Domain.Tests/Services/SeedValidatorTests.cs` | TO DO |
| E2-T7 | TEST | CLI tests for `SeedValidateCommand` | `tests/Twig.Cli.Tests/Commands/SeedValidateCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `SeedValidator.Validate` returns pass for seed with all required fields
- [ ] `SeedValidator.Validate` returns failure with specific rule/message for missing fields
- [ ] `twig seed validate` with ID shows detailed per-rule results
- [ ] `twig seed validate` without ID shows summary for all seeds
- [ ] Exit code 0 when all pass, non-zero when any fail

---

### Epic 3: Core Publish Pipeline

**Goal**: Implement the single-seed publish flow: validate → create in ADO → fetch back → transactional local update (remap IDs, remap ParentId, delete+save) → publish ID map.

**Prerequisites**: Epic 2.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Create `SeedPublishResult` and `SeedPublishBatchResult` value objects | `src/Twig.Domain/ValueObjects/SeedPublishResult.cs` | TO DO |
| E3-T2 | IMPL | Add `WithParentId(int?)` and `WithIsSeed(bool)` copy methods to `WorkItem` | `src/Twig.Domain/Aggregates/WorkItem.cs` | TO DO |
| E3-T3 | IMPL | Expand `AdoResponseMapper.MapSeedToCreatePayload` to include all populated non-readonly fields from `seed.Fields` | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | TO DO |
| E3-T4 | IMPL | Add `RemapIdAsync(int oldId, int newId)` to `ISeedLinkRepository` | `src/Twig.Domain/Interfaces/ISeedLinkRepository.cs` | TO DO |
| E3-T5 | IMPL | Implement `RemapIdAsync` in `SqliteSeedLinkRepository` | `src/Twig.Infrastructure/Persistence/SqliteSeedLinkRepository.cs` | TO DO |
| E3-T6 | IMPL | Add `RemapParentIdAsync(int oldParentId, int newParentId)` to `IWorkItemRepository` | `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | TO DO |
| E3-T7 | IMPL | Implement `RemapParentIdAsync` in `SqliteWorkItemRepository` | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | TO DO |
| E3-T8 | IMPL | Create `IPublishIdMapRepository` interface and `SqlitePublishIdMapRepository` implementation | `src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs`, `src/Twig.Infrastructure/Persistence/SqlitePublishIdMapRepository.cs` | TO DO |
| E3-T9 | IMPL | Add `publish_id_map` table to `SqliteCacheStore` schema, bump `SchemaVersion` to 8 | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | TO DO |
| E3-T10 | IMPL | Create `SeedPublishOrchestrator` with `PublishAsync(seedId, force, dryRun)` using `IUnitOfWork` for transactional local update | `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | TO DO |
| E3-T11 | TEST | Unit tests for `WithParentId` and `WithIsSeed` | `tests/Twig.Domain.Tests/Aggregates/WorkItemCopyTests.cs` | TO DO |
| E3-T12 | TEST | Unit tests for expanded `MapSeedToCreatePayload` | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | TO DO |
| E3-T13 | TEST | Unit tests for `RemapIdAsync` and `RemapParentIdAsync` | `tests/Twig.Infrastructure.Tests/Persistence/` | TO DO |
| E3-T14 | TEST | Unit tests for `SqlitePublishIdMapRepository` | `tests/Twig.Infrastructure.Tests/Persistence/SqlitePublishIdMapRepositoryTests.cs` | TO DO |
| E3-T15 | TEST | Unit tests for `SeedPublishOrchestrator.PublishAsync` including transactional flow and ParentId guard | `tests/Twig.Domain.Tests/Services/SeedPublishOrchestratorTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `CreateAsync` payload includes all non-empty, non-readonly seed fields
- [ ] `RemapIdAsync` updates both source_id and target_id references
- [ ] `RemapParentIdAsync` updates all child seeds' parent_id
- [ ] `WithIsSeed(true)` correctly preserves seed provenance on fetched-back items
- [ ] `WithParentId` correctly creates copy with new parent reference
- [ ] Orchestrator creates ADO item, fetches it back, replaces local row
- [ ] Local operations (remap + delete + save) are wrapped in a single transaction
- [ ] Publish ID map records the old→new mapping
- [ ] Single-seed publish guards against ParentId < 0 with clear error
- [ ] Already-published seed (positive ID) is skipped with success
- [ ] `--force` bypasses validation
- [ ] `--dry-run` returns plan without API calls

---

### Epic 4: ADO Link Creation

**Goal**: Add the ability to create links in ADO via the REST API.

**Prerequisites**: None (can parallel with Epic 3).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Create `SeedLinkTypeMapper` mapping SeedLinkTypes constants to ADO relation type reference names | `src/Twig.Domain/Services/SeedLinkTypeMapper.cs` | TO DO |
| E4-T2 | IMPL | Add `AddLinkAsync(int sourceId, int targetId, string adoLinkType)` to `IAdoWorkItemService` | `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | TO DO |
| E4-T3 | IMPL | Implement `AddLinkAsync` in `AdoRestClient` using PATCH with `/relations/-` | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | TO DO |
| E4-T4 | TEST | Unit tests for `SeedLinkTypeMapper` | `tests/Twig.Domain.Tests/Services/SeedLinkTypeMapperTests.cs` | TO DO |
| E4-T5 | TEST | Tests for `AdoRestClient.AddLinkAsync` | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientLinkTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] All SeedLinkTypes map to correct ADO relation reference names
- [ ] `AddLinkAsync` sends correct PATCH with `/relations/-` operation
- [ ] `parent-child` maps to `System.LinkTypes.Hierarchy-Forward`
- [ ] `depends-on` maps to `System.LinkTypes.Dependency-Reverse`
- [ ] `related` maps to `System.LinkTypes.Related`

---

### Epic 5: Link Promotion + Batch Publish

**Goal**: Promote virtual seed links to ADO relations during publish, and implement `--all` with topological sorting that includes ParentId edges and all directional link types.

**Prerequisites**: Epic 3, Epic 4.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | Create `SeedLinkPromoter` service | `src/Twig.Domain/Services/SeedLinkPromoter.cs` | TO DO |
| E5-T2 | IMPL | Create `SeedDependencyGraph` with topological sort and cycle detection. Graph includes: (a) seed_links edges for `depends-on`, `blocked-by`, `blocks`, `depended-on-by`; (b) ParentId < 0 edges (child→parent) | `src/Twig.Domain/Services/SeedDependencyGraph.cs` | TO DO |
| E5-T3 | IMPL | Add `PublishAllAsync(force, dryRun)` to `SeedPublishOrchestrator` — re-loads each seed before publish to pick up remapped ParentId | `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | TO DO |
| E5-T4 | IMPL | Integrate `SeedLinkPromoter` into `PublishAsync` after ID remap step | `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | TO DO |
| E5-T5 | TEST | Unit tests for `SeedLinkPromoter` | `tests/Twig.Domain.Tests/Services/SeedLinkPromoterTests.cs` | TO DO |
| E5-T6 | TEST | Unit tests for `SeedDependencyGraph` — includes ParentId edges, all four directional link types, cycle detection | `tests/Twig.Domain.Tests/Services/SeedDependencyGraphTests.cs` | TO DO |
| E5-T7 | TEST | Integration test for `PublishAllAsync` — verifies parent-child publish order with ParentId remapping | `tests/Twig.Domain.Tests/Services/SeedPublishOrchestratorTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] Link promoter calls `AddLinkAsync` only when both endpoints have positive IDs
- [ ] Parent-child links are skipped (already set at creation)
- [ ] Topological sort produces correct publish order for dependency chains
- [ ] Topological sort includes `blocks` and `depended-on-by` edges (not just `depends-on` and `blocked-by`)
- [ ] Topological sort includes ParentId < 0 edges (child waits for parent)
- [ ] Batch publish re-loads each seed before CreateAsync to get remapped ParentId
- [ ] Circular dependency detected and reported as error
- [ ] Link promotion failure is a warning, not a publish failure

---

### Epic 6: Backlog Ordering

**Goal**: Position newly published items near their parent's siblings in the backlog.

**Prerequisites**: Epic 3.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E6-T1 | IMPL | Create `BacklogOrderer` service | `src/Twig.Domain/Services/BacklogOrderer.cs` | TO DO |
| E6-T2 | IMPL | Integrate `BacklogOrderer.TryOrderAsync` into `SeedPublishOrchestrator.PublishAsync` | `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | TO DO |
| E6-T3 | TEST | Unit tests for `BacklogOrderer` | `tests/Twig.Domain.Tests/Services/BacklogOrdererTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] Detects StackRank (Agile) or BacklogPriority (Scrum/CMMI) dynamically
- [ ] Sets value after last sibling when siblings exist
- [ ] No-op when no parent or no ordering field found
- [ ] Patch failure logged as warning, publish still succeeds

---

### Epic 7: Reconcile Command

**Goal**: Implement the `twig seed reconcile` command to repair orphaned and stale seed_links using the publish_id_map for discovery.

**Prerequisites**: Epic 3.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E7-T1 | IMPL | Create `SeedReconcileResult` value object | `src/Twig.Domain/ValueObjects/SeedReconcileResult.cs` | TO DO |
| E7-T2 | IMPL | Create `SeedReconcileOrchestrator` — uses `IPublishIdMapRepository` to repair stale negative-ID references, deletes orphaned links, fixes stale parent_id values | `src/Twig.Domain/Services/SeedReconcileOrchestrator.cs` | TO DO |
| E7-T3 | IMPL | Add `FormatSeedReconcileResult` to `IOutputFormatter` and implement | formatters | TO DO |
| E7-T4 | IMPL | Create `SeedReconcileCommand` CLI command | `src/Twig/Commands/SeedReconcileCommand.cs` | TO DO |
| E7-T5 | TEST | Unit tests for reconcile orchestrator — stale reference repair, orphan cleanup, parent_id fix | `tests/Twig.Domain.Tests/Services/SeedReconcileOrchestratorTests.cs` | TO DO |
| E7-T6 | TEST | CLI tests for `SeedReconcileCommand` | `tests/Twig.Cli.Tests/Commands/SeedReconcileCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] Orphaned links (endpoint deleted, not in publish_id_map) are removed
- [ ] Stale negative-ID references (found in publish_id_map) are remapped to new positive ID
- [ ] Stale parent_id < 0 values are fixed using publish_id_map
- [ ] Clean system reports "nothing to reconcile"
- [ ] Output shows summary of actions taken

---

### Epic 8: Publish Command + CLI Wiring

**Goal**: Create the `SeedPublishCommand`, wire all new commands into DI and Program.cs, add help text.

**Prerequisites**: Epics 3, 5, 6, 7.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E8-T1 | IMPL | Add `FormatSeedPublishResult` and `FormatSeedPublishBatchResult` to `IOutputFormatter` and implement | formatters | TO DO |
| E8-T2 | IMPL | Create `SeedPublishCommand` with `<id>`, `--all`, `--force`, `--dry-run` | `src/Twig/Commands/SeedPublishCommand.cs` | TO DO |
| E8-T3 | IMPL | Register orchestrators in `CommandServiceModule` | `src/Twig/DependencyInjection/CommandServiceModule.cs` | TO DO |
| E8-T4 | IMPL | Register commands in `CommandRegistrationModule` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | TO DO |
| E8-T5 | IMPL | Add command routing in `Program.cs` with help text | `src/Twig/Program.cs` | TO DO |
| E8-T6 | TEST | CLI integration tests for `SeedPublishCommand` | `tests/Twig.Cli.Tests/Commands/SeedPublishCommandTests.cs` | TO DO |
| E8-T7 | TEST | End-to-end lifecycle test: create → validate → publish → verify (includes parent-child publish order) | `tests/Twig.Cli.Tests/Commands/SeedLifecycleIntegrationTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig seed publish -5` publishes seed and outputs "Published seed #-5 as #N: Title"
- [ ] `twig seed publish -5` with ParentId < 0 errors with "publish parent first" message
- [ ] `twig seed publish --all` publishes in topological order (parents before children)
- [ ] `twig seed publish --dry-run` shows plan without API calls
- [ ] `twig seed publish --force` skips validation
- [ ] Help text updated with new commands
- [ ] All new services registered in DI (including IPublishIdMapRepository)
- [ ] Lifecycle test passes: seed → validate → publish → verify

---

## References

- [ADO REST API: Work Items - Update (Add Link)](https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1) -- JSON patch format for adding relations
- [ADO Link Type Reference](https://learn.microsoft.com/azure/devops/boards/queries/link-type-reference?view=azure-devops) -- System.LinkTypes.Related, Hierarchy-Forward/Reverse, Dependency-Forward/Reverse
- [ADO REST API: Work Items - Create](https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/create?view=azure-devops-rest-7.1) -- JSON patch format for work item creation
- Plan 1: `docs/projects/twig-seed-local.plan.md` (Seeds + Subcommands)
- Plan 2: `docs/projects/twig-seed-links.plan.md` (Virtual Links + Chain Builder)
