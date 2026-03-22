# Twig Test Quality — Improvement Plan

> **Status:** EPIC-001 DONE — EPIC-002 DONE — EPIC-003 DONE — EPIC-004 DONE  
> **Date:** 2026-03-22  
> **Scope:** All 4 test projects (148 test files, ~1,878 test methods)

---

## Executive Summary

Twig has good test coverage (~1,160+ passing tests) with consistent tooling (xUnit + Shouldly + NSubstitute, zero assertion framework mixing). The tests are weakest in the CLI command layer, where commands accept 8–14 constructor parameters, forcing tests to create 5–9 mocks per test class. This makes command tests expensive to write, brittle to maintain, and low in behavioral value — many test "does the command call X?" rather than "does the command produce the right outcome?". Infrastructure and domain tests are well-isolated and need only tactical improvements. This plan focuses investment where it yields the most: extracting testable domain services from fat commands, consolidating shared test data, and eliminating brittle output-string tests.

---

## Current State

| Project | Files | Tests | Quality | Key Issue |
|---------|-------|-------|---------|-----------|
| **Twig.Cli.Tests** | 75 | 931 | Mixed | 25+ files need 5–9 mocks; 20+ use `Console.SetOut` capture |
| **Twig.Domain.Tests** | 43 | 520 | Good | Decentralized `MakeItem` helpers duplicate across files |
| **Twig.Infrastructure.Tests** | 28 | 394 | Good | `:memory:` SQLite pattern is solid; integration tests properly gated |
| **Twig.Tui.Tests** | 2 | 33 | Adequate | Small surface, low priority |

### What's Working Well

- **100% Shouldly** — Zero assertion framework mixing. No `Assert.True(x == y)` anti-patterns.
- **Domain isolation** — Domain tests are pure (zero I/O). Mock counts 2–4 per class.
- **SQLite test pattern** — All persistence tests use `:memory:`, IDisposable, no file leaks.
- **Integration test gating** — `AdoRestClientIntegrationTests` uses `[Trait("Category", "Integration")]` + env guards. Git tests use temp repos with proper cleanup.
- **Auth testability** — Injectable `envVarReader` / `configPatReader` lambdas avoid real env access.
- **Spectre test console** — Rendering tests use `TestConsole` (injected `IAnsiConsole`), not stdout capture.

### What's Not Working

1. **Fat commands = fat test setup.** Commands with 8–14 constructor params create massive mock boilerplate. `FlowDoneCommand` needs 13 mocks; `RefreshCommand` needs 14. The tests spend more lines on setup than on assertions.

2. **Console.SetOut capture tests.** 20+ test files redirect stdout to a StringWriter, then assert on output strings. These break on any cosmetic change (spacing, wording, icons). Example: `output.ShouldContain("Active:")` breaks when EPIC-002 changed the header format.

3. **Decentralized test data construction.** 5+ files have local `MakeItem(int id, string title, ...)` helpers with slightly different signatures. `ProcessConfiguration` is constructed inline in every file that needs it. No shared builders.

4. **Low-value trivial tests.** ~50 tests verify single properties, constructors, or type checks (`formatter.ShouldBeOfType<JsonOutputFormatter>()`). These don't test behavior — they test assembly.

5. **Implicit integration tests.** The "async" command test files (`StatusCommandAsyncTests`, `TreeCommandAsyncTests`, `WorkspaceCommandAsyncTests`) instantiate real `SpectreRenderer`, real `HintEngine`, real `OutputFormatterFactory` with 7+ mocks. They're integration tests wearing unit test clothes — slow, fragile, and test too many things at once.

---

## Strategy

### Principles

1. **Test behavior, not wiring.** A test should assert "given this work item state, the user sees X" — not "mock A was called with parameter B."
2. **Reduce mock count, not test count.** Extract testable services so commands orchestrate fewer primitives. Mock one service, not its 5 internal dependencies.
3. **Shared builders, not shared state.** A fluent `WorkItemBuilder` eliminates `MakeItem` duplication without coupling test classes through shared fixtures.
4. **Assert on structure, not strings.** Replace `output.ShouldContain("Active:")` with structured assertions on rendering output (already proven by `TestConsole` pattern in rendering tests).

---

## EPICs

### EPIC-001: Shared test data builders — **DONE** ✓

**Completed:** 2026-03-22

**Problem:** `MakeItem`, `MakeSeed`, `BuildAgileConfig`, `BuildBasicProcessConfiguration` are copy-pasted across 15+ test files with inconsistent signatures.

