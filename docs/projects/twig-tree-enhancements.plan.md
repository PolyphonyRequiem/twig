# Twig Tree Enhancements: Sibling Counts & Related Links

> **Revision:** 6 — Fixes critical Link Persistence Write Path gap (score 88→revised)  
> **Status:** Draft  
> **Date:** 2026-03-18

---

## Executive Summary

This design adds two focused features to the `twig tree` command: (1) a **sibling count indicator** displaying `...N` (known) or `...?` (unknown) below tree nodes to show sibling count at each level, and (2) a **related links section** showing non-hierarchy relationships (Related, Predecessor, Successor) of the focused work item below the main tree, rendered via box-drawing lines. Both features require: adding a `WorkItemLink` value object to the domain, extracting non-hierarchy relations in `AdoResponseMapper`, persisting them in a new `work_item_links` SQLite table (schema version 4 → 5), enriching the `WorkTree` read model, and modifying both rendering paths (`SpectreRenderer` and `HumanOutputFormatter`). The design is additive — existing `WorkItem` aggregate, `IAdoWorkItemService`, and `IWorkItemRepository` interfaces gain new members without breaking existing consumers.

---

## Background

### Current Architecture

The `twig tree` command follows a four-layer architecture:

1. **Domain Layer** (`Twig.Domain`)
   - `WorkItem` aggregate (`src/Twig.Domain/Aggregates/WorkItem.cs`): stores identity (Id, Type, Title, State), hierarchy (ParentId), metadata. Has **no** knowledge of non-hierarchy relations (Related, Predecessor, Successor).
   - `WorkTree` read model (`src/Twig.Domain/ReadModels/WorkTree.cs`): immutable composite of `FocusedItem`, `ParentChain` (root → parent), and `Children`. No sibling count or link data.
   - `IWorkItemRepository` (`src/Twig.Domain/Interfaces/IWorkItemRepository.cs`): provides `GetChildrenAsync(parentId)` and `GetParentChainAsync(id)`. No relation-querying methods.
   - `IAdoWorkItemService` (`src/Twig.Domain/Interfaces/IAdoWorkItemService.cs`): domain-side contract with `FetchAsync`, `FetchBatchAsync`, `FetchChildrenAsync`. Returns `WorkItem` — DTO relations are discarded.

2. **Infrastructure Layer** (`Twig.Infrastructure`)
   - `AdoRestClient` (`src/Twig.Infrastructure/Ado/AdoRestClient.cs`): implements `IAdoWorkItemService`. All fetch methods use `$expand=relations` — the ADO response **already contains** all relation data, but `AdoResponseMapper.MapWorkItem()` only extracts `ParentId` from `System.LinkTypes.Hierarchy-Reverse` and discards everything else.
   - `AdoResponseMapper` (`src/Twig.Infrastructure/Ado/AdoResponseMapper.cs`): anti-corruption layer. `ExtractParentId()` filters for hierarchy-reverse relation and parses URL suffix. Non-hierarchy relations are ignored.
   - `SqliteWorkItemRepository` (`src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs`): SQLite-backed repository. Schema version 4 with `work_items` table. `GetChildrenAsync(parentId)` executes `SELECT * FROM work_items WHERE parent_id = @parentId ORDER BY type, title` and returns `IReadOnlyList<WorkItem>` (full objects, not counts).
   - `SqliteCacheStore` (`src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`): manages DB lifecycle, schema versioning (currently `SchemaVersion = 4`), WAL mode.
   - `SyncCoordinator` (`src/Twig.Domain/Services/SyncCoordinator.cs`): coordinates sync between local cache and ADO. All writes go through `ProtectedCacheWriter` which wraps `IWorkItemRepository` with dirty-item protection logic via `SyncGuard`.
   - `ProtectedCacheWriter` (`src/Twig.Domain/Services/ProtectedCacheWriter.cs`): wraps `IWorkItemRepository.SaveAsync`/`SaveBatchAsync` with dirty-item protection. Checks `SyncGuard.GetProtectedItemIdsAsync()` before every write to prevent overwriting locally-modified items.

3. **CLI Layer** (`Twig`)
   - `TreeCommand` (`src/Twig/Commands/TreeCommand.cs`): orchestrates tree rendering. Resolves rendering pipeline (async Spectre vs. sync formatter), loads focused item + parent chain + children.
   - `SpectreRenderer` (`src/Twig/Rendering/SpectreRenderer.cs`): async Spectre.Console Live() rendering. `RenderTreeAsync()` builds tree progressively: parent chain → focused item → children. Uses `BuildSpectreTree()` to construct `Spectre.Console.Tree`.
   - `HumanOutputFormatter` (`src/Twig/Formatters/HumanOutputFormatter.cs`): sync ANSI formatter. `FormatTree()` renders with box-drawing chars (├── └──), right-aligned state badges.
   - `IAsyncRenderer` (`src/Twig/Rendering/IAsyncRenderer.cs`): interface for `RenderTreeAsync()`.
   - `IOutputFormatter` (`src/Twig/Formatters/IOutputFormatter.cs`): interface for `FormatTree()`.

4. **TUI Layer** (`Twig.Tui`)
   - `src/Twig.Tui/`: Separate TUI project with its own test project. Does **not** reference `WorkTree` and is **unchanged** by this design.

5. **Test Coverage**
   - `TreeCommandTests.cs`, `TreeCommandAsyncTests.cs`: test both sync/async paths, depth limiting, active markers, dirty indicators.
   - `HumanOutputFormatterTests.cs`: tests box-drawing, state alignment, badge rendering.
   - `AdoResponseMapperTests.cs`: tests ExtractParentId, MapWorkItem, MapPatchDocument.

### Context

