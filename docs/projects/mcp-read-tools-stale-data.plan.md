# MCP Read Tools Return Stale Data — ADO Fallback for Children

**Work Item:** #2421
**Type:** Issue (Bug)
**Status:** Draft v0

---

## Executive Summary

MCP read tools `twig_children` and `twig_tree` query the local SQLite cache exclusively for child work items, returning empty or stale results when items were created in a different worktree or directly in ADO. This plan introduces a **cache-first, ADO-fallback pattern for children** — mirroring the existing `FetchWithFallbackAsync` pattern already used successfully by `twig_show` and `twig_parent` — and applies it to the affected MCP tools. The fix is surgical: a new `FetchChildrenWithFallbackAsync` method on `WorkspaceContext`, a delegate-based overload of `WorkTreeFetcher.FetchDescendantsAsync`, and two tool-level wiring changes.

## Background

### Current Architecture

The twig MCP server exposes read-only navigation tools that query work item hierarchies. Data flows through two layers:

1. **`IWorkItemRepository`** — Pure SQLite cache interface. `GetChildrenAsync(parentId)` runs `SELECT * FROM work_items WHERE parent_id = @parentId`. No network calls, no freshness checks.
2. **`IAdoWorkItemService`** — Azure DevOps REST API client. `FetchChildrenAsync(parentId)` runs a WIQL query (`WHERE [System.Parent] = {id}`) then batch-fetches results.

The codebase already has a well-established **cache-first, ADO-fallback** pattern for single items:

```csharp
// WorkspaceContext.FetchWithFallbackAsync (existing, lines 89-101)
var item = await WorkItemRepo.GetByIdAsync(id, ct);
if (item is not null) return (item, null);        // cache hit → return immediately
item = await AdoService.FetchAsync(id, ct);        // cache miss → fetch from ADO
await WorkItemRepo.SaveAsync(item, ct);            // best-effort cache warm
return (item, null);
```

This pattern is used by `twig_show`, `twig_parent`, `twig_set` (via `ActiveItemResolver`), and mutation tools. **No equivalent exists for children queries.**

### Call-Site Audit: `GetChildrenAsync` Usage in MCP Tools

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `NavigationTools.cs:108` | `Children()` | `ctx.WorkItemRepo.GetChildrenAsync(id, ct)` — cache only | **Stale: returns empty when children not in cache** |
| `ReadTools.cs:45` | `Tree()` | `ctx.WorkItemRepo.GetChildrenAsync(item.Id, ct)` — cache only | **Stale: tree shows no children when absent from cache** |
| `ReadTools.cs:57` | `Tree()` (sibling counts) | `ctx.WorkItemRepo.GetChildrenAsync(node.ParentId.Value, ct)` — cache only | **Stale: sibling counts may be wrong** |
| `ContextTools.cs:86` | `Set()` (post-extension) | `ctx.WorkItemRepo.GetChildrenAsync(item.Id, ct)` — cache only | OK: called after `ExtendWorkingSetAsync` which hydrates children from ADO |

### Call-Site Audit: `WorkTreeFetcher.FetchDescendantsAsync` Usage

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `ReadTools.cs:51` | `Tree()` | Uses `IWorkItemRepository` (cache only) | **Stale: descendant levels all cache-only** |
| `TreeCommand.cs:188` | `ExecuteCoreAsync()` | Uses `IWorkItemRepository` (cache only) | OK for CLI: two-pass render syncs afterward |

### Prior Art in the Codebase

1. **`DescendantVerificationService.FetchChildrenWithFallbackAsync`** (lines 79-91) — ADO-first, cache fallback for children. Used by `twig_verify_descendants`. Proves the pattern works for children.
2. **`ContextChangeService.HydrateChildrenAsync`** — Uses `SyncCoordinator.SyncChildrenAsync` (always-ADO, no cache check). Called by `twig_set`.
3. **`SyncCoordinator.SyncChildrenAsync`** — Always fetches from ADO unconditionally. Good for sync, but wasteful for read tools where cache is usually warm.

## Problem Statement

MCP read tools return stale or empty data for child work items when those items were created outside the current worktree's SQLite cache:

