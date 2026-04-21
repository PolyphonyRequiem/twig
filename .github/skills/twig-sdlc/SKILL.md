---
name: twig-sdlc
description: Run the end-to-end SDLC workflow for twig development. Takes an ADO work item or natural language prompt through planning, implementation, PR lifecycle, and close-out via Conductor multi-agent orchestration. Activate when asked to implement a feature end-to-end, run the full SDLC pipeline, or orchestrate planning through delivery.
user-invokable: true
---

# End-to-End SDLC Workflow

Orchestrated SDLC pipeline powered by the `conductor` skill. Takes an ADO work item (Epic or Issue) or a natural language prompt and drives it through planning, implementation, code review, PR management, and close-out — all via multi-agent orchestration.

> **This workflow is long-running** — typically 30-120+ minutes depending on scope. **Always launch with `conductor --silent run ... --web`** — this suppresses console noise and opens a real-time web dashboard. **Do NOT use `--web-bg`** — it does not work correctly; always use `--web`.

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

## Quick Reference

**Always run in a dedicated worktree** — never on `main` or your working tree. Name the
worktree `twig2-<ID>` based on the work item ID.

```bash
# 1. Create a worktree for the work item
git worktree add -b sdlc/<ID> ../twig2-<ID> main
cd ../twig2-<ID>

# 2. Run the full SDLC (planning → implementation) for a single work item
conductor --silent run twig-sdlc-full@twig --input work_item_id=<ID> --web

# Plan only (recursive planner — creates Epic/Issues/Tasks in ADO)
conductor --silent run twig-sdlc-planning@twig --input work_item_id=<ID> --web

# Implement only (requires existing plan + seeded work items)
conductor --silent run twig-sdlc-implement@twig --input work_item_id=<ID> --input plan_path="docs/projects/foo.plan.md" --web

# Start from a natural language prompt (use a descriptive branch name instead of ID)
conductor --silent run twig-sdlc-full@twig --input prompt="Add a twig export command" --web

# Skip human approval gates
conductor --silent run twig-sdlc-full@twig --input work_item_id=<ID> --input skip_plan_review=true --web
```

## Launching Multiple Runs

When running multiple work items, use **discrete worktrees** and launch **10 seconds apart**.
Use `tools/run-conductor.ps1` to ensure all child processes (MCP servers, test runners)
are killed when conductor exits — prevents orphaned processes that lock worktree directories.

```bash
# Create worktrees (NEVER on main — always a dedicated branch)
git worktree add -b sdlc/1673 ../twig2-1673 main
git worktree add -b sdlc/1782 ../twig2-1782 main

# Launch 10s apart using Job Object wrapper (kills all children on exit)
$ids = 1673, 1782
foreach ($id in $ids) {
    Start-Process -FilePath "pwsh" -ArgumentList "-NoProfile","-File",
        "tools\run-conductor.ps1",
        "-WorkingDirectory", "C:\Users\dangreen\projects\twig2-$id",
        "-Arguments", "--silent run twig-sdlc-implement@twig --input work_item_id=$id --web" `
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

## When to Use

- **Full feature implementation** — "implement this Epic end-to-end"
- **New feature from scratch** — "build a twig export command"
- **Structured pipeline** — when you want planning, review gates, and PR management automated

## When NOT to Use

- **Quick fixes** — single-file changes don't need a 5-phase pipeline
