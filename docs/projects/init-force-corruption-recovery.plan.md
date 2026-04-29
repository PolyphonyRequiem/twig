# `twig init --force` Cannot Recover from Corrupted Cache

**Work Item:** #2549  
**Type:** Bug Fix  
**Revision:** 0 (Initial draft)

> **Status**: ✅ Done

## Executive Summary

`twig init --force` fails with the same "⚠ Cache corrupted" error it tells users to
run, creating an unrecoverable loop. The root cause is a transitive DI dependency chain:
resolving `InitCommand` from the container triggers `HintEngine` → `IProcessConfigurationProvider`
→ `IProcessTypeStore` → `SqliteCacheStore`, which throws before `InitCommand.ExecuteAsync()`
ever runs. Since `--force` is a command argument only accessible inside the handler,
the DI layer has no way to know it should skip corruption checks. The fix breaks this
transitive dependency by creating a standalone `HintEngine` for `InitCommand` (which
already accepts `null` for its process config provider) and adds a defensive guard to
the `SqliteCacheStore` DI factory so missing DB directories throw "not initialized"
instead of the misleading "corruption" message.

## Background

### Current Architecture

The CLI uses ConsoleAppFramework with a global `ExceptionFilter` and Microsoft DI.
Services are registered as singletons with factory lambdas for AOT compatibility.
Commands are routed through `TwigCommands`, which lazily resolves command classes via
`IServiceProvider.GetRequiredService<T>()`.

The `SqliteCacheStore` is the central database gateway — a singleton that opens the
SQLite connection, enables WAL mode, and ensures schema on construction. Every
repository and store depends on it.

`InitCommand` is special: it creates the workspace (`.twig/` directory, config, DB)
from scratch. It was designed to not directly depend on `SqliteCacheStore` from DI —
it constructs its own at line 376 after creating the directory structure. However,
a transitive dependency through `HintEngine` undermines this design.

### The Dependency Chain (Root Cause)

```
TwigCommands.Init()
  └─ services.GetRequiredService<InitCommand>()    ← DI resolution starts
       └─ InitCommand factory (CommandRegistrationModule.cs:33)
            └─ sp.GetRequiredService<HintEngine>()  ← triggers chain
                 └─ HintEngine factory (CommandServiceModule.cs:32)
                      └─ sp.GetRequiredService<IProcessConfigurationProvider>()
                           └─ IProcessTypeStore → SqliteCacheStore factory
                                └─ new SqliteCacheStore(...)  ← THROWS
```

When the DB file doesn't exist or is corrupt, `SqliteCacheStore`'s constructor
catches `SqliteException` and wraps it as `InvalidOperationException("⚠ Cache
corrupted...")`. This propagates up the DI chain, through `ExceptionFilter`,
to `ExceptionHandler.Handle()` which matches the FM-008 pattern and outputs the
same "Run 'twig init --force'" message — creating the unrecoverable loop.

### Call-Site Audit: `SqliteCacheStore` Construction

| Location | File | Type | Handles Failure? |
|----------|------|------|-----------------|
| Program.cs:80 | Pre-compute state entries | Direct construction | ✅ Yes — guarded by `File.Exists()` + try/catch |
| TwigServiceRegistration.cs:84 | DI singleton factory | Lazy resolution | ❌ **No** — only checks `Directory.Exists(TwigDir)` |
| InitCommand.cs:121 | Force-reinit pending probe | Direct construction | ✅ Yes — wrapped in try/catch |
| InitCommand.cs:376 | Post-rebuild DB creation | Direct construction | N/A — runs after directory setup |

### Call-Site Audit: `HintEngine` Dependencies

| Consumer | Needs Process Config? | Notes |
|----------|-----------------------|-------|
| `InitCommand` | **No** — `GetHints("init")` returns static text | Only uses `DisplayConfig.Hints` |
| `SetCommand` | No — static hints | |
| `StateCommand` | **Yes** — resolves state category | Needs process config for sibling analysis |
| `ShowCommand` | No — stale seed count only | |
| `WorkspaceCommand` | No — dirty item count | |
| All other commands | No — static hints | |

**Key finding:** `HintEngine`'s constructor already accepts `IProcessConfigurationProvider?`
as nullable with default `null`:
```csharp
public HintEngine(DisplayConfig displayConfig, IProcessConfigurationProvider? processConfigProvider = null)
```
And `GetHints("init", ...)` never accesses `_processConfigProvider`.

## Problem Statement

1. `twig init --force` is stuck in an unrecoverable error loop when the cache is
   corrupted, missing, or partially initialized.
