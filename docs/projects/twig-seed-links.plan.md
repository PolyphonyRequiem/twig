# Twig Virtual Links + Chain Builder + Navigation

**Plan:** 2 of 3 — Virtual Links + Chain Builder + Navigation  
**Status:** Complete  
**Revision:** 1 — Initial draft.

---

## Executive Summary

This plan extends the Twig seed subsystem (established in Plan 1) with three capabilities: virtual links between seeds and existing ADO work items, an interactive chain builder for rapid sequential seed creation, and next/prev sibling navigation for stepping through work item sequences. Virtual links are stored in a new `seed_links` SQLite table as local-only typed directional relationships (parent-child, blocks/blocked-by, depends-on/depended-on-by, related) between any combination of seeds (negative IDs) and real ADO items (positive IDs). The chain builder (`twig seed chain`) provides a tight brainstorming loop — enter titles in sequence, get linked seeds in seconds. Navigation (`twig next` / `twig prev`) adds sibling traversal to the existing up/down tree navigation. All three features are designed for Plan 3 compatibility: virtual links carry sufficient metadata for ADO link materialization at publish time.

---

## Background

### Current Architecture (Post–Plan 1)

Plan 1 transformed seeds into local-first SQLite drafts. The current system provides:

| Component | File | Role |
|-----------|------|------|
| `SeedNewCommand` | `src/Twig/Commands/SeedNewCommand.cs` | Creates seeds with negative IDs, no ADO push. `--editor` flag for field population. |
| `SeedViewCommand` | `src/Twig/Commands/SeedViewCommand.cs` | Dashboard grouped by parent with age, completeness, stale warnings. |
| `SeedEditCommand` | `src/Twig/Commands/SeedEditCommand.cs` | Opens seeds in editor via `IEditorLauncher` + `SeedEditorFormat`. |
| `SeedDiscardCommand` | `src/Twig/Commands/SeedDiscardCommand.cs` | Deletes seeds with confirmation via `IConsoleInput`. |
| `WorkItem` | `src/Twig.Domain/Aggregates/WorkItem.cs` | `CreateSeed()`, `WithSeedFields()`, `InitializeSeedCounter()`. `IsSeed=true`, negative IDs, `SeedCreatedAt`. |
| `SeedFactory` | `src/Twig.Domain/Services/SeedFactory.cs` | Validates parent/child rules via `ProcessConfiguration`, infers child type. |
| `SeedEditorFormat` | `src/Twig.Domain/Services/SeedEditorFormat.cs` | Generates/parses section-header format for editor. |
| `SeedViewGroup` | `src/Twig.Domain/ReadModels/SeedViewGroup.cs` | Record: `(WorkItem? Parent, IReadOnlyList<WorkItem> Seeds)`. |
| `NavigationCommands` | `src/Twig/Commands/NavigationCommands.cs` | `twig up` / `twig down` via tree navigation + `SetCommand` delegation. |
| `ActiveItemResolver` | `src/Twig.Domain/Services/ActiveItemResolver.cs` | Resolves active item from `IContextStore` → cache → ADO auto-fetch. |
| `IContextStore` | `src/Twig.Domain/Interfaces/IContextStore.cs` | `GetActiveWorkItemIdAsync()`, `SetActiveWorkItemIdAsync()` backed by SQLite `context` table. |
| `WorkItemLink` | `src/Twig.Domain/ValueObjects/WorkItemLink.cs` | Existing value object: `(SourceId, TargetId, LinkType)`. Used for ADO link caching. |
| `IWorkItemLinkRepository` | `src/Twig.Domain/Interfaces/IWorkItemLinkRepository.cs` | `GetLinksAsync(workItemId)`, `SaveLinksAsync(workItemId, links)`. For ADO-synced links. |
| `SqliteCacheStore` | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Schema v6. DDL defines `work_item_links` table (source_id, target_id, link_type). |
| `LinkTypes` | `src/Twig.Domain/ValueObjects/WorkItemLink.cs` | Constants: `Related`, `Predecessor`, `Successor`. |

### Command Routing Pattern

`TwigCommands` in `Program.cs` routes all CLI commands. Subcommands use `[Command("x y")]` attributes:
- `seed new`, `seed edit`, `seed discard`, `seed view` are established patterns (lines 300–317).
- Top-level navigation: `Up()` and `Down()` delegate to `NavigationCommands` (lines 288–293).
- The bare `Seed()` method (line 296) remains as a backward-compat alias for `seed new`.

### Active Context Mechanism

The active item is tracked via `IContextStore.SetActiveWorkItemIdAsync(int id)`, which writes to the `context` SQLite table with key `active_work_item_id`. `SetCommand.ExecuteAsync()` is the canonical way to change active context — it resolves the item, hydrates the parent chain, triggers working-set sync, and writes prompt state. Navigation commands (`up`, `down`) delegate to `SetCommand` after resolving the target ID.

### Existing Link Infrastructure

Two separate link systems exist:
1. **`work_item_links` table** — stores ADO-synced non-hierarchy links (Related, Predecessor, Successor). Managed by `IWorkItemLinkRepository` / `SqliteWorkItemLinkRepository`. Populated during `SyncCoordinator.SyncWorkingSetAsync()`.
2. **Parent-child hierarchy** — stored as `parent_id` column on `work_items`. Not a link table — it's a direct foreign-key-style relationship.

The `work_item_links` table is **not suitable** for virtual seed links because: (a) it uses `SaveLinksAsync(workItemId, links)` which replaces all links for an item — destructive for mixed seed/ADO link sets; (b) it has no `created_at` for audit/ordering; (c) seed links need to survive refresh while `work_item_links` is overwritten by ADO sync.

### Seed Protection During Refresh

