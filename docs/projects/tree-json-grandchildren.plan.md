# twig tree --depth N: JSON output missing grandchildren

| Field | Value |
|-------|-------|
| **Work Item** | #2069 |
| **Type** | Issue (Tasks planned below) |
| **Status** | Draft |
| **Revision** | 0 |

---

## Executive Summary

When `twig tree --output json` is invoked, the JSON payload contains only the
direct children of the focused work item — grandchildren and deeper descendants
are missing entirely. This is because `TreeCommand` only calls
`workItemRepo.GetChildrenAsync(focusId)` once (yielding depth-1 children), the
`WorkTree` read model stores a flat `Children` list, and all JSON formatters
iterate that flat list without recursion. The fix adds a
`DescendantsByParentId` dictionary to `WorkTree`, introduces recursive child
fetching in the command layer, and updates the three JSON-producing formatters
(`JsonOutputFormatter`, `JsonCompactOutputFormatter`, `McpResultBuilder`) to
emit nested `children` arrays. Terminal output (human, minimal, Spectre) is
unaffected.

## Background

### Current Architecture

The `twig tree` command follows this data flow:

```
TreeCommand.ExecuteCoreAsync
  → workItemRepo.GetChildrenAsync(focusId)     // depth-1 only
  → WorkTree.Build(focus, parentChain, children, siblingCounts, links)
  → formatter.FormatTree(tree, maxChildren, activeId)
```

`WorkTree` (domain read model) holds:
- `FocusedItem` — the active work item
- `ParentChain` — ancestors from root to immediate parent
- `Children` — **flat list of direct children only**
- `SiblingCounts` — sibling count per node ID
- `FocusedItemLinks` — non-hierarchy links

The `--depth N` parameter is mapped to `maxChildren` which controls the
**count** of children shown (not recursion depth):

```csharp
var maxChildren = all ? int.MaxValue
    : depth ?? config.Display.TreeDepth;  // default: 10
```

### JSON Formatters — No Recursion

`JsonOutputFormatter.FormatTree` (line 130):
```csharp
writer.WriteStartArray("children");
foreach (var child in tree.Children)
    WriteWorkItemObject(writer, child);   // flat — no nested children
writer.WriteEndArray();
```

`JsonCompactOutputFormatter.FormatTree` (line 53):
```csharp
writer.WriteStartArray("children");
foreach (var child in tree.Children)
    WriteCompactItem(writer, child);      // flat — no nested children
writer.WriteEndArray();
```

`McpResultBuilder.FormatTree` (line 90):
```csharp
WriteWorkItemArray(writer, "children", tree.Children);  // flat
```

### Contrast: Workspace View Does Recurse

The workspace command's rendering **does** use recursive tree building:
- `SpectreRenderer.AddWorkspaceTreeChildren` recurses with depth tracking
- `HumanOutputFormatter.RenderTreeChildren` recurses with depth tracking

This confirms the codebase has working recursive patterns to draw from.

### Call-Site Audit for `WorkTree.Build`

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `TreeCommand.cs:222` | `ExecuteCoreAsync` | Builds tree for sync path (JSON/minimal/human non-Spectre) | Must pass descendants map |
| `ReadTools.cs:65` | `Tree` | MCP twig_tree tool | Must pass descendants map |
| `WorkTreeTests.cs` | Various | Unit tests for WorkTree | Must adapt to new optional param |
| `JsonOutputFormatterTests.cs` | Various | FormatTree tests | Must add grandchildren tests |
| `JsonCompactOutputFormatterTests.cs` | Various | FormatTree tests | Must add grandchildren tests |
| `McpResultBuilderTests.cs` | `FormatTree_*` | MCP result tests | Must add grandchildren tests |
| `TreeCommandTests.cs` | Various | Command integration tests | Must mock recursive child fetch |
| `ReadToolsTreeTests.cs` | Various | MCP tree tool tests | Must mock recursive child fetch |

### Call-Site Audit for `FormatTree` (all formatters)

