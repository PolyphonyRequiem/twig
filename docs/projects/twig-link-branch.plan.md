# twig link branch — Link Existing Branch to Work Item

| Field | Value |
|-------|-------|
| **Work Item** | #2015 |
| **Type** | Issue |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

Add a `twig link branch` CLI command and `twig_link_branch` MCP tool that link an existing git branch to a work item as an ADO artifact link, without creating or checking out the branch. The branch-linking logic currently embedded in `BranchCommand.cs` (lines 77–101) will be extracted into a shared `BranchLinkService` in the Domain layer, enabling consistent reuse across CLI, MCP, and future consumers. The CLI command supports active work item context with `--id` override; the MCP tool follows the same `WorkspaceResolver` pattern used by existing tools.

## Background

### Current State

The `twig branch` command (`BranchCommand.cs`) generates a branch name from the active work item, creates/checks out the branch via `IGitService`, and optionally adds an `ArtifactLink` (type `"Branch"`) to the ADO work item via `IAdoGitService.AddArtifactLinkAsync`. The linking logic (lines 77–101) is tightly coupled to the branch creation flow:

```
projectId ← IAdoGitService.GetProjectIdAsync()
repoId    ← IAdoGitService.GetRepositoryIdAsync()
URI       = vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}
revision  ← IAdoWorkItemService.FetchAsync(itemId)
           → IAdoGitService.AddArtifactLinkAsync(itemId, URI, "ArtifactLink", revision, "Branch")
```

The `twig link` command group currently handles parent–child hierarchy links (`parent`, `unparent`, `reparent`) via `LinkCommand.cs`, using `IAdoWorkItemService.AddLinkAsync`. Branch artifact linking is a fundamentally different operation (work item → git artifact vs. work item → work item) but fits naturally as a subcommand in the `twig link` namespace.

### Motivation

The conductor workflow creates planning branches (`planning/61937848`) and implementation branches (`conductor/61937848/pg-1-...`) via `git worktree add` or `git checkout -b`. It then needs to link these branches to the relevant work items. Today, `twig branch` creates AND checks out a branch — we need to link an already-existing branch without side effects.

### MCP Gap: IAdoGitService Not in WorkspaceContext

The MCP server's `WorkspaceContext` (line 14–98 in `WorkspaceContext.cs`) bundles per-workspace services but does **not** include `IAdoGitService`. The `WorkspaceContextFactory` creates `AdoRestClient` (for `IAdoWorkItemService`) but not `AdoGitClient`. To support `twig_link_branch` in MCP, `IAdoGitService?` must be added to the workspace context, conditional on `config.Git.Repository` being configured.

