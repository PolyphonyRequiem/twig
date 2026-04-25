# twig seed publish --link-branch: Auto-link branch on publish

| Field | Value |
|-------|-------|
| **Work Item** | #2018 |
| **Type** | Issue |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

Add a `--link-branch <branch>` flag to `twig seed publish` that automatically creates
branch artifact links on every newly-created ADO work item during seed publication.
Today, the conductor workflow must iterate over every published seed and call artifact
link APIs individually — this flag collapses that N-call post-publish loop into a
single flag on the publish command. The implementation reuses the existing
`IAdoGitService.AddArtifactLinkAsync` infrastructure already proven in `BranchCommand`,
`CommitCommand`, and `PrCommand`. Linking is best-effort: failures emit a warning but
do not block seed publication.

---

## Background

### Current Architecture

Seed publication flows through two layers:

1. **`SeedPublishCommand`** (`src/Twig/Commands/SeedPublishCommand.cs`) — the CLI
   entry point that dispatches to the orchestrator, handles context updates, and
   formats output.
2. **`SeedPublishOrchestrator`** (`src/Twig.Domain/Services/SeedPublishOrchestrator.cs`)
   — the domain service that validates, creates in ADO, fetches back, runs
   transactional local updates, promotes seed links, and returns
   `SeedPublishResult`/`SeedPublishBatchResult`.

The orchestrator returns results containing `NewId` (the ADO-assigned ID) and
`Status` (Created, Skipped, DryRun, ValidationFailed, Error). The command layer
already iterates over these results to update active context.

### Existing Artifact Link Infrastructure

Three commands already create artifact links using the same pattern:

| Command | Artifact Type | URI Format | Link Name |
|---------|--------------|------------|-----------|
| `BranchCommand` | Branch | `vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}` | `"Branch"` |
| `CommitCommand` | Commit | `vstfs:///Git/Commit/{projectId}/{repoId}/{hash}` | `"Fixed in Commit"` |
| `PrCommand` | Pull Request | `vstfs:///Git/PullRequestId/{projectId}/{repoId}/{prId}` | `"Pull Request"` |

All three follow the same pattern:
1. Call `adoGitService.GetProjectIdAsync()` and `GetRepositoryIdAsync()` (cacheable)
2. Build the `vstfs:///` artifact URI
3. Call `adoService.FetchAsync(itemId)` to get the current revision for optimistic concurrency
4. Call `adoGitService.AddArtifactLinkAsync(itemId, uri, "ArtifactLink", revision, name)`
5. Wrap in try/catch for best-effort behavior

### Call-Site Audit for `AddArtifactLinkAsync`

| File | Method | Current Usage | Impact |
|------|--------|--------------|--------|
| `BranchCommand.cs:93` | `ExecuteAsync` | Links branch to active work item after checkout | None — independent command |
| `CommitCommand.cs:78` | `ExecuteAsync` | Links commit hash to active work item | None — independent command |
| `PrCommand.cs:107` | `ExecuteAsync` | Links PR to active work item | None — independent command |
| `AdoGitClient.cs:127` | `AddArtifactLinkAsync` | Implementation of `IAdoGitService` | None — called by new code |

No changes to existing call sites are required. The new code adds a new caller of the
same interface method.

### ConsoleAppFramework Command Wiring

Commands in Program.cs use this pattern:
```csharp
[Command("seed publish")]
public async Task<int> SeedPublish(
    [Argument] int? id = null, bool all = false, bool force = false,
    bool dryRun = false, string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default)
    => await services.GetRequiredService<SeedPublishCommand>()
        .ExecuteAsync(id, all, force, dryRun, output, ct);
```

New parameters are added as named optional parameters (ConsoleAppFramework source-gen
maps `linkBranch` to `--link-branch`).

---

## Problem Statement

The conductor workflow creates a planning branch, then seeds work items from a plan
document, then publishes all seeds. After publication, each work item should be linked
to the planning branch so ADO shows where the plan lives. Currently this requires
iterating over every published seed and calling `twig link branch` individually —
which is slow (N sequential HTTP calls) and verbose (N separate command invocations).

---

## Goals and Non-Goals

### Goals
1. A single `--link-branch <branch>` flag on `twig seed publish` links all published seeds to the specified branch
2. Branch validation (project ID + repo ID resolution) happens once upfront, not per-seed
3. Linking failures are best-effort — warn but don't block publication
4. Works with both `--all` (batch) and single-seed publish
5. Reuses existing `IAdoGitService.AddArtifactLinkAsync` — no new API plumbing
6. Dry-run mode skips linking (no API calls)

### Non-Goals
- Adding a standalone `twig link branch` subcommand (separate work item)
- Persisting the branch link configuration in twig.db
- Auto-detecting the current branch (user must specify explicitly)
- Supporting multiple branch links in a single publish
- Modifying the `SeedPublishOrchestrator` domain service (linking happens at command layer)

---

## Requirements

