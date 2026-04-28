# Flow & Git Integration Removal — Remove 13 Commands

| Field | Value |
|-------|-------|
| **Epic** | #2153 |
| **Status** | 🔨 In Progress |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

## Executive Summary

This plan removes 13 user-facing commands from the twig CLI that either violate the
process-agnostic principle (`flow-start`, `flow-done`, `flow-close`) or wrap git/gh
CLI that users already use directly (`branch`, `commit`, `pr`, `link branch`, `context`,
`hooks install`, `hooks uninstall`, `_hook`, `stash`, `stash pop`, `log`). The removal
encompasses 12 command implementation files, 3 infrastructure classes (`GitCliService`,
`HookInstaller`, `GitGuard`), 1 domain interface (`IGitService`), 2 domain services
(`FlowTransitionService`, `CommitMessageService`), associated configuration sections
(`FlowConfig`, `HooksConfig`), and ~20 test files. Retained commands (`status`,
`seed publish`) that optionally depend on the removed infrastructure will have their
git-dependent parameters removed, gracefully losing optional features (branch display,
PR linkage display). `IAdoGitService` and `AdoGitClient` are **retained** because they
serve `seed publish --link-branch`, MCP `twig_link_branch`, and `StatusCommand` PR display.

## Background

### Current Architecture

Twig's command surface includes three categories of commands being removed:

1. **Flow commands** (`flow-start`, `flow-done`, `flow-close`) — Opinionated workflow
   shortcuts that bundle context setting, state transitions, branch creation, assignment,
   and PR creation into single commands. They depend on `FlowTransitionService` and encode
   assumptions about state category mappings (Proposed→InProgress, InProgress→Resolved/Completed).

2. **Git wrapper commands** (`branch`, `commit`, `pr`, `stash`, `stash pop`, `log`) — Thin
   wrappers around `git` CLI operations that add work-item context (branch naming, commit
   message formatting, PR creation with artifact links, annotated git log).

3. **Git integration plumbing** (`link branch`, `context`, `hooks install`, `hooks uninstall`,
   `_hook`) — Infrastructure for git hook management and branch-to-work-item linking.

The commands are implemented across three layers:
- **CLI layer** (`src/Twig/Commands/`): 12 command classes + `GitGuard` helper
- **Infrastructure layer** (`src/Twig.Infrastructure/Git/`): `GitCliService`, `HookInstaller`,
  `GitOperationException`
- **Domain layer** (`src/Twig.Domain/`): `IGitService` interface, `FlowTransitionService`,
  `CommitMessageService`, `BranchNamingService`

### Call-Site Audit

The following table inventories all production code call sites for the types being removed.
Test-only references are excluded (tests are deleted wholesale with their commands).

| File | Type Referenced | Usage | Impact |
|------|----------------|-------|--------|
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | `IGitService`, `GitCliService` | `AddSingleton<IGitService, GitCliService>()` | Remove registration |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | `IGitService`, `HookInstaller`, `FlowStartCommand`, `FlowDoneCommand`, `FlowCloseCommand`, `BranchCommand`, `CommitCommand`, `PrCommand`, `StashCommand`, `LogCommand`, `HooksCommand`, `GitContextCommand`, `HookHandlerCommand`, `LinkBranchCommand` | Factory registrations in `AddGitCommands()`, `AddFlowCommands()`, `AddCoreCommands()` | Remove methods/registrations |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | `FlowTransitionService` | Factory registration | Remove registration |
| `src/Twig/Program.cs` | All 13 commands | `[Command]` method definitions, `KnownCommands` list, help text | Remove command methods, update lists/help |
| `src/Twig/CommandExamples.cs` | N/A | Example entries for `branch`, `commit`, `pr`, `stash`, `stash pop`, `log`, `flow-start`, `flow-done`, `flow-close`, `hooks install`, `hooks uninstall`, `context`, `link branch` | Remove entries |
| `src/Twig/Hints/HintEngine.cs` | `IGitService`, `BranchNameTemplate` | `GetBranchDetectionHintAsync()` method, hint text referencing `twig branch/commit/hooks/flow-done` | Remove method, update hints |
| `src/Twig/Commands/StatusCommand.cs` | `IGitService?`, `IAdoGitService?` | Optional constructor params for branch/PR display | Remove `IGitService?` param; keep `IAdoGitService?` |
| `src/Twig/Commands/SeedPublishCommand.cs` | `IAdoGitService?` | `--link-branch` feature | **No change** (retained) |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | `IAdoGitService`, `BranchLinkService` | MCP `twig_link_branch` tool | **No change** (retained) |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `BranchLinkService` | `twig_link_branch` MCP tool | **No change** (retained) |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | `FlowConfig`, `HooksConfig`, `GitConfig` | Config class properties and `SetValue()` cases | Remove `FlowConfig`, `HooksConfig`; prune `GitConfig` |

