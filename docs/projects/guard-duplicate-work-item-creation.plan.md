# Guard Against Duplicate Work Item Creation Across SDLC Retries

| Field | Value |
|---|---|
| **Issue** | #1891 — Guard against duplicate work item creation across SDLC retries |
| **Status** | 📋 Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

When the SDLC conductor workflow retries — due to agent failures, network
interruptions, or human-in-the-loop revisions — the `twig_new` MCP tool can
create duplicate work items in Azure DevOps because it has no idempotency
guard. This plan introduces a **title+type+parent deduplication check** into
the `twig_new` tool and a complementary `twig_find_or_create` convenience tool
that encapsulates the check-then-create pattern. The approach queries ADO for
existing children matching the proposed title/type before creating, returns the
existing item when found, and only creates when no match exists. This is a
targeted, low-risk change that adds ~400 LoC across domain services, MCP tools,
and tests.

---

## Background

### Current Architecture

Twig exposes work item creation through three code paths:

| Path | File | ADO Interaction | Idempotency |
|------|------|----------------|-------------|
| `twig_new` (MCP) | `src/Twig.Mcp/Tools/CreationTools.cs` | Immediate `CreateAsync` | ❌ None |
| `twig new` (CLI) | `src/Twig/Commands/NewCommand.cs` | Immediate `CreateAsync` | ❌ None |
| `twig seed new` → `seed publish` | `SeedNewCommand.cs` → `SeedPublishOrchestrator.cs` | Deferred (local then publish) | ✅ Positive-ID skip |

The MCP `twig_new` tool is the primary creation vector during SDLC conductor
runs. It calls `SeedFactory.Create()` / `CreateUnparented()` to build a local
seed, then immediately calls `IAdoWorkItemService.CreateAsync()` which POSTs to
the ADO REST API. **There is no check for existing items with the same title,
type, and parent before creating.**

### SDLC Workflow Retry Scenarios

The twig-sdlc conductor workflow proceeds through 6 phases:
1. **Intake** — reads/creates the Epic
2. **Planning** — architect + reviewers produce a plan (can loop)
3. **Work Tree Seeding** — creates Issues/Tasks via `twig seed new` + `twig seed publish`
4. **Implementation** — task_manager agents implement code (can loop)
5. **Close-out** — transitions to Done
6. **Closeout Filing** — tags observations

Retry/duplicate risk occurs when:
- **Phase 3 (Seeding) partially completes** then the workflow retries — some
  Issues/Tasks exist in ADO, others don't. Re-running Phase 3 creates duplicates.
- **Phase 4 (Implementation) fails** and the workflow re-enters Phase 3 — the
  seeding agent creates the same work items again.
- **Agent tool calls fail transiently** — the conductor retries the tool call,
  and `twig_new` creates a second item with the same title.
- **Human gate rejects and loops back** — the planning phase revises and
  re-seeds, creating duplicates of previously-seeded items.

### Existing Idempotency Mechanisms

| Mechanism | Scope | Gap |
|-----------|-------|-----|
| Seed publish positive-ID skip | `SeedPublishOrchestrator` | Only guards republish of already-published seeds |
| `plan-ado-map.json` check | `seed-from-plan.ps1` | PowerShell-only, not available to MCP agents |
| `ConflictRetryHelper` | PATCH operations | Only for updates, not creation |
| `publish_id_map` table | SQLite | Maps seed→ADO IDs, not title-based dedup |

### Call-Site Audit: `IAdoWorkItemService.CreateAsync`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig.Mcp/Tools/CreationTools.cs` | `New()` | Direct create, no dedup | **Primary target** — add dedup check |
| `src/Twig/Commands/NewCommand.cs` | `ExecuteAsync()` | Direct create, no dedup | Secondary — CLI users have manual control |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | `PublishAsync()` | Create from local seed | Has positive-ID guard, lower risk |

---

## Problem Statement

The `twig_new` MCP tool — the primary work item creation path for SDLC conductor
workflows — has no idempotency guard. When a conductor workflow retries (due to
agent failure, network issues, or human-gate loop-back), the tool blindly creates
duplicate work items in ADO. This leads to:

1. **Orphaned duplicates** in ADO that require manual cleanup
2. **Confused agents** that may pick up duplicate items and produce conflicting work
3. **Wasted ADO capacity** from accumulating phantom work items
4. **User friction** from having to reconcile duplicates after every failed run