### Functional
- **FR-1:** `twig seed publish --all --link-branch <branch>` links all Created seeds to the branch
- **FR-2:** `twig seed publish <id> --link-branch <branch>` links the single Created seed
- **FR-3:** Branch name is a short name (e.g., `planning/61937848`), not a full ref
- **FR-4:** ProjectId and RepositoryId are resolved once before publish loop
- **FR-5:** If either ProjectId or RepositoryId is null (git service not configured), skip linking with a warning
- **FR-6:** Per-seed link failures produce a warning in output but do not change the exit code
- **FR-7:** `--dry-run` + `--link-branch` shows the branch in the plan but makes no link API calls
- **FR-8:** Skipped/Error seeds are not linked (only `SeedPublishStatus.Created`)
- **FR-9:** Output includes link status per-seed (linked count, failed count)

### Non-Functional
- **NFR-1:** No new NuGet dependencies
- **NFR-2:** AOT-compatible — no reflection
- **NFR-3:** Branch linking adds ≤1 extra HTTP call per seed (the `AddArtifactLinkAsync` call; revision comes from the orchestrator's post-publish refresh)
- **NFR-4:** All new code covered by unit tests

---

## Proposed Design

### Architecture Overview

The change is entirely in the **command layer** (`SeedPublishCommand`). The domain
orchestrator (`SeedPublishOrchestrator`) is unchanged — it already returns `NewId` and
`Status` on each result, which is all the command layer needs to decide whether to link.

```
┌──────────────────────────────────────────────────────┐
│  Program.cs  [Command("seed publish")]               │
│    --link-branch param → SeedPublishCommand          │
└───────────────────┬──────────────────────────────────┘
                    │
┌───────────────────▼──────────────────────────────────┐
│  SeedPublishCommand.ExecuteAsync(...)                 │
│  1. If linkBranch: validate upfront (projectId,       │
│     repoId, build artifactUri template)               │
│  2. Delegate to orchestrator (unchanged)              │
│  3. For each Created result: call AddArtifactLinkAsync│
│     best-effort, collect warnings                     │
│  4. Output results + link summary                     │
└───────────────────┬──────────────────────────────────┘
                    │
┌───────────────────▼──────────────────────────────────┐
│  SeedPublishOrchestrator (unchanged)                  │
│  - PublishAsync / PublishAllAsync                      │
│  - Returns SeedPublishResult with NewId, Status       │
└──────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `SeedPublishCommand` — Modified

**New constructor dependency:** `IAdoGitService?` and `IAdoWorkItemService` — both
already registered in DI, just need to be injected into this command.

**New parameter:** `string? linkBranch = null` — the short branch name.

**Upfront validation block** (before any publish calls):
```csharp
string? artifactUri = null;
if (linkBranch is not null && !dryRun)
{
    if (adoGitService is null)
    {
        Console.Error.WriteLine(fmt.FormatWarning(
            "--link-branch requires git service configuration. Skipping branch linking."));
    }
    else
    {
        var projectId = await adoGitService.GetProjectIdAsync();
        var repoId = await adoGitService.GetRepositoryIdAsync();
        if (projectId is not null && repoId is not null)
        {
            var encoded = Uri.EscapeDataString(linkBranch);
            artifactUri = $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encoded}";
        }
        else
        {
            Console.Error.WriteLine(fmt.FormatWarning(
                "Could not resolve project/repository IDs. Skipping branch linking."));
        }
    }
}
```

**Post-publish linking** (after each successful publish):
```csharp
if (artifactUri is not null && result.Status == SeedPublishStatus.Created && result.NewId > 0)
{
    try
    {
        var remote = await adoService.FetchAsync(result.NewId, ct);
        await adoGitService!.AddArtifactLinkAsync(
            result.NewId, artifactUri, "ArtifactLink", remote.Revision, "Branch", ct);
        linkedCount++;
    }
    catch (Exception ex)
    {
        linkFailures++;
        Console.Error.WriteLine(fmt.FormatWarning(
            $"Failed to link branch to #{result.NewId}: {ex.Message}"));
    }
}
```

#### 2. `Program.cs` — Modified

Add `linkBranch` parameter to the `SeedPublish` command method and pass through:
```csharp
[Command("seed publish")]
public async Task<int> SeedPublish(
    [Argument] int? id = null, bool all = false, bool force = false,
    bool dryRun = false, string? linkBranch = null,
    string output = OutputFormatterFactory.DefaultFormat,
    CancellationToken ct = default)
    => await services.GetRequiredService<SeedPublishCommand>()
        .ExecuteAsync(id, all, force, dryRun, linkBranch, output, ct);