Seeds and their data survive refresh because:
1. `RefreshCommand` only fetches positive IDs from ADO.
2. `EvictExceptAsync` preserves working set items, and seeds are always in the working set via `GetSeedsAsync()`.
3. The new `seed_links` table (separate from `work_item_links`) will not be touched by refresh logic.

---

## Problem Statement

1. **Seeds exist in isolation.** After Plan 1, seeds know their parent (`ParentId`) but cannot express any other relationship. A seed cannot block another seed, depend on an existing ADO item, or be related to another seed. Users planning interconnected work must hold these relationships in their heads.

2. **Creating multiple related seeds is slow.** Each `twig seed new` invocation requires typing the full command, waiting for output, and manually noting the assigned negative ID. Creating a sequence of 5–10 related tasks requires 5–10 separate invocations with no way to link them sequentially.

3. **No sibling navigation.** `twig up` and `twig down` navigate vertically (parent ↔ child), but there is no horizontal navigation between siblings under the same parent. Users working through a list of tasks must `twig set <id>` each item individually.

4. **The seed dashboard lacks relationship context.** `twig seed view` groups seeds by parent but shows no other relationships — no blocks, dependencies, or cross-references.

---

## Goals and Non-Goals

### Goals

1. **G1:** Store typed, directional virtual links between any pair of seeds and/or existing ADO items in SQLite, queryable by either endpoint.
2. **G2:** Provide CLI subcommands to create, remove, and list virtual links (`seed link`, `seed unlink`, `seed links`).
3. **G3:** Enable rapid sequential seed creation via an interactive chain builder (`seed chain`) that auto-links seeds.
4. **G4:** Add sibling navigation (`twig next`, `twig prev`) that moves through children of the current parent.
5. **G5:** Display virtual links in the seed view dashboard for both SpectreRenderer and HumanOutputFormatter paths.
6. **G6:** Design the `SeedLink` model to carry sufficient information for Plan 3's ADO link materialization.

### Non-Goals

- **NG1:** Publishing seeds to ADO (Plan 3).
- **NG2:** Converting virtual links to real ADO links (Plan 3).
- **NG3:** Link validation rules (e.g., "must have description before publish") (Plan 3).
- **NG4:** Seed rules, `twig seed validate`, `twig seed reconcile`, `twig seed publish` (Plan 3).
- **NG5:** Backlog ordering on publish (Plan 3).
- **NG6:** Modifying the existing `work_item_links` table or `IWorkItemLinkRepository` — these remain for ADO-synced links.
- **NG7:** TUI integration for new commands (out of scope; TUI is a separate binary).

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | A `seed_links` table stores `(source_id, target_id, link_type, created_at)` with composite PK `(source_id, target_id, link_type)`. |
| FR-02 | Link types: `parent-child`, `blocks`, `blocked-by`, `depends-on`, `depended-on-by`, `related`. Extensible via string column. |
| FR-03 | `twig seed link <sourceId> <targetId> [--type <linkType>]` creates a virtual link. Default type is `related`. |
| FR-04 | `twig seed unlink <sourceId> <targetId> [--type <linkType>]` removes a virtual link. |
| FR-05 | `twig seed links [<id>]` lists all virtual links, or links for a specific item (seed or real). |
| FR-06 | `twig seed chain [--parent <id>] [--type <workItemType>]` starts an interactive loop: prompt title → create seed → link to previous → repeat until empty input. |
| FR-07 | Chain builder displays summary on completion: "Created N seeds: -1 → -2 → -3". |
| FR-08 | `twig next` moves to the next sibling under the current parent (sorted by ID). |
| FR-09 | `twig prev` moves to the previous sibling under the current parent (sorted by ID). |
| FR-10 | Next/prev at boundary: show message "Already at first/last sibling" (no wrap-around). |
| FR-11 | Seed view dashboard shows virtual links per seed (e.g., "→ blocks -2", "→ depends on #12345"). |
| FR-12 | Discarding a seed cascades: delete associated `seed_links` rows where the discarded ID is source or target. |
| FR-13 | `seed_links` survives `twig refresh` — refresh does not touch the `seed_links` table. |
| FR-14 | At least one ID in a seed link must be a seed (negative). Pure positive-to-positive links belong in `work_item_links`. |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Native AOT compatible — no reflection. Source-gen JSON where needed. |
| NFR-02 | Process-agnostic — link types do not assume any particular ADO process template. |
| NFR-03 | Chain builder works in non-TTY mode by reading lines from stdin. |
| NFR-04 | Schema change requires `SqliteCacheStore.SchemaVersion` bump (v6 → v7). |
| NFR-05 | Both SpectreRenderer and HumanOutputFormatter render virtual links consistently. |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                          CLI Layer (Twig)                           │
│                                                                     │
│  TwigCommands (Program.cs)                                          │
│    ├── SeedLink()        [Command("seed link")]                    │
│    ├── SeedUnlink()      [Command("seed unlink")]                  │
│    ├── SeedLinks()       [Command("seed links")]                   │
│    ├── SeedChain()       [Command("seed chain")]                   │
│    ├── Next()            top-level command                          │
│    └── Prev()            top-level command                          │
│                                                                     │
│  Commands/                                                          │
│    ├── SeedLinkCommand.cs        ── link/unlink/list logic         │
│    ├── SeedChainCommand.cs       ── interactive chain builder      │
│    └── NavigationCommands.cs     ── extended with NextAsync/PrevAsync│
│                                                                     │
│  Formatters/                                                        │
│    ├── IOutputFormatter.cs       ── +FormatSeedLinks()             │
│    ├── HumanOutputFormatter.cs   ── virtual link rendering         │
│    └── JsonOutputFormatter.cs    ── virtual link rendering         │
│                                                                     │
│  Rendering/                                                         │
│    └── SpectreRenderer.cs        ── seed view + link display       │
├─────────────────────────────────────────────────────────────────────┤
│                        Domain Layer (Twig.Domain)                   │
│                                                                     │
│  ValueObjects/                                                      │
│    ├── SeedLink.cs               ── (SourceId, TargetId, LinkType, │
│    │                                  CreatedAt)                    │
│    └── SeedLinkType.cs           ── static string constants        │
│                                                                     │
│  ReadModels/                                                        │
│    └── SeedViewGroup.cs          ── extended with Links per seed   │
│                                                                     │
│  Interfaces/                                                        │
│    └── ISeedLinkRepository.cs    ── CRUD for seed_links            │
├─────────────────────────────────────────────────────────────────────┤
│                   Infrastructure Layer (Twig.Infrastructure)        │
│                                                                     │
│  Persistence/                                                       │
│    ├── SqliteSeedLinkRepository.cs ── ISeedLinkRepository impl     │
│    └── SqliteCacheStore.cs         ── schema v7 + seed_links DDL   │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. SeedLink Value Object