The problem is especially acute during Phase 3 (Work Tree Seeding) where a single
SDLC run may create 5–15 work items in rapid succession. A retry at this stage
can double the work item count.

---

## Goals and Non-Goals

### Goals

1. **G1**: `twig_new` with a `parentId` checks for existing children with matching
   title and type before creating, returning the existing item if found.
2. **G2**: A new `twig_find_or_create` MCP tool provides explicit find-or-create
   semantics for agent workflows that want guaranteed idempotency.
3. **G3**: Deduplication logic is testable in isolation (domain service, not
   embedded in tool method).
4. **G4**: No breaking changes to existing `twig_new` behavior — dedup is opt-in
   via a new `skipDuplicateCheck` parameter (default: `false` for MCP, preserving
   current CLI behavior).

### Non-Goals

- **NG1**: Cross-project deduplication — scope is within a single parent's children.
- **NG2**: Fuzzy title matching — exact case-insensitive string comparison only.
- **NG3**: Deduplication for `twig seed new` / `twig seed publish` — the local
  seed flow already has idempotency via the publish_id_map.
- **NG4**: Retroactive cleanup of existing duplicates — this is a forward-looking guard.
- **NG5**: Deduplication for the CLI `twig new` command — CLI users have manual
  control and can inspect before creating.

---

## Requirements

### Functional

- **FR1**: When `twig_new` is called with a `parentId`, query ADO for existing
  children of that parent with the same title (case-insensitive) and type.
  If a match is found, return the existing item with a `"status": "already_exists"`
  indicator instead of creating a new one.
- **FR2**: When `twig_new` is called without a `parentId`, no dedup check is
  performed (no reliable scope for comparison).
- **FR3**: The caller can explicitly opt out of dedup by passing
  `skipDuplicateCheck: true` to `twig_new`.
- **FR4**: A new `twig_find_or_create` tool encapsulates the full find-or-create
  pattern, always performing the dedup check.
- **FR5**: The dedup check uses a WIQL query:
  `SELECT [System.Id] FROM WorkItems WHERE [System.Parent] = {parentId} AND [System.Title] = '{title}' AND [System.WorkItemType] = '{type}'`
- **FR6**: The response for a deduplicated item clearly indicates it was found
  (not created) via a distinct JSON field so agents can distinguish.

### Non-Functional

- **NFR1**: The dedup check adds at most one additional ADO API call per creation.
- **NFR2**: No changes to the SQLite schema.
- **NFR3**: All new code is AOT-compatible (no reflection, source-gen JSON).
- **NFR4**: Test coverage ≥90% for new domain logic.

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────┐
│  CreationTools.cs    │  MCP tool layer
│  ┌───────────────┐   │
│  │ twig_new      │───┼──► DuplicateGuard.FindExistingAsync(...)
│  │               │   │      │
│  │ twig_find_or_ │   │      ├── WIQL query via IAdoWorkItemService
│  │ create        │───┼──►   │   (parent + title + type match)
│  └───────────────┘   │      │
└─────────────────────┘      ├── Match found → return existing WorkItem
                              └── No match → proceed to CreateAsync
