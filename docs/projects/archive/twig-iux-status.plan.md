# Twig IUX — Progressive Status Dashboard Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-004  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the UX specifications for the progressive status dashboard feature introduced in EPIC-004. The `twig status` command now renders a panel-based dashboard using Spectre.Console's `Panel` and `Grid` widgets, showing work item details and pending change summaries in a structured layout. Contextual hints from `HintEngine` are rendered below the dashboard panels.

---

## Rendering Pipeline

```
StatusCommand.ExecuteAsync
  ├── RenderingPipelineFactory.Resolve(format, noLive)
  │   ├── Human + TTY + !noLive → async path (SpectreRenderer)
  │   └── JSON / Minimal / piped / --no-live → sync path (IOutputFormatter)
  └── Async path:
      ├── SpectreRenderer.RenderStatusAsync receives data-fetching lambdas:
      │   1. getItem() → returns the pre-fetched active work item
      │   2. getPendingChanges() → loads pending changes from store
      └── Post-render:
          ├── Load seeds for stale seed count
          ├── HintEngine.GetHints("status", ...)
          └── renderer.RenderHints(hints)
```

---

## Dashboard Layout

### Work Item Panel

A Spectre.Console `Panel` with `BoxBorder.Rounded` containing a `Grid` of work item details.

| Row | Label | Content |
|-----|-------|---------|
| 1 | Type | Type badge glyph + type name (deterministic color) |
| 2 | State | State text with category-based color |
| 3 | Assigned | Assignee name or `(unassigned)` |
| 4 | Area | Area path |
| 5 | Iteration | Iteration path |

- **Panel header**: `[bold]#<id> <title>[/]{dirty marker}`
- **Dirty marker**: `[yellow]•[/]` when `item.IsDirty` is true
- **Panel expand**: `true` (fills terminal width)
- **Panel border**: `BoxBorder.Rounded`

### Pending Changes Panel

Shown only when `pending.Count > 0`. A Spectre.Console `Panel` with a `Grid` summarizing change counts.

| Row | Label | Content |
|-----|-------|---------|
| 1 | Field changes | Count of non-note pending changes |
| 2 | Notes | Count of note-type pending changes |

- **Panel header**: `[bold]Pending Changes[/]`
- **Panel border**: `BoxBorder.Rounded`
- **Panel expand**: `true`

### Hints Section

Rendered below the panels via `SpectreRenderer.RenderHints()`:
- Format: `[dim]  hint: {escaped hint text}[/]`
- Stale seed hint: `⚠ N stale seed(s). Consider completing or cutting them.`

---

## Color Mapping

### Type Badge Colors

| Type | Badge | Color Source |
|------|-------|-------------|
| Epic | ◆ | `DeterministicTypeColor.GetAnsiEscape` → Spectre color name |
| Feature | ▪ | Same deterministic mapping |
| User Story / PBI | ● | Same |
| Bug / Impediment | ✦ | Same |
| Task / Test Case | □ | Same |

### State Colors

| State Category | Color |
|----------------|-------|
| Proposed | `grey` |
| InProgress | `blue` |
| Resolved / Completed | `green` |
| Removed | `red` |
| Unknown | `default` |

---

## Fallback Behavior

| Condition | Behavior |
|-----------|----------|
| `--output json` | Sync path: `JsonOutputFormatter.FormatWorkItem()` + `FormatInfo()` |
| `--output minimal` | Sync path: `MinimalOutputFormatter.FormatWorkItem()` + `FormatInfo()` |
| `--no-live` | Sync path: `HumanOutputFormatter.FormatWorkItem()` + `FormatInfo()` |
| Piped output | Sync path (detected via `Console.IsOutputRedirected`) |
| No `RenderingPipelineFactory` | Sync path (backward compat — optional constructor parameter) |

---

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| No active work item | Error message, exit code 1 |
| Item not found in cache | Error message with item ID, exit code 1 |
| No pending changes | Work item panel only, no pending changes panel |
| Null item from getItem | No output (early return) |
| Item with no assignee | Shows `(unassigned)` |
| Dirty item | Yellow `•` marker in panel header |
