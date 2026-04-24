# SDLC Workflow Redesign — Decision Journal

> This document captures the investigation, design decisions, and implementation
> progress for the twig SDLC conductor workflow redesign. It is intended as a
> handoff artifact for agents continuing this work.

## Background

The twig CLI uses a conductor-based SDLC pipeline to automate planning, implementation,
code review, PR management, and close-out for ADO work items. The pipeline is organized
as 4 workflow files:

1. **twig-sdlc-full.yaml** — apex entry point (routes to planning and/or implementation)
2. **twig-sdlc-planning.yaml** — planning sub-workflow (architect → review → seed → task decomposition)
3. **plan-issue.yaml** — per-Issue task decomposition sub-workflow
4. **twig-sdlc-implement.yaml** — implementation sub-workflow (code → review → PR → merge → close-out)

Workflows live in the conductor registry at `~/.conductor/registries/twig/recursive/`.
Prompts live at `~/.conductor/registries/twig/prompts/`.
The registry repo is [PolyphonyRequiem/twig-conductor-workflows](https://github.com/PolyphonyRequiem/twig-conductor-workflows).

NOTE: ALL ADO WORK ITEM REFERENCES ARE IN THE TWIG ADO REPOSITORY (See ~/projects/twig2/.twig/ for configuration details)

## What Triggered This Redesign

### Repeated "early completion" failures

Epics #1945 (Workspace Modes, 7 Issues) and #2014 (Artifact Linking, 4 Issues) were
dispatched through the SDLC pipeline multiple times. Each run "completed" after
implementing only a subset of the work — typically 2-3 of 7 Issues.

### Root cause investigation (April 23-24, 2026)

We traced the failures through conductor event logs and identified three bugs:

**Bug 1: Empty PG task_ids**
The architect writes a plan document with PG (PR Group) headings during the planning
phase. Much later, during implementation, `load-work-tree.ps1` parses the plan text
to rediscover PG structure by regex-extracting `#ID` references from PG sections.
Problem: the plan was written BEFORE ADO Tasks were created, so the IDs don't exist
in the plan. Result: PGs with `task_ids: []` → `task_manager` skips them → no code
written → `pr_group_manager` declares "all_complete" after processing empty PGs.

**Bug 2: pr_finalizer auto-approve**
`pr_finalizer.prompt.md` had: "If this is attempt 3 or higher and verification still
fails, set `verified: true` anyway." This force-approved even when 5 of 11 PGs had
no merged PRs, no branches, and no code. The close-out agent then ran on incomplete
work.

**Bug 3: Close-out premature tagging**
The close-out agent would create version tags and transition Epics to Done even when
only a subset of children were complete. (This was partially fixed earlier with
`epic_completed` guards in commit `e996e5b`, but the root causes above still allowed
the workflow to reach close-out prematurely.)

### Comparison with cloudvault SDLC

We examined the cloudvault SDLC (`~/projects/cloudvault-service-api/.conductor/workflows/`)
which is based on twig's but has evolved separately. Key differences:

- **Execution planner agent** — separate agent in planning that defines PGs before seeding
- **PG tags on work items** — `Custom.String6 = PG name` written during seeding
- **Deterministic load-work-tree.ps1** — parses plan + ADO state with fallback single PG
- **No auto-verify** — pr_finalizer does honest verification, no force-pass

## Design Principles

Through iterative discussion, we established 8 design principles documented in
`.github/skills/conductor-design/SKILL.md`. Key ones driving the redesign:

- **P1: Work Items Are Source of Truth** — ADO work items, not plan files, are
  authoritative for state. PG membership belongs on work items (tags), not in plan text.
- **P2: Plans Are Context, Not Control Flow** — Plans provide design rationale but
  don't drive routing, task assignment, or state transitions.
- **P2a: Plans Describe Solutions, Not Work Items** — The architect designs the solution;
  a separate seeder creates work items. Plans don't contain ADO IDs as operational data.
- **P3: Re-Entry by State Discovery** — Resume by inspecting observable state.
- **P4: Explicit Intent (new/redo/resume)** — Replaces ad-hoc flags like
  `skip_plan_review` and `plan_path`.
- **P7: Fail Honestly, Don't Auto-Approve** — No force-pass after N attempts.

## Redesign Scope

We're redesigning each workflow layer separately, starting with the apex workflow.

### Phase 1: Apex Workflow (`twig-sdlc-full.yaml`) — IN PROGRESS

**Current state (before redesign):**
```
Inputs: work_item_id?, prompt?, skip_plan_review?, plan_path?

plan_detector → replan_gate (human) → planning → implementation
```
- `plan_detector` scans filesystem for `.plan.md` files (P1/P2 violation)
- `replan_gate` asks "use existing plan?" (P4/P6 violation — no intent signal)
- `implementation` receives `plan_path` and parses plan for PG structure (P1/P2 violation)
- Close-out is buried inside implementation sub-workflow

**Redesigned:**
```
Inputs: work_item_id?, prompt?, intent? (default: resume), plan_path? (override only)

state_detector → intent_correction_gate? → cleanup? → planning → implementation → close_out → retrospective
```

7 nodes:

1. **state_detector** (deterministic PowerShell script) — validates inputs, inspects
   ADO state + plan artifact links, determines phase (done/needs_planning/ready_for_implementation).
   Does NOT do deep tree walks — sub-workflows own their own P3 discovery.

2. **intent_correction_gate** (human gate, conditional) — triggered when `intent=new`
   but prior work exists. Asks user: "Did you mean resume or redo?" This is a genuine
   P6 decision, not a routine checkpoint.

3. **cleanup** (agent, conditional on intent=redo) — closes children, abandons PRs,
   deletes branches, resets root to To Do, clears PG tags.

4. **planning** (sub-workflow) — architect → exec_planner → seeder, iterated per
   hierarchy level. Separate redesign scope.

5. **implementation** (sub-workflow) — discovers PGs from work item tags (not plan text).
   Separate redesign scope.

6. **close_out** (agent) — verifies all children Done, all PRs merged. Transitions
   root to Done, tags version. Partial completion: no tag, rollback orphans. (P7)

7. **retrospective** (agent) — postmortem: what went well/poorly/improve. Files
   closeout findings as ADO Issue.

**Key decisions made during design:**

- *Plan discovery via artifact links (P1):* Plans are linked to work items via
  `twig link artifact`. The state_detector reads these links instead of scanning
  the filesystem. Filesystem scan is a fallback until Issue #2059 (artifact link
  sync to local cache) lands.

