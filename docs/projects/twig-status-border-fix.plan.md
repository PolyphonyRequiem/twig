# Solution Design: Fix Status Panel Bottom Border Rendering Bug

**Author:** Copilot  
**Status:** Draft  
**Revision:** 3 — Addressed technical review feedback (score 84/100)

---

## Executive Summary

The `twig status` command exhibits a visual artifact where the bottom border of the status panel has a horizontal gap aligned with the syncing indicator footprint. The root cause is an architectural mismatch: `RenderStatusAsync` writes the panel directly to the console **outside** any Spectre `Live()` context, then `RenderWithSyncAsync` starts a **separate** `Live()` region below it. When the Live region shrinks (sync indicator clears), Spectre's ANSI cursor repositioning overwrites the panel's bottom border. This plan describes a test-first fix for `StatusCommand`, a defensive hardening of `RenderWithSyncAsync` to prevent future regressions, and an audit of all other callers (`TreeCommand`, `SetCommand`) for the same pattern.

---

## Background

### Current Architecture

The rendering pipeline has two paths:
- **Sync path** (JSON, minimal, `--no-live`, piped output): uses `IOutputFormatter` — writes complete text to `Console.WriteLine`.
- **Async TTY path** (human format, interactive terminal): uses `IAsyncRenderer` (`SpectreRenderer`) — leverages Spectre.Console's `Live()` regions for progressive rendering.

Key components:
- **`SpectreRenderer.RenderStatusAsync`** (`SpectreRenderer.cs:427-488`): Builds a Spectre `Panel` and writes it directly via `_console.Write(itemPanel)` and `_console.MarkupLine(...)`. This is **not** inside a `Live()` context.
- **`SpectreRenderer.RenderWithSyncAsync`** (`SpectreRenderer.cs:822-899`): A reusable cache-render-sync-revise primitive. Wraps content in `_console.Live(cachedView).StartAsync()`. Shows a transient sync indicator (`"⟳ syncing..."`, then `"✓ up to date"`), then removes it.
- **`StatusCommand.ExecuteAsync`** (`StatusCommand.cs:90-113`): Calls `RenderStatusAsync` (panel written outside Live), then calls `RenderWithSyncAsync` with `new Text(" ")` as the cached view.

### How Spectre Live() Works

Spectre's `Live()` region tracks how many terminal lines it occupies. When content is updated to something shorter, it repositions the cursor **upward** using ANSI escape sequences to erase the surplus lines. If content was written to the terminal **before** the Live region started, this upward cursor movement can overwrite that preceding content — specifically the last line(s), which in this case is the panel's bottom border.

### Prior Art

- The existing test `RenderWithSyncAsync_SpaceCachedView_ProducesMoreOutputThanEmptyString` (`RenderWithSyncTests.cs:192-235`) already identified that `Text(string.Empty)` is worse than `Text(" ")` because it collapses to 0 lines. However, `Text(" ")` still causes border corruption because the transition from 2 lines (cached + sync indicator) to 1 line (cached only) still triggers upward cursor movement.
- `SetCommand` uses `RenderWithSyncAsync` correctly — the work item text is passed as `buildCachedView`, so all content is inside the Live region.

---

## Problem Statement

1. **Primary bug**: `twig status` renders a panel with a broken bottom border. A horizontal gap appears at the position where the sync indicator was displayed and subsequently cleared.

2. **Architectural pattern flaw**: The pattern of writing content outside a `Live()` context and then starting a `Live()` region immediately below is inherently fragile. Any shrinkage of the Live region risks corrupting the content above.

3. **Same pattern in TreeCommand**: `TreeCommand` (`TreeCommand.cs:95-109`) renders a tree inside its own `Live()` context, then starts a **second** `Live()` via `RenderWithSyncAsync` with `new Text(string.Empty)` (line 102) — the worst-case variant that collapses to 0 lines.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Write a failing test that reproduces the exact visual artifact (panel border corruption) before any fix is applied.
2. **G-2**: Fix `StatusCommand`'s TTY rendering so the panel border is never disturbed by the sync indicator lifecycle.
3. **G-3**: Harden `RenderWithSyncAsync` defensively so that Live region shrinkage cannot corrupt content above, regardless of how callers use it.
4. **G-4**: Audit all callers of `RenderWithSyncAsync` and fix any that exhibit the same pattern.
5. **G-5**: All existing tests continue to pass.