```

### Data Flow

**Single seed publish with `--link-branch`:**
1. Command receives `linkBranch = "planning/abc123"`
2. Upfront: resolve projectId + repoId → build `artifactUri`
3. Call `orchestrator.PublishAsync(seedId, ...)` → returns `SeedPublishResult`
4. If Created: `FetchAsync(newId)` for revision → `AddArtifactLinkAsync(newId, artifactUri, ...)`
5. Output result + link status

**Batch publish (`--all`) with `--link-branch`:**
1. Same upfront resolution (once)
2. `orchestrator.PublishAllAsync(...)` → returns `SeedPublishBatchResult`
3. Iterate results: for each Created, attempt link
4. Output batch result + link summary (X linked, Y failed)

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Link at command layer, not orchestrator | The orchestrator is a domain service with no git service dependency. Adding `IAdoGitService` would couple domain to infrastructure. Branch linking is a command-layer concern. |
| Upfront validation | Avoids N redundant `GetProjectIdAsync`/`GetRepositoryIdAsync` calls. Fail-fast if git service isn't configured. |
| Best-effort with warnings | Matches existing patterns in `BranchCommand`, `CommitCommand`, `PrCommand`. Seeds were published successfully — a link failure shouldn't undo that. |
| Use `FetchAsync` for revision | Each newly-published item may have a different revision (from the post-publish cache refresh in the orchestrator). Using the cached revision ensures optimistic concurrency. |
| No `SeedPublishResult` changes | The result model already carries everything needed (`NewId`, `Status`). Adding link status to the result would require domain model changes for a command-layer concern. Instead, link summary is handled via console output. |

---

## Dependencies

### External
- None. Uses existing `IAdoGitService` and `IAdoWorkItemService` interfaces.

### Internal
- **T1 `twig link branch` infrastructure** — `AddArtifactLinkAsync` on `IAdoGitService`
  is already implemented in `AdoGitClient`. ✅ Satisfied.
- **`SeedPublishOrchestrator`** — unchanged, provides `NewId` in results.

### Sequencing
- No blocking dependencies. The T1 infrastructure already exists.

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--link-branch` work without `--all` (single seed)? | Low | The description says "works with `--all` and individual seed publish" — implementing both. |
| 2 | Should we cache the revision from the orchestrator's post-publish refresh? | Low | Using `FetchAsync` is consistent with existing patterns and ensures we have the latest revision. The extra HTTP call per seed is acceptable. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| *(none)* | No new files needed |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/SeedPublishCommand.cs` | Add `IAdoGitService?` and `IAdoWorkItemService` to constructor. Add `linkBranch` parameter to `ExecuteAsync`. Add upfront branch validation. Add post-publish linking loop with best-effort error handling. |
| `src/Twig/Program.cs` | Add `linkBranch` parameter to `SeedPublish` command method signature and pass-through. Update help text. |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | No change needed — `SeedPublishCommand` uses primary constructor DI. |
| `src/Twig/CommandExamples.cs` | Add `--link-branch` example to seed publish examples. |
| `tests/Twig.Cli.Tests/Commands/SeedPublishCommandTests.cs` | Add tests for: link-branch with single seed, link-branch with --all, link failure warning, dry-run skips linking, null adoGitService warning. |

---

## ADO Work Item Structure

### Issue #2022: Add --link-branch flag to seed publish with best-effort artifact linking

**Goal:** Implement the `--link-branch` flag end-to-end: parameter wiring, upfront
validation, per-seed linking, best-effort error handling, output, and tests.

**Prerequisites:** None (T1 infrastructure already exists).

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | Wire `--link-branch` parameter through Program.cs and SeedPublishCommand constructor | `Program.cs`, `SeedPublishCommand.cs` | Small |
| T2 | Implement upfront branch validation and per-seed artifact linking with best-effort error handling | `SeedPublishCommand.cs` | Medium |
| T3 | Add unit tests for all link-branch scenarios | `SeedPublishCommandTests.cs` | Medium |
| T4 | Update command examples and help text | `CommandExamples.cs`, `Program.cs` | Small |

**Acceptance Criteria:**
- [ ] `twig seed publish --all --link-branch planning/abc` links all created seeds
- [ ] `twig seed publish -5 --link-branch planning/abc` links the single created seed
- [ ] Branch validation (projectId/repoId) happens once, not per-seed
- [ ] Link failures produce warnings but don't change exit code
- [ ] `--dry-run --link-branch` makes no API calls
- [ ] Null `IAdoGitService` produces a warning, not an error
- [ ] All new code has unit test coverage
- [ ] Build succeeds with `TreatWarningsAsErrors=true`

---

## PR Groups

### PG-1: seed publish --link-branch (all changes)

| Property | Value |
|----------|-------|
| **Classification** | Deep |
| **Tasks** | T1, T2, T3, T4 |
| **Estimated LoC** | ~250 |
| **Estimated Files** | 4 |
| **Predecessor** | *(none)* |

**Rationale:** All changes are tightly coupled — the parameter wiring, implementation,
tests, and docs form a single coherent review unit. The estimated size (~250 LoC, 4 files)
is well within the ≤2000 LoC / ≤50 files guardrails. Splitting into multiple PRs would
add review overhead without improving reviewability.

---

## References

- BranchCommand artifact link pattern: `src/Twig/Commands/BranchCommand.cs:77-101`
- CommitCommand artifact link pattern: `src/Twig/Commands/CommitCommand.cs:65-85`
- PrCommand artifact link pattern: `src/Twig/Commands/PrCommand.cs:94-116`
- ADO vstfs URI format for branches: `vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}`
- SeedPublishOrchestrator: `src/Twig.Domain/Services/SeedPublishOrchestrator.cs`
