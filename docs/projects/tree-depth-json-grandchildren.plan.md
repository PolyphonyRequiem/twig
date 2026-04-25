# Tree Depth JSON Grandchildren Fix

**ADO Work Item:** #2069 — twig tree --depth N: JSON output missing grandchildren
**Author:** Daniel Green
**Status:** Draft
**Revision:** 0

---

## Executive Summary

When running `twig tree --depth N` with JSON output (`-o json` or `-o json-compact`),
grandchildren and deeper descendants are missing from the output. The root cause is a
chain of flat-data-model decisions: `TreeCommand` only fetches direct children via
`GetChildrenAsync`, `WorkTree` stores children as a flat `IReadOnlyList<WorkItem>`,
and the JSON formatters write children without recursion. Additionally, the `--depth`
parameter is confusingly used as a **child count limit** rather than a **hierarchical
depth**. This plan introduces a `WorkTreeNode` recursive model, adds depth-aware
child fetching via a `TreeChildPopulator` service, and updates all JSON output paths
(CLI formatters + MCP) to emit nested `children` arrays while preserving backward
compatibility for non-JSON formatters.

## Background

### Current Architecture

The `twig tree` command follows this data flow:

1. **TreeCommand.ExecuteCoreAsync** (line 47) resolves the active work item, fetches its
   parent chain via `GetParentChainAsync`, and fetches **direct children only** via
   `IWorkItemRepository.GetChildrenAsync(item.Id)` (line 188).
2. A flat `WorkTree` read model is built with `WorkTree.Build(item, parentChain, children)`
   (line 222). `WorkTree.Children` is `IReadOnlyList<WorkItem>` — no nesting.
3. The `WorkTree` is passed to `IOutputFormatter.FormatTree(tree, maxChildren, activeId)`.
4. JSON formatters iterate `tree.Children` flat — each child is a leaf node with
   `{id, title, type, state, assignedTo, isDirty, isSeed, parentId}`. No nested
   `children` array exists.

The `--depth` CLI parameter is documented as "Max child depth to display" but is
implemented as a **child count limit**:

```csharp
// TreeCommand.cs:62-63
var maxChildren = all ? int.MaxValue
    : depth ?? config.Display.TreeDepth;
```

This value is used as `Take(maxChildren)` in MCP ReadTools.Tree (line 46) and as
`Math.Min(tree.Children.Count, maxChildren)` in the SpectreRenderer and
HumanOutputFormatter.

**Critical observation:** The JSON formatter **already ignores** `maxChildren` — it
outputs all children regardless. The code comment says "All children (no truncation
for JSON consumers)" (JsonOutputFormatter.cs:129). This means reinterpreting `--depth`
as hierarchical depth for JSON output is non-breaking for existing JSON consumers.

### Call-Site Audit

`WorkTree.Children` and `IOutputFormatter.FormatTree` are used across multiple components:

| File | Method / Line | Current Usage | Impact |
|------|---------------|---------------|--------|
| `TreeCommand.cs:222` | `WorkTree.Build(...)` | Builds flat tree for non-renderer (sync) path | Must populate `ChildNodes` when depth>1 |
| `TreeCommand.cs:103` | `getChildren: () => repo.GetChildrenAsync(...)` | Renderer path — flat fetch | No change (Spectre handles flat children) |
| `TreeCommand.cs:122,150` | `GetChildrenAsync(activeId.Value)` | Cache + sync paths — flat fetch | No change (Spectre path) |
| `JsonOutputFormatter.cs:110-155` | `FormatTree(tree, max, id)` | Flat iteration of `tree.Children` | Must use `ChildNodes` when populated |
| `JsonCompactOutputFormatter.cs:38-63` | `FormatTree(tree, max, id)` | Flat iteration of `tree.Children` | Must use `ChildNodes` when populated |
| `HumanOutputFormatter.cs:231-340` | `FormatTree(tree, max, id, ...)` | Box-drawing flat children | No change — uses flat `Children` |
| `MinimalOutputFormatter.cs:25-60` | `FormatTree(tree, max, id)` | Pipe-friendly flat output | No change — uses flat `Children` |
| `McpResultBuilder.cs:77-115` | `FormatTree(tree, total)` | MCP JSON output | Must use `ChildNodes` when populated |
| `ReadTools.cs:44` | `repo.GetChildrenAsync(item.Id)` | MCP tree — flat fetch | Must recursively fetch children |
| `WorkTree.cs:63-98` | `FindByPattern`, `MoveDown` | Navigation uses `Children` | No change — flat `Children` preserved |
| `SpectreRenderer.cs:651-705` | `RenderTreeAsync` | Live tree rendering | No change — flat children path |
| `SpectreRenderer.cs:840-900` | `BuildTreeViewAsync` | Cached tree view | No change — flat children path |