### Non-Goals

- **NG-1**: Refactoring the entire rendering pipeline or changing the sync/async path selection logic.
- **NG-2**: Fixing unrelated visual issues in the status panel (e.g., column alignment, field truncation).
- **NG-3**: Changing the behavior of the non-TTY (sync) rendering path.

---

## Requirements

### Functional

- **FR-1**: The status panel's bottom border must render as a complete, unbroken rounded box border in the final terminal output when using TTY mode.
- **FR-2**: The sync indicator ("⟳ syncing...", "✓ up to date", etc.) must still appear and clear correctly without visual artifacts.
- **FR-3**: The tree command's output must not be corrupted by the post-render sync indicator.
- **FR-4**: `SetCommand`'s rendering behavior must remain unchanged (it is already correct).

### Non-Functional

- **NFR-1**: No new public API surface on `IAsyncRenderer`. `BuildStatusViewAsync` is `internal` on `SpectreRenderer`; `StatusCommand` accesses it via the cast-to-`SpectreRenderer` pattern already established by `TreeCommand` (line 57).
- **NFR-2**: Test-first: a regression test must exist before the fix is applied.
- **NFR-3**: Minimal code changes — surgical fix, not a rewrite.

---

## Proposed Design

### Architecture Overview

The fix has three layers:

```
┌─────────────────────────────────────────────────────┐
│ Layer 3: Caller fixes (StatusCommand, TreeCommand)  │
│   Move preceding content INTO the Live() region     │
├─────────────────────────────────────────────────────┤
│ Layer 2: RenderWithSyncAsync hardening              │
│   Never let Live region shrink below initial height │
├─────────────────────────────────────────────────────┤
│ Layer 1: Test infrastructure                        │
│   Regression test proving the bug + fix             │
└─────────────────────────────────────────────────────┘
```

### Key Component Changes

#### 1. New internal method: `SpectreRenderer.BuildStatusViewAsync`

Extract the panel-building logic from `RenderStatusAsync` into a new `internal` method that **returns** an `IRenderable` without writing it to the console.

```csharp
internal async Task<IRenderable> BuildStatusViewAsync(
    Func<Task<WorkItem?>> getItem,
    Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges,
    CancellationToken ct,
    IReadOnlyList<FieldDefinition>? fieldDefinitions = null,
    IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null)
```

Returns a `Rows(summaryMarkup, itemPanel[, changesPanel])` composite renderable. The summary header line (`_console.MarkupLine(...)` at line 443) is included **inside** the composite as a `Markup(...)` renderable — this is a resolved design decision (see Design Decisions below).

**Nullability handling:** The return type is `Task<IRenderable>` (non-nullable), matching the `Func<Task<IRenderable>>` signature required by `RenderWithSyncAsync.buildCachedView`. If the item returned by `getItem()` is null, the method throws `InvalidOperationException`. This is safe because `StatusCommand` validates the item is non-null upstream (lines 65–72 via `ActiveItemResolver.GetActiveItemAsync()`) before ever calling this method. With `Nullable=enable` and `TreatWarningsAsErrors=true` in `Directory.Build.props`, a nullable return type would cause a compilation error at the `buildCachedView:` call site.

The existing `RenderStatusAsync` is refactored internally to call `BuildStatusViewAsync` then write the result. External behavior is preserved; existing tests pass unchanged. Note: `RenderStatusAsync` still handles the null-item case gracefully (early return) before delegating; it passes through to `BuildStatusViewAsync` only when item is non-null.

**Why `internal` and not on `IAsyncRenderer`:** NFR-1 prohibits new public API surface on `IAsyncRenderer`. The cast-to-`SpectreRenderer` pattern is already established in the codebase — `TreeCommand` (line 57) uses `if (renderer is SpectreRenderer spectreRenderer)` to access `TypeLevelMap` and `ParentChildMap`. `StatusCommand` will use the same pattern.

#### 2. StatusCommand: Single Live() region