| File | Method | Current Behavior | Impact |
|------|--------|------------------|--------|
| `JsonOutputFormatter.cs:110` | `FormatTree` | Flat children array | Must recurse using descendants map |
| `JsonCompactOutputFormatter.cs:38` | `FormatTree` | Flat children array | Must recurse using descendants map |
| `HumanOutputFormatter.cs:231` | `FormatTree` | Flat children with box-drawing | No change (terminal works) |
| `MinimalOutputFormatter.cs:25` | `FormatTree` | Flat children with indent | No change (terminal works) |
| `McpResultBuilder.cs:77` | `FormatTree` | Flat children array | Must recurse using descendants map |
| `TreeCommand.cs:232` | via `fmt.FormatTree` | Calls formatter | No change (passes WorkTree) |

## Problem Statement

JSON consumers of `twig tree --output json` (agents, scripts, CI pipelines)
receive an incomplete hierarchy. When focused on an Epic with Issues containing
Tasks, the JSON output shows the Issues but not their child Tasks. This forces
consumers to make multiple `twig tree` calls to reconstruct the full hierarchy,
which is inefficient and error-prone.

## Goals and Non-Goals

### Goals

1. **G-1**: JSON output from `twig tree` includes nested `children` arrays for
   each child item, recursively, up to the available depth in the local cache.
2. **G-2**: MCP `twig_tree` tool output includes the same nested structure.
3. **G-3**: The `--depth N` flag controls recursion depth (how many levels of
   descendants to include), not just child count.
4. **G-4**: Backward-compatible JSON schema — `children` array remains at the
   same location; each child object gains an optional `children` array.
5. **G-5**: No regression in terminal output (human, minimal, Spectre paths).

### Non-Goals

- Changing terminal tree rendering to show grandchildren (separate enhancement)
- Adding a new `--recursive` flag (reuse `--depth`)
- Changing the `IOutputFormatter.FormatTree` interface signature
- Supporting infinite recursion (capped at depth parameter or config default)

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| F-1 | `WorkTree` exposes a `DescendantsByParentId` dictionary mapping parent IDs to their child work items |
| F-2 | `TreeCommand` recursively fetches children up to `depth` levels below the focus item |
| F-3 | `JsonOutputFormatter.FormatTree` emits nested `children` arrays using the descendants map |
| F-4 | `JsonCompactOutputFormatter.FormatTree` emits nested `children` arrays (compact schema) |
| F-5 | `McpResultBuilder.FormatTree` emits nested `children` arrays |
| F-6 | `ReadTools.Tree` recursively fetches children before building `WorkTree` |
| F-7 | When `DescendantsByParentId` is null or empty, formatters fall back to current flat behavior |
| F-8 | `totalChildren` remains the count of direct children (backward compat) |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF-1 | No additional ADO API calls — all data comes from the local SQLite cache |
| NF-2 | Recursive fetch is bounded by depth parameter (default 10) to prevent runaway queries |
| NF-3 | AOT-compatible — no reflection, all JSON via manual Utf8JsonWriter |

## Proposed Design

### Architecture Overview

The fix is a vertical slice through three layers:

```
┌────────────────────────────────────────────┐
│  Command Layer (TreeCommand, ReadTools)     │
│  ↓ recursive fetch: GetChildrenAsync × N   │
├────────────────────────────────────────────┤
│  Domain Model (WorkTree)                   │
│  + DescendantsByParentId dictionary        │
├────────────────────────────────────────────┤
│  Formatter Layer (JSON, JsonCompact, MCP)  │
│  recursive WriteChildrenWithDescendants()  │
└────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkTree` — Domain Read Model Enhancement

Add an optional `DescendantsByParentId` property:

```csharp
/// <summary>
/// Multi-level children keyed by parent ID. Used by JSON formatters
/// to emit nested children arrays. Null when only depth-1 is available.
/// </summary>
public IReadOnlyDictionary<int, IReadOnlyList<WorkItem>>? DescendantsByParentId { get; }
```

Update `Build` factory method with a new optional parameter:

```csharp
public static WorkTree Build(
    WorkItem focus,
    IReadOnlyList<WorkItem> parentChain,
    IReadOnlyList<WorkItem> children,
    IReadOnlyDictionary<int, int?>? siblingCounts = null,
    IReadOnlyList<WorkItemLink>? focusedItemLinks = null,
    IReadOnlyDictionary<int, IReadOnlyList<WorkItem>>? descendantsByParentId = null)
```

This is **fully backward-compatible** — existing callers pass `null` (default)
and formatters fall back to the flat `Children` list.

