# Twig Git Integration — Solution Design & Implementation Plan

> **Revision notes:** Initial draft.

---

## Executive Summary

This document proposes a bidirectional Git integration layer for the Twig CLI, a .NET 9 Native AOT tool for Azure DevOps work item management. The integration covers two directions: (A) enriching Azure DevOps work items with git context — auto-linking branches/commits/PRs, branch creation from work items, commit message enrichment, and PR creation; and (B) improving git workflows with Twig awareness — git hooks, branch-to-context tracking, worktree support, stash integration, merge awareness, and log enrichment. The design shells out to the `git` CLI for all git operations (avoiding libgit2sharp due to AOT incompatibility), adds a new `IGitService` domain interface with a `GitCliService` infrastructure implementation, extends `TwigConfiguration` with a `git` section, and introduces ~10 new CLI commands across 6 implementation epics. All git features are additive and opt-in — they never break normal git workflows.

---

## Background

### Current State

Twig is a .NET 9 Native AOT CLI built on ConsoleAppFramework v5.7.13. It operates on Azure DevOps (ADO) work items through a local SQLite cache stored in `.twig/` at the repository root. The architecture follows a clean three-layer structure:

- **`Twig` (CLI layer):** Commands, formatters, hints, and the `Program.cs` DI composition root.
- **`Twig.Domain`:** Aggregates (`WorkItem`), value objects, interfaces, enums, services (state transitions, conflict resolution).
- **`Twig.Infrastructure`:** ADO REST client (`AdoRestClient`), SQLite persistence, configuration (`TwigConfiguration`, `TwigPaths`), authentication, and source-generated JSON serialization (`TwigJsonContext`).

Key conventions observed in the codebase:

| Convention | Evidence |
|---|---|
| Commands are standalone classes with `ExecuteAsync` returning `Task<int>` | `StatusCommand`, `StateCommand`, `SetCommand`, etc. |
| DI is composed in `Program.cs` with `AddSingleton` registrations | Lines 15–127 of `Program.cs` |
| Commands are routed via a single `TwigCommands` facade class | Lines 256–332 of `Program.cs` |
| Output goes through `IOutputFormatter` (human/json/minimal) | `OutputFormatterFactory` pattern |
| Contextual hints via `HintEngine` | Post-command hint system |
| Configuration is a JSON POCO at `.twig/config` | `TwigConfiguration` class |
| ADO API uses version 7.1 with source-generated JSON | `AdoRestClient`, `TwigJsonContext` |
| All serialization is AOT-compatible (no reflection) | `JsonSerializerIsReflectionEnabledByDefault=false` |
| Domain interfaces decouple infrastructure | `IAdoWorkItemService`, `IContextStore`, etc. |

### Context and Motivation

Users work in git repositories and frequently switch between code and work item management. Currently, Twig has no awareness of git — creating branches, writing commit messages with work item IDs, linking branches to ADO work items, and creating PRs are all manual steps. This creates friction and inconsistency. ADO's "Development" section on work items remains empty unless users manually link artifacts. Twig is uniquely positioned to bridge this gap because it already knows the ADO org/project/team context and the active work item.

### Prior Art in Codebase

- `InitCommand.AppendToGitignore()` — already interacts with `.gitignore`, showing git-awareness precedent.
- `AdoRestClient` — already uses ADO REST API v7.1 for work item CRUD, including relation links (parent-child via `AdoPatchOperation`).
- `AdoResponseMapper.MapSeedToCreatePayload` — demonstrates the `relations/-` patch operation pattern needed for artifact links.
- `IContextStore` — key-value store in SQLite can be extended to track branch↔work-item mappings.

---

## Problem Statement

1. **No git↔ADO linkage:** Branch names, commits, and PRs are not automatically linked to ADO work items. The ADO "Development" panel for work items remains empty, reducing traceability and visibility.

2. **Manual branch naming:** Users must manually construct branch names that include work item IDs, leading to inconsistent naming across the team.

3. **Manual commit message formatting:** No enforcement or assistance for including work item references in commit messages.

4. **Context switching friction:** When switching branches, the user must manually run `twig set <id>` to match the Twig context to the new branch's work item.

5. **No PR automation:** Creating a PR requires going to the ADO web UI or using `az repos pr create` with manual argument construction.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|---|---|---|
| G-1 | Auto-link branches, commits, and PRs to ADO work items | ADO "Development" section populated when using Twig git commands |
| G-2 | Create branches from work item context with configurable naming | `twig branch` produces consistently named branches |
| G-3 | Enrich commit messages with work item context | `twig commit` auto-prefixes with work item ID |
| G-4 | Create PRs from CLI with work item linkage | `twig pr` creates linked ADO PR |
| G-5 | Auto-detect work item from branch name on checkout | Context switches automatically with branch |
| G-6 | Provide opt-in git hooks for teams wanting enforcement | `twig hooks install` installs hooks |
| G-7 | Maintain Native AOT compatibility | No libgit2sharp; shell out to `git` CLI |
| G-8 | Preserve normal git workflows | All enrichment is additive; git commands work unmodified |

### Non-Goals

| ID | Non-Goal | Rationale |
|---|---|---|
| NG-1 | Replace git CLI | Twig wraps/enriches git, does not replace it |
| NG-2 | Support GitHub (non-ADO) | Twig is ADO-focused; GitHub integration is a separate initiative |
| NG-3 | Full merge conflict resolution UI | Git's existing tooling handles this well |
| NG-4 | CI/CD pipeline integration | Out of scope — focus is on local developer workflow |
| NG-5 | Support TFVC repositories | Twig targets git repositories only |
| NG-6 | libgit2sharp integration | Incompatible with Native AOT (see Alternatives Considered) |

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|---|---|---|
| FR-001 | `twig branch` creates a git branch named from the active work item using a configurable pattern | High |
| FR-002 | `twig branch` auto-transitions work item to "Active"/"In Progress" state | High |
| FR-003 | `twig branch` adds an ArtifactLink (Branch type) to the ADO work item | High |
| FR-004 | `twig commit` wraps `git commit` and auto-prefixes commit message with work item ID | High |
| FR-005 | `twig commit` adds an ArtifactLink (Commit type) to the ADO work item after commit | Medium |
| FR-006 | `twig pr` creates an ADO pull request via REST API, linked to the active work item | High |
| FR-007 | `twig pr` auto-populates PR title/description from work item fields | High |
| FR-008 | `twig pr` detects source/target branches and sets reviewers from config | Medium |
| FR-009 | `twig status` shows associated branches, PRs, and build status alongside work item state | Medium |
| FR-010 | `twig hooks install` installs git hooks (prepare-commit-msg, commit-msg, post-checkout) | High |
| FR-011 | `twig hooks uninstall` removes Twig-installed hooks without affecting other hooks | High |
| FR-012 | post-checkout hook auto-detects work item from branch name and updates Twig context | High |
| FR-013 | prepare-commit-msg hook auto-prefixes work item ID in commit messages | High |
| FR-014 | commit-msg hook validates that a work item ID reference exists | Medium |
| FR-015 | Branch naming patterns are configurable via `git.branchPattern` in `.twig/config` | High |
| FR-016 | Commit message templates are configurable via `git.commitTemplate` in `.twig/config` | High |
| FR-017 | `twig context` shows current work item + branch + PR linkage | Medium |
| FR-018 | Git worktrees are detected; each worktree can have its own Twig context | Low |
| FR-019 | `twig stash` wraps git stash with work item metadata in stash message | Low |
| FR-020 | `twig log` shows git log annotated with work item types and states | Low |

