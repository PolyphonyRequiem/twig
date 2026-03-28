---
name: twig-sdlc
description: Run the end-to-end SDLC workflow for twig development. Takes an ADO work item or natural language prompt through planning, implementation, PR lifecycle, and close-out via Conductor multi-agent orchestration. Activate when asked to implement a feature end-to-end, run the full SDLC pipeline, or orchestrate planning through delivery.
user-invokable: true
---

# End-to-End SDLC Workflow

Orchestrated SDLC pipeline powered by the `conductor` skill. Takes an ADO work item (Epic or Issue) or a natural language prompt and drives it through planning, implementation, code review, PR management, and close-out — all via multi-agent orchestration.

> **This workflow is long-running** — typically 30-120+ minutes depending on scope. You MUST use `read_file` to load the full conductor skill instructions from `.github/skills/conductor/SKILL.md` and follow its execution procedure exactly. **Always launch with `conductor --silent run ... --web-bg`** — this suppresses console noise and opens a real-time web dashboard.

## Prerequisites

- `conductor` skill — load `.github/skills/conductor/SKILL.md` for installation and execution details
- `twig` CLI — configured with an ADO workspace (`twig workspace` shows current context)
- `gh` CLI — authenticated for PR creation and merging
- A work item ID or prompt describing what to build

## Workflow

One workflow template is included in `assets/`:

| Workflow | Purpose | Key Inputs |
|----------|---------|------------|
| `twig-sdlc.yaml` | Full SDLC pipeline: intake → plan → seed → implement → PR → close-out | `work_item_id` or `prompt`, optional `skip_plan_review` |

## Quick Reference

```bash
# Implement an existing ADO work item end-to-end
conductor --silent run assets/twig-sdlc.yaml --input work_item_id=1273 --web-bg

# Start from a natural language prompt (creates new Epic)
conductor --silent run assets/twig-sdlc.yaml --input prompt="Add a twig export command that outputs CSV" --web-bg

# Skip the human plan approval gate
conductor --silent run assets/twig-sdlc.yaml --input work_item_id=1273 --input skip_plan_review=true --web-bg
```

> **Always use absolute paths** for workflow templates — see the `conductor` skill's execution guidance.

> **Always share the dashboard URL** — after launching, provide the user the web dashboard URL from terminal output.

## Phases

### Phase 1: Intake (1 agent)

Reads an existing ADO work item or creates a new Epic from a prompt. Gathers context (title, description, existing child items) for the planning phase.

### Phase 2: Planning (4 agents + 1 parallel group + 2 human gates)

```
architect → open_questions_gate (human gate, conditional) → reducer_plan
  → parallel(technical_reviewer, readability_reviewer)
  → review_router → plan_approval (human gate)
```

- **architect** (Opus 1M) — researches codebase, creates `.plan.md` with PR groupings
- **open_questions_gate** — human gate triggered when architect has blocking open questions (moderate+ severity)
- **reducer_plan** (Sonnet) — flags scope creep, over-engineering, unnecessary complexity
- **technical_reviewer** (Opus 1M) + **readability_reviewer** (Sonnet) — parallel review, both must score ≥90
- **review_router** — checks scores, loops to architect or skips approval for trivial plans
- **plan_approval** — human gate with approve/revise/reject options

### Phase 3: Work Tree Seeding (1 agent)

Creates ADO Issues and Tasks from the plan via `twig seed new`, `twig seed chain`, and `twig seed publish`. Maps plan Epics → ADO Issues, plan Tasks → ADO Tasks. Uses successor links for execution ordering.

### Phase 4: Implementation (7 agents + 1 human gate)

```
implementation_manager → coder → reducer_code → task_reviewer
     ▲                                              │
     │              ┌── REQUEST_CHANGES ─────────────┘
     │              ↓
     │            coder (fix)
     │              │
     ├── task approved, more tasks ──────────────────┘
     │
     ├── issue tasks done → user_acceptance (human gate, conditional)
     │                        │
     │                        ├── accepted/skipped → close issue
     │                        └── changes → implementation_manager
     │
     ├── PR group done → pr_submit → pr_reviewer
     │                       ▲            │
     │                       │            ├── APPROVE → pr_merge → implementation_manager
     │                       │            └── REQUEST_CHANGES → pr_fixer → pr_submit
     │
     └── all done → reducer_final → close_out
```

- **implementation_manager** (Sonnet) — central hub: manages task/issue lifecycle via twig, creates branches, routes work
- **coder** (Opus 1M) — implements one task at a time with incremental commits and twig notes
- **reducer_code** (Sonnet) — simplifies each task's implementation
- **task_reviewer** (Sonnet) — per-task quality gate
- **user_acceptance** — human gate, conditional per-issue when user-facing changes are flagged
- **pr_submit** (Sonnet) — creates GitHub PR via `gh pr create`
- **pr_reviewer** (Opus 1M) — holistic PR review
- **pr_fixer** (Sonnet) — addresses PR-level review feedback
- **pr_merge** (Sonnet) — merges and cleans up branch

### Phase 5: Close-out (2 agents)

- **reducer_final** (Sonnet) — holistic reduction pass across all changes
- **close_out** (Opus 1M) — transitions Epic to Done, produces meta-observations on agent performance and workflow improvements

## Agent Summary

| Agent | Model | Role |
|-------|-------|------|
| intake | Sonnet | Read work item / create Epic |
| architect | Opus 1M | Design + implementation plan |
| open_questions_gate | Human Gate | Blocking open questions during planning |
| reducer_plan | Sonnet | Plan-level complexity reduction |
| technical_reviewer | Opus 1M | Technical accuracy review |
| readability_reviewer | Sonnet | Clarity and structure review |
| review_router | Sonnet | Score checking + routing |
| plan_approval | Human Gate | Approve / revise / reject plan |
| work_tree_seeder | Sonnet | Create ADO Issues + Tasks |
| implementation_manager | Sonnet | Lifecycle hub + routing |
| coder | Opus 1M | Task implementation |
| reducer_code | Sonnet | Per-task code simplification |
| task_reviewer | Sonnet | Per-task quality gate |
| user_acceptance | Human Gate | Conditional per-issue acceptance |
| pr_submit | Sonnet | GitHub PR creation |
| pr_reviewer | Opus 1M | Holistic PR review |
| pr_fixer | Sonnet | PR fix cycle |
| pr_merge | Sonnet | Merge + cleanup |
| reducer_final | Sonnet | Final holistic reduction |
| close_out | Opus 1M | Epic completion + observations |

## When to Use

- **Full feature implementation** — "implement this Epic end-to-end"
- **New feature from scratch** — "build a twig export command"
- **Structured pipeline** — when you want planning, review gates, and PR management automated

## When NOT to Use

- **Quick fixes** — single-file changes don't need a 5-phase pipeline
- **Plan only** — use `octane-workflow-plan` for just the planning phase
- **Implement from existing plan** — use `octane-workflow-implement` if you already have a `.plan.md`
