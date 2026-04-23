# Query and Search Tool: twig_query

| Field | Value |
|---|---|
| **Issue** | #1820 — Query and search tool: twig_query |
| **Parent Epic** | #1812 — Expand twig-mcp with 7 new tools |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan completes the `twig_query` MCP tool by adding the two missing date-range
filter parameters — `createdSince` and `changedSince` — to the existing
`NavigationTools.Query()` method. The core tool is already fully implemented: WIQL
query building via `WiqlQueryBuilder`, ADO execution via `QueryByWiqlAsync`, cache
write via `SaveBatchAsync`, and JSON formatting via `McpResultBuilder.FormatQueryResults`
are all in place with 10 passing test cases. The remaining work is surgically scoped:
wire two new `int?` parameters into the MCP method signature, propagate them to
`QueryParameters.CreatedSinceDays` / `ChangedSinceDays`, update the local
`BuildQueryDescription` helper, and add ~6 new unit tests covering the new filter
paths and their interaction with query description formatting.

---

## Background

### Current MCP Architecture

The `twig-mcp` server exposes **17 tools** across **6 tool classes**:

| Class | Tools | Purpose |
|---|---|---|
| `ContextTools` | `twig_set`, `twig_status` | Context management |
| `ReadTools` | `twig_tree`, `twig_workspace` | Read-only queries (context-dependent) |
| `NavigationTools` | `twig_show`, `twig_query`, `twig_children`, `twig_parent`, `twig_sprint` | Read-only navigation (ID-based) |
| `MutationTools` | `twig_state`, `twig_update`, `twig_note`, `twig_discard`, `twig_sync` | Write operations |
| `CreationTools` | `twig_new`, `twig_find_or_create`, `twig_link` | Creation and linking |
| `WorkspaceTools` | `twig_list_workspaces` | Workspace discovery |

All tools follow a consistent pattern:

1. **`[McpServerToolType]` sealed class** with primary constructor injecting `WorkspaceResolver`
2. **`[McpServerTool]` methods** with `[Description]` annotations on parameters, returning `Task<CallToolResult>`
3. **Workspace resolution** via `resolver.TryResolve(workspace, out var ctx, out var err)` with one-line guard
4. **Data retrieval** via `WorkspaceContext` service properties (repos, ADO client, etc.)
5. **JSON formatting** via `McpResultBuilder` static methods using `Utf8JsonWriter` (AOT-safe)
6. **Registration** via `WithTools<T>()` in `Program.cs` (no reflection)

### Existing twig_query Implementation

The `twig_query` tool is already implemented in `NavigationTools.cs` (lines 32–85) with
the following data flow:

```
MCP params → QueryParameters record → WiqlQueryBuilder.Build()
  → IAdoWorkItemService.QueryByWiqlAsync(wiql, top, ct)
  → IAdoWorkItemService.FetchBatchAsync(ids, ct)
  → IWorkItemRepository.SaveBatchAsync(items, ct)  // best-effort cache
  → McpResultBuilder.FormatQueryResults(items, isTruncated, queryDescription, workspace)
```

**Current MCP parameters** (9 filters + workspace + ct):

| Parameter | Type | Mapped To |
|---|---|---|
| `searchText` | `string?` | `QueryParameters.SearchText` |
| `type` | `string?` | `QueryParameters.TypeFilter` |
| `state` | `string?` | `QueryParameters.StateFilter` |
| `title` | `string?` | `QueryParameters.TitleFilter` |
| `assignedTo` | `string?` | `QueryParameters.AssignedToFilter` |
| `areaPath` | `string?` | `QueryParameters.AreaPathFilter` |
| `iterationPath` | `string?` | `QueryParameters.IterationPathFilter` |
| `top` | `int` | `QueryParameters.Top` |
| `workspace` | `string?` | Workspace routing |

**Missing parameters** (present in `QueryParameters` but not wired to MCP):