### Prior Art

The workspace tree already implements recursive depth via `SprintHierarchyNode`:

```csharp
public sealed class SprintHierarchyNode {
    public WorkItem Item { get; }
    public List<SprintHierarchyNode> Children { get; } = new();
}
```

The `HumanOutputFormatter.RenderTreeChildren` and `SpectreRenderer.AddWorkspaceTreeChildren`
methods render recursively with `TreeDepthDown` limiting. This plan follows the same
pattern for the tree command.

## Problem Statement

1. **`--depth` is misinterpreted:** The parameter name and documentation say "depth" but
   the implementation uses it as a child count limit (`Take(maxChildren)`).
2. **No recursive child fetching:** `TreeCommand` and MCP `ReadTools.Tree` call
   `GetChildrenAsync` once for the focused item — never for grandchildren.
3. **Flat data model:** `WorkTree.Children` is `IReadOnlyList<WorkItem>` — no nesting
   capability.
4. **Flat JSON rendering:** All JSON formatters write children as a flat array. Each
   child object has no nested `children` property.
5. **MCP parity gap:** The MCP `twig_tree` tool has the same limitation — grandchildren
   are never included regardless of the `depth` parameter.

## Goals and Non-Goals

### Goals

- **G-1:** JSON output (`-o json`, `-o json-compact`, MCP) includes recursive children
  up to `--depth N` levels deep, with nested `children` arrays.
- **G-2:** Default depth of 1 preserves current behavior (direct children only).
- **G-3:** `--depth 0` shows no children; `--depth 2` shows children + grandchildren;
  `--depth 3` shows three levels, etc.
- **G-4:** Human, Spectre, and Minimal formatters continue to work with flat children
  (no regression).
- **G-5:** MCP `twig_tree` tool supports the same depth semantics.
- **G-6:** Existing JSON consumers see backward-compatible output at default depth.

### Non-Goals

- Recursive rendering in Human/Spectre/Minimal formatters (future work).
- Adding a separate `--max-children` flag to replace the current count-limit behavior.
- Recursive fetching from ADO (we only query the local SQLite cache).
- Changing the `config.Display.TreeDepth` config key semantics.

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `WorkTree` exposes an optional `ChildNodes` property with recursive `WorkTreeNode` hierarchy |
| FR-2 | `TreeCommand` populates `ChildNodes` when depth ≥ 1 for the non-renderer (sync) path |
| FR-3 | `JsonOutputFormatter.FormatTree` renders nested `children` arrays from `ChildNodes` when populated |
| FR-4 | `JsonCompactOutputFormatter.FormatTree` renders nested `children` arrays from `ChildNodes` when populated |
| FR-5 | MCP `ReadTools.Tree` populates `ChildNodes` recursively |
| FR-6 | MCP `McpResultBuilder.FormatTree` renders nested `children` arrays from `ChildNodes` when populated |
| FR-7 | `--depth 0` produces empty children array; `--depth 1` produces direct children only (default) |
| FR-8 | When `ChildNodes` is null, formatters fall back to flat `Children` (backward compat) |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF-1 | No reflection — all JSON via `Utf8JsonWriter` (AOT compatible) |
| NF-2 | Recursive fetching bounded by depth to prevent unbounded queries |
| NF-3 | Backward compatible JSON schema — existing fields (`focus`, `parentChain`, `totalChildren`, `links`) unchanged |
| NF-4 | All warnings treated as errors (existing constraint) |