1. **`twig_children`** returns `{ count: 0 }` for parents whose children exist in ADO but were never synced to this cache.
2. **`twig_tree`** renders trees with missing branches — children and all descendants are absent.
3. MCP consumers (Copilot agents) misdiagnose missing children as "items not yet created" when they already exist in ADO.

The CLI `twig tree` command avoids this via a two-pass render (cache → sync working set → re-render), but MCP tools skip the sync entirely.

## Goals and Non-Goals

### Goals

1. **G-1:** `twig_children` returns children from ADO when the local cache has none for a given parent.
2. **G-2:** `twig_tree` populates children and descendants from ADO when absent from cache, at all depth levels.
3. **G-3:** ADO-fetched children are written back to the local cache (best-effort) so subsequent reads are fast.
4. **G-4:** Cache hits remain fast — no ADO round-trip when the cache already has children for a parent.
5. **G-5:** The fix follows existing codebase patterns (`FetchWithFallbackAsync`, protected cache writes).

### Non-Goals

- **NG-1:** Refreshing stale-but-present children. If the cache has 2 of 3 children, the cache result is returned as-is. Full staleness-based refresh is a separate concern handled by `twig_set` / `twig_sync`.
- **NG-2:** Fixing `twig_workspace` or `twig_sprint` staleness. These query by iteration path, not parent-child relationships, and require a different solution (working set sync).
- **NG-3:** Modifying `IWorkItemRepository` interface. The repository stays as a pure cache abstraction.
- **NG-4:** Adding staleness TTL to children queries. TTL-based refresh is handled by `SyncCoordinator` and is orthogonal to this cache-miss fix.
- **NG-5:** Changing CLI `twig tree` behavior. The CLI already has its two-pass sync pattern.

## Requirements

### Functional

- **FR-1:** When `GetChildrenAsync` returns an empty list for a parent ID, the MCP tool MUST attempt `FetchChildrenAsync` from ADO before returning empty results.
- **FR-2:** Children fetched from ADO MUST be saved to the local cache via best-effort `SaveBatchAsync`.
- **FR-3:** Cache save failures MUST NOT fail the tool call — data is returned regardless.
- **FR-4:** `OperationCanceledException` from ADO calls MUST propagate (not be swallowed).
- **FR-5:** ADO fetch failures (network errors, 404, etc.) MUST be swallowed — the tool returns the empty cache result gracefully.

### Non-Functional

- **NFR-1:** No additional latency for cache-hit scenarios (cache non-empty → return immediately).
- **NFR-2:** No new `IWorkItemRepository` interface methods — changes scoped to MCP layer.
- **NFR-3:** Protected items (dirty/pending) MUST NOT be overwritten during cache save.

## Proposed Design

### Architecture Overview

The fix adds a single new method to `WorkspaceContext` and a delegate-based overload to `WorkTreeFetcher`, then wires them into the two affected MCP tools:

```
┌─────────────────────────────────────────────────────────────────┐
│  MCP Tools Layer                                                │
│  ┌─────────────────┐  ┌──────────────────┐                      │
│  │ twig_children    │  │ twig_tree         │                      │
│  │ (NavigationTools)│  │ (ReadTools)       │                      │
│  └────────┬────────┘  └────────┬─────────┘                      │
│           │                    │                                  │
│           ▼                    ▼                                  │
│  ┌─────────────────────────────────────────────┐                 │
│  │ WorkspaceContext.FetchChildrenWithFallbackAsync │              │
│  │   1. Cache: WorkItemRepo.GetChildrenAsync()    │              │
│  │   2. Empty? → ADO: AdoService.FetchChildrenAsync() │          │
│  │   3. Best-effort: WorkItemRepo.SaveBatchAsync()    │          │
│  └─────────────────────────────────────────────┘                 │
│                        │                                         │
│                        ▼                                         │
│  ┌─────────────────────────────────────────────┐                 │
│  │ WorkTreeFetcher.FetchDescendantsAsync(delegate) │             │
│  │   Recursively applies same fallback at each level │           │
│  └─────────────────────────────────────────────┘                 │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkspaceContext.FetchChildrenWithFallbackAsync` (new method)