### Non-Functional Requirements

| ID | Requirement | Metric |
|---|---|---|
| NFR-001 | All git operations must shell out to `git` CLI (AOT compatible) | No P/Invoke to native git libraries |
| NFR-002 | Git commands must complete within 2s for local operations | Excludes network round-trips to ADO |
| NFR-003 | All new commands must support `--output human|json|minimal` | Consistent with existing command pattern |
| NFR-004 | Git hooks must be opt-in and uninstallable | `twig hooks install` / `twig hooks uninstall` |
| NFR-005 | Configuration additions must be backward-compatible | Missing `git.*` config keys use safe defaults |
| NFR-006 | All new DTOs must have `[JsonSerializable]` entries in `TwigJsonContext` | AOT serialization compliance |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                        Twig CLI Layer                            │
│  ┌──────────┐ ┌──────────┐ ┌────────┐ ┌───────┐ ┌────────────┐ │
│  │BranchCmd │ │CommitCmd │ │ PrCmd  │ │HooksCmd│ │ContextCmd  │ │
│  └────┬─────┘ └────┬─────┘ └───┬────┘ └───┬───┘ └─────┬──────┘ │
│       │            │           │           │           │        │
│  ┌────┴────────────┴───────────┴───────────┴───────────┴──────┐ │
│  │                    Twig.Domain                              │ │
│  │  ┌──────────────┐  ┌───────────────┐  ┌─────────────────┐  │ │
│  │  │ IGitService   │  │BranchPattern  │  │CommitTemplate   │  │ │
│  │  │ (interface)   │  │(value object) │  │(value object)   │  │ │
│  │  └──────┬───────┘  └───────────────┘  └─────────────────┘  │ │
│  └─────────┼──────────────────────────────────────────────────┘ │
│            │                                                    │
│  ┌─────────┼──────────────────────────────────────────────────┐ │
│  │         │       Twig.Infrastructure                        │ │
│  │  ┌──────┴───────┐  ┌───────────────┐  ┌─────────────────┐ │ │
│  │  │GitCliService │  │AdoGitClient   │  │ HookInstaller   │ │ │
│  │  │(shells out   │  │(ADO Git REST  │  │ (writes hook    │ │ │
│  │  │ to git CLI)  │  │ API for PRs,  │  │  scripts to     │ │ │
│  │  │              │  │ branches)     │  │  .git/hooks/)   │ │ │
│  │  └──────────────┘  └───────────────┘  └─────────────────┘ │ │
│  └────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `IGitService` (Domain Interface)

Abstracts local git operations. Lives in `Twig.Domain/Interfaces/`.

```csharp
public interface IGitService
{
    Task<string> GetCurrentBranchAsync(CancellationToken ct = default);
    Task<string> GetRepositoryRootAsync(CancellationToken ct = default);
    Task<bool> IsInsideWorkTreeAsync(CancellationToken ct = default);
    Task CreateBranchAsync(string branchName, CancellationToken ct = default);
    Task CheckoutAsync(string branchName, CancellationToken ct = default);
    Task<string> CommitAsync(string message, bool allowEmpty = false, CancellationToken ct = default);
    Task<string> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);
    Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default);
    Task<string> GetHeadCommitHashAsync(CancellationToken ct = default);
    Task StashAsync(string? message = null, CancellationToken ct = default);
    Task StashPopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLogAsync(int count = 20, string? format = null, CancellationToken ct = default);
    Task<string?> GetWorktreeRootAsync(CancellationToken ct = default);
}
```

#### 2. `GitCliService` (Infrastructure Implementation)

Implements `IGitService` by shelling out to `git` CLI via `System.Diagnostics.Process`. Lives in `Twig.Infrastructure/Git/`.

- Uses `ProcessStartInfo` with `RedirectStandardOutput` and `RedirectStandardError`.
- Parses stdout for return values.
- Throws descriptive exceptions on non-zero exit codes.
- Fully AOT-compatible — no P/Invoke, no reflection.

```csharp
internal sealed class GitCliService : IGitService
{
    private async Task<string> RunGitAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new GitOperationException($"git {arguments} failed: {error.Trim()}");
        }
        return output.Trim();
    }
    // ... method implementations delegate to RunGitAsync ...
}
```

#### 3. `IAdoGitService` (Domain Interface)

Abstracts ADO Git REST API operations (branches, PRs, artifact links). Lives in `Twig.Domain/Interfaces/`.

```csharp
public interface IAdoGitService
{
    Task AddArtifactLinkAsync(int workItemId, string artifactUri, string linkType, int revision, CancellationToken ct = default);
    Task<string> CreatePullRequestAsync(PullRequestCreate request, CancellationToken ct = default);
    Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForBranchAsync(string branchName, CancellationToken ct = default);
    Task<string?> GetRepositoryIdAsync(CancellationToken ct = default);
    Task<string?> GetProjectIdAsync(CancellationToken ct = default);
}
```

#### 4. `AdoGitClient` (Infrastructure Implementation)

Extends the ADO REST integration in `Twig.Infrastructure/Ado/`. Reuses `HttpClient` and `IAuthenticationProvider` patterns from `AdoRestClient`.

Key ADO artifact link formats:
- **Branch:** `vstfs:///Git/Ref/{ProjectId}/{RepoId}/GB{BranchName}`
- **Commit:** `vstfs:///Git/Commit/{ProjectId}/{RepoId}/{CommitHash}`
- **Pull Request:** `vstfs:///Git/PullRequestId/{ProjectId}/{PullRequestId}`

#### 5. `BranchNamingService` (Domain Service)