Replace the current two-step pattern:
```csharp
// BEFORE (broken):
await renderer.RenderStatusAsync(...);           // writes panel OUTSIDE Live()
await renderer.RenderWithSyncAsync(
    buildCachedView: () => ... new Text(" "),    // separate Live() below
    ...);
```

With a single Live() region via cast-to-`SpectreRenderer`:
```csharp
// AFTER (fixed):
if (renderer is SpectreRenderer spectreRenderer)
{
    var workingSet = await workingSetService.ComputeAsync(item.IterationPath);
    await renderer.RenderWithSyncAsync(
        buildCachedView: () => spectreRenderer.BuildStatusViewAsync(
            getItem, getPendingChanges, ct, fieldDefs, statusFieldEntries),
        performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet),
        buildRevisedView: syncResult => Task.FromResult<IRenderable?>(null),
        CancellationToken.None);
}
```

This compiles cleanly because `BuildStatusViewAsync` returns `Task<IRenderable>` (non-nullable), matching the `Func<Task<IRenderable>>` parameter type of `buildCachedView`. The null-item case is impossible here because `StatusCommand` validates the item at lines 65–72 before reaching this code path.

This ensures the panel (and summary line) are **inside** the Live region. When the sync indicator clears, the Live region transitions from `Rows(statusView, syncIndicator)` to `statusView` — but `statusView` includes the panel, so it is redrawn intact.

**Rendering difference note:** The current `RenderStatusAsync` uses `_console.MarkupLine(...)` (adds a trailing newline) then `_console.Write(itemPanel)` as two separate console operations. The new approach composes these into `Rows(Markup(...), Panel(...))` which manages its own line separation. Spectre's `Rows` renderable inserts line breaks between children, so the visual output should be equivalent. However, the regression test (E1-T2) must explicitly validate that the summary-to-panel layout is preserved — checking that the summary line appears above the panel border with no extra or missing spacing.

#### 3. RenderWithSyncAsync: Prevent shrinkage (defense-in-depth)

When clearing the sync indicator, pad the content to maintain the same line count:

```csharp
// BEFORE (line 847):
ctx.UpdateTarget(cachedView);

// AFTER:
ctx.UpdateTarget(new Rows(cachedView, new Text(" ")));
```

This ensures the Live region never shrinks below `cachedView + 1 line`, preventing upward cursor repositioning from ever reaching content above.

Applied at all four "clear" transitions:
- `SyncResult.UpToDate` (line 847)
- `SyncResult.Updated` (line 860)
- `SyncResult.PartiallyUpdated` (line 876)
- `SyncResult.Skipped` (line 891)

#### 4. TreeCommand: Fix empty cached view

Change `new Text(string.Empty)` to `new Text(" ")` in TreeCommand's `RenderWithSyncAsync` call (line 102). Combined with the shrinkage-prevention in Layer 2, this eliminates the tree corruption bug.

### Data Flow

**Before (StatusCommand TTY path):**
```
RenderStatusAsync → _console.MarkupLine(summary)      [1 line, OUTSIDE Live()]
                  → _console.Write(itemPanel)          [N lines, OUTSIDE Live()]
                  ↓ [output on terminal, OUTSIDE Live()]
RenderWithSyncAsync → _console.Live(Text(" ")).StartAsync()
                    → UpdateTarget(Rows(spacer, "syncing..."))  [2 lines]
                    → UpdateTarget(Rows(spacer, "✓ up to date")) [2 lines]
                    → UpdateTarget(spacer)  [1 line — SHRINKS, corrupts panel border]
```

