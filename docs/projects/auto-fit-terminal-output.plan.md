---
work_item_id: 2548
title: "Auto-Fit Terminal Output — Width-Aware Rendering Across All Commands"
type: Epic
---

# Auto-Fit Terminal Output — Solution Design & Implementation Plan

| Field | Value |
|---|---|
| **Work Item** | #2548 — Auto-Fit Terminal Output — Width-Aware Rendering Across All Commands |
| **Type** | Epic |
| **Author** | Generated via codebase analysis |
| **Status** | 📋 Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan introduces a centralized width-budget system for twig's terminal
rendering. Today, `SpectreRenderer` renders all titles, area paths, iteration
paths, and assigned-to fields at full length — causing horizontal overflow and
multi-line wrapping when terminals are narrower than 120 characters. The only
width-aware code in the entire renderer is a single `_console.Profile.Width < 80`
check in `RenderInteractiveTreeAsync` and one hardcoded `rawTitle.Length > 56`
truncation in `BuildPreviewPanel`. This plan introduces a `WidthBudget` readonly
record struct that computes column-space budgets from the console width across
three responsive breakpoints (Compact 60–79, Standard 80–119, Wide 120+), along
with `TruncateTitle` and `TruncatePath` static helpers. These are then applied
systematically to all nine rendering surfaces — workspace flat table, workspace
tree, focused tree, status panel, work-item panel, seed view, area view,
interactive tree navigator, and flow summary — ensuring no horizontal overflow
at 60 characters while preserving full-fidelity output at 120+.

---

## Background

### Current State

`SpectreRenderer` (2,249 lines) is the sole `IAsyncRenderer` implementation. It
renders all human-readable CLI output using Spectre.Console's `Table`, `Tree`,
`Panel`, `Grid`, `Live`, and `Markup` components. The class is injected with
`IAnsiConsole` (via primary constructor) and `SpectreTheme`, making
`_console.Profile.Width` available to every method — but almost no method uses it.

**Width-awareness today is near-zero:**

| Location | Code | Purpose |
|---|---|---|
| `SpectreRenderer.cs:1782` | `_console.Profile.Width < 80` | Single vs. dual-column layout in interactive tree |
| `SpectreRenderer.cs:2190` | `rawTitle.Length > 56 ? rawTitle[..56] + "..."` | Hardcoded title truncation in preview panel |
| `SpectreRenderer.cs:1090,1099` | `TruncateField(value, 200)` | History/Tags truncation in `RenderWorkItemAsync` |
| `SpectreRenderer.cs:1158,1182` | `FormatFieldValue(value, dataType, maxWidth: 60)` | Extended field values in status views |

**Titles are never truncated** in: workspace table rows (`RenderWorkspaceAsync`
line 341), workspace tree labels (`FormatWorkspaceTreeNodeLabel` lines 579/582/585),
focused tree child nodes (`RenderTreeAsync` line 697), seed view rows
(`RenderSeedViewAsync` line 1638), area view nodes (`FormatAreaNode`), status
panel headers (`BuildStatusViewAsync` line 1008), work-item panel headers
(`RenderWorkItemAsync` line 1074), selection prompts (`BuildSelectionRenderable`
lines 1376/1426), and flow summary headers (`RenderFlowSummaryAsync` line 1522).

**Area and iteration paths** are rendered as full backslash-delimited strings
(e.g. `Project\Team\SubArea\Deep\Leaf`) in `BuildStatusViewAsync` (lines 923–924)
and `RenderWorkItemAsync` (lines 1056–1057). Only `BuildPreviewPanel` extracts
the last segment for iteration (lines 2204–2208).

**Spectre's `Table.Expand()`** fills the terminal width and internally wraps cell
content — but wrapping produces multi-line rows that destroy visual alignment and
readability. The fix must truncate content *before* Spectre receives it.

### Call-Site Audit — Rendering Methods

All rendering flows through `SpectreRenderer` implementing `IAsyncRenderer`. Each
method, its production callers, and the width-sensitive fields it renders:

| Method | Production Caller(s) | Title Truncated? | Area/Iteration Shown? | Assigned-To Shown? |
|---|---|---|---|---|
| `RenderWorkspaceAsync` (line 59) | `WorkspaceCommand.cs:192` | ❌ No | ❌ No | Team-view only, not truncated |
| `RenderWorkspaceTreeAsync` (line 372) | Internal from `RenderWorkspaceAsync:69` | ❌ No | ❌ No | ❌ No |
| `FormatWorkspaceTreeNodeLabel` (line 561) | Internal from tree rendering (lines 512, 539) | ❌ No | ❌ No | ❌ No |
| `RenderTreeAsync` (line 654) | `TreeRenderingService.cs:79` → `ShowCommand:60`, `WorkspaceCommand:256` | ❌ No | ❌ No | ❌ No |
| `BuildStatusViewAsync` (line 895) | `RenderStatusAsync:1032`, `ShowCommand:205,235` | ❌ No (header) | ✅ Full path | ✅ Not truncated |
| `RenderWorkItemAsync` (line 1040) | `IAsyncRenderer` contract | ❌ No (header) | ✅ Full path | ✅ Not truncated |
| `RenderSeedViewAsync` (line 1565) | `SeedViewCommand.cs:45` | ❌ No | ❌ No | ❌ No |
| `RenderAreaViewAsync` (line 1651) | `AreaCommand.cs:65,117` | ❌ No | ❌ No (filter paths at top) | ❌ No |
| `RenderInteractiveTreeAsync` (line 1774) | `NavigationCommands.cs:60` | ⚠️ 56-char hardcoded (preview only) | Iteration last-segment only (preview) | ✅ Preview only |
| `BuildSelectionRenderable` (lines 1352, 1388) | `PromptDisambiguationAsync:1283` | ❌ No | ❌ No | ❌ No |
| `RenderFlowSummaryAsync` (line 1514) | Interface contract (no production caller found) | ❌ No | ❌ No | ❌ No |
| `TruncateField` (line 1196) | `RenderWorkItemAsync:1090,1099` | N/A (fields only) | N/A | N/A |

### Existing Truncation Utilities

| Utility | Location | Behavior |
|---|---|---|
| `SpectreRenderer.TruncateField(value, maxLength)` | Line 1196 | Strip HTML + truncate to fixed char count |
| `FormatterHelpers.Truncate(value, maxLength)` | Line 429 | Simple `trimmed[..(maxLength-1)] + "…"` |
| `FormatterHelpers.FormatFieldValue(value, dataType, maxWidth)` | Line 22 | Type-aware formatting with truncation |
| `BuildPreviewPanel` inline truncation | Line 2190 | `rawTitle[..56] + "..."` |

---

## Problem Statement

1. **Horizontal overflow at narrow terminals.** When a user runs `twig workspace`,
   `twig tree`, or `twig show` in a terminal narrower than ~100 characters, titles
   wrap to multiple lines, destroying the tabular/tree alignment. A 60-character
   terminal is common in side-by-side editor splits.

2. **No responsive rendering.** The renderer has no concept of available space
   beyond a single `< 80` check. Fixed-width fields (ID, type badge, state badge,
   tree indentation) consume a known number of characters, but the remaining
   "title budget" is never computed or enforced.

3. **Hardcoded magic numbers.** The preview panel truncates at 56 characters,
   extended fields at 60, and history/tags at 200 — all without reference to the
   actual console width.

4. **Area/iteration paths overflow.** Deep ADO hierarchies produce paths like
   `Org\Project\Team\SubTeam\Sprint 42` that consume 40+ characters, leaving
   almost nothing for other fields at narrow widths.

---

## Goals and Non-Goals

### Goals

1. **No horizontal overflow at 60 characters.** Every rendering surface must
   produce output that fits within a 60-character terminal without wrapping.
2. **Responsive breakpoints.** Three tiers — Compact (60–79), Standard (80–119),
   Wide (120+) — that control how aggressively titles and paths are truncated.
3. **Truncation with ellipsis.** Truncated titles end with `…`, truncated paths
   show `…\LastSegment` or `…\Last\TwoSegments` depending on budget.