Generates branch names from work item context using configurable patterns. Lives in `Twig.Domain/Services/`.

```
Default pattern: {type}/{id}-{title-slug}
Examples:
  feature/12345-add-user-authentication
  bug/12346-fix-login-crash
  task/12347-update-readme
```

Slugification: lowercase, replace spaces/special chars with hyphens, truncate to 60 chars, trim trailing hyphens.

Work item type mapping: `User Story` → `feature`, `Bug` → `bug`, `Task` → `task`, `Epic` → `epic`.

#### 6. `CommitMessageService` (Domain Service)

Generates commit messages from work item context using configurable templates.

```
Default template: {type}(#{id}): {message}
Example: feat(#12345): add user authentication flow
```

Conventional commit mapping: `User Story` → `feat`, `Bug` → `fix`, `Task` → `chore`, `Epic` → `feat`.

#### 7. `HookInstaller` (Infrastructure)

Manages git hook scripts in `.git/hooks/`. Lives in `Twig.Infrastructure/Git/`.

- Writes shell scripts that invoke `twig` commands.
- Detects existing hooks and appends (does not overwrite).
- Supports `install` and `uninstall` operations.
- Hook scripts include a marker comment (`# twig-managed`) for safe uninstallation.

#### 8. Configuration Extension

`TwigConfiguration` gets a new `GitConfig` section:

```json
{
  "git": {
    "branchPattern": "{type}/{id}-{title-slug}",
    "commitTemplate": "{type}(#{id}): {message}",
    "defaultTargetBranch": "main",
    "autoLink": true,
    "autoTransition": true,
    "typeMap": {
      "User Story": "feature",
      "Bug": "bug",
      "Task": "task",
      "Epic": "epic"
    },
    "hooks": {
      "prepareCommitMsg": true,
      "commitMsg": true,
      "postCheckout": true
    }
  }
}
```

### Data Flow

#### `twig branch` — Create Branch from Work Item

```
User runs: twig branch

1. Get active work item from IContextStore → WorkItem (id=12345, type=Bug, title="Fix crash")
2. BranchNamingService.Generate(workItem, config.Git.BranchPattern)
   → "bug/12345-fix-crash"
3. IGitService.CreateBranchAsync("bug/12345-fix-crash")
   → git checkout -b bug/12345-fix-crash
4. IAdoGitService.GetRepositoryIdAsync() + GetProjectIdAsync()
   → Fetch repo/project GUIDs from ADO REST API
5. IAdoGitService.AddArtifactLinkAsync(12345,
     "vstfs:///Git/Ref/{projectId}/{repoId}/GBbug/12345-fix-crash",
     "Branch", revision)
   → PATCH _apis/wit/workitems/12345 with relation add
6. If config.Git.AutoTransition:
   → StateCommand.ExecuteAsync("c") // "Active"/"Committed"
7. Output: "Created branch bug/12345-fix-crash and linked to #12345"
```

#### `twig commit` — Commit with Work Item Context

```
User runs: twig commit "add validation logic"

1. Get active work item → WorkItem (id=12345, type=Bug)
2. CommitMessageService.Format(workItem, "add validation logic", config.Git.CommitTemplate)
   → "fix(#12345): add validation logic"
3. IGitService.CommitAsync("fix(#12345): add validation logic")
   → git commit -m "fix(#12345): add validation logic"
4. IGitService.GetHeadCommitHashAsync()
   → "abc123def456..."
5. If config.Git.AutoLink:
   → IAdoGitService.AddArtifactLinkAsync(12345,
       "vstfs:///Git/Commit/{projectId}/{repoId}/abc123def456",
       "Commit", revision)
6. Output: "Committed abc123d — fix(#12345): add validation logic"
```

#### `twig pr` — Create Pull Request

```
User runs: twig pr

1. Get active work item → WorkItem (id=12345)
2. IGitService.GetCurrentBranchAsync() → "bug/12345-fix-crash"
3. Determine target branch: config.Git.DefaultTargetBranch → "main"
4. Build PR title from work item: "Bug 12345: Fix crash"
5. Build PR description from work item fields (title, description, acceptance criteria)
6. IAdoGitService.CreatePullRequestAsync({
     sourceRefName: "refs/heads/bug/12345-fix-crash",
     targetRefName: "refs/heads/main",
     title: "Bug 12345: Fix crash",
     description: "...",
     workItemRefs: [{ id: 12345 }]
   })
7. IAdoGitService.AddArtifactLinkAsync(12345,
     "vstfs:///Git/PullRequestId/{projectId}/{prId}",
     "Pull Request", revision)
8. Output: "Created PR #42: Bug 12345: Fix crash → main"
```

#### post-checkout Hook — Auto-detect Context

```
Hook fires after: git checkout bug/12345-fix-crash

1. Hook script invokes: twig _hook post-checkout <old-ref> <new-ref> <branch-flag>
2. If branch-flag != 1, exit (not a branch switch)
3. IGitService.GetCurrentBranchAsync() → "bug/12345-fix-crash"
4. BranchNamingService.ExtractWorkItemId("bug/12345-fix-crash")
   → 12345 (regex: /(\d{3,})/)
5. IContextStore.SetActiveWorkItemIdAsync(12345)
6. Output to stderr: "Twig context → #12345 (Bug: Fix crash)"
```

### API Contracts

#### New CLI Commands

| Command | Arguments | Description |
|---|---|---|
| `twig branch` | `[name]` `[--no-link]` `[--no-transition]` | Create and checkout a branch from active work item |
| `twig commit` | `[message]` `[--no-link]` `[--amend]` `[-- <pathspec>...]` | Commit with work item context |
| `twig pr` | `[--target <branch>]` `[--title <title>]` `[--draft]` | Create an ADO pull request |
| `twig hooks install` | `[--force]` | Install git hooks |
| `twig hooks uninstall` | | Remove Twig-managed hooks |
| `twig context` | | Show current work item + branch + PR linkage |
| `twig stash` | `[message]` | Stash with work item context |
| `twig stash pop` | | Pop stash, restore context |
| `twig log` | `[--count <n>]` `[--work-item <id>]` | Git log annotated with work item info |

#### New ADO REST API Endpoints Used

| Operation | Method | Endpoint |
|---|---|---|
| Add artifact link | PATCH | `_apis/wit/workitems/{id}?api-version=7.1` |
| Create pull request | POST | `_apis/git/repositories/{repoId}/pullrequests?api-version=7.1` |
| List pull requests | GET | `_apis/git/repositories/{repoId}/pullrequests?searchCriteria.sourceRefName=...&api-version=7.1` |
| Get repository | GET | `_apis/git/repositories?api-version=7.1` |
| Get project | GET | `_apis/projects/{project}?api-version=7.1` |