### Call-Site Audit: `IAdoGitService.AddArtifactLinkAsync`

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/BranchCommand.cs:93-94` | `ExecuteAsync` | Links newly-created branch to active work item (best-effort, wrapped in try/catch) | Will be refactored to use `BranchLinkService` — no behavior change |

This is the **only** call site. The change adds two new consumers (LinkBranchCommand, MCP CreationTools).

### Call-Site Audit: `IAdoGitService` Injection (via `GetService<IAdoGitService>()`)

| File | Line(s) | Command | Notes |
|------|---------|---------|-------|
| `CommandRegistrationModule.cs` | 99 | BranchCommand | Will gain BranchLinkService instead |
| `CommandRegistrationModule.cs` | 108 | CommitCommand | Unchanged |
| `CommandRegistrationModule.cs` | 116 | PrCommand | Unchanged |
| `CommandRegistrationModule.cs` | 146 | GitContextCommand | Unchanged |
| `CommandRegistrationModule.cs` | 184 | FlowDoneCommand | Unchanged |
| `CommandRegistrationModule.cs` | 197 | FlowCloseCommand | Unchanged |

## Problem Statement

1. **No standalone branch linking**: The only way to create a branch artifact link is through `twig branch`, which also creates/checks out the branch — unacceptable for workflows that manage branches independently.
2. **Inline logic not reusable**: The artifact link construction logic (URI format, revision fetch, API call) is embedded in `BranchCommand.ExecuteAsync`, preventing reuse from MCP or other command paths.
3. **MCP has no git service access**: `WorkspaceContext` lacks `IAdoGitService`, so MCP tools cannot create artifact links at all.

## Goals

1. **Standalone branch linking**: `twig link branch <name>` links an existing branch to the active work item without creating or checking out the branch.
2. **Shared service**: Extract branch artifact link logic into `BranchLinkService` for consistent reuse.
3. **MCP parity**: Expose `twig_link_branch` MCP tool with equivalent functionality.
4. **--id override**: Support targeting a specific work item via `--id` flag (CLI) or `id` parameter (MCP).
5. **Branch validation**: Validate the branch exists locally before linking (CLI only; MCP skips since no `IGitService`).

## Non-Goals

- Branch creation or checkout (use `twig branch`)
- Remote-only branch validation (local check is sufficient; ADO doesn't validate server-side)
- Modifying existing `twig branch` behavior beyond refactoring to use the shared service
- Changing other commands that inject `IAdoGitService`

## Requirements

### Functional

1. **FR-1**: `twig link branch <branch-name>` creates an `ArtifactLink` of type `"Branch"` on the active (or `--id`-specified) work item, using the `vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}` artifact URI format.
2. **FR-2**: The command validates the branch exists locally via `IGitService.BranchExistsAsync` before linking. If the branch does not exist, it exits with a clear error.
3. **FR-3**: The `--id <id>` flag allows targeting a specific work item instead of the active context.
4. **FR-4**: Output supports `human`, `json`, `jsonc`, and `minimal` formats via `--output` / `-o`.
5. **FR-5**: The `twig_link_branch` MCP tool provides equivalent functionality, resolving workspace via `WorkspaceResolver.TryResolve`.
6. **FR-6**: `BranchCommand` is refactored to use the shared `BranchLinkService`, with no behavior change.

### Non-Functional

1. **NFR-1**: AOT-compatible — no reflection, all serializable types registered in `TwigJsonContext`.
2. **NFR-2**: No new serializable types required — `BranchLinkService` returns tuples; MCP formatting uses `Utf8JsonWriter` directly.
3. **NFR-3**: `IAdoGitService` remains optional (`GetService<IAdoGitService>()`) — null when git project/repo not configured.
4. **NFR-4**: Errors written to stderr (CLI) or returned via `McpResultBuilder.ToError` (MCP).

## Proposed Design

### Architecture Overview

```
┌─────────────┐     ┌──────────────────┐     ┌───────────────────┐
│ twig link    │────▶│ BranchLinkService │────▶│ IAdoGitService    │
│ branch CLI   │     │ (Domain/Services) │     │ (AddArtifactLink) │
├─────────────┤     │                    │     ├───────────────────┤
│ twig_link_   │────▶│ LinkBranchAsync() │────▶│ IAdoWorkItemSvc   │
│ branch MCP   │     └──────────────────┘     │ (FetchAsync for   │
├─────────────┤                                │  revision)        │
│ twig branch  │──(refactored)──────────▶      └───────────────────┘
│ (existing)   │
└─────────────┘
```

Three consumers share `BranchLinkService`:
1. **LinkBranchCommand** (new) — CLI command for `twig link branch`
2. **CreationTools.LinkBranch** (new) — MCP tool for `twig_link_branch`
3. **BranchCommand** (refactored) — existing `twig branch` command

### Key Components

#### 1. `BranchLinkService` (Domain/Services)

A sealed class encapsulating the branch artifact link creation logic extracted from `BranchCommand`:

```csharp
public sealed class BranchLinkService(
    IAdoGitService adoGitService,
    IAdoWorkItemService adoService)
{
    public async Task<(bool Linked, string? ArtifactUri, string? Error)> LinkBranchAsync(
        int workItemId, string branchName, CancellationToken ct = default)
    {
        var projectId = await adoGitService.GetProjectIdAsync(ct);
        var repoId = await adoGitService.GetRepositoryIdAsync(ct);

        if (projectId is null || repoId is null)
            return (false, null, "Git project or repository ID could not be resolved from ADO.");

        var encodedBranch = Uri.EscapeDataString(branchName);
        var artifactUri = $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}";

        var remote = await adoService.FetchAsync(workItemId, ct);
        await adoGitService.AddArtifactLinkAsync(
            workItemId, artifactUri, "ArtifactLink", remote.Revision, "Branch", ct);

        return (true, artifactUri, null);
    }
}
```

**Design decisions**:
- Returns a tuple rather than a discriminated union — the operation has exactly two outcomes (success with URI, or failure with reason), and callers handle both inline.
- Takes `branchName` as raw short name (e.g. `"feature/123-test"`), not a full ref — consistent with `BranchCommand` usage.
- Does not validate branch existence — that's the caller's responsibility (CLI validates via `IGitService`, MCP skips).

#### 2. `LinkBranchCommand` (Twig/Commands)

A sealed command class following the established command pattern:

```csharp
public sealed class LinkBranchCommand(
    ActiveItemResolver activeItemResolver,
    BranchLinkService branchLinkService,
    OutputFormatterFactory formatterFactory,
    IGitService? gitService = null,
    TextWriter? stderr = null)