## Proposed Design

### Architecture Overview

```
┌──────────────────┐     ┌────────────────────┐     ┌──────────────────────┐
│   TreeCommand    │────▶│     WorkTree       │────▶│   JSON Formatter     │
│   (calls         │     │   .ChildNodes      │     │   (recursive write   │
│  TreeChildPop.)  │     │   [optional]       │     │    when populated)   │
└──────────────────┘     └────────────────────┘     └──────────────────────┘
                                │
┌──────────────────┐            │
│  MCP ReadTools   │────────────┘
│  (calls          │
│  TreeChildPop.)  │
└──────────────────┘
```

### Key Components

#### 1. `WorkTreeNode` Record (Domain)

A sealed record representing a node in the recursive child hierarchy:

```csharp
// In WorkTree.cs as a companion type
public sealed record WorkTreeNode(
    WorkItem Item,
    IReadOnlyList<WorkTreeNode> Children);
```

Leaf nodes have `Children = []`. This mirrors the existing `SprintHierarchyNode`
pattern but is immutable (record) and uses `IReadOnlyList<T>`.

#### 2. `WorkTree.ChildNodes` Property

An optional nullable property on `WorkTree`:

```csharp
public IReadOnlyList<WorkTreeNode>? ChildNodes { get; }
```

When populated (depth ≥ 2 in the tree command), JSON formatters use it for
recursive rendering. When null, formatters fall back to the existing flat
`Children` list — no behavioral change for Human/Spectre/Minimal formatters.

A new `Build` overload accepts `IReadOnlyList<WorkTreeNode>`:

```csharp
public static WorkTree Build(
    WorkItem focus,
    IReadOnlyList<WorkItem> parentChain,
    IReadOnlyList<WorkTreeNode> childNodes,
    IReadOnlyDictionary<int, int?>? siblingCounts = null,
    IReadOnlyList<WorkItemLink>? focusedItemLinks = null)
```

The flat `Children` property is **derived** from `ChildNodes` via
`childNodes.Select(n => n.Item).ToList()`. This ensures backward compatibility:
navigation methods (`FindByPattern`, `MoveDown`), Human formatter, and Spectre
renderer all continue working unchanged.

#### 3. `TreeChildPopulator` (Domain Service)

A static helper class used by both `TreeCommand` and MCP `ReadTools`:

```csharp
// src/Twig.Domain/Services/TreeChildPopulator.cs
public static class TreeChildPopulator
{
    public static async Task<IReadOnlyList<WorkTreeNode>> PopulateAsync(
        IWorkItemRepository repo, int parentId, int remainingDepth,
        CancellationToken ct = default)
    {
        if (remainingDepth <= 0)
            return [];

        var children = await repo.GetChildrenAsync(parentId, ct);
        var nodes = new List<WorkTreeNode>(children.Count);
        foreach (var child in children)
        {
            var grandchildren = await PopulateAsync(
                repo, child.Id, remainingDepth - 1, ct);
            nodes.Add(new WorkTreeNode(child, grandchildren));
        }
        return nodes;
    }
}
```

This queries the local SQLite cache only — no ADO round-trips. The recursive
depth is bounded by the `remainingDepth` parameter, preventing unbounded queries.

#### 4. JSON Recursive Writing

A private helper method added to both `JsonOutputFormatter` and `McpResultBuilder`:

```csharp
private static void WriteTreeNodeFields(Utf8JsonWriter writer, WorkItem item)
{
    writer.WriteNumber("id", item.Id);
    writer.WriteString("title", item.Title);
    writer.WriteString("type", item.Type.ToString());
    writer.WriteString("state", item.State);
    writer.WriteString("assignedTo", item.AssignedTo);
    writer.WriteBoolean("isDirty", item.IsDirty);
    writer.WriteBoolean("isSeed", item.IsSeed);
    if (item.ParentId.HasValue)
        writer.WriteNumber("parentId", item.ParentId.Value);
    else
        writer.WriteNull("parentId");
}

private static void WriteTreeNode(Utf8JsonWriter writer, WorkTreeNode node)
{
    writer.WriteStartObject();
    WriteTreeNodeFields(writer, node.Item);
    writer.WriteStartArray("children");
    foreach (var child in node.Children)
        WriteTreeNode(writer, child);
    writer.WriteEndArray();
    writer.WriteEndObject();
}
```