```

### Key Components

#### 1. `DuplicateGuard` (Domain Service)

**File**: `src/Twig.Domain/Services/DuplicateGuard.cs`

A static domain service that encapsulates the dedup query logic:

```csharp
public static class DuplicateGuard
{
    public static async Task<WorkItem?> FindExistingChildAsync(
        IAdoWorkItemService adoService,
        int parentId,
        string title,
        WorkItemType type,
        CancellationToken ct = default)
    {
        // Build WIQL: match parent + title (exact) + type
        var escapedTitle = title.Replace("'", "''");
        var wiql = $"SELECT [System.Id] FROM WorkItems " +
                   $"WHERE [System.Parent] = {parentId} " +
                   $"AND [System.Title] = '{escapedTitle}' " +
                   $"AND [System.WorkItemType] = '{type.Value}'";

        var ids = await adoService.QueryByWiqlAsync(wiql, top: 1, ct);
        if (ids.Count == 0) return null;

        return await adoService.FetchAsync(ids[0], ct);
    }
}
```

**Design Decisions:**
- **Static class**: Matches the pattern used by `SeedFactory`, `ConflictRetryHelper`,
  and `WiqlQueryBuilder` — no state needed, pure function over injected services.
- **WIQL query**: Uses the existing `QueryByWiqlAsync` path, no new API endpoints.
- **Top 1**: We only need to know if *any* match exists; fetching more is waste.
- **Returns full WorkItem**: Allows the caller to return complete item details to
  the agent, same as a successful create response.

#### 2. Updated `twig_new` Tool

**File**: `src/Twig.Mcp/Tools/CreationTools.cs`

Add a `skipDuplicateCheck` parameter (default `false`). When `parentId` is
provided and `skipDuplicateCheck` is `false`, call `DuplicateGuard.FindExistingChildAsync`
before `CreateAsync`. If an existing item is found, return it with a distinct
status indicator.

#### 3. New `twig_find_or_create` Tool

**File**: `src/Twig.Mcp/Tools/CreationTools.cs`

A new MCP tool that always performs the dedup check. This provides explicit
find-or-create semantics for workflows that want guaranteed idempotency. It
delegates to the same `DuplicateGuard` and creation logic but makes the
dedup check mandatory (no `skipDuplicateCheck` parameter).

#### 4. `McpResultBuilder` Extension

**File**: `src/Twig.Mcp/Services/McpResultBuilder.cs`

Add a `FormatFoundExisting(WorkItem, string url, string workspace)` method
that formats the response with an `"action": "found_existing"` field (vs
`"action": "created"` for new items) so agents can distinguish.

### Data Flow

**Happy Path (no duplicate):**
```
Agent → twig_new(type, title, parentId)
  → DuplicateGuard.FindExistingChildAsync() → null (no match)
  → SeedFactory.Create() → WorkItem seed
  → AdoService.CreateAsync(seed) → newId
  → AdoService.FetchAsync(newId) → created item
  → McpResultBuilder.FormatCreated() → response
```

**Dedup Path (duplicate found):**
```
Agent → twig_new(type, title, parentId)
  → DuplicateGuard.FindExistingChildAsync() → existing WorkItem
  → McpResultBuilder.FormatFoundExisting() → response with "found_existing"
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Query scope | Parent's children only | Natural boundary — agents always create under a parent. Cross-project dedup is too broad and expensive. |
| Title matching | Exact, case-insensitive | WIQL `=` operator is case-insensitive by default in ADO. Fuzzy matching would produce false positives. |
| Default behavior | Dedup ON for MCP | MCP callers are automated agents that benefit from idempotency by default. CLI users have manual control. |
| Static service | `DuplicateGuard` static class | Matches existing patterns (`SeedFactory`, `ConflictRetryHelper`). No state needed. |
| New tool vs parameter | Both | `skipDuplicateCheck` on `twig_new` for backward compat; `twig_find_or_create` for explicit semantics. |

---

## Dependencies

### External
- Azure DevOps REST API (WIQL query endpoint) — already used by `twig_query`
- ModelContextProtocol SDK — already referenced for MCP tool registration

### Internal
- `IAdoWorkItemService.QueryByWiqlAsync` — existing method, no changes needed
- `IAdoWorkItemService.FetchAsync` — existing method, no changes needed
- `McpResultBuilder` — needs new `FormatFoundExisting` method
- `WorkspaceResolver` / `WorkspaceContext` — existing infrastructure, no changes

### Sequencing
- No prerequisites — all required infrastructure exists.

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|------------|------|
| `CreationTools.cs` | Modified — new param + dedup logic + new tool | Medium (core creation path) |
| `McpResultBuilder.cs` | Modified — new format method | Low |
| `DuplicateGuard.cs` | New — domain service | Low |
| `DuplicateGuardTests.cs` | New — unit tests | Low |

### Backward Compatibility

- `twig_new` gains a new optional parameter `skipDuplicateCheck` (default `false`).
  Existing callers that don't pass it get dedup behavior automatically.
- The response JSON gains an `"action"` field. Existing consumers that don't
  read this field are unaffected.
- No schema changes, no breaking API changes.

### Performance

- One additional WIQL query per `twig_new` call when `parentId` is provided
  (~50-100ms typical ADO latency). This is acceptable given work item creation
  is not a hot path.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| WIQL title matching has edge cases with special characters | Low | Medium | SQL-escape single quotes; test with titles containing `'`, `"`, `&` |
