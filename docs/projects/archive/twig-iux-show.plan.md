# Twig IUX — Progressive Work Item Detail View Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-007  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the UX specifications for the progressive work item detail view feature introduced in EPIC-007. The `RenderWorkItemAsync` method renders work item core fields (title, type, state, assigned, area, iteration) immediately in a Spectre.Console `Panel`, then progressively populates extended fields (description, history, tags) from the `WorkItem.Fields` dictionary.

---

## Rendering Pipeline

```
Command.ExecuteAsync (future `twig show <id>` or extended `twig status`)
  ├── RenderingPipelineFactory.Resolve(format, noLive)
  │   ├── Human + TTY + !noLive → async path (SpectreRenderer)
  │   └── JSON / Minimal / piped / --no-live → sync path (IOutputFormatter)
  └── Async path:
      ├── SpectreRenderer.RenderWorkItemAsync receives:
      │   1. getItem() → returns the work item to display
      │   2. showDirty → whether to display dirty indicator
      └── Progressive rendering:
          ├── Stage 1: Load WorkItem from getItem()
          ├── Stage 2: Render core fields panel immediately
          ├── Stage 3: Load and render Description (from Fields["System.Description"])
          ├── Stage 4: Load and render History (from Fields["System.History"])
          └── Stage 5: Load and render Tags (from Fields["System.Tags"])
```

---

## Panel Layout

### Work Item Detail Panel

A Spectre.Console `Panel` with `BoxBorder.Rounded` containing a `Grid` of work item fields, rendered progressively.

#### Core Fields (Stage 2 — immediate)

| Row | Label | Content |
|-----|-------|---------|
| 1 | Type | Type badge glyph + type name (deterministic color) |
| 2 | State | State text with category-based color |
| 3 | Assigned | Assignee name or `(unassigned)` |
| 4 | Area | Area path |
| 5 | Iteration | Iteration path |

#### Extended Fields (Stage 3–5 — progressive)

| Row | Label | Content | Source |
|-----|-------|---------|--------|
| 6 | Description | Truncated plain text (max 200 chars), HTML stripped | `Fields["System.Description"]` |
| 7 | History | Truncated plain text (max 200 chars), HTML stripped | `Fields["System.History"]` |
| 8 | Tags | Truncated plain text (max 200 chars), HTML stripped | `Fields["System.Tags"]` |

- **Panel header**: `[bold]#<id> <title>[/]{dirty marker}`
- **Dirty marker**: `[yellow]•[/]` when `showDirty && item.IsDirty` is true
- **Panel expand**: `true` (fills terminal width)
- **Panel border**: `BoxBorder.Rounded`

---

## Progressive Rendering Strategy

1. **Immediate render**: Core fields are displayed as soon as the work item is loaded via `getItem()`. The panel appears on screen before any extended fields are processed.
2. **Field-by-field update**: Each extended field is checked in sequence (Description → History → Tags). When present and non-empty, the field is appended to the grid and the panel is re-rendered via `LiveDisplayContext.Refresh()`.
3. **HTML stripping**: ADO fields often contain HTML markup. A simple tag-stripping algorithm removes `<...>` sequences before display.
4. **Truncation**: Extended field values are truncated to 200 characters with an `…` suffix to keep the panel compact.
5. **Cancellation**: `CancellationToken` is checked between stages to support responsive cancellation.

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

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| Null item from getItem() | Early return, no output |
| No extended fields in Fields dictionary | Only core fields panel is shown |
| Description contains HTML | HTML tags stripped before display |
| Description exceeds 200 chars | Truncated with `…` suffix |
| History contains HTML | HTML tags stripped before display |
| History exceeds 200 chars | Truncated with `…` suffix |
| Tags contains HTML | HTML tags stripped before display |
| Tags exceeds 200 chars | Truncated with `…` suffix |
| showDirty=false with dirty item | No dirty marker shown |
| showDirty=true with clean item | No dirty marker shown |
| Empty/whitespace-only extended fields | Field row not added |
| Cancellation during progressive loading | `OperationCanceledException` propagated cleanly |

---

## Fallback Behavior

| Condition | Behavior |
|-----------|----------|
| `--output json` | Sync path: formatter handles work item rendering |
| `--output minimal` | Sync path: formatter handles work item rendering |
| `--no-live` | Sync path: formatter handles work item rendering |
| Piped output | Sync path (detected via `Console.IsOutputRedirected`) |

---

## Internal Helpers

### `TruncateField(string value, int maxLength)`

Strips HTML tags and truncates to `maxLength` characters with `…` suffix. Used for Description, History, and Tags fields.

### `StripHtmlTags(string input)`

Regex-free HTML tag removal. Iterates characters, buffering content between `<` and `>`. Only discards buffered content when a matching `>` is found. If another `<` is encountered before `>`, or end-of-string is reached while inside an unclosed `<`, the buffered content (including the `<`) is flushed as literal text. AOT-safe (no `System.Text.RegularExpressions` dependency).