```

**Key behaviors**:
- Resolves work item via `--id` or active context (same pattern as `UpdateCommand`)
- Validates git repo via `GitGuard.EnsureGitRepoAsync`
- Validates branch exists via `IGitService.BranchExistsAsync`
- Delegates linking to `BranchLinkService.LinkBranchAsync`
- Outputs human/json/minimal formats

#### 3. MCP Tool: `twig_link_branch` (CreationTools)

Added to the existing `CreationTools` class (which already hosts `twig_link` for work-item links):

```csharp
[McpServerTool(Name = "twig_link_branch"),
 Description("Link an existing git branch to a work item as an ADO artifact link")]
public async Task<CallToolResult> LinkBranch(
    [Description("Branch name (short form, e.g. 'feature/123-test')")] string branchName,
    [Description("Work item ID. When omitted, uses the active work item.")] int? id = null,
    [Description("Target workspace...")] string? workspace = null,
    CancellationToken ct = default)
```

**Key behaviors**:
- Resolves workspace via `WorkspaceResolver.TryResolve`
- Resolves work item via explicit `id` or active context
- Requires `IAdoGitService` on the workspace context (returns error if not configured)
- No branch existence validation (no `IGitService` in MCP)
- Returns `FormatBranchLinked` result

#### 4. `WorkspaceContext` Extension

Add `IAdoGitService?` as a nullable property:

```csharp
public IAdoGitService? AdoGitService { get; }
```

`WorkspaceContextFactory` conditionally creates `AdoGitClient` when `config.Git.Repository` is configured (same conditional pattern used in `NetworkServiceModule`).

### Data Flow

**CLI (`twig link branch feature/my-branch --id 12345`)**:
1. ConsoleAppFramework routes to `TwigCommands.LinkBranch`
2. Resolves `LinkBranchCommand` from DI
3. `LinkBranchCommand.ExecuteAsync`:
   a. Resolves work item via `--id 12345` using `ActiveItemResolver.ResolveByIdAsync`
   b. Validates git repo via `GitGuard.EnsureGitRepoAsync`
   c. Validates `feature/my-branch` exists via `IGitService.BranchExistsAsync`
   d. Calls `BranchLinkService.LinkBranchAsync(12345, "feature/my-branch")`
   e. Service fetches project/repo IDs, constructs URI, fetches revision, adds artifact link
   f. Outputs success/failure message

**MCP (`twig_link_branch`)**:
1. MCP server routes to `CreationTools.LinkBranch`
2. Resolves workspace via `WorkspaceResolver.TryResolve`
3. Resolves work item via explicit `id` or active context
4. Validates `WorkspaceContext.AdoGitService` is not null
5. Creates `BranchLinkService` with workspace services
6. Calls `LinkBranchAsync`
7. Returns `FormatBranchLinked` result

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Separate command class vs. extending `LinkCommand` | Separate `LinkBranchCommand` | `LinkCommand` handles hierarchy links via `IAdoWorkItemService.AddLinkAsync`; branch linking uses `IAdoGitService.AddArtifactLinkAsync` — different dependencies and different ADO API |
| Service return type | Tuple `(bool, string?, string?)` | Only two outcomes; avoids creating new types for a simple operation |
| MCP tool name | `twig_link_branch` (not extending `twig_link`) | `twig_link` takes `sourceId/targetId/linkType` for work-item relationships; branch linking is work-item → git-artifact, different semantics |
| Branch validation location | CLI command, not service | `IGitService` unavailable in MCP context; validation is a UI concern, not a domain concern |
| `IAdoGitService` in `WorkspaceContext` | Nullable optional property | Mirrors CLI pattern where `IAdoGitService` is `null` when git project/repo not configured |

## Dependencies

### External
- `IAdoGitService` / `AdoGitClient` — existing ADO Git REST API wrapper (api-version 7.1)
- `IAdoWorkItemService` / `AdoRestClient` — for fetching work item revision

### Internal
- `ActiveItemResolver` — active work item resolution pattern
- `OutputFormatterFactory` — multi-format output
- `GitGuard` — git repo validation
- `WorkspaceResolver` / `WorkspaceContext` — MCP workspace resolution

### Sequencing
- Task 1 (BranchLinkService) must complete before Task 2 (LinkBranchCommand), Task 3 (BranchCommand refactor), and Task 4 (MCP tool)
- Tasks 2, 3, 4 are independent of each other after Task 1

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `twig link branch` error if the branch is already linked to the work item, or silently succeed? | Low | ADO will return a 409 Conflict if the relation already exists. The current `BranchCommand` swallows all exceptions (best-effort). For the standalone command, a clear error message for duplicate links is better UX. Can handle by catching `AdoConflictException`. |
| 2 | Should the MCP tool validate `config.Git.Repository` is set, or attempt auto-detection? | Low | The CLI auto-detects via git remote URL during startup. MCP has no git CLI access but has config. Using config values only is consistent with the MCP server's stateless, no-git-CLI design. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/BranchLinkService.cs` | Shared service encapsulating branch artifact link creation |
| `src/Twig/Commands/LinkBranchCommand.cs` | CLI command implementing `twig link branch` |
| `tests/Twig.Domain.Tests/Services/BranchLinkServiceTests.cs` | Unit tests for BranchLinkService |
| `tests/Twig.Cli.Tests/Commands/LinkBranchCommandTests.cs` | Unit tests for LinkBranchCommand |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/BranchCommand.cs` | Replace inline artifact link logic (lines 77–101) with `BranchLinkService.LinkBranchAsync` call |
| `src/Twig/Program.cs` | Register `[Command("link branch")]` routing to `LinkBranchCommand` |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Add DI registration for `LinkBranchCommand`; update `BranchCommand` registration to inject `BranchLinkService` |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Register `BranchLinkService` as singleton |
| `src/Twig.Mcp/Services/WorkspaceContext.cs` | Add `IAdoGitService?` property and constructor parameter |
| `src/Twig.Mcp/Services/WorkspaceContextFactory.cs` | Conditionally create `AdoGitClient` when `config.Git.Repository` is configured |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Add `twig_link_branch` MCP tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatBranchLinked` method |