In `FormatTree`, the decision branch:

```csharp
writer.WriteStartArray("children");
if (tree.ChildNodes is not null)
{
    foreach (var node in tree.ChildNodes)
        WriteTreeNode(writer, node);
}
else
{
    foreach (var child in tree.Children)
        WriteWorkItemObject(writer, child);
}
writer.WriteEndArray();
```

### Data Flow

For `twig tree -o json --depth 2`:

1. `TreeCommand.ExecuteCoreAsync` resolves active item and parent chain.
2. Instead of `GetChildrenAsync(item.Id)`, calls
   `TreeChildPopulator.PopulateAsync(repo, item.Id, depth: 2, ct)`:
   - Fetches direct children of focused item (depth 2 → 1)
   - For each child, fetches grandchildren (depth 1 → 0)
   - Returns `IReadOnlyList<WorkTreeNode>` with nested structure
3. Builds `WorkTree` via new `Build` overload with `ChildNodes` populated.
4. `JsonOutputFormatter.FormatTree` detects `tree.ChildNodes is not null`.
5. Writes each child node recursively with nested `children` arrays.

For `twig tree -o json` (no `--depth`, default depth=1):

1. Same resolution steps.
2. Calls `TreeChildPopulator.PopulateAsync(repo, item.Id, depth: 1, ct)`:
   - Fetches direct children only (depth 1 → 0 for each child)
   - Each `WorkTreeNode` has `Children = []`
3. Builds `WorkTree` with `ChildNodes` populated but each node has empty children.
4. JSON output: each child has `"children": []` — slightly different from current
   output (which has no `children` field on child objects), but additive only.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| `ChildNodes` is optional (nullable) | Preserves backward compatibility; flat-only paths (Spectre, Human, Minimal) don't need changes |
| `Children` derived from `ChildNodes` | Navigation methods (`FindByPattern`, `MoveDown`) continue working unchanged |
| Helper is `static` on a service class | Shared between CLI and MCP without coupling; no DI registration needed |
| Default depth remains 1 | Matches current behavior — direct children only |
| SQLite-only recursion | Local cache is fast; ADO recursion would be slow and complex |
| `WorkTreeNode` as sealed record | Immutable, AOT-safe, follows codebase convention for domain read models |
| Always emit `children: []` on leaf nodes in JSON | Consistent schema for consumers — they can always expect `children` array |

### JSON Schema (After Fix)

With `--depth 2`:

```json
{
  "focus": { "id": 10, "title": "Feature", ... },
  "parentChain": [ { "id": 1, "title": "Epic", ... } ],
  "children": [
    {
      "id": 20, "title": "Task 1", ...,
      "children": [
        { "id": 30, "title": "Subtask 1", ..., "children": [] },
        { "id": 31, "title": "Subtask 2", ..., "children": [] }
      ]
    },
    {
      "id": 21, "title": "Task 2", ...,
      "children": []
    }
  ],
  "totalChildren": 2,
  "links": []
}
```

With `--depth 1` (default) — each child now has `"children": []`:

```json
{
  "focus": { "id": 10, "title": "Feature", ... },
  "parentChain": [ ... ],
  "children": [
    { "id": 20, "title": "Task 1", ..., "children": [] },
    { "id": 21, "title": "Task 2", ..., "children": [] }
  ],
  "totalChildren": 2,
  "links": []
}
```

## Dependencies

