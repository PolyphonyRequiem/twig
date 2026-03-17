# Twig Flow Lifecycle — Solution Design & Implementation Plan

> **Revision notes:** Initial draft.

---

## Executive Summary

This document proposes the `twig flow` command group — a prescriptive, opinionated developer inner loop lifecycle for the Twig CLI. It composes three new orchestrating commands (`flow start`, `flow done`, `flow close`) atop existing primitives (`set`, `state`, `save`, `refresh`), git integration (from `twig-git-integration.plan.md`), and new infrastructure services (SyncGuard, ADO Git PR API). The flow codifies the developer lifecycle — **start** → work → **done** → review → **close** — with automatic state transitions, self-assignment, git branch management, sync safety, save scoping, and PR creation. The design adds ~19 new files, modifies ~9 existing files, and is structured across 6 implementation epics. The `twig-git-integration.plan.md` EPIC-001 (`IGitService`/`GitCliService`) is a prerequisite — this plan assumes that foundation exists.

---

## Background

### Current State

Twig is a .NET 9 Native AOT CLI built on ConsoleAppFramework v5.7.13 that manages Azure DevOps work items through a local SQLite cache. The current architecture:

- **CLI layer (`Twig`):** 19 command classes (`SetCommand`, `StateCommand`, `SaveCommand`, `RefreshCommand`, etc.), each with `ExecuteAsync` returning `Task<int>`. Output flows through `IOutputFormatter` (human/json/minimal). Commands are composed in `Program.cs` via DI and routed through a `TwigCommands` facade class.
- **Domain layer (`Twig.Domain`):** `WorkItem` aggregate with command-queue pattern, value objects (`StateShorthand`, `StateEntry`, `FieldChange`), services (`StateCategoryResolver`, `StateTransitionService`, `ConflictResolver`), and interface contracts (`IAdoWorkItemService`, `IWorkItemRepository`, `IContextStore`, `IPendingChangeStore`).
- **Infrastructure layer (`Twig.Infrastructure`):** `AdoRestClient` (ADO REST API v7.1), SQLite persistence (`SqliteWorkItemRepository`, `SqliteContextStore`, `SqlitePendingChangeStore`), configuration (`TwigConfiguration`), and authentication (`PatAuthProvider`, `AzCliAuthProvider`).

Key existing components that flow commands will compose:

| Component | Location | Used by flow |
|---|---|---|
| `SetCommand` logic (resolve item, set context) | `src/Twig/Commands/SetCommand.cs` | `flow start` |
| `StateCommand` logic (resolve shorthand, transition) | `src/Twig/Commands/StateCommand.cs` | `flow start`, `flow done`, `flow close` |
| `SaveCommand` logic (push pending changes) | `src/Twig/Commands/SaveCommand.cs` | `flow done` |
| `ConflictResolutionFlow` (detect/resolve conflicts) | `src/Twig/Commands/ConflictResolutionFlow.cs` | `flow start`, `flow done` |
| `StateCategoryResolver` (map state → category) | `src/Twig.Domain/Services/StateCategoryResolver.cs` | All flow commands |
| `StateShorthand.Resolve` (shorthand → state name) | `src/Twig.Domain/ValueObjects/StateShorthand.cs` | All flow commands |
| `IContextStore` (active work item context) | `src/Twig.Domain/Interfaces/IContextStore.cs` | All flow commands |
| `IPendingChangeStore` (dirty tracking) | `src/Twig.Domain/Interfaces/IPendingChangeStore.cs` | `flow done`, `flow close`, SyncGuard |
| `IIterationService` (current sprint, user identity) | `src/Twig.Domain/Interfaces/IIterationService.cs` | `flow start` (picker, assignment) |
| `OutputFormatterFactory` (human/json/minimal) | `src/Twig/Formatters/OutputFormatterFactory.cs` | All flow commands |

### Context and Motivation

Currently, the developer workflow is fragmented: a developer runs `twig set 12345` to set context, `twig state c` to transition to Active, manually creates a git branch, codes, then manually runs `twig save`, `twig state s` to resolve, creates a PR in the ADO web UI, and later runs `twig state d` and cleans up the branch. Each step requires knowing the right command and correct shorthand. The `twig flow` commands collapse this into 3 intentional commands with safety rails.

### Prior Art in Codebase

- **`SetCommand`** already handles item resolution (numeric ID, fuzzy pattern match, interactive disambiguation via Spectre.Console).
- **`StateCommand`** already handles shorthand resolution (`p`/`c`/`s`/`d`/`x`), transition validation, confirmation prompts, conflict detection, and auto-push of pending notes.
- **`SaveCommand`** already pushes all pending changes (fields + notes) to ADO with conflict resolution, but currently scopes to *all dirty items* — not the active work tree.
- **`RefreshCommand`** already fetches sprint items and refreshes cache, but has no dirty guard protection.
- **`twig-git-integration.plan.md`** defines `IGitService` and `GitCliService` for git operations.

---

## Problem Statement

1. **Fragmented developer workflow:** Starting work on an item requires 3–4 separate commands (`set`, `state`, manually create branch, optionally assign). Finishing requires another 3–4 (`save`, `state`, create PR, clean up). This is error-prone and slow.

2. **No save scoping:** `twig save` currently pushes *all dirty items*. When running `flow done` on a specific item, only that item's work tree (itself + dirty children) should be saved. Similarly, `twig save <id>` for a single item and `twig save --all` for everything are not yet supported.

3. **No refresh safety:** `twig refresh` blindly overwrites the local cache with remote data, even if local items have unsaved changes. This can silently discard work.

4. **No context clearing:** `IContextStore.SetActiveWorkItemIdAsync(int id)` takes a non-nullable `int` — there's no API to clear the active context (needed by `flow close`).

5. **No PR integration:** No ADO Git API support exists for querying or creating pull requests. `flow done` needs to offer PR creation; `flow close` needs to guard against open PRs.

6. **No self-assignment:** No command currently assigns a work item to the authenticated user. `flow start` needs this.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|---|---|---|
| G-1 | Single-command lifecycle transitions | `flow start` replaces 3–4 manual steps; `flow done` replaces 2–3; `flow close` replaces 3–4 |
| G-2 | Safe sync model | SyncGuard prevents refresh from overwriting dirty items; save scoping limits blast radius |
| G-3 | Git-aware flow | Branch creation/checkout on start, branch detection, branch cleanup on close |
| G-4 | Idempotent operations | Running `flow start` on an already-active item is safe; re-running `flow done` on a resolved item skips state transition |
| G-5 | Non-interactive support | `--format json` and non-TTY environments work without prompts |
| G-6 | Configurable behavior | Branch templates, auto-assign policy, auto-save, PR offer — all configurable |