```csharp
// src/Twig.Domain/ValueObjects/SeedLink.cs
namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a virtual link between two items
/// (seeds and/or existing ADO work items). At least one ID must be negative (a seed).
/// </summary>
public readonly record struct SeedLink(
    int SourceId,
    int TargetId,
    string LinkType,
    DateTimeOffset CreatedAt);

/// <summary>
/// Constants for seed link types. String-based for extensibility.
/// </summary>
public static class SeedLinkTypes
{
    public const string ParentChild = "parent-child";
    public const string Blocks = "blocks";
    public const string BlockedBy = "blocked-by";
    public const string DependsOn = "depends-on";
    public const string DependedOnBy = "depended-on-by";
    public const string Related = "related";

    /// <summary>
    /// All known link types, used for validation and help text.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ParentChild, Blocks, BlockedBy, DependsOn, DependedOnBy, Related
    };

    /// <summary>
    /// Returns the reverse link type for directional pairs, or null if
    /// the type is symmetric (related) or has no reverse.
    /// Used during publish to create both sides of a link in ADO.
    /// </summary>
    public static string? GetReverse(string linkType) => linkType switch
    {
        Blocks => BlockedBy,
        BlockedBy => Blocks,
        DependsOn => DependedOnBy,
        DependedOnBy => DependsOn,
        ParentChild => null, // hierarchy, not a symmetric link
        Related => null,     // symmetric
        _ => null,
    };
}
```

**Design rationale:** Separate from `WorkItemLink` / `LinkTypes` to avoid conflating ADO-synced links with local virtual links. The `CreatedAt` timestamp supports audit and ordering. `GetReverse()` provides Plan 3 with the inverse mapping needed when materializing directional links in ADO.

#### 2. ISeedLinkRepository

```csharp
// src/Twig.Domain/Interfaces/ISeedLinkRepository.cs
namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for virtual seed links stored in SQLite.
/// Separate from IWorkItemLinkRepository (ADO-synced links).
/// </summary>
public interface ISeedLinkRepository
{
    Task AddLinkAsync(SeedLink link, CancellationToken ct = default);
    Task RemoveLinkAsync(int sourceId, int targetId, string linkType, CancellationToken ct = default);
    Task<IReadOnlyList<SeedLink>> GetLinksForItemAsync(int itemId, CancellationToken ct = default);
    Task<IReadOnlyList<SeedLink>> GetAllSeedLinksAsync(CancellationToken ct = default);
    Task DeleteLinksForItemAsync(int itemId, CancellationToken ct = default);
}
```

**Design rationale:** A separate interface rather than extending `IWorkItemRepository` because: (a) single responsibility — work item persistence is already a large interface; (b) the `seed_links` table is structurally different from `work_items`; (c) Plan 3 may need to inject `ISeedLinkRepository` independently for publish orchestration.

#### 3. SqliteSeedLinkRepository

Implements `ISeedLinkRepository` using the `seed_links` table. All queries use parameterized SQL. `GetLinksForItemAsync` queries both `source_id = @id OR target_id = @id` to return all links where the item participates as either endpoint.

`DeleteLinksForItemAsync` removes all rows where `source_id = @id OR target_id = @id` — used by `SeedDiscardCommand` for cascade cleanup.

#### 4. SeedLinkCommand

A single command class handling three subcommands (`seed link`, `seed unlink`, `seed links`):

```csharp
// src/Twig/Commands/SeedLinkCommand.cs
public sealed class SeedLinkCommand(
    ISeedLinkRepository seedLinkRepo,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Create a virtual link between two items.</summary>
    public async Task<int> LinkAsync(int sourceId, int targetId, string? type, string outputFormat, CancellationToken ct);

    /// <summary>Remove a virtual link.</summary>
    public async Task<int> UnlinkAsync(int sourceId, int targetId, string? type, string outputFormat, CancellationToken ct);

    /// <summary>List virtual links for an item, or all links.</summary>
    public async Task<int> ListLinksAsync(int? id, string outputFormat, CancellationToken ct);
}
```

**Validation logic for `LinkAsync`:**
1. At least one of `sourceId`, `targetId` must be negative (a seed). Reject pure positive-to-positive links with error message directing user to ADO.
2. Validate that the specified link type is in `SeedLinkTypes.All` (case-insensitive comparison). If not, show error with available types.
3. Check that both IDs exist in the cache (`workItemRepo.ExistsByIdAsync`). For positive IDs that aren't cached, allow the link anyway (the ADO item might not be fetched yet) — warn but don't block.
4. Prevent duplicate links (the PK constraint will enforce this at the DB level; catch and report gracefully).

#### 5. SeedChainCommand

```csharp
// src/Twig/Commands/SeedChainCommand.cs
public sealed class SeedChainCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine)
{
    public async Task<int> ExecuteAsync(
        int? parentOverride,
        string? type,
        string outputFormat,
        CancellationToken ct);
}
```