## ADO Work Item Structure

### Issue #2019: Extract branch linking service and register twig link branch command + MCP tool

**Goal**: Extract the branch artifact link logic from `BranchCommand.cs` into a shared `BranchLinkService`. Register `twig link branch` CLI command and `twig_link_branch` MCP tool. Validate branch exists before linking. Support `--id` override. Add unit tests.

**Prerequisites**: None (this is the only Issue under #2015)

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | **Create BranchLinkService domain service**: Extract artifact link logic from `BranchCommand.cs` into `BranchLinkService` in `Twig.Domain/Services/`. Register as singleton in `CommandServiceModule.cs`. Add unit tests covering happy path, null project/repo ID, and fetch failure. | `src/Twig.Domain/Services/BranchLinkService.cs`, `src/Twig/DependencyInjection/CommandServiceModule.cs`, `tests/Twig.Domain.Tests/Services/BranchLinkServiceTests.cs` | ~170 LoC |
| T2 | **Create LinkBranchCommand + CLI registration**: New `LinkBranchCommand` sealed class with `ExecuteAsync(branchName, id?, outputFormat)`. Uses `ActiveItemResolver` for --id resolution, `GitGuard` for git validation, `IGitService.BranchExistsAsync` for branch validation, and `BranchLinkService` for linking. Register `[Command("link branch")]` in `Program.cs` and DI in `CommandRegistrationModule.cs`. Add unit tests covering: happy path, --id override, no active item, branch not found, git unavailable, link failure. | `src/Twig/Commands/LinkBranchCommand.cs`, `src/Twig/Program.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs`, `tests/Twig.Cli.Tests/Commands/LinkBranchCommandTests.cs` | ~350 LoC |
| T3 | **Refactor BranchCommand to use BranchLinkService**: Replace inline linking logic (lines 77–101) with `BranchLinkService.LinkBranchAsync` call. Update DI registration to inject `BranchLinkService` instead of raw `IAdoGitService` for the linking path. Verify all existing `BranchCommandTests` pass without modification. | `src/Twig/Commands/BranchCommand.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | ~40 LoC |
| T4 | **Add IAdoGitService to MCP WorkspaceContext + twig_link_branch tool**: Add `IAdoGitService?` property to `WorkspaceContext`. Create `AdoGitClient` conditionally in `WorkspaceContextFactory` when `config.Git.Repository` is set. Add `twig_link_branch` tool to `CreationTools.cs` with `branchName` (required), `id` (optional), and `workspace` (optional) parameters. Add `FormatBranchLinked` to `McpResultBuilder`. | `src/Twig.Mcp/Services/WorkspaceContext.cs`, `src/Twig.Mcp/Services/WorkspaceContextFactory.cs`, `src/Twig.Mcp/Tools/CreationTools.cs`, `src/Twig.Mcp/Services/McpResultBuilder.cs` | ~120 LoC |

**Acceptance Criteria**:
- `twig link branch <name>` links an existing branch to the active work item without creating or checking out the branch
- `twig link branch <name> --id <id>` links to a specific work item
- Branch existence is validated locally before linking (CLI)
- `twig_link_branch` MCP tool provides equivalent functionality
- `BranchCommand` is refactored to use `BranchLinkService` with no behavior change
- All new code has unit tests; all existing tests pass
- AOT-compatible (no new serializable types needed)

## PR Groups

| PG | Name | Tasks | Classification | Est. LoC | Est. Files | Description |
|----|------|-------|----------------|----------|------------|-------------|
| PG-1 | Branch linking service + CLI + MCP | T1, T2, T3, T4 | Deep | ~680 | 12 | All tasks form a cohesive feature: shared service, CLI command, BranchCommand refactor, and MCP tool. Well under the 2000 LoC / 50 file guardrails. |

**Rationale for single PG**: Total estimated LoC (~680 including tests) is well under the 2000 LoC limit. The changes are tightly coupled — the service is useless without consumers, and the consumers require the service. Splitting would create a PG that can't be validated in isolation. A single reviewer can trace the data flow from service → CLI → MCP in one pass.

**Execution order**: T1 → T2 ∥ T3 ∥ T4 (T1 is prerequisite; T2, T3, T4 are parallelizable after T1)
