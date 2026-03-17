# Spec-Driven Development via Conductor (SDD)

## Overview

The Conductor SDD scenario automates the complete Design → Plan → Implementation workflow using [Conductor](https://github.com/microsoft/conductor) multi-agent orchestration. Instead of manually invoking individual agents and prompts, Conductor runs YAML-defined workflows that coordinate multiple AI agents through review loops with built-in quality gates.

This is the **automated, workflow-driven** variant of the [Spec-Driven Development](../spec-driven-development/README.md) scenario.

## When to Use

- You want automated feature development with built-in quality gates
- You need hands-off design document generation with multi-reviewer validation
- You're building a large feature spanning multiple epics
- You want consistent, repeatable development workflows with less manual orchestration overhead
- You prefer automated per-epic commits and iterative review loops over manual control

## What's Included

### Skills
- **conductor** (shared) — Core workflow execution engine
- **octane-workflow-plan** — Design and plan document generation workflows
- **octane-workflow-implement** — Code implementation workflows from plan documents

### Workflow Templates

| Workflow | Skill | Purpose |
|----------|-------|---------|
| `plan.yaml` | octane-workflow-plan | Solution design + actionable implementation plan with epics/tasks |
| `implement.yaml` | octane-workflow-implement | Implement code changes from a plan document |

### Multi-Agent Pipelines

**Planning workflow** uses: `architect` → `technical_reviewer` → `readability_reviewer` with loop-back until quality scores ≥ 90

**Implementation workflow** uses: `coder` → `epic_reviewer` → `committer` (per epic) → `plan_reviewer` → `fixer` (holistic)

## Prerequisites

- Conductor CLI installed (`uv tool install git+https://github.com/microsoft/conductor.git`)
- Understanding of spec-driven development workflows
- MCP servers: `web-search`, `context7`, `ms-learn` (used by planning workflows)

## Workflows

The recommended way to use these workflows is via **slash commands** in GitHub Copilot Chat. Each skill is invokable directly — just describe what you want and the skill handles Conductor execution for you.

### Workflow 1: Plan (Design + Implementation Plan)

1. Invoke the planning skill with a description of what you want to build
2. Conductor launches the `architect` agent to research the codebase and produce a design + implementation plan
3. The `technical_reviewer` scores the plan; if < 90, it loops back to the architect with feedback
4. The `readability_reviewer` scores clarity; if < 90, it loops back again
5. Once both reviewers approve, a `.plan.md` file is written to disk

```
/octane-workflow-plan Create a plan for building OAuth2 authentication with PKCE flow
```

### Workflow 2: Implement from Plan

1. Invoke the implementation skill with a path to a `.plan.md` file (and optionally a specific epic)
2. The `epic_selector` identifies the next incomplete epic from the plan
3. The `coder` agent implements the epic's tasks and writes tests
4. The `epic_reviewer` reviews code quality; if changes are requested, the coder iterates
5. The `committer` creates a git commit and updates the plan status
6. Steps 2–5 repeat for each remaining epic
7. After all epics, the `plan_reviewer` does a holistic review; the `fixer` addresses any issues

```
/octane-workflow-implement Implement the plan in docs/oauth2.plan.md
```

Implement a specific epic:

```
/octane-workflow-implement Implement EPIC-001 from docs/oauth2.plan.md
```

### Workflow 3: Full End-to-End

1. **Generate Plan** — `/octane-workflow-plan` to produce a `.plan.md` with design and epics
2. **Review** — Check the generated plan and adjust if needed
3. **Implement** — `/octane-workflow-implement` against the plan to code, review, and commit each epic
4. **Verify** — The workflow handles code review automatically via built-in quality gates

### CLI Alternative

You can also run workflows directly via the Conductor CLI:

```bash
# Solution design + implementation plan
conductor run plan.yaml --input purpose="Build OAuth2 authentication with PKCE flow"

# Implement from plan
conductor run implement.yaml --input plan="feature.plan.md"

# Implement a specific epic
conductor run implement.yaml --input plan="feature.plan.md" --input epic="EPIC-001"
```

## Example Prompts

```
/octane-workflow-plan Create a plan for adding WebSocket support to the API gateway
/octane-workflow-plan Design a caching layer for the user service
/octane-workflow-implement Implement the plan in docs/websocket.plan.md
/octane-workflow-implement Implement EPIC-002 from docs/caching.plan.md
```

## Expected Output

- **Plan workflow**: A `.plan.md` file containing an executive summary, proposed design, architecture overview, and a structured implementation plan with epics, tasks, acceptance criteria, and status tracking
- **Implement workflow**: Git commits (one per epic) with code changes, tests, and updated task statuses in the plan document; after all epics, a holistic review pass ensures cross-cutting consistency

## Key Differences from Manual SDD

| Aspect | Manual SDD | Conductor SDD |
|--------|-----------|---------------|
| Agent invocation | Manual via `@Agent` mentions | Automated via YAML workflows |
| Quality gates | Manual review | Automated scoring (≥ 90 threshold) |
| Review loops | One-shot | Iterative loop-back until approved |
| Epic execution | One at a time, manually | Sequential with automatic progression |
| Commit management | Manual | Automated per-epic commits |

## Difficulty

**Intermediate** — Requires Conductor CLI setup and understanding of workflow-driven development

## Tags

`development` `planning` `implementation` `conductor` `automation`

## Related Scenarios

### Spec-Driven Development (Manual)

The original [Spec-Driven Development](../spec-driven-development/README.md) scenario uses individual agents and prompts that you invoke manually. Use it when you want more control over each step or are working on smaller tasks that don't need full automation.

| Scenario | Best For |
|----------|----------|
| **Conductor SDD** | Larger features, automated quality gates, hands-off execution |
| **Spec-Driven Development** | Smaller tasks, manual control, iterative exploration |
| **Research-First Development** | Complex/ambiguous work items, stakeholder alignment needed |

**Install both SDD variants**: `octane install conductor-sdd spec-driven-development`