**Interactive loop:**
1. Resolve parent context: use `--parent <id>` if provided, otherwise `ActiveItemResolver.GetActiveItemAsync()`.
2. Determine work item type: use `--type` if provided, otherwise infer from parent via `SeedFactory` logic.
3. Initialize seed counter from DB.
4. Loop:
   a. Write prompt: `"Seed title (empty to finish): "` to stdout.
   b. Read line via `consoleInput.ReadLine()`.
   c. If null or empty, break.
   d. Create seed via `SeedFactory.Create()`.
   e. Persist via `workItemRepo.SaveAsync()`.
   f. If not the first seed in chain, create virtual link from previous seed to this seed (type: `related` — chains are sequences, not strict parent-child).
   g. Print confirmation: `"  #{id} {title}"`.
   h. Record seed ID for next iteration.
5. Print summary: `"Created N seeds: #{id1} → #{id2} → #{id3}"`.
6. Return 0.

**Non-TTY behavior:** When `consoleInput.IsOutputRedirected` is true (piped input), the prompt is suppressed and lines are read directly. `ReadLine()` returns `null` at EOF, naturally terminating the loop.

#### 6. NavigationCommands Extension (Next/Prev)

Extend the existing `NavigationCommands` class with `NextAsync` and `PrevAsync`:

```csharp
// Added to src/Twig/Commands/NavigationCommands.cs
/// <summary>Navigate to the next sibling work item.</summary>
public async Task<int> NextAsync(string outputFormat, CancellationToken ct)
{
    // 1. Get active item via contextStore
    // 2. If no parent, error: "Cannot navigate siblings — item has no parent."
    // 3. Get children of parent via workItemRepo.GetChildrenAsync(parentId)
    //    (includes both real items and seeds — they're all in work_items table)
    // 4. Sort by ID (ascending) — consistent ordering
    // 5. Find current item's index in the list
    // 6. If at last index, message: "Already at last sibling."
    // 7. Otherwise, delegate to setCommand.ExecuteAsync(nextId)
}

/// <summary>Navigate to the previous sibling work item.</summary>
public async Task<int> PrevAsync(string outputFormat, CancellationToken ct)
{
    // Same as NextAsync but step -1 instead of +1
}
```

**Design decision — sorting by ID:** `GetChildrenAsync` already returns items sorted by `type, title` (see `SqliteWorkItemRepository` line 39). For next/prev navigation, we re-sort by ID because: (a) ID order is stable and deterministic; (b) ADO backlog order is not available locally; (c) seeds interleave naturally (negative IDs sort before positive IDs, but within seeds, newer seeds have more negative IDs). This is documented as a simplification — Plan 3 may refine ordering when backlog position data is available.

#### 7. SeedViewGroup Enhancement

The current `SeedViewGroup` carries `(Parent, Seeds)`. To display virtual links, the command layer needs links per seed. Rather than modifying the domain read model, the `SeedViewCommand` will fetch links alongside seeds and pass them to the formatter:

**Option chosen:** Add an optional `Links` dictionary parameter to the formatter/renderer methods. The `SeedViewCommand` fetches links for all seeds in bulk, then passes a `Dictionary<int, IReadOnlyList<SeedLink>>` alongside the groups.

This avoids modifying `SeedViewGroup` (which is a clean domain read model) and keeps the link-fetching responsibility in the command layer.

#### 8. Schema Changes

Bump `SqliteCacheStore.SchemaVersion` from 6 to 7. Add to `DropAllTables`:

```csharp
string[] tables = [..., "seed_links"];
```

Add to DDL:

```sql
CREATE TABLE seed_links (
    source_id INTEGER NOT NULL,
    target_id INTEGER NOT NULL,
    link_type TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (source_id, target_id, link_type)
);
CREATE INDEX idx_seed_links_source ON seed_links(source_id);
CREATE INDEX idx_seed_links_target ON seed_links(target_id);
```

**Schema version bump impact:** Existing databases will be rebuilt (all tables dropped and recreated). This is acceptable because: (a) Twig caches are reconstructable via `twig refresh`; (b) seeds with negative IDs will be lost on schema upgrade — this should be documented in release notes with guidance to publish or note seed data before upgrading.

### Data Flow

#### Create Virtual Link (`twig seed link -1 -2 --type blocks`)

```
User → TwigCommands.SeedLink() → SeedLinkCommand.LinkAsync()
  │
  ├── Validate: at least one negative ID ✓
  ├── Validate: link type "blocks" ∈ SeedLinkTypes.All ✓
  ├── Optionally validate IDs exist in cache (warn if not)
  ├── Create SeedLink(-1, -2, "blocks", DateTimeOffset.UtcNow)
  └── seedLinkRepo.AddLinkAsync(link) → INSERT INTO seed_links
```

#### Chain Builder (`twig seed chain`)

```
User → TwigCommands.SeedChain() → SeedChainCommand.ExecuteAsync()
  │
  ├── Resolve parent context (#100 Feature X)
  ├── Infer child type: Task
  ├── Initialize seed counter
  │
  ├── Prompt: "Seed title (empty to finish): "
  │   User: "Set up database schema"
  │   → SeedFactory.Create("Set up database schema", parent, config)
  │   → workItemRepo.SaveAsync(seed #-1)
  │   → Print: "  #-1 Set up database schema"
  │
  ├── Prompt: "Seed title (empty to finish): "
  │   User: "Write migration scripts"
  │   → SeedFactory.Create("Write migration scripts", parent, config)
  │   → workItemRepo.SaveAsync(seed #-2)
  │   → seedLinkRepo.AddLinkAsync(SeedLink(-1, -2, "related", now))
  │   → Print: "  #-2 Write migration scripts"
  │
  ├── Prompt: "Seed title (empty to finish): "
  │   User: "" (empty)
  │   → Break
  │
  └── Print: "Created 2 seeds: #-1 → #-2"
```

#### Sibling Navigation (`twig next`)