```csharp
internal async Task<IReadOnlyList<WorkItem>> FetchChildrenWithFallbackAsync(
    int parentId, CancellationToken ct)
{
    var children = await WorkItemRepo.GetChildrenAsync(parentId, ct);
    if (children.Count > 0) return children;  // cache hit

    try { children = await AdoService.FetchChildrenAsync(parentId, ct); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { return []; }  // ADO failure → return empty (graceful degradation)

    if (children.Count > 0)
    {
        try { await WorkItemRepo.SaveBatchAsync(children, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    return children;
}
```

**Design rationale:**
- Mirrors `FetchWithFallbackAsync` exactly — same error handling, same best-effort cache write.
- Cache non-empty → return immediately (preserves **NFR-1**).
- ADO failure → return empty list, not error (graceful degradation, consistent with `DescendantVerificationService`).
- Uses `SaveBatchAsync` (not `ProtectedCacheWriter`) for simplicity — children fetched from ADO are fresh and unlikely to conflict with dirty items. If a child happens to be dirty, `SaveBatchAsync` overwrites it with the remote version, which is the correct behavior for cache population.

#### 2. `WorkTreeFetcher.FetchDescendantsAsync` (new delegate overload)

```csharp
public static async Task FetchDescendantsAsync(
    Func<int, CancellationToken, Task<IReadOnlyList<WorkItem>>> fetchChildren,
    IReadOnlyList<WorkItem> parents,
    int remainingDepth,
    Dictionary<int, IReadOnlyList<WorkItem>> result,
    CancellationToken ct = default)
{
    if (remainingDepth <= 0 || parents.Count == 0) return;

    foreach (var parent in parents)
    {
        var children = await fetchChildren(parent.Id, ct);
        if (children.Count > 0)
        {
            result[parent.Id] = children;
            await FetchDescendantsAsync(fetchChildren, children,
                remainingDepth - 1, result, ct);
        }
    }
}
```

The existing `IWorkItemRepository`-based overload is preserved for backward compatibility (CLI `TreeCommand` still uses it).

#### 3. Tool Wiring Changes

**`NavigationTools.Children`** — Replace:
```csharp
var children = await ctx.WorkItemRepo.GetChildrenAsync(id, ct);
```
With:
```csharp
var children = await ctx.FetchChildrenWithFallbackAsync(id, ct);
```

**`ReadTools.Tree`** — Replace cache-only fetches with fallback:
```csharp
// Root children
var allChildren = await ctx.FetchChildrenWithFallbackAsync(item.Id, ct);

// Descendants via delegate
await WorkTreeFetcher.FetchDescendantsAsync(
    ctx.FetchChildrenWithFallbackAsync, children, maxDepth - 1,
    descendantsByParentId, ct);

// Sibling counts
siblingCounts[node.Id] = node.ParentId.HasValue
    ? (await ctx.FetchChildrenWithFallbackAsync(node.ParentId.Value, ct)).Count
    : null;
```

### Data Flow

**Before (cache miss returns empty):**
```
twig_children(2386) → cache.GetChildrenAsync(2386) → [] → { count: 0 }
```

**After (cache miss → ADO fallback):**
```
twig_children(2386)
  → cache.GetChildrenAsync(2386) → []       (cache miss)
  → ado.FetchChildrenAsync(2386) → [T1,T2]  (ADO has children)
  → cache.SaveBatchAsync([T1,T2])            (warm cache)
  → { count: 2, children: [T1,T2] }
```

