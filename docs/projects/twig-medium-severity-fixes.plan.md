# Twig — MEDIUM Severity Fixes (M-001, M-002, M-003, M-005, M-007, M-008)

**Date:** 2026-03-21
**Source:** [Architecture Analysis](twig-architecture-analysis.doc.md) § 9.2
**Scope:** Fix all 6 remaining MEDIUM-severity Gatekeeper findings (M-004 and M-006 were resolved by the HIGH-severity EPICs)

---

## 1. Problem Statement

Six MEDIUM-severity findings remain from the Gatekeeper anti-pattern review:

1. **M-001** — Missing `CancellationToken` in ~28/33 non-alias command entry points in `TwigCommands`. Only `Set`, `Up`, `Down`, `Upgrade`, and `Changelog` propagate it. The remaining commands cannot be interrupted during network calls.
2. **M-002** — Duplicated git repo check (null guard + `IsInsideWorkTreeAsync()` + try/catch) in 11 commands: Branch, Commit, FlowClose, FlowDone, FlowStart, HookHandler, GitContext, Hooks, Log, Pr, Stash.
3. **M-003** — Duplicated `ActiveItemResult` pattern-match block (8–12 lines) in 19+ commands. The exhaustive `switch` on `Found`/`FetchedFromAdo`/`Unreachable`/default is copy-pasted everywhere.
4. **M-005** — `ConflictResolutionFlow.ResolveAsync()` treats unknown user input as "proceed with local". The safe default should be abort.
5. **M-007** — `HookHandlerCommand` uses `Regex.IsMatch` without a timeout. Two call sites at lines 85 and 104.
6. **M-008** — `TwigConfiguration.SetValue()` does not validate `auth.method`. Accepts any string; should validate against known values (`azcli`, `pat`).

---

## 2. Design Decisions

### DD-001: CancellationToken propagation via TwigCommands signatures (M-001)

Add `CancellationToken ct = default` to all remaining command entry points in `TwigCommands`. Pass `ct` through to `ExecuteAsync` methods. ConsoleAppFramework automatically wires Ctrl+C to the token. Do NOT propagate `ct` deeper into every service call in this EPIC — just ensure commands accept and forward it to their `ExecuteAsync` method.

### DD-002: Extract GitGuard helper (M-002)

Create a static `GitGuard` class with a method `EnsureGitRepoAsync(IGitService?, IOutputFormatter)` that returns `(bool isValid, int exitCode)`. Replace the 15-line boilerplate in each command with a 3-line call. Place it in `src/Twig/Commands/GitGuard.cs`.

### DD-003: Add ActiveItemResult.TryGetWorkItem extension (M-003)

Add an extension method on `ActiveItemResult` that collapses the exhaustive switch to a single call: `result.TryGetWorkItem(fmt, out var item)` returning `(WorkItem? Item, int? ExitCode)` or a bool + out pattern. Place it in `src/Twig.Domain/Services/ActiveItemResultExtensions.cs` (domain layer, since ActiveItemResult is domain).

### DD-004: Default to abort on unknown conflict input (M-005)

Change the fallback in `ConflictResolutionFlow.ResolveAsync()` from `Proceed` to `Aborted`. Only explicit `"l"` input proceeds with local.

### DD-005: Add regex timeout (M-007)

Add `RegexOptions.None, TimeSpan.FromSeconds(1)` to both `Regex.IsMatch` calls in `HookHandlerCommand`.

### DD-006: Validate auth.method (M-008)

Add validation in `TwigConfiguration.SetValue()` for the `auth.method` path: only accept `"pat"` or `"azcli"`, returning `false` otherwise. Consistent with how `display.icons` and `flow.autoassign` already validate.

---

## 3. Constraints

- AOT compatibility: No reflection. Extension methods and static helpers are AOT-safe.
- The `ActiveItemResult.TryGetWorkItem` writes to `Console.Error` — this is acceptable since all existing consumers do so. The extension receives an `IOutputFormatter` to format error messages.
- `CancellationToken` propagation: Only add the parameter to `TwigCommands` signatures and pass to `ExecuteAsync()`. Do NOT refactor every downstream call in this pass.

---

## 9. Implementation Plan

### EPIC-001: Fix ConflictResolutionFlow default, regex timeout, and auth.method validation (M-005, M-007, M-008)

**Goal:** Three trivial one-line fixes grouped together.

**Prerequisites:** None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-001 | IMPL | In `ConflictResolutionFlow.ResolveAsync()`, add an explicit `if (choice == "l")` check that returns `Proceed`, and change the final fallback return to `Aborted`. Add a message: `fmt.FormatInfo("Unrecognized input. Aborted.")` | `src/Twig/Commands/ConflictResolutionFlow.cs` | DONE |
| T-002 | IMPL | In `HookHandlerCommand`, add `RegexOptions.None, TimeSpan.FromSeconds(1)` to both `Regex.IsMatch` calls (line ~85 and ~104). Add `using System.Text.RegularExpressions;` if not present | `src/Twig/Commands/HookHandlerCommand.cs` | DONE |
| T-003 | IMPL | In `TwigConfiguration.SetValue()`, change the `auth.method` case to validate: `var lower = value.ToLowerInvariant(); if (lower is not ("pat" or "azcli")) return false; Auth.Method = lower; return true;` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| T-004 | TEST | Add test for ConflictResolutionFlow: unknown input returns `Aborted`, explicit `"l"` returns `Proceed` | `tests/Twig.Cli.Tests/Commands/` | DONE |
| T-005 | TEST | Add test for TwigConfiguration.SetValue auth.method: `"pat"` and `"azcli"` accepted, `"foo"` rejected | `tests/Twig.Infrastructure.Tests/Config/` | DONE |
| T-006 | TEST | Verify all existing tests pass | All test projects | DONE |