```
User → TwigCommands.Next() → NavigationCommands.NextAsync()
  │
  ├── contextStore.GetActiveWorkItemIdAsync() → #102 (Task under Feature #100)
  ├── workItemRepo.GetByIdAsync(102) → WorkItem { ParentId = 100 }
  ├── workItemRepo.GetChildrenAsync(100) → [#101, #102, #103, #-1, #-2]
  ├── Sort by ID → [#-2, #-1, #101, #102, #103]
  ├── Find index of #102 → 3
  ├── Next index → 4 → #103
  └── setCommand.ExecuteAsync("103") → sets context, prints item
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Separate `seed_links` table** from `work_item_links` | ADO-synced links are replaced on every refresh; seed links must persist. Different lifecycle, different table. |
| **Separate `ISeedLinkRepository`** from `IWorkItemRepository` | Single responsibility. Seed links are a distinct concern with different query patterns. |
| **String-based link types** | Extensible without code changes. Process-agnostic — no enum tied to ADO process template. |
| **`GetReverse()` on `SeedLinkTypes`** | Plan 3 needs to create both sides of directional links in ADO. Building the mapping now avoids retrofitting. |
| **Cascade delete on discard** | Orphaned links to non-existent seeds would corrupt the link graph. Cascade is simpler than warning. |
| **No wrap-around in next/prev** | Less surprising behavior. Users at the boundary get a clear message rather than jumping to the other end. |
| **Sort siblings by ID for next/prev** | Deterministic, available locally. ADO backlog order is not cached. |
| **Chain links use "related" type** | Chains are brainstorming sequences, not strict dependencies. Users can re-type links later. |
| **Link display via formatter parameters** (not `SeedViewGroup` modification) | Keeps domain read model clean. Links are a display concern fetched at command layer. |

---

## Alternatives Considered

### 1. Extend `work_item_links` Table for Seed Links

**Approach:** Add `created_at` column and `is_virtual` flag to the existing `work_item_links` table. Reuse `IWorkItemLinkRepository`.

**Pros:** No new table, no new repository interface.

**Cons:** (a) `SaveLinksAsync` replaces all links for an item — would destroy virtual links on ADO sync. Requires refactoring the method to be selective, which breaks existing callers. (b) `work_item_links` has no `created_at` — adding it requires schema migration for existing data. (c) Conceptual conflation — ADO-synced links and local virtual links have fundamentally different lifecycles.

**Rejected because** the lifecycle mismatch makes co-storage dangerous. Separate table is cleaner and safer.

### 2. Store Links in `SeedLink` JSON Array on WorkItem

**Approach:** Add a `links_json` column to `work_items` and store links as JSON within each seed row.

**Pros:** No new table. Links travel with the seed.

**Cons:** (a) Querying "all links involving item X" requires scanning all rows and parsing JSON. (b) Bidirectional links must be stored twice (on each endpoint) — consistency risk. (c) Links between a seed and an existing ADO item have no natural home on the ADO item's row.

**Rejected because** relational storage with proper indexing is the right tool for relationship data.

### 3. Wrap-Around for Next/Prev Navigation

**Approach:** When at the last sibling, `twig next` wraps to the first sibling.

**Pros:** No dead ends — always navigates somewhere.

**Cons:** Disorienting. Users may not realize they've wrapped. Can create infinite loops in scripts. The `git log --oneline | head` analogy suggests finite lists.

**Rejected because** explicit boundary messages are less surprising.

---

## Dependencies

### Internal Dependencies

| Dependency | Type | Notes |
|------------|------|-------|
| `SeedNewCommand` (Plan 1) | Prerequisite | Chain builder reuses `SeedFactory.Create()` and `workItemRepo.SaveAsync()` patterns. |
| `IConsoleInput` / `ConsoleInput` | Existing | Chain builder reads lines via `IConsoleInput.ReadLine()`. |
| `ActiveItemResolver` | Existing | Chain builder and navigation resolve parent/active context. |
| `SetCommand` | Existing | Next/prev delegate to `SetCommand.ExecuteAsync()` to change context. |
| `OutputFormatterFactory` | Existing | All new commands use the formatter pipeline. |
| `SqliteCacheStore` | Existing | Schema version bump + DDL extension. |
| `SeedFactory` | Existing | Chain builder creates seeds via factory. |

### External Dependencies

None. All features are local-only (SQLite + CLI). No new NuGet packages required.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `SqliteCacheStore` | Schema v6 → v7. DDL extended with `seed_links` table. `DropAllTables` updated. |
| `SeedDiscardCommand` | Must cascade-delete seed links when discarding a seed. |
| `SeedViewCommand` | Fetches and passes virtual links to formatters/renderer. |
| `HumanOutputFormatter` | New `FormatSeedLinks()` method. `FormatSeedView()` updated to show links. |
| `JsonOutputFormatter` | `FormatSeedView()` updated to include links in JSON output. |
| `MinimalOutputFormatter` | `FormatSeedView()` updated (minimal link display). |
| `SpectreRenderer` | `RenderSeedViewAsync()` updated with link column. |
| `NavigationCommands` | Extended with `NextAsync()` / `PrevAsync()`. |
| `TwigCommands` (Program.cs) | New command routing entries. |
| `CommandRegistrationModule` | DI registration for new commands. |
| `CommandServiceModule` | DI registration for `ISeedLinkRepository`. |
| `TwigServiceRegistration` | DI registration for `SqliteSeedLinkRepository`. |
| `GroupedHelp` | Updated help text with new commands. |
| `HintEngine` | New hints for chain builder and link commands. |

### Backward Compatibility

- **Schema version bump** drops and recreates all tables. Seeds created under v6 will be lost. Mitigated by release notes.
- **No breaking CLI changes.** All new commands are additive. Existing `seed` subcommands unchanged.
- **`work_item_links` table untouched.** Existing ADO link sync continues to work.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema v7 bump destroys user's seeds | Medium | Medium | Document in release notes. Seeds are local drafts — by design they're ephemeral pre-publish. |
| Chain builder breaks under piped input | Low | Medium | `IConsoleInput.ReadLine()` returns null at EOF. Prompt is suppressed when `IsOutputRedirected`. Test with piped input in integration tests. |
| Next/prev ordering feels wrong to users | Medium | Low | ID ordering is deterministic and documented. Can be refined in Plan 3 when backlog order data is available. |
| Virtual links reference discarded seeds | Low | Low | Cascade delete in `SeedDiscardCommand` via `seedLinkRepo.DeleteLinksForItemAsync()`. |
| Large number of seed links degrades performance | Low | Low | SQLite indexed on both `source_id` and `target_id`. Seed link counts will be small (tens, not thousands). |
| `SeedLinkTypes.GetReverse()` mapping becomes stale | Low | Low | Mapping is simple and tested. New link types can be added without reverse if symmetric. |

---

## Open Questions

1. **[Low]** Should `twig seed chain` default link type be `related` or a new `sequence` type? `related` is chosen for simplicity, but a dedicated type could enable ordering semantics in Plan 3.

2. **[Low]** Should `twig next` / `twig prev` sort siblings by ID, title, or creation order? ID is chosen for determinism. Could be configurable in the future.

3. **[Low]** Should `twig seed link` validate that the target ID exists (positive IDs may not be cached)? Current design warns but allows — the item might exist in ADO but not be locally cached.

4. **[Low]** Should the chain builder support `--link-type` to override the default `related` type for chain links? Not included in v1 for simplicity.

---

## Implementation Phases

### Phase 1: Domain Model + Persistence (Foundation)
**Exit criteria:** `SeedLink` value object, `ISeedLinkRepository`, `SqliteSeedLinkRepository`, and schema v7 exist and pass unit tests.

### Phase 2: Link Commands (CRUD)
**Exit criteria:** `twig seed link`, `twig seed unlink`, `twig seed links` work end-to-end with human/JSON output.

### Phase 3: Chain Builder
**Exit criteria:** `twig seed chain` creates linked seeds interactively, works in TTY and piped modes.

### Phase 4: Navigation (Next/Prev)
**Exit criteria:** `twig next` / `twig prev` navigate siblings and update active context.

### Phase 5: Dashboard Integration + Polish
**Exit criteria:** Seed view shows virtual links. Hints updated. Help text updated. All tests green.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/SeedLink.cs` | `SeedLink` record struct + `SeedLinkTypes` constants |
| `src/Twig.Domain/Interfaces/ISeedLinkRepository.cs` | Repository contract for seed links |
| `src/Twig.Infrastructure/Persistence/SqliteSeedLinkRepository.cs` | SQLite implementation of `ISeedLinkRepository` |
| `src/Twig/Commands/SeedLinkCommand.cs` | Link/unlink/list command logic |
| `src/Twig/Commands/SeedChainCommand.cs` | Interactive chain builder |
| `tests/Twig.Domain.Tests/ValueObjects/SeedLinkTests.cs` | Unit tests for `SeedLink` and `SeedLinkTypes` |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteSeedLinkRepositoryTests.cs` | Repository integration tests |
| `tests/Twig.Cli.Tests/Commands/SeedLinkCommandTests.cs` | Command tests for link/unlink/list |
| `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | Command tests for chain builder |
| `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | Command tests for next/prev navigation |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Schema v6→v7, `seed_links` DDL, `DropAllTables` update |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `ISeedLinkRepository` → `SqliteSeedLinkRepository` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `SeedLinkCommand`, `SeedChainCommand` |
| `src/Twig/Commands/NavigationCommands.cs` | Add `NextAsync()`, `PrevAsync()` |
| `src/Twig/Commands/SeedDiscardCommand.cs` | Inject `ISeedLinkRepository`, cascade-delete links |
| `src/Twig/Commands/SeedViewCommand.cs` | Fetch seed links, pass to formatters |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatSeedLinks()` signature |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatSeedLinks()`, update `FormatSeedView()` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatSeedLinks()`, update `FormatSeedView()` |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatSeedLinks()`, update `FormatSeedView()` |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Update `RenderSeedViewAsync()` signature for links |
| `src/Twig/Rendering/SpectreRenderer.cs` | Update `RenderSeedViewAsync()` to display links |
| `src/Twig/Program.cs` | Route `seed link`, `seed unlink`, `seed links`, `seed chain`, `next`, `prev` |
| `src/Twig/Hints/HintEngine.cs` | Add hints for chain, link, next/prev commands |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | Update schema version assertions |
| `tests/Twig.Cli.Tests/Commands/SeedDiscardCommandTests.cs` | Test cascade link deletion |
| `tests/Twig.Cli.Tests/Commands/SeedViewCommandTests.cs` | Test link display in dashboard |
| `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Test next/prev (or new file) |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| *(none)* | |