**Solution:** Create shared builder types in a `tests/Twig.TestKit/` project (or `Twig.Domain.Tests/TestKit/` folder) with fluent APIs.

| # | Task | Scope | Status |
|---|------|-------|--------|
| 1 | **`WorkItemBuilder`** — Fluent builder: `new WorkItemBuilder(1, "Fix login").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").Build()`. Defaults all fields to sensible values. Returns `WorkItem`. | `Twig.TestKit/` or `Twig.Domain.Tests/` | DONE |
| 2 | **`ProcessConfigBuilder`** — Fluent builder: `ProcessConfigBuilder.Agile()`, `.Scrum()`, `.Cmmi()`, `.Custom(...)`. Returns `ProcessConfiguration` with standard state entries, type hierarchy, and backlog levels. Replaces inline construction across 10+ files. | Same | DONE |
| 3 | **`SprintHierarchyBuilder`** — Given a list of WorkItems (from WorkItemBuilder), build a `SprintHierarchy` with proper parent chains and assignee groups. Replaces manual `Build()` calls + parentLookup construction in tests. | Same | DONE |
| 4 | **`WorkspaceBuilder`** — Fluent builder: `new WorkspaceBuilder().WithContext(item).WithSprintItems(items).WithSeeds(seeds).WithHierarchy(hierarchy).Build()`. Replaces manual `Workspace.Build()` boilerplate. | Same | DONE |
| 5 | **Migrate existing tests** — Replace local `MakeItem` / `BuildConfig` helpers with builder calls across all test projects. Remove dead local helpers. | All test projects | DONE |

**Outcome:** Shared vocabulary for test data. New test files take ~5 lines of setup instead of ~30.

**Implementation Notes:** 13 test files migrated. Net reduction of ~313 lines of duplicated test helper code. `AgileUserStoryOnly()` used for files exercising only User Story states; `Agile()` used for files needing the broader type hierarchy. `ToStateEntries()` preserved in `StateTransitionServiceTests` for malformed-record tests. All 2303 tests pass.

---

### EPIC-002: Extract command orchestration into domain services — **DONE** ✓

**Completed:** 2026-03-22

**Problem:** Commands are "fat" — they orchestrate 8–14 dependencies directly. Tests must mock all of them, even when testing a small behavioral slice.

**Solution:** Extract orchestration logic into focused domain services. Commands become thin CLI shells that parse args, delegate to services, and format output. Tests can then:
- Test the **service** with 2–4 mocks (behavioral tests)
- Test the **command** with 1–2 mocks (wiring tests, or skip entirely)

| # | Task | New Service | Command Params Before → After | Status |
|---|------|------------|-------------------------------|--------|
| 1 | **`RefreshOrchestrator`** — Encapsulates WIQL construction → ADO fetch → conflict detection → batch save. Accepts `IAdoWorkItemService`, `IWorkItemRepository`, `ProtectedCacheWriter`, config. `RefreshCommand` delegates to it. | `Twig.Domain/Services/` | RefreshCommand: 14 → ~8 | DONE |
| 2 | **`FlowTransitionService`** — Encapsulates item resolution → save → state transition → optional PR creation. Accepts `ActiveItemResolver`, `ProtectedCacheWriter`, `IProcessConfigProvider`. FlowDone/FlowClose delegate to it. | `Twig.Domain/Services/` | FlowDoneCommand: 13 → 10, FlowCloseCommand: 12 → 9 | DONE |
| 3 | **`StatusOrchestrator`** — Encapsulates active item resolution + pending changes + working set + sync coordination into a single "status snapshot" result. `StatusCommand` delegates to it. | `Twig.Domain/Services/` | StatusCommand: 12 → ~6 | DONE |
| 4 | **Migrate command tests** — For each extracted service, move behavioral tests from command test files to service test files. Command tests reduce to: "does the command call the service and format the output?" (1–2 mocks). | All test projects | | DONE |
| 5 | **Remove redundant command tests** — After extraction, some command test files that were testing orchestration logic (now in services) become dead. Audit and remove. | `Twig.Cli.Tests/` | | DONE |

**Outcome:** Command tests shrink from 100+ lines of setup to ~20. Service tests are focused and fast.

**Risk:** This is the highest-effort EPIC. Each extraction touches the command, the DI registration, and the tests. Sequential implementation (one command at a time) minimizes merge risk.