## Problem Statement

The twig CLI currently includes 13 commands that fall into two problematic categories:

1. **Process-agnostic violation**: `flow-start`, `flow-done`, and `flow-close` encode
   opinionated workflow shortcuts tied to specific state category assumptions
   (Proposed→InProgress→Resolved→Completed). These violate twig's core principle
   that process-specific mapping comes from `IProcessConfigurationProvider` at runtime,
   not hardcoded command logic. While `FlowTransitionService` uses process config for
   state resolution, the *intent* of the flow commands — "start work", "mark done",
   "close" — presupposes a linear workflow that doesn't apply to all process templates.

2. **Redundant git wrapping**: `branch`, `commit`, `pr`, `stash`, `log`, `hooks`,
   `context`, `link branch`, and `_hook` wrap `git` and `gh` CLI tools that users
   already have and know. The value-add (branch naming, commit message formatting,
   artifact linking) is modest compared to the maintenance cost and confusion about
   whether to use `twig commit` vs `git commit`.

Together, these 13 commands account for 12 command implementation files (~2,400 LoC),
3 infrastructure classes (~480 LoC), 2+ domain services (~250 LoC), and ~20 test files
(~4,000+ LoC). Removing them reduces the command surface by ~25% and eliminates the
`src/Twig.Infrastructure/Git/` directory entirely.

## Goals and Non-Goals

### Goals

1. Remove all 13 commands from the CLI surface (`flow-start`, `flow-done`, `flow-close`,
   `branch`, `commit`, `pr`, `link branch`, `context`, `hooks install`, `hooks uninstall`,
   `_hook`, `stash`, `stash pop`, `log`)
2. Remove all dead infrastructure: `IGitService`, `GitCliService`, `HookInstaller`,
   `GitGuard`, `GitOperationException`, `FlowTransitionService`, `CommitMessageService`,
   `BranchNamingService`
3. Remove dead configuration: `FlowConfig`, `HooksConfig`, and git-command-specific
   `GitConfig` properties (`BranchTemplate`, `CommitTemplate`, `AutoLink`, `AutoTransition`,
   `TypeMap`, `DefaultTarget`)
4. Remove all associated tests
5. Retain `IAdoGitService`, `AdoGitClient`, `BranchLinkService`, and PR/artifact DTOs
   (still used by `seed publish --link-branch`, MCP `twig_link_branch`, and `StatusCommand`)
6. Clean compilation with zero warnings after removal
7. All remaining tests pass

### Non-Goals

- Removing the `web` command (explicitly kept — it is a utility, not git integration)
- Removing `IAdoGitService` or `AdoGitClient` (still serves `seed publish` and MCP)
- Removing `AdoRemoteParser` (still used by `Program.cs` for git remote auto-detection
  that feeds `IAdoGitService` registration)
- Removing `BranchNameTemplate` or `WorkItemIdExtractor` (still used by retained code
  paths in `HintEngine` via IGitService null-check — becomes dead code but harmless;
  deferred to a separate cleanup if desired)
- Adding replacement functionality for the removed commands
- Modifying the MCP server tools beyond what's needed for compilation

## Requirements

### Functional Requirements

1. **FR-1**: The 13 commands must no longer appear in CLI help output or be executable
2. **FR-2**: `twig status` must continue to function without git context (branch/PR display
   sections silently omitted when `IGitService` is unavailable)
3. **FR-3**: `twig seed publish --link-branch` must continue to function (uses `IAdoGitService`, not `IGitService`)
4. **FR-4**: MCP `twig_link_branch` tool must continue to function
5. **FR-5**: Configuration files with `flow.*` or `git.hooks.*` keys must not cause errors
   (graceful ignore via existing `SetValue` fallback behavior)
6. **FR-6**: All remaining commands must compile and pass tests

### Non-Functional Requirements

