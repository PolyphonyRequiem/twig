---
name: twig-sdlc
description: Run the end-to-end SDLC workflow for twig development. Takes an ADO work item or natural language prompt through planning, implementation, PR lifecycle, and close-out via Conductor multi-agent orchestration. Activate when asked to implement a feature end-to-end, run the full SDLC pipeline, or orchestrate planning through delivery.
user-invokable: true
---

# End-to-End SDLC Workflow

Orchestrated SDLC pipeline powered by the `conductor` skill. Takes an ADO work item (Epic or Issue) or a natural language prompt and drives it through planning, implementation, code review, PR management, and close-out — all via multi-agent orchestration.

> **This workflow is long-running** — typically 30-120+ minutes depending on scope. **Always launch with `conductor --silent run ... --web`** — this suppresses console noise and opens a real-time web dashboard. **Do NOT use `--web-bg`** — it does not work correctly; always use `--web`.

> **Always launch detached** — use `detach: true` (or `Start-Process`) so conductor
> survives if the parent session drops. Non-detached async shells die when the Copilot
> CLI session reconnects or terminates.

## Workflows

All workflows are registered in the `twig` conductor registry. Use short names — no absolute paths needed.

> **Workflow source**: [PolyphonyRequiem/twig-conductor-workflows](https://github.com/PolyphonyRequiem/twig-conductor-workflows)
> Install: `conductor registry add twig --source github://PolyphonyRequiem/twig-conductor-workflows`
> Update: `conductor registry update twig`

| Workflow | Registry Name | Purpose | Key Inputs |
|----------|---------------|---------|------------|
| Planning only | `twig-sdlc-planning@twig` | Recursive planner: architect + review + seed + per-issue task planning | `work_item_id` or `prompt` |
| Implementation only | `twig-sdlc-implement@twig` | Coding, review, PR lifecycle, close-out | `work_item_id`, optional `plan_path` |
| Full (composite) | `twig-sdlc-full@twig` | Planning → implementation via sub-workflow composition | `work_item_id` or `prompt` |
| Legacy | `twig-sdlc-legacy@twig` | Original monolithic pipeline (deprecated) | `work_item_id` or `prompt` |

## Workflow Metadata

All metadata is passed dynamically via `--metadata` / `-m` flags at invocation time.
The workflow YAMLs contain no metadata — the invoking agent resolves all values.

| Field | Example | Description |
|-------|---------|-------------|
| `tracker` | `ado` | Work item tracking system |
| `project_url` | `https://dev.azure.com/dangreen-msft/Twig` | ADO project URL |
| `git_repo` | `C:\Users\dangreen\projects\twig2` | Originating git repo path |
| `workitem_id` | `1842` | Target work item ID (numeric) |
| `worktree_name` | `twig2-1842` | Git worktree directory name |
| `cwd` | `C:\Users\dangreen\projects\twig2-1842` | Worktree working directory |

> **All values must be resolved** — templates with `{braces}` are skipped by the dashboard.

## Quick Reference

**Always run in a dedicated worktree** — never on `main` or your working tree. Name the
worktree `twig2-<ID>` based on the work item ID.

```powershell
# 1. Create a worktree for the work item
git worktree add -b sdlc/<ID> ../twig2-<ID> main
cd ../twig2-<ID>

# 2. Restore dependencies (worktrees don't share NuGet packages)
dotnet restore

# 3. Copy .twig workspace and set context to the target work item
Copy-Item -Recurse ../twig2/.twig .twig
twig set <ID>
twig sync

# 4. Run the full SDLC — pass ALL metadata via -m flags
conductor --silent run twig-sdlc-full@twig --input work_item_id=<ID> --input skip_plan_review=true -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=<ID> -m worktree_name=twig2-<ID> -m cwd=C:\Users\dangreen\projects\twig2-<ID> --web
```

## Launching Multiple Runs

When running multiple work items, use **discrete worktrees** and launch **10 seconds apart**.

### Option A: Direct launch with logging (recommended)

Simpler approach — launches conductor directly with `Tee-Object` for log capture.
Dashboard URLs are written to `conductor.log` in each worktree.

```powershell
# Create worktrees (NEVER on main — always a dedicated branch)
git worktree add -b sdlc/1673 ../twig2-1673 main
git worktree add -b sdlc/1782 ../twig2-1782 main

# Launch 10s apart with log capture
$ids = 1673, 1782
foreach ($id in $ids) {
    $wt = "C:\Users\dangreen\projects\twig2-$id"
    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-Command",
        "cd $wt; conductor --silent run twig-sdlc-full@twig --input work_item_id=$id --input skip_plan_review=true -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=$id -m worktree_name=twig2-$id -m cwd=$wt --web 2>&1 | Tee-Object -FilePath $wt\conductor.log" `
        -PassThru | ForEach-Object { "Launched #$id — PID $($_.Id)" }
    if ($id -ne $ids[-1]) { Start-Sleep -Seconds 10 }
}