**Implementation Notes:** FlowTransitionService bug fixed (OriginalState captured before ChangeState mutation). FlowDoneCommand reduced 13→10 params, FlowCloseCommand reduced 12→9 params. RefreshOrchestrator (11 tests) and StatusOrchestrator (6 tests) have comprehensive test coverage. RefreshCommand and StatusCommand intentionally left unconverted — orchestrators are registered and ready for EPIC-003/004. All 2,337 tests passing.

---

### EPIC-003: Eliminate Console.SetOut capture tests — **DONE** ✓

**Completed:** 2026-03-22

**Problem:** 20+ test files redirect `Console.SetOut()` to assert on formatted output strings. These tests:
- Break on any cosmetic change (spacing, wording, icons, color codes)
- Test the formatter, the command, and the domain logic simultaneously
- Can't distinguish "wrong output" from "slightly different output"

**Solution:** Replace stdout capture with structured assertions at the right layer:
- **Formatter tests** → Assert on `IOutputFormatter` method return values (already the pattern in `HumanOutputFormatterTests`)
- **Command exit code tests** → Assert on return value (`result.ShouldBe(0)`) — don't care about output
- **Rendering tests** → Use `TestConsole.Output` pattern (already in `Rendering/` tests)

| # | Task | Scope |
|---|------|-------|
| 1 | **Audit all `Console.SetOut` usage** — Catalog every test that captures stdout/stderr. Classify each as: formatter test (move assertion to formatter), exit code test (drop output assertion), or rendering test (use TestConsole). | Analysis |
| 2 | **Convert formatter-focused tests** — Tests that assert `output.ShouldContain("Active:")` should call the formatter directly and assert on the returned string. No command execution needed. | `Twig.Cli.Tests/Commands/` → `Twig.Cli.Tests/Formatters/` |
| 3 | **Convert exit-code-only tests** — Tests that only care "command failed/succeeded" should remove SetOut and assert on the return code. | `Twig.Cli.Tests/Commands/` |
| 4 | **Convert rendering-focused tests** — Tests that need terminal output should inject `TestConsole` (via `RenderingPipelineFactory`) — the pattern already exists in `Rendering/CacheRefreshTests`. | `Twig.Cli.Tests/` |
| 5 | **Delete residual SetOut tests** — After migration, any remaining SetOut test is dead weight. Remove. | `Twig.Cli.Tests/` |

**Outcome:** Zero `Console.SetOut` in the test codebase. Tests stop breaking on cosmetic changes.

**Implementation Notes:** Audit found that the actual scope was 11 tests across 4 files using global `Console.SetError()` redirects (not Console.SetOut). Most test files already used proper dependency injection (passing `TextWriter` to `ExceptionHandler.Handle()`, `GitGuard.EnsureGitRepoAsync()`, etc.). The fix added optional `TextWriter? stderr` constructor parameters to RefreshCommand, SaveCommand, and StatusCommand, following the existing HookHandlerCommand pattern. Tests now pass StringWriter via constructor instead of global Console.SetError. Assembly-level parallelization disable retained (needed for file system races in Init/PromptState tests), comment updated. All 2,331 tests passing.

---

### EPIC-004: Prune trivial and duplicate tests — **DONE** ✓

**Completed:** 2026-03-22

**Problem:** ~50 tests verify single properties, constructor existence, or `ShouldBeOfType<T>()`. Some command families have 3 test files testing overlapping scenarios (`StatusCommandTests` + `StatusCommandAsyncTests` + `StatusCommandGitTests`).

**Solution:** Remove tests that add no behavioral value. Consolidate split command test files where the split doesn't represent meaningfully different test scenarios.

| # | Task | Scope | Status |
|---|------|-------|--------|
| 1 | **Identify trivial tests** — Tests that assert only: constructor doesn't throw, property returns value, type is correct. Tag with `// TRIVIAL` for review. | All test projects | DONE |
| 2 | **Delete pure-property tests** — Tests like `color.Value.R.ShouldBe((byte)255)` that verify framework behavior, not application behavior. Keep tests where the property has business logic. | Selective deletion | DONE |
| 3 | **Consolidate command test triads** — `StatusCommandTests` + `StatusCommandAsyncTests` + `StatusCommandGitTests` → merge into a single `StatusCommandTests` with clearly named test methods. Same for Tree and Workspace. | `Twig.Cli.Tests/Commands/` | DONE |
| 4 | **Remove dead helpers** — After EPIC-001 (builders) and EPIC-002 (service extraction), local test helpers become dead. Sweep and remove. | All test projects | DONE |