1. **NFR-1**: Zero new warnings under `TreatWarningsAsErrors=true`
2. **NFR-2**: AOT compilation must succeed (`PublishAot=true`, `TrimMode=full`)
3. **NFR-3**: No orphaned `[JsonSerializable]` entries in `TwigJsonContext` for removed types
   (only remove entries whose types are deleted; PR/artifact DTOs are retained)

## Proposed Design

### Architecture Overview

The removal is a surgical deletion with minimal refactoring. The approach:

1. **Delete command files** — 12 files in `src/Twig/Commands/`
2. **Delete infrastructure** — entire `src/Twig.Infrastructure/Git/` directory (3 files)
3. **Delete domain interface** — `IGitService.cs`
4. **Delete domain services** — `FlowTransitionService.cs`, `CommitMessageService.cs`,
   `BranchNamingService.cs` (and their value objects if orphaned)
5. **Update Program.cs** — remove 13 command method definitions, update `KnownCommands`,
   update help text, remove git remote auto-detection background task
6. **Update DI modules** — remove `AddGitCommands()`, `AddFlowCommands()` from
   `CommandRegistrationModule.cs`; remove `FlowTransitionService` from
   `CommandServiceModule.cs`; remove `IGitService` from `TwigServiceRegistration.cs`
7. **Update retained commands** — remove `IGitService?` parameter from `StatusCommand`
8. **Update hints** — remove git/flow references from `HintEngine`
9. **Update config** — remove `FlowConfig`, `HooksConfig`, prune `GitConfig`
10. **Delete tests** — all test files for removed commands and services

### Key Design Decisions

**DD-1: Keep `IAdoGitService` and `AdoGitClient`**
These are ADO REST API clients, not local git CLI wrappers. They serve:
- `SeedPublishCommand` (`--link-branch` creates artifact links via ADO API)
- MCP `twig_link_branch` tool (via `BranchLinkService`)
- `StatusCommand` (optional PR display)

Removing them would break features unrelated to the git CLI integration.

**DD-2: Keep `BranchLinkService`**
Used by MCP `twig_link_branch` and `SeedPublishCommand`. It depends on `IAdoGitService`,
not `IGitService`. The CLI `link branch` command is removed, but the underlying service
is still valuable.

**DD-3: Keep `AdoRemoteParser` and git remote auto-detection in `Program.cs`**
The background task in `Program.cs` that parses git remote URLs feeds `IAdoGitService`
registration (determines `git.project` and `git.repository`). Since `IAdoGitService`
is retained, this plumbing stays.

**DD-4: Remove `StatusCommand` git features gracefully**
`StatusCommand` currently takes `IGitService?` for branch display and `IAdoGitService?`
for PR display. After removing `IGitService`, the branch/PR section becomes unavailable.
Since `IAdoGitService` is retained but branch name is needed to query PRs, the PR display
also becomes dead code in `StatusCommand` — but we keep `IAdoGitService?` to avoid
unnecessary churn (the null-check pattern handles this gracefully).

**DD-5: Keep `GitConfig.Project`, `GitConfig.Repository`, `GitConfig.BranchPattern`**
These are still used by:
- `AdoRemoteParser` / git remote auto-detection → feeds `IAdoGitService`
- `GitConfig.Project` / `GitConfig.Repository` → configure `AdoGitClient` scope
- `GitConfig.BranchPattern` → used by `WorkItemIdExtractor` → `HintEngine`

Remove only the properties used exclusively by deleted commands:
`BranchTemplate`, `CommitTemplate`, `DefaultTarget`, `AutoLink`, `AutoTransition`, `TypeMap`, `Hooks`.

**DD-6: Remove `FlowConfig` entirely**
All three properties (`AutoAssign`, `AutoSaveOnDone`, `OfferPrOnDone`) are exclusively
used by flow commands. The `FlowConfig` class and its registration in `TwigConfiguration`
are removed.

### Data Flow (Key Operations After Removal)

**`twig status`** (retained, simplified):
```
StatusCommand → ActiveItemResolver → IWorkItemRepository → render
                                   ↘ (no more branch/PR section)
```

**`twig seed publish --link-branch`** (retained, unchanged):
```
SeedPublishCommand → SeedPublishOrchestrator → IAdoWorkItemService (create items)
                   → IAdoGitService.GetProjectIdAsync/GetRepositoryIdAsync
                   → IAdoGitService.AddArtifactLinkAsync (link branch)
```

