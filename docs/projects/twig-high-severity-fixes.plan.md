# Twig — HIGH Severity Fixes (H-001, H-002, H-003)

**Date:** 2026-03-21
**Source:** [Architecture Analysis](twig-architecture-analysis.doc.md) § 9.1
**Scope:** Fix all 3 HIGH-severity Gatekeeper findings

---

## 1. Problem Statement

The architecture analysis identified three HIGH-severity anti-patterns:

1. **H-001** — Sync-over-async in `RenderingServiceModule.cs` DI factory: `Task.Run(() => processTypeStore.GetAllAsync()).GetAwaiter().GetResult()` to load state entries for `SpectreTheme`.
2. **H-002** — Sync-over-async in `PromptStateWriter.WritePromptStateCore()`: three chained `.GetAwaiter().GetResult()` calls against `IContextStore`, `IWorkItemRepository`, and `IProcessTypeStore`. Compounded by a bare `catch {}` (M-006) that swallows `OutOfMemoryException`.
3. **H-003** — State-to-color mapping duplicated 3× with a **correctness bug**: `HumanOutputFormatter.GetStateColor()` passes `null` for state entries (heuristic-only), while `SpectreTheme.GetStateStyle()` passes actual ADO state entries. Custom ADO states render different colors depending on the rendering path.

---

## 2. Design Decisions

### DD-001: Pre-compute state entries during bootstrap (H-001)

**Decision:** Load process type state entries during `Program.cs` bootstrap (alongside `TwigConfiguration`) and pass them as an explicit parameter to `AddTwigRenderingServices()`. This eliminates the `Task.Run().GetAwaiter().GetResult()` anti-pattern inside the DI factory.

**Rationale:** The config is already loaded synchronously via `.GetAwaiter().GetResult()` in `TwigServiceRegistration`. Moving state entry loading to the same bootstrap phase keeps all sync-over-async in one explicit place (the bootstrap) rather than hidden inside DI factories. The state entries can then be threaded into both the rendering module AND the `HumanOutputFormatter` (addressing H-003 simultaneously).

### DD-002: Make PromptStateWriter async (H-002)

**Decision:** Change `IPromptStateWriter.WritePromptState()` to `IPromptStateWriter.WritePromptStateAsync()` returning `Task`. Update all 14 callers to `await` it. Narrow the bare `catch {}` to `catch (Exception)` to stop swallowing non-`Exception` types.

**Rationale:** All callers are already in `async Task<int>` methods. The sync interface was a premature constraint. Making it async eliminates 3 `.GetAwaiter().GetResult()` calls cleanly. The fire-and-forget concern is a non-issue because callers already call this at the end of their method — awaiting adds no observable delay.

### DD-003: Unify state color resolution via shared state entries (H-003)

**Decision:** Pass `IReadOnlyList<StateEntry>?` to `HumanOutputFormatter`'s constructor (matching `SpectreTheme`'s constructor signature). Convert `GetStateColor` from `static` to instance method so it can access the state entries. Both renderers then call `StateCategoryResolver.Resolve(state, stateEntries)` with the same entries.

**Rationale:** This is the minimal change that fixes the correctness bug. No new service/class needed — just plumb the same data through. The `HumanOutputFormatter` already receives `DisplayConfig` and `TypeAppearances` at construction; adding `stateEntries` follows the same pattern.

---

## 3. Constraints

- **AOT compatibility**: No reflection. All DI registrations must use explicit factory lambdas.
- **Test compatibility**: 2,048 existing tests must continue to pass. Tests that construct `HumanOutputFormatter` directly (with 0 or 2 args) must remain valid via optional parameter.
- **Backward compatibility**: The `IPromptStateWriter` interface change (sync → async) affects 14 callers in the CLI project and test mocks. All must be updated.

---

## 9. Implementation Plan

### EPIC-001: Pre-compute state entries and eliminate sync-over-async in DI (H-001)

**Goal:** Move process type state entry loading from inside the `SpectreTheme` DI factory to the `Program.cs` bootstrap phase. Pass the pre-computed entries into `AddTwigRenderingServices()`.

**Prerequisites:** None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-001 | IMPL | Add `IReadOnlyList<StateEntry>?` parameter to `AddTwigRenderingServices()` method signature | `src/Twig/DependencyInjection/RenderingServiceModule.cs` | DONE |
| T-002 | IMPL | In `Program.cs`, after `AddTwigCoreServices()` and before `AddTwigRenderingServices()`, load process type state entries using `IProcessTypeStore.GetAllAsync()` with the same try/catch pattern currently in the DI factory. Pass the result to `AddTwigRenderingServices(stateEntries)` | `src/Twig/Program.cs` | DONE |
| T-003 | IMPL | Update the `SpectreTheme` DI factory in `RenderingServiceModule` to use the passed-in `stateEntries` parameter instead of resolving `IProcessTypeStore` and calling `GetAllAsync()` inside the factory. Remove the `Task.Run().GetAwaiter().GetResult()` call entirely | `src/Twig/DependencyInjection/RenderingServiceModule.cs` | DONE |
| T-004 | TEST | Verify all existing tests pass. No new tests needed — the behavior is identical, only the loading location changed | All test projects | DONE |

