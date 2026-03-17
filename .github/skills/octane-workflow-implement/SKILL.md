---
name: octane-workflow-implement
description: Run deep implementation workflows via Conductor to execute code changes from an implementation plan. Use when implementing features from plan documents, executing epics, running plan-then-implement cycles, or orchestrating code-to-review-to-commit workflows with built-in quality gates. Builds on the conductor skill for execution.
user-invokable: true
---

# Deep Implementation

Orchestrated implementation workflow powered by the `conductor` skill. Executes code changes from a `.plan.md` document through multi-agent review loops with per-epic and holistic quality gates.

> **This workflow is long-running** — typically 30+ minutes depending on epic count. You MUST use `read_file` to load the full conductor skill instructions from the installed conductor skill's `SKILL.md` and follow its execution procedure exactly. **Always launch with `conductor --silent run ... --web-bg`** — this suppresses console noise and opens a real-time web dashboard.

## Prerequisites

- `conductor` skill — you MUST use `read_file` to load `~/.copilot/skills/conductor/SKILL.md` or `.github/skills/conductor/SKILL.md` for installation and execution details. Do NOT run `conductor --version` to check; just run the workflow directly and install only if the command fails.
- A `.plan.md` file — generate one first using the `octane-workflow-plan` skill

## Known Issues & Error Handling

See the conductor skill's `SKILL.md` for known issues (including the Windows `fcntl` bug) and error handling procedures. If `conductor` fails, report the error and consult the conductor skill documentation before taking any action.

## Workflow

One workflow template is included in `assets/` (relative to this skill):

| Workflow | Purpose | Key Inputs |
|----------|---------|------------|
| `implement.yaml` | Implement code changes from a plan document | `plan`, optional `epic` |

## Quick Reference

```bash
# Implement: execute all epics from a plan
conductor --silent run assets/implement.yaml --input plan="feature.plan.md" --web-bg

# Implement: execute a specific epic
conductor --silent run assets/implement.yaml --input plan="feature.plan.md" --input epic="EPIC-001" --web-bg

# Dry run to preview execution
conductor run assets/implement.yaml --input plan="feature.plan.md" --dry-run
```

> **No validation needed** — these are pre-packaged, tested workflows. You can skip `conductor validate`.

> **Always use absolute paths** for workflow templates — see the `conductor` skill's execution guidance.

> **Always share the dashboard URL** — after launching the workflow, provide the user with the web dashboard URL from the terminal output so they can track progress in real time.

## Implementation Workflow

**Agents:** `epic_selector` (Sonnet) → `coder` (Opus 1M) → `epic_reviewer` (Sonnet) → `committer` (Sonnet) → loops per epic → `plan_reviewer` (Opus 1M) → `fixer` (Sonnet)

```
epic_selector ──→ coder ──→ epic_reviewer ──→ approved? ──→ committer
     ▲                           │                              │
     │                           └── changes requested ──→ coder │
     │                                                          │
     │           ┌──────────────────────────────────────────────┘
     │           │
     │           ├── more epics? ──→ epic_selector (next epic)
     │           │
     └───────────┘── all done ──→ plan_reviewer ──→ approved? ──→ $end
                                      │
                                      └── changes ──→ fixer ──→ plan_reviewer
```

### Per-Epic Loop

1. **Epic Selector** — reads the plan, identifies the single next incomplete epic, extracts its details
2. **Coder** — researches the codebase, implements ONLY the selected epic's tasks, writes tests
3. **Epic Reviewer** — reviews code quality, correctness, test coverage; approves or requests changes
4. **Committer** — creates a git commit, updates the plan document status, determines next epic

### Holistic Review (after all epics)

5. **Plan Reviewer** — reviews the entire implementation for architecture coherence, cross-cutting concerns, test coverage
6. **Fixer** — addresses any plan-level issues identified by the reviewer

## When to Use

| I want to... | Input |
|---|---|
| Implement all remaining epics from a plan | `plan` (path to `.plan.md` file) |
| Implement a single specific epic | `plan` + `epic` (e.g., `EPIC-001`) |
| Resume after interruption | Same `plan` — the workflow picks up from the first non-DONE epic |

## Expected Plan Format

The workflow expects a `.plan.md` file with structured epics. Generate one using the `octane-workflow-plan` skill's `plan.yaml` workflow.

```markdown
## 9. Implementation Plan

### EPIC-001: [Title]
**Goal:** ...
**Prerequisites:** ...

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| T-001   | IMPL | ...         | ...   | TO DO  |
| T-002   | TEST | ...         | ...   | TO DO  |

**Acceptance Criteria:**
- [ ] Criterion 1
- [ ] Criterion 2
```

Tasks use status tracking: `TO DO` → `IN PROGRESS` → `DONE`

## Tips

- **Start with a plan** — always create a plan using the `octane-workflow-plan` skill before implementing
- **Review the plan first** — check the `.plan.md` output before running implement; adjust epics if needed
- **Specific epics** — use `--input epic="EPIC-003"` to implement one epic at a time for better control
- **Resume** — if the workflow is interrupted, re-run with the same plan; it picks up the first non-DONE epic
- **Dry run** — use `--dry-run` to preview the execution plan without running agents
- **Epic commits** — each completed epic is auto-committed with plan status updates