**MCP `twig_link_branch`** (retained, unchanged):
```
CreationTools → WorkspaceResolver → WorkspaceContext.BranchLinkService
             → BranchLinkService → IAdoGitService (resolve IDs, add artifact link)
```

## Dependencies

### Internal Dependencies (Sequencing Constraints)

- Issue 2 (remove git infrastructure) must complete before Issue 3 (update retained commands),
  since retained commands need to compile without the removed types
- Issue 1 (remove flow commands) can proceed in parallel with Issue 2
- Issue 4 (cleanup config & hints) depends on Issues 1-3
- Issue 5 (delete tests) can proceed in parallel with any issue

### External Dependencies

None. All changes are internal to the twig codebase.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Missed call site causes build break | Low | Low | Comprehensive call-site audit completed; incremental build after each issue |
| Users depend on removed commands | Medium | Medium | Commands wrap existing git/gh tools; users can use those directly |
| Config files with removed keys cause errors | Low | Medium | `SetValue()` fallback behavior already ignores unknown keys; test coverage |
| MCP twig_link_branch breaks | Low | High | `BranchLinkService` and `IAdoGitService` explicitly retained; test coverage |

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `BranchNameTemplate`, `WorkItemIdExtractor`, `SlugHelper`, and `BranchNamingService` be removed as dead code, or deferred to a separate cleanup? They become unreachable after `IGitService` removal (the `HintEngine.GetBranchDetectionHintAsync` path short-circuits when `gitService` is null), but are harmless to keep. | Low | Open |
| 2 | Should `GitConfig.BranchPattern` be removed? It feeds `WorkItemIdExtractor` which feeds `HintEngine.GetBranchDetectionHintAsync`, but that method returns null when `IGitService` is null. Keeping it avoids config-file breakage for users who have `git.branchPattern` set. | Low | Open |
| 3 | The description says "10 commands" but the title says "13". This plan counts 13 by including `stash`, `stash pop`, `log`, `hooks install`, `hooks uninstall`, and `_hook` as separate user-facing commands. Confirm stash/log are in scope. | Low | Open — planned for inclusion |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| `src/Twig/Commands/FlowStartCommand.cs` | Flow command removed |
| `src/Twig/Commands/FlowDoneCommand.cs` | Flow command removed |
| `src/Twig/Commands/FlowCloseCommand.cs` | Flow command removed |
| `src/Twig/Commands/BranchCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/CommitCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/PrCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/LinkBranchCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/GitContextCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/HooksCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/HookHandlerCommand.cs` | Git hook handler removed |
| `src/Twig/Commands/StashCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/LogCommand.cs` | Git wrapper removed |
| `src/Twig/Commands/GitGuard.cs` | Only used by removed commands |
| `src/Twig.Infrastructure/Git/GitCliService.cs` | IGitService implementation removed |
| `src/Twig.Infrastructure/Git/HookInstaller.cs` | Hook infrastructure removed |
| `src/Twig.Infrastructure/Git/GitOperationException.cs` | Only thrown by GitCliService |
| `src/Twig.Domain/Interfaces/IGitService.cs` | Interface for removed service |
| `src/Twig.Domain/Services/Process/FlowTransitionService.cs` | Only used by flow commands |
| `src/Twig.Domain/Services/Navigation/CommitMessageService.cs` | Only used by CommitCommand |
| `src/Twig.Domain/Services/Navigation/BranchNamingService.cs` | Only used by removed commands |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/FlowStartCommand_ContextChangeTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/FlowCloseCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/FlowDoneCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/BranchCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/CommitCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/PrCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/LinkBranchCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/GitContextCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/HooksCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/HookHandlerCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/GitGuardTests.cs` | Tests for removed helper |
| `tests/Twig.Cli.Tests/Commands/StashCommandTests.cs` | Tests for removed command |
| `tests/Twig.Cli.Tests/Commands/LogCommandTests.cs` | Tests for removed command |
| `tests/Twig.Infrastructure.Tests/Git/GitCliServiceTests.cs` | Tests for removed service |
| `tests/Twig.Infrastructure.Tests/Git/HookInstallerTests.cs` | Tests for removed service |
| `tests/Twig.Domain.Tests/Services/Process/FlowTransitionServiceTests.cs` | Tests for removed service |
| `tests/Twig.Domain.Tests/Services/Navigation/CommitMessageServiceTests.cs` | Tests for removed service |
| `tests/Twig.Domain.Tests/Services/Navigation/BranchNamingServiceTests.cs` | Tests for removed service |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | Remove 13 command method definitions; remove entries from `KnownCommands` and `HiddenCommands`; update help text to remove Git and Workflow sections; remove stash/log entries |
| `src/Twig/CommandExamples.cs` | Remove example entries for all 13 removed commands |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Remove `AddGitCommands()` method and call; remove flow command registrations from `AddFlowCommands()` or remove the method; remove `LinkBranchCommand` from `AddCoreCommands()` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Remove `FlowTransitionService` registration |
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Remove `IGitService`/`GitCliService` registration |
| `src/Twig/Commands/StatusCommand.cs` | Remove `IGitService?` constructor parameter; remove branch detection and `AppendGitContextAsync` method; keep `IAdoGitService?` for future use (becomes no-op without branch name) |
| `src/Twig/Hints/HintEngine.cs` | Remove `GetBranchDetectionHintAsync()` method; update hint cases to remove references to `twig branch`, `twig commit`, `twig hooks`, `twig flow-done` |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Remove `FlowConfig` class and `Flow` property; remove `HooksConfig` class; remove `GitConfig.BranchTemplate`, `.CommitTemplate`, `.DefaultTarget`, `.AutoLink`, `.AutoTransition`, `.TypeMap`, `.Hooks` properties; remove corresponding `SetValue()` cases |
| `tests/Twig.Cli.Tests/DependencyInjection/CommandRegistrationModuleTests.cs` | Remove all flow/git command DI resolution tests |
| `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | Remove test cases that exercise `FlowStartCommand`, `FlowDoneCommand`, `FlowCloseCommand` |
| `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | Remove test cases that exercise git context / branch detection features |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Verify no orphaned `[JsonSerializable]` entries (PR/artifact DTOs stay since `AdoGitClient` is retained) |

## ADO Work Item Structure

### Issue 1: Remove Flow Commands and FlowTransitionService

**Goal**: Delete the three flow commands and their shared state transition service.

**Prerequisites**: None (can start immediately).

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 1.1 | Delete `FlowStartCommand.cs`, `FlowDoneCommand.cs`, `FlowCloseCommand.cs` | `src/Twig/Commands/FlowStartCommand.cs`, `FlowDoneCommand.cs`, `FlowCloseCommand.cs` | S |
| 1.2 | Delete `FlowTransitionService.cs` and its result types | `src/Twig.Domain/Services/Process/FlowTransitionService.cs` | S |
| 1.3 | Remove flow command method definitions from `Program.cs` (`FlowStart`, `FlowDone`, `FlowClose`) | `src/Twig/Program.cs` | S |
| 1.4 | Remove `AddFlowCommands()` method and call from `CommandRegistrationModule.cs`; remove `FlowTransitionService` from `CommandServiceModule.cs` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `CommandServiceModule.cs` | S |
| 1.5 | Remove `FlowConfig` class, `Flow` property from `TwigConfiguration`, and all `flow.*` cases from `SetValue()` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | S |
| 1.6 | Remove flow command examples from `CommandExamples.cs` (`flow-start`, `flow-done`, `flow-close`) | `src/Twig/CommandExamples.cs` | S |
| 1.7 | Delete all flow command tests | `tests/Twig.Cli.Tests/Commands/FlowStartCommandTests.cs`, `FlowStartCommand_ContextChangeTests.cs`, `FlowCloseCommandTests.cs`, `FlowDoneCommandTests.cs`, `tests/Twig.Domain.Tests/Services/Process/FlowTransitionServiceTests.cs` | S |

**Acceptance Criteria**:
- [ ] No `flow-start`, `flow-done`, `flow-close` in CLI help
- [ ] `FlowTransitionService`, `FlowConfig` classes deleted
- [ ] Build succeeds with no warnings
- [ ] Remaining tests pass

### Issue 2: Remove Git Wrapper Commands and Infrastructure

**Goal**: Delete the 10 git-related commands and supporting infrastructure (`IGitService`,
`GitCliService`, `HookInstaller`, `GitGuard`, `GitOperationException`).

**Prerequisites**: None (can proceed in parallel with Issue 1).

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 2.1 | Delete git command files: `BranchCommand.cs`, `CommitCommand.cs`, `PrCommand.cs`, `LinkBranchCommand.cs`, `GitContextCommand.cs`, `HooksCommand.cs`, `HookHandlerCommand.cs`, `StashCommand.cs`, `LogCommand.cs`, `GitGuard.cs` | `src/Twig/Commands/` (10 files) | S |
| 2.2 | Delete `GitCliService.cs`, `HookInstaller.cs`, `GitOperationException.cs` (entire `Git/` directory) | `src/Twig.Infrastructure/Git/` | S |
| 2.3 | Delete `IGitService.cs` | `src/Twig.Domain/Interfaces/IGitService.cs` | S |
| 2.4 | Delete `CommitMessageService.cs`, `BranchNamingService.cs` | `src/Twig.Domain/Services/Navigation/CommitMessageService.cs`, `BranchNamingService.cs` | S |
| 2.5 | Remove git command method definitions from `Program.cs` (`Branch`, `Commit`, `Pr`, `Stash`, `StashPop`, `Log`, `LinkBranch`, `Context`, `HooksInstall`, `HooksUninstall`, `_Hook`) | `src/Twig/Program.cs` | M |
| 2.6 | Remove `AddGitCommands()` method and call from `CommandRegistrationModule.cs`; remove `LinkBranchCommand` from `AddCoreCommands()` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S |
| 2.7 | Remove `IGitService`/`GitCliService` registration from `TwigServiceRegistration.cs` | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | S |
| 2.8 | Remove git command examples from `CommandExamples.cs` (`branch`, `commit`, `pr`, `stash`, `stash pop`, `log`, `hooks install`, `hooks uninstall`, `context`, `link branch`) | `src/Twig/CommandExamples.cs` | S |
| 2.9 | Remove `HooksConfig` class and `GitConfig.Hooks` property; remove `GitConfig.BranchTemplate`, `.CommitTemplate`, `.DefaultTarget`, `.AutoLink`, `.AutoTransition`, `.TypeMap` and corresponding `SetValue()` cases | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | S |
| 2.10 | Delete all git command and infrastructure tests | `tests/Twig.Cli.Tests/Commands/` (10 files), `tests/Twig.Infrastructure.Tests/Git/` (2 files), `tests/Twig.Domain.Tests/Services/Navigation/` (2 files) | S |

**Acceptance Criteria**:
- [ ] No git wrapper commands in CLI help
- [ ] `src/Twig.Infrastructure/Git/` directory deleted
- [ ] `IGitService` interface deleted
- [ ] Build succeeds with no warnings
- [ ] Remaining tests pass

### Issue 3: Update Retained Commands and Services

**Goal**: Fix compilation of retained commands that previously depended on removed types.

**Prerequisites**: Issues 1 and 2 (types must be deleted first to identify remaining references).

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 3.1 | Remove `IGitService?` parameter from `StatusCommand`; remove `AppendGitContextAsync` method and branch/PR display logic; remove `gitService` usage in branch detection hint call | `src/Twig/Commands/StatusCommand.cs` | M |
| 3.2 | Update `StatusCommand` DI registration in `CommandRegistrationModule.cs` to stop injecting `IGitService` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | S |
| 3.3 | Remove `GetBranchDetectionHintAsync()` method from `HintEngine`; remove hint cases referencing removed commands (`branch`, `commit`, `hooks`, `pr`); remove `IGitService` import | `src/Twig/Hints/HintEngine.cs` | S |
| 3.4 | Update `KnownCommands` and `HiddenCommands` arrays in `Program.cs`; update help text string to remove Git and Workflow sections | `src/Twig/Program.cs` | S |
| 3.5 | Update `PromptStateIntegrationTests.cs` to remove flow command test cases | `tests/Twig.Cli.Tests/Commands/PromptStateIntegrationTests.cs` | S |
| 3.6 | Update `CommandRegistrationModuleTests.cs` to remove flow/git DI resolution tests | `tests/Twig.Cli.Tests/DependencyInjection/CommandRegistrationModuleTests.cs` | S |
| 3.7 | Update `StatusCommandTests.cs` to remove git context / branch detection test cases | `tests/Twig.Cli.Tests/Commands/StatusCommandTests.cs` | S |
| 3.8 | Verify `TwigJsonContext.cs` — confirm PR/artifact DTO entries stay (used by retained `AdoGitClient`) | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | S |

**Acceptance Criteria**:
- [ ] `twig status` works without git context (omits branch/PR section)
- [ ] `twig seed publish --link-branch` still works
- [ ] MCP `twig_link_branch` still works
- [ ] All hints reference only retained commands
- [ ] Build succeeds with no warnings
- [ ] All remaining tests pass

### Issue 4: Final Verification and Documentation Cleanup

**Goal**: End-to-end verification, documentation update, and commit.

**Prerequisites**: Issues 1–3.

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 4.1 | Full build verification (`dotnet build`) — all projects compile with zero warnings | All | S |
| 4.2 | Full test suite (`dotnet test`) — all remaining tests pass | All test projects | S |
| 4.3 | Update documentation that references removed commands (architecture docs, skill files) | `docs/architecture/commands.md`, `.github/skills/twig-cli/SKILL.md`, `docs/ohmyposh.md` | M |
| 4.4 | Verify the `using Twig.Infrastructure.Git` namespace is no longer imported anywhere in production code | All `src/` files | S |
| 4.5 | Commit changes with `AB#2153` reference | N/A | S |