### Non-Goals

| ID | Non-Goal | Rationale |
|---|---|---|
| NG-1 | Implicit save/transition on old item during context switch | Dangerous side effect — scenario doc explicitly excludes this |
| NG-2 | Remote branch deletion on `flow close` | Higher blast radius — only local branch deletion |
| NG-3 | Full PR management (update, merge, abandon) | Out of scope — `flow close` only guards against open PRs |
| NG-4 | Automatic refresh on `flow start` | Explicit `twig refresh` preferred; flow only fetches the target item |
| NG-5 | Multi-item flow operations | Each flow command operates on one item at a time |

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|---|---|---|
| FR-001 | `twig flow start [id-or-pattern]` resolves item, sets context, transitions Proposed→InProgress, assigns to self, creates/checks out git branch | High |
| FR-002 | `twig flow start` (no args) shows interactive picker of unstarted sprint items | High |
| FR-003 | `twig flow done [id]` saves active work tree, transitions InProgress→Resolved, offers PR creation | High |
| FR-004 | `twig flow close [id]` guards against unsaved changes/open PRs, transitions to Completed, deletes branch, clears context | High |
| FR-005 | `SaveCommand` supports scoped save: active work tree, single item, or all dirty | High |
| FR-006 | `SyncGuard` service identifies protected items (dirty or pending changes) | High |
| FR-007 | `RefreshCommand` halts when remote updates conflict with protected items | High |
| FR-008 | `IContextStore` supports clearing active context | High |
| FR-009 | `IAdoGitService` supports PR query and creation | High |
| FR-010 | Flow commands support `--no-branch`, `--no-state`, `--no-assign`, `--take`, `--force`, `--no-save`, `--no-pr` flags | Medium |
| FR-011 | Flow commands output human/json/minimal formats with structured action results | Medium |
| FR-012 | Exit codes: 0 (success), 1 (error), 2 (guarded — open PR, dirty items) | Medium |
| FR-013 | Git branch template configurable via `git.branchTemplate` with `{id}`, `{type}`, `{title}` tokens | Medium |
| FR-014 | Title slugification follows deterministic rules (lowercase, strip, collapse, truncate 50 chars) | Medium |
| FR-015 | `flow start` context switch emits a hint about previous item | Low |

### Non-Functional Requirements

| ID | Requirement | Metric | Rationale |
|---|---|---|---|
| NFR-001 | Native AOT compatibility | Zero reflection, source-generated JSON | Project constraint |
| NFR-002 | Testability | All flow orchestration mockable via DI interfaces | Existing pattern |
| NFR-003 | Composability | Flow commands delegate to primitives, never bypass them | Scenario doc principle |
| NFR-004 | Idempotency | Re-running flow commands on already-transitioned items is safe | Developer UX |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        CLI Layer (Twig)                       │
│                                                               │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐       │
│  │ FlowStart    │  │ FlowDone     │  │ FlowClose     │       │
│  │ Command      │  │ Command      │  │ Command       │       │
│  └──────┬───────┘  └──────┬───────┘  └───────┬───────┘       │
│         │                 │                   │               │
│  ┌──────┴─────────────────┴───────────────────┴──────────┐   │
│  │                  FlowOrchestrator                      │   │
│  │  (composes primitives: set, state, save, git, assign)  │   │
│  └────────┬──────────┬──────────┬──────────┬─────────────┘   │
│           │          │          │          │                   │
│  ┌────────┴──┐ ┌─────┴────┐ ┌──┴─────┐ ┌─┴──────────┐       │
│  │  SaveScope│ │SyncGuard │ │BranchNm│ │ SlugHelper │       │
│  │  Helper   │ │          │ │Template│ │            │       │
│  └───────────┘ └──────────┘ └────────┘ └────────────┘       │
│                                                               │
├───────────────────────────────────────────────────────────────┤
│                     Domain Layer (Twig.Domain)                │
│                                                               │
│  IContextStore ← ClearActiveWorkItemIdAsync()  [NEW]         │
│  IGitService   ← (from git-integration plan)                 │
│  IAdoGitService← GetPullRequestsForBranchAsync, CreatePR     │
│  SyncGuard     ← GetProtectedItemIdsAsync()    [NEW]         │
│  BranchNameTemplate ← Generate, Parse          [NEW]         │
│                                                               │
├───────────────────────────────────────────────────────────────┤
│                Infrastructure Layer (Twig.Infrastructure)     │
│                                                               │
│  SqliteContextStore ← ClearActiveWorkItemIdAsync() impl      │
│  GitCliService      ← (from git-integration plan)            │
│  AdoGitClient       ← PR query/create impl     [NEW]         │
│  TwigConfiguration  ← git.*, flow.* sections   [EXTENDED]    │
└───────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `FlowStartCommand` (CLI)

**File:** `src/Twig/Commands/FlowStartCommand.cs`

**Responsibilities:**
- Resolve target item (by ID, pattern, or interactive picker)
- Set active context
- Transition state (Proposed → InProgress)
- Assign to self (if unassigned)
- Create/checkout git branch
- Print structured summary

**Dependencies:** `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IIterationService`, `IGitService`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`, `RenderingPipelineFactory?`

**Key logic:**
```
1. Resolve item → SetCommand-style resolution
2. Fetch latest from ADO → conflict check → ConflictResolutionFlow
3. contextStore.SetActiveWorkItemIdAsync(id)
4. Resolve state category → if Proposed, transition to InProgress via StateShorthand.Resolve('c')
5. If unassigned (or --take), assign to authenticated user via PatchAsync
6. If IGitService.IsInsideWorkTreeAsync(), create/checkout branch from template
7. Print summary
```

#### 2. `FlowDoneCommand` (CLI)

**File:** `src/Twig/Commands/FlowDoneCommand.cs`

**Responsibilities:**
- Resolve target (active context or explicit ID)
- Save active work tree (or single item if explicit ID)
- Transition state (InProgress → Resolved, or fallback to Completed)
- Offer PR creation if branch is ahead of target

**Dependencies:** `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IGitService`, `IAdoGitService`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`

#### 3. `FlowCloseCommand` (CLI)

**File:** `src/Twig/Commands/FlowCloseCommand.cs`