### Design Decisions

| Decision | Rationale |
|---|---|
| **Shell out to `git` CLI instead of libgit2sharp** | libgit2sharp has known AOT incompatibilities (NativeLibrary resolution issues, diff operations fail under AOT). Shelling out to `git` is universally available, fully AOT-compatible, and matches the user's installed git version. |
| **Separate `IAdoGitService` from `IAdoWorkItemService`** | Single Responsibility — git-specific ADO operations (PRs, repos, branches) are a distinct concern from work item CRUD. Keeps `AdoRestClient` focused. |
| **Hooks are marker-commented scripts, not symlinks** | Symlinks have cross-platform issues (Windows requires privileges). Marker comments (`# twig-managed-start` / `# twig-managed-end`) allow coexistence with user-defined hooks and safe removal. |
| **Work item ID extraction uses broad regex `(\d{3,})`** | Covers all common branch naming patterns (`feature/12345-desc`, `bug/12345`, `12345-desc`). Configurable pattern allows override for teams with non-standard naming. |
| **`twig commit` wraps `git commit`, not `git add`** | Twig should not alter staging behavior. Users stage files with `git add` as normal, then use `twig commit` for the commit step only. |
| **ADO artifact links are added via work item PATCH (not commit message parsing)** | Explicit API calls are more reliable than depending on ADO's `#mention` parsing in commit messages. Both can coexist. |
| **Configuration is backward-compatible** | Missing `git` section in existing `.twig/config` files results in safe defaults (auto-link enabled, standard patterns). No migration needed. |

---

## Alternatives Considered

### Git Operations: libgit2sharp vs. Git CLI

| Aspect | libgit2sharp | Git CLI (shell out) |
|---|---|---|
| **AOT compatibility** | ❌ Known issues (NativeLibrary, diff NRE) | ✅ Fully compatible |
| **Single binary** | ❌ Requires native `.dll`/`.so` alongside | ✅ No additional binaries |
| **Performance** | ✅ In-process, fast | ✅ Fast enough (process start ~10ms) |
| **Feature coverage** | ✅ Full git API | ✅ Full git CLI coverage |
| **Maintenance** | ⚠️ Library updates, binding issues | ✅ Uses user's installed git |
| **Testing** | ✅ Mockable interface | ✅ Mockable via `IGitService` |

**Decision:** Git CLI. The AOT constraint is a hard blocker for libgit2sharp. Process startup overhead is negligible for a CLI tool that runs one command per invocation.

### Hook Management: Marker Comments vs. Husky-style

| Aspect | Marker Comments | Husky-style (`.husky/` directory) |
|---|---|---|
| **Simplicity** | ✅ Standard `.git/hooks/` | ⚠️ Requires `core.hooksPath` config |
| **Coexistence** | ✅ Can append to existing hooks | ❌ Takes over hook directory |
| **Team sharing** | ⚠️ Not committed (in `.git/`) | ✅ Committed to repo |
| **Uninstall** | ✅ Remove between markers | ⚠️ Must restore original `core.hooksPath` |

**Decision:** Marker comments in `.git/hooks/`. Simpler, works with existing hook setups, and `twig hooks install` is explicitly opt-in per developer.

### PR Creation: ADO REST API vs. `az repos pr create`

| Aspect | ADO REST API | `az repos pr create` |
|---|---|---|
| **Dependency** | ✅ Only needs HTTP (already have) | ❌ Requires Azure CLI installed |
| **Control** | ✅ Full JSON payload control | ⚠️ Limited by CLI flags |
| **Error handling** | ✅ Typed exceptions | ⚠️ Parse stderr |
| **Consistency** | ✅ Same auth as other ADO calls | ⚠️ Separate auth flow |

**Decision:** ADO REST API. Consistent with existing `AdoRestClient` patterns and avoids an external tool dependency.

---

## Dependencies

### External Dependencies

| Dependency | Type | Status | Notes |
|---|---|---|---|
| `git` CLI | Runtime | Required | Must be on `PATH`; verified at command invocation |
| ADO REST API v7.1 | Network | Existing | Already used by `AdoRestClient` |
| ADO Git API v7.1 | Network | New | Pull requests, repositories, branches |
| ADO Projects API v7.1 | Network | New | Get project ID for artifact URIs |

### Internal Dependencies

| Dependency | Component | Notes |
|---|---|---|
| `IAdoWorkItemService` | Existing | Used for work item PATCH (artifact links) |
| `IContextStore` | Existing | Active work item tracking |
| `IWorkItemRepository` | Existing | Cache lookups |
| `TwigConfiguration` | Existing | Extended with `GitConfig` section |
| `TwigJsonContext` | Existing | Extended with new DTO types |
| `OutputFormatterFactory` | Existing | New commands use same pattern |
| `HintEngine` | Existing | New hints for git commands |

### Sequencing Constraints

1. `IGitService` + `GitCliService` must be implemented before any git command.
2. `IAdoGitService` + `AdoGitClient` must be implemented before artifact linking and PR creation.
3. `BranchNamingService` must exist before `BranchCommand`.
4. Configuration extension must land before commands that read `git.*` config.
5. Hook management is independent and can be done in parallel with commands.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|---|---|
| `TwigConfiguration` | Add `GitConfig` class, add `Git` property, extend `SetValue` switch |
| `TwigJsonContext` | Add `[JsonSerializable]` entries for new DTOs |
| `TwigCommands` | Add routing methods for ~10 new commands |
| `Program.cs` | Add DI registrations for new services |
| `HintEngine` | Add hints for new commands |
| `IOutputFormatter` | May need new format methods for PR, branch, context output |
| `SqliteCacheStore` | No schema change needed (branch→work-item mapping uses `context` table) |

### Backward Compatibility

- **Configuration:** Existing `.twig/config` files without a `git` section continue to work with defaults. No migration needed.
- **SQLite schema:** No schema version bump required. Branch tracking uses the existing `context` key-value table.
- **Existing commands:** No changes to existing command behavior.
- **Git workflow:** All git enrichment is additive. Users who don't use `twig branch/commit/pr/hooks` are unaffected.

### Performance Implications

- `git` CLI invocations add ~10–30ms per process start. For single-command CLI invocations, this is negligible.
- ADO REST calls for artifact linking add one network round-trip (~100–500ms). This is acceptable and can be made optional via `--no-link`.
- No impact on existing Twig commands that don't use git features.

---

## Security Considerations