| Race condition: two concurrent creates for same title | Low | Low | Acceptable — agents are sequential within a conductor workflow. Document as known limitation. |
| Dedup check adds latency to every create | Low | Low | One WIQL query is ~50-100ms; negligible for work item creation |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `twig_find_or_create` be a separate tool or just a mode of `twig_new`? Both approaches are viable; a separate tool is more explicit for agent discovery. | Low | Resolved — both: parameter on `twig_new` + dedicated `twig_find_or_create` tool |
| 2 | Should the dedup check also match on description content, not just title? | Low | Resolved — title + type + parent is sufficient. Description matching is fragile (formatting differences). |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/DuplicateGuard.cs` | Domain service encapsulating dedup query logic |
| `tests/Twig.Domain.Tests/Services/DuplicateGuardTests.cs` | Unit tests for `DuplicateGuard` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Mcp/Tools/CreationTools.cs` | Add `skipDuplicateCheck` param to `twig_new`; add `twig_find_or_create` tool; integrate `DuplicateGuard` |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatFoundExisting()` method for dedup response formatting |

---

## ADO Work Item Structure

This is an Issue (#1891) — Tasks are defined directly under it.

### Issue #1891: Guard against duplicate work item creation across SDLC retries

**Goal**: Prevent duplicate work item creation when SDLC conductor workflows
retry, by adding title+type+parent deduplication to the `twig_new` MCP tool
and providing a `twig_find_or_create` convenience tool.

**Prerequisites**: None

#### Tasks

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|----------------|--------|
| T1 | Create `DuplicateGuard` domain service with `FindExistingChildAsync` method. Includes WIQL query construction with proper escaping of title characters. | `src/Twig.Domain/Services/DuplicateGuard.cs` | ~50 LoC | TO DO |
| T2 | Write unit tests for `DuplicateGuard`: match found, no match, special characters in title, cancellation token forwarding. | `tests/Twig.Domain.Tests/Services/DuplicateGuardTests.cs` | ~120 LoC | TO DO |
| T3 | Update `twig_new` in `CreationTools.cs`: add `skipDuplicateCheck` parameter, integrate `DuplicateGuard` call before `CreateAsync` when `parentId` is provided. | `src/Twig.Mcp/Tools/CreationTools.cs` | ~30 LoC changed | TO DO |
| T4 | Add `twig_find_or_create` tool to `CreationTools.cs`: new MCP tool method with mandatory dedup, delegates to shared creation logic. | `src/Twig.Mcp/Tools/CreationTools.cs` | ~60 LoC | TO DO |
| T5 | Add `FormatFoundExisting` method to `McpResultBuilder.cs` for dedup response formatting with `"action": "found_existing"` field. Update `FormatCreated` to include `"action": "created"`. | `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~40 LoC | TO DO |

**Acceptance Criteria**:
- [ ] `twig_new` with `parentId` checks for existing child with same title+type before creating
- [ ] `twig_new` returns existing item with `"action": "found_existing"` when duplicate detected
- [ ] `twig_new` with `skipDuplicateCheck: true` bypasses the check
- [ ] `twig_new` without `parentId` does not perform dedup check
- [ ] `twig_find_or_create` always performs dedup check (no skip option)
- [ ] `DuplicateGuard` handles special characters in titles (single quotes, etc.)
- [ ] All new code is AOT-compatible (no reflection)
- [ ] Unit tests pass with ≥90% coverage of new code
- [ ] Existing `twig_new` behavior is preserved for callers not using `parentId`

---

## PR Groups

### PG-1: Domain service + MCP integration (Deep)

**Scope**: All tasks (T1–T5)

**Classification**: Deep — few files, focused logic changes in creation path

**Tasks included**: T1, T2, T3, T4, T5

**Files**:
- `src/Twig.Domain/Services/DuplicateGuard.cs` (new)
- `tests/Twig.Domain.Tests/Services/DuplicateGuardTests.cs` (new)
- `src/Twig.Mcp/Tools/CreationTools.cs` (modified)
- `src/Twig.Mcp/Services/McpResultBuilder.cs` (modified)

**Estimated LoC**: ~300 LoC (new + changed)

**Rationale**: This is a single cohesive change — the domain service, MCP
integration, response formatting, and tests are tightly coupled and should
be reviewed together. Splitting would force reviewers to context-switch
between the dedup logic and its integration.

**Successor**: None

---

## References

- [ADO REST API: WIQL](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/wiql/query-by-wiql)
- [ADO REST API: Work Items - Create](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/create)
- Existing pattern: `ConflictRetryHelper.cs` (retry with re-fetch)
- Existing pattern: `SeedPublishOrchestrator.cs` (positive-ID guard)
- Existing pattern: `seed-from-plan.ps1` (plan-ado-map.json dedup)
