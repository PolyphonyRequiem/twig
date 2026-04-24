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

Through iterative discussion, we established design principles documented in
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
- **P8: Prefer Scripts Over Agents for Deterministic Logic** *(late addition)* —
  When a decision is deterministic and straightforward to implement as code, use a
  script rather than an LLM agent. Agents add latency, cost, and nondeterminism for
  zero value when the logic is just `if/else`. Discovered during post-implementation
  audit: the `planning_or_implementation` router was an LLM doing boolean logic that
  should have been a script or Jinja condition.

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

### Phase 2: Planning Workflow (`twig-sdlc-planning.yaml`) — IN PROGRESS

**Audit findings:** 8 hard violations, 8 soft. See `files/planning-audit.md` and
`files/planning-node-report.md` for full details.

**Hard violations fixed (commit `d8c1e28`):**
- P4: Removed `skip_plan_review`, replaced with `intent` input
- P2: Removed plan detection from intake (state_detector handles upstream)
- P9: Renamed `epic_id`/`epic_title` → `work_item_id`/`title` across all prompts
- P1: Updated work_tree_seeder prompt to tag items with PG-N and link plan artifact

**Structural redesign (in progress):**

Modular sub-workflow architecture inspired by cloudvault SDLC:
- `plan-design.yaml` — architect → reviewers → execution planner (NEW agent)
- `planning-pr.yaml` — commit plans to branch, push, link artifact, create PR (NEW)
- `plan-seeding.yaml` — create work items with PG tags, fan-out to child planners
- `plan-child.yaml` — replaces `plan-issue.yaml` with complexity-based routing

Agent/script split (P8): 8 scripts, 9 agents, 3 human gates. Key conversions:
- `duplicate_check`, `plan_check`, `review_router`, `plan_status_updater`,
  `branch_check`, `planning_branch_pusher`, `seeding_check`, `complexity_assessor`
  all converted from agents to deterministic scripts.

Scoring rubrics (P11): Technical and readability reviewers use dimension-by-dimension
scoring with 5 weighted dimensions each. Any dimension ≤ 2 = blocking issue.

Human gate confidence thresholds (P6 update): ≤85% during planning (surface early),
≥95% during implementation (only genuine blockers).

**Key design decisions:**
- Execution planner is separate from architect (cloudvault pattern). Architect designs
  WHAT, execution planner determines HOW (PG grouping). Exec planner can loop back
  to architect if PGs can't be self-contained.
- Planning PR is mandatory — plans committed to `planning/<id>` branch, linked to
  work item, PR created for design governance.
- Complexity threshold for child plans — ≥3 tasks or significant scope gets a plan
  doc; simple items get enriched work item descriptions instead.
- Idempotency checks at every sub-workflow entry (P3).

### Phase 3: Implementation Workflow (`twig-sdlc-implement.yaml`) — NOT YET STARTED

Key changes needed:
- `load-work-tree.ps1` reads PG tags from ADO work items instead of parsing plan text
- Fallback: single PG-1 for untagged legacy items
- `pr_finalizer` auto-approve already removed (done)
- Close-out agent removed from implementation (moved to apex)

## Implementation Progress

### Completed (Apex Workflow — Phase 1)
- `detect-state.ps1` — deterministic state detector script, tested against 4 scenarios
- `twig-sdlc-full.yaml` — fully rewritten with 6 nodes: state_detector, intent_correction_gate,
  cleanup, planning, implementation, close_out, retrospective
- `cleanup.prompt.md` + `cleanup.system.md` — cleanup agent for intent=redo
- `retrospective.prompt.md` + `retrospective.system.md` — postmortem review agent
- `pr-finalizer.prompt.md` — removed auto-approve (P7)
- `close-out.prompt.md` — Step 1c failure path, Step 1e rollback, Steps 9/10 guarded
- `planning_or_implementation` LLM router removed — replaced with Jinja `when` clause (P8)
- `implementation` input_mapping simplified — 2-way ternary, no `workflow.input` fallback
- Unused workflow files deleted (4 files, 898 lines removed)

### Completed (Planning Workflow — Phase 2, partial)
- `conductor-design/SKILL.md` — 11 design principles (P1-P11) documented
- Planning workflow: `skip_plan_review` removed, `intent` added, `epic_*` renamed
- `work-tree-seeder.prompt.md` — PG tagging and plan artifact linking added
- `intake.prompt.md` — plan detection removed (P2 fix)
- All prompts: `epic_id` → `work_item_id` bulk rename across 9 files
- `twig-sdlc/SKILL.md` — updated with new inputs (intent replaces skip_plan_review)

