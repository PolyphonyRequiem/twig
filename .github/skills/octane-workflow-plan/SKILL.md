---
name: octane-workflow-plan
description: Run deep planning workflows via Conductor to produce design documents and implementation plans. Use when creating solution designs, architecture documents, or implementation plans. Builds on the conductor skill for execution.
user-invokable: true
---

# Deep Planning

Orchestrated planning workflow powered by the `conductor` skill. Produces a high-quality design and implementation plan through multi-agent review loops with quality gates.

> **This workflow is long-running** — typically 5–15 minutes per run. You MUST use `read_file` to load the full conductor skill instructions from the installed conductor skill's `SKILL.md` and follow its execution procedure exactly. **Always launch with `conductor --silent run ... --web-bg`** — this suppresses console noise and opens a real-time web dashboard.

## Prerequisites

- `conductor` skill — you MUST use `read_file` to load `~/.copilot/skills/conductor/SKILL.md` or `.github/skills/conductor/SKILL.md` for installation and execution details. Do NOT run `conductor --version` to check; just run the workflow directly and install only if the command fails.

## Known Issues & Error Handling

See the conductor skill's `SKILL.md` for known issues (including the Windows `fcntl` bug) and error handling procedures. If `conductor` fails, report the error and consult the conductor skill documentation before taking any action.

## Workflow

A single workflow template is included in `assets/` (relative to this skill):

| Workflow | Purpose | Key Inputs |
|----------|---------|------------|
| `plan.yaml` | Solution design + actionable implementation plan with epics/tasks | `purpose`, optional `output_path` |

## Quick Reference

```bash
# Plan: solution design + implementation plan
conductor --silent run assets/plan.yaml --input purpose="Build OAuth2 authentication with PKCE flow" --web-bg

# Plan: with explicit output path
conductor --silent run assets/plan.yaml --input purpose="..." --input output_path="docs/auth-migration.plan.md" --web-bg

# Dry run to preview execution
conductor run assets/plan.yaml --input purpose="..." --dry-run
```

> **No validation needed** — these are pre-packaged, tested workflows. You can skip `conductor validate`.

> **Always use absolute paths** for workflow templates — see the `conductor` skill's execution guidance.

> **Always share the dashboard URL** — after launching the workflow, provide the user with the web dashboard URL from the terminal output so they can track progress in real time.

## Plan Workflow

**Agents:** `architect` (Opus 1M) → `technical_reviewer` (Opus 1M) → `readability_reviewer` (Gemini) → loops until both scores ≥ 90

```
architect ──→ technical_reviewer ──→ approved? ──→ readability_reviewer ──→ approved? ──→ $end
                    │                                      │
                    └── score < 90 ──→ architect            └── score < 90 ──→ architect
```

- Architect researches the codebase + web, produces a combined design and implementation plan
- Technical reviewer validates accuracy, feasibility, codebase grounding, plan actionability
- Readability reviewer ensures clarity, structure, audience fit, traceability
- Loops back with feedback until both quality thresholds are met
- Outputs a `.plan.md` file

**MCP servers (embedded in workflow YAML, not scenario-level):** `web-search`, `context7`, `ms-learn`

**Output document sections:** Executive Summary, Background, Problem Statement, Goals/Non-Goals, Requirements, Proposed Design (architecture, components, data flow, API contracts, design decisions), Alternatives Considered, Dependencies, Impact Analysis, Security Considerations, Risks & Mitigations, Open Questions, Implementation Phases, Files Affected, Implementation Plan (epics with tasks and acceptance criteria), References

## Plan Document Format

Plan documents use `.plan.md` files with this structure:

```markdown
# Solution Design & Implementation Plan

## Executive Summary
## Background
## Problem Statement
## Goals and Non-Goals
## Requirements
## Proposed Design
   - Architecture Overview
   - Key Components
   - Data Flow
   - API Contracts
   - Design Decisions
## Alternatives Considered (optional)
## Dependencies
## Impact Analysis (optional)
## Security Considerations (optional)
## Risks and Mitigations (optional)
## Open Questions
## Implementation Phases
## Files Affected (New / Modified / Deleted tables)
## Implementation Plan
   - EPIC-001: [Goal, Prerequisites, Tasks table, Acceptance Criteria]
   - EPIC-002: ...
## References
```

Tasks use status tracking: `TO DO` → `IN PROGRESS` → `DONE`

## Tips

- **Confirm the purpose** — clarify the goal with the user before running; a well-defined purpose produces better output
- **Review output** — check the generated document before passing to implementation; adjust if needed
- **Dry run** — use `--dry-run` to preview the execution plan without running agents
