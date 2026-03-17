# Twig IUX — Progressive Workspace Rendering Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-002  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the UX specifications for the progressive workspace rendering feature introduced in EPIC-002. The `twig workspace` command now renders data progressively using Spectre.Console's `Live()` context, showing a loading state immediately and populating the table as data arrives in stages.

---

## Rendering Pipeline

```
WorkspaceCommand.ExecuteAsync
  ├── RenderingPipelineFactory.Resolve(format, noLive)
  │   ├── Human + TTY + !noLive → async path (SpectreRenderer)
  │   └── JSON / Minimal / piped / --no-live → sync path (IOutputFormatter)
  └── Async path:
      ├── StreamWorkspaceData() yields chunks:
      │   1. ContextLoaded(contextItem)
      │   2. SprintItemsLoaded(items)
      │   3. SeedsLoaded(seeds)
      └── SpectreRenderer.RenderWorkspaceAsync processes each chunk
```

---

## Table Layout

| Column | Header | Alignment | Width | Content |
|--------|--------|-----------|-------|---------|
| 1 | **ID** | Right | Auto | Work item ID (negative for seeds, dimmed) |
| 2 | **Type** | Center | Auto | Unicode badge glyph with deterministic color |
| 3 | **Title** | Left | Expand | Escaped title text; seeds may have `⚠ stale` suffix |
| 4 | **State** | Left | Auto | State text with category-based color |

- **Table border**: `TableBorder.Rounded`
- **Table expand**: `true` (fills terminal width)

---

## Loading State

- **Initial state**: Single row with `[dim]Loading workspace...[/]` spanning four columns
- **Cleared on**: First `WorkspaceDataChunk` received
- **Target**: Loading row visible within 100ms of invocation

---

## Caption / Header

| Scenario | Caption Text |
|----------|-------------|
| Context set | `Active: #<id> <title>` |
| No context | `[dim]No active context[/]` |
| Refresh in progress | `[yellow]⟳ refreshing...[/]` |
| Refresh completed | Restores original caption |

---

## Seeds Section

When seeds are present, rendered below sprint items with a visual separator:

```
───  ───  Seeds  ───
-1   □   Seed Task
-2   □   Stale Seed ⚠ stale
```

- Separator row uses `[dim]` markup
- Seed IDs (negative) rendered in `[dim]` style
- Stale seeds (older than `config.seed.staleDays`) show `[yellow]⚠ stale[/]` badge after title

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

## Spinner Text

No separate spinner widget is used. The `[dim]Loading workspace...[/]` row within the `Live()` table context serves as the loading indicator. This follows DD-009 (single `Live()` context — no Status→Live transition).

---

## Fallback Behavior

| Condition | Behavior |
|-----------|----------|
| `--output json` | Sync path: `JsonOutputFormatter.FormatWorkspace()` |
| `--output minimal` | Sync path: `MinimalOutputFormatter.FormatWorkspace()` |
| `--no-live` | Sync path: `HumanOutputFormatter.FormatWorkspace()` |
| Piped output | Sync path (detected via `Console.IsOutputRedirected`) |
| `--all` (sprint view) | Sync path (hierarchy building requires all data upfront) |

---

## Hint Integration

After the progressive rendering stream completes:
1. `Workspace.Build()` is called with closure-captured data
2. `HintEngine.GetHints("workspace", ...)` computes hints
3. `SpectreRenderer.RenderHints()` renders hints below the table as `[dim]  hint: <text>[/]`