- **Internal:** `IWorkItemRepository.GetChildrenAsync` — already exists, no changes needed
- **External:** None — all data comes from local SQLite cache
- **Sequencing:** None — self-contained change

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--depth 1` (default) emit `"children": []` on each child object, or omit it for strict backward compat? | Low | Recommend: always emit `"children"` when `ChildNodes` is populated. This is additive (new field) and consistent. JSON consumers should handle unknown fields gracefully. |
| 2 | Should recursive depth also apply to Human/Spectre/Minimal formatters in a future iteration? | Low | Out of scope per Non-Goals. Can be added later using the same `ChildNodes` data. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/TreeChildPopulator.cs` | Static helper for recursive child fetching from local cache |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ReadModels/WorkTree.cs` | Add `WorkTreeNode` record; add `ChildNodes` property; add `Build` overload for hierarchical children |
| `src/Twig/Commands/TreeCommand.cs` | Use `TreeChildPopulator` in sync (non-renderer) path; pass depth to recursive fetch |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Add `WriteTreeNode` recursive writer; update `FormatTree` to use `ChildNodes` when populated |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Add `WriteCompactTreeNode` recursive writer; update `FormatTree` to use `ChildNodes` when populated |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Use `TreeChildPopulator` for recursive child fetch; pass depth parameter |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `WriteTreeNode` recursive writer; update `FormatTree` to use `ChildNodes` when populated |
| `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs` | Add tests for `WorkTreeNode` and hierarchical `Build` overload |
| `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | Add tests for nested children in JSON output |
| `tests/Twig.Cli.Tests/Formatters/JsonCompactOutputFormatterTests.cs` | Add tests for nested children in compact JSON |
| `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | Add tests for recursive depth in MCP tree |

## ADO Work Item Structure

This is an Issue (#2069) — Tasks are defined directly under it.

### Issue #2069: twig tree --depth N: JSON output missing grandchildren

**Goal:** Fix `--depth N` to recursively populate children in JSON output for all
JSON-producing paths (CLI JSON, CLI JSON-compact, MCP).

**Prerequisites:** None.

**Tasks:**

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T-1 | Add `WorkTreeNode` record and `ChildNodes` property to `WorkTree`; add `Build` overload that accepts `IReadOnlyList<WorkTreeNode>` and derives flat `Children` from it | `src/Twig.Domain/ReadModels/WorkTree.cs` | S (~35 LoC) | TO DO |
| T-2 | Create `TreeChildPopulator` static helper with `PopulateAsync` for recursive depth-bounded child fetching from `IWorkItemRepository` | `src/Twig.Domain/Services/TreeChildPopulator.cs` (new) | S (~25 LoC) | TO DO |
| T-3 | Update `TreeCommand.ExecuteCoreAsync` sync path to call `TreeChildPopulator.PopulateAsync` and build `WorkTree` with hierarchical `ChildNodes` | `src/Twig/Commands/TreeCommand.cs` | S (~15 LoC) | TO DO |
| T-4 | Update `JsonOutputFormatter.FormatTree` with `WriteTreeNode` recursive writer; branch on `ChildNodes` vs flat `Children` fallback | `src/Twig/Formatters/JsonOutputFormatter.cs` | S (~35 LoC) | TO DO |
| T-5 | Update `JsonCompactOutputFormatter.FormatTree` with `WriteCompactTreeNode` recursive writer; branch on `ChildNodes` vs flat `Children` fallback | `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | S (~30 LoC) | TO DO |
| T-6 | Update MCP `ReadTools.Tree` to use `TreeChildPopulator.PopulateAsync` and pass `ChildNodes` to `WorkTree.Build` | `src/Twig.Mcp/Tools/ReadTools.cs` | S (~15 LoC) | TO DO |
| T-7 | Update MCP `McpResultBuilder.FormatTree` with `WriteTreeNode` recursive writer; branch on `ChildNodes` | `src/Twig.Mcp/Services/McpResultBuilder.cs` | S (~30 LoC) | TO DO |
| T-8 | Add domain unit tests: `WorkTreeNode` construction, `Build` overload with `ChildNodes`, flat `Children` derivation | `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs` | S (~50 LoC) | TO DO |
| T-9 | Add formatter unit tests: JSON and JSON-compact with depth 0, 1, 2; verify nested `children` arrays in output | `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs`, `JsonCompactOutputFormatterTests.cs` | M (~80 LoC) | TO DO |
| T-10 | Add MCP unit tests: `ReadToolsTreeTests` with grandchild mocking to verify recursive depth in MCP tree output | `tests/Twig.Mcp.Tests/Tools/ReadToolsTreeTests.cs` | S (~60 LoC) | TO DO |

