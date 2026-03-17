# Twig IUX — Interactive Disambiguation Prompts Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-005  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the UX specifications for the interactive disambiguation feature introduced in EPIC-005. When `twig set <pattern>` or `twig down <pattern>` matches multiple work items, an interactive selection prompt is shown instead of a static numbered list.

**AOT Constraint**: `SelectionPrompt<T>` produces IL2067 trim warnings via `TypeConverterHelper` (see ITEM-001A spike results). The implementation uses a custom `AnsiConsole.Live()`-based prompt with keyboard input as the AOT-safe fallback.

---

## Rendering Pipeline

```
SetCommand.ExecuteAsync / NavigationCommands.DownAsync
  ├── RenderingPipelineFactory.Resolve(outputFormat)
  │   ├── Human + TTY → async path (SpectreRenderer)
  │   └── JSON / Minimal / piped / non-TTY → sync path (IOutputFormatter)
  └── Pattern match returns MultipleMatches:
      ├── Async path:
      │   ├── renderer.PromptDisambiguationAsync(candidates)
      │   │   ├── Live() context with keyboard input loop
      │   │   ├── Up/Down arrow navigation
      │   │   ├── Enter to select, Escape to cancel
      │   │   └── Type to filter candidates
      │   └── Selected item → continue with setCommand.ExecuteAsync(id)
      └── Sync path:
          ├── fmt.FormatDisambiguation(matches) → static numbered list
          └── Exit code 1
```

---

## Prompt Layout

### Selection Display
```
Multiple matches — select one:
Filter: {filterText}           ← shown only when filter is active
  #101 Auth login page         ← dim style for non-selected
❯ #102 Auth token refresh      ← bold + aqua marker for selected
  #103 Auth logout flow        ← dim style for non-selected
↑/↓ navigate · Enter select · Esc cancel · type to filter
```

### Keyboard Controls

| Key | Action |
|-----|--------|
| `↑` / `↓` | Move selection up/down |
| `Enter` | Confirm selection, proceed with selected item |
| `Escape` | Cancel selection, return exit code 1 |
| Alphanumeric | Filter candidates by title or ID substring |
| `Backspace` | Remove last filter character |

### Filter Behavior

- Case-insensitive substring matching on title
- Also matches numeric ID substring
- Selection index clamped to filtered list bounds
- "No items match filter" shown when filter eliminates all candidates

---

## Fallback Behavior

| Condition | Behavior |
|-----------|----------|
| `--output json` | Static JSON disambiguation list, exit code 1 |
| `--output minimal` | Static minimal list, exit code 1 |
| Non-TTY (piped output) | Static human list, exit code 1 |
| No `RenderingPipelineFactory` | Static human list, exit code 1 (backward compat) |

---

## Design Decisions

### DD-012: FindByPattern over MoveDown

`NavigationCommands.DownAsync` calls `tree.FindByPattern(idOrPattern)` directly instead of `tree.MoveDown(idOrPattern)`. `MoveDown` returns `Result.Fail<int>(errorMessage)` on multi-match — the `multi.Candidates` list is discarded in the error string. `FindByPattern` returns `MatchResult.MultipleMatches { Candidates }`, preserving the candidate list for the interactive prompt.

### AOT-Safe Prompt Implementation

Uses `AnsiConsole.Live()` with `System.Console.ReadKey(true)` for keyboard input, avoiding the `TypeConverterHelper` dependency that makes `SelectionPrompt<T>` incompatible with Native AOT + `TreatWarningsAsErrors`.

---

## Test Coverage

| Test | Scenario | Verifies |
|------|----------|----------|
| `Set_MultiMatch_Tty_PromptsAndSelectsItem` | TTY + human + multi-match | Interactive prompt called, selected item becomes context |
| `Set_MultiMatch_Tty_UserCancels_ReturnsExitCode1` | User presses Escape | Null return → exit code 1, no context change |
| `Set_MultiMatch_Json_ReturnsStaticList` | JSON format + multi-match | Prompt NOT called, static list returned |
| `Set_MultiMatch_NonTty_ReturnsStaticList` | Piped output + multi-match | Prompt NOT called, static list returned |
| `Down_MultiMatch_Tty_PromptsAndSelectsChild` | Down command + TTY + multi-match | FindByPattern used, prompt called, child selected |
| `Down_MultiMatch_Tty_UserCancels_ReturnsExitCode1` | Down + Escape | Exit code 1 |
| `Down_MultiMatch_Json_ReturnsStaticList` | Down + JSON | Static fallback |
| `BuildSelectionRenderable_*` | Renderable output | Correct markup for highlight, filter, empty states |
