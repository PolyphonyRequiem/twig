---
name: sdlc-launch
description: Launch SDLC conductor workflows in isolated git worktrees. Handles worktree creation, dependency restore, and staggered process launch. Activate when asked to "run SDLC", "launch SDLC", or implement work items via the conductor pipeline.
user-invokable: true
---

# SDLC Launch Skill

Manages the **infrastructure** for running conductor SDLC workflows: worktree lifecycle,
dependency restore, and process launch. For workflow content (agents, phases, inputs),
see the `twig-sdlc` skill.

> **This skill owns worktree + launch. The `twig-sdlc` skill owns workflow selection + content.**

## Prerequisites

- `conductor` CLI installed (`conductor -V` to check)
- `twig` conductor registry configured: `conductor registry add twig --source github://PolyphonyRequiem/twig-conductor-workflows`
- The repo has `tools/run-conductor.ps1` (Job Object wrapper for Windows process cleanup)

## Naming Conventions

| Item | Pattern | Example |
|------|---------|---------|
| Worktree directory | `../twig2-{id}` (sibling of repo root) | `../twig2-1851` |
| Branch name | `sdlc/{id}` | `sdlc/1851` |
| Run-conductor wrapper | `tools/run-conductor.ps1` (in repo root, NOT worktree) | — |

The `twig2` prefix is hardcoded — it matches the `worktree_name` field in conductor
workflow metadata (`twig2-{work_item_id}`). Do not change it independently.

## Single Item Launch

### Step 1: Reconcile worktree state

Before creating or reusing a worktree, check **both** filesystem and git state:

```powershell
# Check all three sources of truth
$dir = "../twig2-$id"
$dirExists = Test-Path $dir
$branchExists = (git branch --list "sdlc/$id") -ne ""
$worktreeRegistered = (git worktree list --porcelain | Select-String "twig2-$id") -ne $null
```

**Decision matrix:**

| Directory | Branch | Worktree registered | Action |
|-----------|--------|---------------------|--------|
| ✗ | ✗ | ✗ | **Create fresh**: `git worktree add -b sdlc/{id} ../twig2-{id} main` |
| ✗ | ✓ | ✗ | **Stale branch**: `git branch -D sdlc/{id}`, then create fresh |
| ✓ | ✗ | ✗ | **Stale directory**: remove directory, `git worktree prune`, then create fresh |
| ✓ | ✓ | ✓ | **Existing worktree**: check branch state (see below) |
| ✓ | ✓ | ✗ | **Orphaned**: `git worktree prune`, remove directory, delete branch, create fresh |
| other | — | — | `git worktree prune` first, then re-evaluate |

### Step 2: Handle existing worktrees

When a valid worktree already exists, check its state:

```powershell
# How far ahead of main?
$commits = git -C "../twig2-$id" --no-pager log --oneline main..HEAD
$uncommitted = git -C "../twig2-$id" status --short
```

**Ask the user** with these choices:
- **Reset to main** — `git reset --hard main` (loses all work)
- **Continue from current state** — keep existing branch and changes
- **Skip this item** — don't launch SDLC for it

If there are uncommitted changes, warn before offering reset.

### Step 3: Ensure main is current

Before branching from main, ensure it's up to date:

```powershell
git fetch origin main --quiet
```

This prevents worktrees from branching off stale commits.

### Step 4: Restore dependencies

```powershell
dotnet restore "../twig2-$id" --verbosity quiet
```

Worktrees don't share NuGet package caches with the main tree — restore is mandatory.

### Step 5: Select workflow

**Rules for workflow selection** (strict — do not guess):

| Condition | Workflow |
|-----------|----------|
| Item is an Epic or Issue with no existing plan | `twig-sdlc-full@twig` |
| Item is an Epic or Issue with an approved plan AND seeded child tasks | `twig-sdlc-implement@twig` |
| Item is a Task (leaf work item) | `twig-sdlc-full@twig` (planning will be minimal) |
| User explicitly requests "plan only" | `twig-sdlc-planning@twig` |
| User explicitly requests "implement only" | `twig-sdlc-implement@twig` |
| Existing worktree has in-progress implementation branch | Ask user: continue with `implement` or reset + `full` |