**Acceptance Criteria:**
- [ ] `twig tree -o json --depth 2` includes grandchildren in nested `children` arrays
- [ ] `twig tree -o json-compact --depth 2` includes grandchildren in nested `children` arrays
- [ ] `twig tree -o json` (no --depth) shows direct children with `"children": []` on each
- [ ] `twig tree -o json --depth 0` shows empty children array
- [ ] MCP `twig_tree` with `depth: 2` includes grandchildren in nested structure
- [ ] Human/Spectre/Minimal formatters still work correctly (no regression)
- [ ] All existing tests pass
- [ ] New tests cover recursive depth 0, 1, 2, and 3+ scenarios

## PR Groups

### PG-1: Tree Depth JSON Grandchildren Fix

**Classification:** Deep (few files, complex recursive logic)
**Tasks:** T-1 through T-10
**Estimated LoC:** ~375
**Estimated Files:** ~11
**Successor:** None

**Rationale:** This is a cohesive, self-contained change that touches the domain model,
CLI command, formatters, MCP tools, and their tests. All changes are tightly coupled —
the recursive model, fetching, and rendering must ship together. The total LoC is well
within the ≤2000 limit, making a single PR appropriate.

**Execution order:**
1. Domain model (T-1, T-2) — foundation types and recursive fetch helper
2. CLI integration (T-3, T-4, T-5) — wire up TreeCommand and JSON formatters
3. MCP integration (T-6, T-7) — wire up ReadTools and McpResultBuilder
4. Tests (T-8, T-9, T-10) — validates all paths

---

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|-------|------|---------------|--------------|------|
| PG-1 | `PG-1-tree-depth-json-grandchildren` | #2069 / T-1 – T-10 | None | Deep |

### Execution Order

**PG-1** is the only group and has no predecessors. All ten tasks are delivered in a
single pull request in this internal order:

1. **Domain foundation (T-1, T-2):** `WorkTreeNode` record + `ChildNodes` property on
   `WorkTree`, plus the `TreeChildPopulator` static service. These are the base types
   and data-access helpers that everything else depends on. The codebase builds after
   this step, though no command yet uses the new types.

2. **CLI integration (T-3, T-4, T-5):** `TreeCommand` sync path, `JsonOutputFormatter`,
   and `JsonCompactOutputFormatter` are updated to call `TreeChildPopulator` and render
   recursive children. The codebase builds and `twig tree -o json --depth N` works end-
   to-end after this step.

3. **MCP integration (T-6, T-7):** `ReadTools.Tree` and `McpResultBuilder` gain the same
   recursive support. MCP consumers now receive nested `children` arrays.

4. **Tests (T-8, T-9, T-10):** Unit tests for the domain model, CLI formatters, and MCP
   layer are added last (against already-working code), ensuring all scenarios — depth 0,
   1, 2, 3+ — are verified before merge.

### Validation Strategy

**PG-1 — `PG-1-tree-depth-json-grandchildren`**

| Check | Method |
|-------|--------|
| Build passes | `dotnet build` — no warnings-as-errors regressions |
| Domain tests | `dotnet test tests/Twig.Domain.Tests` — WorkTreeNode, Build overload, flat-Children derivation |
| Formatter tests | `dotnet test tests/Twig.Cli.Tests` — JSON depth 0/1/2, compact JSON depth 0/1/2 |
| MCP tests | `dotnet test tests/Twig.Mcp.Tests` — ReadToolsTreeTests recursive depth mocking |
| Regression guard | Full `dotnet test` — Human/Spectre/Minimal formatters unaffected |
| Manual smoke | `twig tree -o json --depth 2` on a known item with grandchildren; verify nested `children` arrays in output |