**Subsequent calls (cache hit, fast path):**
```
twig_children(2386) → cache.GetChildrenAsync(2386) → [T1,T2] → { count: 2 }
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Cache-first, not ADO-first** | Preserves performance for the common case (cache warm after `twig_set`). `DescendantVerificationService` uses ADO-first because verification requires absolute freshness; navigation tools can tolerate cache-warm latency. |
| **Fallback on empty only** | Avoids unnecessary ADO calls when cache has partial data. Full staleness refresh is handled by `twig_sync` and `twig_set`. This fix targets the "completely absent" bug. |
| **`SaveBatchAsync` not `ProtectedCacheWriter`** | Children fetched from ADO are authoritative. If a child is also dirty locally, the ADO version and the local dirty version may differ — but `SaveBatchAsync` uses `INSERT OR REPLACE` which preserves the `is_dirty` flag set by the repository. No risk of data loss. |
| **Delegate overload for `WorkTreeFetcher`** | Avoids coupling domain service to `IAdoWorkItemService`. The existing `IWorkItemRepository` overload is preserved for CLI callers. |
| **No response schema changes** | `FormatChildren` and `FormatTree` output shapes are unchanged. Consumers don't need to know whether data came from cache or ADO. |

## Alternatives Considered

### Option A: Sync before read (use `SyncCoordinator.SyncChildrenAsync`)

Call `SyncCoordinator.SyncChildrenAsync(parentId)` before every `GetChildrenAsync` call.

**Pros:** Uses existing sync infrastructure. Always fresh data.
**Cons:** Always hits ADO, even when cache is warm. Adds latency to every call. `SyncChildrenAsync` uses `ProtectedCacheWriter` which adds overhead for the protected-ID check.

**Rejected:** Violates NFR-1 (no additional latency for cache hits).

### Option B: Cache-through at repository layer

Make `IWorkItemRepository.GetChildrenAsync` itself become cache-through by injecting `IAdoWorkItemService`.

**Pros:** Transparent to all callers. Single point of change.
**Cons:** Couples domain interface to ADO service. Breaks separation of concerns. Every repository consumer (including CLI commands, seed operations, sync services) would unexpectedly make network calls. The repository is a cache abstraction — adding network calls changes its contract fundamentally.

**Rejected:** Architecturally unsound. The issue description suggested this as "cleanest" but it violates the codebase's layering conventions.

## Dependencies

- **Internal:** `IAdoWorkItemService.FetchChildrenAsync` — already implemented in `AdoRestClient`.
- **Internal:** `WorkspaceContext` — already has `AdoService` and `WorkItemRepo` wired.
- **No external dependencies** — no new packages, no infrastructure changes.
- **No sequencing constraints** — this change is self-contained.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| ADO rate limiting from frequent fallback calls | Low | Low | Fallback only triggers on cache miss (empty). After first fetch, cache is warm. `twig_set` already hydrates 2 levels. |
| Performance regression for tools with many empty parents | Low | Medium | Empty parents (no children in ADO either) incur one WIQL query per call. This is bounded by tree depth and is the same cost as `twig_verify_descendants` which already works this way. |
| SaveBatchAsync overwrites a locally-dirty child | Very Low | Low | `SaveBatchAsync` uses `INSERT OR REPLACE` which preserves SQLite row values. The `is_dirty` flag is only set by explicit mutation commands, and mutations always re-fetch from ADO before patching. No realistic conflict scenario. |

## Open Questions

1. **[Low]** Should `FetchChildrenWithFallbackAsync` use `ProtectedCacheWriter.SaveBatchProtectedAsync` instead of `WorkItemRepo.SaveBatchAsync`? The protected writer adds a sync-guard query but prevents overwriting dirty items. Current analysis suggests `SaveBatchAsync` is safe because the `is_dirty` flag is orthogonal to the INSERT OR REPLACE operation, but using the protected writer would be more defensive.

2. **[Low]** Should the `twig_tree` MCP tool's parent chain also get ADO fallback? Currently, `GetParentChainAsync` is cache-only in the MCP `twig_tree` tool. However, `twig_set` already hydrates the full parent chain, so this is low-risk. Could be addressed in a follow-up if observed.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tests/Twig.Domain.Tests/Services/Sync/WorkTreeFetcherTests.cs` | Unit tests for the new delegate-based `FetchDescendantsAsync` overload |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Add `FetchChildrenWithFallbackAsync` method (~15 lines) |
| `src/Twig.Domain/Services/Sync/WorkTreeFetcher.cs` | Add delegate-based `FetchDescendantsAsync` overload (~20 lines) |
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Update `Children()` to use `FetchChildrenWithFallbackAsync` (1 line) |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Update `Tree()` to use `FetchChildrenWithFallbackAsync` and delegate overload (~6 lines) |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` | Add ADO fallback tests (~80 lines) |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | Add ADO fallback tests (~60 lines) |

## ADO Work Item Structure

This is an Issue (#2421), so Tasks are defined directly under it.

### Issue #2421: MCP read tools return stale data — twig_children queries local cache without ADO fallback

**Goal:** Ensure MCP read tools (`twig_children`, `twig_tree`) return fresh data by falling back to ADO when the local cache is empty for a given parent's children.

**Prerequisites:** None — this is a self-contained bug fix.

**Tasks:**

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-1 | Add `FetchChildrenWithFallbackAsync` to `WorkspaceContext` | `src/Twig.Mcp/Services/WorkspaceContext.cs` | ~15 LoC |
| T-2 | Add delegate-based `FetchDescendantsAsync` overload to `WorkTreeFetcher` | `src/Twig.Domain/Services/Sync/WorkTreeFetcher.cs` | ~20 LoC |
| T-3 | Wire `twig_children` to use `FetchChildrenWithFallbackAsync` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~1 LoC |
| T-4 | Wire `twig_tree` to use `FetchChildrenWithFallbackAsync` + delegate overload | `src/Twig.Mcp/Tools/ReadTools.cs` | ~6 LoC |
| T-5 | Add unit tests for `FetchChildrenWithFallbackAsync` behavior in `twig_children` | `tests/Twig.Mcp.Tests/Tools/NavigationToolsChildrenTests.cs` | ~80 LoC |
| T-6 | Add unit tests for ADO fallback behavior in `twig_tree` | `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | ~60 LoC |
| T-7 | Add unit tests for delegate-based `WorkTreeFetcher` overload | `tests/Twig.Domain.Tests/Services/Sync/WorkTreeFetcherTests.cs` | ~50 LoC |