**Responsibilities:**
- Resolve target
- Guard: refuse if unsaved changes exist
- Guard: warn if open PR exists
- Transition state to Completed
- Delete local branch (prompt)
- Clear active context

**Dependencies:** `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IGitService`, `IAdoGitService`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`

#### 4. `SyncGuard` (Domain Service)

**File:** `src/Twig.Domain/Services/SyncGuard.cs`

Single point of truth for "which items are protected from overwrite." An item is protected if it has `is_dirty=1` or has rows in `pending_changes`.

```csharp
public static class SyncGuard
{
    public static async Task<IReadOnlySet<int>> GetProtectedItemIdsAsync(
        IWorkItemRepository repo, IPendingChangeStore pendingStore, CancellationToken ct = default)
    {
        var dirtyItems = await repo.GetDirtyItemsAsync(ct);
        var pendingIds = await pendingStore.GetDirtyItemIdsAsync(ct);
        var result = new HashSet<int>();
        foreach (var item in dirtyItems) result.Add(item.Id);
        foreach (var id in pendingIds) result.Add(id);
        return result;
    }
}
```

#### 5. `BranchNameTemplate` (Domain Value Object)

**File:** `src/Twig.Domain/ValueObjects/BranchNameTemplate.cs`

Generates branch names from configurable templates with token substitution and title slugification.

```csharp
public static class BranchNameTemplate
{
    public static string Generate(string template, int id, string type, string title) { ... }
    public static int? ExtractWorkItemId(string branchName, string pattern) { ... }
}
```

**Slugification rules (from scenario doc):**
- Lowercase
- Spaces and underscores → hyphens
- Strip non-alphanumeric (except hyphens)
- Collapse consecutive hyphens
- Truncate to 50 characters
- Trim trailing hyphens

#### 6. `IContextStore` Extension

**File:** `src/Twig.Domain/Interfaces/IContextStore.cs` (modified)

Add `ClearActiveWorkItemIdAsync()` to support `flow close` clearing context:

```csharp
Task ClearActiveWorkItemIdAsync(CancellationToken ct = default);
```

#### 7. `SaveCommand` Scoping Extension

**File:** `src/Twig/Commands/SaveCommand.cs` (modified)

`ExecuteAsync` gains optional parameters for scoped save:

```csharp
public async Task<int> ExecuteAsync(
    int? targetId = null,        // null = active work tree; non-null = single item
    bool all = false,            // --all = all dirty items (current behavior)
    string outputFormat = "human")
```

Behavior matrix:
- `twig save` → active item + dirty children
- `twig save <id>` → single item
- `twig save --all` → all dirty items (current behavior)

#### 8. Configuration Extensions

**File:** `src/Twig.Infrastructure/Config/TwigConfiguration.cs` (modified)

Add `GitConfig` and `FlowConfig` sub-objects:

```csharp
public GitConfig Git { get; set; } = new();
public FlowConfig Flow { get; set; } = new();

public sealed class GitConfig
{
    public string BranchTemplate { get; set; } = "feature/{id}-{title}";
    public string BranchPattern { get; set; } = @"(?:^|/)(?<id>\d{3,})(?:-|/|$)";
    public string DefaultTarget { get; set; } = "main";
}

public sealed class FlowConfig
{
    public string AutoAssign { get; set; } = "if-unassigned";
    public bool AutoSaveOnDone { get; set; } = true;
    public bool OfferPrOnDone { get; set; } = true;
}
```

### Data Flow

#### `twig flow start 12345`

```
User → FlowStartCommand.ExecuteAsync("12345")
  1. int.TryParse("12345") → id = 12345
  2. workItemRepo.GetByIdAsync(12345) → cache hit or adoService.FetchAsync(12345)
  3. adoService.FetchAsync(12345) → remote (latest revision)
  4. ConflictResolutionFlow.ResolveAsync(local, remote, ...) → Proceed
  5. contextStore.SetActiveWorkItemIdAsync(12345)
  6. processConfigProvider.GetConfiguration() → typeConfig.StateEntries
  7. StateCategoryResolver.Resolve(item.State, entries) → Proposed
  8. StateShorthand.Resolve('c', entries) → "Active"
  9. adoService.PatchAsync(12345, [State: "Active"], revision) → newRev
  10. config.User.DisplayName → "Dan Green"
  11. item.AssignedTo == null → adoService.PatchAsync(12345, [AssignedTo: "Dan Green"], newRev)
  12. gitService.IsInsideWorkTreeAsync() → true
  13. BranchNameTemplate.Generate("feature/{id}-{title}", 12345, "Bug", "Login timeout...") → "feature/12345-login-timeout"
  14. gitService.CreateBranchAsync("feature/12345-login-timeout")
  15. gitService.CheckoutAsync("feature/12345-login-timeout")
  16. Print summary → exit 0
```

#### `twig flow done`

```
User → FlowDoneCommand.ExecuteAsync()
  1. contextStore.GetActiveWorkItemIdAsync() → 12345
  2. workItemRepo.GetByIdAsync(12345) → item
  3. [Save active work tree]
     a. workItemRepo.GetChildrenAsync(12345) → children
     b. For item + dirty children: FetchAsync → ConflictResolutionFlow → PatchAsync → ClearChangesAsync
  4. StateCategoryResolver.Resolve(item.State, entries) → InProgress
  5. StateShorthand.Resolve('s', entries) → "Resolved" (or 'd' → "Closed" if no Resolved category)
  6. adoService.PatchAsync(12345, [State: "Resolved"], revision)
  7. gitService.GetCurrentBranchAsync() → "feature/12345-login-timeout"
  8. gitService.IsAheadOfAsync("main") → true
  9. Prompt: "Create pull request? [Y/n]" → "y"
  10. adoGitService.CreatePullRequestAsync(...)  → PR #891
  11. Print summary → exit 0
```

#### `twig flow close`

```
User → FlowCloseCommand.ExecuteAsync()
  1. contextStore.GetActiveWorkItemIdAsync() → 12345
  2. pendingChangeStore.GetDirtyItemIdsAsync() → check if 12345 in set → refuse if so
  3. adoGitService.GetPullRequestsForBranchAsync("feature/12345-login-timeout") → check status
  4. If active PR exists → prompt "Close anyway? [y/N]" (non-TTY: exit 2)
  5. StateShorthand.Resolve('d', entries) → "Closed"
  6. adoService.PatchAsync(12345, [State: "Closed"], revision)
  7. Prompt: "Delete branch feature/12345-login-timeout? [Y/n]" → "y"
  8. gitService.CheckoutAsync("main")
  9. gitService.DeleteBranchAsync("feature/12345-login-timeout")
  10. contextStore.ClearActiveWorkItemIdAsync()
  11. Print summary → exit 0