#### 2. `TreeCommand` — Recursive Child Fetching

In the sync path (lines 180–256), after fetching direct children, recursively
fetch grandchildren up to `maxChildren` depth levels:

```csharp
// Recursive descendant fetch for JSON consumers
var descendantsMap = new Dictionary<int, IReadOnlyList<WorkItem>>();
await FetchDescendantsRecursiveAsync(
    workItemRepo, children, descendantsMap,
    currentDepth: 1, maxDepth: maxChildren, ct);

var tree = WorkTree.Build(item, parentChain, children,
    siblingCounts, links, descendantsMap);
```

The recursive helper:

```csharp
private static async Task FetchDescendantsRecursiveAsync(
    IWorkItemRepository repo,
    IReadOnlyList<WorkItem> parents,
    Dictionary<int, IReadOnlyList<WorkItem>> map,
    int currentDepth, int maxDepth,
    CancellationToken ct)
{
    if (currentDepth >= maxDepth || parents.Count == 0) return;

    foreach (var parent in parents)
    {
        var grandchildren = await repo.GetChildrenAsync(parent.Id, ct);
        if (grandchildren.Count > 0)
        {
            map[parent.Id] = grandchildren;
            await FetchDescendantsRecursiveAsync(
                repo, grandchildren, map,
                currentDepth + 1, maxDepth, ct);
        }
    }
}
```

#### 3. JSON Formatters — Recursive Output

`JsonOutputFormatter.FormatTree` gains a private recursive helper:

```csharp
private static void WriteChildWithDescendants(
    Utf8JsonWriter writer, WorkItem child,
    IReadOnlyDictionary<int, IReadOnlyList<WorkItem>>? descendants)
{
    WriteWorkItemObject(writer, child);
    // Before closing the object, add nested children
    // (WriteWorkItemObject already writes WriteEndObject,
    //  so we restructure to write children inside the object)
}
```

The `children` array for each child is populated by looking up
`descendants[child.Id]` and recursing.

#### 4. MCP `ReadTools.Tree` — Same Pattern

Mirror the recursive fetch from `TreeCommand` in the MCP tool, passing the
descendants map to `WorkTree.Build`.

### Data Flow (JSON path)

```
User: twig tree --output json --depth 3

TreeCommand.ExecuteCoreAsync:
  1. Resolve active item (ID=100, Epic)
  2. Fetch parent chain: []
  3. Fetch children of 100: [Issue#200, Issue#201]
  4. Recursive fetch:
     - Children of 200: [Task#300, Task#301]
     - Children of 201: [Task#302]
     - Children of 300: [] (leaf)
     - Children of 301: [] (leaf)
     - Children of 302: [] (leaf)
  5. Build descendantsMap:
     { 200: [Task#300, Task#301], 201: [Task#302] }
  6. WorkTree.Build(focus=100, children=[200,201], descendantsMap)
  7. JsonOutputFormatter.FormatTree → recursive JSON

Output:
{
  "focus": { "id": 100, ... },
  "children": [
    {
      "id": 200, ...,
      "children": [
        { "id": 300, ..., "children": [] },
        { "id": 301, ..., "children": [] }
      ]
    },
    {
      "id": 201, ...,
      "children": [
        { "id": 302, ..., "children": [] }
      ]
    }
  ],
  "totalChildren": 2
}
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Add `DescendantsByParentId` map to `WorkTree` rather than a recursive `TreeNode` type | Avoids introducing a new domain type; backward-compatible since existing `Children` property is unchanged; map lookup is O(1) per node |
| Keep `--depth` as the controlling parameter | Reuses existing CLI parameter; semantic shift from "child count" to "recursion depth" is more aligned with the parameter name and documentation |
| Only JSON/MCP formatters recurse; terminal formatters unchanged | Terminal output already "works correctly" per the issue; avoids scope creep |
| Recursive fetch uses `GetChildrenAsync` (local cache) | No ADO API calls; NF-1 satisfied; SQLite queries are fast |
| `children` array present on every child object (empty `[]` for leaves) | Consistent schema for JSON consumers; no need to check for presence |

## Dependencies

- **Internal**: `IWorkItemRepository.GetChildrenAsync` — already exists, no changes needed
- **Internal**: `WorkTree.Build` — additive change (new optional parameter)
- **External**: None — all data from local SQLite cache

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--depth` continue to limit child count for terminal output while controlling recursion depth for JSON? | Low | Backward compat favors keeping terminal behavior unchanged. The parameter documentation already says "Maximum tree depth to display" which aligns with recursion depth semantics. |
| 2 | Should leaf nodes emit `"children": []` or omit the `children` key? | Low | Emitting `[]` is more consistent and easier for consumers to parse. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | All changes are modifications to existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ReadModels/WorkTree.cs` | Add `DescendantsByParentId` property; update `Build` factory method |
| `src/Twig/Commands/TreeCommand.cs` | Add recursive descendant fetching in sync path |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Recursive `children` writing in `FormatTree` |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Recursive `children` writing in `FormatTree` |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Recursive descendant fetching in `Tree` method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Recursive `children` writing in `FormatTree` |
| `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs` | Tests for `DescendantsByParentId` |
| `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | Tests for recursive JSON output |
| `tests/Twig.Cli.Tests/Formatters/JsonCompactOutputFormatterTests.cs` | Tests for recursive compact JSON output |
| `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs` | Tests for recursive fetching with JSON output |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | Tests for MCP recursive output |
| `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs` | Tests for MCP recursive JSON |