- ADO REST API already returns all relations via `$expand=relations` — no additional API calls needed
- ADO relation reference names: `System.LinkTypes.Related` (network topology), `System.LinkTypes.Dependency-Forward` (Successor), `System.LinkTypes.Dependency-Reverse` (Predecessor)
- The `TreeCommand` currently calls `workItemRepo.GetChildrenAsync(parentId)` to compute children, which also provides a natural place to compute sibling counts

---

## Problem Statement

1. **No sibling visibility**: When viewing a tree, users cannot see how many siblings exist at each level. A focused item under a parent with 50 children looks identical to one under a parent with 2 children. This impairs navigation decisions.

2. **No non-hierarchy link visibility**: Related items, predecessors, and successors are only visible in the ADO web UI. The `twig tree` command discards these relations during mapping, losing valuable context about work item dependencies and associations.

---

## Goals and Non-Goals

### Goals
- G1: Show sibling count for each tree node (parent chain nodes and focused item) as a dimmed indicator line
- G2: Display non-hierarchy links (Related, Predecessor, Successor) from the focused work item in a "Links" section below the tree
- G3: Support both SpectreRenderer (async Live path) and HumanOutputFormatter (sync path)
- G4: Persist link data in SQLite for offline access

### Non-Goals
- **N1**: Navigating to related items (future feature)
- **N2**: Showing links for non-focused items (children, parents)
- **N3**: Showing all ADO link types (hyperlinks, external, test, remote, CMMI-specific)
- **N4**: Editing or creating links from the CLI
- **N5**: Displaying links in JSON or Minimal formatters (render paths only)

---

## Requirements

### Functional