```

### API Contracts

#### `IAdoGitService` (from git-integration plan, extended)

```csharp
public interface IAdoGitService
{
    Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForBranchAsync(
        string branchName, CancellationToken ct = default);
    Task<PullRequestInfo> CreatePullRequestAsync(
        PullRequestCreate request, CancellationToken ct = default);
    Task<string?> GetRepositoryIdAsync(CancellationToken ct = default);
}
```

#### `IGitService` additions needed beyond git-integration plan

```csharp
// Additional methods needed by flow commands:
Task<bool> HasUncommittedChangesAsync(CancellationToken ct = default);
Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);
Task DeleteBranchAsync(string branchName, CancellationToken ct = default);
Task<bool> IsAheadOfAsync(string targetBranch, CancellationToken ct = default);
Task<bool> IsDetachedHeadAsync(CancellationToken ct = default);
```

#### `PullRequestInfo` and `PullRequestCreate` value objects

```csharp
public sealed record PullRequestInfo(
    int PullRequestId, string Title, string Status,
    string SourceBranch, string TargetBranch, string Url);

public sealed record PullRequestCreate(
    string SourceBranch, string TargetBranch, string Title,
    string Description, int? WorkItemId);
```

### Design Decisions

| ID | Decision | Rationale |
|---|---|---|
| DD-001 | Flow commands are separate classes, not methods on existing commands | Keeps existing commands stable; flow commands have distinct parameter sets and orchestration logic |
| DD-002 | `FlowOrchestrator` is NOT a separate service — orchestration lives directly in each flow command | Each flow command has distinct enough logic that a shared orchestrator would be over-abstraction. Shared utilities (SyncGuard, BranchNameTemplate, SlugHelper) are extracted instead |
| DD-003 | `SyncGuard` is a static service (no DI registration needed) | Follows existing pattern of `StateCategoryResolver`, `StateTransitionService`, `ConflictResolver` — utility classes with no state |
| DD-004 | `IContextStore` gets `ClearActiveWorkItemIdAsync()` instead of making `SetActiveWorkItemIdAsync` accept `int?` | Avoids breaking all existing call sites; clearer intent |
| DD-005 | `SaveCommand` scoping is added by extending `ExecuteAsync` with optional parameters | Minimizes new files; save scoping is a refinement of existing behavior, not a new command |
| DD-006 | Branch template and slug logic are domain value objects | They encode business rules (naming conventions) that should be testable without infrastructure |
| DD-007 | Git operations are skipped (not errored) when not in a git repo | `flow start` in a non-git context should still set context, transition state, and assign |
| DD-008 | Context switch on `flow start` silently replaces — no save/transition on old item | Scenario doc: "implicit saves/transitions on the old item are dangerous" |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|---|---|---|---|
| **Single `FlowCommand` class with subcommands** | Single file, shared state | Violates single-responsibility; would grow to 500+ lines | Rejected — separate classes per subcommand |
| **Shared `FlowOrchestrator` service** | Reusable step composition | Over-abstraction; each flow has distinct logic | Rejected — direct composition in each command |
| **Make `SetActiveWorkItemIdAsync` accept `int?`** | One method instead of two | Breaks all existing call sites; nullable semantics less clear | Rejected — add `ClearActiveWorkItemIdAsync` |
| **New `FlowSaveCommand` instead of extending `SaveCommand`** | Clean separation | Duplicates save logic; inconsistent UX (`twig save` vs `twig flow save`) | Rejected — extend existing save with scoping |
| **SyncGuard as DI service** | Consistent with other services | Stateless utility; DI adds complexity for no benefit | Rejected — static class like `StateCategoryResolver` |

---

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| `git` CLI | Any modern version | Branch creation, checkout, deletion, status detection |
| Azure DevOps REST API | v7.1 | Work item PATCH, PR query/create |
| ConsoleAppFramework | 5.7.13 | Command routing with subcommand support |
| Spectre.Console | Current | Interactive picker for `flow start` (no args) |

### Internal Dependencies

| Dependency | Status | Required by |
|---|---|---|
| `IGitService` + `GitCliService` (git-integration plan EPIC-001) | **Not built** | `flow start`, `flow done`, `flow close` |
| `IAdoGitService` + `AdoGitClient` (git-integration plan EPIC-001/002) | **Not built** | `flow done` (PR), `flow close` (PR guard) |
| `SetCommand` | Built | Item resolution logic reused |
| `StateCommand` + `StateShorthand` | Built | State transition logic reused |
| `SaveCommand` | Built (needs scope extension) | Save logic reused |
| `ConflictResolutionFlow` | Built | Conflict resolution reused |
| `StateCategoryResolver` | Built | State category detection |
| `IIterationService.GetAuthenticatedUserDisplayNameAsync` | Built | Self-assignment |
| `IProcessConfigurationProvider` | Built | State entries for shorthand resolution |
| `OutputFormatterFactory` | Built | Output formatting |
| `RenderingPipelineFactory` (Spectre) | Built | Interactive picker |

### Sequencing Constraints

1. `IGitService`/`GitCliService` from `twig-git-integration.plan.md` EPIC-001 **must** be implemented first.
2. `IAdoGitService`/`AdoGitClient` (PR APIs) from `twig-git-integration.plan.md` EPIC-001 **must** be implemented first.
3. `SyncGuard` and `IContextStore.ClearActiveWorkItemIdAsync` have no external dependencies — can be built immediately.
4. `SaveCommand` scoping can be built independently.
5. Flow commands depend on all of the above.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|---|---|
| `IContextStore` | New method `ClearActiveWorkItemIdAsync` added to interface |
| `SqliteContextStore` | Implements new method (DELETE from context) |
| `SaveCommand` | `ExecuteAsync` signature extended with optional `targetId` and `all` parameters |
| `RefreshCommand` | Pre-save SyncGuard check added |
| `TwigConfiguration` | New `Git` and `Flow` sub-config objects |
| `TwigJsonContext` | New `[JsonSerializable]` attributes for `GitConfig`, `FlowConfig`, PR DTOs |
| `Program.cs` | New DI registrations and `TwigCommands` routing for flow subcommands |
| `IOutputFormatter` | No changes — existing `FormatSuccess`/`FormatError`/`FormatInfo` suffice |

### Backward Compatibility

- **`IContextStore` interface change:** Adding `ClearActiveWorkItemIdAsync` is a breaking change for any external implementations. Since all known implementations are internal (`SqliteContextStore`), impact is contained to tests using `NSubstitute` (auto-stubbed).
- **`SaveCommand.ExecuteAsync` signature:** Adding optional parameters is source-compatible. Existing callers pass no arguments and get the same `all = false` behavior. However, the default behavior changes from "all dirty" to "active work tree" — this is the intended behavioral change per the scenario doc.
- **Configuration:** New `git` and `flow` sections are additive. Existing `.twig/config` files without these sections get defaults via POCO initialization.

### Performance Implications

- **`flow start`:** 2–3 ADO API calls (fetch item, patch state, patch assignment) + 1–2 git CLI calls. Comparable to running `set` + `state` + git manually.
- **`flow done`:** N ADO API calls for N dirty items in work tree + 1 state patch + optional PR creation. May be slower for large work trees.
- **SyncGuard on refresh:** One additional SQLite query (`GetDirtyItemsAsync` + `GetDirtyItemIdsAsync`) before batch save. Negligible overhead.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `IGitService` from git-integration plan is not yet built | High | High | SyncGuard, save scoping, and config work can proceed independently. Flow commands can be built with git operations behind a null-check (`gitService is not null`) |
| ConsoleAppFramework subcommand routing (`twig flow start`) may not work with current `TwigCommands` pattern | Medium | Medium | Test early in EPIC-003. Fallback: use `twig flow-start` (hyphenated) or nested `ConsoleApp.Create()` |
| `SaveCommand` behavior change (all → work tree default) breaks existing users | Medium | Medium | Add `--all` flag and update `twig save` documentation. Consider a deprecation warning for one release |
| ADO 412 (optimistic concurrency) during flow operations | Medium | Low | Retry once after re-fetching revision, per scenario doc OQ-002 |
| Branch name collisions when template generates duplicates | Low | Low | Check existence first; checkout existing branch with warning |

---

## Open Questions

| ID | Question | Proposed Answer | Status |
|---|---|---|---|
| OQ-001 | Should `flow start` auto-refresh if cache is stale? | No — explicit `twig refresh` first. Flow start only fetches the target item. | Resolved (per scenario doc) |
| OQ-002 | What if `flow done` encounters a 412 from ADO? | Retry once after re-fetching revision. If still 412, surface as conflict. | Resolved (per scenario doc) |
| OQ-003 | Should `flow close` auto-delete remote branches? | No — only local. Remote branch deletion has higher blast radius. | Resolved (per scenario doc) |
| OQ-004 | Should `flow start` on an already-InProgress item still create a branch? | Yes — user explicitly asked to start work. | Resolved (per scenario doc) |
| OQ-005 | How does ConsoleAppFramework handle `twig flow start` subcommand routing? | Needs investigation — may require nested command groups or hyphenated naming. | **Open** |
| OQ-006 | Should `SaveCommand` default behavior change from "all" to "work tree"? | Yes per scenario doc, but this is a behavioral breaking change that needs communication. | **Open** |
| OQ-007 | What PR fields should `flow done` populate? | Title from work item title, description with `AB#12345`, source/target from git config. | Resolved |