## ADO Work Item Structure

### Issue #2069: twig tree --depth N: JSON output missing grandchildren

**Goal**: Fix JSON output of `twig tree` to include nested children recursively.

**Prerequisites**: None

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | Add `DescendantsByParentId` to `WorkTree` domain model | `WorkTree.cs`, `WorkTreeTests.cs` | S |
| T2 | Add recursive child fetching in `TreeCommand` sync path | `TreeCommand.cs`, `TreeCommandTests.cs` | M |
| T3 | Update `JsonOutputFormatter.FormatTree` for recursive children | `JsonOutputFormatter.cs`, `JsonOutputFormatterTests.cs` | M |
| T4 | Update `JsonCompactOutputFormatter.FormatTree` for recursive children | `JsonCompactOutputFormatter.cs`, `JsonCompactOutputFormatterTests.cs` | S |
| T5 | Update MCP `ReadTools.Tree` and `McpResultBuilder.FormatTree` | `ReadTools.cs`, `McpResultBuilder.cs`, `ReadToolsTreeTests.cs`, `McpResultBuilderTests.cs` | M |
| T6 | Verify build, run all tests, confirm no regressions | All | S |

**Acceptance Criteria**:
- [ ] `twig tree --output json` includes nested `children` arrays for grandchildren
- [ ] `twig tree --output json-compact` includes nested `children` arrays
- [ ] MCP `twig_tree` tool includes nested `children` arrays
- [ ] `--depth N` controls recursion depth for JSON output
- [ ] Terminal output (human, minimal, Spectre) is unchanged
- [ ] All existing tests pass; new tests cover recursive scenarios
- [ ] Build succeeds with AOT, no warnings-as-errors

## PR Groups

### PG-1: Domain model + formatters (deep)

**Scope**: T1, T3, T4 — `WorkTree` model change + JSON formatter updates

**Files** (~6):
- `src/Twig.Domain/ReadModels/WorkTree.cs`
- `src/Twig/Formatters/JsonOutputFormatter.cs`
- `src/Twig/Formatters/JsonCompactOutputFormatter.cs`
- `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs`
- `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs`
- `tests/Twig.Cli.Tests/Formatters/JsonCompactOutputFormatterTests.cs`

**Estimated LoC**: ~250
**Classification**: Deep — focused changes to core model and formatters
**Successor**: PG-2

### PG-2: Command layer + MCP (deep)

**Scope**: T2, T5, T6 — Recursive fetching in TreeCommand and MCP ReadTools

**Files** (~6):
- `src/Twig/Commands/TreeCommand.cs`
- `src/Twig.Mcp/Tools/ReadTools.cs`
- `src/Twig.Mcp/Services/McpResultBuilder.cs`
- `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs`
- `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs`
- `tests/Twig.Mcp.Tests/Services/McpResultBuilderTests.cs`

**Estimated LoC**: ~300
**Classification**: Deep — recursive fetch logic and integration tests
**Predecessor**: PG-1