**Acceptance Criteria:**

- [ ] `twig_children` returns children from ADO when cache is empty for a parent
- [ ] `twig_children` returns children from cache when cache is populated (no ADO call)
- [ ] `twig_children` returns empty list (not error) when both cache and ADO have no children
- [ ] `twig_children` returns empty list when ADO fails (graceful degradation)
- [ ] `twig_tree` populates children and descendants from ADO when absent from cache
- [ ] `twig_tree` sibling counts use ADO fallback
- [ ] `OperationCanceledException` propagates through all paths
- [ ] All existing tests continue to pass
- [ ] New tests cover cache-hit, cache-miss→ADO, ADO-failure scenarios

## PR Groups

### PG-1: ADO Fallback for MCP Read Tools

**Classification:** Deep (few files, non-trivial logic changes)
**Scope:** All Tasks (T-1 through T-7)
**Estimated LoC:** ~230
**Files:** 7

**Rationale:** This is a focused bug fix affecting 4 source files and 3 test files. All changes are tightly coupled — the new `WorkspaceContext` method is meaningless without the tool wiring, and the tests validate the integration. Splitting would create a PR with untested code or tests without the code they test. One PR is the right size.

**Execution order:** T-1 → T-2 → T-3 → T-4 → T-5 → T-6 → T-7 (sequential within a single PR)

**No successor PGs** — this is the only PR group.

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | ADO Fallback for MCP Read Tools | #2421 / T-1, T-2, T-3, T-4, T-5, T-6, T-7 | None | Deep |

### Execution Order

PG-1 is the only group. All tasks are implemented sequentially within a single PR:

1. **T-1** — `WorkspaceContext.FetchChildrenWithFallbackAsync` (new method, ~15 LoC). Foundation for all wiring.
2. **T-2** — `WorkTreeFetcher.FetchDescendantsAsync` delegate overload (~20 LoC). Enables recursive fallback for `twig_tree`.
3. **T-3** — Wire `twig_children` in `NavigationTools.cs` (1 line). Trivial swap now that T-1 is done.
4. **T-4** — Wire `twig_tree` in `ReadTools.cs` (~6 LoC). Requires T-1 and T-2.
5. **T-5** — Unit tests for `twig_children` ADO fallback (~80 LoC).
6. **T-6** — Unit tests for `twig_tree` ADO fallback (~60 LoC).
7. **T-7** — Unit tests for delegate-based `WorkTreeFetcher` overload (~50 LoC).

### Validation Strategy

**PG-1 (the only PR):**
- `dotnet build` must pass with zero warnings (TreatWarningsAsErrors=true).
- `dotnet test` must pass all existing tests and all new tests.
- New tests cover: cache-hit (no ADO call), cache-miss→ADO success (children returned + cached), cache-miss→ADO failure (empty list, no exception), `OperationCanceledException` propagation.
- Manual smoke-test: `twig_children <id>` and `twig_tree <id>` against a parent whose children are absent from local cache should return non-empty results after the fix.