---

## Implementation Phases

### Phase 1: Foundation (EPIC-001 + EPIC-002)
Build SyncGuard, context clearing, save scoping, config extensions, and BranchNameTemplate. No git dependency.

**Exit criteria:** `twig save` supports scoped save, `twig refresh` has dirty guard, config has `git.*` and `flow.*` sections, `BranchNameTemplate` generates correct names.

### Phase 2: Git Prerequisite (EPIC-003)
Extend `IGitService` with flow-specific methods. Requires `twig-git-integration.plan.md` EPIC-001 to be complete.

**Exit criteria:** `IGitService` has `HasUncommittedChangesAsync`, `BranchExistsAsync`, `DeleteBranchAsync`, `IsAheadOfAsync`, `IsDetachedHeadAsync`.

### Phase 3: Flow Commands (EPIC-004)
Implement `FlowStartCommand`, `FlowDoneCommand`, `FlowCloseCommand`. Wire into DI and `TwigCommands`.

**Exit criteria:** All three flow commands work end-to-end with tests.

### Phase 4: PR Integration (EPIC-005)
Add ADO Git PR query/create to `IAdoGitService`. Wire into `flow done` (offer PR) and `flow close` (guard).

**Exit criteria:** `flow done` creates PRs; `flow close` guards against open PRs.

### Phase 5: Polish (EPIC-006)
Interactive picker for `flow start`, hints, passive branch detection, JSON output contract.

**Exit criteria:** Full scenario doc compliance. All user stories pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Domain/Services/SyncGuard.cs` | Static service: identifies protected (dirty/pending) items |
| `src/Twig.Domain/ValueObjects/BranchNameTemplate.cs` | Branch name generation from template + title slugification |
| `src/Twig.Domain/ValueObjects/SlugHelper.cs` | Title-to-slug conversion with deterministic rules |
| `src/Twig.Domain/ValueObjects/PullRequestInfo.cs` | Read model for PR data from ADO |
| `src/Twig.Domain/ValueObjects/PullRequestCreate.cs` | Value object for PR creation request |
| `src/Twig/Commands/FlowStartCommand.cs` | `twig flow start` orchestration |
| `src/Twig/Commands/FlowDoneCommand.cs` | `twig flow done` orchestration |
| `src/Twig/Commands/FlowCloseCommand.cs` | `twig flow close` orchestration |
| `tests/Twig.Domain.Tests/Services/SyncGuardTests.cs` | Tests for SyncGuard logic |
| `tests/Twig.Domain.Tests/ValueObjects/BranchNameTemplateTests.cs` | Tests for branch name generation and ID extraction |
| `tests/Twig.Domain.Tests/ValueObjects/SlugHelperTests.cs` | Tests for slugification rules |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Tests for flow start orchestration |
| `tests/Twig.Cli.Tests/Commands/FlowDoneCommandTests.cs` | Tests for flow done orchestration |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Tests for flow close orchestration |
| `tests/Twig.Cli.Tests/Commands/SaveCommandScopingTests.cs` | Tests for save scoping (work tree, single, all) |
| `tests/Twig.Cli.Tests/Commands/RefreshDirtyGuardTests.cs` | Tests for refresh dirty guard with SyncGuard |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Domain/Interfaces/IContextStore.cs` | Add `ClearActiveWorkItemIdAsync()` method |
| `src/Twig.Infrastructure/Persistence/SqliteContextStore.cs` | Implement `ClearActiveWorkItemIdAsync()` (DELETE from context) |
| `src/Twig/Commands/SaveCommand.cs` | Add `targetId` and `all` parameters to `ExecuteAsync`; implement work tree scoping |
| `src/Twig/Commands/RefreshCommand.cs` | Add SyncGuard check before `SaveBatchAsync`; add `--force` flag |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `GitConfig`, `FlowConfig` sub-objects; extend `SetValue` for new paths |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for `GitConfig`, `FlowConfig`, PR DTOs |
| `src/Twig/Program.cs` | Register flow commands in DI; add routing in `TwigCommands`; register `IGitService` if not already |
| `src/Twig.Domain/Interfaces/IGitService.cs` | Add flow-specific methods (if git-integration plan's interface doesn't include them) |
| `tests/Twig.Infrastructure.Tests/Persistence/SqliteContextStoreTests.cs` | Add test for `ClearActiveWorkItemIdAsync` |