| ID | Requirement |
|----|------------|
| FR-1 | For each node in the parent chain, show a dimmed line `...N` where N is the sibling count (children of that node's parent) |
| FR-2 | For the focused item, show `...N` based on children count of its parent (same GetChildrenAsync call used for tree children) |
| FR-3 | If sibling count is unknown (parent not in cache), show `...?` |
| FR-4 | Sibling count indicator appears as a dimmed line below the node, at the same indentation |
| FR-5 | After the main tree, display a "Links" section showing Related, Predecessor, and Successor links from the focused work item |
| FR-6 | Each link shows: link type label, work item ID, title, and state (if the target item is in cache) |
| FR-7 | If the target item is not in cache, show ID only with "(not cached)" indicator |
| FR-8 | Links section connected to tree via box-drawing lines |
| FR-9 | Link data persisted in SQLite `work_item_links` table |
| FR-10 | Schema version bumped from 4 to 5 (triggers clean rebuild) |

### Non-Functional

| ID | Requirement |
|----|------------|
| NFR-1 | Link extraction uses existing `$expand=relations` ADO response. Link sync adds 1 API call per `twig tree` invocation (same endpoint, same response shape) — no new API surface |
| NFR-2 | Sibling count computation reuses `GetChildrenAsync` returning full objects, with `.Count` for the count — no more than 1 query per parent chain node |
| NFR-3 | AOT-compatible — no reflection-based serialization |

---

## Proposed Design

### Architecture Overview

```
ADO REST API ($expand=relations)
       │
       ▼
AdoResponseMapper ──► MapWorkItem() extracts ParentId (existing)
       │             ├─► NEW: MapWorkItemWithLinks() also returns List<WorkItemLink>
       │
       ▼
AdoRestClient ──► FetchAsync() / FetchBatchAsync()
       │          ├─► NEW: FetchWithLinksAsync() returns (WorkItem, IReadOnlyList<WorkItemLink>) tuple
       │          └─► via new IAdoWorkItemService method
       │
       ▼
SyncCoordinator ──► Persists WorkItem via ProtectedCacheWriter (existing)
       │            ├─► NEW: SyncLinksAsync(focusedId) — fetches+persists links for a single item
       │            │        Called by TreeCommand after working-set sync.
       │            │        Persists links directly via IWorkItemLinkRepository
       │            │        (bypasses ProtectedCacheWriter — links have no dirty semantics)
       │
       ▼
SqliteWorkItemRepository ──► work_items table (existing)
SqliteWorkItemLinkRepository ──► work_item_links table (NEW)
       │
       ▼
TreeCommand ──► Loads WorkTree (existing)
       │       ├─► NEW: Calls SyncLinksAsync for focused item, then loads links from repo
       │       ├─► NEW: Computes sibling counts from GetChildrenAsync on parents
       │
       ▼
WorkTree ──► FocusedItem, ParentChain, Children (existing)
       │    ├─► NEW: SiblingCounts (IReadOnlyDictionary<int, int?>)
       │    ├─► NEW: FocusedItemLinks (IReadOnlyList<WorkItemLink>)
       │
       ▼
SpectreRenderer / HumanOutputFormatter ──► Render tree (existing)
       │                                  ├─► NEW: Render sibling count lines
       │                                  ├─► NEW: Render Links section
```

### Key Components

#### 1. `WorkItemLink` Value Object (NEW)

```csharp
// src/Twig.Domain/ValueObjects/WorkItemLink.cs
public readonly record struct WorkItemLink(
    int SourceId,
    int TargetId,
    string LinkType);  // "Related", "Predecessor", "Successor"
```

Immutable value object. Three link types supported via constants:

```csharp
public static class LinkTypes
{
    public const string Related = "Related";
    public const string Predecessor = "Predecessor";
    public const string Successor = "Successor";
}
```

#### 2. `AdoResponseMapper` Extension

Add new `ExtractNonHierarchyLinks()` method alongside existing `ExtractParentId()`:

```csharp
internal static List<WorkItemLink> ExtractNonHierarchyLinks(int sourceId, List<AdoRelation>? relations)
```

Maps ADO relation reference names:
- `System.LinkTypes.Related` → "Related"
- `System.LinkTypes.Dependency-Forward` → "Successor"  
- `System.LinkTypes.Dependency-Reverse` → "Predecessor"

Parses target work item ID from the relation URL suffix (same pattern as `ExtractParentId`).

Add overload `MapWorkItemWithLinks()` that returns `(WorkItem Item, IReadOnlyList<WorkItemLink> Links)`.

#### 3. `IAdoWorkItemService` Extension

Add new method:

```csharp
Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default);
```

**Additive** — existing `FetchAsync` remains unchanged. `FetchWithLinksAsync` calls the same ADO endpoint but uses the new `MapWorkItemWithLinks` mapper. This avoids breaking the 19 production files and 38 test files that reference `IAdoWorkItemService`.

#### 4. `IWorkItemLinkRepository` (NEW Interface)

```csharp
// src/Twig.Domain/Interfaces/IWorkItemLinkRepository.cs
public interface IWorkItemLinkRepository
{
    Task<IReadOnlyList<WorkItemLink>> GetLinksAsync(int workItemId, CancellationToken ct = default);
    Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default);
}
```

#### 5. SQLite Schema Changes

New table in `SqliteCacheStore.Ddl`:

```sql
CREATE TABLE work_item_links (
    source_id INTEGER NOT NULL,
    target_id INTEGER NOT NULL,
    link_type TEXT NOT NULL,
    PRIMARY KEY (source_id, target_id, link_type)
);
CREATE INDEX idx_work_item_links_source ON work_item_links(source_id);
```

`SchemaVersion` bumped from 4 to 5. This triggers a full schema rebuild on next CLI invocation (existing behavior — `EnsureSchema()` drops + recreates all tables on version mismatch).

`DropAllTables()` updated to include `"work_item_links"` in the tables array (currently: `["pending_changes", "work_items", "process_types", "context", "metadata"]`).

`SqliteWorkItemLinkRepository` implements `IWorkItemLinkRepository`:
- `GetLinksAsync`: `SELECT * FROM work_item_links WHERE source_id = @id`
- `SaveLinksAsync`: Delete existing + insert new within transaction

#### 6. `WorkTree` Read Model Extension

Add two new properties:

```csharp
public IReadOnlyDictionary<int, int?> SiblingCounts { get; }
public IReadOnlyList<WorkItemLink> FocusedItemLinks { get; }
```

Update `Build()` factory to accept these:

```csharp
public static WorkTree Build(
    WorkItem focus, 
    IReadOnlyList<WorkItem> parentChain, 
    IReadOnlyList<WorkItem> children,
    IReadOnlyDictionary<int, int?>? siblingCounts = null,
    IReadOnlyList<WorkItemLink>? focusedItemLinks = null)
```

Default `null` parameters ensure backward compatibility with all 41 existing call sites (3 production in `TreeCommand.cs` + `NavigationCommands.cs`, 38 in tests across `WorkTreeTests.cs`, `HumanOutputFormatterTests.cs`, `JsonOutputFormatterTests.cs`, `MinimalOutputFormatterTests.cs`).

**Sibling count dictionary**: keys are work item IDs (from parent chain + focused item), values are `int?` — `null` means unknown (parent not in cache), `int` means known count.

#### 7. `TreeCommand` Changes — Sibling Count Algorithm

Sibling count for node X = number of children of X's parent = `GetChildrenAsync(X.ParentId).Count`.

In the sync path, before calling `WorkTree.Build()`:

```csharp
var siblingCounts = new Dictionary<int, int?>();

// Parent chain nodes: siblings = children of their parent
foreach (var node in parentChain)
{
    if (node.ParentId.HasValue)
    {
        var siblings = await workItemRepo.GetChildrenAsync(node.ParentId.Value);
        siblingCounts[node.Id] = siblings.Count;
    }
    else
    {
        siblingCounts[node.Id] = null; // root — no parent to query
    }
}

// Focused item: siblings = children of focused item's parent
if (item.ParentId.HasValue)
{
    // Note: item.ParentId is the last element of parentChain.
    // We need GetChildrenAsync(item.ParentId) — NOT the children of the focused item.
    var focusedSiblings = await workItemRepo.GetChildrenAsync(item.ParentId.Value);
    siblingCounts[item.Id] = focusedSiblings.Count;
}
else
{
    siblingCounts[item.Id] = null; // root — no meaningful sibling count
}
```

**Note on `GetChildrenAsync` returning full objects**: The existing `GetChildrenAsync` returns `IReadOnlyList<WorkItem>` (full row hydration via `SELECT * FROM work_items WHERE parent_id = @parentId ORDER BY type, title`). We call `.Count` on the result. This is slightly wasteful compared to a `SELECT COUNT(*)` query, but acceptable because: (a) parent chains are typically 2–4 levels deep, (b) each query is ~1ms on indexed `parent_id`, (c) it avoids adding a new repository method for a single use case. If profiling reveals this as a bottleneck, a `GetChildCountAsync(parentId)` method can be added later.

For the async path (`SpectreRenderer.RenderTreeAsync`), add new callback parameters:
- `Func<int, Task<int?>> getSiblingCount` — given a work item ID, returns its sibling count
- `Func<Task<IReadOnlyList<WorkItemLink>>> getLinks` — returns links for the focused item

#### 8. Rendering Changes

**HumanOutputFormatter.FormatTree()** — after each parent chain node line and after the focused item line, optionally emit:
```
      ...3                    (dimmed, same indent + offset)
```

**Links section** after children (box-drawing connected):
```
    ┊
    ╰── Links
        ├── Related: #456 Some Related Item      [Active]
        ├── Predecessor: #123 Must Do First       [Closed]
        └── Successor: #789 Next Step             [New]
```

**SpectreRenderer.RenderTreeAsync()** — after building the tree and adding children:
- Add sibling count dimmed text nodes below each parent and focused item
- Add a links section using Spectre.Console markup after the tree

#### 9. Link Persistence Write Path — SyncCoordinator.SyncLinksAsync

**Critical design context**: `SyncItemAsync()` has **zero production callers** — all commands use `SyncWorkingSetAsync()` (batch) or `ActiveItemResolver.ResolveByIdAsync()` (cache-first with `FetchAsync` fallback). Neither of these paths invokes `FetchWithLinksAsync` or persists links. Therefore, link persistence requires a **new dedicated method** with an explicit production call site.

**Solution**: Add `SyncLinksAsync(int itemId, CancellationToken ct)` to `SyncCoordinator`. This is a new public method (not a modification of existing methods) that `TreeCommand` calls explicitly after working-set sync.

```csharp
/// <summary>
/// Fetches and persists non-hierarchy links for a single item.
/// Called by TreeCommand to ensure links are available for rendering.
/// Returns the fetched links for immediate use by the caller.
/// </summary>
public async Task<IReadOnlyList<WorkItemLink>> SyncLinksAsync(int itemId, CancellationToken ct = default)
{
    var (fetched, links) = await _adoService.FetchWithLinksAsync(itemId, ct);
    await _protectedCacheWriter.SaveProtectedAsync(fetched, ct); // piggyback refresh
    await _linkRepo.SaveLinksAsync(itemId, links, ct);           // direct — no protection needed
    return links;
}
```

**Why this approach is correct:**

1. **Explicit production caller**: `TreeCommand.ExecuteAsync()` calls `SyncLinksAsync(focusedId)` — verified call chain exists.
2. **Follows existing naming pattern**: sits alongside `SyncItemAsync`, `SyncWorkingSetAsync`, `SyncChildrenAsync`.
3. **Single API call**: `FetchWithLinksAsync` hits the same ADO endpoint (`$expand=relations`) as existing `FetchAsync` — one HTTP round-trip fetches both the work item and its links.
4. **Piggyback work-item refresh**: saves the fetched work item through `ProtectedCacheWriter` as a bonus refresh, which is harmless (idempotent) and may save a later stale-check fetch.
5. **Direct link persistence**: Links bypass `ProtectedCacheWriter` because they have no dirty/pending semantics (per non-goal N4). `SyncCoordinator` injects `IWorkItemLinkRepository` directly.

**TreeCommand integration** (sync path):
```csharp
// After building parent chain + children (existing), before WorkTree.Build():
IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
try
{
    links = await syncCoordinator.SyncLinksAsync(item.Id, CancellationToken.None);
}
catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

var tree = WorkTree.Build(item, parentChain, children, siblingCounts, links);
```

**TreeCommand integration** (async path):
```csharp
// Links callback for SpectreRenderer — fetched lazily during progressive render
getLinks: async () =>
{
    try { return await syncCoordinator.SyncLinksAsync(resolvedItem.Id, ct); }
    catch { return Array.Empty<WorkItemLink>(); }
}
```

**Architectural decision**: `SyncCoordinator` writes work items through `ProtectedCacheWriter`, which wraps `IWorkItemRepository` with dirty-item protection via `SyncGuard`. Links, however, have **no dirty/pending semantics** — they are server-authoritative metadata that cannot be locally edited (per non-goal N4). Therefore, `SyncCoordinator` injects `IWorkItemLinkRepository` directly and calls `SaveLinksAsync()` without going through `ProtectedCacheWriter`.

This is the correct approach because:
1. `ProtectedCacheWriter` guards against overwriting locally-modified work item fields. Links have no local modification path.
2. Creating a `ProtectedLinkWriter` analog would add complexity with no benefit — there are no dirty links to protect.
3. Direct injection into `SyncCoordinator` follows the existing pattern where `SyncCoordinator` already holds `IWorkItemRepository` directly (for read operations like staleness checks) alongside `ProtectedCacheWriter` (for writes).

```csharp
public sealed class SyncCoordinator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly IWorkItemLinkRepository _linkRepo;  // NEW
    private readonly int _cacheStaleMinutes;
    // ...
}
```

**DI registration** (in `CommandServiceModule.cs`, line 44):
```csharp
services.AddSingleton<SyncCoordinator>(sp => new SyncCoordinator(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<IWorkItemLinkRepository>(),  // NEW
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));
```

### Data Flow

**Fetch + Persist Flow (links for focused item):**
```
TreeCommand.ExecuteAsync()
  → syncCoordinator.SyncLinksAsync(focusedId)        // NEW — dedicated link sync step
    → AdoRestClient.FetchWithLinksAsync(focusedId)
      → ADO API GET $expand=relations (same endpoint as FetchAsync)
      → AdoResponseMapper.MapWorkItemWithLinks(dto)
      → returns (WorkItem, List<WorkItemLink>)
    → ProtectedCacheWriter.SaveProtectedAsync(workItem)  // piggyback refresh
    → IWorkItemLinkRepository.SaveLinksAsync(links)       // direct, no protection
  → returns IReadOnlyList<WorkItemLink>                   // available for immediate use
```

**Read + Render Flow (sync path):**
```
TreeCommand.ExecuteAsync()
  → activeItemResolver.ResolveByIdAsync(activeId)        // resolve focused item (existing)
  → workItemRepo.GetParentChainAsync(parentId)            // parent chain (existing)
  → workItemRepo.GetChildrenAsync(focusedId)              // children (existing)
  → FOR EACH node: workItemRepo.GetChildrenAsync(node.ParentId)  // sibling counts (use .Count)
  → syncCoordinator.SyncLinksAsync(focusedId)             // fetch+persist+return links (NEW)
  → WorkTree.Build(focus, parents, children, siblingCounts, links)
  → formatter.FormatTree(tree, maxChildren, activeId)
  → syncCoordinator.SyncWorkingSetAsync(workingSet)       // best-effort background sync (existing)
```

**Read + Render Flow (async/Spectre path):**
```
TreeCommand.ExecuteAsync()
  → renderer.RenderTreeAsync(
      getFocusedItem, getParentChain, getChildren,
      getSiblingCount, getLinks,                         // NEW callbacks
      maxChildren, activeId, ct)
  → getLinks callback internally calls:
      syncCoordinator.SyncLinksAsync(focusedId)          // lazy fetch during progressive render
  → renderer.RenderWithSyncAsync(...)                    // working set sync (existing)
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| New `FetchWithLinksAsync` method instead of modifying `FetchAsync` | Additive change — avoids breaking 19 production files and 38 test files that reference `IAdoWorkItemService` |
| Separate `work_item_links` table instead of JSON column on `work_items` | Enables querying links by target (future), avoids deserializing all fields for link lookup |
| Schema version bump (4→5) triggers full rebuild | Existing `SqliteCacheStore` behavior — simpler than ALTER TABLE migration for a local cache |
| `WorkItemLink` as value object, not entity | Links are identity-less tuples; they're replaced wholesale on each sync |
| `WorkTree.Build()` default parameters | Backward compat — all 41 existing call sites (3 production + 38 test) don't need changes |
| Sibling count via `GetChildrenAsync` on parent, using `.Count` | Reuses existing SQLite query returning `IReadOnlyList<WorkItem>` ordered by type, title — no new repository method. Slightly over-fetches (full objects vs count-only) but acceptable for 2–4 nodes at ~1ms each |
| Only 3 link types (Related, Predecessor, Successor) | These are the standard ADO work link types; CMMI-specific and test types excluded per non-goal N3 |
| New `SyncLinksAsync` method on `SyncCoordinator` | `SyncItemAsync` has zero production callers — all commands use `SyncWorkingSetAsync` or `ActiveItemResolver`. A new dedicated method with an explicit caller (`TreeCommand`) ensures links are actually persisted during `twig tree` usage. |
| Link writes bypass `ProtectedCacheWriter` | Links have no dirty/pending semantics (N4: no editing). Direct `IWorkItemLinkRepository` injection into `SyncCoordinator` is correct — no protection needed |

---

## Alternatives Considered

### Modifying `FetchAsync` Return Type
**Option**: Change `FetchAsync` to return `(WorkItem, IReadOnlyList<WorkItemLink>)`.
**Pros**: No new method.
**Cons**: Breaks all 19 production consumers and 38 test files. Massive blast radius.
**Decision**: Rejected — additive `FetchWithLinksAsync` is safer.

### Storing Links on WorkItem Aggregate
**Option**: Add `IReadOnlyList<WorkItemLink> Links` property to `WorkItem`.
**Pros**: Single aggregate carries all data.
**Cons**: Violates aggregate design (WorkItem is the aggregate root for *this* item, not its relations). Requires changing `MapRow()`, `SaveWorkItem()`, serialization. Bloats the aggregate with data only used by tree view.
**Decision**: Rejected — separate repository and value object keeps concerns clean.

### Computing Sibling Counts from Existing Children Data
**Option**: Since we already fetch children of the focused item, could we derive sibling count?
**Pros**: No additional queries.
**Cons**: Children of focused item ≠ siblings of focused item. Siblings = children of focused item's *parent*. For parent chain nodes, we'd need separate queries anyway.
**Decision**: Rejected — explicit `GetChildrenAsync(parentId)` queries needed for correct counts.

### Adding a `GetChildCountAsync` Repository Method
**Option**: Add `Task<int> GetChildCountAsync(int parentId)` to `IWorkItemRepository` using `SELECT COUNT(*)`.
**Pros**: More efficient — avoids hydrating full `WorkItem` objects just to get a count.
**Cons**: Adds a new method to a widely-implemented interface for a marginal gain (~1ms per query with 2–4 queries). Increases surface area.
**Decision**: Deferred — use `GetChildrenAsync(...).Count` for now. If profiling shows this matters, add the count-only method as a follow-up.

### Routing Links Through ProtectedCacheWriter
**Option**: Create a `ProtectedLinkWriter` analog or extend `ProtectedCacheWriter` to handle links.
**Pros**: Consistent write path for all data.
**Cons**: Links are server-authoritative and cannot be locally edited (N4). `SyncGuard` dirty-item protection adds overhead with zero benefit. `ProtectedCacheWriter` is designed around `IWorkItemRepository` — extending it for a separate repository type adds unnecessary coupling.
**Decision**: Rejected — direct `IWorkItemLinkRepository` injection into `SyncCoordinator` is simpler and correct.

### Persisting Links in SyncItemAsync
**Option**: Modify `SyncItemAsync` to call `FetchWithLinksAsync` and `SaveLinksAsync` instead of `FetchAsync`.
**Pros**: Minimal code change; sits alongside existing sync logic.
**Cons**: `SyncItemAsync` has **zero production callers** — all commands use `SyncWorkingSetAsync()` (batch) or `ActiveItemResolver.ResolveByIdAsync()` (cache-first). Neither would invoke the modified method, meaning links would never be persisted during normal usage.
**Decision**: Rejected — a new `SyncLinksAsync` method with an explicit `TreeCommand` call site is required.

### Piggybacking on SyncWorkingSetAsync
**Option**: Modify `SyncWorkingSetAsync` to call `FetchWithLinksAsync` (or `MapWorkItemWithLinks`) instead of `FetchAsync` for stale items, persisting links for all re-fetched items during batch sync.
**Pros**: No extra API call for the focused item when it's stale; links sync alongside work items.
**Cons**: (1) Links are only needed for the focused item in tree view — extracting links for all working-set items is wasteful. (2) If the focused item is fresh (not stale), it won't be re-fetched, so links won't be captured. (3) Changes the batch sync return type and processing logic.
**Decision**: Rejected — a dedicated `SyncLinksAsync` call for the focused item is simpler and always correct regardless of staleness state.

---

## Dependencies

### External
- **ADO REST API 7.1**: Already returns `$expand=relations` — no new API features needed
- **Spectre.Console**: Already in use for tree rendering — no new packages

### Internal
- `SqliteCacheStore` schema versioning (version 4 → 5)
- `AdoResponseMapper` existing relation-parsing infrastructure (`ExtractParentId` pattern)
- `SpectreRenderer.BuildSpectreTree()` internal method (needs extension)

### Sequencing
- Epic 1 (domain + infrastructure) must complete before Epic 3 (related links feature)
- Epic 2 (sibling counts) is independent of Epic 1 — they can be developed in parallel
- Schema bump in Epic 1 will clear local cache on first run — acceptable for a dev tool

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `WorkItem` aggregate | **Unchanged** — no modifications |
| `WorkTree` read model | Extended with 2 optional properties (backward compatible) |
| `IAdoWorkItemService` | New method `FetchWithLinksAsync` (additive) |
| `IWorkItemRepository` | **Unchanged** — existing `GetChildrenAsync` reused for sibling counts |
| `AdoResponseMapper` | New methods `ExtractNonHierarchyLinks`, `MapWorkItemWithLinks` (additive) |
| `AdoRestClient` | New `FetchWithLinksAsync` implementation |
| `SqliteCacheStore` | Schema version 4→5, new `work_item_links` DDL, `DropAllTables` updated |
| `SqliteWorkItemLinkRepository` | **New** — implements `IWorkItemLinkRepository` |
| `SyncCoordinator` | New `IWorkItemLinkRepository` dependency; new `SyncLinksAsync` method. Constructor change affects DI registration in `CommandServiceModule.cs` (line 44) and ~25 direct `new SyncCoordinator(...)` calls across 17 test files. |
| `ProtectedCacheWriter` | **Unchanged** — links bypass this component |
| `TreeCommand` | Extended to call `SyncLinksAsync` (best-effort) for focused item, compute sibling counts, pass both to WorkTree and renderers |
| `SpectreRenderer` | `RenderTreeAsync` extended with new callbacks |
| `HumanOutputFormatter` | `FormatTree` extended to render sibling counts + links |
| `IAsyncRenderer` | `RenderTreeAsync` signature extended with new parameters |
| `IOutputFormatter` | **Unchanged** — `FormatTree` signature unchanged; WorkTree carries new data |
| `JsonOutputFormatter` | May add links to JSON output (optional) |
| `MinimalOutputFormatter` | **Unchanged** |
| `Twig.Tui` | **Unchanged** — does not reference `WorkTree` |

### Backward Compatibility
- `WorkTree.Build()` uses default parameters — **all 41 existing call sites unaffected** (3 production + 38 test)
- `IAdoWorkItemService.FetchAsync()` — **unchanged**
- `IOutputFormatter.FormatTree()` — **unchanged signature** (WorkTree internal change)
- `IAsyncRenderer.RenderTreeAsync()` — **breaking change** to interface, but only `SpectreRenderer` implements it. Tests use the concrete type.
- Schema version bump clears cache — acceptable for local dev tool

### Performance
- Sibling count adds N calls to `GetChildrenAsync` per parent chain node (typically 2–4 nodes). Each call executes `SELECT * FROM work_items WHERE parent_id = @parentId ORDER BY type, title`, returning full `WorkItem` objects. We use `.Count` on the result. Each query is ~1ms on the indexed `parent_id` column. This over-fetches compared to a `SELECT COUNT(*)` but is acceptable for this use case.
- Link sync adds 1 ADO API call per `twig tree` invocation — `FetchWithLinksAsync` hits the same `$expand=relations` endpoint as `FetchAsync`. This also piggybacks a work-item refresh.
- Link loading from SQLite adds 1 query per tree render (only when reading from cache after initial sync)
- No additional API calls beyond the 1 link sync call

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Schema rebuild clears user cache | Medium | Low | Expected behavior; cache rebuilds on next `twig refresh`. Document in changelog. |
| `IAsyncRenderer.RenderTreeAsync` signature change | Low | Medium | Only `SpectreRenderer` implements it. Update tests directly. |
| Sibling count queries slow for deep trees | Low | Low | Parent chains are typically 2-4 levels. Query is indexed on `parent_id`. |
| Future link types not supported | Low | Low | `ExtractNonHierarchyLinks` uses a whitelist; easy to extend. |
| `SyncCoordinator` constructor change breaks tests | Medium | Medium | Constructor gains `IWorkItemLinkRepository` parameter. ~25 `new SyncCoordinator(...)` calls across 17 test files need updates. Use find-and-replace; pass a null/mock `IWorkItemLinkRepository`. Update DI registration in `CommandServiceModule.cs`. |
| Link sync adds 1 extra API call per `twig tree` | Low | Low | Same endpoint as `FetchAsync` (`$expand=relations`). ~200ms round-trip. Acceptable for a CLI command. Piggybacks work-item refresh. |

---

## Open Questions

[Low] Should the sibling count for root-level nodes (no parent) show `...?` or be omitted entirely? Recommend omitting — root items have no meaningful sibling context.

[Low] Should the Links section show in `--no-live` / sync path only, or also in the Spectre async path? Recommend both for consistency.

[Low] Should `JsonOutputFormatter` include links and sibling counts? Recommend yes for links (machine consumers benefit), no for sibling counts (they can compute from data).

---

## Implementation Phases

### Phase 1: Domain & Infrastructure Foundation
**Exit Criteria**: `WorkItemLink` value object exists, `AdoResponseMapper` extracts non-hierarchy links, `work_item_links` table created, `SqliteWorkItemLinkRepository` passes unit tests.

### Phase 2: Sibling Count Feature
**Exit Criteria**: `WorkTree` carries sibling counts, `TreeCommand` computes them, both renderers display `...N`/`...?` indicators, tests pass.

### Phase 3: Related Links Feature
**Exit Criteria**: `SyncLinksAsync` wired in `SyncCoordinator` with explicit `TreeCommand` call site, `TreeCommand` loads links, both renderers display Links section, test `SyncCoordinator` constructors updated, tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/WorkItemLink.cs` | `WorkItemLink` value object + `LinkTypes` constants |
| `src/Twig.Domain/Interfaces/IWorkItemLinkRepository.cs` | Repository interface for link persistence |
| `src/Twig.Infrastructure/Persistence/SqliteWorkItemLinkRepository.cs` | SQLite implementation of link repository |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemLinkRepositoryTests.cs` | Link repository unit tests |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperLinkTests.cs` | Mapper link extraction tests |
| `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | Tree command link rendering tests |
| `tests/Twig.Cli.Tests/Formatters/TreeSiblingCountTests.cs` | Sibling count formatting tests |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Add `ExtractNonHierarchyLinks()`, `MapWorkItemWithLinks()` |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Add `FetchWithLinksAsync()` implementation |
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add `FetchWithLinksAsync()` method |
| `src/Twig.Domain/ReadModels/WorkTree.cs` | Add `SiblingCounts`, `FocusedItemLinks` properties; update `Build()` |
| `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Bump `SchemaVersion` to 5, add `work_item_links` DDL + index, add `"work_item_links"` to `DropAllTables` |
| `src/Twig.Domain/Services/SyncCoordinator.cs` | Add `IWorkItemLinkRepository` constructor parameter; add `SyncLinksAsync` method |
| `src/Twig/Commands/TreeCommand.cs` | Call `SyncLinksAsync` for focused item; load sibling counts; pass both to WorkTree.Build() and renderer |
| `src/Twig/Rendering/IAsyncRenderer.cs` | Extend `RenderTreeAsync()` with sibling count + links callbacks |
| `src/Twig/Rendering/SpectreRenderer.cs` | Render sibling counts below nodes; render Links section |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Render sibling count lines; render Links section |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Add links array to FormatTree JSON output |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Register `IWorkItemLinkRepository` → `SqliteWorkItemLinkRepository` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Update `SyncCoordinator` registration to pass `IWorkItemLinkRepository` |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### Epic 1: Domain & Infrastructure Foundation

**Goal**: Establish domain types, mapper extraction, SQLite persistence for work item links.

**Prerequisites**: None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | IMPL | Create `WorkItemLink` value object and `LinkTypes` constants | `src/Twig.Domain/ValueObjects/WorkItemLink.cs` | TO DO |
| E1-T2 | IMPL | Add `ExtractNonHierarchyLinks()` to `AdoResponseMapper` — parse Related, Predecessor, Successor from relations array | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | TO DO |
| E1-T3 | TEST | Unit tests for `ExtractNonHierarchyLinks` — valid relations, empty, null, mixed types, URL parsing | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperLinkTests.cs` | TO DO |
| E1-T4 | IMPL | Add `MapWorkItemWithLinks()` overload to `AdoResponseMapper` | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | TO DO |
| E1-T5 | IMPL | Create `IWorkItemLinkRepository` interface | `src/Twig.Domain/Interfaces/IWorkItemLinkRepository.cs` | TO DO |
| E1-T6 | IMPL | Bump `SqliteCacheStore.SchemaVersion` to 5, add `work_item_links` table DDL + index, add `"work_item_links"` to `DropAllTables` array | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | TO DO |
| E1-T7 | IMPL | Implement `SqliteWorkItemLinkRepository` (GetLinksAsync, SaveLinksAsync) | `src/Twig.Infrastructure/Persistence/SqliteWorkItemLinkRepository.cs` | TO DO |
| E1-T8 | TEST | Integration tests for `SqliteWorkItemLinkRepository` — save/load/replace/empty | `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemLinkRepositoryTests.cs` | TO DO |
| E1-T9 | IMPL | Add `FetchWithLinksAsync()` to `IAdoWorkItemService` and implement in `AdoRestClient` | `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs`, `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | TO DO |
| E1-T10 | IMPL | Register `IWorkItemLinkRepository` in DI container | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `WorkItemLink` value object compiles and is immutable
- [ ] `ExtractNonHierarchyLinks` correctly parses Related, Predecessor, Successor from ADO relations
- [ ] `SqliteWorkItemLinkRepository` round-trips links through SQLite
- [ ] `FetchWithLinksAsync` returns both WorkItem and links from ADO response
- [ ] Schema version 5 creates `work_item_links` table with correct indexes
- [ ] All existing tests pass (no regressions)

### Epic 2: Sibling Count Feature

**Goal**: Compute and display sibling counts in tree view for both rendering paths.

**Prerequisites**: None (independent of Epic 1 — can be developed in parallel)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Extend `WorkTree` with `SiblingCounts` dictionary property (optional, defaults to empty) | `src/Twig.Domain/ReadModels/WorkTree.cs` | TO DO |
| E2-T2 | IMPL | In `TreeCommand` sync path: compute sibling counts via `GetChildrenAsync(node.ParentId).Count` for each parent chain node + focused item | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| E2-T3 | IMPL | Extend `IAsyncRenderer.RenderTreeAsync()` with `Func<int, Task<int?>> getSiblingCount` parameter | `src/Twig/Rendering/IAsyncRenderer.cs` | TO DO |
| E2-T4 | IMPL | In `SpectreRenderer.RenderTreeAsync()`: after each parent node and focused node, add dimmed `...N` or `...?` text node | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E2-T5 | IMPL | In `HumanOutputFormatter.FormatTree()`: after each parent/focused line, emit dimmed `...N` or `...?` aligned line | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| E2-T6 | IMPL | Update `TreeCommand` async path: pass sibling count callback wrapping `GetChildrenAsync` | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| E2-T7 | TEST | Tests for sibling count in `HumanOutputFormatter` — known count, unknown, root node | `tests/Twig.Cli.Tests/Formatters/TreeSiblingCountTests.cs` | TO DO |
| E2-T8 | TEST | Tests for sibling count in `SpectreRenderer` — verify dimmed output | `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | TO DO |
| E2-T9 | TEST | Test `WorkTree.Build()` backward compat — existing callers without sibling counts still work | `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] Each parent chain node shows `...N` (dimmed) below it in both renderers
- [ ] Focused item shows sibling count below it
- [ ] Root nodes (no parent) omit sibling count or show `...?`
- [ ] Existing `WorkTree.Build()` callers compile without changes
- [ ] All existing tests pass