# Retrieve dashboard URLs after ~15s
Start-Sleep -Seconds 15
foreach ($id in $ids) {
    Get-Content "C:\Users\dangreen\projects\twig2-$id\conductor.log" |
        Select-String "Dashboard:" | Select-Object -First 1
}
```

### Option B: Job Object wrapper (orphan cleanup)

Use `tools/run-conductor.ps1` when orphaned processes are a concern (MCP servers, test
runners surviving after conductor exits). The wrapper creates a Windows Job Object that
kills all child processes when conductor exits.

> **⚠️ Argument quoting is critical** — the `-Arguments` value must be wrapped in escaped
> quotes so `Start-Process` passes it as a single token to the script parameter.

```powershell
$ids = 1673, 1782
foreach ($id in $ids) {
    $wt = "C:\Users\dangreen\projects\twig2-$id"
    $args = "--silent run twig-sdlc-full@twig --input work_item_id=$id --input skip_plan_review=true -m tracker=ado -m project_url=https://dev.azure.com/dangreen-msft/Twig -m git_repo=C:\Users\dangreen\projects\twig2 -m workitem_id=$id -m worktree_name=twig2-$id -m cwd=$wt --web"
    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile", "-File",
        "tools\run-conductor.ps1",
        "-WorkingDirectory", "`"$wt`"",
        "-Arguments", "`"$args`"" `
        -WindowStyle Hidden
    if ($id -ne $ids[-1]) { Start-Sleep -Seconds 10 }
}
```

The 10-second stagger avoids MCP server port collisions and rate-limit spikes.

> **Always share the dashboard URL** — after launching, provide the user the web dashboard URL from terminal output.

### Worktree Cleanup

After runs complete, clean up worktrees:

```bash
# Remove worktrees and branches
git worktree remove --force ../twig2-1673
git branch -D sdlc/1673

# If directories are locked, kill lingering processes first:
dotnet build-server shutdown
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'twig2-\d+' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

## Phases

### Phase 1: Intake (1 agent)

Reads an existing ADO work item or creates a new Epic from a prompt. Gathers context (title, description, existing child items) for the planning phase.

### Phase 2: Planning (4 agents + 1 parallel group + 2 human gates)

```
architect → open_questions_gate (human gate, conditional)
  → parallel(technical_reviewer, readability_reviewer)
  → review_router → plan_approval (human gate)
```

- **architect** (Opus 1M) — researches codebase, creates `.plan.md` with PR groupings
- **open_questions_gate** — human gate triggered when architect has blocking open questions (moderate+ severity)
- **technical_reviewer** (Opus 1M) + **readability_reviewer** (Sonnet) — parallel review, both must score ≥90
- **review_router** — checks scores, loops to architect or skips approval for trivial plans
- **plan_approval** — human gate with approve/revise/reject options

### Phase 3: Work Tree Seeding (1 agent)

Creates ADO Issues and Tasks from the plan via `twig seed new`, `twig seed chain`, and `twig seed publish`. Seeds Issues and Tasks matching the plan's ADO hierarchy under the input work item. Uses successor links for execution ordering.

### Phase 4: Implementation (10 agents + 1 human gate)

Two-tier orchestrator split: `pr_group_manager` (outer) owns branch lifecycle and issue
closure; `task_manager` (inner) owns task execution within a PR group. Issues are ONLY
closed after their PR is merged — structurally preventing "code complete ≠ code merged".

```
pr_group_manager ──→ task_manager ──→ coder → reducer_code → task_reviewer
      ▲                   ▲                                       │
      │                   │         ┌── REQUEST_CHANGES ──────────┘
      │                   │         ↓
      │                   │       coder (fix)
      │                   │         │
      │                   ├── task approved, more tasks ──────────┘
      │                   │
      │                   ├── all tasks done → reducer_issue → issue_reviewer
      │                   │                                       │
      │                   │                  ┌── REQUEST_CHANGES ─┘
      │                   │                  ↓
      │                   │         task_manager (fix task)
      │                   │                  │
      │                   ├── issue approved → user_acceptance (human gate)
      │                   │                       │
      │                   │                       ├── accepted/skipped ──┘
      │                   │                       └── changes → task_manager
      │                   │
      │                   └── all issues reviewed → pr_group_ready
      │
      ├── pr_group_ready → reducer_pr → pr_submit → pr_reviewer
      │                                    ▲            │
      │                                    │            ├── APPROVE → pr_merge ──┐
      │                                    │            └── REQUEST_CHANGES ─→ pr_fixer
      │                                    └────────────────────────────────────┘
      ├── pr_merge returns → close issues in this PR group → next PR group
      │
      └── all PR groups done → close_out
```