### Remaining (Planning Workflow — Phase 2)
- Create 4 sub-workflow YAML files (plan-design, planning-pr, plan-seeding, plan-child)
- Create execution_planner agent prompt
- Write 6 deterministic scripts (idempotency checks, review router, branch pusher)
- Update reviewer prompts with P11 rubrics
- Rewrite twig-sdlc-planning.yaml as modular orchestrator

### Remaining (Implementation Workflow — Phase 3)
- `load-work-tree.ps1` → read PG tags instead of parsing plan text
- Close-out extraction (already moved to apex level)
- Principle audit + redesign

### Dependencies
- **Issue #2059** (artifact link sync) — SDLC run dispatched, in progress.
  Soft dependency: state_detector uses filesystem fallback until this lands.

## Design Principles Evolution

Principles were established iteratively through discussion:

| # | Principle | When Added | Trigger |
|---|-----------|------------|---------|
| P1 | Work Items Are Source of Truth | Initial | Bug 2 (empty PG task_ids) |
| P2 | Plans Are Context, Not Control Flow | Initial | plan_detector violations |
| P2a | Plans Describe Solutions, Not Work Items | Mid-session | User correction on plan vs. work item scope |
| P3 | Re-Entry by State Discovery | Initial | No resume capability |
| P4 | Explicit Intent (new/redo/resume) | Initial | replan_gate as workaround |
| P5 | Type-Agnostic Workflow Structure | Initial | Epic-only naming |
| P6 | Human Gates for Genuine Decisions | Initial | Unnecessary gates |
| P6+ | Confidence thresholds (85%/95%) | Late | Planning vs implementation gate frequency |
| P7 | Fail Honestly, Don't Auto-Approve | Initial | pr_finalizer force-approve |
| P8 | Prefer Scripts for Deterministic Logic | Late | planning_or_implementation LLM doing if/else |
| P9 | Concise, Contextual Naming | Late | epic_id vs work_item_id |
| P10 | Explicit Invariants | Late | Missing pre/postconditions |
| P11 | Rubric-Based Scoring | Late | Single opaque 0-100 scores |

## Key Files

| File | Location | Purpose |
|------|----------|---------|
| `twig-sdlc-full.yaml` | `~/.conductor/registries/twig/recursive/` | Apex workflow ✅ REDESIGNED |
| `twig-sdlc-planning.yaml` | same | Planning sub-workflow (Phase 2, in progress) |
| `twig-sdlc-implement.yaml` | same | Implementation sub-workflow (Phase 3) |
| `plan-issue.yaml` | same | Per-Issue decomposition (being replaced by plan-child.yaml) |
| `detect-state.ps1` | `same/scripts/` | Deterministic state detector ✅ |
| `load-work-tree.ps1` | `same/scripts/` | Work tree loader (needs PG tag support) |
| `pr-finalizer.prompt.md` | `~/.conductor/registries/twig/prompts/` | PR verification ✅ FIXED |
| `close-out.prompt.md` | same | Close-out agent ✅ HARDENED |
| `cleanup.prompt.md` | same | Cleanup agent for intent=redo ✅ NEW |
| `retrospective.prompt.md` | same | Postmortem review ✅ NEW |
| `conductor-design/SKILL.md` | `.github/skills/conductor-design/` | Design principles (P1-P11) ✅ |
| `twig-sdlc/SKILL.md` | `.github/skills/twig-sdlc/` | Launch instructions ✅ UPDATED |
| `sdlc-redesign-journal.md` | `docs/projects/` | This document |
| `detect-state.ps1` | `same/scripts/` | New deterministic state detector |
| `load-work-tree.ps1` | `same/scripts/` | Work tree loader (needs PG tag support) |
| `pr-finalizer.prompt.md` | `~/.conductor/registries/twig/prompts/` | PR verification (fixed) |
| `close-out.prompt.md` | same | Close-out agent (hardened) |
| `conductor-design/SKILL.md` | `.github/skills/conductor-design/` | Design principles |
| `twig-sdlc/SKILL.md` | `.github/skills/twig-sdlc/` | Launch instructions (needs update) |