**After (StatusCommand TTY path):**
```
RenderWithSyncAsync → buildCachedView() returns Rows(summaryMarkup, itemPanel[, changesPanel])
                    → _console.Live(statusView).StartAsync()
                    → UpdateTarget(Rows(statusView, "syncing..."))
                    → UpdateTarget(Rows(statusView, "✓ up to date"))
                    → UpdateTarget(Rows(statusView, Text(" ")))  [never shrinks below initial]
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| `BuildStatusViewAsync` is `internal` on `SpectreRenderer`, not on `IAsyncRenderer` | Satisfies NFR-1 (no new public API surface). `StatusCommand` accesses it via cast-to-`SpectreRenderer`, matching the pattern already used by `TreeCommand` at line 57 for `TypeLevelMap`/`ParentChildMap`. |
| `BuildStatusViewAsync` includes the summary header as `Markup(...)` inside the composite | **Resolved decision.** If the summary remained outside the Live region (via `_console.MarkupLine`), it would be subject to the same border corruption. Including it in `Rows(summaryMarkup, itemPanel[, changesPanel])` ensures everything is inside Live(). |
| `BuildStatusViewAsync` returns non-nullable `Task<IRenderable>` | `RenderWithSyncAsync.buildCachedView` expects `Func<Task<IRenderable>>` (non-nullable). With `Nullable=enable` and `TreatWarningsAsErrors=true`, a nullable return would be a compilation error. The null-item case is guarded upstream by `StatusCommand` (lines 65–72), so the method can safely throw `InvalidOperationException` on null item. |
| Pad Live region on clear instead of shrinking | Defense-in-depth; protects against future callers making the same mistake. Applied at all four clear transitions (lines 847, 860, 876, 891). |
| `RenderStatusAsync` external behavior preserved; internal implementation refactored | `RenderStatusAsync` delegates to `BuildStatusViewAsync` then writes the result. Existing tests that call `RenderStatusAsync` directly continue to work because the console output is identical. The refactoring is purely internal. |
| Fix TreeCommand's empty string separately | TreeCommand's pattern is slightly different (two sequential Live regions); the `Text(string.Empty)` → `Text(" ")` fix plus shrinkage prevention is sufficient. |
| Regression test validates Rows-based layout | The `MarkupLine` → `Rows(Markup(...), Panel(...))` transition may subtly change line separation. E1-T2 explicitly verifies the summary appears above the panel border with correct spacing. |

---

## Alternatives Considered

### Alternative A: Add `BuildStatusViewAsync` to the `IAsyncRenderer` public interface

**Pros**: Clean composition; no downcasts; callers can build views without knowing the implementation type.
**Cons**: Violates NFR-1 (no new public API surface on `IAsyncRenderer`). Every `IAsyncRenderer` implementation would need to implement it. Adds coupling between the interface and the `StatusCommand`-specific view composition concern.
**Verdict**: Rejected — the cast-to-`SpectreRenderer` pattern is already established in the codebase (`TreeCommand:57`) and keeps the interface stable. `BuildStatusViewAsync` is a rendering-implementation detail, not a pipeline contract.

### Alternative B: Refactor all rendering into a single Live() per command

**Pros**: Eliminates the class of bugs entirely; no content is ever outside a Live region.
**Cons**: Massive refactor; changes `RenderTreeAsync`, `RenderWorkspaceAsync`, and all command flows; high risk.
**Verdict**: Rejected — too large a change for this bug fix. The targeted approach (BuildStatusViewAsync + shrinkage prevention) is safer.

### Alternative C: Use ANSI save/restore cursor instead of prevention

**Pros**: Preserves exact current output format.
**Cons**: Spectre.Console manages ANSI sequences internally; injecting raw sequences conflicts with its state tracking; fragile.
**Verdict**: Rejected — fights the framework rather than working with it.

### Alternative D: Write a blank line after panel output to create a buffer zone

**Pros**: Trivial one-line change.
**Cons**: Adds a visible blank line to the output; doesn't fix the root cause; breaks if Live region shrinks by more than 1 line.
**Verdict**: Rejected — band-aid that introduces a visual regression.

---

## Dependencies

### External
- **Spectre.Console**: The fix relies on `Rows()` compositing and `Live()` behavior. No version change needed; current behavior is correct — the bug is in our usage pattern.

### Internal
- `SpectreRenderer`: New `internal` method `BuildStatusViewAsync` and modification to `RenderWithSyncAsync`.
- `StatusCommand`, `TreeCommand`: Both callers need modification.

### Sequencing
1. Tests must be written first (Epic 1).
2. `BuildStatusViewAsync` extraction (Epic 2) enables the StatusCommand fix (Epic 3).
3. `RenderWithSyncAsync` hardening (Epic 4) can proceed in parallel with Epic 3.
4. TreeCommand fix (Epic 5) depends on Epic 4 (shrinkage prevention).

---

## Impact Analysis

### Components Affected
| Component | Change Type | Risk |
|-----------|------------|------|
| `SpectreRenderer` | New internal method + modify `RenderWithSyncAsync` | Medium — core rendering logic |
| `StatusCommand` | Change TTY rendering flow; add cast-to-`SpectreRenderer` | Medium — user-visible output |
| `TreeCommand` | Change cached view string | Low — minimal change |
| `RenderWithSyncTests` | New tests + update existing | Low |
| `StatusCommandTests` | New tests | Low |

### Backward Compatibility
- Non-TTY paths (JSON, minimal, `--no-live`) are completely unaffected.
- `RenderStatusAsync` external behavior is preserved; existing tests pass unchanged.
- `IAsyncRenderer` interface is unchanged — no impact on any consumers.
- The visual output of `twig status` improves (border fix); no regression.
- The trailing single-space line in the Live region is invisible in practice (terminal scrolls past it).

### Performance
- No measurable impact. `BuildStatusViewAsync` extracts existing logic; no additional work.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `Rows()` composition changes panel layout vs. `MarkupLine` + `Write` | Medium | Medium | Regression test (E1-T2) explicitly validates summary-to-panel layout including line separation |
| Live region padding leaves visible blank line | Low | Low | Single space is invisible; can trim if needed |
| Cast-to-`SpectreRenderer` fails at runtime | Low | High | Guarded with `if (renderer is SpectreRenderer spectreRenderer)` — falls back gracefully; existing pattern proven in `TreeCommand` |
| Test console doesn't reproduce ANSI cursor behavior | Medium | Medium | Test focuses on structural output (border chars present, not ANSI sequences) |

---

## Open Questions

1. **[Low]** Should the Live region padding (`Text(" ")`) be added only on the "clear" transitions, or on ALL final `UpdateTarget` calls? Adding it only on clear transitions (UpToDate, Updated, PartiallyUpdated, Skipped) is sufficient since `Failed` already persists its indicator and doesn't shrink. Current plan applies padding to the four clear transitions.

---

## Implementation Phases

### Phase 1: Test-First Bug Reproduction
**Exit criteria**: A test exists that exercises the StatusCommand TTY pattern (panel outside Live, then Live shrinks) and asserts border integrity. The test SHOULD currently fail or demonstrate the structural issue.

### Phase 2: Extract BuildStatusViewAsync
**Exit criteria**: `BuildStatusViewAsync` exists as `internal` on `SpectreRenderer`, returns the composite status renderable (including summary), and `RenderStatusAsync` delegates to it internally. External behavior preserved.

### Phase 3: Fix StatusCommand
**Exit criteria**: `StatusCommand` uses `BuildStatusViewAsync` via cast-to-`SpectreRenderer` in `buildCachedView`; the regression test passes; all existing tests pass.

### Phase 4: Harden RenderWithSyncAsync
**Exit criteria**: `RenderWithSyncAsync` never shrinks its Live region below the initial cached view height on clear transitions; existing `RenderWithSyncTests` updated/pass.

### Phase 5: Audit and Fix TreeCommand
**Exit criteria**: TreeCommand uses `Text(" ")` instead of `Text(string.Empty)`; audit confirms no other callers are affected; all tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | All changes are in existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Rendering/SpectreRenderer.cs` | Add `internal BuildStatusViewAsync`; refactor `RenderStatusAsync` to delegate to it; pad Live region on clear in `RenderWithSyncAsync` |
| `src/Twig/Commands/StatusCommand.cs` | Replace two-step render + sync with single `RenderWithSyncAsync` using cast-to-`SpectreRenderer` and `BuildStatusViewAsync` |
| `src/Twig/Commands/TreeCommand.cs` | Change `Text(string.Empty)` to `Text(" ")` in `RenderWithSyncAsync` call |
| `tests/Twig.Cli.Tests/Rendering/RenderWithSyncTests.cs` | Add border-corruption regression test; update existing tests for padding behavior |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Add end-to-end border integrity test for TTY path |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### Epic 1: Test-First Bug Reproduction (G-1)