### Epic 3: Related Links Feature

**Goal**: Wire link data from ADO through persistence to tree rendering.

**Prerequisites**: Epic 1 (domain + infrastructure)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Extend `WorkTree` with `FocusedItemLinks` property (optional, defaults to empty list) | `src/Twig.Domain/ReadModels/WorkTree.cs` | TO DO |
| E3-T2 | IMPL | Add `IWorkItemLinkRepository` constructor parameter to `SyncCoordinator`; implement `SyncLinksAsync(int itemId, ct)` — calls `FetchWithLinksAsync`, saves work item via `ProtectedCacheWriter`, saves links via `IWorkItemLinkRepository`, returns links | `src/Twig.Domain/Services/SyncCoordinator.cs` | TO DO |
| E3-T2b | IMPL | Update `SyncCoordinator` DI registration in `CommandServiceModule.cs` to pass `IWorkItemLinkRepository` | `src/Twig/DependencyInjection/CommandServiceModule.cs` | TO DO |
| E3-T2c | TEST | Update ~25 `new SyncCoordinator(...)` calls across 17 test files to pass mock/null `IWorkItemLinkRepository` | Multiple test files | TO DO |
| E3-T3 | IMPL | In `TreeCommand` sync path: call `syncCoordinator.SyncLinksAsync(focusedId)` (best-effort, wrapped in try/catch), pass returned links to `WorkTree.Build()` | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| E3-T4 | IMPL | Extend `IAsyncRenderer.RenderTreeAsync()` with `Func<Task<IReadOnlyList<WorkItemLink>>> getLinks` parameter | `src/Twig/Rendering/IAsyncRenderer.cs` | TO DO |
| E3-T5 | IMPL | In `SpectreRenderer.RenderTreeAsync()`: after children, add Links section with box-drawing | `src/Twig/Rendering/SpectreRenderer.cs` | TO DO |
| E3-T6 | IMPL | In `HumanOutputFormatter.FormatTree()`: after children, render Links section with box-drawing lines, right-aligned state | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| E3-T7 | IMPL | In `TreeCommand` async path: pass links callback wrapping `syncCoordinator.SyncLinksAsync(focusedId)` (best-effort, returns empty on failure) | `src/Twig/Commands/TreeCommand.cs` | TO DO |
| E3-T8 | IMPL | Add links array to `JsonOutputFormatter.FormatTree()` output | `src/Twig/Formatters/JsonOutputFormatter.cs` | TO DO |
| E3-T9 | TEST | Tests for `SyncLinksAsync` — verifies links are fetched, persisted, and returned; verifies work item is also saved via ProtectedCacheWriter | `tests/Twig.Domain.Tests/Services/SyncCoordinatorTests.cs` | TO DO |
| E3-T10 | TEST | Tests for Links section in `HumanOutputFormatter` — with/without links, target in cache, target not cached | `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | TO DO |
| E3-T11 | TEST | Tests for Links section in `SpectreRenderer` async path | `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | TO DO |
| E3-T12 | TEST | End-to-end: TreeCommand with links — verify SyncLinksAsync is called, links flow through WorkTree to both renderers | `tests/Twig.Cli.Tests/Commands/TreeCommandLinkTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `SyncLinksAsync` fetches links from ADO, persists them, and returns them to caller
- [ ] `TreeCommand` calls `SyncLinksAsync` (best-effort) and passes links to `WorkTree.Build()`
- [ ] Links section appears below tree when focused item has non-hierarchy links
- [ ] Links section omitted when no links exist
- [ ] Each link shows type, ID, title (if cached), state (if cached)
- [ ] Box-drawing lines connect Links section to tree
- [ ] JSON output includes links array
- [ ] All ~25 test `SyncCoordinator` constructors updated and passing
- [ ] All existing tests pass

---

## References

- [ADO REST API — Work Item Relation Types](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-relation-types/list?view=azure-devops-rest-7.1)
- [ADO Link Types Reference](https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference?view=azure-devops)
- ADO relation reference names:
  - `System.LinkTypes.Related` — Related (network topology)
  - `System.LinkTypes.Dependency-Forward` — Successor
  - `System.LinkTypes.Dependency-Reverse` — Predecessor
  - `System.LinkTypes.Hierarchy-Forward` — Child (already handled)
  - `System.LinkTypes.Hierarchy-Reverse` — Parent (already handled)
- Existing codebase: `AdoResponseMapper.ExtractParentId()` at `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs:123-147`
- Existing schema: `SqliteCacheStore.Ddl` at `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs:125-181`