### Deleted Files

| File Path | Reason |
|---|---|
| (none) | |

---

## Implementation Plan

### EPIC-001: SyncGuard & Context Clearing

**Goal:** Build the sync safety foundation — SyncGuard service and `IContextStore.ClearActiveWorkItemIdAsync`.

**Prerequisites:** None.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-001 | IMPL | Create `SyncGuard` static class with `GetProtectedItemIdsAsync` method. Combines `IWorkItemRepository.GetDirtyItemsAsync()` and `IPendingChangeStore.GetDirtyItemIdsAsync()` into a unified `IReadOnlySet<int>`. | `src/Twig.Domain/Services/SyncGuard.cs` | DONE |
| ITEM-002 | TEST | Unit tests for `SyncGuard`: empty repos, dirty-only, pending-only, both, overlapping IDs. Mock `IWorkItemRepository` and `IPendingChangeStore` via NSubstitute. | `tests/Twig.Domain.Tests/Services/SyncGuardTests.cs` | DONE |
| ITEM-003 | IMPL | Add `ClearActiveWorkItemIdAsync(CancellationToken ct = default)` to `IContextStore` interface. | `src/Twig.Domain/Interfaces/IContextStore.cs` | DONE |
| ITEM-004 | IMPL | Implement `ClearActiveWorkItemIdAsync` in `SqliteContextStore`: `DELETE FROM context WHERE key = 'active_work_item_id'`. | `src/Twig.Infrastructure/Persistence/SqliteContextStore.cs` | DONE |
| ITEM-005 | TEST | Test `ClearActiveWorkItemIdAsync`: set an active ID, clear it, verify `GetActiveWorkItemIdAsync` returns null. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteContextStoreTests.cs` | DONE |

**Acceptance Criteria:**
- [x] `SyncGuard.GetProtectedItemIdsAsync` returns correct union of dirty and pending item IDs
- [x] `IContextStore.ClearActiveWorkItemIdAsync` removes the active context
- [x] All tests pass

---

### EPIC-002: Save Scoping, Refresh Guard & Configuration

**Goal:** Extend `SaveCommand` with work tree scoping, add dirty guard to `RefreshCommand`, and add `git.*`/`flow.*` configuration sections.

**Prerequisites:** EPIC-001 (SyncGuard).

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-006 | IMPL | Extend `SaveCommand.ExecuteAsync` with optional `targetId` (int?) and `all` (bool) parameters. When `targetId` is set, save only that item. When `all` is true, save all dirty (current behavior). When both are null/false, save active work tree: active context item + children that have pending changes. | `src/Twig/Commands/SaveCommand.cs` | DONE |
| ITEM-007 | TEST | Tests for save scoping: (a) active work tree saves item + dirty children only, (b) explicit ID saves single item, (c) `--all` saves everything, (d) no active context with no args errors. | `tests/Twig.Cli.Tests/Commands/SaveCommandScopingTests.cs` | DONE |
| ITEM-008 | IMPL | Add SyncGuard check to `RefreshCommand.ExecuteAsync`: before `SaveBatchAsync`, compute protected IDs via `SyncGuard.GetProtectedItemIdsAsync`. For each remote item where `remote.Revision > local.Revision` AND item ID is in protected set → add to conflict list. If conflicts exist, print details and exit 1. Add `--force` flag to bypass. | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-009 | TEST | Tests for refresh dirty guard: (a) clean cache refreshes normally, (b) dirty item with newer remote revision halts refresh, (c) dirty item with same revision skips (safe), (d) `--force` overrides, (e) pending-only items are also protected. | `tests/Twig.Cli.Tests/Commands/RefreshDirtyGuardTests.cs` | DONE |
| ITEM-010 | IMPL | Add `GitConfig` and `FlowConfig` classes to `TwigConfiguration`. Add `Git` and `Flow` properties. Extend `SetValue` switch for `git.branchtemplate`, `git.branchpattern`, `git.defaulttarget`, `flow.autoassign`, `flow.autosaveondone`, `flow.offerprondone`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-011 | IMPL | Add `[JsonSerializable(typeof(GitConfig))]` and `[JsonSerializable(typeof(FlowConfig))]` to `TwigJsonContext`. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-012 | IMPL | Update `TwigCommands.Save` routing in `Program.cs` to pass `targetId` and `all` parameters from CLI args to `SaveCommand.ExecuteAsync`. | `src/Twig/Program.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig save` (no args) saves only active work tree
- [x] `twig save --all` saves all dirty items
- [x] `twig refresh` halts when dirty items conflict with remote changes
- [x] `twig refresh --force` overrides dirty guard
- [x] `twig config git.branchtemplate "bug/{id}-{title}"` works
- [x] All tests pass

---

### EPIC-003: Branch Name Template & Slug Helper — DONE

**Goal:** Implement branch name generation from configurable templates with deterministic slugification.

**Prerequisites:** None.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-013 | IMPL | Create `SlugHelper` static class with `Slugify(string input, int maxLength = 50)` method. Rules: lowercase, spaces/underscores→hyphens, strip non-alphanumeric (except hyphens), collapse consecutive hyphens, truncate to maxLength, trim trailing hyphens. Guard: `maxLength <= 0` returns empty string. | `src/Twig.Domain/ValueObjects/SlugHelper.cs` | DONE |
| ITEM-014 | TEST | Tests for `SlugHelper.Slugify`: basic conversion, special chars, consecutive hyphens, truncation at word boundary, trailing hyphen trim, empty input, unicode handling, zero/negative maxLength returns empty. | `tests/Twig.Domain.Tests/ValueObjects/SlugHelperTests.cs` | DONE |
| ITEM-015 | IMPL | Create `BranchNameTemplate` static class (non-partial). `Generate(string template, int id, string type, string title)`: replace `{id}`, `{type}` (slugified via `SlugHelper.Slugify` so multi-word types like 'User Story' produce 'user-story'), `{title}` (slugified). `ExtractWorkItemId(string branchName, string pattern)`: apply regex wrapped in try/catch(ArgumentException), return captured `id` group as int or null on malformed pattern. | `src/Twig.Domain/ValueObjects/BranchNameTemplate.cs` | DONE |
| ITEM-016 | TEST | Tests for `BranchNameTemplate`: default template, custom template, all tokens, multi-word type slug, ID extraction from various branch formats, no-match returns null, malformed pattern returns null. | `tests/Twig.Domain.Tests/ValueObjects/BranchNameTemplateTests.cs` | DONE |

**Acceptance Criteria:**
- [x] `SlugHelper.Slugify("Login timeout on slow connections")` → `"login-timeout-on-slow-connections"`
- [x] `BranchNameTemplate.Generate("feature/{id}-{title}", 12345, "Bug", "Login timeout on slow connections")` → `"feature/12345-login-timeout-on-slow-connections"`
- [x] `BranchNameTemplate.ExtractWorkItemId("feature/12345-login-timeout", defaultPattern)` → `12345`
- [x] Long titles are truncated to 50 chars
- [x] All tests pass

---

### EPIC-004: Flow Commands — Start, Done, Close — DONE

**Completed**: 2026-03-17. EPIC-004 delivered: `FlowStartCommand`, `FlowDoneCommand`, `FlowCloseCommand` with full DI registration in `Program.cs`. All tasks DONE. Coverage gap closed: `NonExplicitId_WorkTreeSaved_IncludedWhenChildIsDirtyButActiveItemIsClean` added to `FlowDoneCommandTests.cs` to exercise the dirty-child branch (`children.Any(c => dirtySet.Contains(c.Id))`) on line 86 of `FlowDoneCommand.cs`.

**Goal:** Implement the three `twig flow` orchestrating commands.

**Prerequisites:** EPIC-001 (SyncGuard, context clearing), EPIC-002 (save scoping, config), EPIC-003 (branch naming). Soft dependency on `IGitService` from `twig-git-integration.plan.md` — git operations are skipped if service is unavailable.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-017 | IMPL | Create `FlowStartCommand` class. Constructor injection: `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`, `RenderingPipelineFactory?`, `IGitService?`. Implement `ExecuteAsync(string? idOrPattern, bool noBranch, bool noState, bool noAssign, bool take, bool force, string outputFormat)`. Sequence: resolve item → fetch latest → conflict check → set context → transition state (if Proposed) → assign (if unassigned/--take) → create branch (if git) → print summary. | `src/Twig/Commands/FlowStartCommand.cs` | DONE |
| ITEM-018 | TEST | Tests for `FlowStartCommand`: (a) full happy path (set+state+assign+branch), (b) --no-branch skips git, (c) --no-state skips transition, (d) --no-assign skips assignment, (e) already InProgress skips state transition, (f) already assigned skips assignment, (g) --take assigns even if already assigned, (h) --force proceeds with uncommitted changes, (i) no git repo skips branch, (j) pattern disambiguation, (k) context switch hint, (l) JSON output format, (m) conflict during fetch aborts. | `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | DONE |
| ITEM-019 | IMPL | Create `FlowDoneCommand` class. Constructor injection: `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IGitService?`, `IAdoGitService?`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`. Implement `ExecuteAsync(int? id, bool noSave, bool noPr, string outputFormat)`. Sequence: resolve target → save work tree (if not --no-save) → transition InProgress→Resolved (or Completed fallback) → offer PR if ahead → print summary. Explicit ID narrows save scope to single item and does NOT change active context. | `src/Twig/Commands/FlowDoneCommand.cs` | DONE |
| ITEM-020 | TEST | Tests for `FlowDoneCommand`: (a) happy path (save+state+PR offer), (b) --no-save skips save, (c) --no-pr skips PR offer, (d) explicit ID saves single item, (e) explicit ID does not change context, (f) no active context errors, (g) conflict during save aborts, (h) type with no Resolved category falls back to Completed, (i) already Resolved skips transition, (j) JSON output format. | `tests/Twig.Cli.Tests/Commands/FlowDoneCommandTests.cs` | DONE |
| ITEM-021 | IMPL | Create `FlowCloseCommand` class. Constructor injection: `IWorkItemRepository`, `IAdoWorkItemService`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `IGitService?`, `IAdoGitService?`, `IConsoleInput`, `OutputFormatterFactory`, `HintEngine`, `TwigConfiguration`. Implement `ExecuteAsync(int? id, bool force, bool noBranchCleanup, string outputFormat)`. Sequence: resolve target → guard unsaved changes → guard open PRs → transition to Completed → delete branch (prompt) → clear context → print summary. | `src/Twig/Commands/FlowCloseCommand.cs` | DONE |
| ITEM-022 | TEST | Tests for `FlowCloseCommand`: (a) happy path (guard+state+branch+clear), (b) unsaved changes refuses (exit 1), (c) open PR warns and prompts, (d) --force bypasses guards, (e) --no-branch-cleanup skips branch delete, (f) non-TTY exits 2 on open PR, (g) already Completed skips transition, (h) no git repo skips branch cleanup, (i) JSON output format. | `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | DONE |
| ITEM-023 | IMPL | Register `FlowStartCommand`, `FlowDoneCommand`, `FlowCloseCommand` in `Program.cs` DI. Add `TwigCommands` routing methods: `FlowStart`, `FlowDone`, `FlowClose` (or equivalent ConsoleAppFramework subcommand registration). | `src/Twig/Program.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig flow start 12345` sets context, transitions state, assigns, creates branch
- [x] `twig flow done` saves work tree, transitions to Resolved, offers PR
- [x] `twig flow close` guards, transitions to Closed, deletes branch, clears context
- [x] All skip flags work correctly
- [x] JSON format produces structured output
- [x] Exit codes follow contract (0/1/2)
- [x] All tests pass

