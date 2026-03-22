# Twig Rendering Fixes — Post-EPIC-003 Regressions

> **Status:** Draft  
> **Date:** 2026-03-21  
> **Severity:** Medium — visual regressions, no data loss or functional breakage  
> **Prerequisite:** Land after EPIC-004 working tree is committed. Independent of EPIC-005.

---

## Summary

Five rendering issues were identified after EPIC-003 (process-aware workspace display) landed. Four are visual regressions or inconsistencies; one (FIX-003) is a structural correction — the sprint view should group by assignee at top level, not by state category. No data loss or functional breakage. All can be fixed in a single pass.

---

## Findings

### FIX-001: Status panel bottom border clipped after sync indicator clears ✅ DONE (tests revised)

**Severity:** High  
**Affected command:** `twig status`  
**File:** `src/Twig/Commands/StatusCommand.cs` (lines 82–85), `src/Twig/Rendering/SpectreRenderer.cs` (`RenderWithSyncAsync`)

**Root cause:**  
`StatusCommand.ExecuteAsync` calls `renderer.RenderStatusAsync()` which writes panels directly to the console via `_console.Write(itemPanel)`. It then calls `renderer.RenderWithSyncAsync()` with a `buildCachedView` that returns `new Spectre.Console.Text(string.Empty)`.

`RenderWithSyncAsync` creates a `Live()` region initialized with this empty renderable. During sync, it shows a status line (`⟳ syncing...` → `✓ up to date`). After the delay, it calls `ctx.UpdateTarget(cachedView)` — which is the empty `Text`. Spectre's `Live()` measures the new renderable at 0 lines and erases the vertical space it previously occupied. Since that space overlaps with the bottom border of the panel written *before* the Live region started, the border gets clipped.

**Fix:**  
Replace `new Spectre.Console.Text(string.Empty)` with `new Spectre.Console.Text(" ")` (single space) so the Live region always reserves at least one terminal line when it collapses back to the cached view. This prevents Spectre from erasing into the content above.

```csharp
// Before (StatusCommand.cs:84)
new Spectre.Console.Text(string.Empty)

// After
new Spectre.Console.Text(" ")
```

**Test:** Existing `RenderWithSyncAsync` tests should verify the cached view is rendered. Add a test confirming the Live region's final renderable is non-empty (measures ≥ 1 line).

---

### FIX-002: State column lacks explicit `TableColumn` construction ✅ DONE

**Severity:** Low  
**Affected command:** `twig workspace`, `twig sprint`  
**File:** `src/Twig/Rendering/SpectreTheme.cs` (`CreateWorkspaceTable`, line 130)

**Root cause:**  
Three of the four core columns use the explicit `TableColumn` constructor:
- ID → `new TableColumn("[bold]ID[/]").RightAligned()`
- Type → `new TableColumn("[bold]Type[/]").Centered()`
- Title → `"[bold]Title[/]"` (string overload — left-aligned, acceptable for long text)
- State → `"[bold]State[/]"` (string overload — no explicit alignment)

The State column uses the string overload, which skips alignment control. State values (`Active`, `New`, `In Progress`) are short, fixed-vocabulary labels — right-alignment groups them visually at the column edge and improves scanability in the grouped layout introduced by EPIC-003.

**Fix:**  
Replace the string overload with an explicit `new TableColumn("[bold]State[/]").RightAligned()`.

```csharp
// Before
.AddColumn("[bold]State[/]");

// After
.AddColumn(new TableColumn("[bold]State[/]").RightAligned());
```

**Test:** Update `CreateWorkspaceTable` unit tests (if any) to verify 4 columns are created. Visual verification via `twig workspace`.

---

### FIX-003: Sprint view should group by assignee, not state category ✅ DONE

**Severity:** Medium  
**Affected command:** `twig sprint` (team view, `--all`)  
**Files:** `src/Twig/Formatters/HumanOutputFormatter.cs` (`FormatSprintView`, `RenderHierarchicalSprintForCategory`)

**Root cause:**  
EPIC-003 introduced state category grouping (Proposed → In Progress → Resolved → Completed) as the top-level organizer for the sprint view. Within each category, items are further grouped by assignee. This produces a 3-level nesting structure before work item content appears:

```
  Sprint (9 items):          ← 2 spaces (sprint header)
    In Progress (5)          ← 4 spaces (category header)
      Daniel Green (5):      ← 6 spaces (assignee header)
        ◆ Epic: Payment...   ← 8 spaces (root item)
        └── ◇ Feature:...   ← 8 + 4 = 12 spaces (child item)
            └── □ Task:...   ← 8 + 4 + 4 = 16 spaces (grandchild item)
```

Two problems:
1. **Wrong primary axis.** The sprint (team) view answers "who's doing what?" — the standup question. Assignee should be the top-level grouping, not state. State is secondary information already shown per-item.
2. **Excessive indentation.** The category layer adds 2 characters of nesting at every level. At 16 characters of indent, titles are truncated on 80-column terminals.

**Note:** The sprint view (`all=true`) always flows through `HumanOutputFormatter.FormatSprintView` — the Spectre async path is gated by `!all` and only serves the personal workspace view. The Spectre workspace path's state category grouping is appropriate for the personal "what's in flight vs. done?" question and is not changed here.

**Fix:**  
Remove state category grouping from `FormatSprintView`. Group by assignee at top level. Items under each assignee render with hierarchy (box-drawing connectors) as today, but without the category wrapper.

Target layout:

```
  Sprint (9 items):
    Daniel Green (5):
      ◆ Epic: Payment Refactor                    [Active]
      └── ◇ Feature: Retry Logic                  [New]
          └── □ Task: Add timeout                  [Active]
    Sarah Chen (4):
      ◇ Feature: Dark Mode                        [Active]
      └── □ Task: Fix CSS alignment                [Active]
```

Maximum indent depth: `2 (sprint) + 2 (assignee) + 4 (root item offset) + 4 (connector) = 12` for grandchild items (was 16).

**Implementation changes:**
1. **`FormatSprintView`** — Remove the `GroupByStateCategory` call and the `foreach (var (category, categoryItems) in categoryGroups)` loop. Replace with direct iteration over `ws.Hierarchy.AssigneeGroups` (when hierarchy is available) or over items grouped by `item.AssignedTo` (flat path). ✅
2. **New `RenderHierarchicalSprint` method** — Iterates `hierarchy.AssigneeGroups` directly: for each assignee, renders all root nodes and children without category filtering. Replaces `RenderHierarchicalSprintForCategory`. ✅
3. **New `RenderFlatSprint` method** — Groups `ws.SprintItems` by assignee without category wrapper. Replaces `RenderFlatSprintForCategory`. ✅
4. **Removed `RenderHierarchicalSprintForCategory` and `RenderFlatSprintForCategory`** — Dead after the above changes. ✅
5. **`CountSprintItemsInCategory` removed** (dead after category removal). **`NodeOrDescendantBelongsToCategory` retained** — still called by `FormatWorkspace` (personal view); not dead. `CollectHierarchyChildrenForCategory` also retained for same reason. ✅
6. **Indent widths** — Assignee header at 4 spaces (was 6 under category), root item at 6 spaces (was 8). Box-drawing connectors remain 4 characters wide. ✅

**New helper added:** `CountSprintItems` — counts all sprint items in a hierarchy node tree (no category filter); replaces `CountSprintItemsInCategory`.

**Note:** `CollectHierarchyChildren` (non-ForCategory variant) is now actively used by `RenderHierarchicalSprint` and is not dead code — this makes FIX-005 moot by construction.

**Not changed:**
- `FormatWorkspace` (personal view, `!all`) — not affected; uses its own rendering path.
- `SpectreRenderer.RenderWorkspaceAsync` (personal view async path) — retains state category grouping, which is appropriate for "my items by status."
- `GroupByStateCategory` utility method — retained for `SpectreRenderer` and potentially `FormatWorkspace`.

**Test:** Update existing `FormatSprintView` tests to expect assignee-first grouping without category headers. Remove tests specific to category-filtered rendering. Visual verification via `twig sprint`.

---

### FIX-004: State value bracket wrapping inconsistency between formatters

**Severity:** Low  
**Affected commands:** `twig workspace`, `twig sprint`, `twig tree`, `twig status`  
**Files:** `src/Twig/Rendering/SpectreRenderer.cs` (`FormatState`), `src/Twig/Formatters/HumanOutputFormatter.cs` (`CollectHierarchyNodeLine`)