**Acceptance Criteria:**
- [x] No `Task.Run` or `.GetAwaiter().GetResult()` in `RenderingServiceModule.cs`
- [x] `SpectreTheme` receives the same state entries as before
- [x] All 2,048 tests pass

**Status: DONE** (2026-03-21)

**Implementation Notes:**
- Review finding: expanded catch block in `Program.cs` from `catch (InvalidOperationException)` to `catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)` to also handle query-time SQLite errors (disk I/O, corruption) that `GetAllAsync()` can throw directly after the connection is open.
- Review finding: added `BuildPaths_WithOrgAndProject_ReturnsContextScopedPath` (Fact) and `BuildPaths_WithEmptyOrgOrProject_ReturnsFlatPath` (Theory, 8 inline data cases) tests to `TwigPathsTests.cs` covering `TwigPaths.BuildPaths()`.

---

### EPIC-002:Make PromptStateWriter async and narrow catch (H-002 + M-006)

**Goal:** Change `IPromptStateWriter` interface from sync to async. Update all 14 callers. Narrow the bare `catch {}` to `catch (Exception)`.

**Prerequisites:** None (independent of EPIC-001)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-005 | IMPL | Change `IPromptStateWriter.WritePromptState()` to `WritePromptStateAsync()` returning `Task` | `src/Twig.Domain/Interfaces/IPromptStateWriter.cs` | DONE |
| T-006 | IMPL | Update `PromptStateWriter` implementation: rename method to `WritePromptStateAsync`, make it `async Task`, replace 3 `.GetAwaiter().GetResult()` calls with `await`, change bare `catch {}` to `catch (Exception)` | `src/Twig.Infrastructure/Config/PromptStateWriter.cs` | DONE |
| T-007 | IMPL | Update all 14 command callers from `promptStateWriter?.WritePromptState()` to `if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync()`. Files: BranchCommand.cs, ConfigCommand.cs, EditCommand.cs, FlowCloseCommand.cs, FlowDoneCommand.cs, FlowStartCommand.cs, HookHandlerCommand.cs, NoteCommand.cs, RefreshCommand.cs, SaveCommand.cs, SetCommand.cs, StashCommand.cs, StateCommand.cs, UpdateCommand.cs | `src/Twig/Commands/*.cs` (14 files) | DONE |
| T-008 | IMPL | Update DI registration in `TwigServiceRegistration.cs` if the factory references the method name | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE (no change needed) |
| T-009 | TEST | Update PromptStateWriter test mocks and unit tests to use the new async interface. Update `PromptStateWriterTests.cs` and `PromptStateIntegrationTests.cs` | `tests/Twig.Cli.Tests/Commands/PromptStateWriterTests.cs`, `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | DONE |
| T-010 | TEST | Verify all 2,048 tests pass after the interface change | All test projects | DONE |

**Acceptance Criteria:**
- [x] `IPromptStateWriter` interface declares `Task WritePromptStateAsync()`
- [x] No `.GetAwaiter().GetResult()` calls remain in `PromptStateWriter.cs`
- [x] Bare `catch {}` is replaced with `catch (Exception)` 
- [x] All 14 callers use `await`
- [x] All tests pass

**Status: DONE** (2026-03-21)

---

### EPIC-003: Unify state color resolution across rendering paths (H-003 + M-004)

**Goal:** Pass state entries to `HumanOutputFormatter` so both rendering paths use `StateCategoryResolver.Resolve(state, stateEntries)` with the same data. Fix the correctness bug where custom ADO states render different colors.

**Prerequisites:** EPIC-001 (state entries are pre-computed and available)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-011 | IMPL | Add `IReadOnlyList<StateEntry>? stateEntries = null` optional parameter to the `HumanOutputFormatter` constructor. Store as `private readonly IReadOnlyList<StateEntry>? _stateEntries` field | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| T-012 | IMPL | Change `GetStateColor` from `private static string GetStateColor(string state)` to `private string GetStateColor(string state)` (remove `static`). Update its body to call `StateCategoryResolver.Resolve(state, _stateEntries)` instead of `StateCategoryResolver.Resolve(state, null)` | `src/Twig/Formatters/HumanOutputFormatter.cs` | TO DO |
| T-013 | IMPL | Update the `HumanOutputFormatter` DI factory in `RenderingServiceModule` to pass the pre-computed `stateEntries` parameter: `new HumanOutputFormatter(cfg.Display, cfg.TypeAppearances, stateEntries)` | `src/Twig/DependencyInjection/RenderingServiceModule.cs` | TO DO |
| T-014 | TEST | Add test verifying `HumanOutputFormatter` and `SpectreTheme` produce the same state category for a custom ADO state when both receive the same state entries | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | TO DO |
| T-015 | TEST | Verify all existing tests pass — the new constructor parameter is optional so existing test constructors remain valid | All test projects | TO DO |

**Acceptance Criteria:**
- [ ] `HumanOutputFormatter.GetStateColor` is no longer static
- [ ] Both `HumanOutputFormatter` and `SpectreTheme` pass state entries to `StateCategoryResolver.Resolve`
- [ ] Custom ADO states (e.g., "Design", "Review") render the same color in both paths
- [ ] All tests pass