---

### EPIC-005: PR Integration — DONE

**Completed**: 2026-03-17. EPIC-005 delivered: `IsOutputRedirected` abstracted into `IConsoleInput` for testability, `FlowDoneCommand` and `FlowCloseCommand` wired to ADO Git PR query/create via `IAdoGitService`. TTY detection in `FlowCloseCommand` now delegates to `consoleInput.IsOutputRedirected`. `FlowDoneCommandTests` renamed `UserDeclinesPr_NoPrCreated` (typo fix) and replaced a near-duplicate with `OpenPr_Tty_UserDeclines_ReturnsZero` testing the TTY interactive path. `FlowCloseCommandTests` added `CompletedPr_DoesNotTriggerGuard`, removed dead mock setup, and defaults `_consoleInput.IsOutputRedirected.Returns(true)` for backwards compatibility.

**Goal:** Wire ADO Git PR query/create into `flow done` and `flow close`. Depends on `IAdoGitService` from `twig-git-integration.plan.md`.

**Prerequisites:** EPIC-004 (flow commands exist with PR code paths stubbed). `IAdoGitService`/`AdoGitClient` from git-integration plan.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-024 | IMPL | Create `PullRequestInfo` record: `PullRequestId`, `Title`, `Status`, `SourceBranch`, `TargetBranch`, `Url`. Create `PullRequestCreate` record: `SourceBranch`, `TargetBranch`, `Title`, `Description`, `WorkItemId`. | `src/Twig.Domain/ValueObjects/PullRequestInfo.cs`, `src/Twig.Domain/ValueObjects/PullRequestCreate.cs` | DONE |
| ITEM-025 | IMPL | Add `[JsonSerializable]` attributes for PR-related DTOs to `TwigJsonContext` (ADO PR API request/response types). | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-026 | IMPL | Wire `IAdoGitService.CreatePullRequestAsync` into `FlowDoneCommand`: after state transition, if `config.Flow.OfferPrOnDone` and git branch is ahead of `config.Git.DefaultTarget`, prompt user. On "y", call `CreatePullRequestAsync` with `AB#{id}` in description. | `src/Twig/Commands/FlowDoneCommand.cs` | DONE |
| ITEM-027 | IMPL | Wire `IAdoGitService.GetPullRequestsForBranchAsync` into `FlowCloseCommand`: before state transition, query PRs for current branch. If active (non-merged) PR exists, print warning and prompt. Non-TTY: exit 2. `--force` bypasses. | `src/Twig/Commands/FlowCloseCommand.cs` | DONE |
| ITEM-028 | TEST | Integration tests for PR wiring: mock `IAdoGitService`, verify `FlowDoneCommand` calls `CreatePullRequestAsync` when branch is ahead and user confirms. Verify `FlowCloseCommand` exits 2 when active PR exists in non-TTY. | `tests/Twig.Cli.Tests/Commands/FlowDoneCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | DONE |

**Acceptance Criteria:**
- [x] `flow done` prompts for PR creation when branch is ahead
- [x] PR includes `AB#12345` in description for ADO linking
- [x] `flow close` warns about active PRs
- [x] Non-TTY `flow close` exits 2 with PR details
- [x] `--force` bypasses PR guard
- [x] All tests pass

