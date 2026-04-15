# MCP Server Closeout Findings — Implementation Plan

**Issue:** #1618 — MCP Server (Epic 1484) Closeout Findings
**Parent Epic:** #1484 — MCP Server
**Status**: 🔨 In Progress

---

## Executive Summary

This plan addresses five follow-up findings from the MCP Server epic (#1484) closeout review. The findings span branch naming consistency in `FlowStartCommand`, pre-close-out note syncing, worktree-aware close-out, task-level state verification before Issue closure, and an explicit `--id` flag for `twig update`. These are targeted, surgical improvements to the existing flow lifecycle commands (`flow-start`, `flow-done`, `flow-close`) and the `update` command. Each task is independently committable and testable. The combined effort hardens the close-out workflow, ensures data integrity (no lost notes, no premature closures), and improves command ergonomics for AI agent and scripting consumers.

## Background

The twig CLI's flow lifecycle commands (`flow-start`, `flow-done`, `flow-close`) manage work-item state transitions, branch operations, and context management. During the MCP Server epic (#1484) closeout review, five gaps were identified in these commands that could lead to data inconsistency, user friction, or incorrect board states. This plan addresses each gap with targeted, independently testable changes.

### Reader Orientation

This section provides a detailed inventory of the components, services, and call sites relevant to the five closeout tasks. It describes the current architecture and usage patterns as they exist today — proposed changes are deferred to the [Proposed Design](#proposed-design) section. Readers already familiar with the flow lifecycle may skip to the [Problem Statement](#problem-statement).

### Current Architecture

The twig CLI implements a three-stage flow lifecycle:

1. **`flow-start`** — Resolves a work item, sets active context, transitions Proposed → InProgress, assigns to self, creates/checks out a git branch via `BranchNameTemplate.Generate()`.
2. **`flow-done`** — Flushes pending changes (notes + field edits), transitions InProgress → Resolved (with Completed fallback), offers PR creation.
3. **`flow-close`** — Guards unsaved changes and open PRs, transitions to Completed, deletes the feature branch, clears active context.

Key supporting services:
- **`BranchNamingService`** / **`BranchNameTemplate`** — Generate branch names from configurable templates (`{id}`, `{type}`, `{title}` tokens). `SlugHelper.Slugify()` normalizes titles. Default template: `feature/{id}-{title}`. `BranchNamingService.Generate()` resolves the `{type}` token through a configurable type map before delegating to `BranchNameTemplate.Generate()`. `BranchNameTemplate` also provides `ExtractWorkItemId()` for parsing work item IDs from existing branch names.
- **`FlowTransitionService`** — Resolves items (by explicit ID or active context) and transitions states through `IProcessConfigurationProvider`.
- **`ActiveItemResolver`** — Resolves the active work item from context → cache → ADO auto-fetch.
- **`AutoPushNotesHelper`** — Pushes pending notes as ADO comments and clears them after state changes. Signature: `PushAndClearAsync(int workItemId, IPendingChangeStore, IAdoWorkItemService)`.
- **`IGitService`** / **`GitCliService`** — Git operations including `GetWorktreeRootAsync()`, `IsInsideWorkTreeAsync()`, branch CRUD, etc.
- **`StateCategoryResolver`** — Static class that resolves a work item state name to its `StateCategory` enum (`Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`) by matching against `IReadOnlyList<StateEntry>` from `ProcessConfiguration.TypeConfigs[type].StateEntries`. The `Resolve` method accepts `IReadOnlyList<StateEntry>?` so `null` entries trigger the hardcoded fallback.
- **`ProcessConfiguration`** — Immutable aggregate encoding ADO process configuration. `TypeConfigs` is a `IReadOnlyDictionary<WorkItemType, TypeConfig>` where each `TypeConfig` contains `StateEntries` (ordered state list with `StateCategory` metadata) and `AllowedChildTypes`.

#### The `--force` Flag

`FlowCloseCommand.ExecuteAsync()` accepts a `bool force = false` parameter. Currently (pre-change), `--force` bypasses two guards:
- **Unsaved-changes guard** (step 2) — skips the dirty-item check, allowing close-out with pending field edits.
- **Open PR guard** (step 3) — skips the active pull request check.

This plan adds a third guard that `--force` also bypasses: the child-state verification gate (Task #1622). The `--force` flag is a general "skip all guards" override, not a per-guard toggle (see DD-6 in Design Decisions).

### Call-Site Audit: AutoPushNotesHelper.PushAndClearAsync

Complete inventory of all call sites in the codebase:

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/UpdateCommand.cs` L113 | `ExecuteAsync` | Flushes notes after a field update is pushed to ADO | No change |
| `src/Twig/Commands/EditCommand.cs` L120 | `ExecuteAsync` | Flushes notes after interactive edit; wrapped in its own try/catch (failures warn, don't roll back field changes) | No change |
| `src/Twig/Commands/StateCommand.cs` L109 | `ExecuteAsync` | Flushes notes after a state transition is pushed to ADO | No change |
| `src/Twig/Commands/FlowCloseCommand.cs` | *(not called)* | No note flush before unsaved-changes guard | **Gap** — addressed by Task #1620 |

### Call-Site Audit: BranchNameTemplate.Generate / BranchNamingService.Generate

Complete inventory of all call sites for branch name generation:

| File | Method | API Used | Current Usage | Impact |
|------|--------|----------|---------------|--------|
| `src/Twig/Commands/BranchCommand.cs` L53 | `ExecuteAsync` | `BranchNamingService.Generate()` | Canonical path: resolves type via type map before generating branch name | No change — this is the correct pattern |
| `src/Twig/Commands/FlowStartCommand.cs` L233 | `ExecuteAsync` | `BranchNameTemplate.Generate()` | Direct call with `item.Type.Value` (raw ADO type name), bypassing type map resolution | **Gap** — addressed by Task #1619 |
| `src/Twig.Domain/Services/BranchNamingService.cs` L41 | `Generate` | `BranchNameTemplate.Generate()` | Internal delegation: `BranchNamingService.Generate()` resolves the type then delegates to `BranchNameTemplate.Generate()` | No change — internal implementation detail |

*Note: `BranchNameTemplate.ExtractWorkItemId()` is also referenced by `HintEngine.cs` (L226), `WorkItemIdExtractor.cs` (L26), and `TwigConfiguration.cs` (L368, via `DefaultPattern`). These are read/parse operations and are not affected by the branch name generation changes in Task #1619.*

## Problem Statement

The MCP Server epic closeout review identified five concrete gaps in the flow lifecycle commands:

1. **Branch naming inconsistency** — `FlowStartCommand` (line 233) calls `BranchNameTemplate.Generate()` directly with `item.Type.Value` (the raw ADO type name), while `BranchCommand` calls `BranchNamingService.Generate()` which resolves the type through a configurable type map first. The divergence is visible when a template includes the `{type}` token. For example, with template `{type}/{id}-{title}` and a "User Story" work item:
   - **`flow-start`** (raw path): passes `"User Story"` → `SlugHelper.Slugify("User Story")` → `user-story/1234-add-login-page`
   - **`twig branch`** (canonical path): `BranchNamingService.ResolveType("User Story")` → `"feature"` (via `DefaultTypeMap`) → `feature/1234-add-login-page`

   With the default template `feature/{id}-{title}` (no `{type}` token), both paths produce identical results because the type value is never substituted. The bug only manifests when users configure a template that includes `{type}`.

2. **No pre-close-out note sync** — `flow-close` guards against unsaved changes but doesn't flush pending notes. If a user has staged notes (via `twig note --text "..."` when offline), `flow-close` blocks with "unsaved changes" but provides no automatic resolution path. `flow-done` flushes the work tree, but `flow-close` does not.

3. **No worktree awareness** — `flow-close` calls `gitService.CheckoutAsync(defaultTarget)` + `gitService.DeleteBranchAsync(currentBranch)` without checking whether the current directory is a linked git worktree. In a worktree, `git checkout main` may fail or produce unexpected behavior, and the worktree directory itself should arguably be removed.

4. **No child-state verification** — When closing an Issue-level item, `flow-close` transitions it to Completed without verifying that all child Tasks are in a terminal state (Resolved or Completed). This allows premature closure that leaves the ADO board inconsistent.

5. **No explicit `--id` flag on `twig update`** — `UpdateCommand.ExecuteAsync()` always resolves via `activeItemResolver.GetActiveItemAsync()`, requiring `twig set <id>` before `twig update`. Other commands (`flow-done`, `flow-close`, `web`, `discard`, `save`) accept an optional `id` parameter. Adding `--id` to `update` would enable one-shot scripting: `twig update --id 1234 System.Description "..."`.

## Goals and Non-Goals

### Goals
- **G-1**: Ensure `flow-start` and `branch` produce identical branch names for the same work item and configuration.
- **G-2**: Automatically flush pending notes before close-out guards evaluate, eliminating a class of "unsaved changes" false positives.
- **G-3**: Detect linked git worktrees in `flow-close` and skip destructive branch cleanup with a clear warning.
- **G-4**: Prevent premature Issue closure by verifying all child Tasks are in a terminal state before transitioning.
- **G-5**: Enable one-shot `twig update --id <n> <field> <value>` without requiring a prior `twig set`.

### Non-Goals

The following are explicitly out of scope for this work:

- Refactoring the entire flow lifecycle into a single orchestrator.
- Adding `--id` to `note`, `state`, or `edit` commands (future work, same pattern).
- Automatic worktree removal (too destructive; warn only).
- Cross-network child verification (only check cached/fetchable children).

## Requirements

### Functional
- **FR1**: `FlowStartCommand` must use `BranchNamingService.Generate()` instead of calling `BranchNameTemplate.Generate()` directly, ensuring type map resolution is applied.
- **FR2**: `FlowCloseCommand` must flush pending notes (via `AutoPushNotesHelper.PushAndClearAsync`) before the unsaved-changes guard evaluates.
- **FR3**: `FlowCloseCommand` must detect when running in a linked git worktree (via `IGitService.GetWorktreeRootAsync()`) and skip the checkout+delete branch cleanup, emitting a warning instead.
- **FR4**: `FlowCloseCommand` must, when the target item has children, fetch child items and verify all are in a terminal `StateCategory` (Completed, Resolved, or Removed). If any are not, block closure unless `--force` is used.
- **FR5**: `UpdateCommand` must accept an optional `int? id` parameter. When provided, resolve via `ActiveItemResolver.ResolveByIdAsync(id)` instead of `GetActiveItemAsync()`.

### Non-Functional
- **NFR1**: All changes must be AOT-compatible (no reflection, source-gen JSON only).
- **NFR2**: All new code paths must have unit tests with NSubstitute mocks and Shouldly assertions.
- **NFR3**: Zero telemetry changes (no new telemetry properties).
- **NFR4**: Backward compatible — existing command signatures remain valid.

## Proposed Design

### Architecture Overview

All five tasks modify existing components — no new architectural layers are introduced. The changes affect three command files and their DI registrations:

- **`FlowStartCommand`** (Task #1619) — Single-line fix to use the canonical `BranchNamingService.Generate()` path.
- **`FlowCloseCommand`** (Tasks #1620, #1621, #1622) — Three new guard steps are inserted into the existing `ExecuteAsync()` flow: note flush (before unsaved-changes guard), worktree detection (nested inside the existing `IsInsideWorkTreeAsync()` guard in the branch cleanup section), and child-state verification (after the open PR check, step 4, and before the state transition, step 6). These require additional constructor dependencies: `IAdoWorkItemService`, `IWorkItemRepository`, and `IProcessConfigurationProvider`.
- **`UpdateCommand`** (Task #1633) — New `int? id` parameter with conditional item resolution.

The supporting infrastructure (`AutoPushNotesHelper`, `StateCategoryResolver`, `BranchNamingService`, `FlowTransitionService`) is used as-is — no modifications to shared services are required.

#### Post-Change Guard Sequence in FlowCloseCommand.ExecuteAsync

After all five tasks are implemented, `FlowCloseCommand.ExecuteAsync()` will execute the following ordered guard sequence:

1. **Resolve target** — `FlowTransitionService.ResolveItemAsync()` *(existing, unchanged)*
2. **Flush pending notes** — `AutoPushNotesHelper.PushAndClearAsync()` *(NEW — Task #1620)*
3. **Unsaved-changes guard** — Block if dirty items remain after note flush *(existing, unchanged)*
4. **Open PR guard** — Check for active pull requests on the feature branch *(existing, unchanged)*
5. **Child-state verification gate** — Verify all child Tasks are terminal before Issue closure *(NEW — Task #1622)*
6. **State transition** — `FlowTransitionService.TransitionStateAsync()` to Completed *(existing, unchanged)*
7. **Branch cleanup** — Checkout default + delete feature branch, with **worktree detection** *(MODIFIED — Task #1621 adds a worktree check inside the existing branch cleanup section)*
8. **Context cleanup** — Clear active work item context *(existing, unchanged)*

Steps 2 and 5 are the new guard insertions. Step 7 is a modification within the existing branch cleanup block. The remaining steps are untouched.

### Key Components

#### Task #1619: Branch Naming Consistency

**Problem**: `FlowStartCommand` (line 233) calls `BranchNameTemplate.Generate()` directly, passing `item.Type.Value` as the type token. This bypasses `BranchNamingService.ResolveType()` which maps ADO type names (e.g., "User Story") to branch tokens (e.g., "feature") via configurable type maps.

**Fix**: Replace the direct call with `BranchNamingService.Generate(item, config.Git.BranchTemplate, config.Git.TypeMap)`, matching what `BranchCommand` already does.

```csharp
// Before (FlowStartCommand.cs L233):
branchName = BranchNameTemplate.Generate(
    config.Git.BranchTemplate, item.Id, item.Type.Value, item.Title);

// After:
branchName = BranchNamingService.Generate(
    item, config.Git.BranchTemplate, config.Git.TypeMap);
```

This is a two-line-to-one-line replacement with high confidence — the original `BranchNameTemplate.Generate()` call spans two lines (L233–234) and is replaced by a single `BranchNamingService.Generate()` call, which is the canonical path already used by `BranchCommand`. Note: `FlowStartCommand.cs` already has `using Twig.Domain.Services;` at line 3, so no new import directive is needed.

**Implementation note**: `BranchNamingService` is a **static class** (declared `public static class BranchNamingService` at `BranchNamingService.cs` line 11). It is **not** injected via DI — `Generate()` and `ResolveType()` are static methods called directly. No constructor changes or service registration updates are needed for this task.

#### Task #1620: Pre-Close-Out Note Sync

**Problem**: `FlowCloseCommand` checks for dirty items (line 46, the `if (!force)` guard) and blocks if the target item has unsaved changes. But it doesn't distinguish between notes and field edits, and doesn't offer to flush notes automatically. Notes are additive (ADO comments) and cannot conflict — they are always safe to push.

**Fix**: Before the unsaved-changes guard, call `AutoPushNotesHelper.PushAndClearAsync()` to flush any pending notes. Wrap in a try/catch — on network failure, log a warning and continue (notes are best-effort; the unsaved-changes guard will still catch field edits). Then re-evaluate dirty state. This mirrors what `StateCommand` and `UpdateCommand` already do after their operations, but applied proactively.

```csharp
// Step 2: Flush pending notes — between resolve (step 1) and unsaved-changes guard (step 3)
try
{
    await AutoPushNotesHelper.PushAndClearAsync(targetId, pendingChangeStore, adoService);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine(fmt.FormatInfo(
        $"Could not flush pending notes for #{targetId}: {ex.Message}. Continuing with close-out."));
}
```

**Error handling rationale**: Note flush is best-effort. On network failure, the user is warned but close-out continues; the unsaved-changes guard (step 3) still blocks if field edits are pending. The exception filter uses `when (ex is not OutOfMemoryException)` — intentionally catches `OperationCanceledException` so the flow continues to subsequent guards rather than hard-aborting.

**CancellationToken note**: `AutoPushNotesHelper.PushAndClearAsync()` does not accept a `CancellationToken` parameter (its signature is `(int workItemId, IPendingChangeStore, IAdoWorkItemService)`). An `OperationCanceledException` can still propagate from inner async operations (e.g., HTTP calls within `IAdoWorkItemService.AddCommentAsync()`), but this is unlikely in practice. The broad `catch` is defensive — the primary failure mode it guards against is network connectivity errors, not cancellation.

This requires adding `IAdoWorkItemService` as a constructor dependency to `FlowCloseCommand` (currently not injected). A `using Twig.Infrastructure.Ado;` directive must also be added to `FlowCloseCommand.cs` since `AutoPushNotesHelper` lives in that namespace (following the pattern used by `UpdateCommand`, `EditCommand`, and `StateCommand`).

#### Task #1621: Worktree-Aware Close-Out

**Problem**: In a linked git worktree, `flow-close` attempts `git checkout main` + `git branch -D <branch>`. The checkout may fail because the worktree is bound to its branch, and even if it succeeds, the worktree directory becomes orphaned.

**Existing guard hierarchy**: The branch cleanup section at line 111–141 implements a tiered guard:

1. `!noBranchCleanup` — primary flag gate (line 113)
2. `gitService is not null` — null-check on optional service (line 113)
3. `await gitService.IsInsideWorkTreeAsync()` — confirms git repo (line 117)
4. `!string.Equals(currentBranch, defaultTarget, StringComparison.OrdinalIgnoreCase)` — only delete if not on default (line 124)

The worktree check inserts between guards 3 and 4 in the existing hierarchy — *after* `IsInsideWorkTreeAsync()` confirms we're in a git repo, but *before* the checkout+delete sequence. It does NOT bypass or duplicate the `noBranchCleanup` flag; when `--no-branch-cleanup` is passed, the entire block (including the worktree check) is skipped.

**Fix**: Inside the existing `IsInsideWorkTreeAsync()` guard in the branch cleanup section (FlowCloseCommand line 117), add a worktree check *before* the checkout+delete sequence. Call `gitService.GetWorktreeRootAsync()`:

- **Returns non-null** (a path string): The current directory is inside a **linked git worktree**. The returned value is the worktree's root path (the directory containing the worktree's `.git` file). In this case, `git checkout <other-branch>` would fail because a worktree is bound to its branch, and `git branch -D` would be unsafe. Skip the checkout+delete sequence and emit a warning.
- **Returns null**: The current directory is the **main working tree** (not a linked worktree). The standard checkout+delete sequence is safe to proceed.

The detection works by comparing `git rev-parse --git-common-dir` with `--git-dir` — in a linked worktree, these differ because the worktree's `.git` is a file pointing to the main repo's `.git/worktrees/<name>` directory. A secondary guard `!string.Equals(commonDir, ".git", StringComparison.Ordinal)` filters out the degenerate case where `--git-common-dir` returns the literal string `".git"` (a relative path indicating the main working tree, not a linked worktree).

**Edge case risk note**: `GetWorktreeRootAsync()` returns `null` (safe/no-op) for three atypical scenarios: (1) **bare repos** — `rev-parse --show-toplevel` throws `GitOperationException`, caught and returned as `null`; (2) **detached HEAD** — the method checks worktree linkage, not HEAD state, so detached HEAD in the main working tree correctly returns `null`; (3) **`--detach` worktrees** (`git worktree add --detach`) — these are still linked worktrees with distinct `--git-common-dir` and `--git-dir` values, so they correctly return non-null and trigger the skip-branch-cleanup path. Risk is low: the existing `GitOperationException` catch provides a safe fallback for any unexpected `git rev-parse` failure.

```csharp
// Step 7: Branch cleanup — full guard hierarchy
// Guard: !noBranchCleanup && gitService is not null (line 113)
//   Guard: await gitService.IsInsideWorkTreeAsync() (line 117)
//     NEW: linked worktree detection
//       Guard: !string.Equals(currentBranch, defaultTarget, StringComparison.OrdinalIgnoreCase) (line 124)
if (!noBranchCleanup && gitService is not null)
{
    try
    {
        var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
        if (isInWorkTree)
        {
            // NEW: check if this is a linked worktree (not the main working tree)
            var worktreeRoot = await gitService.GetWorktreeRootAsync();
            if (worktreeRoot is not null)
            {
                Console.Error.WriteLine(fmt.FormatInfo(
                    $"In a linked worktree at '{worktreeRoot}' — skipping branch cleanup. "
                    + "Remove worktree manually: git worktree remove <path>"));
            }
            else
            {
                // Existing checkout + delete logic (unchanged)
                currentBranch ??= await gitService.GetCurrentBranchAsync();
                var defaultTarget = config.Git.DefaultTarget;
                if (!string.Equals(currentBranch, defaultTarget, StringComparison.OrdinalIgnoreCase))
                {
                    // ... existing prompt + checkout + delete ...
                }
            }
        }
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Git operation failures skip branch cleanup as a safe default (existing behavior)
    }
}
```

#### Task #1622: Task-Level State Verification Gate

**Problem**: `flow-close` transitions any work item to Completed without checking children. For Issue-level items, this can leave child Tasks in non-terminal states.

**Fix**: After the open PR check (step 4) and before the state transition (step 6), fetch children and verify each child's state category. Block if any are in a non-terminal state. This placement ensures that unsaved changes and open PRs are resolved before the child verification network call, and that the verification gate has the final say before an irreversible state transition. The implementation uses inline logic (no new helper methods) for clarity.

##### Child Resolution Strategy

Try local cache first via `IWorkItemRepository.GetChildrenAsync(parentId)`, then fall back to ADO via `IAdoWorkItemService.FetchChildrenAsync(parentId)` if the cache returns empty. This avoids false negatives from a stale cache while minimizing network calls. The ADO fallback is wrapped in a try/catch — if the cache is cold *and* the user is offline, `FetchChildrenAsync()` will throw. In that case, emit an info message and suggest `--force` to skip verification rather than crashing the command.

**Known limitation — partial stale cache**: The cache-first resolution falls back to ADO only when `children.Count == 0` (cold cache). If the cache contains a *partial* set of children (e.g., 3 of 5 Tasks were cached from a previous fetch), the gate evaluates only the cached subset and does not discover the uncached children. This means a parent could pass the verification gate even though uncached children are in non-terminal states. This is an accepted trade-off: the alternative (always fetching from ADO) would make `flow-close` fail when offline even with a warm cache. The risk is mitigated by the fact that `twig refresh` and `twig tree` populate the cache with all children, and users typically run these commands during active triage before closing out.

**Exception filter note**: The ADO fallback uses `when (ex is not OutOfMemoryException and not OperationCanceledException)` — `OperationCanceledException` is excluded because cancellation must abort the verification gate immediately (unlike note flush, this gate controls whether close-out proceeds).

##### Terminal State Logic

For each child, look up its `WorkItemType` in `ProcessConfiguration.TypeConfigs` to get the `TypeConfig.StateEntries` for that type. Then call `StateCategoryResolver.Resolve(child.State, typeConfig.StateEntries)` to get the `StateCategory`. A child is terminal if its category is `Completed`, `Resolved`, or `Removed`. Children whose type is not found in `TypeConfigs` are treated as **non-terminal** (conservative) to surface unmapped types rather than silently ignoring them.

##### Error Path

When incomplete children are found, the command returns exit 1 with a per-child listing showing ID, title, and current state. The `--force` flag bypasses the entire gate and emits an info message for audit trail consistency.

The following code block implements the complete child verification gate:

```csharp
// Step 5: child state verification (after open PR check at step 4, before state transition at step 6):
if (!force)
{
    // Cache-first child resolution with ADO fallback
    var children = await workItemRepo.GetChildrenAsync(targetId, ct);
    var resolvedFromCache = children.Count > 0;
    if (!resolvedFromCache)
    {
        try
        {
            children = await adoService.FetchChildrenAsync(targetId, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Offline or network error with cold cache — cannot verify children
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Could not fetch children for #{targetId}: {ex.Message}. "
                + "Use --force to skip child verification."));
            return 1;
        }
    }

    if (children.Count > 0)
    {
        var processConfig = processConfigProvider.GetConfiguration();
        var incomplete = new List<WorkItem>();
        foreach (var child in children)
        {
            if (processConfig.TypeConfigs.TryGetValue(child.Type, out var childTypeConfig))
            {
                var category = StateCategoryResolver.Resolve(child.State, childTypeConfig.StateEntries);
                if (category is not (StateCategory.Completed or StateCategory.Resolved or StateCategory.Removed))
                    incomplete.Add(child);
            }
            else
            {
                // Unknown/unmapped type — treat as non-terminal (conservative)
                incomplete.Add(child);
            }
        }

        if (incomplete.Count > 0)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Cannot close #{targetId}: {incomplete.Count} child item(s) not in terminal state."));
            foreach (var c in incomplete)
                Console.Error.WriteLine(fmt.FormatInfo($"  #{c.Id} {c.Title} [{c.State}]"));
            return 1;
        }
    }
}
else
{
    // --force: log info for audit trail consistency
    Console.Error.WriteLine(fmt.FormatInfo(
        $"Skipping child state verification for #{targetId} (--force)."));
}
```

This requires adding `IWorkItemRepository`, `IAdoWorkItemService`, and `IProcessConfigurationProvider` as constructor dependencies to `FlowCloseCommand`. Note: `IAdoWorkItemService` is already being added for Task #1620 (note flush), so only `IWorkItemRepository` and `IProcessConfigurationProvider` are incremental dependencies for this task. Additionally, a `using Twig.Domain.Aggregates;` directive must be added to `FlowCloseCommand.cs` — the child verification code explicitly references the `WorkItem` type (in `new List<WorkItem>()` and the `foreach` iteration variable), and the existing using directives do not include this namespace.

**⚠️ Test breakage note**: Adding three new constructor parameters (`IAdoWorkItemService` from Task #1620, `IWorkItemRepository` and `IProcessConfigurationProvider` from this task) will break every existing test in `FlowCloseCommandTests.cs`. The current `CreateCommand()` test helper (line 59–61) instantiates `FlowCloseCommand` with 8 arguments and must be updated to pass 11. All 14 existing test methods use this helper. This is a mechanical update — the new parameters should have `NSubstitute.Substitute.For<T>()` defaults in the helper — but it must be planned as part of the implementation. See the test fixture discussion below.

`FlowTransitionService` already has `IAdoWorkItemService` and `IProcessConfigurationProvider` injected, but the child verification logic lives in the command (not the transition service) because it is a command-level guard, not a state transition concern. Specifically: `FlowTransitionService.TransitionStateAsync()` is a reusable primitive shared by `flow-done` and `flow-close` — it handles item resolution and state transitions. Child verification is a **pre-condition gate** specific to `flow-close` (it blocks closure, not transitions in general). Placing it in `FlowTransitionService` would either (a) force `flow-done` to also check children (undesirable — completing a Task shouldn't require verifying sibling Tasks), or (b) require a boolean flag to conditionally skip the check, which is a code smell. The command owns its guard sequence; the transition service owns state machine logic.

#### Task #1633: Explicit --id Flag on twig update

**Problem**: `UpdateCommand` always resolves via `activeItemResolver.GetActiveItemAsync()`, requiring a prior `twig set <id>`.

**Fix**: Add `int? id = null` parameter to `ExecuteAsync()`. When provided, resolve via `activeItemResolver.ResolveByIdAsync(id.Value)` instead of `GetActiveItemAsync()`. Thread through from `TwigCommands.Update()` in Program.cs.

**DI note**: `UpdateCommand` is registered via auto-resolution (`AddSingleton<UpdateCommand>()` at `CommandRegistrationModule.cs` line 64), unlike `FlowCloseCommand` which uses an explicit factory lambda. Since the new `--id` parameter is a command argument (not a constructor dependency), **no DI registration changes are needed** for this task.

```csharp
// UpdateCommand.ExecuteAsync signature change
// (outputFormat, format, filePath, readStdin omitted for brevity — unchanged from current signature):
public async Task<int> ExecuteAsync(
    string field, string? value = null,
    string outputFormat = OutputFormatterFactory.DefaultFormat, string? format = null,
    string? filePath = null, bool readStdin = false,
    int? id = null,  // NEW
    CancellationToken ct = default)

// Resolution logic change:
var resolved = id.HasValue
    ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
    : await activeItemResolver.GetActiveItemAsync(ct);
```

Program.cs routing (full signature and call chain):
```csharp
// Program.cs routing — output/format/file/stdin omitted for brevity — unchanged from current signature:
/// <param name="id">Target a specific work item by ID instead of the active context.</param>
public async Task<int> Update(
    [Argument] string field, [Argument] string? value = null,
    string output = OutputFormatterFactory.DefaultFormat, string? format = null,
    string? file = null, bool stdin = false,
    int? id = null,  // NEW
    CancellationToken ct = default)
    => await services.GetRequiredService<UpdateCommand>()
        .ExecuteAsync(field, value, output, format, file, stdin, id, ct);
```

### Exception Handling Policy

**Design principle**: The asymmetry between #1620 and #1622 is intentional. Note flush (#1620) is **additive and idempotent** — `OperationCanceledException` is caught and flow continues, since notes remain in the pending store and the unsaved-changes guard still fires. Child verification (#1622) is a **one-way gate** — `OperationCanceledException` is not caught and aborts the command, because a state transition to Completed is irreversible. Worktree detection (#1621) uses the existing branch-cleanup `catch` block unchanged.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **DD-1**: Flush notes proactively in `flow-close` rather than adding a `--sync` flag | Notes are always safe to push (additive). Forcing users to remember `twig sync` before `flow-close` is error-prone. |
| **DD-2**: Skip worktree branch cleanup with warning rather than attempting `git worktree remove` | `git worktree remove` is destructive and may delete uncommitted files. Warning is the safe default; `--force` could be extended later. |
| **DD-3**: Cache-first child resolution with ADO fallback | Avoids network calls when cache is warm (common case), but doesn't miss children when cache is cold. |
| **DD-4**: `--id` is a named flag, not a positional argument | `twig update <field> <value>` already uses two positional `[Argument]` parameters. Adding a third positional `id` would be ambiguous (is "System.Title" a field name or an id?). `--id` is explicit and avoids positional ambiguity. Note: `flow-done` and `flow-close` use `[Argument] int? id` (positional), which works because they have only one positional parameter. `update` has two, so `--id` (named) is the correct choice. |
| **DD-5**: Terminal states = Completed ∪ Resolved ∪ Removed | Resolved and Removed items are finished work. Only Proposed and InProgress indicate unfinished work that should block parent closure. |
| **DD-6**: `--force` is a general "skip all guards" flag, not a per-guard toggle | `--force` already bypasses two guards: unsaved changes (step 3) and open PR check (step 4). Adding child verification as a third guard that `--force` also bypasses is consistent — it is a deliberate "I know what I'm doing" override. The `--force` bypass emits a warning for audit trail visibility. |

## Alternatives Considered

| Decision | Alternative | Why Rejected |
|----------|-------------|--------------|
| **DD-2**: Warn-only for worktree branch cleanup | **Attempt `git worktree remove`** — Automatically remove the linked worktree directory and clean up the branch. | `git worktree remove` deletes the worktree directory including any uncommitted or untracked files. This is too destructive for a default behavior — data loss risk outweighs convenience. The warn-and-skip approach is safe by default; users can opt in to manual removal. A future `--remove-worktree` flag could be added without breaking backward compatibility. |
| **DD-2**: Warn-only for worktree branch cleanup | **Fail with error** — Return exit code 1 when a worktree is detected, requiring `--force` to proceed. | Overly restrictive. The worktree only affects *branch cleanup*, not the state transition or context clearing. Blocking the entire close-out because of a branch cleanup edge case would frustrate users. Warning and skipping just the branch cleanup step is more granular and less disruptive. |
| **DD-3**: Cache-first child resolution with ADO fallback | **Always fetch from ADO** — Skip the cache entirely and always call `FetchChildrenAsync()` for authoritative child state. | Guarantees correctness (no stale-cache risk) but makes `flow-close` fail when offline, even if the user has a warm cache from a recent `twig refresh`. This penalizes the common case (online + warm cache produces identical results) to guard against an uncommon case (partial stale cache). The cache-first approach with ADO fallback on cold cache is the better trade-off for a CLI that needs to work in intermittent-connectivity scenarios. |
| **DD-3**: Cache-first child resolution with ADO fallback | **Opt-in verification via `--verify-children` flag** — Only check child states when the user explicitly requests it. | Defeats the purpose: the gap exists because users forget to check children before closing. An opt-in flag would rarely be used, and the default (no check) would perpetuate premature closures. The gate should be on by default with `--force` as the escape hatch, not off by default with an opt-in flag. |
| **DD-4**: `--id` as a named flag | **Third positional `[Argument]`** — `twig update <field> <value> <id>`. | Ambiguous: `twig update System.Title "New title" 1234` is parseable, but `twig update System.Title` (without value) makes `1234` look like the value, not the id. ConsoleAppFramework's positional parsing would bind arguments left-to-right, making `id` indistinguishable from `value` when `value` is omitted. The `--id` flag is unambiguous regardless of which optional positional args are provided. |
| **DD-4**: `--id` as a named flag | **Subcommand variant** — `twig update by-id 1234 System.Title "New title"`. | Overengineered. This introduces a new subcommand surface, documentation, and test matrix for a feature that is a simple conditional on item resolution. The `--id` flag adds the capability without expanding the command tree or breaking existing CLI muscle memory. |

## Dependencies

### Internal
- Tasks #1620, #1621, and #1622 all modify `FlowCloseCommand.cs`: #1620 adds the note flush step and `IAdoWorkItemService` dependency, #1621 adds the worktree check inside the branch cleanup section, and #1622 adds the child verification gate and two more constructor dependencies (`IWorkItemRepository`, `IProcessConfigurationProvider`). They should be implemented in sequence or carefully merged.
- Task #1619 is fully independent (only touches `FlowStartCommand.cs`).
- Task #1633 is fully independent (only touches `UpdateCommand.cs` and `Program.cs`).
- **DI factory growth note**: The `FlowCloseCommand` DI factory lambda in `CommandRegistrationModule.cs` (lines 172–181) currently resolves 9 constructor parameters. After Tasks #1620 and #1622, it will grow to 12 parameters (`+IAdoWorkItemService`, `+IWorkItemRepository`, `+IProcessConfigurationProvider`). This is functional but approaching the threshold for a builder/options-pattern refactor. See Risks and Mitigations for details.
- **Test fixture update note**: The `FlowCloseCommandTests.CreateCommand()` helper (line 59–61) currently passes 8 positional arguments. After Tasks #1620–#1622, it must pass 11. Expand it with three named optional parameters (`IAdoWorkItemService?`, `IWorkItemRepository?`, `IProcessConfigurationProvider?`) defaulting to `Substitute.For<T>()`. This preserves all 14 existing call sites unchanged while letting new tests inject specific mocks.

### Sequencing
- No hard ordering constraints between tasks, but #1620, #1621, and #1622 share the same file (`FlowCloseCommand.cs`) and should be in the same PR group to avoid merge conflicts.

## Impact Analysis

### Components Affected
- **FlowStartCommand** — branch generation call (Task #1619)
- **FlowCloseCommand** — note sync, worktree check, child verification (Tasks #1620, #1621, #1622)
- **UpdateCommand** — new `--id` parameter (Task #1633)
- **Program.cs (TwigCommands)** — `Update()` routing signature (Task #1633)
- **CommandRegistrationModule** — `FlowCloseCommand` constructor args (Tasks #1620, #1622)

### Backward Compatibility
All changes are backward compatible. Existing command signatures remain valid — new parameters are optional with defaults matching current behavior (`id = null`, `force = false`).

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| **Stale partial cache passes child verification gate** — Cache-first resolution falls back to ADO only when `children.Count == 0`. If the cache holds a partial subset of children, uncached children in non-terminal states are not evaluated. | Low | Medium | Accepted trade-off: the alternative (always fetching from ADO) would make `flow-close` fail offline even with a warm cache. In practice, `twig refresh` and `twig tree` populate full child sets. |
| **DI factory lambda growth for FlowCloseCommand** — The `FlowCloseCommand` constructor currently has 9 parameters. After Tasks #1620 and #1622, it will grow to 12 parameters (adding `IAdoWorkItemService`, `IWorkItemRepository`, `IProcessConfigurationProvider`), approaching the threshold where a builder or options-pattern refactor is warranted. | Medium | Low | The 12-parameter constructor is functional and all parameters are genuinely needed. A builder/options refactor would be premature for this plan — it would expand scope and introduce risk for a cosmetic improvement. Flagged as future technical debt; if `FlowCloseCommand` gains further dependencies, refactoring to a `FlowCloseOptions` record should be prioritized. |
| **Worktree detection false negative on exotic git configurations** — `GetWorktreeRootAsync()` compares `--git-common-dir` and `--git-dir`; unusual git layouts (submodules within worktrees, nested repos) might confuse the heuristic. | Low | Low | The existing `GitOperationException` catch provides a safe fallback (returns `null`, proceeds with standard cleanup). The method has been stable since its introduction and covers the documented worktree scenarios. |
| **Offline with cold cache blocks close-out** — If the child cache is empty *and* the user is offline, `FetchChildrenAsync()` throws and the verification gate returns exit 1, blocking close-out. | Low | Medium | The error message explicitly suggests `--force` to skip verification. This is the correct behavior: closing an Issue with unverified children is riskier than asking the user to explicitly override. |

## Open Questions

None.

## Files Affected

### New Files

None.

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/FlowStartCommand.cs` | Replace `BranchNameTemplate.Generate()` with `BranchNamingService.Generate()` (L233) |
| `src/Twig/Commands/FlowCloseCommand.cs` | Add note flush step, worktree detection, child state verification gate; add new constructor deps |
| `src/Twig/Commands/UpdateCommand.cs` | Add `int? id` parameter, conditional resolution path |
| `src/Twig/Program.cs` | Add `int? id` parameter to `TwigCommands.Update()` routing |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Update `FlowCloseCommand` DI factory with new dependencies (note: `UpdateCommand` uses auto-resolution — no changes needed) |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Add test verifying `BranchNamingService` type map is used |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Update `CreateCommand()` helper with 3 new optional parameters; add tests for note flush, worktree skip, child verification gate (including cache observability message, offline fallback, and --force warning) |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Add tests for `--id` parameter: with id resolves by ID, without id uses active context, error when id not found |

### Deleted Files

None.

## ADO Work Item Structure

### Issue #1618: MCP Server Closeout Findings

**Goal**: Address five gaps found during MCP Server epic closeout review.
**Prerequisites**: None.

#### Tasks

| Task ID | Description | Traces To | Files | Effort | Status |
|---------|-------------|-----------|-------|--------|--------|
| #1619 | Enforce branch naming consistency in FlowStartCommand | FR1, G-1 | `FlowStartCommand.cs`, `FlowStartCommandTests.cs` | ~30 LoC | TO DO |
| #1620 | Add pre-close-out sync step for pending notes | FR2, G-2 | `FlowCloseCommand.cs`, `CommandRegistrationModule.cs`, `FlowCloseCommandTests.cs` | ~70 LoC | TO DO |
| #1621 | Add worktree-aware close-out flow | FR3, G-3 | `FlowCloseCommand.cs`, `FlowCloseCommandTests.cs` | ~40 LoC | TO DO |
| #1622 | Add task-level state verification gate before Issue closure | FR4, G-4 | `FlowCloseCommand.cs`, `CommandRegistrationModule.cs`, `FlowCloseCommandTests.cs` | ~130 LoC | TO DO |
| #1633 | Add explicit work-item ID flag to twig update command | FR5, G-5 | `UpdateCommand.cs`, `Program.cs`, `UpdateCommandTests.cs` | ~50 LoC | TO DO |

#### Acceptance Criteria
- [ ] `twig flow-start <id>` generates the same branch name as `twig branch` for the same work item
- [ ] `twig flow-close` flushes pending notes before evaluating unsaved-changes guard
- [ ] `twig flow-close` warns but continues if note flush fails (network error)
- [ ] `twig flow-close` skips branch cleanup in a linked worktree with a warning
- [ ] `twig flow-close` worktree check respects `--no-branch-cleanup` (skipped entirely when flag is set)
- [ ] `twig flow-close` on an Issue with incomplete child Tasks returns exit 1 (unless `--force`)
- [ ] `twig flow-close` with `--force` emits a warning about skipping child verification
- [ ] `twig flow-close` on an Issue when offline with cold cache returns exit 1 with `--force` suggestion
- [ ] `twig update --id 1234 System.Title "New title"` updates work item #1234 without changing active context
- [ ] All new code paths have unit tests
- [ ] `dotnet build` succeeds with zero warnings
- [ ] `dotnet test` passes

## PR Groups

### PG-1: Branch naming fix + Update --id flag (independent changes)
**Type**: Wide (multiple files, mechanical changes)
**Tasks**: #1619, #1633
**Estimated LoC**: ~80
**Files**: `FlowStartCommand.cs`, `FlowStartCommandTests.cs`, `UpdateCommand.cs`, `Program.cs`, `UpdateCommandTests.cs`
**Rationale**: These two tasks are fully independent of each other and of the FlowClose changes. Grouping them avoids an undersized PR while keeping review scope manageable. Neither touches `FlowCloseCommand.cs`.
**Successors**: None (can merge independently)

### PG-2: FlowClose hardening (note sync + worktree + child verification)
**Type**: Deep (few files, complex logic changes)
**Tasks**: #1620, #1621, #1622
**Estimated LoC**: ~240
**Files**: `FlowCloseCommand.cs`, `CommandRegistrationModule.cs`, `FlowCloseCommandTests.cs`
**Rationale**: All three tasks modify `FlowCloseCommand` — its constructor, its `ExecuteAsync()` method, and its DI registration. #1621 modifies the branch cleanup section specifically. Implementing them in a single PR avoids repeated merge conflicts and allows the reviewer to see the complete close-out guard sequence in context.
**Recommended implementation order**: #1620 (note flush) → #1621 (worktree detection) → #1622 (child verification). This sequence minimizes merge friction: #1620 adds the first new constructor dependency (`IAdoWorkItemService`) and establishes the DI factory expansion pattern; #1621 adds logic within the existing branch cleanup block (no new constructor deps); #1622 adds the remaining two constructor dependencies and the most complex guard logic, building on the DI pattern established by #1620.

**Successors**: None (can merge independently of PG-1)

---

*End of plan document — Rev 9*