- **Git hooks:** Hooks execute arbitrary code. Twig hooks call `twig` commands (not arbitrary scripts). Users must explicitly opt in via `twig hooks install`. Hook scripts are plaintext and auditable in `.git/hooks/`.
- **Credential handling:** Git operations use the user's existing git credential manager. ADO API calls use the same `IAuthenticationProvider` (PAT or `az cli` token) as existing Twig commands. No new credential flows.
- **Process execution:** `GitCliService` runs `git` with hardcoded command names and parameterized arguments. No user input is passed to shell interpreters — `UseShellExecute = false` prevents command injection.
- **Artifact URIs:** `vstfs:///` URIs are constructed from ADO-provided project/repo IDs, not user input.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `git` not on PATH | Low | High | Check at command start; show clear error: "git is required. Install from https://git-scm.com" |
| ADO repo ID resolution fails (wrong remote URL) | Medium | Medium | Parse `git remote get-url origin`; support `dev.azure.com` and `visualstudio.com` URL formats |
| Branch name collisions | Low | Low | Check if branch exists before creating; offer suffix or abort |
| Existing hooks conflict | Medium | Medium | Marker-based append; never overwrite; `--force` flag for reinstall |
| ADO artifact link API format changes | Low | High | Pin to API version 7.1; `vstfs:///` format is stable since TFS 2015 |
| Work item ID extraction from branch name fails | Medium | Low | Regex fallback chain; configurable pattern; graceful degradation (skip auto-set) |
| Large commit message passthrough breaks | Low | Low | Use `--` separator for pathspecs; pass-through unknown flags to git |

---

## Open Questions

| ID | Question | Owner | Status |
|---|---|---|---|
| OQ-1 | Should `twig branch` support creating remote-tracking branches (push to origin)? | Team | Open |
| OQ-2 | Should `twig pr` support adding specific reviewers, or only use team defaults? | Team | Open |
| OQ-3 | How should `twig commit` handle interactive commit (`git commit` with no `-m`)? | Team | Open — proposal: open `$EDITOR` with pre-filled template |
| OQ-4 | Should hooks work with git worktrees (hook path resolution)? | Team | Open — git worktrees share `.git/hooks/` by default |
| OQ-5 | Should `twig branch` support a `--from <branch>` flag to branch from non-HEAD? | Team | Open |
| OQ-6 | What ADO repository should be used when multiple remotes exist? | Team | Open — proposal: use `origin` by default, configurable via `git.remote` |
| OQ-7 | Should artifact links be best-effort (ignore errors) or hard-fail? | Team | Open — proposal: best-effort with warning |

---

## Implementation Phases

### Phase 1: Foundation (Epic 1–2)
**Exit criteria:** `IGitService`, `IAdoGitService`, configuration extension, and `twig branch` are functional with tests.

### Phase 2: Core Commands (Epic 3–4)
**Exit criteria:** `twig commit`, `twig pr`, and `twig context` are functional with tests.

