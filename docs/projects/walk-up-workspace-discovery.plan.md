# Walk-Up Directory Search for .twig/ Workspace

**Work Item:** #1615
**Status:** ✅ Done
**Revision:** 2 — Addressed tech/readability review feedback

---

## Executive Summary

Twig currently resolves the `.twig/` workspace directory by looking only in `Directory.GetCurrentDirectory()`. This means users must be in the exact directory where `twig init` was run, or commands fail. This plan introduces walk-up directory search — starting from CWD and traversing parent directories until `.twig/` is found or the filesystem root is reached — identical to how `git` finds `.git/`. This is implemented as a single `WorkspaceDiscovery` class in `Twig.Infrastructure.Config`, with all 9 call sites across 7 source files (CLI, TUI, and MCP entry points) updated to use it. One additional call site (OhMyPosh shell hooks) is identified but deferred to a follow-up. The change is backward-compatible: if `.twig/` is in CWD, behavior is identical to today.

## Background

### Current State

The twig CLI resolves its workspace via a simple `Path.Combine(Directory.GetCurrentDirectory(), ".twig")` pattern. This is hardcoded in **9 call sites** across **7 source files**, plus one additional site in OhMyPosh shell hooks (deferred). The `TwigPaths` class is a value object that holds resolved paths (`TwigDir`, `ConfigPath`, `DbPath`) but does not perform discovery — it receives `twigDir` as a constructor parameter or via `BuildPaths(twigDir, config)`.

Configuration loads from `.twig/config` via `TwigConfiguration.Load(configPath)`. The SQLite database lives at `.twig/{org}/{project}/twig.db` (multi-context layout) or `.twig/twig.db` (legacy flat layout). Both derive from the same `twigDir` root.

### Motivation

Twig is used as a development companion tool. Developers frequently navigate to subdirectories (`src/Twig.Domain/Services/`, `tests/Twig.Cli.Tests/`, etc.) during coding, and having to `cd` back to the project root just to run `twig status` or `twig set` creates unnecessary friction. Git solved this identical problem decades ago with walk-up `.git/` discovery.

### Prior Art

- **Git**: Walks up from CWD to find `.git/`, sets `GIT_WORK_TREE` and `GIT_DIR`.
- **Node.js**: `package.json` resolution walks up to find the nearest `node_modules/`.
- **Rust/Cargo**: `Cargo.toml` discovery walks up from CWD.
- **Twig's `WorkspaceGuard`** (MCP): Already accepts `cwd` as a parameter but only checks the direct CWD for `.twig/config`.

### Call-Site Audit

All locations in source code that resolve `.twig/` from `Directory.GetCurrentDirectory()`:

| # | File | Line | Current Usage | Impact |
|---|------|------|---------------|--------|
| 1 | `src/Twig/Program.cs` | 34 | `var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");` — CLI DI bootstrap | **Must update** — primary entry point for all CLI commands |
| 2 | `src/Twig/Program.cs` | 106 | `var twigDirCheck = Path.Combine(Directory.GetCurrentDirectory(), ".twig");` — Smart landing (no-args) | **Must update** — determines if `twig` with no args shows help or status |
| 3 | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | 50 | Config fallback factory (`preloadedConfig is null` path) | **Must update** — fallback path when config not pre-loaded |
| 4 | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | 60 | TwigPaths factory: `Path.Combine(Directory.GetCurrentDirectory(), ".twig")` | **Must update** — creates TwigPaths singleton |
| 5 | `src/Twig.Tui/Program.cs` | 16 | `var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");` | **Must update** — TUI workspace guard |
| 6 | `src/Twig.Mcp/Program.cs` | 19 | `WorkspaceGuard.CheckWorkspace(Directory.GetCurrentDirectory())` | **Must update** — MCP workspace guard |
| 7 | `src/Twig.Mcp/Program.cs` | 26 | `Path.Combine(Directory.GetCurrentDirectory(), ".twig", "config")` | **Must update** — MCP config path |
| 8 | `src/Twig.Mcp/WorkspaceGuard.cs` | 8 | `Path.Combine(cwd, ".twig", "config")` — checks only direct CWD | **Must update** — add walk-up search |
| 9 | `src/Twig/Commands/EditorLauncher.cs` | 30 | `Path.Combine(Directory.GetCurrentDirectory(), ".twig")` — EDIT_MSG temp file | **Must update** — needs discovered twigDir |
| 10 | `src/Twig/Commands/OhMyPoshCommands.cs` | 131, 153, 176 | Shell hooks reference `$PWD/.twig/prompt.json` in PowerShell (line 131), Bash/Zsh (line 153), and Fish (line 176) | **Deferred** — shell hook walk-up is a separate concern (see Open Question #1). Not in scope for this PR group. |

**Note:** `InitCommand.cs` line 403 uses `Directory.GetCurrentDirectory()` to resolve `.gitignore` (not `.twig/`). This is unaffected — `init` always operates on CWD by design.

**Note:** `GlobalProfilePaths` uses `~/.twig/profiles/` (user-home). This is unaffected — it's a global store, not a workspace path.

## Problem Statement

When a developer runs `twig status` (or any twig command) from `src/Twig.Domain/Services/`, twig cannot find the `.twig/` directory because it only checks the current working directory. The user sees: *"Twig workspace not initialized. Run 'twig init' first."* — a misleading error since the workspace *does* exist, just not in the immediate CWD.

This is the most common friction point reported during dogfooding. It breaks the mental model established by tools like `git`, `cargo`, and `npm` where you can work from any subdirectory within a project.

## Goals and Non-Goals

### Goals

1. **Walk-up discovery**: `twig` commands work from any subdirectory of a project that has `.twig/` at an ancestor level.
2. **Nearest wins**: If multiple `.twig/` directories exist in the ancestor chain, the nearest one (closest to CWD) is selected.
3. **Backward compatible**: When `.twig/` exists in CWD, behavior is identical to today — no change in path resolution, no performance difference.
4. **Clear error on miss**: When no `.twig/` is found anywhere up to the filesystem root, provide an actionable error: `"No twig workspace found. Run 'twig init' in your project root."`.
5. **All entry points**: CLI, TUI, and MCP server all use the same discovery logic.
6. **Testable**: The discovery logic is a pure function that accepts CWD as input (no static filesystem coupling in tests).

### Non-Goals

1. **`twig init --global`**: User-level workspace (`~/.twig/`) is a future enhancement (tracked separately). This plan only addresses project-level walk-up.
2. **`TWIG_DIR` environment variable override**: While useful, this is a separate feature. The discovery function should be designed to allow this later.
3. **Symlink resolution**: Walk-up uses `Directory.GetParent()` which follows the logical path. Symlink-aware resolution is out of scope.
4. **Git worktree integration**: Not coupling twig workspace discovery to `.git/` location.

## Requirements

### Functional

- **FR-1**: Walk-up search from CWD to filesystem root, checking for `.twig/` directory existence at each level.
- **FR-2**: Return the first (nearest) `.twig/` directory found.
- **FR-3**: `twig init` continues to create `.twig/` in CWD (no walk-up for init).
- **FR-4**: Error message on workspace-not-found must suggest `twig init`.


### Non-Functional

- **NFR-1**: Walk-up adds negligible overhead — at most ~10 directory existence checks (typical project depth 3-5 levels).
- **NFR-2**: No new external dependencies.
- **NFR-3**: AOT-compatible — no reflection, no dynamic code generation.
- **NFR-4**: No change to the `.twig/` directory layout or contents.

## Proposed Design

### Architecture Overview

The design introduces a single new class — `WorkspaceDiscovery` — in `Twig.Infrastructure.Config` that encapsulates the walk-up search algorithm. All existing call sites are updated to use it instead of hardcoded CWD+".twig" concatenation.

```
┌─────────────────────────────────────────────────────┐
│                   Entry Points                       │
│  CLI Program.cs  │  TUI Program.cs  │  MCP Program.cs│
└──────┬───────────┴──────┬───────────┴──────┬────────┘
       │                  │                  │
       ▼                  ▼                  ▼
┌─────────────────────────────────────────────────────┐
│           WorkspaceDiscovery.FindTwigDir()           │
│  Input:  startDir (defaults to CWD)                  │
│  Output: string? twigDir (null = not found)          │
│  Algorithm: Walk parent dirs, check Directory.Exists │
└──────────────────────┬──────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│              TwigPaths.BuildPaths(twigDir, config)   │
│              TwigConfiguration.Load(configPath)      │
│              (unchanged — receive twigDir as input)  │
└─────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `WorkspaceDiscovery` (new — `Twig.Infrastructure.Config`)

A static class with a single method:

```csharp
public static class WorkspaceDiscovery
{
    /// Returns the .twig/ directory path, or null if not found.
    public static string? FindTwigDir(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".twig");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
```

**Design decisions:**
- **Static class**: No instance state needed. Follows the pattern of `GlobalProfilePaths`.
- **Nullable return**: Callers decide how to handle not-found (error message, help screen, etc.).
- **`startDir` parameter**: Defaults to CWD for production; tests pass explicit directories.
- **`Directory.GetParent()`**: Returns null at filesystem root, terminating the loop naturally.

**Edge case — partial `.twig/` directory:** The walk-up algorithm checks `Directory.Exists(".twig")` and will find a `.twig/` directory even if it contains no `config` file (e.g., from an interrupted `twig init` or manual directory creation). This is **intentional**: `WorkspaceDiscovery.FindTwigDir()` is responsible only for *locating* the workspace root, not *validating* it. Downstream callers already handle missing config gracefully — `TwigConfiguration.Load()` returns defaults for missing files, and `WorkspaceGuard.CheckWorkspace()` explicitly checks for the config file and returns an error if absent. If walk-up were to check for both the directory and the config file, a partial `.twig/` without config would be silently skipped, and the search would continue to an ancestor — potentially finding a completely unrelated workspace, which is worse than finding the partial one and reporting a clear error.

#### 2. `TwigServiceRegistration` (modified)

The `AddTwigCoreServices` method gains an optional `twigDir` parameter. When provided, it uses that instead of `Path.Combine(Directory.GetCurrentDirectory(), ".twig")`. This eliminates the two hardcoded CWD references in the DI registration.

```csharp
public static IServiceCollection AddTwigCoreServices(
    this IServiceCollection services,
    TwigConfiguration? preloadedConfig = null,
    string? twigDir = null)  // NEW parameter
```

#### 3. `WorkspaceGuard` (modified — MCP)

Updated to use walk-up discovery instead of direct CWD check:

```csharp
internal static (bool IsValid, string? Error, string? TwigDir) CheckWorkspace(string cwd)
{
    var twigDir = WorkspaceDiscovery.FindTwigDir(cwd);
    if (twigDir is null)
        return (false, "No twig workspace found. Run 'twig init' in your project root.", null);
    var configPath = Path.Combine(twigDir, "config");
    return File.Exists(configPath)
        ? (true, null, twigDir)
        : (false, "Twig workspace not initialized. Run 'twig init' first.", null);
}
```

### Data Flow

**Before (current):**
```
CWD → hardcode ".twig" → TwigPaths → commands
```

**After (proposed):**
```
CWD → WorkspaceDiscovery.FindTwigDir() → twigDir
  → TwigConfiguration.Load(twigDir + "/config") → config
  → TwigPaths.BuildPaths(twigDir, config) → paths
  → AddTwigCoreServices(config, twigDir) → DI container
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Walk-up vs. env var | Walk-up first, env var later | Walk-up is the common case; env var can layer on top in a future PR |
| Discovery class location | `Twig.Infrastructure.Config` | Co-located with `TwigPaths`, `TwigConfiguration`, `GlobalProfilePaths` |
| Static vs. injectable | Static class | No mutable state, no dependencies. Same pattern as `GlobalProfilePaths` |
| Init behavior | No walk-up for init | Init creates `.twig/` — it should always use CWD as the target directory |
| Return type | `string?` (nullable) | Clean null-check pattern; callers already handle the not-found case with different error messages |
| EditorLauncher twigDir | Inject via DI (TwigPaths) | EditorLauncher implements `IEditorLauncher`; add `TwigPaths` as a constructor parameter to access `TwigPaths.TwigDir` instead of hardcoding CWD |

## Dependencies

### External Dependencies
None. Uses only `System.IO.Directory`, `System.IO.Path` — already available.

### Internal Dependencies
- `TwigPaths` — unchanged API, receives `twigDir` as it does today.
- `TwigConfiguration` — unchanged API, receives `configPath` as it does today.
- `TwigServiceRegistration` — gains optional `twigDir` parameter (backward-compatible).

### Sequencing Constraints
None. This is a self-contained change with no dependency on in-flight work.

## Impact Analysis

### Components Affected
- **CLI Program.cs**: Bootstrap path changes from hardcoded to discovered.
- **TUI Program.cs**: Same bootstrap change.
- **MCP Program.cs + WorkspaceGuard**: Same bootstrap change.
- **TwigServiceRegistration**: Gains `twigDir` parameter; fallback factories updated.
- **EditorLauncher**: Uses `TwigPaths.TwigDir` instead of `Directory.GetCurrentDirectory()`.
- **LegacyDbMigrator**: `LegacyDbMigrator.MigrateIfNeeded(twigDir, config)` at `Program.cs` line 41 consumes the same `twigDir` variable that this plan proposes to resolve via walk-up. After walk-up, `twigDir` will point to the *discovered* workspace (which may be in an ancestor directory). This is the **correct and desired behavior** — migration should target the workspace that twig is actually using. `LegacyDbMigrator` receives `twigDir` as a parameter (not from CWD), so it will naturally migrate the discovered workspace. No code changes are needed in `LegacyDbMigrator` itself.
### Backward Compatibility
**Fully backward-compatible.** When `.twig/` exists in CWD, `FindTwigDir()` returns on the first iteration — identical to current behavior. The walk-up only activates when `.twig/` is NOT in CWD.

### Performance
Walk-up checks `Directory.Exists()` at each ancestor level. Typical project depth is 3-5 levels. Each `Directory.Exists()` is a single syscall. Total added latency: < 1ms.

## Alternatives Considered

No meaningful alternatives were evaluated for the core walk-up mechanism — this is a well-established pattern (git, cargo, npm) with a single obvious implementation. The only design choice was whether `FindTwigDir()` should check for the directory alone vs. directory-plus-config-file; this is addressed in the edge case discussion under Key Components.

## Security Considerations

No security implications. The walk-up search only checks for the existence of directories the user already has access to. No new filesystem permissions are required, no data crosses trust boundaries, and no new attack surface is introduced.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Nested `.twig/` directories cause unexpected workspace resolution | Low | Low | Nearest-wins behavior matches git convention; `twig init` warning for parent `.twig/` is a follow-up (Open Question #2) |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the OhMyPosh shell hooks do walk-up search or rely on an env var set by twig? | Low | Walk-up in shell is simple (5 lines per shell). An env var like `$TWIG_DIR` would be cleaner but requires twig to set it — chicken-and-egg. Recommend walk-up in shell hooks for now. |
| 2 | Should `twig init` warn if a parent directory already has `.twig/`? | Low | Could create confusion with nested workspaces. A warning is harmless and helpful. Consider as a follow-up. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Config/WorkspaceDiscovery.cs` | Walk-up directory search algorithm |
| `tests/Twig.Infrastructure.Tests/Config/WorkspaceDiscoveryTests.cs` | Unit tests for discovery logic |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Replace hardcoded CWD+".twig" with `WorkspaceDiscovery.FindTwigDir()` at lines 34 and 106; `LegacyDbMigrator.MigrateIfNeeded()` at line 41 receives the discovered `twigDir` (no change to migrator itself) |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Add optional `twigDir` parameter; use it in both factory lambdas (lines 50, 60) |
| `src/Twig.Tui/Program.cs` | Replace hardcoded CWD+".twig" with `WorkspaceDiscovery.FindTwigDir()` at line 16 |
| `src/Twig.Mcp/Program.cs` | Use discovered twigDir from updated `WorkspaceGuard` |
| `src/Twig.Mcp/WorkspaceGuard.cs` | Add walk-up search; return discovered twigDir in tuple |
| `src/Twig/Commands/EditorLauncher.cs` | Inject `TwigPaths` via constructor; use `TwigPaths.TwigDir` instead of `Directory.GetCurrentDirectory()` |
| `tests/Twig.Mcp.Tests/ProgramBootstrapTests.cs` | Update tests for new WorkspaceGuard return type |
| `tests/Twig.Cli.Tests/Commands/EditorLauncherEnhancedTests.cs` | Update for DI-injected TwigPaths |

## ADO Work Item Structure

**Parent Issue:** #1615 — Walk-up directory search for .twig/ workspace

### Task 1: Core Discovery Logic

**Goal:** Implement the `WorkspaceDiscovery` static class with walk-up search algorithm and comprehensive unit tests.

**Prerequisites:** None

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-1.1 | Create `WorkspaceDiscovery.FindTwigDir()` static method | `src/Twig.Infrastructure/Config/WorkspaceDiscovery.cs` | ~30 LoC |
| T-1.2 | Write unit tests: found-in-CWD, found-in-ancestor, not-found, nested-nearest-wins, filesystem-root | `tests/Twig.Infrastructure.Tests/Config/WorkspaceDiscoveryTests.cs` | ~100 LoC |

**Acceptance Criteria:**
- [ ] `FindTwigDir()` returns the `.twig/` path when found at CWD
- [ ] `FindTwigDir()` walks up and returns nearest `.twig/` in ancestor chain
- [ ] `FindTwigDir()` returns null when no `.twig/` exists up to root
- [ ] `FindTwigDir()` selects nearest `.twig/` when multiples exist
- [ ] All tests pass

### Task 2: DI Registration Update

**Goal:** Update `TwigServiceRegistration` to accept an explicit `twigDir` parameter, eliminating hardcoded CWD references in the DI container setup.

**Prerequisites:** Task 1

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-2.1 | Add optional `twigDir` parameter to `AddTwigCoreServices()` and update both factory lambdas (config fallback + TwigPaths) | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | ~25 LoC |

**Acceptance Criteria:**
- [ ] `AddTwigCoreServices(config, twigDir)` uses provided `twigDir` instead of CWD
- [ ] Existing callers without `twigDir` get backward-compatible default behavior
- [ ] Builds without warnings

### Task 3: CLI Entry Point Integration

**Goal:** Update `Program.cs` (CLI) to use `WorkspaceDiscovery.FindTwigDir()` for all workspace resolution, including smart landing and DI bootstrap.

**Prerequisites:** Task 1, Task 2

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-3.1 | Replace DI bootstrap `twigDir` with `WorkspaceDiscovery.FindTwigDir()`, update smart-landing check, and update error message for workspace-not-found | `src/Twig/Program.cs` | ~25 LoC |

**Acceptance Criteria:**
- [ ] `twig status` from a subdirectory of a project with `.twig/` at the root works
- [ ] `twig` with no args from a subdirectory shows status (not help) when workspace exists at ancestor
- [ ] `twig` from a directory with no `.twig/` anywhere in ancestors shows help
- [ ] `twig init` still creates `.twig/` in CWD (not walked-up)

### Task 4: TUI and MCP Entry Point Integration

**Goal:** Update TUI `Program.cs`, MCP `Program.cs`, and `WorkspaceGuard` to use walk-up discovery.

**Prerequisites:** Task 1, Task 2

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-4.1 | Update `WorkspaceGuard.CheckWorkspace()` with walk-up search | `src/Twig.Mcp/WorkspaceGuard.cs` | ~15 LoC |
| T-4.2 | Update MCP `Program.cs` to consume discovered `twigDir` from guard | `src/Twig.Mcp/Program.cs` | ~10 LoC |
| T-4.3 | Update TUI `Program.cs` with walk-up discovery | `src/Twig.Tui/Program.cs` | ~10 LoC |
| T-4.4 | Update MCP tests for new guard return type | `tests/Twig.Mcp.Tests/ProgramBootstrapTests.cs` | ~20 LoC |

**Acceptance Criteria:**
- [ ] MCP server starts from any subdirectory with `.twig/` at ancestor level
- [ ] TUI starts from any subdirectory with `.twig/` at ancestor level
- [ ] Error messages are actionable when no workspace found
- [ ] Existing tests pass or are updated

### Task 5: EditorLauncher Update

**Goal:** Update `EditorLauncher` to use DI-injected `TwigPaths` instead of hardcoded CWD.

**Prerequisites:** Task 2

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T-5.1 | Inject `TwigPaths` into `EditorLauncher`; use `TwigPaths.TwigDir` for EDIT_MSG path | `src/Twig/Commands/EditorLauncher.cs` | ~15 LoC |
| T-5.2 | Update EditorLauncher tests for DI change | `tests/Twig.Cli.Tests/Commands/EditorLauncherEnhancedTests.cs` | ~20 LoC |

**Acceptance Criteria:**
- [ ] `twig edit` works from subdirectory (EDIT_MSG written to correct `.twig/`)
- [ ] All existing tests pass

## PR Groups

> **Classification terms:** **Deep** = few files with complex, logic-heavy changes requiring careful review. **Wide** = many files with mechanical, pattern-following changes that are individually simple but span the codebase.

### PG-1: Core discovery + DI wiring

**Tasks:** T-1.1, T-1.2, T-2.1
**Classification:** Deep (few files, complex logic — the walk-up algorithm and DI plumbing require careful review)
**Estimated LoC:** ~155
**Description:** Introduces `WorkspaceDiscovery` class and updates `TwigServiceRegistration` to accept explicit `twigDir`. This is the foundation all other work depends on. Pure additive — no existing behavior changes until entry points are updated.

### PG-2: Entry point integration (CLI + TUI + MCP + EditorLauncher)

**Tasks:** T-3.1, T-4.1, T-4.2, T-4.3, T-4.4, T-5.1, T-5.2
**Classification:** Wide (many files, mechanical changes — each entry point gets the same FindTwigDir() → pass-through pattern)
**Estimated LoC:** ~135
**Predecessors:** PG-1
**Description:** Updates all three entry points (CLI, TUI, MCP) to use `WorkspaceDiscovery.FindTwigDir()`, passes the discovered `twigDir` through DI, and updates EditorLauncher to use DI-injected paths. This is the PR that activates walk-up behavior.

### Execution Order

```
PG-1 (core + DI)
└── PG-2 (entry points + editor)  ← depends on PG-1
```

## Completion

**Completed:** 2026-04-15
**Delivered via:** PR #35 (merged 2026-04-15T17:28:27Z)
**Release tag:** v0.39.1

### Summary

All 10 tasks across 2 PR groups were implemented and merged in a single day. The `WorkspaceDiscovery.FindTwigDir()` walk-up search is now active across all three entry points (CLI, TUI, MCP), the DI registration, and `EditorLauncher`. Unit tests cover CWD, ancestor, not-found, nearest-wins, and root boundary scenarios.

### Process Notes

- **PG-1** and **PG-2** were co-merged in a single PR (#35) rather than delivered as separate PRs. All code went through PR review — no direct-to-main commits.
- Commit cadence was steady: 10 commits over ~4 hours, with two reduction sweeps interspersed.
- All acceptance criteria from the plan were met without scope changes.

## References

- Git's `discover_git_directory()`: https://github.com/git/git/blob/master/setup.c
- Issue #1615: Walk-up directory search for .twig/ workspace
- Related Task #1608 under Issue #1604 (may be superseded by this work)