---

## Implementation Plan

### Epic 1: Domain Model + Persistence Foundation

**Goal:** Establish the `SeedLink` value object, repository interface, SQLite implementation, schema migration, and DI wiring.

**Prerequisites:** Plan 1 complete (seed infrastructure in place).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `SeedLink` record struct and `SeedLinkTypes` static class with constants and `GetReverse()` | `src/Twig.Domain/ValueObjects/SeedLink.cs` | DONE |
| E1-T2 | TEST | Unit tests for `SeedLink` value equality and `SeedLinkTypes.GetReverse()` mapping | `tests/Twig.Domain.Tests/ValueObjects/SeedLinkTests.cs` | DONE |
| E1-T3 | IMPL | Create `ISeedLinkRepository` interface | `src/Twig.Domain/Interfaces/ISeedLinkRepository.cs` | DONE |
| E1-T4 | IMPL | Bump `SqliteCacheStore.SchemaVersion` to 7, add `seed_links` DDL, update `DropAllTables` | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | DONE |
| E1-T5 | IMPL | Create `SqliteSeedLinkRepository` implementing `ISeedLinkRepository` | `src/Twig.Infrastructure/Persistence/SqliteSeedLinkRepository.cs` | DONE |
| E1-T6 | TEST | Integration tests for `SqliteSeedLinkRepository` (add, remove, get by item, get all, delete for item) | `tests/Twig.Infrastructure.Tests/Persistence/SqliteSeedLinkRepositoryTests.cs` | DONE |
| E1-T7 | IMPL | Register `ISeedLinkRepository` → `SqliteSeedLinkRepository` in `TwigServiceRegistration` | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE |
| E1-T8 | TEST | Update `SqliteCacheStoreTests` for schema version 7 assertions | `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | DONE |

**Acceptance Criteria:**
- [x] `SeedLink` record struct compiles as AOT-compatible value type
- [x] `SeedLinkTypes.GetReverse()` returns correct inverse for all directional types
- [x] `SqliteSeedLinkRepository` round-trips links through SQLite
- [x] `GetLinksForItemAsync` returns links where item is source OR target
- [x] `DeleteLinksForItemAsync` removes all links for an item
- [x] Schema v7 creates `seed_links` table with correct indexes
- [x] All unit and integration tests pass

---

### Epic 2: Seed Link Commands

**Goal:** CLI subcommands for creating, removing, and listing virtual links.

**Prerequisites:** Epic 1 (repository + schema).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Create `SeedLinkCommand` with `LinkAsync`, `UnlinkAsync`, `ListLinksAsync` methods | `src/Twig/Commands/SeedLinkCommand.cs` | DONE |
| E2-T2 | IMPL | Add `SeedLink`, `SeedUnlink`, `SeedLinks` routing in `TwigCommands` (Program.cs) | `src/Twig/Program.cs` | DONE |
| E2-T3 | IMPL | Register `SeedLinkCommand` in `CommandRegistrationModule` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DONE |
| E2-T4 | IMPL | Add `FormatSeedLinks()` to `IOutputFormatter` and implement in Human/JSON/Minimal formatters | `src/Twig/Formatters/IOutputFormatter.cs`, `HumanOutputFormatter.cs`, `JsonOutputFormatter.cs`, `MinimalOutputFormatter.cs` | DONE |
| E2-T5 | TEST | Command tests: link creation, duplicate handling, validation (at least one negative ID), invalid type | `tests/Twig.Cli.Tests/Commands/SeedLinkCommandTests.cs` | DONE |
| E2-T6 | TEST | Command tests: unlink existing/non-existent, list all/by-id | `tests/Twig.Cli.Tests/Commands/SeedLinkCommandTests.cs` | DONE |
| E2-T7 | IMPL | Update `GroupedHelp.Show()` with seed link commands | `src/Twig/Program.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig seed link -1 -2` creates a "related" link
- [x] `twig seed link -1 12345 --type blocks` creates a "blocks" link
- [x] `twig seed link 100 200` rejected with error (no seeds involved)
- [x] `twig seed link -1 -2 --type invalid` rejected with error listing valid types
- [x] `twig seed unlink -1 -2` removes the link
- [x] `twig seed links` shows all virtual links
- [x] `twig seed links -1` shows links for seed -1
- [x] Human, JSON, and minimal output formats work correctly

**Completion Notes:** Bug fix applied — `UnlinkAsync` now validates link types with the same null-check guard as `LinkAsync` (returns exit code 1 with FormatError listing valid types). `Unlink_InvalidType_Rejected` test added covering exit code 1 and no-call-to-repository assertions.

---

### Epic 3: Seed Discard Cascade + Dashboard Links

**Goal:** Cascade-delete links on discard and show links in seed view dashboard.

**Prerequisites:** Epic 1, Epic 2.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Inject `ISeedLinkRepository` into `SeedDiscardCommand`, call `DeleteLinksForItemAsync` on discard | `src/Twig/Commands/SeedDiscardCommand.cs` | DONE |
| E3-T2 | TEST | Test cascade: discard seed removes its links from `seed_links` table | `tests/Twig.Cli.Tests/Commands/SeedDiscardCommandTests.cs` | DONE |
| E3-T3 | IMPL | Update `SeedViewCommand` to fetch links for all seeds, pass link map to formatters | `src/Twig/Commands/SeedViewCommand.cs` | DONE |
| E3-T4 | IMPL | Update `FormatSeedView` in `HumanOutputFormatter` to display links per seed | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| E3-T5 | IMPL | Update `FormatSeedView` in `JsonOutputFormatter` to include links in output | `src/Twig/Formatters/JsonOutputFormatter.cs` | DONE |
| E3-T6 | IMPL | Update `FormatSeedView` in `MinimalOutputFormatter` | `src/Twig/Formatters/MinimalOutputFormatter.cs` | DONE |
| E3-T7 | IMPL | Update `IOutputFormatter.FormatSeedView` signature to accept link map parameter | `src/Twig/Formatters/IOutputFormatter.cs` | DONE |
| E3-T8 | IMPL | Update `IAsyncRenderer.RenderSeedViewAsync` signature and `SpectreRenderer` implementation | `src/Twig/Rendering/IAsyncRenderer.cs`, `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| E3-T9 | TEST | Test seed view displays links correctly | `tests/Twig.Cli.Tests/Commands/SeedViewCommandTests.cs` | DONE |