### Phase 3: Hooks & Enrichment (Epic 5–6)
**Exit criteria:** Git hooks install/uninstall, `twig status` shows git info, `twig log` and `twig stash` work.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Domain/Interfaces/IGitService.cs` | Domain interface for local git operations |
| `src/Twig.Domain/Interfaces/IAdoGitService.cs` | Domain interface for ADO Git REST API |
| `src/Twig.Domain/Services/BranchNamingService.cs` | Branch name generation from work item context |
| `src/Twig.Domain/Services/CommitMessageService.cs` | Commit message formatting from work item context |
| `src/Twig.Domain/Services/WorkItemIdExtractor.cs` | Extract work item IDs from branch names |
| `src/Twig.Domain/ValueObjects/BranchPattern.cs` | Value object for branch naming patterns |
| `src/Twig.Domain/ValueObjects/CommitTemplate.cs` | Value object for commit message templates |
| `src/Twig.Domain/ValueObjects/PullRequestCreate.cs` | DTO for PR creation parameters |
| `src/Twig.Domain/ValueObjects/PullRequestInfo.cs` | DTO for PR query results |
| `src/Twig.Infrastructure/Git/GitCliService.cs` | Git CLI shell-out implementation |
| `src/Twig.Infrastructure/Git/GitOperationException.cs` | Exception type for git operation failures |
| `src/Twig.Infrastructure/Git/HookInstaller.cs` | Git hook management (install/uninstall) |
| `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | ADO Git REST API client (PRs, repos, artifact links) |
| `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestRequest.cs` | DTO for PR creation request |
| `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestResponse.cs` | DTO for PR API response |
| `src/Twig.Infrastructure/Ado/Dtos/AdoRepositoryResponse.cs` | DTO for repository API response |
| `src/Twig.Infrastructure/Ado/Dtos/AdoProjectResponse.cs` | DTO for project API response |
| `src/Twig/Commands/BranchCommand.cs` | `twig branch` command implementation |
| `src/Twig/Commands/CommitCommand.cs` | `twig commit` command implementation |
| `src/Twig/Commands/PrCommand.cs` | `twig pr` command implementation |
| `src/Twig/Commands/HooksCommand.cs` | `twig hooks install/uninstall` command |
| `src/Twig/Commands/GitContextCommand.cs` | `twig context` command implementation |
| `src/Twig/Commands/StashCommand.cs` | `twig stash` / `twig stash pop` command |
| `src/Twig/Commands/LogCommand.cs` | `twig log` command implementation |
| `tests/Twig.Domain.Tests/Services/BranchNamingServiceTests.cs` | Unit tests for branch name generation |
| `tests/Twig.Domain.Tests/Services/CommitMessageServiceTests.cs` | Unit tests for commit message formatting |
| `tests/Twig.Domain.Tests/Services/WorkItemIdExtractorTests.cs` | Unit tests for ID extraction from branch names |
| `tests/Twig.Infrastructure.Tests/Git/GitCliServiceTests.cs` | Integration tests for git CLI wrapper |
| `tests/Twig.Infrastructure.Tests/Git/HookInstallerTests.cs` | Unit tests for hook install/uninstall |
| `tests/Twig.Cli.Tests/Commands/BranchCommandTests.cs` | Unit tests for branch command |
| `tests/Twig.Cli.Tests/Commands/CommitCommandTests.cs` | Unit tests for commit command |
| `tests/Twig.Cli.Tests/Commands/PrCommandTests.cs` | Unit tests for PR command |
| `tests/Twig.Cli.Tests/Commands/HooksCommandTests.cs` | Unit tests for hooks command |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `GitConfig` class and `Git` property; extend `SetValue` with `git.*` paths |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` entries for new DTO types |
| `src/Twig/Program.cs` | Add DI registrations for `IGitService`, `IAdoGitService`, `HookInstaller`, new commands; add routing in `TwigCommands` |
| `src/Twig/Hints/HintEngine.cs` | Add hints for `branch`, `commit`, `pr`, `hooks` commands |
| `src/Twig/Formatters/IOutputFormatter.cs` | Add format methods for PR info, branch info, context display (if needed) |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Implement new format methods |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Implement new format methods |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Implement new format methods |

### Deleted Files

| File Path | Reason |
|---|---|
| (none) | No files are deleted in this plan |

---

## Implementation Plan

### EPIC-001: Git Foundation — `IGitService` & `GitCliService` ✅ DONE

**Goal:** Establish the git operations abstraction layer and CLI implementation.

**Prerequisites:** None.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-001 | IMPL | Create `IGitService` interface with methods for branch, commit, checkout, remote URL, HEAD hash, worktree detection | `src/Twig.Domain/Interfaces/IGitService.cs` | DONE |
| ITEM-002 | IMPL | Create `GitOperationException` for git CLI failures | `src/Twig.Infrastructure/Git/GitOperationException.cs` | DONE |
| ITEM-003 | IMPL | Implement `GitCliService` that shells out to `git` CLI via `Process`. Include `RunGitAsync` helper, stdout/stderr capture, exit code handling | `src/Twig.Infrastructure/Git/GitCliService.cs` | DONE |
| ITEM-004 | TEST | Write integration tests for `GitCliService` (create temp repo, test branch/commit/checkout operations) | `tests/Twig.Infrastructure.Tests/Git/GitCliServiceTests.cs` | DONE |
| ITEM-005 | IMPL | Register `IGitService` → `GitCliService` in `Program.cs` DI container | `src/Twig/Program.cs` | DONE |

**Acceptance Criteria:**
- [x] `GitCliService` can create branches, commit, get current branch, and get HEAD hash in a test repository
- [x] Non-zero git exit codes throw `GitOperationException` with stderr message
- [x] `IGitService` is registered in DI and resolvable

**Completed:** 2026-03-17. All 5 review issues resolved: `GitOperationException(string, int, Exception)` constructor added; internal `GitCliService(string gitBinary)` constructor added to enable binary-not-found test path; `RunGitAsync`/`RunGitBoolAsync` use `_gitBinary` field; `Win32Exception` catch narrowed to `NativeErrorCode == 2 || 3` (ERROR_FILE_NOT_FOUND/ERROR_PATH_NOT_FOUND); CRLF assertion added to `GetLogAsync_ReturnsCommitEntries`; `RunGitAsync_ThrowsGitOperationException_WhenBinaryNotFound` and `GetConfigValue_ThrowsGitOperationException_WhenExitCodeIsNot1` tests added. Build clean (0 errors, 0 warnings), all 342 tests pass including all 22 GitCliService tests.

---

### EPIC-002: Configuration & Branch Naming ✅ DONE

**Goal:** Extend configuration with git settings and implement branch naming from work items.

**Prerequisites:** EPIC-001.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-006 | IMPL | Add `GitConfig` class with `BranchPattern`, `CommitTemplate`, `DefaultTargetBranch`, `AutoLink`, `AutoTransition`, `TypeMap`, `HooksConfig` properties. Add `Git` property to `TwigConfiguration`. Extend `SetValue` switch for `git.*` paths | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-007 | IMPL | Create `BranchNamingService` with `Generate(WorkItem, string pattern)` and `ExtractWorkItemId(string branchName)`. Implement slugification (lowercase, replace non-alphanumeric with hyphens, truncate, trim). Implement type mapping (User Story→feature, Bug→bug, etc.) | `src/Twig.Domain/Services/BranchNamingService.cs`, `src/Twig.Domain/Services/WorkItemIdExtractor.cs` | DONE |
| ITEM-008 | TEST | Unit tests for `BranchNamingService`: pattern substitution, slugification edge cases (unicode, long titles, special chars), type mapping | `tests/Twig.Domain.Tests/Services/BranchNamingServiceTests.cs` | DONE |
| ITEM-009 | TEST | Unit tests for `WorkItemIdExtractor`: various branch name formats (`feature/12345-desc`, `12345`, `bug/12345`, `users/name/12345-desc`), no-match cases | `tests/Twig.Domain.Tests/Services/WorkItemIdExtractorTests.cs` | DONE |
| ITEM-010 | IMPL | Add `[JsonSerializable(typeof(GitConfig))]` and related entries to `TwigJsonContext` | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |

**Acceptance Criteria:**
- [x] `GitConfig` defaults are applied when `git` section is missing from config JSON
- [x] Branch names are generated correctly for all standard work item types
- [x] Slugification handles unicode, long titles, and special characters
- [x] Work item IDs are extracted from all common branch naming patterns

**Completed:** 2026-03-17. All implementation and domain service tests pass. Added comprehensive infrastructure tests for `GitConfig` new properties (`CommitTemplate`, `AutoLink`, `AutoTransition`, `TypeMap`, `Hooks`): defaults validation, `SetValue` paths, full serialization round-trip. Fixed review issues: added missing SetValue tests for `git.committemplate`, `git.autolink`, `git.autotransition`; extended defaults and round-trip tests to cover all new properties; fixed case-sensitivity inconsistency in custom type map resolution; promoted `ResolveType` from internal to public. Build clean, all 570 domain tests and 353 infrastructure tests pass. Post-review fixes: corrected `GitConfig.TypeMap` XML comment (`Bug→fix` → `Bug→bug`); changed `GitConfig.BranchPattern` default to reference `BranchNameTemplate.DefaultPattern` constant (added `using Twig.Domain.ValueObjects`); changed `WorkItemIdExtractor.Extract` parameter to `string?` and added `IsNullOrEmpty` null guard; removed null-forgiving `!` operator from `WorkItemIdExtractorTests`.

---

### EPIC-003: `twig branch` Command & ADO Artifact Linking ✅ DONE

**Goal:** Implement the `twig branch` command with ADO artifact linking.

**Prerequisites:** EPIC-001, EPIC-002.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-011 | IMPL | Create `IAdoGitService` interface with `AddArtifactLinkAsync`, `CreatePullRequestAsync`, `GetPullRequestsForBranchAsync`, `GetRepositoryIdAsync`, `GetProjectIdAsync` | `src/Twig.Domain/Interfaces/IAdoGitService.cs` | DONE |
| ITEM-012 | IMPL | Create `PullRequestCreate` and `PullRequestInfo` value objects | `src/Twig.Domain/ValueObjects/PullRequestCreate.cs`, `src/Twig.Domain/ValueObjects/PullRequestInfo.cs` | DONE |
| ITEM-013 | IMPL | Implement `AdoGitClient`: repository/project ID lookup, artifact link PATCH (branch/commit/PR types), PR creation POST. Reuse `HttpClient` and `IAuthenticationProvider` patterns from `AdoRestClient` | `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | DONE |
| ITEM-014 | IMPL | Create ADO Git REST DTOs: `AdoPullRequestRequest`, `AdoPullRequestResponse`, `AdoRepositoryResponse`, `AdoProjectResponse`. Add `[JsonSerializable]` entries to `TwigJsonContext` | `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestRequest.cs`, `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestResponse.cs`, `src/Twig.Infrastructure/Ado/Dtos/AdoRepositoryResponse.cs`, `src/Twig.Infrastructure/Ado/Dtos/AdoProjectResponse.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |
| ITEM-015 | IMPL | Implement `BranchCommand`: get active work item, generate branch name, create branch via `IGitService`, add artifact link via `IAdoGitService`, optionally auto-transition state. Support `--no-link` and `--no-transition` flags | `src/Twig/Commands/BranchCommand.cs` | DONE |
| ITEM-016 | IMPL | Register `IAdoGitService` → `AdoGitClient` and `BranchCommand` in `Program.cs`. Add `Branch` method to `TwigCommands` | `src/Twig/Program.cs` | DONE |
| ITEM-017 | TEST | Unit tests for `BranchCommand`: mock `IGitService`, `IAdoGitService`, `IContextStore`. Test branch creation, artifact linking, auto-transition, `--no-link` flag | `tests/Twig.Cli.Tests/Commands/BranchCommandTests.cs` | DONE |
| ITEM-018 | IMPL | Add hints for `branch` command to `HintEngine` | `src/Twig/Hints/HintEngine.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig branch` creates a correctly named branch and checks it out
- [x] Branch is linked as an ArtifactLink on the ADO work item
- [x] Work item transitions to Active/In Progress when `autoTransition` is enabled
- [x] `--no-link` skips the ADO API call
- [x] Error when no active work item is set

**Completed:** 2026-03-17. Removed dead `GetArtifactLinkDisplayName` private static method from `AdoGitClient.cs` (display names are now passed explicitly via the `name` parameter). Fixed section comment banner mismatch in `BranchCommandTests.cs`. Registered `BranchCommand` in `Program.cs` DI container using `GetService<T>()` for optional `IGitService?` and `IAdoGitService?` dependencies, following the same pattern as `FlowDoneCommand`/`FlowCloseCommand`. Added `Branch` method to `TwigCommands` routing `twig branch` with `--no-link`, `--no-transition`, and `--output` parameters.

---

### EPIC-004: `twig commit` & `twig pr` Commands ✅ DONE

**Goal:** Implement commit enrichment and PR creation commands.

**Prerequisites:** EPIC-003.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-019 | IMPL | Create `CommitMessageService` with `Format(WorkItem, string userMessage, string template)`. Map work item types to conventional commit prefixes. Handle template substitution (`{type}`, `{id}`, `{message}`, `{title}`) | `src/Twig.Domain/Services/CommitMessageService.cs` | DONE |
| ITEM-020 | TEST | Unit tests for `CommitMessageService`: template substitution, conventional commit mapping, edge cases (empty message, no template) | `tests/Twig.Domain.Tests/Services/CommitMessageServiceTests.cs` | DONE |
| ITEM-021 | IMPL | Implement `CommitCommand`: get active work item, format commit message via `CommitMessageService`, pass remaining args to `git commit`, optionally add commit artifact link. Support `--no-link` and pass-through of `git commit` flags (`--amend`, `--`, pathspecs) | `src/Twig/Commands/CommitCommand.cs` | DONE |
| ITEM-022 | TEST | Unit tests for `CommitCommand`: mock `IGitService` and `IAdoGitService`. Test message formatting, artifact linking, `--no-link`, `--amend` pass-through | `tests/Twig.Cli.Tests/Commands/CommitCommandTests.cs` | DONE |
| ITEM-023 | IMPL | Implement `PrCommand`: get active work item, determine source/target branches, build PR title/description from work item, call `IAdoGitService.CreatePullRequestAsync`, add PR artifact link. Support `--target`, `--title`, `--draft` flags | `src/Twig/Commands/PrCommand.cs` | DONE |
| ITEM-024 | TEST | Unit tests for `PrCommand`: mock services. Test PR creation, work item linking, `--draft` flag, `--target` override | `tests/Twig.Cli.Tests/Commands/PrCommandTests.cs` | DONE |
| ITEM-025 | IMPL | Register `CommitCommand` and `PrCommand` in `Program.cs`. Add `Commit` and `Pr` methods to `TwigCommands` | `src/Twig/Program.cs` | DONE |
| ITEM-026 | IMPL | Add hints for `commit` and `pr` commands to `HintEngine` | `src/Twig/Hints/HintEngine.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig commit "message"` produces a commit with work-item-prefixed message
- [x] Commit hash is linked to ADO work item as artifact link
- [x] `twig pr` creates an ADO pull request linked to the active work item
- [x] PR title/description are populated from work item fields
- [x] `--draft` creates a draft PR

**Completed:** 2026-03-17. Added missing `output` parameter to `TwigCommands.Commit()` facade (placed before `params string[] passthrough` as required by C# syntax) and forwarded it as `outputFormat` to `CommitCommand.ExecuteAsync`, aligning with `TwigCommands.Pr()` which already exposed the parameter. Added `EmptyPassthroughArray_UsesSimpleCommit` test covering the edge case where `params string[]` produces `Array.Empty<string>()` rather than `null`, verifying the empty array correctly routes to `CommitAsync` (not `CommitWithArgsAsync`).

---

### EPIC-005: Git Hooks & Context Tracking

**Goal:** Implement opt-in git hooks and automatic context switching.

**Prerequisites:** EPIC-002.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-027 | IMPL | Implement `HookInstaller` with `Install(string gitDir, HooksConfig)` and `Uninstall(string gitDir)`. Write marker-commented shell scripts for `prepare-commit-msg`, `commit-msg`, `post-checkout`. Detect and preserve existing hook content. Cross-platform scripts (bash for macOS/Linux, batch/PowerShell for Windows) | `src/Twig.Infrastructure/Git/HookInstaller.cs` | DONE |
| ITEM-028 | TEST | Unit tests for `HookInstaller`: install to temp `.git/hooks/`, verify script content, verify marker comments, test uninstall removes only Twig sections, test coexistence with existing hooks | `tests/Twig.Infrastructure.Tests/Git/HookInstallerTests.cs` | DONE |
| ITEM-029 | IMPL | Implement `HooksCommand`: `install` subcommand calls `HookInstaller.Install`, `uninstall` subcommand calls `HookInstaller.Uninstall`. Detect `.git/` directory path via `IGitService` | `src/Twig/Commands/HooksCommand.cs` | DONE |
| ITEM-030 | TEST | Unit tests for `HooksCommand`: mock `HookInstaller` and `IGitService` | `tests/Twig.Cli.Tests/Commands/HooksCommandTests.cs` | DONE |
| ITEM-031 | IMPL | Implement `GitContextCommand` (`twig context`): show active work item, current branch, detect work item from branch name, show linked PRs (via `IAdoGitService.GetPullRequestsForBranchAsync`) | `src/Twig/Commands/GitContextCommand.cs` | DONE |
| ITEM-032 | IMPL | Implement internal `_hook` command for hook scripts to invoke. Handle `post-checkout` (extract work item ID from branch, set context), `prepare-commit-msg` (prefix message file), `commit-msg` (validate reference) | `src/Twig/Commands/HookHandlerCommand.cs` | DONE |
| ITEM-033 | IMPL | Register `HooksCommand`, `GitContextCommand`, `HookHandlerCommand` in `Program.cs`. Add routing to `TwigCommands` | `src/Twig/Program.cs` | DONE |
| ITEM-034 | IMPL | Add hints for `hooks` and `context` commands to `HintEngine` | `src/Twig/Hints/HintEngine.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig hooks install` writes hook scripts to `.git/hooks/` with `# twig-managed` markers
- [x] `twig hooks uninstall` removes only Twig-managed sections from hook files
- [x] Existing hook content is preserved during install
- [x] post-checkout hook auto-sets Twig context when switching to a branch with a work item ID
- [x] prepare-commit-msg hook prefixes commit messages with work item ID
- [x] `twig context` displays current branch ↔ work item ↔ PR linkage