| Parameter | `QueryParameters` Property | CLI Equivalent |
|---|---|---|
| `createdSince` | `CreatedSinceDays` (int?) | `--createdSince 7d` (parsed via `TryParseDuration`) |
| `changedSince` | `ChangedSinceDays` (int?) | `--changedSince 2w` (parsed via `TryParseDuration`) |

### CLI vs MCP Parameter Representation

The CLI's `QueryCommand` accepts duration strings (`7d`, `2w`, `1m`) and parses them
into integer days via `TryParseDuration`. For the MCP tool, direct integer-days input
is more appropriate because:

1. MCP consumers are AI agents, not humans — they benefit from unambiguous numeric values
2. Eliminates regex parsing and error branches in the MCP layer
3. Matches the internal `QueryParameters` representation (int?)
4. The WIQL builder already uses `@Today - N` syntax with raw day counts

### Call-Site Audit

The `BuildQueryDescription` method in `NavigationTools.cs` (lines 142–162) is a private
static method with no external callers — it is only used by `NavigationTools.Query()`.
This method duplicates a subset of logic from `QueryCommand.BuildQueryDescription()`
(lines 263–289) but is intentionally separate because the MCP version omits
`DescriptionFilter` and date filters (which don't exist yet in the MCP parameter set).

| File | Method | Current Usage | Impact |
|---|---|---|---|
| `NavigationTools.cs:142` | `BuildQueryDescription` | Builds human description from 7 MCP filters | Must add `CreatedSinceDays` and `ChangedSinceDays` clauses |
| `NavigationTools.cs:52` | `Query` (QueryParameters init) | Constructs `QueryParameters` from 7 MCP params | Must wire `createdSince` → `CreatedSinceDays`, `changedSince` → `ChangedSinceDays` |

No cross-cutting components are modified. `WiqlQueryBuilder`, `QueryParameters`,
`McpResultBuilder.FormatQueryResults`, and `IAdoWorkItemService.QueryByWiqlAsync` are
already complete and require zero changes.

---

## Problem Statement

The `twig_query` MCP tool is missing two date-range filter parameters specified in the
work item requirements: `createdSince` and `changedSince`. Without these, Copilot agents
cannot narrow search results by recency — a common workflow when triaging recent changes
or finding newly created items. The domain layer (`QueryParameters`, `WiqlQueryBuilder`)
already supports these filters; only the MCP wiring and corresponding test coverage are
absent.

---

## Goals and Non-Goals

### Goals

1. **Add `createdSince` and `changedSince` parameters** to `NavigationTools.Query()` as
   `int?` values representing days (e.g., `createdSince: 7` means "created within 7 days")
2. **Wire parameters through to WIQL** via `QueryParameters.CreatedSinceDays` /
   `ChangedSinceDays` — the builder already generates `[System.CreatedDate] >= @Today - N`
3. **Update `BuildQueryDescription`** to include date-range clauses in the human-readable
   query summary
4. **Add ≥6 unit tests** covering: date filter in WIQL delegation, query description
   formatting with date filters, combined date + text filters, and edge cases (zero days)
5. **Pass all existing tests** — no regressions in the 10 existing query tests

### Non-Goals

- **Duration string parsing** (e.g., "7d", "2w") — MCP accepts raw integer days; the
  CLI handles string parsing in `QueryCommand.TryParseDuration`
- **Description filter** — The MCP tool intentionally omits `DescriptionFilter` (the CLI
  has it, but MCP agents can use `searchText` which searches both title and description)
- **Validation for negative/zero values** — WIQL handles `@Today - 0` correctly; negative
  values produce a future-looking query which is logically empty but not an error
- **Changes to `WiqlQueryBuilder`** — already supports `CreatedSinceDays` / `ChangedSinceDays`
- **Changes to `McpResultBuilder`** — `FormatQueryResults` is parameter-agnostic
- **Changes to `Program.cs`** — `NavigationTools` is already registered

---

## Requirements

### Functional Requirements

| ID | Requirement |
|---|---|
| FR-1 | `twig_query` accepts optional `createdSince` (int?) parameter with description "Only items created within this many days" |
| FR-2 | `twig_query` accepts optional `changedSince` (int?) parameter with description "Only items changed within this many days" |
| FR-3 | When `createdSince` is set, the generated WIQL includes `[System.CreatedDate] >= @Today - N` |
| FR-4 | When `changedSince` is set, the generated WIQL includes `[System.ChangedDate] >= @Today - N` |
| FR-5 | Query description includes date-range filter text (e.g., "created within 7d") |
| FR-6 | Both parameters default to null (no date filtering when omitted) |

### Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-1 | AOT-safe: no reflection, no dynamic code generation |
| NFR-2 | Zero changes to existing `WiqlQueryBuilder` or `McpResultBuilder` |
| NFR-3 | All 10 existing `NavigationToolsQueryTests` continue to pass |
| NFR-4 | ≥6 new test cases covering date filter paths |
| NFR-5 | `TreatWarningsAsErrors` — no new warnings introduced |

---

## Proposed Design

### Architecture Overview

No new components are needed. This is a surgical wiring change within the existing
`NavigationTools.Query()` method:

```
┌──────────────────────────────────────────────────────────┐
│                   NavigationTools.Query()                 │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ Parameters (existing):                               │ │
│  │   searchText, type, state, title, assignedTo,       │ │
│  │   areaPath, iterationPath, top, workspace           │ │
│  │ Parameters (NEW):                                    │ │
│  │   createdSince (int?), changedSince (int?)          │ │
│  └─────────────────┬───────────────────────────────────┘ │
│                    │                                     │
│                    ▼                                     │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ QueryParameters record                               │ │
│  │   .CreatedSinceDays = createdSince  ← NEW wiring    │ │
│  │   .ChangedSinceDays = changedSince  ← NEW wiring    │ │
│  └─────────────────┬───────────────────────────────────┘ │
│                    │                                     │
│                    ▼                                     │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ WiqlQueryBuilder.Build(parameters)  (NO CHANGES)     │ │
│  │   → [System.CreatedDate] >= @Today - N               │ │
│  │   → [System.ChangedDate] >= @Today - N               │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                          │
│  ┌─────────────────────────────────────────────────────┐ │
│  │ BuildQueryDescription(parameters)  (ADD 2 clauses)   │ │
│  │   → "created within Nd" / "changed within Nd"        │ │
│  └─────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### Key Components

**1. `NavigationTools.Query()` — Parameter Extension** (2 new parameters)

Add two nullable int parameters after `iterationPath`, before `top`:

```csharp
[Description("Only items created within this many days")] int? createdSince = null,
[Description("Only items changed within this many days")] int? changedSince = null,
```

Wire into `QueryParameters`:

```csharp
CreatedSinceDays = createdSince,
ChangedSinceDays = changedSince,
```

**2. `BuildQueryDescription()` — Date Clause Formatting** (2 new clauses)

Add after the `iterationPath` clause:

```csharp
if (parameters.CreatedSinceDays.HasValue)
    parts.Add($"created within {parameters.CreatedSinceDays.Value}d");
if (parameters.ChangedSinceDays.HasValue)
    parts.Add($"changed within {parameters.ChangedSinceDays.Value}d");
```

This matches the format used by `QueryCommand.BuildQueryDescription()` in the CLI.

### Data Flow

Unchanged from current implementation. The `createdSince`/`changedSince` values flow
through the existing pipeline:

1. MCP parameter `createdSince: 7` → `QueryParameters { CreatedSinceDays = 7 }`
2. `WiqlQueryBuilder.Build()` → appends `[System.CreatedDate] >= @Today - 7`
3. `QueryByWiqlAsync(wiql, top, ct)` → returns matching IDs
4. `FetchBatchAsync(ids, ct)` → returns full work items
5. `SaveBatchAsync(items, ct)` → best-effort cache write
6. `FormatQueryResults(items, isTruncated, queryDescription)` → JSON response

### Design Decisions

| Decision | Rationale |
|---|---|
| **Raw int days (not duration strings)** | MCP consumers are AI agents; raw integers are unambiguous and machine-friendly. Duration parsing (`7d`, `2w`) is a CLI UX concern handled by `QueryCommand.TryParseDuration`. |
| **Nullable int (not default 0)** | `null` means "no filter" — the builder skips the clause entirely. `0` is a valid filter meaning "today only" (`@Today - 0`). |
| **Parameter placement before `top`** | Groups all filter parameters together, with `top` and `workspace` as trailing control parameters. Matches the CLI argument ordering. |

---

## Dependencies

### Internal Dependencies

| Component | Status | Changes Needed |
|---|---|---|
| `QueryParameters` (Domain) | ✅ Complete | None — `CreatedSinceDays` / `ChangedSinceDays` already exist |
| `WiqlQueryBuilder` (Domain) | ✅ Complete | None — date clause generation already works |
| `McpResultBuilder` (MCP) | ✅ Complete | None — `FormatQueryResults` is filter-agnostic |
| `WorkspaceResolver` (MCP) | ✅ Complete | None |
| `ReadToolsTestBase` (Tests) | ✅ Complete | None |
| `NavigationToolsTestBase` (Tests) | ✅ Complete | None |

### External Dependencies

None. All required NuGet packages are already referenced.

---

## Open Questions

| # | Question | Severity | Status |
|---|---|---|---|
| 1 | Should the MCP tool accept duration strings (matching CLI) instead of raw days? | Low | Resolved — raw int days is more appropriate for programmatic MCP consumers. |

No blocking open questions remain.

---

## Files Affected

### New Files

None.

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Mcp/Tools/NavigationTools.cs` | Add `createdSince` and `changedSince` parameters to `Query()` method; wire to `QueryParameters`; add date clauses to `BuildQueryDescription()` |
| `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` | Add ≥6 new test cases for date filter parameters |

---

## ADO Work Item Structure

### Issue #1820 — Query and search tool: twig_query

**Goal:** Complete the `twig_query` MCP tool by adding the two missing date-range filter
parameters (`createdSince`, `changedSince`) and corresponding test coverage.

**Prerequisites:** None — all dependent domain components are already implemented.

#### Tasks

| Task | Description | Files | Effort |
|---|---|---|---|
| T1 | Add `createdSince` and `changedSince` parameters to `NavigationTools.Query()` and wire to `QueryParameters` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~15 LoC |
| T2 | Add date-range clauses to `BuildQueryDescription()` in `NavigationTools` | `src/Twig.Mcp/Tools/NavigationTools.cs` | ~5 LoC |
| T3 | Add unit tests for date filter parameters (≥6 cases) | `tests/Twig.Mcp.Tests/Tools/NavigationToolsQueryTests.cs` | ~120 LoC |
| T4 | Verify build passes with `TreatWarningsAsErrors` and all tests green | — | Build + test run |

#### Acceptance Criteria

- [ ] `NavigationTools.Query()` accepts `createdSince` (int?) and `changedSince` (int?) parameters
- [ ] Parameters are wired through to `QueryParameters.CreatedSinceDays` / `ChangedSinceDays`
- [ ] `BuildQueryDescription` includes date-range clauses when date filters are set
- [ ] ≥6 new test cases pass covering date filter wiring, query description formatting, combined filters
- [ ] All 10 existing `NavigationToolsQueryTests` continue to pass
- [ ] Solution builds with zero warnings under `TreatWarningsAsErrors`
- [ ] AOT compatibility maintained (no reflection)

---

## PR Groups

### PG-1: Date Filter Parameters for twig_query

| Property | Value |
|---|---|
| **Tasks** | T1, T2, T3, T4 (all from Issue #1820) |
| **Type** | Deep — few files, focused logic change |
| **Files** | 2 (`NavigationTools.cs`, `NavigationToolsQueryTests.cs`) |
| **Estimated LoC** | ~140 |
| **Predecessors** | None |

**Review notes:** Single coherent change — add two parameters, wire them through,
test thoroughly. Reviewer should verify: parameter descriptions are clear for MCP
consumers, `BuildQueryDescription` output matches CLI format, no regressions in
existing tests.