**Acceptance Criteria:**
- [x] Discarding seed -1 removes all `seed_links` where source_id=-1 or target_id=-1
- [x] `twig seed view` shows links per seed (e.g., "→ blocks -2", "→ depends on #12345")
- [x] SpectreRenderer and HumanOutputFormatter both display links
- [x] Seeds with no links show no link annotation

**Completion Notes:** `ISeedLinkRepository` injected into both `SeedDiscardCommand` (cascade delete before discard) and `SeedViewCommand` (link map built via `BuildLinkMapAsync()`). All three formatters (Human, JSON, Minimal) and `SpectreRenderer` updated to accept and render the nullable link map. `IOutputFormatter.FormatSeedView` and `IAsyncRenderer.RenderSeedViewAsync` signatures extended with optional `links` parameter (default null). `SeedLifecycleIntegrationTests` updated to pass `ISeedLinkRepository` mock to updated constructors. Minor trade-off: `SpectreRenderer` cyan annotation rendering is not covered by unit tests (accepted convention in this codebase).

---

### Epic 4: Interactive Chain Builder

**Goal:** `twig seed chain` for rapid sequential seed creation with auto-linking.

**Prerequisites:** Epic 1 (seed link repository).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Create `SeedChainCommand` with interactive loop, `--parent`, `--type` options | `src/Twig/Commands/SeedChainCommand.cs` | DONE |
| E4-T2 | IMPL | Add `SeedChain` routing in `TwigCommands` (Program.cs) | `src/Twig/Program.cs` | DONE |
| E4-T3 | IMPL | Register `SeedChainCommand` in `CommandRegistrationModule` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | DONE |
| E4-T4 | TEST | Test chain creates N seeds with N-1 links, summary output correct | `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | DONE |
| E4-T5 | TEST | Test chain with `--parent` override and `--type` override | `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | DONE |
| E4-T6 | TEST | Test chain in piped mode (multi-line stdin, empty line terminates) | `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | DONE |
| E4-T7 | TEST | Test chain with no parent context (error) and empty input (0 seeds created) | `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | DONE |
| E4-T8 | IMPL | Add chain builder hint in `HintEngine` | `src/Twig/Hints/HintEngine.cs` | DONE |
| E4-T9 | IMPL | Update `GroupedHelp.Show()` with chain command | `src/Twig/Program.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig seed chain` prompts for titles in a loop
- [x] Each seed is linked to the previous one with "related" type
- [x] Summary displays "Created N seeds: #-1 → #-2 → #-3"
- [x] `--parent <id>` overrides active parent context
- [x] `--type <type>` sets work item type for all chain seeds
- [x] Empty input (or EOF in piped mode) terminates the loop
- [x] Zero titles entered prints "No seeds created." and returns 0