**Acceptance Criteria**:
- [ ] `dotnet build` succeeds with zero warnings across all projects
- [ ] `dotnet test` passes with zero failures
- [ ] No stale documentation references to removed commands
- [ ] Clean git status (no untracked generated files)

## PR Groups

### PG-1: Flow Commands Removal (deep)

**Scope**: Issue 1 (all tasks) — delete flow commands, FlowTransitionService, FlowConfig,
and associated tests.

**Files**: ~15 files deleted, ~5 files modified
**Estimated LoC**: ~800 deleted, ~150 modified
**Classification**: Deep — concentrated changes in command layer and domain services
**Successor**: PG-2

### PG-2: Git Commands & Infrastructure Removal + Retained Command Updates (wide)

**Scope**: Issues 2, 3, 4 — delete git commands and infrastructure, update retained
commands, final verification.

**Files**: ~25 files deleted, ~12 files modified
**Estimated LoC**: ~3,500 deleted, ~400 modified
**Classification**: Wide — many files across all layers, but each change is mechanical
**Predecessor**: PG-1

## Execution Plan

### PR Group Table

| Group | Name | Issues / Tasks | Dependencies | Type |
|-------|------|----------------|--------------|------|
| PG-1 | flow-commands-removal | Issue 1 (Tasks 1.1–1.7) | none | deep |
| PG-2 | git-infra-and-retained-updates | Issues 2–4 (Tasks 2.1–2.10, 3.1–3.8, 4.1–4.5) | PG-1 | wide |