**Outcome:** 16 fewer tests (2,331 → 2,315), 4 test files eliminated, 3 consolidated. All remaining tests verify behavior.

**Implementation Notes:** Removed 13 duplicate Theory cases from ResolveTypeBadgeTests (exact duplicate of KnownType_NoIconId tests), 2 trivial tests from WorkItemTypeTests (Constants_HaveCorrectValues, ToString_ReturnsValue — both covered by Parse tests), and 1 trivial config default test from TreeCommandTests (Tree_DefaultDepth_Uses10). Consolidated StatusCommandTests + StatusCommandAsyncTests + StatusCommandGitTests into single StatusCommandTests with clear section comments (Core, Async path, Sync fallback, SpectreRenderer, Git context). Same for TreeCommandTests (merged AsyncTests) and WorkspaceCommandTests (merged AsyncTests). Duplicate CreateWorkItem helpers and OutputFormatterFactory/HintEngine instantiations eliminated across merged files. All 2,315 tests passing.

---

## Implementation Sequence

```
EPIC-001: Shared test data builders (5 tasks)
  ├── Task 1: WorkItemBuilder
  ├── Task 2: ProcessConfigBuilder
  ├── Task 3: SprintHierarchyBuilder
  ├── Task 4: WorkspaceBuilder
  └── Task 5: Migrate existing tests to builders

EPIC-002: Extract command orchestration (5 tasks)
  ├── Task 1: RefreshOrchestrator service + tests
  ├── Task 2: FlowTransitionService + tests
  ├── Task 3: StatusOrchestrator + tests
  ├── Task 4: Migrate command tests to service tests
  └── Task 5: Remove redundant command tests

EPIC-003: Eliminate Console.SetOut capture (5 tasks)
  ├── Task 1: Audit all SetOut usage
  ├── Task 2: Convert formatter-focused tests
  ├── Task 3: Convert exit-code-only tests
  ├── Task 4: Convert rendering-focused tests
  └── Task 5: Delete residual SetOut tests

EPIC-004: Prune trivial and duplicate tests (4 tasks)
  ├── Task 1: Identify trivial tests
  ├── Task 2: Delete pure-property tests
  ├── Task 3: Consolidate command test triads
  └── Task 4: Remove dead helpers

Prerequisite chain:
  EPIC-001 → EPIC-002 (builders needed before service extraction tests)
  EPIC-001 → EPIC-003 (builders simplify converted tests)
  EPIC-002 + EPIC-003 → EPIC-004 (pruning happens after restructure)
```

---

## What This Plan Does NOT Change

- **Test framework** — xUnit + Shouldly + NSubstitute stays. No migration to MSTest or different assertion library.
- **Infrastructure tests** — Already well-structured (`:memory:` SQLite, injectable auth, proper IDisposable). No changes needed.
- **Domain test isolation** — Already pure. Only improvement is shared builders.
- **Integration test gating** — Already properly segregated with `[Trait]` + env guards.
- **TUI tests** — Low priority, small surface (2 files, 33 tests).
- **AOT smoke tests** — Specialized, no change needed.

---

## Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| Avg mocks per command test class | 7.3 | ≤ 3 |
| `Console.SetOut` test files | 20+ | 0 |
| Duplicate `MakeItem` helpers | 5+ files | 0 (shared builder) |
| Total test count | ~1,878 | ~1,750–1,800 (quality over quantity) |
| Test run time | baseline | No regression (expect improvement) |

---

## Decision Log

| Decision | Rationale |
|----------|-----------|
| Builders in shared location, not per-project | WorkItem, ProcessConfiguration are cross-cutting — domain and CLI tests both need them |
| Extract services, don't add adapter layers | Adding interfaces just for testing creates more code to maintain. Concrete domain services with constructor injection are already mockable via NSubstitute |
| Don't create a custom test framework | The xUnit + Shouldly + NSubstitute stack is mature and well-understood. Improvement comes from better test architecture, not better tooling |
| Prune last, not first | Deleting tests before restructuring risks removing tests that *should* exist but are in the wrong place. Restructure first, then prune what's clearly dead |
| EPIC-002 is highest risk + highest value | Fat commands are the root cause of most test pain. Fixing this fixes downstream issues (SetOut capture, massive setup, duplicate coverage) |