**Goal**: Write tests that reproduce the visual artifact before any fix, proving understanding of the root cause.

**Prerequisites**: None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E1-T1 | TEST | Add `PanelThenLiveSync_BorderCorrupted` test to `RenderWithSyncTests.cs`: write a Panel to TestConsole, then call `RenderWithSyncAsync` with `Text(" ")` cached view and `UpToDate` sync result. Assert the panel's bottom border (`╰` and `╯` characters) is present and the border line is unbroken. This test isolates the renderer-level bug. | `tests/Twig.Cli.Tests/Rendering/RenderWithSyncTests.cs` | DONE |
| E1-T2 | TEST | Add `StatusTtyPath_PanelBorderIntact` test to `StatusCommandTests.cs`: exercise the full StatusCommand TTY flow (RenderStatusAsync + RenderWithSyncAsync) via `CreateCommandWithPipeline(CreateTtyPipelineFactory())` and assert the TestConsole output contains intact panel border characters. Also verify summary line appears above the panel's top border with correct spacing (validates `Rows(Markup, Panel)` layout equivalence to `MarkupLine` + `Write`). | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [ ] E1-T1 test exists and demonstrates the border issue (structural assertion on output)
- [ ] E1-T2 test exists and exercises the full StatusCommand TTY path
- [ ] E1-T2 validates summary-to-panel layout (no extra/missing spacing from Rows composition)
- [ ] Tests compile and run (may fail — that's expected before the fix)

---

### Epic 2: Extract BuildStatusViewAsync (G-2)

**Goal**: Create a side-effect-free `internal` method on `SpectreRenderer` that returns the complete status view as an `IRenderable`.

**Prerequisites**: Epic 1

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E2-T1 | IMPL | Add `internal async Task<IRenderable> BuildStatusViewAsync(...)` to `SpectreRenderer`: extract panel-building logic from `RenderStatusAsync` (lines 434–487) into the new method. Return type is non-nullable `Task<IRenderable>` — throws `InvalidOperationException` if item is null (caller guarantees non-null at `StatusCommand:65–72`). Compose the summary header (currently `_console.MarkupLine(...)` at line 443) as a `Markup(...)` renderable. Return `Rows(summaryMarkup, itemPanel[, changesPanel])` composite. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| E2-T2 | IMPL | Refactor `RenderStatusAsync` to call `BuildStatusViewAsync` then write the result via `_console.Write(...)`. Preserve the existing null-item early return (`if (item is null) return;` at line 434) inside `RenderStatusAsync` itself, so it never passes a null item to `BuildStatusViewAsync`. External behavior preserved; internal implementation refactored. Note: the current `_console.MarkupLine(summary)` + `_console.Write(panel)` pattern is replaced with `_console.Write(compositeRenderable)` — the Rows renderable manages line separation equivalently. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| E2-T3 | TEST | Verify all existing `SpectreRenderer_RenderStatusAsync_*` tests still pass (no behavior change). Run the full `StatusCommandTests` suite. | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [ ] `BuildStatusViewAsync` returns the same visual content that `RenderStatusAsync` writes
- [ ] `RenderStatusAsync` delegates to `BuildStatusViewAsync` internally
- [ ] All existing status tests pass without modification

---

### Epic 3: Fix StatusCommand Rendering (G-2)

**Goal**: Eliminate the two-step pattern in `StatusCommand` by using `BuildStatusViewAsync` as the `buildCachedView` for `RenderWithSyncAsync`.

**Prerequisites**: Epic 2

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E3-T1 | IMPL | Modify `StatusCommand.ExecuteAsync` (lines 90-113): add `if (renderer is SpectreRenderer spectreRenderer)` guard (matching `TreeCommand:57` pattern). Replace the sequential `renderer.RenderStatusAsync(...)` + `renderer.RenderWithSyncAsync(Text(" "))` with a single `renderer.RenderWithSyncAsync(buildCachedView: () => spectreRenderer.BuildStatusViewAsync(...), ...)`. Remove the separate `RenderStatusAsync` call from the TTY path. Add fallback for non-SpectreRenderer (call `RenderStatusAsync` directly, skip sync — defensive). | `src/Twig/Commands/StatusCommand.cs` | DONE |
| E3-T2 | TEST | Verify E1-T1 and E1-T2 regression tests now pass. Run full test suite. | `tests/Twig.Cli.Tests/` | DONE |

**Acceptance Criteria**:
- [ ] Status panel border is intact in TestConsole output
- [ ] Sync indicator still appears and clears correctly
- [ ] Summary line appears above panel with correct layout (no extra/missing lines)
- [ ] E1-T1 and E1-T2 tests pass
- [ ] All existing tests pass

---

### Epic 4: Harden RenderWithSyncAsync (G-3)

**Goal**: Prevent Live region shrinkage in `RenderWithSyncAsync` as defense-in-depth.

**Prerequisites**: None (can proceed in parallel with Epic 3)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E4-T1 | IMPL | Modify `RenderWithSyncAsync` in `SpectreRenderer.cs`: on all four "clear" transitions — `UpToDate` (line 847), `Updated` (line 860), `PartiallyUpdated` (line 876), `Skipped` (line 891) — change `ctx.UpdateTarget(displayView)` to `ctx.UpdateTarget(new Rows(displayView, new Text(" ")))`. This ensures the Live region never shrinks below the cached-view height + 1 line. The `Failed` case is excluded because it persists its indicator and does not clear. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE |
| E4-T2 | TEST | Update `RenderWithSyncTests.cs`: verify that after sync completion, the final output contains a trailing space line (or at minimum, the Live region output length does not decrease). Update any assertions that check exact output content. | `tests/Twig.Cli.Tests/Rendering/RenderWithSyncTests.cs` | DONE |

**Acceptance Criteria**:
- [ ] Live region never produces fewer lines after clearing sync indicator than before showing it
- [ ] All `RenderWithSyncTests` pass
- [ ] All command-level tests pass

---

### Epic 5: Audit and Fix Other Callers (G-4)

**Goal**: Fix TreeCommand and confirm all other RenderWithSyncAsync callers are safe.

**Prerequisites**: Epic 4

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| E5-T1 | IMPL | In `TreeCommand.cs` line 102: change `new Text(string.Empty)` to `new Text(" ")` in the `buildCachedView` delegate of `RenderWithSyncAsync`. | `src/Twig/Commands/TreeCommand.cs` | DONE |
| E5-T2 | TEST | Add test in `TreeCommandAsyncTests.cs` verifying tree output border is not corrupted when sync indicator clears. | `tests/Twig.Cli.Tests/Commands/TreeCommandAsyncTests.cs` | DONE |
| E5-T3 | IMPL | Audit: confirm `SetCommand.cs` line 137 uses `fmt.FormatWorkItem(item)` as cached view (content inside Live) — no fix needed. Document in code comment. | `src/Twig/Commands/SetCommand.cs` | DONE |
| E5-T4 | IMPL | Audit: confirm `WorkspaceCommand.cs` does NOT call `RenderWithSyncAsync` — it uses `RenderWorkspaceAsync` with a streaming `IAsyncEnumerable` pattern inside a single Live region. No fix needed. | `src/Twig/Commands/WorkspaceCommand.cs` | DONE |
| E5-T5 | TEST | Run full test suite (`dotnet test`) to confirm no regressions. | all test projects | DONE |

**Acceptance Criteria**:
- [ ] TreeCommand uses `Text(" ")` as cached view
- [ ] SetCommand and WorkspaceCommand confirmed safe (no changes needed)
- [ ] All tests pass across all test projects
- [ ] No other callers of `RenderWithSyncAsync` exhibit the bug pattern

---

## References

- **Spectre.Console Live() documentation**: https://spectreconsole.net/live/live-display
- **Existing test**: `RenderWithSyncTests.cs:192-235` — `RenderWithSyncAsync_SpaceCachedView_ProducesMoreOutputThanEmptyString` (partial awareness of the issue)
- **StatusCommand source**: `src/Twig/Commands/StatusCommand.cs:90-113`
- **SpectreRenderer.RenderWithSyncAsync**: `src/Twig/Rendering/SpectreRenderer.cs:822-899`
- **SpectreRenderer.RenderStatusAsync**: `src/Twig/Rendering/SpectreRenderer.cs:427-488`
- **TreeCommand TTY path**: `src/Twig/Commands/TreeCommand.cs:95-109`
- **TreeCommand cast-to-SpectreRenderer pattern**: `src/Twig/Commands/TreeCommand.cs:57`
- **SetCommand TTY path**: `src/Twig/Commands/SetCommand.cs:132-143` (correct pattern — reference)