- **pr_group_manager** (Sonnet) — outer orchestrator: creates branches, closes Issues (only after PR merge), routes PR groups
- **task_manager** (Sonnet) — inner orchestrator: manages task lifecycle within a PR group, routes to coder/reviewers, returns `pr_group_ready` when done (CANNOT close Issues)
- **coder** (Opus 1M) — implements one task at a time with incremental commits and twig notes; has pre-review checklist to avoid round-trips
- **reducer_code** (Sonnet) — simplifies each task's implementation
- **task_reviewer** (Sonnet) — per-task quality gate
- **reducer_issue** (Sonnet) — post-issue code sweep across all tasks in completed issue
- **issue_reviewer** (Opus 1M) — per-issue acceptance criteria, cross-cutting concerns, integration
- **user_acceptance** — human gate, conditional per-issue when user-facing changes are flagged
- **reducer_pr** (Sonnet) — pre-PR reduction sweep (stale references, dead code, cross-task duplication)
- **pr_submit** (Sonnet) — validates build + tests, then creates GitHub PR via `gh pr create`
- **pr_reviewer** (Opus 1M) — holistic PR review
- **pr_fixer** (Sonnet) — addresses PR-level review feedback
- **pr_merge** (Sonnet) — merges and cleans up branch

### Phase 5: Close-out (1 agent)

- **close_out** (Opus 1M) — transitions the work item to Done, produces meta-observations on agent performance and workflow improvements

### Phase 6: Closeout Filing (1 agent)

- **closeout_filer** (Sonnet) — takes the close_out observations and improvement suggestions, creates a tagged ADO Issue for human review. The Issue is tagged `closeout-notes; Needs Review` for easy discovery and triage.

## Agent Summary

| Agent | Model | Role |
|-------|-------|------|
| intake | Sonnet | Read work item / create Epic |
| plan_reader | Sonnet | Read existing approved plan (bypass planning) |
| architect | Opus 1M | Design + implementation plan |
| open_questions_gate | Human Gate | Blocking open questions during planning |
| technical_reviewer | Opus 1M | Technical accuracy review |
| readability_reviewer | Sonnet | Clarity and structure review |
| review_router | Sonnet | Score checking + routing |
| plan_approval | Human Gate | Approve / revise / reject plan |
| work_tree_seeder | Sonnet | Create ADO Issues + Tasks |
| pr_group_manager | Sonnet | Outer orchestrator: branch + issue closure (after PR merge) |
| task_manager | Sonnet | Inner orchestrator: task lifecycle + routing |
| coder | Opus 1M | Task implementation |
| reducer_code | Sonnet | Per-task code simplification |
| task_reviewer | Sonnet | Per-task quality gate |
| reducer_issue | Sonnet | Post-issue cross-task code sweep |
| issue_reviewer | Opus 1M | Per-issue acceptance criteria check |
| user_acceptance | Human Gate | Conditional per-issue acceptance |
| reducer_pr | Sonnet | Pre-PR reduction sweep |
| pr_submit | Sonnet | Build validation + GitHub PR creation |
| pr_reviewer | Opus 1M | Holistic PR review |
| pr_fixer | Sonnet | PR fix cycle |
| pr_merge | Sonnet | Merge + cleanup |
| close_out | Opus 1M | Epic completion + observations |
| closeout_filer | Sonnet | File observations as tagged ADO Issue |

## Plan File Frontmatter

Every `.plan.md` file in `docs/projects/` **must** include YAML frontmatter with three required fields:

```yaml
---
work_item_id: 1858
title: "AzCliAuthProvider Timeout Override"
type: Issue
---
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `work_item_id` | integer | Yes | The ADO work item ID this plan targets. Primary key for plan detection. |
| `title` | string | Yes | Human-readable plan title. Used by `Get-PlanTitle` and for display. |
| `type` | string | Yes | ADO work item type — `Epic`, `Issue`, or `Task`. Indicates the plan's scope level. |

The frontmatter block is placed before the first H1 heading. Existing metadata tables
and blockquotes below the heading are preserved — they serve as human-readable context.

### Plan Detection Behavior

The `plan_detector` agent uses frontmatter to match plans to work items:

1. **Primary (frontmatter match)**: Parses each `docs/projects/*.plan.md` file's YAML
   frontmatter and compares `work_item_id` to the requested work item ID. An exact
   numeric match is required — this eliminates false positives from content references.
2. **Fallback (content search)**: If no frontmatter match is found, falls back to
   searching file content for the work item ID. This is a legacy heuristic with lower
   confidence and is subject to false positives (e.g., a parent Epic's plan listing
   a child Issue ID).

### Architect Agent Requirements

When generating a new plan, the **architect** agent must:
- Include the YAML frontmatter block as the very first content in the file
- Set `work_item_id` to the target work item's numeric ID (no `#` prefix)
- Set `title` to the plan's goal or title
- Set `type` to the work item type (`Epic`, `Issue`, or `Task`)

Plans without frontmatter will still be detected via content search fallback, but
frontmatter is the canonical mechanism and must be included in all new plans.

## When to Use

- **Full feature implementation** — "implement this Epic end-to-end"
- **New feature from scratch** — "build a twig export command"
- **Structured pipeline** — when you want planning, review gates, and PR management automated

## When NOT to Use

- **Quick fixes** — single-file changes don't need a 5-phase pipeline