**Completion Notes:** Two review fixes applied: (1) `SeedChainCommand.cs` line 114 routes the zero-result message through `fmt.FormatInfo("No seeds created.")` instead of raw `Console.WriteLine`, consistent with all other informational output in the file and matching the pattern in `SeedNewCommand`; (2) `Chain_NullInput_ZeroSeeds_ReturnsZero` test now captures stdout and asserts `ShouldContain("No seeds created.")`, matching the parallel `Chain_EmptyInput_ZeroSeeds_ReturnsZero` test. The two zero-seed test cases were also consolidated into a `[Theory]` to eliminate duplication. All acceptance criteria verified.

---

### Epic 5:Next/Prev Sibling Navigation

**Goal:** `twig next` and `twig prev` for horizontal sibling traversal.

**Prerequisites:** None (independent of link infrastructure).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | Add `NextAsync` and `PrevAsync` methods to `NavigationCommands` | `src/Twig/Commands/NavigationCommands.cs` | DONE |
| E5-T2 | IMPL | Add `Next()` and `Prev()` routing in `TwigCommands` (Program.cs) | `src/Twig/Program.cs` | DONE |
| E5-T3 | TEST | Test next: moves to next sibling by ID, handles boundary | `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | DONE |
| E5-T4 | TEST | Test prev: moves to previous sibling by ID, handles boundary | `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | DONE |
| E5-T5 | TEST | Test next/prev with no parent (error), single child (boundary), mixed seeds+real items | `tests/Twig.Cli.Tests/Commands/NextPrevCommandTests.cs` | DONE |
| E5-T6 | IMPL | Update `GroupedHelp.Show()` with next/prev in Navigation section | `src/Twig/Program.cs` | DONE |
| E5-T7 | IMPL | Add next/prev hints in `HintEngine` | `src/Twig/Hints/HintEngine.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig next` sets context to next sibling (by ID order) and displays it
- [x] `twig prev` sets context to previous sibling (by ID order) and displays it
- [x] At last sibling: "Already at last sibling under #<parentId>."
- [x] At first sibling: "Already at first sibling under #<parentId>."
- [x] No parent: "Cannot navigate siblings — item has no parent."
- [x] Works with both real items and seeds
- [x] `GroupedHelp` shows next/prev under Navigation section

**Completion Notes:** Shared `NavigateSiblingAsync(int direction, ...)` helper avoids duplication between `NextAsync`/`PrevAsync`. Siblings sorted by ID for deterministic ordering (negative seed IDs sort before positive ADO IDs). `GetChildrenAsync` call correctly passes `ct` cancellation token — an improvement over the equivalent call in `UpAsync`/`DownAsync`. `SetCommand.ExecuteAsync` delegated for context switching, consistent with existing navigation pattern. `HintEngine` adds `next`/`prev` hints and a sibling navigation hint on `set` when the item has a parent. All 12 new tests pass alongside 8 existing `TreeNavCommandTests`.

---

## References

- [Plan 1: Seed Foundation](twig-seed-foundation.plan.md) — prerequisite plan establishing local-first seeds
- [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework) — CLI routing framework
- [ADO Work Item Link Types](https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference) — reference for link type semantics when mapping virtual links to ADO in Plan 3
- `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` — schema DDL source of truth
- `src/Twig/Commands/NavigationCommands.cs` — existing up/down navigation pattern
- `src/Twig.Domain/ValueObjects/WorkItemLink.cs` — existing ADO link value object (not modified)