**Acceptance Criteria:**
- [x] Unknown input in ConflictResolutionFlow returns `Aborted`
- [x] Explicit `"l"` input returns `Proceed`
- [x] Both regex calls have 1-second timeout
- [x] `auth.method` rejects unknown values
- [x] All tests pass

---

### EPIC-002: Extract GitGuard helper to reduce git repo check duplication (M-002)

**Goal:** Create a shared `GitGuard` helper and replace the duplicated 15-line git repo check pattern in 11 commands.

**Prerequisites:** None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-007 | IMPL | Create `src/Twig/Commands/GitGuard.cs` with a static method: `internal static async Task<(bool IsValid, int ExitCode)> EnsureGitRepoAsync(IGitService? gitService, IOutputFormatter fmt)`. The method should: (1) check `gitService is null` → return `(false, 1)` with error, (2) try `IsInsideWorkTreeAsync()` → catch Exception → return `(false, 1)` with error, (3) if not in work tree → return `(false, 1)` with error, (4) return `(true, 0)` | `src/Twig/Commands/GitGuard.cs` | DONE |
| T-008 | IMPL | Replace the git repo check boilerplate in all 11 commands with `var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt); if (!isValid) return exitCode;`. Commands: BranchCommand, CommitCommand, FlowCloseCommand, FlowDoneCommand, FlowStartCommand, HookHandlerCommand, GitContextCommand, HooksCommand, LogCommand, PrCommand, StashCommand | `src/Twig/Commands/*.cs` (11 files) | DONE |
| T-009 | TEST | Add unit test for `GitGuard.EnsureGitRepoAsync`: null service returns invalid, not-in-worktree returns invalid, valid repo returns valid | `tests/Twig.Cli.Tests/Commands/` | DONE |
| T-010 | TEST | Verify all existing tests pass | All test projects | DONE |

**Acceptance Criteria:**
- [x] `GitGuard.EnsureGitRepoAsync` exists and handles all three failure cases
- [x] No command contains the raw git repo check boilerplate
- [x] All tests pass

---

### EPIC-003: Extract ActiveItemResult resolution helper (M-003)

**Goal:** Add an extension method on `ActiveItemResult` that collapses the 8-12 line pattern-match block to a single call, then update all 19+ consuming commands.

**Prerequisites:** None

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-011 | IMPL | Create `src/Twig.Domain/Services/ActiveItemResultExtensions.cs` with: `internal static bool TryGetWorkItem(this ActiveItemResult result, out WorkItem? item, out int? errorId, out string? errorReason)`. Returns `true` + `item` for `Found`/`FetchedFromAdo`, `false` + `errorId`/`errorReason` for `Unreachable`, `false` + nulls for `NoContext`/default | `src/Twig.Domain/Services/ActiveItemResultExtensions.cs` | DONE |
| T-012 | IMPL | Replace the ActiveItemResult switch blocks in all consuming commands with the extension method. The caller handles formatting the error message using `errorId`/`errorReason` and the existing `IOutputFormatter`. Commands include: BranchCommand, CommitCommand, EditCommand, FlowCloseCommand, FlowDoneCommand, GitContextCommand, NavigationCommands, NoteCommand, PrCommand, SaveCommand, SeedCommand, SetCommand, StashCommand, StateCommand, StatusCommand, TreeCommand, UpdateCommand, WorkspaceCommand, and any others found | `src/Twig/Commands/*.cs` (19+ files) | DONE |
| T-013 | TEST | Add unit tests for `ActiveItemResultExtensions.TryGetWorkItem`: Found returns true, FetchedFromAdo returns true, Unreachable returns false with id/reason, NoContext returns false | `tests/Twig.Domain.Tests/Services/` | DONE |
| T-014 | TEST | Verify all existing tests pass | All test projects | DONE |

**Acceptance Criteria:**
- [x] `ActiveItemResultExtensions.TryGetWorkItem` exists
- [x] No command contains the raw 8-12 line ActiveItemResult switch block
- [x] All tests pass

---

### EPIC-004: Add CancellationToken to all remaining commands (M-001)

**Goal:** Add `CancellationToken ct = default` to all remaining command entry points in `TwigCommands` and forward to `ExecuteAsync`.

**Prerequisites:** EPIC-002 and EPIC-003 (commands may have been modified)

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-015 | IMPL | Add `CancellationToken ct = default` parameter to all remaining command signatures in `TwigCommands` that don't already have it. Forward `ct` to the `ExecuteAsync` call. Commands: Init, Status, State, Tree, Seed, Note, Update, Edit, Save, Refresh, Workspace, Show, Ws, Sprint, Config, Branch, Commit, Pr, Stash, StashPop, Log, FlowStart, FlowDone, FlowClose, HooksInstall, HooksUninstall, Context, Hook | `src/Twig/Program.cs` | TO DO |
| T-016 | IMPL | Add `CancellationToken ct = default` parameter to each command's `ExecuteAsync` method that doesn't already accept it, and pass it to `ActiveItemResolver`, `GitGuard`, and any ADO service calls that accept `CancellationToken` | `src/Twig/Commands/*.cs` (28 files) | TO DO |
| T-017 | TEST | Verify all existing tests pass — no new tests needed since CancellationToken defaults to `default` | All test projects | TO DO |

**Acceptance Criteria:**
- [ ] Every non-alias command signature in `TwigCommands` has a `CancellationToken ct = default` parameter
- [ ] Every `ExecuteAsync` method accepts and forwards `CancellationToken`
- [ ] All tests pass