---

### EPIC-006: Interactive Picker & Polish — DONE

**Goal:** Add interactive item picker for `flow start` (no args), passive branch detection hint, and final output polish.

**Prerequisites:** EPIC-004 (flow commands).

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-029 | IMPL | Add no-argument path to `FlowStartCommand`: query `workItemRepo.GetByIterationAndAssigneeAsync` for items in current sprint with Proposed state category. Show interactive `SelectionPrompt` via `RenderingPipelineFactory` (TTY) or error with list (non-TTY). After selection, proceed with full start sequence. | `src/Twig/Commands/FlowStartCommand.cs` | DONE |
| ITEM-030 | TEST | Tests for interactive picker: (a) items available shows picker, (b) no items prints "No unstarted items", (c) non-TTY prints list and errors. | `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | DONE |
| ITEM-031 | IMPL | Add passive branch detection hint to `HintEngine`: on commands where no active context is set, check `IGitService.GetCurrentBranchAsync()`, apply `BranchNameTemplate.ExtractWorkItemId`, and if ID exists in cache, emit hint: "Tip: branch matches #12345. Run 'twig set 12345' to set context." Never auto-set. | `src/Twig/Hints/HintEngine.cs` | DONE |
| ITEM-032 | TEST | Tests for branch detection hint: (a) matching branch emits hint, (b) non-matching branch emits nothing, (c) no git repo emits nothing, (d) already has context emits nothing. | `tests/Twig.Cli.Tests/Hints/BranchDetectionHintTests.cs` | DONE |
| ITEM-033 | IMPL | Finalize JSON output contract for all flow commands: structured `actions` object with `contextSet`, `stateChanged`, `assigned`, `branch`, `saved`, `prCreated` fields matching the scenario doc's JSON schema. | `src/Twig/Commands/FlowStartCommand.cs`, `src/Twig/Commands/FlowDoneCommand.cs`, `src/Twig/Commands/FlowCloseCommand.cs` | DONE |
| ITEM-034 | IMPL | Add minimal output format for flow commands: `flow start` → branch name only; `flow done` → PR URL or empty; `flow close` → empty. | `src/Twig/Commands/FlowStartCommand.cs`, `src/Twig/Commands/FlowDoneCommand.cs`, `src/Twig/Commands/FlowCloseCommand.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig flow start` (no args) shows interactive picker of unstarted sprint items
- [x] Branch detection hints appear when branch name matches a cached work item
- [x] JSON output matches the scenario doc contract
- [x] Minimal output matches the scenario doc contract
- [x] All tests pass
- [x] All user stories (US-01 through US-14) are satisfied

---

## References

- **Scenario document:** `docs/projects/twig-flow-lifecycle.scenario.md`
- **Git integration plan:** `docs/projects/twig-git-integration.plan.md` (prerequisite: EPIC-001 for `IGitService`/`GitCliService`)
- **Azure DevOps REST API — Work Items:** https://learn.microsoft.com/en-us/rest/api/azure-devops/wit/work-items
- **Azure DevOps REST API — Pull Requests:** https://learn.microsoft.com/en-us/rest/api/azure-devops/git/pull-requests
- **ConsoleAppFramework subcommands:** https://github.com/Cysharp/ConsoleAppFramework
- **RFC 2119:** https://www.rfc-editor.org/rfc/rfc2119