2. The error message tells users to run the exact command that fails, with no
   alternative recovery path except manual `.twig/` deletion.
3. This blocks all automated worktree setup for conductor SDLC workflows (#2425).
4. The `SqliteCacheStore` DI factory lacks a guard for missing DB parent directories,
   causing "SQLITE_CANTOPEN" to be misreported as "corruption" when the nested
   context directory (`.twig/{org}/{project}/`) simply doesn't exist.

## Goals and Non-Goals

### Goals
- G1: `twig init --force` succeeds when `.twig/` doesn't exist
- G2: `twig init --force` succeeds when `.twig/` partially exists (config but no DB)
- G3: `twig init --force` succeeds when the DB file is corrupt (garbage bytes)
- G4: `twig init --force` succeeds when the DB parent directory doesn't exist
- G5: Normal commands still get the "Cache corrupted" message when the DB is corrupt
- G6: Normal commands still get "not initialized" when workspace doesn't exist
- G7: Regression tests cover all three reproduction scenarios

### Non-Goals
- Lazy-wrapping `SqliteCacheStore` for all consumers (overkill for this bug)
- Making all commands tolerant of missing workspaces (only `init` needs this)
- Changing the `SqliteCacheStore` constructor behavior (it correctly detects corruption)
- Adding retry logic to the DI container

## Requirements

### Functional
- FR-1: `twig init --force` must not trigger `SqliteCacheStore` DI resolution
- FR-2: The `SqliteCacheStore` DI factory must distinguish "missing directory" from
  "corrupt DB" — the former should say "not initialized", not "corrupted"
- FR-3: `InitCommand`'s hint engine must work without process configuration
- FR-4: All three reproduction scenarios must be covered by tests

### Non-Functional
- NFR-1: No behavior change for any command other than `init`
- NFR-2: No new dependencies or packages
- NFR-3: AOT-compatible (no reflection)

## Proposed Design

### Architecture Overview

The fix has two layers:

1. **Break the DI chain** (primary fix): Change `InitCommand`'s DI factory in
   `CommandRegistrationModule.cs` to construct `HintEngine` directly with just
   `DisplayConfig` (no `IProcessConfigurationProvider`). Since `HintEngine` already
   accepts null for process config, and `GetHints("init")` never uses it, this is
   a zero-risk change.

2. **Guard the factory** (defensive fix): Add a directory-existence check for the
   DB parent directory in the `SqliteCacheStore` DI factory in
   `TwigServiceRegistration.cs`. When the nested context directory doesn't exist,
   throw the existing "not initialized" error instead of letting SQLite throw
   `SQLITE_CANTOPEN` which gets misreported as corruption.

### Key Components

**Change 1: `CommandRegistrationModule.cs`**
```csharp
// Before:
sp.GetRequiredService<HintEngine>(),

// After:
new HintEngine(sp.GetRequiredService<TwigConfiguration>().Display),
```

This creates a standalone `HintEngine` for `InitCommand` that doesn't touch the
DB-dependent singleton. The `init` command's hints ("Run 'twig workspace' to see
your sprint...") are static text that never needs process configuration.

**Change 2: `TwigServiceRegistration.cs`**
```csharp
// Before:
if (!Directory.Exists(paths.TwigDir))
    throw new InvalidOperationException("Twig workspace not initialized...");
return new SqliteCacheStore($"Data Source={paths.DbPath}");

// After:
if (!Directory.Exists(paths.TwigDir))
    throw new InvalidOperationException("Twig workspace not initialized...");
var dbDir = Path.GetDirectoryName(paths.DbPath);
if (dbDir is not null && !Directory.Exists(dbDir))
    throw new InvalidOperationException("Twig workspace not initialized...");
return new SqliteCacheStore($"Data Source={paths.DbPath}");
```

This prevents `SqliteCacheStore` from attempting to open a DB in a non-existent
directory, which would throw `SqliteException(SQLITE_CANTOPEN)` that gets
misinterpreted as corruption.

**Change 3: Regression tests**

New tests in `InitCommandTests.cs` (or a companion file) covering:
- Init --force with no `.twig/` at all
- Init --force with `.twig/config` but no SQLite DB (worktree scenario)
- Init --force with corrupt DB file (garbage bytes)
- Init --force with missing context directory

### Data Flow

**Before fix (broken):**
```
twig init --force → DI resolves InitCommand → HintEngine → SqliteCacheStore → THROW
  → ExceptionFilter → "Cache corrupted" → exit 1
```

**After fix (working):**
```
twig init --force → DI resolves InitCommand → HintEngine(DisplayConfig only) → OK
  → InitCommand.ExecuteAsync() → deletes corrupt DB → creates fresh workspace → exit 0
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Create standalone `HintEngine` for `InitCommand` | Minimal blast radius — only `init` changes; `HintEngine` already supports null process config |
| Don't use `Lazy<SqliteCacheStore>` | Would require changing every consumer; overkill for a single-command fix |
| Add DB-directory guard in DI factory | Prevents SQLITE_CANTOPEN → corruption misdiagnosis; helps all "not initialized" scenarios |
| Don't catch corruption in `TwigCommands.Init()` | DI singletons can't be re-resolved after failure; breaking the chain is cleaner |

## Dependencies

- No external dependencies
- No new packages
- Builds on existing nullable support in `HintEngine` constructor

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Create `HintEngine` inline for `InitCommand` instead of resolving from DI |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Add DB parent-directory existence check to `SqliteCacheStore` factory |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Add regression tests for force-init with corrupted/missing DB scenarios |

## ADO Work Item Structure

This is an Issue (#2549), so we define Tasks directly under it.

### Issue: #2549 — twig init --force cannot recover from corrupted cache

**Goal:** Make `twig init --force` work in all corruption/missing-workspace scenarios
by breaking the transitive DI dependency on `SqliteCacheStore`.

**Prerequisites:** None

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | Break InitCommand → SqliteCacheStore DI chain by creating standalone HintEngine in CommandRegistrationModule.cs | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S |
| T2 | Add DB parent-directory guard to SqliteCacheStore DI factory in TwigServiceRegistration.cs | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | S |
| T3 | Add regression tests for all three reproduction scenarios (no .twig, partial .twig, corrupt DB) + missing context dir | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | M |
| T4 | Build verification — ensure all existing tests pass, no new warnings | (build + test run) | S |

**Acceptance Criteria:**
- [ ] `twig init --force` succeeds when `.twig/` doesn't exist
- [ ] `twig init --force` succeeds when `.twig/config` exists but no SQLite DB
- [ ] `twig init --force` succeeds with garbage bytes in the DB file
- [ ] `twig init --force` succeeds when context directory is missing
- [ ] All existing tests continue to pass
- [ ] No new compiler warnings (TreatWarningsAsErrors)

## PR Groups

### PG-1: Fix init --force corruption recovery

**Type:** Deep (few files, targeted logic changes)  
**Tasks:** T1, T2, T3, T4  
**Estimated LoC:** ~80 changed, ~120 new (tests)  
**Files:** ≤5  
**Successor:** None  

All changes are tightly coupled — the DI fix, the guard, and the regression tests
form a single reviewable unit. Splitting would create a PR where the fix is
unverifiable.

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the `HintEngine` DI singleton also be made resilient (e.g., catch and fallback to null process config)? | Low | Not needed for this fix — only `init` hits this path. Could be a follow-up hardening item. |

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | fix-init-force-corruption-recovery | #2549 / T1, T2, T3, T4 | None | Deep |

### Execution Order

**PG-1** is the sole PR group and contains all work for this bug fix:

- **T1** (`CommandRegistrationModule.cs`): Break the transitive DI chain by constructing
  `HintEngine` inline for `InitCommand` using only `DisplayConfig`, bypassing the
  `IProcessConfigurationProvider` → `SqliteCacheStore` resolution path.
- **T2** (`TwigServiceRegistration.cs`): Add DB parent-directory guard to the
  `SqliteCacheStore` DI factory so a missing context directory throws "not initialized"
  instead of being misreported as corruption.
- **T3** (`InitCommandTests.cs`): Regression tests covering all four scenarios (no
  `.twig/`, partial `.twig/`, corrupt DB, missing context directory).
- **T4**: Build verification — all existing tests pass, no new compiler warnings.

All four tasks are tightly coupled: the DI fix and the guard are the production changes;
the tests verify them end-to-end. Splitting them would produce a PR where the fix is
unverifiable in isolation.

### Validation Strategy

**PG-1:**
- `dotnet build` — zero warnings (TreatWarningsAsErrors)
- `dotnet test` — existing tests pass; new regression tests pass
- Manual smoke: run `twig init --force` against a workspace with (a) no `.twig/`,
  (b) partial `.twig/config` but no DB, (c) garbage DB file, (d) missing context dir
- Verify normal commands still get "Cache corrupted" / "not initialized" messages when
  appropriate (NFR-1 — no behavior change for other commands)

## References

- #2425 — Worktree setup automation (blocked by this bug)
- #2541 — MCP Agent Alignment epic (related)
- FM-008 — Cache corruption failure mode specification
- I-003 — Exception chain preservation pattern
