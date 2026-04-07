# Work Item Link Management via twig CLI

**Epic:** #1343  
> **Status**: 🔨 In Progress — 2/3 PR groups merged  
**Revision:** 5 — See [Revision History](#revision-history) for change log.

---

## Executive Summary

This epic adds a `twig link` command family for managing relationships on published (non-seed) ADO work items. Today, once a work item is published to ADO, the only way to add, remove, or reparent links is through the ADO web UI or direct REST API calls. This creates friction when items are created without the correct parent (e.g., via `twig new`, which lacks a `--parent` flag). The design introduces six commands (`link parent`, `link unparent`, `link add`, `link remove`, `link list`, `link reparent`) and enhances `twig new` with `--parent` support. All link mutations automatically resync the local cache so that `twig tree` reflects changes immediately. The implementation reuses existing primitives (`AdoRestClient.AddLinkAsync`, `SeedLinkTypeMapper`, `AdoResponseMapper`) and introduces a new `RemoveLinkAsync` method on `IAdoWorkItemService` and `AdoRestClient` for link deletion via the ADO REST API's index-based removal pattern.

---

## Background

### Current Architecture

Twig's link management is currently split into two disconnected subsystems:

1. **Seed links** (local/virtual): The `twig seed link`, `twig seed unlink`, and `twig seed links` commands operate on local seed work items (negative IDs) stored in the `seed_links` SQLite table. These virtual links are promoted to ADO relations when seeds are published via `SeedLinkPromoter`. The `SeedLinkCommand` validates that at least one endpoint is a seed (negative ID) and explicitly rejects both-positive-ID links with: *"Use ADO for linking positive work items."*

2. **Published item links** (ADO REST): `AdoRestClient.AddLinkAsync` can create links between published work items via PATCH `/relations/-`, but **no CLI command exposes this**. The only link removal in the codebase is `ISeedLinkRepository.RemoveLinkAsync`, which operates on local SQLite — there is no `RemoveLinkAsync` for ADO REST relations.

3. **Link extraction** (read path): `AdoResponseMapper` already extracts both hierarchy links (`ExtractParentId` via `System.LinkTypes.Hierarchy-Reverse`) and non-hierarchy links (`ExtractNonHierarchyLinks` via `NonHierarchyRelMap`). The `SyncCoordinator.SyncLinksAsync` fetches the work item with relations and saves non-hierarchy links to the local `work_item_links` table.

4. **`twig new` gap**: `NewCommand` uses `SeedFactory.CreateUnparented` exclusively — there is no `--parent` parameter. However, `AdoResponseMapper.MapSeedToCreatePayload` already accepts an optional `parentId` parameter and generates the parent relation in the same POST, meaning the infrastructure is already in place.

### Existing Primitives to Reuse

| Component | File | What It Provides |
|-----------|------|-----------------|
| `AdoRestClient.AddLinkAsync` | `Ado/AdoRestClient.cs:148` | PATCH `/relations/-` with link type URL |
| `SeedLinkTypeMapper` | `Domain/Services/SeedLinkTypeMapper.cs` | Friendly name → ADO relation type mapping |
| `AdoResponseMapper.ExtractParentId` | `Ado/AdoResponseMapper.cs:230` | Parses parent from `Hierarchy-Reverse` relation |
| `AdoResponseMapper.ExtractNonHierarchyLinks` | `Ado/AdoResponseMapper.cs:196` | Parses Related/Successor/Predecessor links |
| `SyncCoordinator.SyncLinksAsync` | `Domain/Services/SyncCoordinator.cs:202` | Fetch-with-links + save to cache pattern |
| `ActiveItemResolver` | `Domain/Services/ActiveItemResolver.cs` | Resolve active item with cache-first, ADO-fallback |
| `IWorkItemLinkRepository` | `Domain/Interfaces/IWorkItemLinkRepository.cs` | Local link cache (GetLinksAsync, SaveLinksAsync) |
| `MapSeedToCreatePayload` | `Ado/AdoResponseMapper.cs:115` | Already supports parentId in POST payload |

### Call-Site Audit: `IAdoWorkItemService.AddLinkAsync`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SeedLinkPromoter.cs:54` | `PromoteLinksAsync` | Promotes virtual seed links to ADO after publish | No change — uses same primitive |
| `BranchCommand.cs:106` | `ExecuteAsync` | Adds artifact link (different code path via `IAdoGitService`) | No change — different API surface |

Only `SeedLinkPromoter` calls `AddLinkAsync` today. The new `LinkCommand` will be a second caller. No existing call sites are affected.

### Call-Site Audit: `IOutputFormatter.FormatSeedLinks`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `SeedLinkCommand.cs:114` | `ListLinksAsync` | Formats virtual seed links | No change |

The new `link list` command needs a **new formatter method** (`FormatWorkItemLinks`) since it operates on `WorkItemLink` (published items) rather than `SeedLink` (virtual seeds). This must be added to `IOutputFormatter` and all four implementations.

---

## Problem Statement

1. **No CLI for published link management**: Once a work item is published, the only link operations available are through the ADO web UI. The `SeedLinkCommand` explicitly rejects positive-to-positive links.

2. **Orphaned items from `twig new`**: `twig new` creates top-level items with no `--parent` flag. Items intended as children (e.g., Issues under an Epic) must be manually reparented through the ADO web UI after creation.

3. **No link removal via CLI**: `AdoRestClient` has `AddLinkAsync` but no `RemoveLinkAsync`. The ADO REST API supports link removal via `op: "remove"` with `path: "/relations/{index}"`, but this requires fetching the work item first to determine the relation index.

4. **Stale cache after external changes**: When links are managed outside twig, the local cache doesn't reflect changes until a full `twig sync`. Link mutations through the new commands should trigger targeted resyncs.

---

## Goals and Non-Goals

### Goals

1. **`twig link parent <id>`** — Set the parent of the active work item
2. **`twig link unparent`** — Remove the parent link from the active work item
3. **`twig link reparent <id>`** — Atomic remove-old-parent + set-new-parent
4. **`twig link add <id> --type <type>`** — Add a generic link (related, predecessor, successor, etc.)
5. **`twig link remove <id> --type <type>`** — Remove a specific link
6. **`twig link list [<id>]`** — Display all links on a work item
7. **`twig new --parent <id>`** — Create items with a parent from the start
8. **Automatic cache resync** — All link mutations resync affected items so `twig tree` is immediately correct
9. **All three output formats** — Human (ANSI), JSON, and Minimal output for all link commands
10. **Comprehensive tests** — Unit tests for all new commands, services, and REST client methods

### Non-Goals

- **Bulk link operations** (e.g., `twig link add-many`) — out of scope; can be scripted with loops
- **Link type discovery** (e.g., `twig link types`) — useful but separate enhancement
- **Bidirectional link display in `twig tree`** — tree already shows non-hierarchy links; no structural changes to tree rendering
- **Modifying `SeedLinkCommand`** — the seed link system remains unchanged; this is a parallel system for published items
- **Hyperlink or artifact link management** — only work item-to-work item relation links are in scope
- **Custom link types beyond ADO built-ins** — only the standard ADO link types are supported

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `twig link parent <id>` sets the parent of the active work item by adding a `System.LinkTypes.Hierarchy-Reverse` relation |
| FR-2 | `twig link unparent` removes the parent relation from the active work item |
| FR-3 | `twig link reparent <id>` atomically removes the old parent and sets a new one |
| FR-4 | `twig link add <targetId> --type <type>` adds a non-hierarchy link between the active item and the target |
| FR-5 | `twig link remove <targetId> --type <type>` removes a specific link |
| FR-6 | `twig link list [<id>]` displays all links (hierarchy + non-hierarchy) on the active or specified item |
| FR-7 | `twig new --parent <id>` creates a work item with the specified parent in one ADO call |
| FR-8 | All link mutations trigger a targeted cache resync of both source and target items |
| FR-9 | Human-friendly link type aliases map to ADO relation types (parent, child, related, predecessor, successor) |
| FR-10 | All commands support `--output human|json|minimal` formatting |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | AOT-compatible: all JSON serialization through `TwigJsonContext` source generators |
| NFR-2 | No process-template-specific assumptions: link type resolution is data-driven |
| NFR-3 | Link removal requires fetching the current relations to determine array index — minimize API calls |
| NFR-4 | Telemetry: emit command name, duration, exit code only — no link type names, IDs, or item content |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                     CLI Layer (Twig)                      │
│                                                          │
│  Program.cs ──▶ LinkCommand ──▶ IAdoWorkItemService      │
│       │              │                   │                │
│       │              │         ┌─────────┴──────────┐    │
│       │              │         │  AdoRestClient      │    │
│       │              │         │  .AddLinkAsync()    │    │
│       │              │         │  .RemoveLinkAsync() │◄── NEW
│       │              │         │  .FetchWithLinks()  │    │
│       │              │         └─────────────────────┘    │
│       │              │                                    │
│       │              ├──▶ LinkTypeMapper (new, shared)    │
│       │              │                                    │
│       │              ├──▶ SyncCoordinator.SyncLinksAsync  │
│       │              │                                    │
│       │              └──▶ IOutputFormatter                │
│       │                    .FormatWorkItemLinks() ◄────── NEW
│       │                                                   │
│  NewCommand ── (add --parent flag) ────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `LinkCommand` (new)

A single command class handling all six `twig link` subcommands, following the same pattern as `SeedLinkCommand`:

```csharp
public sealed class LinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    SyncCoordinator syncCoordinator,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
```

**Methods:**
- `ParentAsync(int parentId, ...)` — Add parent link
- `UnparentAsync(...)` — Remove parent link
- `ReparentAsync(int newParentId, ...)` — Remove old + add new parent
- `AddAsync(int targetId, string type, ...)` — Add generic link
- `RemoveAsync(int targetId, string type, ...)` — Remove generic link
- `ListAsync(int? id, ...)` — List all links

**Flow for each mutation command:**
1. Resolve active item via `ActiveItemResolver`
2. Validate inputs (target exists, link type valid)
3. Call ADO REST API (add/remove link)
4. Resync via `SyncCoordinator.SyncLinksAsync` (both source and target)
5. Format and output result
6. Write prompt state

#### 2. `LinkTypeMapper` (new, domain layer)

A generalized, bidirectional link type mapper for published work items (unlike `SeedLinkTypeMapper` which is seed-specific):

```csharp
public static class LinkTypeMapper
{
    // Friendly name → ADO relation type
    public static bool TryResolve(string friendlyName, out string adoRelationType);
    
    // ADO relation type → friendly name  
    public static string ToFriendlyName(string adoRelationType);
    
    // List all supported friendly names
    public static IReadOnlyList<string> SupportedTypes { get; }
}
```

**Supported mappings:**

| Friendly Name | ADO Relation Type |
|--------------|-------------------|
| `parent` | `System.LinkTypes.Hierarchy-Reverse` |
| `child` | `System.LinkTypes.Hierarchy-Forward` |
| `related` | `System.LinkTypes.Related` |
| `predecessor` | `System.LinkTypes.Dependency-Reverse` |
| `successor` | `System.LinkTypes.Dependency-Forward` |

#### 3. `RemoveLinkAsync` (new method on `AdoRestClient`)

The ADO REST API removes relations by **array index**. To remove a specific link:
1. Fetch the work item with `$expand=relations`
2. Find the index of the relation matching `(rel, targetUrl)`
3. PATCH with `op: "remove"`, `path: "/relations/{index}"`

```csharp
// On IAdoWorkItemService:
Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);

// Implementation on AdoRestClient:
public async Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
{
    // 1. GET to fetch current relations array (expand=relations on GET only)
    var getUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?$expand=relations&api-version={ApiVersion}";
    using var fetchResponse = await SendAsync(HttpMethod.Get, getUrl, content: null, ifMatch: null, ct);
    var dto = await DeserializeWorkItemAsync(fetchResponse, ct);
    
    // 2. Find index of matching relation
    var targetUrl = $"{_orgUrl}/_apis/wit/workitems/{targetId}";
    var index = FindRelationIndex(dto.Relations, adoLinkType, targetUrl);
    if (index < 0) return; // Relation not found — idempotent
    
    // 3. PATCH to remove by index (clean URL, no $expand; If-Match for concurrency)
    var patchUrl = $"{_orgUrl}/{_project}/_apis/wit/workitems/{sourceId}?api-version={ApiVersion}";
    var patchDoc = new List<AdoPatchOperation>
    {
        new() { Op = "remove", Path = $"/relations/{index}" }
    };
    var json = JsonSerializer.Serialize(patchDoc, TwigJsonContext.Default.ListAdoPatchOperation);
    var content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType);
    using var _ = await SendAsync(HttpMethod.Patch, patchUrl, content, ifMatch: dto.Rev.ToString(), ct);
}
```

> **AOT note:** No new types are needed in `TwigJsonContext`. `RemoveLinkAsync` reuses the already-registered `List<AdoPatchOperation>` serialization context (`TwigJsonContext.Default.ListAdoPatchOperation`). The `DeserializeWorkItemAsync` private helper (used by `FetchAsync`, `PatchAsync`, `CreateAsync`, `FetchWithLinksAsync`) handles deserialization via the existing `TwigJsonContext.Default.AdoWorkItemResponse` context.

#### 4. `FormatWorkItemLinks` (new formatter method)

New method on `IOutputFormatter` and all four implementations:

```csharp
// On IOutputFormatter:
string FormatWorkItemLinks(IReadOnlyList<WorkItemLink> links, int? parentId, WorkItem? parentItem);
```

**`parentItem` resolution:** The `ListAsync` method in `LinkCommand` is responsible for fetching the parent item. It first calls `SyncCoordinator.SyncLinksAsync` to refresh link data, then reads `parentId` from the returned `WorkItem.ParentId`. If `parentId` is non-null, it resolves the parent via `IWorkItemRepository.GetByIdAsync(parentId)` (cache-first). If the parent is not cached, it falls back to `IAdoWorkItemService.FetchAsync(parentId)`. The resolved `WorkItem?` is passed to the formatter — the formatter never fetches data itself.

**Human format example:**
```
Links for #1339:
  Parent: #1338 — Push-on-write and sync convergence [Done]
  ─────────────────
  Related:     #1340 — Cache invalidation strategy
  Predecessor: #1335 — Field definition sync
```

### Data Flow

#### `twig link parent 1338` (when active item is #1339)

```
1. ActiveItemResolver.GetActiveItemAsync()
   → cache hit: WorkItem #1339 (ParentId = null)

2. Validate: if #1339 already has a parent, emit warning ("Already has parent #X — use `twig link reparent` to change") and abort. If specified parent is already the current parent, output no-op message and return.

3. AdoRestClient.AddLinkAsync(1339, 1338, "System.LinkTypes.Hierarchy-Reverse")
   → PATCH /1339 with /relations/- { rel: "Hierarchy-Reverse", url: ".../1338" }

4. SyncCoordinator.SyncLinksAsync(1339)  // resync source
   → FetchWithLinksAsync → save to cache (ParentId now = 1338)
   
5. SyncCoordinator.SyncItemAsync(1338)   // resync target (best-effort)
   → updates children list in cache

6. Output: "✔ #1339 → parent #1338 (Push-on-write and sync convergence)"

7. WritePromptStateAsync()
```

#### `twig link remove 1340 --type related` (when active item is #1339)

```
1. ActiveItemResolver.GetActiveItemAsync()
   → cache hit: WorkItem #1339

2. LinkTypeMapper.TryResolve("related") → "System.LinkTypes.Related"

3. AdoRestClient.RemoveLinkAsync(1339, 1340, "System.LinkTypes.Related")
   → GET /1339?$expand=relations (find index of Related → #1340)
   → PATCH /1339 with op: "remove", path: "/relations/{index}"

4. SyncCoordinator.SyncLinksAsync(1339)  // resync

5. Output: "✔ Removed related link #1339 → #1340"
```

#### `twig link reparent 1340` (when active item is #1339, current parent is #1338)

```
1. ActiveItemResolver.GetActiveItemAsync()
   → cache hit: WorkItem #1339 (ParentId = 1338)

2. Validate: #1339 has an existing parent (#1338). Resolve new parent #1340 exists.

3. AdoRestClient.RemoveLinkAsync(1339, 1338, "System.LinkTypes.Hierarchy-Reverse")
   → GET /1339?$expand=relations (find index of Hierarchy-Reverse → #1338)
   → PATCH /1339 with op: "remove", path: "/relations/{index}", If-Match: rev
   → Old parent relation removed

4. AdoRestClient.AddLinkAsync(1339, 1340, "System.LinkTypes.Hierarchy-Reverse")
   → PATCH /1339 with /relations/- { rel: "Hierarchy-Reverse", url: ".../1340" }
   → New parent relation added

5. SyncCoordinator.SyncLinksAsync(1339)  // resync child
   → FetchWithLinksAsync → save to cache (ParentId now = 1340)

6. SyncCoordinator.SyncItemAsync(1338)   // resync OLD parent (best-effort)
   → updates children list in cache (#1339 removed)

7. SyncCoordinator.SyncItemAsync(1340)   // resync NEW parent (best-effort)
   → updates children list in cache (#1339 added)

8. Output: "✔ #1339 reparented: #1338 → #1340 (Cache invalidation strategy)"

9. WritePromptStateAsync()
```

> **Reparent note:** This requires two sequential ADO API calls (remove + add). If the add fails after a successful remove, the item is left orphaned — surface a clear error and the user can recover with `twig link parent <old-id>`.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Single `LinkCommand` class** for all 6 subcommands | Follows established pattern (`SeedLinkCommand` handles link/unlink/list). Keeps DI surface minimal. |
| **New `LinkTypeMapper`** (not extending `SeedLinkTypeMapper`) | `SeedLinkTypeMapper` uses `StringComparer.Ordinal` (case-sensitive, binary comparison) with seed-specific constant names from `SeedLinkTypes` (`parent-child`, `blocks`, `blocked-by`, `depends-on`, `depended-on-by`). The new `LinkTypeMapper` needs `StringComparer.OrdinalIgnoreCase` for user-facing CLI input and uses simpler names (`parent`, `child`, `related`, `predecessor`, `successor`). These are fundamentally different lookup semantics (internal constants vs. user-typed strings) and different type catalogs (seed link types include directional pairs like blocks/blocked-by that aren't relevant for published item commands). A shared mapper would require mode-switching logic that adds complexity without benefit. |
| **`RemoveLinkAsync` fetches then patches** (2 API calls) | ADO REST API requires the relation array index for removal. No way to remove by type+target in one call. Fetch is necessary. |
| **`RemoveLinkAsync` uses `If-Match` with fetched revision** | The risks table identifies index-based removal as racy. The existing `SendAsync` method already supports an `ifMatch` parameter (used by `PatchAsync` with `expectedRevision.ToString()`). Passing `dto.Rev.ToString()` from the GET response ensures ADO rejects the PATCH if another user modified relations between fetch and patch, returning `409 Conflict`. |
| **Idempotent removal** | If the relation doesn't exist, `RemoveLinkAsync` returns silently. Avoids errors on retry. |
| **`link parent` aborts when parent already exists** | When `link parent` is called on an item that already has a parent, the command emits a warning ("Already has parent #X — use `twig link reparent` to change") and aborts with exit code 1. This prevents accidental overwrites and guides the user to the explicit `reparent` command, which handles the remove-old + add-new flow safely. If the specified parent is already the current parent, output a no-op message ("Already a child of #X") and return exit code 0. |
| **Resync both source and target** | Parent changes affect both the child's `ParentId` and the parent's children list in cache. |
| **`--parent` on `twig new` uses existing `MapSeedToCreatePayload`** | The infrastructure already supports parent in the POST payload. Only the CLI surface and `NewCommand` need changes. `WorkItem.ParentId` has an `init` setter, so `WithParentId()` creates a copy — no mutation needed. |
| **No new `TwigJsonContext` registrations** | `RemoveLinkAsync` reuses `List<AdoPatchOperation>` (already registered at `TwigJsonContext.Default.ListAdoPatchOperation`) and `DeserializeWorkItemAsync` (uses `TwigJsonContext.Default.AdoWorkItemResponse`). No new serializable types are introduced. |

---

## Dependencies

### External
- **ADO REST API 7.1** — Work item PATCH with `/relations/-` (add) and `/relations/{index}` (remove). Already used by `AddLinkAsync`.

### Internal
- **`AdoRestClient`** — Extend with `RemoveLinkAsync`
- **`IAdoWorkItemService`** — Extend interface with `RemoveLinkAsync`
- **`IOutputFormatter`** — Extend with `FormatWorkItemLinks`
- **`SyncCoordinator`** — Reuse existing `SyncLinksAsync` and `SyncItemAsync`

### Sequencing
- Issue 1 (Infrastructure) must complete before Issues 2-4 (commands rely on the service layer)
- Issues 2 and 3 (link commands) are implemented together after Issue 1 (same files, same PR)
- Issue 4 (`twig new --parent`) is independent and can proceed in parallel
- Issue 5 (Tests) partially overlaps with each Issue

---

## Impact Analysis

### Components Affected

| Component | Change Type | Scope |
|-----------|------------|-------|
| `IAdoWorkItemService` | Interface extension | +1 method (`RemoveLinkAsync`) |
| `AdoRestClient` | Implementation extension | +1 method (`RemoveLinkAsync`) |
| `IOutputFormatter` | Interface extension | +1 method (`FormatWorkItemLinks`) |
| `HumanOutputFormatter` | Implementation | +1 method |
| `JsonOutputFormatter` | Implementation | +1 method |
| `JsonCompactOutputFormatter` | Delegation | +1 delegation |
| `MinimalOutputFormatter` | Implementation | +1 method |
| `Program.cs` (TwigCommands) | Command registration | +6 command methods, +1 parameter on `New` |
| `CommandRegistrationModule` | DI registration | +1 service (`LinkCommand`) |
| `NewCommand` | Parameter addition | +`parent` parameter, conditional parent ID |
| `SeedFactory` | No change | `CreateUnparented` still used; parent handled by caller |
| `LinkTypeMapper` (new) | New static class | Domain layer |
| `LinkCommand` (new) | New command class | CLI layer |

### Backward Compatibility

- All changes are additive — no existing commands or behaviors are modified
- `twig new` without `--parent` behaves exactly as before
- `twig seed link/unlink/links` remain unchanged
- `IOutputFormatter` gains a new method, which is a breaking change for any external implementations, but all implementations are internal to the codebase

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ADO returns different relation URL format for on-prem vs. cloud | Low | Medium | URL matching uses `LastIndexOf('/')` for ID extraction (already proven in `AdoResponseMapper`). Test with cloud URLs. |
| Removing a link by index is racy (another user adds a link between fetch and patch) | Low | Low | ADO returns `409 Conflict` on revision mismatch; use `If-Match` header with the fetched revision. |
| `IOutputFormatter` interface change breaks compilation | Medium | Low | All four implementations are in the same project; single PR updates all. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/LinkCommand.cs` | Command class for all `twig link` subcommands |
| `src/Twig.Domain/Services/LinkTypeMapper.cs` | Bidirectional friendly name ↔ ADO relation type mapping |
| `tests/Twig.Cli.Tests/Commands/LinkCommandTests.cs` | Unit tests for `LinkCommand` |
| `tests/Twig.Domain.Tests/Services/LinkTypeMapperTests.cs` | Unit tests for `LinkTypeMapper` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientRemoveLinkTests.cs` | Tests for `RemoveLinkAsync` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add `RemoveLinkAsync` method |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Implement `RemoveLinkAsync` |
| `src/Twig/Program.cs` | Register 6 `twig link` commands + add `--parent` to `New` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `LinkCommand` singleton |
| `src/Twig/Commands/NewCommand.cs` | Accept `parent` parameter, call `seed.WithParentId(parentId)` to create copy, pass to `CreateAsync` |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add `FormatWorkItemLinks` method |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement `FormatWorkItemLinks` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement `FormatWorkItemLinks` |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Delegate `FormatWorkItemLinks` |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement `FormatWorkItemLinks` |
| `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | Add tests for `--parent` flag |

---

## ADO Work Item Structure

### Epic #1343: Work Item Link Management via twig CLI

---

### Issue 1: Link Infrastructure — `RemoveLinkAsync` and `LinkTypeMapper` (#1414)

> **Status:** ✅ **Done** — PR Group 1 merged. All code verified in codebase.

**Goal:** Establish the service-layer primitives needed by all link commands: a generalized link type mapper and the ability to remove links from ADO.

**Prerequisites:** None (foundational)

**Tasks:**

| Task | Description | Files | Effort | Status |
|------|-------------|-------|--------|--------|
| T1.1 | Create `LinkTypeMapper` static class with bidirectional mapping (friendly ↔ ADO relation types), `OrdinalIgnoreCase` lookup, and `SupportedTypes` list. *(FR-9, NFR-2)* | `src/Twig.Domain/Services/LinkTypeMapper.cs` | S | ✅ Done |
| T1.2 | Add `RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken)` to `IAdoWorkItemService` interface. *(FR-2, FR-3, FR-5)* | `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | S | ✅ Done |
| T1.3 | Implement `RemoveLinkAsync` on `AdoRestClient`: GET with `$expand=relations` to fetch current relations, find relation index by `(rel, targetUrl)`, PATCH (clean URL, no `$expand`) with `op: "remove"`, `path: "/relations/{index}"`, and `If-Match: dto.Rev.ToString()` for concurrency safety. Use `DeserializeWorkItemAsync` (private helper, not generic `DeserializeAsync<T>`). Idempotent if not found. *(FR-2, FR-3, FR-5, NFR-1, NFR-3)* | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | M | ✅ Done |
| T1.4 | Unit tests for `LinkTypeMapper`: all mappings, case insensitivity, unknown type handling, bidirectionality. *(FR-9)* | `tests/Twig.Domain.Tests/Services/LinkTypeMapperTests.cs` | S | ✅ Done |
| T1.5 | Unit tests for `RemoveLinkAsync`: mock HTTP responses, verify index calculation, idempotent not-found, `If-Match` header sent, error handling. *(FR-2, FR-5, NFR-3)* | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientRemoveLinkTests.cs` | M | ✅ Done |

**Acceptance Criteria:**
- [x] `LinkTypeMapper.TryResolve("parent")` returns `System.LinkTypes.Hierarchy-Reverse`
- [x] `LinkTypeMapper.TryResolve("RELATED")` returns `System.LinkTypes.Related` (case-insensitive)
- [x] `RemoveLinkAsync` correctly identifies and removes a relation by index
- [x] `RemoveLinkAsync` is idempotent when the relation doesn't exist
- [x] All unit tests pass
- [x] `dotnet build` with `TreatWarningsAsErrors` succeeds

---

### Issue 2: Parent Link Commands — `link parent`, `link unparent`, `link reparent` (#1415)

> **Status:** ⬜ **To Do** — No implementation code exists. ADO child Tasks are marked Done (state discrepancy — Tasks were planning/design tasks, not implementation). Prerequisite Issue 1 is satisfied.

**Goal:** Enable users to set, remove, and change the parent of any published work item from the CLI.

**Prerequisites:** Issue 1 ✅ (needs `RemoveLinkAsync` for unparent/reparent)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T2.1 | Create `LinkCommand` class with DI constructor and `ParentAsync` method: resolve active item, check if item already has a parent (abort with warning + `reparent` hint if so; no-op if same parent), validate parent ID exists, call `AddLinkAsync` with `Hierarchy-Reverse`, resync both items. *(FR-1, FR-8, FR-10)* | `src/Twig/Commands/LinkCommand.cs` | M |
| T2.2 | Add `UnparentAsync` method to `LinkCommand`: resolve active item, verify has parent, call `RemoveLinkAsync` with `Hierarchy-Reverse`, resync. *(FR-2, FR-8, FR-10)* | `src/Twig/Commands/LinkCommand.cs` | M |
| T2.3 | Add `ReparentAsync` method to `LinkCommand`: resolve active item, remove old parent via `RemoveLinkAsync` + add new parent via `AddLinkAsync` (two HTTP calls, not one PATCH body), resync child + both old and new parents. *(FR-3, FR-8, FR-10)* | `src/Twig/Commands/LinkCommand.cs` | M |
| T2.4 | Register `LinkCommand` in `CommandRegistrationModule` and wire `link parent`, `link unparent`, `link reparent` in `Program.cs` (TwigCommands). *(FR-1, FR-2, FR-3)* | `src/Twig/Program.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S |
| T2.5 | Unit tests for `ParentAsync`, `UnparentAsync`, `ReparentAsync`: success paths, already-has-parent abort, same-parent no-op, validation errors, resync verification, output format testing (human/json/minimal). Include integration-style flow tests: create item → link parent → verify tree → unlink → verify tree (mock ADO, full command flow). *(FR-1, FR-2, FR-3, FR-8, FR-10)* | `tests/Twig.Cli.Tests/Commands/LinkCommandTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig link parent 1338` (with #1339 active) adds parent link and resyncs
- [ ] `twig link parent 1338` when item already has a different parent aborts with warning and `reparent` hint
- [ ] `twig link parent 1338` when item is already a child of #1338 outputs no-op message
- [ ] `twig link unparent` removes parent link and resyncs
- [ ] `twig link reparent 1340` removes old parent and sets new parent
- [ ] Commands error gracefully when no active item is set
- [ ] Commands error gracefully when target item doesn't exist
- [ ] `twig tree` reflects parent changes immediately after command
- [ ] All three output formats work correctly

---

### Issue 3: Generic Link Commands — `link add`, `link remove`, `link list` (#1416)

> **Status:** ⬜ **To Do** — No implementation code exists. No `FormatWorkItemLinks` method in any formatter. ADO child Tasks are marked Done (state discrepancy). Prerequisite Issue 1 is satisfied.

**Goal:** Enable users to add, remove, and view non-hierarchy links (related, predecessor, successor) on published work items.

**Prerequisites:** Issue 1 ✅ (needs `RemoveLinkAsync` and `LinkTypeMapper`)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T3.1 | Add `AddAsync` method to `LinkCommand`: resolve active item, resolve link type via `LinkTypeMapper`, call `AddLinkAsync`, resync. *(FR-4, FR-8, FR-9)* | `src/Twig/Commands/LinkCommand.cs` | S |
| T3.2 | Add `RemoveAsync` method to `LinkCommand`: resolve active item, resolve link type, call `RemoveLinkAsync`, resync. *(FR-5, FR-8, FR-9)* | `src/Twig/Commands/LinkCommand.cs` | S |
| T3.3 | Add `ListAsync` method to `LinkCommand`: resolve item (active or specified), call `SyncCoordinator.SyncLinksAsync` to refresh, read `ParentId` from returned `WorkItem`, fetch parent via `IWorkItemRepository.GetByIdAsync` (cache-first, ADO fallback), gather non-hierarchy links from `IWorkItemLinkRepository`, format output. *(FR-6, FR-8, FR-10)* | `src/Twig/Commands/LinkCommand.cs` | M |
| T3.4 | Add `FormatWorkItemLinks` to `IOutputFormatter` and implement in all four formatters (Human, JSON, JsonCompact, Minimal). Include parent display with title and non-hierarchy links. *(FR-6, FR-10)* | `src/Twig/Formatters/IOutputFormatter.cs`, `HumanOutputFormatter.cs`, `JsonOutputFormatter.cs`, `JsonCompactOutputFormatter.cs`, `MinimalOutputFormatter.cs` | M |
| T3.5 | Wire `link add`, `link remove`, `link list` in `Program.cs` (TwigCommands). *(FR-4, FR-5, FR-6)* | `src/Twig/Program.cs` | S |
| T3.6 | Unit tests for `AddAsync`, `RemoveAsync`, `ListAsync`: all link types, invalid types, empty links, format testing. *(FR-4, FR-5, FR-6, FR-9, FR-10)* | `tests/Twig.Cli.Tests/Commands/LinkCommandTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig link add 1340 --type related` adds a Related link
- [ ] `twig link remove 1340 --type related` removes the link
- [ ] `twig link list` shows all links (parent + non-hierarchy) with titles where available
- [ ] Invalid link types produce a clear error with list of valid types
- [ ] `link list` with explicit ID works without setting active item
- [ ] All three output formats work correctly

---

### Issue 4: `twig new --parent` Enhancement (#1417)

> **Status:** ⬜ **To Do** — No implementation code exists. `NewCommand.ExecuteAsync` has no `parent` parameter. ADO child Tasks are marked Done (state discrepancy).

**Goal:** Allow `twig new` to create work items with a parent in a single ADO API call, eliminating the need to manually reparent after creation.

**Prerequisites:** None (independent of Issues 1-3; uses existing `MapSeedToCreatePayload`)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T4.1 | Add `int? parent` parameter to `NewCommand.ExecuteAsync`. When provided, create seed via `SeedFactory.CreateUnparented()` then call `seed.WithParentId(parentId)` to produce a copy with the parent set (`WorkItem.ParentId` has an `init` setter and cannot be mutated post-construction — `WithParentId()` returns a new instance). Pass the copy to `adoService.CreateAsync(seedWithParent, ct)`. `MapSeedToCreatePayload` already includes the parent relation in the POST when `ParentId` is set. *(FR-7)* | `src/Twig/Commands/NewCommand.cs` | S |
| T4.2 | Wire `--parent` parameter in `Program.cs` for the `New` command method. *(FR-7)* | `src/Twig/Program.cs` | S |
| T4.3 | Unit tests for `twig new --parent`: parent link included in creation, fetch-back shows correct parent, `twig new` without `--parent` unchanged (regression), invalid parent ID handling. *(FR-7)* | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | S |

**Acceptance Criteria:**
- [ ] `twig new --title "My Issue" --type Issue --parent 1338` creates #1339 with parent #1338
- [ ] The seed is created via `CreateUnparented()` then `WithParentId(parentId)` — no mutation of init-only property
- [ ] `twig tree` shows the new item under the parent immediately
- [ ] `twig new` without `--parent` behaves exactly as before (no regression)
- [ ] Invalid parent IDs produce clear error messages

---

## PR Groups

### PR Group 1: Link Infrastructure

> **Status:** ✅ **Merged**

**Tasks:** T1.1, T1.2, T1.3, T1.4, T1.5  
**Classification:** Deep (few files, complex logic in `RemoveLinkAsync`)  
**Estimated LoC:** ~350  
**Files:** ~5  
**Predecessor:** None

**Rationale:** Foundational layer. `RemoveLinkAsync` has the most complex logic (fetch → find index → patch). Must merge before command PRs.

**Verified artifacts:**
- `src/Twig.Domain/Services/LinkTypeMapper.cs` — bidirectional mapper with `TryResolve`, `Resolve`, `ToFriendlyName`, `TryToFriendlyName`
- `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs:21` — `RemoveLinkAsync` on interface
- `src/Twig.Infrastructure/Ado/AdoRestClient.cs:171` — `RemoveLinkAsync` implementation with GET→find-index→PATCH pattern, `If-Match` concurrency, idempotent not-found
- `tests/Twig.Domain.Tests/Services/LinkTypeMapperTests.cs` — mapping tests
- `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientRemoveLinkTests.cs` — HTTP-level tests

---

### PR Group 2: All Link Commands + Formatters

> **Status:** ✅ **Done** — Merged via PR #18

**Tasks:** T2.1, T2.2, T2.3, T2.4, T2.5, T3.1, T3.2, T3.3, T3.4, T3.5, T3.6  
**Classification:** Deep+Wide (core command logic + formatter changes across multiple files)  
**Estimated LoC:** ~1150  
**Files:** ~10  
**Predecessor:** PR Group 1 ✅

**Rationale:** All six `twig link` subcommands and their formatters in one PR. Issues 2 and 3 both write to `LinkCommand.cs` and `Program.cs` — splitting them into "parallel" PRs for a solo project only creates merge coordination overhead. The parent commands (`parent`, `unparent`, `reparent`) and the generic commands (`add`, `remove`, `list`) share the same class and test infrastructure; delivering them together is simpler.

---

### PR Group 3: `twig new --parent`

> **Status:** ⬜ **Ready** — no prerequisites

**Tasks:** T4.1, T4.2, T4.3  
**Classification:** Wide (touches multiple files, mostly simple changes)  
**Estimated LoC:** ~150  
**Files:** ~3  
**Predecessor:** None (independent; uses existing `MapSeedToCreatePayload` infrastructure)

**Rationale:** The `--parent` flag on `twig new` is fully independent — it uses existing infrastructure and has no runtime dependency on the link commands. Can proceed in parallel with PR Group 2.

---

### PR Group Execution Order

```
PR Group 1 (Infrastructure) ✅ MERGED
    └──▶ PR Group 2 (All Link Commands + Formatters)  ✅ MERGED (#18)

PR Group 3 (new --parent)                             ⬜ READY (independent)
```

> **Note:** Both remaining PR groups are unblocked and can proceed immediately. PR Group 3 is fully independent.

---

## Open Questions

None. All open questions from prior revisions have been resolved as design decisions (see Revision 3).

---

## Revision History

| Revision | Date | Changes |
|----------|------|---------|
| 1 | 2026-04-05 | Initial draft |
| 2 | 2026-04-05 | Removed Issue 5 (hints + integration as separate issue): T5.1 (hints) not in requirements, T5.2 folded into T2.5, T5.3 is ceremony. PR Group 4 made independent (no dependency on PRs 2+3). |
| 3 | 2026-04-05 | **Review feedback (tech=88, read=88).** Fixed: (1) SeedLinkPromoter call-site line 44→54. (2) `RemoveLinkAsync` pseudo-code: use `DeserializeWorkItemAsync` helper, separate GET/PATCH URLs, add `If-Match: dto.Rev.ToString()` for concurrency. (3) T4.1/T4.3 updated to use `WithParentId()` (init-only `ParentId` setter); old T4.3 fork removed and T4.4 renumbered. (4) Explicit note that no new `TwigJsonContext` types needed. (5) `FormatWorkItemLinks` parentItem fetch responsibility specified (cache-first in `ListAsync`). (6) OQ#1 resolved as design decision: `link parent` aborts with warning+hint when parent exists. (7) OQ#3 wording clarified to "two JSON Patch operations in a single PATCH request body"; resolved as two HTTP calls with recovery. (8) Added reparent data flow trace with error recovery path. (9) All task rows annotated with FR/NFR IDs. (10) `LinkTypeMapper` rationale expanded: `SeedLinkTypeMapper` uses `Ordinal` comparison + seed-specific constants vs. new mapper's `OrdinalIgnoreCase` + user-facing names. |
| 4 | 2026-04-05 | **Plan-level reduction review.** (1) Removed reparent error-recovery path: not in requirements, user can recover with `twig link parent <old-id>` — removed from reparent note, T2.3 description, T2.5 test list, and Issue 2 acceptance criteria. (2) Removed Open Questions section: both questions were already resolved as design decisions in Revision 3. |
| 5 | 2026-04-06 | **Codebase verification audit.** (1) Verified PR Group 1 is fully merged — `LinkTypeMapper`, `RemoveLinkAsync`, and all tests confirmed in codebase. (2) Verified PR Groups 2–4 have NO implementation code — `LinkCommand.cs` does not exist, `FormatWorkItemLinks` is absent from all formatters, `NewCommand` has no `parent` parameter. (3) Flagged ADO state discrepancy: Issues #1415–#1417 have child Tasks marked Done in ADO, but no corresponding code exists — these were planning/design tasks, not implementation. (4) Updated all Issue and PR Group sections with verified status badges. (5) Added verified artifacts list to PR Group 1. (6) All three remaining PR Groups are unblocked and ready for implementation. |
| 6 | 2026-04-06 | **Plan-level reduction review.** Merged PR Groups 2 and 3 into a single PR Group 2 ("All Link Commands + Formatters"): both wrote to the same files (`LinkCommand.cs`, `Program.cs`) and splitting them only adds merge coordination overhead for a solo project. Renumbered PR Group 4 → PR Group 3. Updated status header (1/4 → 1/3), sequencing note, and execution order diagram. |
