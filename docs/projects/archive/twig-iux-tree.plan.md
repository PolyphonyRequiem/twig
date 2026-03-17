# Twig IUX — Progressive Tree Rendering Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-003  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the UX specifications for the progressive tree rendering feature introduced in EPIC-003. The `twig tree` command now renders the parent chain and focused item immediately using Spectre.Console's `Tree` widget, then progressively adds children within a `Live()` context.

**Note**: `TreeCommand` reads entirely from SQLite cache (zero ADO calls). Progressive rendering here is a **visual enhancement** (structured Spectre.Console `Tree` widget with dimmed parents, bold focus, and live child loading) rather than a latency reduction.

---

## Rendering Pipeline

```
TreeCommand.ExecuteAsync
  ├── RenderingPipelineFactory.Resolve(format, noLive)
  │   ├── Human + TTY + !noLive → async path (SpectreRenderer)
  │   └── JSON / Minimal / piped / --no-live → sync path (IOutputFormatter)
  └── Async path:
      ├── SpectreRenderer.RenderTreeAsync receives data-fetching lambdas:
      │   1. getFocusedItem() → loads focused work item from cache
      │   2. getParentChain() → loads parent chain from cache
      │   3. getChildren() → loads children from cache
      └── Progressive rendering:
          Stage 1: Build Spectre Tree with parent chain (dimmed) + focused item (bold)
          Stage 2: Render tree immediately via Live() context
          Stage 3: Load children and add them as nodes progressively
```

---

## Tree Layout

### Parent Chain Nodes (dimmed)
- Format: `{TypeBadge} {Title (dim)} {State (colored)}`
- Parents are rendered from root ancestor → immediate parent
- Each parent is a nested child of the previous

### Focused Item Node (bold)
- Format: `{ActiveMarker} {TypeBadge} #{Id} {Title (bold)} {DirtyMarker} {State (colored)}`
- Active marker (`●`) shown when item matches `activeId`
- Dirty marker (`•`) shown in yellow when item has unsaved changes

### Children Nodes
- Format: `{ActiveMarker} {TypeBadge} #{Id} {Title} {DirtyMarker} {State (colored)}`
- Added progressively within the `Live()` context
- Respects `maxChildren` limit; truncated children show `... and N more`

---

## Backward Compatibility

- **`--output json`**: Uses sync `IOutputFormatter.FormatTree` path — unchanged
- **`--output minimal`**: Uses sync `IOutputFormatter.FormatTree` path — unchanged
- **`--no-live`**: Forces sync path even for human format
- **Piped output** (stdout redirected): Falls back to sync path automatically
- **Existing `TreeCommand` constructor**: Optional `RenderingPipelineFactory` parameter preserves backward compatibility with existing tests

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Use `IHasTreeNodes` interface for focus container | Allows children to be added to either `Tree` (root is focused) or `TreeNode` (focused item has parents) uniformly |
| `BuildSpectreTree` returns `(Tree, IHasTreeNodes)` tuple | Avoids post-hoc search for focus node; the builder knows which node is focused |
| Data-fetching lambdas instead of pre-loaded data | Matches `IAsyncRenderer.RenderTreeAsync` contract; enables progressive loading pattern |
| `Live()` context wraps entire tree | Enables progressive refresh as children are added one by one |
| Hoist focused item fetch in `TreeCommand.ExecuteAsync` | Avoids redundant `GetByIdAsync` round-trip in the `getParentChain` lambda; the focused item's `ParentId` is captured once and reused |
| Format methods (`FormatFocusedNode`, `FormatParentNode`, `BuildSpectreTree`) are `internal static` | Enables direct unit testing of markup strings (bold/dim) since `TestConsole` strips Spectre markup |
| Async tests avoid `GetAwaiter().GetResult()` | Prevents fragile sync-over-async patterns that can cause `AggregateException` wrapping or deadlocks if a sync context is introduced |

---

## Files Modified

| File | Change |
|------|--------|
| `src/Twig/Rendering/SpectreRenderer.cs` | Implemented `RenderTreeAsync` with progressive child loading |
| `src/Twig/Commands/TreeCommand.cs` | Added `RenderingPipelineFactory` integration, async/sync path split |
| `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | 16 tests covering async path, sync fallback, and SpectreRenderer |