**Completed:** 2026-03-17. Implemented HookInstaller with marker-delimited shell scripts (# twig-managed-start/end) for safe coexistence with user hooks. HooksCommand provides install/uninstall subcommands via IGitService for .git directory resolution. GitContextCommand shows branch/work item/PR linkage with human/json/minimal output. HookHandlerCommand handles post-checkout (auto-context), prepare-commit-msg (prefix), and commit-msg (validation) hook invocations. All commands registered in Program.cs with DI and TwigCommands routing. HintEngine updated with hooks/context hints. 25 tests passing (18 HookInstaller + 7 HooksCommand).

---

### EPIC-006: Status Enrichment & Secondary Commands ✅ DONE

**Goal:** Enrich `twig status` with git info and implement lower-priority git commands.

**Prerequisites:** EPIC-003, EPIC-005.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-035 | IMPL | Extend `StatusCommand` to show current branch, linked PRs (title + status), and build status when git context is available. Gracefully degrade when not in a git repo | `src/Twig/Commands/StatusCommand.cs` | DONE |
| ITEM-036 | IMPL | Implement `StashCommand` (`twig stash` / `twig stash pop`): wrap `git stash` with work item context in stash message, restore Twig context on pop | `src/Twig/Commands/StashCommand.cs` | DONE |
| ITEM-037 | IMPL | Implement `LogCommand` (`twig log`): parse git log, extract work item IDs from commit messages, annotate with work item type/state from cache. Support `--count` and `--work-item` filter flags | `src/Twig/Commands/LogCommand.cs` | DONE |
| ITEM-038 | IMPL | Add format methods to `IOutputFormatter` and implementations for git context display (branch info, PR info, annotated log entries) | `src/Twig/Formatters/IOutputFormatter.cs`, `src/Twig/Formatters/HumanOutputFormatter.cs`, `src/Twig/Formatters/JsonOutputFormatter.cs`, `src/Twig/Formatters/MinimalOutputFormatter.cs` | DONE |
| ITEM-039 | IMPL | Register `StashCommand` and `LogCommand` in `Program.cs`. Add routing to `TwigCommands` | `src/Twig/Program.cs` | DONE |
| ITEM-040 | TEST | Unit tests for status enrichment, stash command, and log command | `tests/Twig.Cli.Tests/Commands/StatusCommandGitTests.cs`, `tests/Twig.Cli.Tests/Commands/StashCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/LogCommandTests.cs` | DONE |

**Acceptance Criteria:**
- [x] `twig status` shows branch name and linked PR status alongside work item info
- [x] `twig stash` includes work item context in stash message
- [x] `twig log` annotates commits with work item type badges
- [x] `twig log --work-item 12345` filters to commits referencing that work item
- [x] All commands degrade gracefully when not in a git repo

**Completed:** 2026-03-17. Extended StatusCommand with IAdoGitService? optional dependency and WriteGitContextAsync helper (graceful degradation). Implemented StashCommand (twig stash / twig stash pop) wrapping git stash with work item context in message and restoring Twig context on pop. Implemented LogCommand parsing git log %H %s format, extracting work item IDs via #NNN and AB#NNN regex patterns, batch-looking up from cache, and annotating entries with type badges/state; supports --count and --work-item filters. Added FormatBranchInfo, FormatPrStatus, and FormatAnnotatedLogEntry to IOutputFormatter with Human (ANSI colors/badges), JSON (structured), and Minimal (compact) implementations. Registered StashCommand and LogCommand in Program.cs. 36 unit tests covering status enrichment, stash, and log commands.

---

## References

| Reference | URL |
|---|---|
| ADO REST API — Work Items (Patch/Relations) | https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update |
| ADO REST API — Git Pull Requests | https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests/create |
| ADO REST API — Git Refs (Branches) | https://learn.microsoft.com/en-us/rest/api/azure/devops/git/refs |
| ADO REST API — Git Repositories | https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories |
| ADO Link Type Reference | https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference |
| ADO Artifact Link URI Format | `vstfs:///Git/Ref/{ProjectId}/{RepoId}/GB{BranchName}` |
| libgit2sharp AOT Issues | https://github.com/libgit2/libgit2sharp/issues/2082, https://github.com/libgit2/libgit2sharp/issues/2160 |
| ConsoleAppFramework v5 | https://github.com/Cysharp/ConsoleAppFramework |
| Conventional Commits | https://www.conventionalcommits.org/ |
| Git Hooks Documentation | https://git-scm.com/docs/githooks |