**Root cause:**  
The two rendering paths format state differently:

| Formatter | State format | Example |
|-----------|-------------|---------|
| `HumanOutputFormatter` | `[{color}{state}{reset}]` | `[Active]` (with ANSI color inside brackets) |
| `SpectreRenderer.FormatState()` | `[{color}]{state}[/]` | `Active` (colored, no brackets) |
| `SpectreRenderer` status header | `[[{summaryState}]]` | `[Active]` (brackets via Spectre `[[` escape) |

The status header line wraps the state in `[[...]]` (Spectre escape for literal square brackets), matching the HumanOutputFormatter. But the workspace table's state column and tree view nodes omit brackets. This creates a visual inconsistency: `twig status` shows `[Active]` but `twig workspace` shows `Active`.

**Fix:**  
Add bracket wrapping to `SpectreRenderer.FormatState()` so all Spectre-rendered state values include brackets:

```csharp
// Before (SpectreTheme.cs, FormatState)
return $"[{color}]{Markup.Escape(state)}[/]";

// After
return $"[{color}][[[/]{Markup.Escape(state)}[{color}]]][/]";
```

Wait — that's overly complex with Spectre markup escaping. Simpler approach: wrap in literal bracket characters *outside* the color span:

```csharp
return $"[[{color}]{Markup.Escape(state)}[/]]]";
```

This renders as `[Active]` with color applied to the state text and brackets in default color. Matches what HumanOutputFormatter produces.

**Test:** Update tree and workspace Spectre rendering tests to expect bracketed state values. Visual verification across all commands.

---

### FIX-005: Dead code — `CollectHierarchyChildren` (non-ForCategory variant) ⚠️ MOOT

**Severity:** Trivial  
**File:** `src/Twig/Formatters/HumanOutputFormatter.cs` (lines ~403–413)

**Root cause:**  
`CollectHierarchyChildren` (the non-`ForCategory` variant) was the original method before EPIC-003 added category-filtered rendering. After EPIC-003, all call sites use `CollectHierarchyChildrenForCategory`. The original method is now only self-referential — it calls itself recursively but has zero external callers. This was noted in the EPIC-003 review as a missed cleanup.

**Fix:**  
~~Remove the method entirely.~~

**Status:** **Moot after FIX-003.** `CollectHierarchyChildren` is now called by the new `RenderHierarchicalSprint` method introduced in FIX-003. It was never dead after FIX-003 landed. No action needed.

**Test:** N/A — method is actively used.

---

## Implementation Sequence

All five fixes are independent and can be applied in any order. Recommended grouping for a single commit:

```
FIX-001: Status panel clipping (StatusCommand.cs) — 1-line change
FIX-002: State column alignment (SpectreTheme.cs) — 1-line change
FIX-003: Sprint view assignee grouping (HumanOutputFormatter.cs) — restructure FormatSprintView,
         remove category-filtered renderers + helpers, add assignee-first renderers
FIX-004: State bracket wrapping (SpectreTheme.cs) — 1-line change in FormatState
FIX-005: Dead code removal (HumanOutputFormatter.cs) — delete ~10 lines
```

**Estimated scope:** FIX-001/002/004/005 are trivial (~5 lines total). FIX-003 is a medium restructure of `FormatSprintView` — removes ~60 lines of category-filtered code, replaces with ~40 lines of assignee-first rendering. Net reduction in complexity. No new files. No API changes. No domain model changes.

**Testing strategy:**
1. Unit tests: update `FormatSprintView` assertions for assignee-first grouping (FIX-003), bracketed state (FIX-004)
2. Visual verification: `twig status`, `twig workspace`, `twig sprint` against a real ADO project
3. All existing tests must pass (no regressions)

---

## What This Plan Does NOT Change

- **Rendering infrastructure** — `RenderingPipelineFactory`, `IOutputFormatter`, `IAsyncRenderer` are untouched
- **Data models** — No domain changes
- **Feature behavior** — All fixes are visual-only
- **EPIC-004 work** — Dynamic columns are orthogonal; these fixes apply to the core column layout
- **EPIC-005 plan** — Unparented item rendering is unaffected