- *State detector is minimal:* We considered having it walk the entire work tree
  to check planning completeness at every level. User feedback: "that's the planning
  sub-workflow's job." The apex state_detector just answers "what phase?" and lets
  sub-workflows do detailed P3 discovery.

- *Intent replaces ad-hoc flags:* `skip_plan_review` and `plan_path` (as primary input)
  are removed. `plan_path` is kept as a guarded debugging override only.

- *Close-out and retrospective at apex level:* These were previously inside the
  implementation sub-workflow. They span the entire lifecycle, so they belong at the
  top level.

- *No human gate for "use existing plan":* The `replan_gate` is removed. If `intent=resume`
  and plans exist, we skip planning automatically. The user already told us their intent.

### Phase 2: Planning Workflow (`twig-sdlc-planning.yaml`) — NOT YET STARTED

Planned approach: interleaved architect → exec_planner → seeder at each hierarchy level.
The architect says WHAT (design), the exec_planner says HOW (PGs, decomposition),
the seeder creates work items with PG tags. This replaces the current model where the
architect writes one big plan, then seeding happens in bulk.

### Phase 3: Implementation Workflow (`twig-sdlc-implement.yaml`) — NOT YET STARTED

Key changes needed:
- `load-work-tree.ps1` reads PG tags from ADO work items instead of parsing plan text
- Fallback: single PG-1 for untagged legacy items
- `pr_finalizer` auto-approve already removed (done)
- Close-out agent removed from implementation (moved to apex)

## Implementation Progress

### Completed
- `detect-state.ps1` — deterministic state detector script, tested against 4 scenarios
- `pr-finalizer.prompt.md` — removed auto-approve, increased retry logic, P7 compliance
- `close-out.prompt.md` — Step 1c failure path, Step 1e rollback, Steps 9/10 guarded (earlier commit `e996e5b`)
- `conductor-design/SKILL.md` — 8 design principles documented and cross-referenced
- Unused workflow files deleted (recursive-implementer, task-implementation, issue-review, integration-fix)

### Remaining
- `redesign-full-yaml` — rewrite `twig-sdlc-full.yaml` with new nodes and routing
- `cleanup-agent-prompt` — create cleanup agent prompt for intent=redo
- `pg-tag-seeder-update` — update seeder prompts to tag items with PG-N
- `load-work-tree-tags` — update load-work-tree.ps1 to read PG tags
- `update-skills` — update SKILL.md launch instructions

### Dependencies
- **Issue #2059** (artifact link sync to local cache) — dispatched as SDLC run, in progress.
  Soft dependency: state_detector uses filesystem fallback until this lands.

## Key Files

| File | Location | Purpose |
|------|----------|---------|
| `twig-sdlc-full.yaml` | `~/.conductor/registries/twig/recursive/` | Apex workflow (being redesigned) |
| `twig-sdlc-planning.yaml` | same | Planning sub-workflow (Phase 2) |
| `twig-sdlc-implement.yaml` | same | Implementation sub-workflow (Phase 3) |
| `plan-issue.yaml` | same | Per-Issue task decomposition |
| `detect-state.ps1` | `same/scripts/` | New deterministic state detector |
| `load-work-tree.ps1` | `same/scripts/` | Work tree loader (needs PG tag support) |
| `pr-finalizer.prompt.md` | `~/.conductor/registries/twig/prompts/` | PR verification (fixed) |
| `close-out.prompt.md` | same | Close-out agent (hardened) |
| `conductor-design/SKILL.md` | `.github/skills/conductor-design/` | Design principles |
| `twig-sdlc/SKILL.md` | `.github/skills/twig-sdlc/` | Launch instructions (needs update) |