### Execution Order

**PG-1** is merged first. It deletes the three flow command files, `FlowTransitionService`,
`FlowConfig`, all associated test files, and removes their registrations and examples.
After PG-1 merges, the build is clean and all remaining tests pass — there are no
references to flow types anywhere in retained code.

**PG-2** follows. With `IGitService` and `FlowTransitionService` already gone, the
`StatusCommand`, `HintEngine`, and DI modules can be updated without ambiguity. PG-2
deletes the 10 git command files, the entire `src/Twig.Infrastructure/Git/` directory,
and domain services (`IGitService`, `CommitMessageService`, `BranchNamingService`), then
updates retained commands and runs final build/test verification. The wide classification
reflects the mechanical, cross-layer nature of these deletions rather than any deep
logic change.

### Self-Containment Verification

**PG-1**
- Deletes only flow-specific types (`FlowStartCommand`, `FlowDoneCommand`,
  `FlowCloseCommand`, `FlowTransitionService`, `FlowConfig`).
- No retained command references these types — the call-site audit confirms
  `FlowTransitionService` is only wired through `AddFlowCommands()` and
  `CommandServiceModule`, both of which are removed in the same PR.
- Build passes after PG-1: ✅ self-contained.

**PG-2**
- Depends on PG-1 being merged (flow types already gone).
- Removes git infrastructure and updates `StatusCommand` / `HintEngine` in one sweep.
- `IAdoGitService`, `AdoGitClient`, `BranchLinkService` are explicitly retained;
  no references to deleted types remain after the PR.
- Final tasks (4.1–4.5) confirm `dotnet build` and `dotnet test` are green.
- Build passes after PG-2: ✅ self-contained.