4. **Tree-depth-aware budgets.** Tree node labels deduct 2 characters per indent
   level from the title budget (matching Spectre's tree indent width).
5. **Centralized width logic.** A single `WidthBudget` value object computes all
   budgets from console width — no more scattered magic numbers.
6. **No visual regression at 120+.** Wide terminals should see the same output
   quality as today (full titles, full paths).
7. **Test coverage at all breakpoints.** Tests for 60, 80, and 120+ widths for
   every affected rendering surface.

### Non-Goals

- **Column-width constraints on Spectre tables.** Spectre's `TableColumn.Width(n)`
  would require computing exact widths for every column, which is fragile. Instead,
  truncate content before passing to Spectre and let `Expand()` handle distribution.
- **Dynamic column hiding.** Not hiding columns (e.g., dropping State at compact) —
  only truncating content.
- **HumanOutputFormatter changes.** The non-Spectre formatter path has its own
  width handling. Out of scope for this epic.
- **Minimum width enforcement.** Not enforcing a minimum console width or showing
  a warning below 60 characters — just graceful degradation.
- **Assigned-to column truncation in workspace tables.** Assigned-to is already
  short (display names); truncation to 20 chars is applied in panels only.

---

## Requirements

### Functional

| ID | Requirement |
|---|---|
| FR-01 | `WidthBudget` computes `TitleBudget`, `AssignedToBudget`, `PathBudget` from console width |
| FR-02 | `TreeTitleBudget(int depth)` deducts `depth × 2` chars from the title budget |
| FR-03 | `TruncateTitle(title, budget)` returns `title[..budget-1] + "…"` when title exceeds budget |
| FR-04 | `TruncatePath(path, budget)` returns last N segments with `…\` prefix when path exceeds budget |
| FR-05 | All 9 rendering surfaces use `WidthBudget` for title truncation |
| FR-06 | `BuildStatusViewAsync` and `RenderWorkItemAsync` truncate area/iteration paths via `TruncatePath` |
| FR-07 | `BuildPreviewPanel` replaces hardcoded `56` with `WidthBudget.TreeTitleBudget(0)` |
| FR-08 | `SpectreRenderer.TruncateField` is removed; callers migrate to `WidthBudget` helpers |
| FR-09 | `RenderBreakpoint` enum has values `Compact`, `Standard`, `Wide` |

### Non-Functional

| ID | Requirement |
|---|---|
| NF-01 | `WidthBudget` is a `readonly record struct` (zero-allocation, value semantics) |
| NF-02 | All truncation helpers are `static` pure functions (no side effects) |
| NF-03 | No new DI registrations required — `WidthBudget` is created inline from `_console.Profile.Width` |
| NF-04 | AOT-compatible — no reflection, no dynamic code generation |
| NF-05 | Minimum title budget clamp at 10 characters to prevent degenerate output |

---

## Proposed Design

### Architecture Overview

```
┌────────────────────────┐
│  _console.Profile.Width │  ← Console width (e.g. 60, 80, 120)
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│      WidthBudget       │  ← readonly record struct
│  ─────────────────────  │
│  ConsoleWidth: int     │
│  Breakpoint: enum      │
│  TitleBudget: int      │
│  PathBudget: int       │
│  AssignedToBudget: int │
│  TreeTitleBudget(d): int│
└──────────┬─────────────┘
           │  created at start of each render method
           ▼
┌────────────────────────────────────────────────┐
│         Truncation Helpers (static)            │
│  TruncateTitle(string title, int budget)       │
│  TruncatePath(string path, int budget)         │
└──────────┬─────────────────────────────────────┘
           │  called inline during label/row building
           ▼
┌─────────────────────────────────────────────────────┐
│              SpectreRenderer Methods                │
│  RenderWorkspaceAsync   │  FormatWorkspaceTreeLabel │
│  RenderTreeAsync        │  BuildStatusViewAsync     │
│  RenderWorkItemAsync    │  RenderSeedViewAsync      │
│  RenderAreaViewAsync    │  BuildPreviewPanel        │
│  BuildSelectionRenderable  RenderFlowSummaryAsync   │
└─────────────────────────────────────────────────────┘
```

**No DI changes.** `WidthBudget` is a pure value object created inline:
```csharp
var budget = new WidthBudget(_console.Profile.Width);
```

**No interface changes to `IAsyncRenderer`.** Width budgets are an internal
rendering concern — callers don't know or care about truncation.

### Key Components

#### 1. `WidthBudget` readonly record struct

**File:** `src/Twig/Rendering/WidthBudget.cs`

```csharp
namespace Twig.Rendering;

/// <summary>
/// Responsive breakpoints for terminal-width-aware rendering.
/// </summary>
public enum RenderBreakpoint
{
    /// <summary>60–79 characters. Aggressive truncation.</summary>
    Compact,
    /// <summary>80–119 characters. Moderate truncation.</summary>
    Standard,
    /// <summary>120+ characters. Minimal or no truncation.</summary>
    Wide
}

/// <summary>
/// Allocates column-space budgets from the available console width.
/// Fixed chrome (ID column ~7, type badge ~5, state badge ~12, table borders ~6)
/// is subtracted to yield a title budget. Tree contexts deduct 2 chars per level.
/// </summary>
public readonly record struct WidthBudget
{
    private const int MinTitleBudget = 10;
    private const int FixedChrome = 30; // ID + type badge + state + borders
    private const int TreeIndentPerLevel = 2;

    public int ConsoleWidth { get; }
    public RenderBreakpoint Breakpoint { get; }
    public int TitleBudget { get; }
    public int PathBudget { get; }
    public int AssignedToBudget { get; }

    public WidthBudget(int consoleWidth)
    {
        ConsoleWidth = Math.Max(consoleWidth, 60);
        Breakpoint = ConsoleWidth switch
        {
            < 80 => RenderBreakpoint.Compact,
            < 120 => RenderBreakpoint.Standard,
            _ => RenderBreakpoint.Wide
        };
        TitleBudget = Math.Max(MinTitleBudget, ConsoleWidth - FixedChrome);
        PathBudget = Breakpoint switch
        {
            RenderBreakpoint.Compact => 20,
            RenderBreakpoint.Standard => 35,
            _ => ConsoleWidth - FixedChrome
        };
        AssignedToBudget = Breakpoint == RenderBreakpoint.Compact ? 15 : 20;
    }

    /// <summary>
    /// Title budget adjusted for tree indentation depth.
    /// Each level consumes 2 characters (Spectre Tree indent width).
    /// </summary>
    public int TreeTitleBudget(int depth) =>
        Math.Max(MinTitleBudget, TitleBudget - (depth * TreeIndentPerLevel));
}
```

#### 2. Truncation Helpers

**File:** `src/Twig/Rendering/WidthBudget.cs` (same file, static class)

```csharp
/// <summary>
/// Pure-function helpers for width-aware text truncation.
/// </summary>
internal static class TruncationHelpers
{
    /// <summary>
    /// Truncates a title to fit within the budget, appending "…" if truncated.
    /// Returns the title unchanged if it fits.
    /// </summary>
    internal static string TruncateTitle(string title, int budget)
    {
        if (budget <= 0 || title.Length <= budget)
            return title;
        if (budget <= 1)
            return "…";
        return string.Concat(title.AsSpan(0, budget - 1), "…");
    }

    /// <summary>
    /// Truncates a backslash-delimited path to fit within the budget.
    /// Shows the last N segments that fit, prefixed with "…\" if segments were dropped.
    /// Returns the path unchanged if it fits.
    /// </summary>
    internal static string TruncatePath(string path, int budget)
    {
        if (budget <= 0 || path.Length <= budget)
            return path;

        var segments = path.Split('\\');
        // Try last 1, 2, 3... segments until we exceed budget
        for (var n = 1; n <= segments.Length; n++)
        {
            var candidate = string.Join("\\", segments[^n..]);
            var withPrefix = n < segments.Length ? "…\\" + candidate : candidate;
            if (withPrefix.Length > budget)
            {
                // Back off to previous n, or truncate last segment
                if (n == 1)
                    return TruncateTitle(segments[^1], budget);
                var prev = string.Join("\\", segments[^(n - 1)..]);
                return "…\\" + prev;
            }
        }
        return path;
    }
}
```

#### 3. Integration Pattern

Each rendering method creates a `WidthBudget` and uses it for inline truncation.
The pattern is consistent across all surfaces:

```csharp
// At method entry
var budget = new WidthBudget(_console.Profile.Width);

// When building labels
var displayTitle = TruncationHelpers.TruncateTitle(item.Title, budget.TitleBudget);

// When building tree labels
var displayTitle = TruncationHelpers.TruncateTitle(
    item.Title, budget.TreeTitleBudget(depth));

// When showing paths
var displayArea = TruncationHelpers.TruncatePath(
    item.AreaPath.ToString(), budget.PathBudget);
```

### Data Flow

1. **Console width** is read once from `_console.Profile.Width` at method entry.
2. **WidthBudget** computes breakpoint + budgets deterministically (pure function).
3. **Truncation helpers** are called inline during label string building —
   before `Markup.Escape()` (truncation operates on raw text, not markup).
4. **Spectre components** receive pre-truncated strings — no Spectre API changes.

### Design Decisions

| Decision | Rationale |
|---|---|
| `readonly record struct` over class | Zero-allocation, value semantics, follows `StateEntry`/`SeedLink` codebase pattern |
| Static helpers over instance methods | Pure functions with no state — easier to test, no DI needed |
| Truncation before Markup.Escape | Truncation must cut raw text; cutting after escape could split `[markup]` sequences |
| No Spectre `TableColumn.Width(n)` | Spectre's auto-layout with `Expand()` handles column distribution; we only control content length |
| FixedChrome = 30 chars | Conservative estimate: ID(~7) + type-badge(~5) + state(~12) + borders/padding(~6) |
| MinTitleBudget = 10 | Prevents degenerate single-character truncation; still readable at extreme narrow widths |
| PathBudget varies by breakpoint | Compact: 20 (last segment only), Standard: 35, Wide: full |
| Same file for struct + helpers | They're tightly coupled; splitting adds indirection for two small types |
| `BuildPreviewPanel` becomes non-static | Needs `_console.Profile.Width`; converted to instance method or receives budget parameter |

---

## Dependencies

### Internal Dependencies
- `IAnsiConsole` injection already in place (`SpectreRenderer` primary constructor)
- `Spectre.Console.Testing.TestConsole` supports `Profile.Width` for test seams
- `WorkItemBuilder` TestKit for constructing test work items

### Sequencing Constraints
- Issue #2859 (foundation) must complete before #2860–#2862
- Issues #2860, #2861, #2862 can proceed in parallel after #2859

---

## Impact Analysis

### Components Affected

| Component | Change Type | Scope |
|---|---|---|
| `SpectreRenderer.cs` | Modified | 9 rendering methods + 3 helper methods |
| `SpectreTheme.cs` | Modified | `CreateWorkspaceTable` — minor (no structural change) |
| `WidthBudget.cs` | New | `readonly record struct` + `TruncationHelpers` static class |
| 6 test files | New | Width-aware rendering tests at 60/80/120 breakpoints |

### Backward Compatibility
- **No API changes.** `IAsyncRenderer` interface is unchanged.
- **No config changes.** Width is read from the console, not configuration.
- **Visual change at wide terminals:** None — titles that fit within budget are unchanged.
- **Visual change at narrow terminals:** Titles/paths truncated with `…` instead of wrapping.

### Performance
- `WidthBudget` construction: trivial (3 comparisons, 3 subtractions).
- `TruncateTitle`: O(n) where n = title length — same as today's no-op.
- `TruncatePath`: O(s) where s = segment count — typically 3–5 segments.
- No measurable impact on rendering latency.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Markup escape sequences counted as visible chars | Medium | Medium | Truncate raw text before `Markup.Escape()` — design enforces this order |
| Spectre type badge widths vary (Nerd Font vs Unicode) | Low | Low | FixedChrome is a conservative estimate; badge width variation is ≤2 chars |
| `BuildPreviewPanel` is `static` — needs width context | Low | Low | Pass `WidthBudget` as parameter; static remains testable |
| Test fragility from exact output matching | Medium | Low | Assert `ShouldContain("…")` rather than exact string equality where possible |

---

## Open Questions

| # | Question | Severity | Notes |
|---|---|---|---|
| 1 | Should `FixedChrome` vary between table vs. tree vs. panel contexts? | Low | A single value is simpler; per-context values add complexity for marginal accuracy. Starting with 30 across all contexts; can refine in a follow-up. |
| 2 | Should we add a `--width` CLI flag for testing/overriding? | Low | Not needed for this epic. Users can resize their terminal; tests use `TestConsole.Profile.Width`. |
| 3 | Should `HumanOutputFormatter` (non-Spectre path) get width-awareness too? | Low | Explicitly a non-goal for this epic. It already has its own `GetTerminalWidth()` and state-alignment logic. |

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig/Rendering/WidthBudget.cs` | `WidthBudget` readonly record struct, `RenderBreakpoint` enum, `TruncationHelpers` static class |
| `tests/Twig.Cli.Tests/Rendering/WidthBudgetTests.cs` | Unit tests for `WidthBudget` construction at 60/80/120/200 widths, breakpoint resolution, budget computation |
| `tests/Twig.Cli.Tests/Rendering/TruncationHelperTests.cs` | Unit tests for `TruncateTitle` and `TruncatePath` — edge cases, boundary conditions, empty/null inputs |
| `tests/Twig.Cli.Tests/Rendering/WorkspaceWidthTests.cs` | Integration tests: workspace flat table and tree rendering at 60/80/120 widths |
| `tests/Twig.Cli.Tests/Rendering/TreeStatusShowWidthTests.cs` | Integration tests: `RenderTreeAsync`, `BuildStatusViewAsync`, `RenderWorkItemAsync` at narrow widths |
| `tests/Twig.Cli.Tests/Rendering/SeedAreaInteractiveWidthTests.cs` | Integration tests: seed view, area view, interactive tree, disambiguation, flow summary at 60-char width |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig/Rendering/SpectreRenderer.cs` | (1) Create `WidthBudget` at entry of each rendering method. (2) Replace `Markup.Escape(item.Title)` with `Markup.Escape(TruncationHelpers.TruncateTitle(item.Title, budget.TitleBudget))` in 10+ locations. (3) Truncate area/iteration paths in `BuildStatusViewAsync` and `RenderWorkItemAsync`. (4) Truncate assigned-to in panel views. (5) Replace hardcoded `56` in `BuildPreviewPanel` with `budget.TreeTitleBudget(0)`. (6) Remove `TruncateField` method (callers migrated). (7) Pass `WidthBudget` to `FormatWorkspaceTreeNodeLabel`, `BuildSelectionRenderable`, `BuildPreviewPanel`. |
| `src/Twig/Rendering/SpectreTheme.cs` | No structural changes. `CreateWorkspaceTable` remains as-is — truncation happens in row content, not column definitions. |

---

## ADO Work Item Structure

### Epic #2548 — Auto-Fit Terminal Output

#### Issue #2859 — Width Budget Foundation

**Goal:** Create the foundational `WidthBudget` readonly record struct,
`RenderBreakpoint` enum, and `TruncateTitle`/`TruncatePath` helpers. Remove the
legacy `SpectreRenderer.TruncateField`. Establish comprehensive unit tests.

**Prerequisites:** None — this is the foundation.

| Task | Description | Files | Effort |
|---|---|---|---|
| T1: Create WidthBudget + RenderBreakpoint | Define `WidthBudget` readonly record struct with `ConsoleWidth`, `Breakpoint`, `TitleBudget`, `PathBudget`, `AssignedToBudget` properties. Define `RenderBreakpoint` enum (`Compact`, `Standard`, `Wide`). Implement `TreeTitleBudget(int depth)` method. | `src/Twig/Rendering/WidthBudget.cs` | S |
| T2: Create TruncationHelpers | Implement `TruncateTitle(string, int)` and `TruncatePath(string, int)` as `internal static` methods on `TruncationHelpers` class. `TruncateTitle` appends `…` when exceeding budget. `TruncatePath` shows last N backslash-delimited segments with `…\` prefix when path exceeds budget. | `src/Twig/Rendering/WidthBudget.cs` | S |
| T3: Remove TruncateField | Remove `SpectreRenderer.TruncateField` (line 1196). Migrate its two callers (History line 1090, Tags line 1099 in `RenderWorkItemAsync`) to use `TruncationHelpers.TruncateTitle`. `StripHtmlTags` remains — it's used independently. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T4: WidthBudget unit tests | Test `WidthBudget` construction at widths 60, 79, 80, 119, 120, 200. Assert correct `Breakpoint` resolution, `TitleBudget` = `width - 30` (clamped ≥ 10), `TreeTitleBudget(depth)` deducts `depth × 2`. Test edge cases: width < 60 (clamped to 60), depth that would produce budget < 10. | `tests/Twig.Cli.Tests/Rendering/WidthBudgetTests.cs` | S |
| T5: TruncationHelper unit tests | Test `TruncateTitle`: short string (no-op), exact-length (no-op), one-over (truncated with `…`), very long string. Test `TruncatePath`: single segment, two segments fits, three segments needs truncation, very deep path (`A\B\C\D\E\F`), budget = 5 (forces last-segment truncation). Empty string, budget ≤ 0 edge cases. | `tests/Twig.Cli.Tests/Rendering/TruncationHelperTests.cs` | S |

**Acceptance Criteria:**
- [ ] `WidthBudget(60).Breakpoint == Compact`, `WidthBudget(80).Breakpoint == Standard`, `WidthBudget(120).Breakpoint == Wide`
- [ ] `WidthBudget(60).TitleBudget == 30`, `WidthBudget(120).TitleBudget == 90`
- [ ] `TreeTitleBudget(5)` at width 60 == 20 (30 - 10)
- [ ] `TruncateTitle("Hello World", 5) == "Hell…"`
- [ ] `TruncatePath("A\\B\\C\\D", 10)` shows last segments with `…\` prefix
- [ ] `SpectreRenderer.TruncateField` no longer exists
- [ ] All tests pass, build succeeds with AOT

---

#### Issue #2860 — Workspace Rendering Width-Awareness

**Goal:** Apply `WidthBudget` to `RenderWorkspaceAsync` (flat table),
`RenderWorkspaceTreeAsync`, and `FormatWorkspaceTreeNodeLabel`. Truncate titles
and assigned-to in workspace rows. Update `SpectreTheme.CreateWorkspaceTable` if
needed.

**Prerequisites:** Issue #2859 (WidthBudget foundation)

| Task | Description | Files | Effort |
|---|---|---|---|
| T1: Workspace flat table width-awareness | In `RenderWorkspaceAsync`, create `WidthBudget` from `_console.Profile.Width`. Replace `Markup.Escape(item.Title)` (line 341) with `Markup.Escape(TruncationHelpers.TruncateTitle(item.Title, budget.TitleBudget))`. Truncate `AssignedTo` in team-view rows (line 346) to `budget.AssignedToBudget`. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T2: Workspace tree label width-awareness | Add `WidthBudget` parameter to `FormatWorkspaceTreeNodeLabel`. Replace all three `Markup.Escape(item.Title)` calls (lines 579, 582, 585) with truncated versions using `budget.TreeTitleBudget(depth)`. Update callers (lines 512, 539) to pass budget and tree depth. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T3: Seed row truncation in workspace tree | In `RenderWorkspaceTreeAsync`, truncate seed titles in the seeds section (line 465 area) using `budget.TreeTitleBudget(0)`. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T4: Workspace width tests | Create `WorkspaceWidthTests.cs`. Test `RenderWorkspaceAsync` flat table at `Profile.Width = 60, 80, 120`. Assert titles contain `…` at 60 for long titles, are untruncated at 120. Test `RenderWorkspaceTreeAsync` at 60 — verify tree labels are truncated. Test team-view assigned-to truncation. | `tests/Twig.Cli.Tests/Rendering/WorkspaceWidthTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig workspace` at 60-char width shows truncated titles with `…`
- [ ] `twig workspace --tree` at 60-char width shows truncated tree labels
- [ ] Team-view assigned-to is truncated to 15 chars at compact width
- [ ] At 120+ width, titles appear unchanged from current behavior
- [ ] All existing workspace tests still pass

---

#### Issue #2861 — Tree/Status/Show Panel Width-Awareness

**Goal:** Apply `WidthBudget` to `RenderTreeAsync`, `BuildStatusViewAsync`, and
`RenderWorkItemAsync`. Truncate titles in tree child labels and panel headers.
Truncate area/iteration paths via `TruncatePath`. Truncate assigned-to in panels.

**Prerequisites:** Issue #2859 (WidthBudget foundation)

| Task | Description | Files | Effort |
|---|---|---|---|
| T1: Tree child label truncation | In `RenderTreeAsync`, create `WidthBudget`. In the child-rendering loop (line 697), replace `Markup.Escape(child.Title)` with truncated version using `budget.TreeTitleBudget(1)` (children are at depth 1 from focus). Also truncate parent-chain labels (`FormatParentNode`, `FormatFocusedNode`) proportionally. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T2: BuildStatusViewAsync width-awareness | Create `WidthBudget` in `BuildStatusViewAsync`. (1) Truncate title in summary markup (line 915). (2) Truncate area path (line 923) via `TruncatePath(item.AreaPath.ToString(), budget.PathBudget)`. (3) Truncate iteration path (line 924) via `TruncatePath`. (4) Truncate assigned-to (line 922) to `budget.AssignedToBudget`. (5) Truncate title in panel header (line 1008). (6) Truncate parent title and child titles in relationships sections. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T3: RenderWorkItemAsync width-awareness | Create `WidthBudget`. (1) Truncate area path (line 1056). (2) Truncate iteration path (line 1057). (3) Truncate assigned-to (line 1055). (4) Truncate title in panel header (line 1074). (5) Migrate History/Tags `TruncateField` calls to `TruncationHelpers.TruncateTitle`. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T4: Tree/Status/Show width tests | Create `TreeStatusShowWidthTests.cs`. Test `RenderTreeAsync` at 60-char width — assert child titles are truncated. Test `BuildStatusViewAsync` at 60 — assert area/iteration paths use `…\`. Test `RenderWorkItemAsync` at 60 — assert panel header title truncated. Test all three at 120 — no truncation. | `tests/Twig.Cli.Tests/Rendering/TreeStatusShowWidthTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig tree` at 60-char width shows truncated child titles with `…`
- [ ] `twig show` at 60-char width shows area/iteration paths as `…\LastSegment`
- [ ] `twig status` panel header title is truncated at narrow widths
- [ ] Assigned-to is truncated to 15/20 chars based on breakpoint
- [ ] At 120+ width, all fields appear unchanged
- [ ] Existing `BuildStatusViewCacheAgeTests`, `BuildStatusViewDescriptionTests`, `RenderWorkItemTests` still pass

---

#### Issue #2862 — Seed View, Area View, and Interactive Tree Width-Awareness

**Goal:** Apply `WidthBudget` to remaining rendering surfaces: `RenderSeedViewAsync`,
`RenderAreaViewAsync`, `RenderInteractiveTreeAsync` (including `BuildPreviewPanel`
and `BuildInteractiveTreeRenderable`), `BuildSelectionRenderable`, and
`RenderFlowSummaryAsync`. Replace the hardcoded `56` with `budget.TreeTitleBudget(0)`.

**Prerequisites:** Issue #2859 (WidthBudget foundation)

| Task | Description | Files | Effort |
|---|---|---|---|
| T1: Seed view width-awareness | In `RenderSeedViewAsync`, create `WidthBudget`. Truncate parent title (line 1593) and seed titles (line 1638) using `budget.TitleBudget`. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T2: Area view width-awareness | In `RenderAreaViewAsync` and `FormatAreaNode`, create/pass `WidthBudget`. Truncate item titles in tree nodes and flat table rows. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T3: Interactive tree + preview panel | (1) In `BuildInteractiveTreeRenderable`, add `WidthBudget` parameter. Truncate parent-chain titles, sibling titles, and child titles using `budget.TreeTitleBudget(depth)`. (2) In `BuildPreviewPanel`, add `WidthBudget` parameter. Replace `rawTitle.Length > 56 ? rawTitle[..56] + "..."` (line 2190) with `TruncationHelpers.TruncateTitle(rawTitle, budget.TreeTitleBudget(0))`. Truncate assigned-to. (3) Update `RenderInteractiveTreeAsync` to create `WidthBudget` and pass to both static methods. The `singleColumn` check (line 1782) can now use `budget.Breakpoint == RenderBreakpoint.Compact`. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T4: Disambiguation prompts | In both `BuildSelectionRenderable` overloads (lines 1352, 1388), add `int titleBudget` parameter. Truncate `title` in rendered rows. Update caller `PromptDisambiguationAsync` (line 1283) to pass `budget.TitleBudget`. | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T5: Flow summary width-awareness | In `RenderFlowSummaryAsync`, create `WidthBudget`. Truncate title in the success header line (line 1522). | `src/Twig/Rendering/SpectreRenderer.cs` | S |
| T6: Tests for all remaining surfaces | Create `SeedAreaInteractiveWidthTests.cs`. Test `RenderSeedViewAsync` at 60 — seed titles truncated. Test `RenderAreaViewAsync` at 60 — area tree node titles truncated. Test `BuildPreviewPanel` — title truncated dynamically (no hardcoded 56). Test `BuildSelectionRenderable` — titles truncated at narrow width. Test `RenderFlowSummaryAsync` — header truncated. | `tests/Twig.Cli.Tests/Rendering/SeedAreaInteractiveWidthTests.cs` | M |

**Acceptance Criteria:**
- [ ] `twig seed view` at 60-char width shows truncated seed titles
- [ ] `twig area` at 60-char width shows truncated area-node titles
- [ ] `twig nav` at 60-char width: preview panel uses dynamic budget, not hardcoded 56
- [ ] Disambiguation prompts truncate long titles at narrow width
- [ ] `RenderFlowSummaryAsync` truncates title in header
- [ ] The literal `56` no longer appears in `SpectreRenderer.cs`
- [ ] All existing `InteractiveTreeRenderTests`, `AreaViewRenderingTests` still pass

---

## PR Groups

PR groups cluster tasks for reviewable pull requests. They are sized for reviewability
and sequenced by dependency.

### PG-1: Width Budget Foundation (deep)

**Scope:** Issue #2859 (all 5 tasks)
**Estimated LoC:** ~350 (100 production + 250 test)
**Files:** 3 (1 new production, 2 new test, 1 modified)
**Classification:** Deep — concentrated logic in a small number of files
**Branch:** `feature/2859-width-budget-foundation`

**Contains:**
- `WidthBudget` readonly record struct + `RenderBreakpoint` enum
- `TruncationHelpers` static class
- `TruncateField` removal + caller migration
- Comprehensive unit tests

**Rationale:** This PR establishes the foundation that all subsequent PRs depend on.
It's small, self-contained, and can be reviewed independently. Zero risk of merge
conflicts with downstream work.

**Successor:** PG-2, PG-3

---

### PG-2: Core Rendering Surfaces (wide)

**Scope:** Issue #2860 + Issue #2861 (all tasks from both issues)
**Estimated LoC:** ~800 (400 production changes + 400 test)
**Files:** ~5 (1 modified production, 2 new test, existing test files updated)
**Classification:** Wide — mechanical application of the same pattern across many
rendering methods
**Branch:** `feature/2860-core-rendering-width`

**Contains:**
- Workspace flat table truncation (`RenderWorkspaceAsync`)
- Workspace tree label truncation (`FormatWorkspaceTreeNodeLabel`)
- Focused tree truncation (`RenderTreeAsync`)
- Status panel truncation (`BuildStatusViewAsync`)
- Work-item panel truncation (`RenderWorkItemAsync`)
- Area/iteration path truncation in panels
- Integration tests at 60/80/120 widths

**Rationale:** These are the highest-priority, most-used rendering surfaces.
Grouping workspace + tree + status + show provides a complete review of the
"core experience" in one PR. The changes are highly mechanical (same pattern
repeated), making the ~800 LoC manageable for review despite the file count.

**Predecessor:** PG-1

---

### PG-3: Remaining Surfaces (wide)

**Scope:** Issue #2862 (all 6 tasks)
**Estimated LoC:** ~550 (250 production changes + 300 test)
**Files:** ~3 (1 modified production, 1 new test, existing test files updated)
**Classification:** Wide — same mechanical pattern applied to less-critical surfaces
**Branch:** `feature/2862-remaining-surfaces-width`

**Contains:**
- Seed view truncation (`RenderSeedViewAsync`)
- Area view truncation (`RenderAreaViewAsync`)
- Interactive tree + preview panel (`RenderInteractiveTreeAsync`, `BuildPreviewPanel`)
- Disambiguation prompt truncation (`BuildSelectionRenderable`)
- Flow summary truncation (`RenderFlowSummaryAsync`)
- Hardcoded `56` removal
- Integration tests at 60-char width

**Rationale:** These are lower-priority surfaces that follow the identical pattern
established in PG-2. Grouping them into a single PR keeps the total PR count at 3
(the justified maximum per the PR grouping strategy) while maintaining review
coherence — the reviewer sees one consistent pattern applied to "everything else."

**Predecessor:** PG-1

**Note:** PG-2 and PG-3 are independent of each other (both depend only on PG-1)
and can be implemented and reviewed in parallel.

---

## References

- **SpectreRenderer.cs** — `src/Twig/Rendering/SpectreRenderer.cs` (2,249 lines)
- **SpectreTheme.cs** — `src/Twig/Rendering/SpectreTheme.cs` (203 lines)
- **IAsyncRenderer.cs** — `src/Twig/Rendering/IAsyncRenderer.cs` (131 lines)
- **Spectre.Console documentation** — Table, Tree, Panel, Live rendering
- **Existing test patterns** — `WorkspaceTreeRenderTests.cs` uses `TestConsole` + `Profile.Width`
- **PR Grouping Strategy** — `.github/instructions/pr-grouping.instructions.md`