**When in doubt, use `twig-sdlc-full@twig`** — the planning phase is idempotent and will
detect existing plans.

To check for existing plans:
```powershell
# Check for plan files in the worktree
Get-ChildItem "../twig2-$id/docs/projects/*.plan.md" -ErrorAction SilentlyContinue
# Check for seeded child tasks
twig children $id --output json
```

### Step 6: Launch

Use `tools/run-conductor.ps1` from the **repo root** (not the worktree — the script
path must be absolute or relative to the launching shell's CWD):

```powershell
$repoRoot = "C:\Users\dangreen\projects\twig2"
$worktreeDir = "C:\Users\dangreen\projects\twig2-$id"

Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-File",
    "$repoRoot\tools\run-conductor.ps1",
    "-WorkingDirectory", $worktreeDir,
    "-Arguments", "--silent run $workflow --input work_item_id=$id --web" `
    -WindowStyle Normal
```

> **Use `-WindowStyle Normal`** (not Hidden) so the user can see the conductor dashboard
> URL in the terminal window and monitor progress.

## Multi-Item Launch

When launching multiple SDLC runs:

1. **Run steps 1-4 for all items first** (worktree creation + restore can be parallelized)
2. **Stagger launches 10 seconds apart** to avoid MCP server port collisions and API rate limits
3. **Present a summary table** after all launches

```powershell
$repoRoot = "C:\Users\dangreen\projects\twig2"

foreach ($run in $runs) {
    $id = $run.Id
    $wf = $run.Workflow
    $dir = "$repoRoot\..\twig2-$id"

    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-File",
        "$repoRoot\tools\run-conductor.ps1",
        "-WorkingDirectory", (Resolve-Path $dir).Path,
        "-Arguments", "--silent run $wf --input work_item_id=$id --web" `
        -WindowStyle Normal

    if ($run -ne $runs[-1]) { Start-Sleep -Seconds 10 }
}
```

### Summary table format

After launching, present:

```
| # | ID   | Title                              | Workflow     | Worktree     |
|---|------|------------------------------------|-------------|--------------|
| 1 | 1851 | Add --id parameter to commands     | full        | twig2-1851   |
| 2 | 1813 | Read-only lookup tools             | full        | twig2-1813   |
```

## Concurrency Rules

- **Epic + child Issue**: Do NOT run SDLC on an Epic AND its child Issues simultaneously.
  The Epic's planning phase will create/modify the same child items. Ask the user which
  scope to use — either the Epic (covers children) or individual Issues (more targeted).
- **Sibling Issues under same Epic**: Safe to run in parallel — they operate on different
  branches and different work items.
- **Same item twice**: Never launch two SDLC runs for the same work item ID.

## Worktree Cleanup

After SDLC runs complete:

```powershell
# Shut down build servers that may hold file locks
dotnet build-server shutdown

# Remove a specific worktree
git worktree remove --force ../twig2-$id
git branch -D sdlc/$id

# If directory is locked, find the specific conductor/MCP process:
Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match "twig2-$id" -and $_.Name -match "conductor|node|dotnet" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

### Bulk cleanup

```powershell
dotnet build-server shutdown
$ids = @(1851, 1813, 1852, 1855)
foreach ($id in $ids) {
    git worktree remove --force "../twig2-$id" 2>$null
    git branch -D "sdlc/$id" 2>$null
}
git worktree prune
```

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `fatal: a branch named 'sdlc/{id}' already exists` | `git branch -D sdlc/{id}` then retry |
| `fatal: '{path}' is already checked out` | Worktree exists elsewhere — `git worktree list` to find it |
| Worktree directory exists but no `.git` file | Stale directory — remove it, `git worktree prune`, recreate |
| `dotnet restore` fails in worktree | Check that `global.json` and `Directory.Build.props` are present (they should be — worktrees share repo content) |
| Conductor can't find `twig-sdlc-full@twig` | Registry not configured: `conductor registry add twig --source github://PolyphonyRequiem/twig-conductor-workflows` |
| Lingering processes after cleanup | `Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'twig2-\d+' }` to find them |
