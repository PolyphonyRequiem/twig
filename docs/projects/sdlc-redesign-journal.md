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

---

## Phase 2 Completion: Planning Workflow (April 24, 2026)

### What was built

Rewrote `twig-sdlc-planning.yaml` from a monolithic 340-line workflow into a thin
orchestrator (160 lines) calling 3 modular sub-workflows:

1. **plan-design.yaml** — architect → reviewers (P11 rubrics) → execution planner (NEW).
   Execution planner defines PGs and can loop back to architect if PGs can't be
   self-contained. Review router converted from LLM to deterministic script (P8).

2. **planning-pr.yaml** — commits plans to `planning/<id>` branch, pushes, links
   artifact to work item, creates GitHub PR. All nodes are scripts except the
   (now also script) PR submit.

3. **plan-seeding.yaml** — creates ADO work items with PG tags and descriptions,
   fans out to plan-child.yaml per child.

4. **plan-child.yaml** — replaces plan-issue.yaml. Routes by complexity threshold:
   ≥3 tasks → child_architect (plan doc), else → description_enricher.

### Scripts created (P8)
- `check-plan.ps1` — idempotency: approved plan exists?
- `check-branch.ps1` — idempotency: planning branch exists?
- `check-seeding.ps1` — idempotency: children already seeded?
- `review-router.ps1` — deterministic gating math + feedback assembly
- `push-planning-branch.ps1` — git branch, commit, push, link artifact
- `assess-complexity.ps1` — child plan vs description enrichment threshold

### Prompts created/updated
- `execution-planner.system.md` + `.prompt.md` — NEW agent defining PGs
- `technical-reviewer.prompt.md` — P11 rubric (5 weighted dimensions)
- `readability-reviewer.prompt.md` — P11 rubric (5 weighted dimensions)
- All 24 system prompts — P10 invariants (pre/postconditions) added

### Audit results (post-redesign)
- **1 hard violation** — P1 filesystem fallback in plan_check (known, pending #2059)
- **6 soft violations** — all acceptable or deferred (description divergence, naming)
- **1 false positive reverted** — child_reviewer revision cap was incorrectly added

### Key commits
- `ec37f78` — scripts, prompts, rubrics (10 files)
- `df63407` — 4 sub-workflow YAMLs + orchestrator rewrite (5 files)
- `6e778c1` — P10 invariants on all 24 system prompts
- `ff66c6e` → `84de016` — soft violation fixes, reverted false positive

---

## Phase 3 Audit: Implementation Workflow (April 24, 2026)

### Audit findings

**Hard violations: 7**

| # | P | Node | Issue |
|---|---|------|-------|
| 1 | P2 | inputs | `plan_path` is a primary input — plans driving control flow |
| 2 | P1 | inputs | `child_issue_plans` — plan-centric mapping from old design |
| 3 | P4 | inputs | `prompt` — dead input, never used |
| 4 | P9 | intake | Still outputs `epic_id`, `epic_title`, `plan_already_approved` |
| 5 | P1/P2 | plan_reader | Entire agent exists to read plan and extract metadata |
| 6 | P1/P2 | work_tree_seeder | Passes `-PlanPath` to load-work-tree.ps1, extracts PGs from plan text |
| 7 | P7 | pr_finalizer | YAML routing still has `verification_attempt >= 3` auto-approve despite prompt fix |

**Critical finding:** pr_finalizer prompt was fixed (commit `e996e5b`) but the YAML
routing condition on line 512 still force-passes on attempt 3. The prompt says "fail
honestly" but the routing bypasses the result.

**Soft violations: 5** — duplicate_check as LLM, user_acceptance confidence threshold,
reviewers lacking P11 rubrics, pr_fixer no revision cap, no resume capability.

### Structural changes needed
1. Remove `plan_reader` — implementation discovers PGs from work item tags (P1)
2. Rewrite `load-work-tree.ps1` to read PG tags instead of plan text
3. Fix pr_finalizer YAML routing (remove `verification_attempt >= 3`)
4. Remove `close_out` (moved to apex level)
5. Clean up inputs (remove plan_path, child_issue_plans, prompt; add intent)
6. Rename `epic_*` outputs in intake
7. Add state discovery for resume capability (P3)

### Next step
Examining cloudvault implementation workflow for patterns to adopt.

---

## Phase 3 Completion: Implementation Workflow (April 24, 2026)

### What was changed

Comprehensive redesign of `twig-sdlc-implement.yaml` — all 7 hard violations resolved.

**Removed:**
- `plan_reader` agent — entirely gone. Implementation no longer reads plans.
- `close_out` agent — moved to apex workflow level.
- Inputs: `plan_path`, `child_issue_plans`, `prompt`, `work_item_type`
- All `plan_reader.output` references across 11 prompt files

**Rewritten:**
- `load-work-tree.ps1` — discovers PGs from ADO work item tags (P1). Falls back
  to single PG-1 if no tags. No plan text parsing.
- `pr_finalizer` YAML routing — removed `verification_attempt >= 3` auto-approve.
  Now retries up to 10, then terminates honestly (P7).
- `intake` outputs renamed: `epic_id` → `work_item_id`, etc. (P9)
- `intent` input added (P4)

**New scripts wired in:**
- `assess-staleness.ps1` — wired into pr_group_manager, runs per-PG before starting
- `post-merge-regression.ps1` — wired into pr_merge, runs after each PR merge
- Review cap in task_reviewer — 2 rejections then approve-or-escalate

**Prompt cleanups:**
- `pr-group-manager.prompt.md` — staleness check + branch-from-HEAD documented
- `pr-merge.prompt.md` — post-merge regression testing, removed plan status update
- `task-reviewer.prompt.md` — review cap, removed plan_reader reference
- No-nulls rule added to all 24 system prompts
- All remaining `plan_reader.output` references replaced (8 files)

### Cloudvault patterns adopted
1. PG discovery from work items (tags) not plan text
2. Staleness assessor per PG (proceed/adapt/replan)
3. Post-merge regression testing on main
4. Review caps with escalation (2 rejections → approve or escalate)
5. Branch creation from current HEAD for PG dependencies
6. No-nulls rule on all system prompts
7. PR finalizer as verification-only (no auto-approve)

### Key commits
- `94d868c` — major rewrite (load-work-tree, remove plan_reader/close_out, fix routing)
- `7eefaf7` — wire scripts into prompts, remove all plan_reader references

### Remaining (non-blocking, incremental improvements)
- Convert `duplicate_check` from LLM to script (P8)
- Add P11 rubrics to task_reviewer, issue_reviewer, pr_reviewer
- Resume state discovery at implementation entry (P3)
- Formal staleness gate (currently prompt-based, could be script + human gate)

---

## Summary: All Three Phases Complete

| Phase | Workflow | Status | Commits |
|-------|----------|--------|---------|
| 1 | `twig-sdlc-full.yaml` (apex) | ✅ COMPLETE | `3f1757a` → `26ad0af` |
| 2 | `twig-sdlc-planning.yaml` + 4 sub-workflows | ✅ COMPLETE | `ec37f78` → `84de016` |
| 3 | `twig-sdlc-implement.yaml` | ✅ COMPLETE | `94d868c` → `7eefaf7` |

### Design principles established: P1–P11
### Total files changed: ~50 across both repos
### Net result: plan-centric → work-item-centric SDLC pipeline

---

## Final Principles Audit (April 24, 2026)

### Result: 0 hard violations, 2 soft P9 concerns

Post-redesign audit across all 7 workflow files found zero hard violations.

Two minor P9 soft concerns:
- `branching` in planning orchestrator — does more than branch (also commits plans,
  creates PR). Acceptable in context of the 3-step flow (design → branching → seeding).
- `work_tree_seeder` in implementation — name says "seeder" but it loads/reads the tree.
  Leftover from when it actually created items. Could be `work_tree_loader`.

### Principles Evolution (final state)

| # | Principle | Summary |
|---|-----------|---------|
| P1 | Work Items Are Source of Truth | ADO state over plan files |
| P2 | Plans Are Context, Not Control Flow | Plans don't drive routing |
| P2a | Plans Describe Solutions, Not Work Items | Architect designs, seeder creates |
| P3 | Re-Entry by State Discovery | Resume via observable state |
| P4 | Explicit Intent (new/redo/resume) | Replaces ad-hoc flags |
| P5 | Type-Agnostic Workflow Structure | Same nodes for Epic/Issue/Task |
| P6 | Human Gates for Genuine Decisions | ≤85% planning, ≥95% implementation |
| P7 | Fail Honestly, Don't Auto-Approve | No force-pass after N attempts |
| P8 | Prefer Scripts for Deterministic Logic | if/else → script, judgment → agent |
| P9 | Clear, Minimal Naming | Clear in scope, minimal text, unambiguous at a glance |
| P10 | Explicit Invariants | Pre/postconditions on all agents |
| P11 | Rubric-Based Scoring | Weighted dimensions, academic grounding |

**P9 was refined late in the process** — original wording ("Concise, Contextual Naming")
led the audit agent to suggest overly verbose names like `"disambiguate_intent_conflict"`.
Updated to emphasize minimalism: "use as little text as needed to still be clear."

### What's left (non-blocking, incremental)

~~These are improvement opportunities, not violations:~~
~~- Convert implementation `duplicate_check` from LLM to script (P8)~~
~~- Add P11 rubrics to task_reviewer, issue_reviewer, pr_reviewer~~
~~- Rename `work_tree_seeder` → `work_tree_loader` in implementation (P9)~~
~~- Resume state discovery at implementation entry (P3 — conductor limitation)~~
~~- Human escalation gate on pr_finalizer after max retries (P7 polish)~~

**All resolved** — see post-audit fixes below.

---

## Post-Audit Fixes (April 24, 2026)

All five "non-blocking, incremental" items from the final audit were resolved:

| Item | Principle | Fix | Commit |
|------|-----------|-----|--------|
| `duplicate_check` → script | P8 | Converted from LLM to script in implementation | `6c73ef5` |
| P11 rubrics on reviewers | P11 | Added 5-dimension rubric to task_reviewer, issue_reviewer, pr_reviewer | `6c73ef5` |
| `work_tree_seeder` → `work_tree_loader` | P9 | Renamed in implementation yaml + all references | `6c73ef5` |
| Resume state discovery | P3 | `load-work-tree.ps1` detects completed PGs via merged PRs + task states. `pr_group_manager` reads `next_pg` to skip completed PGs on restart. | `4b8d60e` |
| pr_finalizer escalation | P7 | Added `verification_failure_gate` — human gate after 10 failed attempts | `6c73ef5` |

### P3 Resume Design

The resume mechanism works as follows:
1. `load-work-tree.ps1` queries `gh pr list --state merged` and cross-references
   PG branch names. Also checks if all tasks in a PG are Done.
2. Outputs `completed_pgs`, `pending_pgs`, and `next_pg` fields.
3. `pr_group_manager` reads `next_pg` on first invocation and starts from there.
4. If a run dies mid-PG-2 and is restarted, it skips PG-1 (already merged) and
   resumes at PG-2.

This was initially thought to be a conductor platform limitation. It's not — it's
just deterministic state discovery in the work tree loader script, exactly per P3
and P8.

---

## Final State

All SDLC workflow redesign work is complete. The pipeline has been transformed from
a plan-centric, monolithic design to a work-item-centric, modular architecture with:

- **11 design principles** (P1-P11) governing all workflow decisions
- **7 workflow files** (apex + 4 planning sub-workflows + implementation + plan-child)
- **10 deterministic scripts** replacing LLM agents for routing/validation
- **P11 rubrics** on all 5 reviewer agents (tech, readability, task, issue, PR)
- **P10 invariants** on all 24 system prompts
- **Resume support** via state discovery at every entry point
- **No auto-approve** anywhere — honest failure with human escalation
- **0 hard principle violations** in final audit

---

## Deployment Lessons (April 24, 2026 — first live test)

### Errors encountered during first launch

**Error 1: TemplateError — 'dict object' has no attribute 'work_item_id'**
- **Cause**: Conductor script agents don't parse stdout JSON into typed fields.
  `state_detector.output.work_item_id` doesn't exist — only `state_detector.output.stdout`
  (raw string) and `state_detector.output.exit_code` are available.
- **Fix**: Use `{{ state_detector.output.stdout }}` for prompt context (dumps the full
  JSON). Use `workflow.input.*` for sub-workflow input_mapping (the canonical source).
  Route via exit codes, not output field values.
- **Lesson**: Conductor script agents have three output channels:
  - `stdout` → available as `agent_name.output.stdout` (string)
  - `stderr` → available as `agent_name.output.stderr` (string)
  - `exit_code` → used for `when:` routing conditions
- **Feature request filed**: [microsoft/conductor#118](https://github.com/microsoft/conductor/issues/118)
  — allow script agents to declare output schemas for typed downstream access.

**Error 2: Stale github registry cache serving version 0.1.0**
- **Cause**: Conductor github registry mode resolved workflows by tag, not latest commit.
  Tags `0.1.0`, `1.0.0`, `2.1.0` all pointed to an ancient commit. New tag `3.0.0` was
  created but conductor still pulled the old cached version.
- **Fix**: Switched to path registry mode (local clone). Cleared `~/.conductor/cache/`
  globally. Path mode reads directly from the local clone — always current.
- **Lesson**: Github registry mode + tags requires understanding conductor's version
  resolution strategy. Path mode is simpler for development; github mode for distribution.

**Error 3: Overly strict 'seeded but no plan' error**
- **Cause**: `detect-state.ps1` treated "children exist but no discoverable plan file"
  as a corrupted state error. For existing epics planned per-issue (not per-epic), this
  is a legitimate state — children were created from per-issue plans that don't match
  the parent epic's ID.
- **Fix**: If children are seeded, route to implementation regardless of plan status.
  The plan is context (P2), not required for implementation to proceed.
- **Lesson**: P2 compliance means the system works WITHOUT plans. Plans enhance context
  but must never be a gate for execution when work items already exist.

**Error 4: TOML path escaping**
- **Cause**: `conductor registry add` wrote Windows backslashes into `registries.toml`.
  TOML interprets `\U`, `\t`, etc. as escape sequences.
- **Fix**: Manually corrected to forward slashes in `registries.toml`.
- **Lesson**: Always use forward slashes in conductor TOML config, even on Windows.

### Commits
- `1d00e46` — script output schema removal, exit code routing
- `194e660` — stdout for context, workflow.input for mapping, relaxed plan gate
- `748ce91` — remove script output field references from apex workflow output
- `346663d` — fix MCP server args indentation (collateral from script fix)
- `d25b440` — remove conditional plan_path args

---

## ⚠️ CRITICAL: Conductor Bug — `workflow.input` Empty in Script Agent Context

**Status:** Confirmed, reported to conductor team. **BLOCKS all script agents that
reference workflow inputs.**

### The Problem

Script agents (`type: script`) receive an EMPTY `workflow.input` dict `{}` in their
template rendering context, even when inputs are correctly passed via `--input` flags
and displayed in the UI.

LLM agents (`type: agent`) receive the correct populated `workflow.input`. Only
script agents are affected.

### How We Found It

After multiple launch failures with `TemplateError: 'dict object' has no attribute
'work_item_id'`, we monkey-patched `conductor.executor.template.TemplateRenderer.render()`
to dump the context:

```python
# Debug output:
TEMPLATE FAILED: '{{ workflow.input.work_item_id if workflow.input.work_item_id else 0 }}'
CONTEXT KEYS: ['context', 'workflow']
  workflow keys: ['input']
  workflow.input: {}      ← EMPTY! Should be {"work_item_id": 1945}
```

The workflow UI correctly shows `{"work_item_id": 1945}` — the inputs are parsed
and stored, but `ScriptExecutor` doesn't inject them into the template context.

### Root Cause

`conductor/executor/script.py` (lines ~86-87) builds the template context for script
agents. It includes `workflow` in the context but does NOT populate `workflow.input`
from the engine's input state. Compare with `executor/agent.py` which correctly
populates `workflow.input` for LLM agents.

### Impact on SDLC Redesign

This blocks ALL redesigned workflows. The `state_detector` script agent is the entry
point and needs `workflow.input.work_item_id` to function. Every deterministic script
node (P8) that references workflow inputs is affected.

### Workarounds

1. **Convert script agents back to LLM agents** — defeats P8 (determinism) but unblocks
2. **Pass inputs as environment variables** instead of template args — untested
3. **Hardcode values** — obviously not viable for reusable workflows
4. **Wait for conductor fix** — the fix is likely a 1-line change in `script.py`

### Debug Script

The monkey-patch debug script is at `tools/conductor-repro/debug_template.py`.
Run from any worktree to reproduce:
```
cd <worktree>
python tools/conductor-repro/debug_template.py
```

### Lessons

1. **Always get the actual traceback** — we spent hours hypothesizing about eager
   resolution, sub-workflow loading, YAML structure. The monkey-patch gave us the
   answer in 30 seconds.
2. **Don't trust "Cannot Reproduce"** — the conductor dev agent tested a simplified
   repro that worked. The real bug only manifests in script agents (not LLM agents).
3. **Template context ≠ workflow config** — conductor's UI shows correct inputs
   but the execution context is a different code path.
4. **P8 (scripts over agents) has a platform dependency** — deterministic scripts
   are only viable if the platform passes context to them correctly.

---

## Conductor Platform Fixes (April 24, 2026 — collaborative debugging session)

### Three conductor features shipped in one session

Working with the conductor development agent, we identified and fixed three
platform issues blocking the SDLC redesign:

| # | Issue | Fix | Status |
|---|-------|-----|--------|
| 1 | `workflow.input` empty `{}` in script agent context (explicit mode) | Script executor now populates workflow inputs into template context | PR #119, Issue #120 |
| 2 | No way to reference workflow file location in script args | Added `{{ workflow.dir }}`, `{{ workflow.file }}`, `{{ workflow.name }}` template vars | Installed locally |
| 3 | Script agent JSON stdout not parsed into `output.field` | Auto-parse JSON stdout, merge fields into output dict | Installed locally |

### Debugging methodology that worked

The key breakthrough was **monkey-patching `TemplateRenderer.render()`** to dump
the actual template context at the point of failure. This revealed `workflow.input: {}`
in 30 seconds after hours of wrong hypotheses. The debug script is at
`tools/conductor-repro/debug_template.py`.

### Script path resolution

Registry-based workflows have scripts co-located with the YAML files. But conductor
resolves paths relative to CWD (the worktree), not the workflow file.

- **Absolute paths** break portability (contain username)
- **Relative paths** resolve to CWD, not the registry
- **Solution**: `{{ workflow.dir }}/scripts/detect-state.ps1`

### Jinja `| default()` vs `if X else Y`

Conductor uses **StrictUndefined** Jinja mode:

```jinja
{{ workflow.input.prompt | default('') }}                    {# FAILS #}
{{ workflow.input.prompt if workflow.input.prompt else '' }}  {# WORKS #}
```

`| default()` catches undefined variables but NOT missing dict keys via attribute
access. Always use the `if X else Y` guard for optional workflow inputs.

### `duplicate_check` — script file never existed

Converted from LLM to `type: script` referencing a .ps1 that was never created.
PowerShell printed usage help, exited 0, silently skipped the check. Reverted to
LLM agent.

**Lesson**: When converting LLM → script (P8), verify the script file exists.

### Commits
- `53c9572` — `{{ workflow.dir }}` for portable paths
- `d9b5050` — revert to output.field routing
- `467caef` — revert duplicate_check to LLM
- `b3b9cb1` — guard optional inputs with if-else
- `8bebd28` — explicit default for optional prompt
- `cd96fae` — replace all `| default()` with if-else guards
- `97d4297` — add prompt input to implementation for shared prompt compat
- `8fbd660` — explicitly pass prompt='' in input_mapping

### Ongoing: Sub-workflow `workflow.input` propagation

**Status: BLOCKED on conductor platform behavior**

The shared `intake.prompt.md` template uses `{% if workflow.input.prompt %}` which
works in the planning sub-workflow (where `prompt` is passed via input_mapping) but
fails in the implementation sub-workflow even after:

1. Adding `prompt` to implementation's input declarations with `default: ""`
2. Explicitly passing `prompt: ""` in the apex's `input_mapping`
3. Using `if X else Y` guard pattern instead of `| default()`

The core issue: `workflow.input` inside a sub-workflow may not be populated with
all declared inputs + defaults. Only values explicitly in `input_mapping` from the
parent appear — and even explicit `prompt: ""` doesn't seem to make it through.

Event log shows the failure path clearly:
```
state_detector ✓ → implementation (sub-workflow) → duplicate_check ✓ → intake FAILS
```

This is filed with the conductor dev team for investigation. The fix is likely in
`_execute_subworkflow()` (workflow.py:606) where child engine context is built.

### Lessons learned during deployment

1. **Shared prompt files are a liability** — `intake.prompt.md` is used by both
   planning and implementation, but they have different input contracts. Shared
   prompts that reference optional inputs create fragile coupling.

2. **Jinja StrictUndefined + `| default()` is a trap** — the filter doesn't catch
   missing dict keys via attribute access. Always use `if X else Y` guards.

3. **Sub-workflow input propagation is opaque** — there's no way to inspect what
   `workflow.input` looks like inside a running sub-workflow without monkey-patching.

4. **Incremental testing is essential** — each fix revealed the next error deeper
   in the workflow chain. A comprehensive integration test before deploying the
   redesign would have caught these in sequence.

---

## The Real Root Cause: `context: mode: explicit` (April 24, 2026)

### What we thought was wrong

We spent hours debugging what we believed were conductor platform bugs:
- `workflow.input` empty in script agents (WAS a real bug, fixed by conductor team)
- `| default()` not working with StrictUndefined (real Jinja behavior, not a bug)
- Sub-workflow input_mapping not propagating (NOT a bug — our config was wrong)
- Template pre-resolution in sub-workflows (NOT happening — templates are deferred)

### What was actually wrong

**`context: mode: explicit`** means agents ONLY see inputs they explicitly declare
in their `input:` block. Every agent that references an upstream output
(`intake.output.work_item_id`, `pr_group_manager.output.action`, etc.) MUST list
that reference in its `input:` section.

The old workflow had this right. Our redesign renamed agents (`work_tree_seeder` →
`work_tree_loader`), converted some to scripts, and changed output schemas — but
didn't audit that every agent's `input:` block still declared all the upstream
references used in its `prompt:` or `args:` templates.

### The fix pattern

For each agent in explicit mode:
1. Find all `{{ X.output.Y }}` references in its `prompt:` or `args:`
2. Ensure `X.output` (or `X.output?` for optional) is in the agent's `input:` block
3. For shared prompts that reference optional inputs, use `workflow.input.X?`

### What's remaining

A systematic audit of ALL agents in `twig-sdlc-implement.yaml` to ensure their
`input:` blocks declare every upstream reference. This is mechanical work — the
pattern is clear, it just needs to be applied to ~15 agents.

### Progress through the deployment chain

Each fix got us one agent deeper:

```
state_detector  ✓ (exit code routing → JSON stdout parsing)
implementation  ✓ (sub-workflow routing)
duplicate_check ✓ (reverted to LLM, script file didn't exist)
intake          ✓ (added workflow.input.prompt? to input block)
work_tree_loader ← CURRENT (added intake.output to input block)
pr_group_manager ← NEXT (needs work_tree_loader.output + intake.output)
...
```

### Lesson

**When using `context: mode: explicit`, renaming or restructuring agents requires
a full audit of input declarations.** This should be a checklist item in the
conductor-design skill. Consider adding a P12 or appending to P10:

> When modifying agent names, types, or output schemas in explicit context mode,
> audit ALL downstream agents' `input:` blocks to ensure they still reference
> the correct upstream outputs.

### Commits
- `51b3cf1` — add prompt to intake explicit inputs
- `a8f22e0` — add intake.output to work_tree_loader inputs
- `a2781d3` — deduplicate intake.output refs, audit all input declarations
- `5255ed8` — rename work_tree_seeder → work_tree_loader in 5 prompts

### Updated deployment chain

```
state_detector       ✓ (script, JSON stdout → output.field routing)
implementation       ✓ (sub-workflow routing)
duplicate_check      ✓ (LLM agent, gracefully handles missing script)
intake               ✓ (added workflow.input.prompt? to input block)
work_tree_loader     ✓ (added intake.output to input block, script runs)
pr_group_manager     ✓ (deduplicated inputs, runs through task cycle)
...task/review loop  ✓ (reached pr_finalizer)
pr_finalizer         ✓ (fixed stale work_tree_seeder → work_tree_loader ref)
close_out            ✓ (fixed same stale reference)
```

### Stale reference pattern

When renaming agents (P9), we updated:
- ✅ The YAML agent `name:` field
- ✅ The YAML `input:` references in the same file
- ❌ Prompt `.md` files that use `{{ agent_name.output.X }}` templates

5 prompt files still referenced `work_tree_seeder` after the rename to
`work_tree_loader`. This is invisible to `conductor validate` since prompts
are treated as opaque strings until runtime.

**Lesson**: Agent renames require grep across BOTH `.yaml` AND `.md` files:
```
grep -r "old_name" recursive/ prompts/
```

---

## Output Schema / Prompt Mismatch Audit (April 25, 2026)

### Discovery

While launching SDLC runs for #2014 and #1945, we noticed `task_reviewer` had a
prompt that specified one set of output fields (with a `dimensions` scoring rubric and
`score` composite), but the YAML output schema declared a different, smaller set. The
agent would produce `dimensions` and `score` in its JSON output, but conductor would
drop them because they weren't in the schema.

### Systematic audit

We audited **all 11 workflow YAMLs** and **30+ prompt files** across the registry,
comparing each agent's YAML `output:` block against the output JSON examples in its
prompt file.

### Pattern: P11 scoring rubric added to prompts but not schemas

The P11 scoring rubric (`dimensions` + `score`) was introduced across all reviewer
prompts to provide per-dimension scoring breakdowns. The YAML output schemas were
never updated to capture these fields. This was consistent across **6 reviewer agents**
in the monolithic and recursive workflows. Only `plan-design.yaml`'s
`technical_reviewer` and `readability_reviewer` had `dimensions` declared correctly.

### Findings

| Severity | Agent | Workflow(s) | Missing Fields |
|----------|-------|-------------|----------------|
| Critical | `task_reviewer` | twig-sdlc.yaml, implement.yaml | `dimensions`, `score`, `review_attempt` |
| Critical | `issue_reviewer` | twig-sdlc.yaml, implement.yaml | `dimensions`, `score` |
| Critical | `technical_reviewer` | twig-sdlc.yaml | `dimensions` |
| Critical | `readability_reviewer` | twig-sdlc.yaml | `dimensions` |
| Critical | `pr_reviewer` | twig-sdlc.yaml, implement.yaml | `dimensions`, `score` |
| Moderate | `child_reviewer` | plan-child.yaml | `critical_issues` |

Additionally, `task_reviewer`'s prompt template referenced
`task_reviewer.output.review_attempt` for a review-cap mechanism (auto-approve or
escalate after 3 attempts), but `review_attempt` was never declared in the output
schema — so the review cap could never fire.

Two informational findings were noted but not fixed:
- `close_out` in twig-sdlc-full.yaml has a deliberately different schema than the
  main workflow (retrospective handles observations separately)
- `intake` uses different field names across workflows sharing the same prompt
  (YAML schema guides output, so this is fine)

### Fix

Added the missing fields to all affected YAML output schemas. Also updated
`task-reviewer.prompt.md` to include `review_attempt` in the example JSON output.

**Files changed:**
- `twig-sdlc/twig-sdlc.yaml` — technical_reviewer, readability_reviewer, pr_reviewer
- `recursive/twig-sdlc-implement.yaml` — task_reviewer, issue_reviewer, pr_reviewer
- `recursive/plan-child.yaml` — child_reviewer
- `prompts/task-reviewer.prompt.md` — added `review_attempt` to example JSON

### Lesson

**YAML output schemas and prompt output examples must stay in sync.** When adding
structured output patterns (like P11 scoring rubrics) to prompts, update the YAML
`output:` block in every workflow that uses that prompt. A quick way to audit:

```
# For each prompt with a ```json output block, extract the keys and compare
# against every YAML file that references that prompt via !file
grep -l "task-reviewer.prompt.md" recursive/*.yaml twig-sdlc/*.yaml | \
  xargs grep -A 30 "name: task_reviewer" | grep "type:"
```

This is the same class of problem as the stale reference pattern — prompts are
opaque strings to `conductor validate`, so schema drift is invisible until runtime.

---

## Resume State Reconciliation (April 25, 2026)

### Problem

Epic #1945 completed its SDLC run with **15 open tasks** — 10 under Dashboard
Rendering (#1951) with merged code but stale ADO states, and 4 genuinely unfinished
under Area Mode (#1949).

### Root cause analysis

The designed responsibility chain works correctly in the **happy path**:

| Transition | Owner | Trigger |
|---|---|---|
| Task → Done | `task_manager` | After `task_reviewer` approves |
| Issue → Done | `pr_group_manager` | After PR merge confirmed |
| Epic → Done | `close_out` | After all children Done |

On **resume**, the gap manifests:

1. `load-work-tree.ps1` marks PGs as "completed" when a merged PR exists
   (Condition A), OR when all tasks are Done without a merged PR (Condition B)
2. `pr_group_manager` skips completed PGs without reconciling ADO states
3. Tasks/Issues orphaned in "Doing" or "To Do" are never fixed
4. `pr_finalizer` verifies PRs and Issues but **never checks Task states**
5. `close_out` detects non-Done children → blocks Epic transition → rolls back
   orphaned "Doing" items to "To Do" (making things worse)

### Fix: Three-part approach

**1. `load-work-tree.ps1` — detect reconciliation needs**
- Changed PG completion logic to merged-PR-only (removed Condition B, which could
  mark a PG complete when all tasks are Done but no PR exists — hiding unmerged code)
- Added `pgs_needing_reconciliation` output: PGs with merged PRs but non-Done
  tasks/issues, including specific `non_done_task_ids` and `non_done_issue_ids`

**2. `pr-group-manager.prompt.md` — resume reconciliation step**
- Added mandatory "Resume Reconciliation" section after existing "Resume Support (P3)"
- On first invocation, before starting `next_pg`, iterate over
  `pgs_needing_reconciliation` and transition all orphaned tasks → Done and
  issues → Done with reconciliation notes
- Added Task ownership carve-out to `pr-group-manager.system.md` — explicitly
  scoped to first-invocation resume only

**3. `pr-finalizer.prompt.md` — defense-in-depth**
- Added Step 5b: verify all Tasks in completed PGs are Done
- Any non-Done Task is a `state_violation` — catches anything the reconciliation
  step missed

### Design decision: why pr_group_manager and not a new agent?

Options considered:
- **New `state_reconciler` script agent** — clean separation but adds another agent,
  duplicates PR-matching logic, and runs after pr_group_manager exits (too late for
  its tracking)
- **Fix in `close_out`** — last line of defense but violates ownership model
  (close_out owns Epic, not Tasks/Issues)
- **Fix in `pr_group_manager`** (chosen) — reconciliation happens at the right time
  (before new work starts), uses the agent that already owns Issue closure, minimal
  new code, and the Task ownership carve-out is clearly scoped to resume-only

### Condition B removal rationale

The old `load-work-tree.ps1` line 194 had:
```powershell
$pg.completed = ($matchedPR.Count -gt 0) -or ($pg.task_ids.Count -gt 0 -and $pgTasksDone)
```
Condition B ("all tasks Done, no merged PR") masked a dangerous state: code
implemented and committed on a feature branch, all tasks closed in ADO, but the PR
was never created or merged. The code lives on a branch that never reaches main.
`pr_finalizer` catches this via its unmerged-branch check, but the routing back to
`pr_group_manager` is awkward because the resume logic would skip the PG.

Removing Condition B means: if there's no merged PR, the PG is pending → the workflow
will attempt to submit a PR → `pr_submit` creates the PR → normal flow continues.

### Stale cache fast-path bug (discovered during relaunch)

After applying the reconciliation fix, the relaunch of #1945 completed in **7 seconds**.
The `state_detector` script read `work_item_state: "Done"` from the worktree's local
`.twig` cache and routed straight to `$end`.

**Root cause**: We changed #1945 to "Doing" via MCP (which writes to the main project's
`.twig`), then copied that `.twig` to the worktree, then ran `twig sync`. However,
the prior SDLC run's `close_out` agent (still running at the time) transitioned #1945
back to "Done" **after** our state change — overwriting it. When we re-copied and
re-synced, the cache correctly reflected ADO's "Done" state.

**Fix**: Added `twig sync` as **Step 0** in both deterministic scripts:
- `detect-state.ps1` — syncs before reading work item state for routing decisions
- `load-work-tree.ps1` — syncs before loading PG structure and completion status

This ensures scripts always operate on fresh ADO data, regardless of when the `.twig`
folder was last copied or how stale the local SQLite cache might be.

**Lesson**: Deterministic scripts that read ADO state via the twig CLI must sync
first. The `.twig` cache is not a reliable source of truth — it's a local snapshot
that can be invalidated by other agents, manual changes, or race conditions with
concurrent workflow runs.

### Early push strategy (crash recovery)

Added pre-emptive branch pushing for crash durability:
- `pr-group-manager.prompt.md` — `git push -u origin <branch>` immediately after
  branch creation (both first PG and after-merge PG transitions)
- `coder.prompt.md` (main + recursive) — `git push` after every task commit

Previously, all commits were local-only until `pr_submit` pushed the branch. If
the workflow crashed mid-implementation, all work was lost. The early push strategy
ensures committed work is durable on the remote at every step.

### Drop `--silent` from launch commands (logging visibility)

Changed the launch strategy from `conductor --silent run ... --web` to
`conductor run ... --web` with `-WindowStyle Hidden`.

**Why `--silent` was harmful**: With `--silent`, the conductor log captured via
`Tee-Object` contained only the dashboard URL and "Workflow complete." — no agent
routing, no output, no errors. When #1945 completed in 7 seconds due to stale
cache, we had zero diagnostic information in the log. The dashboard shows real-time
state but has no persistent log export.

**New approach**: Drop `--silent`, use `-WindowStyle Hidden` on `Start-Process`.
Full agent output is captured in `conductor.log` via `Tee-Object`, the hidden
window keeps the terminal clean, and the web dashboard still provides real-time
monitoring. Post-mortem analysis can now grep the log for routing decisions,
agent outputs, and error messages.

Updated the twig-sdlc skill doc (`SKILL.md`) — all launch commands, including
the Quick Reference and both multi-run Options A and B.

### `twig link branch` incompatible with GitHub repos

Investigated using `twig link branch` to link feature branches to work items
in the SDLC workflow. Found that `BranchCommand.cs` constructs a
`vstfs:///Git/Ref/{projectId}/{repoId}/GB{branch}` URI, which requires an ADO
Git repository. `GetRepositoryIdAsync()` queries the ADO Git API — for a
GitHub-hosted repo, this returns `null` and the link silently fails.

The alternative — `twig link artifact <github-url> --name "PG-N Branch"` — works
but requires the branch to exist on the remote first. The early push strategy
(above) now enables this, but the timing is awkward: `pr_group_manager` creates
and pushes the branch, but the Issue IDs for linking are known at that point too.
Deferred as a follow-up — AB# references in PR bodies already provide ADO↔GitHub
linkage for most use cases.

### Reconciliation too aggressive — false-positive task closure (discovered during relaunch)

After the reconciliation fix, #1945's relaunch completed in 6 minutes. The
reconciliation worked mechanically — but it was too blunt. It closed ALL non-Done
tasks in completed PGs, including Area Mode tasks #2005, #2007, #2001 that had
**no implementation code**. Those tasks were in "To Do" (never started), but the
reconciliation closed them just because the PG's PR was merged.

**Root cause**: The PG contained tasks from multiple Issues (Area Mode + Dashboard
Rendering). The PR only covered Dashboard Rendering tasks. The reconciliation didn't
distinguish "To Do — genuinely unstarted" from "To Do — code merged but state never
transitioned."

**Fix — surgical reconciliation**:

| Task State | Action | Rationale |
|---|---|---|
| "Doing" | Close → Done | Crash interrupted the Done transition — code was being worked on |
| "To Do" | Leave open | May be genuinely unimplemented — no evidence of work |
| Issues | Close → Done | Issues represent PG-level scope; the PR covers the Issue |

Updated `load-work-tree.ps1` to output `stale_doing_task_ids` (only "Doing" tasks)
separately from `skipped_todo_task_ids` (reported for visibility but not closed).
Updated `pr-group-manager.prompt.md` to close only stale "Doing" tasks and Issues,
explicitly instructing agents to NOT close "To Do" tasks.

**Tradeoff**: Some "To Do" tasks might have merged code (their state was never moved
to "Doing" before the crash). These will remain as false negatives — but that's safer
than false-positive closure of genuinely unimplemented tasks. The user can manually
close them or a future run will implement them.

**Lesson**: Reconciliation actions should match the strength of evidence. A merged
PR is strong evidence for Issue closure (Issues map 1:1 to PG scope) but weak evidence
for individual Task closure (a PG may contain tasks from multiple Issues, and the PR
may not cover all of them). Task "Doing" state is the strongest signal that work was
interrupted — "To Do" provides no signal either way.

### Stale cache + close_out race condition (second relaunch failure)

After adding `twig sync` to `detect-state.ps1`, the relaunch still completed in
6 minutes because the **prior run's close_out had transitioned all 7 Issues to Done**.
We only reverted the Epic state — not the child Issues. The `state_detector` saw
Epic=Doing + all Issues=Done → `ready_for_implementation` → `work_tree_loader` →
`pr_group_manager` → `all_complete` → `close_out` → Done again.

**Event log analysis** (found at `$env:TEMP\conductor\*.events.jsonl`):
```
08:28:19  state_detector   → implementation (Epic=Doing, all 7 Issues=Done)
08:28:57  work_tree_loader → pr_group_manager (⚠ No PG tags, single PG-1, all Done)
08:29:42  pr_group_manager → pr_finalizer     (all_complete after 35s)
08:30:53  close_out        → retrospective    (transitioned Epic to Done)
```

**Additional finding**: `work_tree_loader` reported "No PG tags found on work items"
— the prior SDLC runs never applied PG tags to the newer task decomposition
(tasks 1996-2007, 1968-1977). The plan referenced old task IDs (1961, 1962, 1964,
1965) that predated the re-seeded tasks.

### Manual recovery: revert + re-tag

To properly re-run #1945 Area Mode, we manually:

1. Reverted Issue #1949 (Area Mode) from Done → Doing
2. Reverted 4 genuinely unfinished Tasks from Done → To Do:
   - #2004 (area-filtered view implementation)
   - #2005 (SpectreRenderer area-view rendering)
   - #2007 (area-view rendering tests)
   - #2001 (area command unit/integration tests)
3. Applied `PG-2` tags to all 4 tasks + Issue #1949
4. Reverted Epic #1945 from Done → Doing
5. Relaunched without `--silent` for full log visibility

**Lesson**: When a close_out agent has poisoned the tree (closing items with no
code), recovery requires reverting BOTH the parent AND the incorrectly-closed
children. Reverting only the parent is insufficient — the children's Done state
propagates up through `children_summary` and `work_tree_loader`, causing the
workflow to treat everything as complete.

**Lesson**: PG tags are the operational contract between planning and implementation.
If the seeder creates new tasks that don't match the plan's PG definitions, those
tasks are invisible to the PG-based routing in `load-work-tree.ps1`. The fallback
(single PG-1 with all items) masks this — it silently groups everything together
and marks the PG complete if any merged PR exists.

### `twig tree --depth 2` grandchild bug — the foundational failure (April 25, 2026)

While investigating why `load-work-tree.ps1` always reports `total_tasks: 0`, we
discovered that `twig tree --depth 2 --output json` from Epic level **never returns
grandchildren**. The `--depth` parameter is effectively display-only.

**Three-layer gap:**
1. `SqliteWorkItemRepository.GetChildrenAsync` — `WHERE parent_id = @parentId` (one level)
2. `TreeCommand.cs:188` — calls `GetChildrenAsync` once, no recursion
3. `WorkTree.Children` — flat `IReadOnlyList<WorkItem>`, no nesting support
4. `JsonOutputFormatter.FormatTree` — serializes as flat array

This means **every SDLC feature that depends on task-level visibility from Epic
focus has never worked**: PG tag detection on tasks, task completion status,
resume reconciliation, and the pr_finalizer task state check we just added.
The entire `load-work-tree.ps1` task-level logic was flying blind.

Verified with Epic #2014 (different Epic, same result: `grandchildren=0`). From
Issue focus, `twig tree --depth 2` correctly returns 14 children. The bug is
specific to the Epic→Issue→Task chain in JSON output.

### close_out overwrite: not a close_out bug

Investigation of close_out confirmed it has proper guards:
- Step 1c blocks Epic closure when children aren't Done
- Step 1e only rolls back orphaned "Doing" items
- It does NOT blanket-close all children

The overwrite chain is upstream:
1. `load-work-tree.ps1` uses broken `twig tree --depth 2` → 0 tasks
2. Without tasks, PGs appear complete → `pr_group_manager` → `all_complete`
3. `close_out` sees all Issues Done → transitions Epic to Done

The fix is two-pronged:
- **#2069**: Fix `twig tree --depth N` to return nested grandchildren in JSON
- **#2070**: Add a close_out workaround — drill into each Issue individually
  (`twig set <id>` → `twig tree`) to verify Tasks before Epic closure

### SDLC runs launched for fixes

Both Issues created as root-level ADO Items (no parent Epic) and launched with
`intent=new` through the full SDLC pipeline:

| Issue | Title | Dashboard |
|-------|-------|-----------|
| #2069 | twig tree --depth N: JSON output missing grandchildren | http://127.0.0.1:52456 |
| #2070 | SDLC close_out: drill into Issues to verify Task states | http://127.0.0.1:56253 |

### Process note: `Start-Process` detachment

First launch attempt used `-WindowStyle Hidden` but processes still died when the
Copilot CLI session reconnected. `Start-Process` on Windows creates child processes
that inherit the parent's job object — if the parent session terminates, the children
may be killed.

Reliable detachment requires `Start-Process` from a non-child pwsh session (no
`detach: true` equivalent in the powershell tool). The second launch worked because
the sync mode `Start-Process` call completes before any session disruption.

**Lesson**: For long-running conductor processes, always verify the process tree
is alive after launch with `Get-CimInstance Win32_Process`.

### Shared prompt template error: `intake` undefined in sub-workflow (April 25, 2026)

Both #2069 and #2070 SDLC runs crashed at the `architect` agent with:
```
TemplateError: Undefined variable in template: 'intake' is undefined
```

**Root cause**: `architect.prompt.md` references `intake.output.work_item_id`,
`intake.output.title`, etc. In the monolithic workflow, `intake` is a sibling
agent and this works. In the recursive architecture, `intake` lives in the parent
workflow (`twig-sdlc-planning.yaml`) and passes its data via `input_mapping` to
`plan-design.yaml` as `workflow.input.*`. The architect agent in `plan-design.yaml`
has `workflow.input` in its input block — but the prompt hardcodes `intake.output`.

**Why validation didn't catch it**: `conductor validate` checks template references
within a single workflow file. Cross-sub-workflow references (parent's agent output
used in child's prompt) are invisible to the validator. The prompt is treated as an
opaque string.

**Fix**: Jinja `{% set %}` variable aliasing at the top of `architect.prompt.md`:
```jinja
{% set wi_id = intake.output.work_item_id if intake is defined else workflow.input.work_item_id %}
{% set wi_title = intake.output.title if intake is defined else workflow.input.title %}
...
```
This makes the prompt work in both contexts — monolithic (intake exists) and
sub-workflow (intake undefined, data in workflow.input).

**Lesson**: Shared prompts used across workflow boundaries must guard every
cross-scope reference with `if X is defined else` fallbacks. This is the same
class of problem as the `| default()` trap documented earlier — but for
agent-scope references rather than workflow inputs.

**Lesson**: Always validate AND do a test launch before declaring runs ready.
Validation catches structural errors; test launches catch template resolution
errors that only manifest at runtime.

### The real root cause: `- workflow.input` doesn't work in explicit mode (April 25, 2026)

After four failed launch attempts, traced the actual failure through conductor's
source code (`engine/context.py:242-293`).

**The bug**: In `plan-design.yaml`, the architect agent had:
```yaml
input:
  - workflow.input           # ← 2-part reference
```
In explicit mode, `_add_explicit_input()` only handles `workflow.input.<param_name>`
(3+ parts, line 279). The 2-part `workflow.input` falls through to the agent output
lookup branch, finds nothing, and the entire `workflow.input` dict stays empty.

**The fix**: Expand to individual 3-part references:
```yaml
input:
  - workflow.input.work_item_id
  - workflow.input.title?
  - workflow.input.description?
  - workflow.input.item_type?
  - workflow.input.existing_issues?
  - workflow.input.intent?
  - workflow.input.prompt?
```

**Additional fixes needed along the way**:
- `architect.prompt.md` — rewrote to use `workflow.input.*` directly instead of
  `intake.output.*` (which leaks from parent as a flat dict without `.output` wrapper)
- `architect.system.md` — removed hardcoded `intake.output.work_item_id` reference
- `technical-reviewer.prompt.md` — same `workflow.input.*` rewrite
- `execution-planner.prompt.md` — same `workflow.input.*` rewrite

**Why it took 4 attempts**:
1. First fix: Jinja `intake is defined` guard → failed because conductor leaks
   parent agent outputs into sub-workflow context as flat dicts (no `.output` wrapper)
2. Second fix: Deep attribute guards with `intake.output is defined` → failed because
   Jinja StrictUndefined raises on `.output` attribute access before `is defined` evaluates
3. Third fix: Rewrote to use `workflow.input.*` directly → failed because
   `- workflow.input` (2-part) doesn't populate anything in explicit mode
4. Fourth fix: Expanded to `- workflow.input.<param_name>` (3-part) → success

**Lesson**: In explicit mode, `workflow.input` as an input reference is a no-op.
Always use the full 3-part path `workflow.input.<param_name>`. This should be
added to the conductor-design skill as a principle or FAQ.

**Lesson**: Conductor leaks parent agent outputs into sub-workflow template contexts
as flat dicts. `intake` in a sub-workflow is `{"work_item_id": 2070, "title": "..."}`,
NOT `{"output": {"work_item_id": 2070}}`. Shared prompts must not assume the
`.output` namespace wrapper — use `workflow.input` which is consistent across contexts.

### Conductor platform fix: `workflow.input` 2-part form (April 25, 2026)

Filed a bug report with the conductor dev. Fix shipped same-day:

**Fix** (3 files in conductor):
- `engine/context.py` — added `elif len(parts) == 2 and parts[1] == "input"` branch
  that merges all workflow inputs via `.update()`
- `config/validator.py` — updated regex to accept `workflow.input` (no param) as valid
- 5 new tests covering the 2-part form, merge behavior, and optional variant

**Issue 2 (parent output leaking without `.output` wrapper) — not a bug**:
The conductor dev confirmed the unwrap/re-wrap flow is correct: line 639 strips
the `.output` wrapper via `value.get("output", value)`, then `build_for_agent`
re-wraps with `{"output": ...}`. Templates using `intake.output.work_item_id`
should resolve correctly in sub-workflows. Our failures were caused by Issue 1
(empty `workflow.input`), not by missing `.output` wrappers. The checkpoint file
showing a flat dict was misleading — that's the serialization format, not the
runtime template context.

**Status**: Fix installed locally. Our workaround (`- workflow.input.<param>` expanded
form) remains in place and is harmless — it's more explicit and works with or without
the fix. Can revert to `- workflow.input` now that the fix is live, but no urgency.

Also deleted the monolithic `twig-sdlc/` workflow directory from the registry.
The recursive architecture is now the only active workflow.

### Process detachment remains unsolved (April 25, 2026)

`Start-Process -WindowStyle Hidden` does NOT survive Copilot CLI session drops.
Processes launched this way are still children of the pwsh session and get killed
when the session reconnects. We lost runs multiple times today to this.

The twig-sdlc skill doc says "use `detach: true`" but that's a Copilot CLI async
shell feature, not applicable to `Start-Process`. No reliable cross-session
detachment method found yet for Windows. Current best practice: verify processes
are alive after launch and accept that session drops will kill them.

### Full audit: `intake.output` and bare `workflow.input` references (April 25, 2026)

Systematic audit of all workflow YAMLs and prompt files for two classes of bugs:
1. **Bare `- workflow.input`** (2-part) in explicit mode — silently injects nothing
2. **`intake.output.*`** in prompts used by sub-workflows where intake is not a sibling

**Bare `workflow.input` refs found and expanded (6 total):**
- `plan-design.yaml` — technical_reviewer, execution_planner (2)
- `plan-child.yaml` — child_architect, child_reviewer, description_enricher (3)
- `plan-seeding.yaml` — work_tree_seeder (1)
All expanded to `- workflow.input.<param_name>` (3-part form).

**`intake.output` refs in cross-boundary prompts found and fixed (1 new):**
- `work-tree-seeder.prompt.md` — 7 refs, used in `plan-seeding.yaml` (no intake)
  Rewrote all to `workflow.input.*`. Added `title` and `item_type` to
  `plan-seeding.yaml` input declarations and parent `input_mapping`.

**Previously fixed:** architect.prompt.md, architect.system.md,
technical-reviewer.prompt.md, execution-planner.prompt.md

**Safe (no action needed):** 8 prompts with `intake.output` refs that are only
used in `twig-sdlc-implement.yaml` where `intake` IS a sibling agent:
closeout-filer, coder, issue-reviewer, plan-reader, pr-reviewer, reducer-issue,
reducer-pr, task-manager.

**Root cause pattern**: The recursive architecture split workflows into
sub-workflows but continued sharing prompt files written for the monolithic
architecture. Prompts that reference sibling agents (`intake.output.*`) break
when the agent lives in a different workflow scope. The fix is to use
`workflow.input.*` which is consistently available via `input_mapping`.

### Script agents also need `input:` blocks in explicit mode (April 25, 2026)

Two more crashes after the initial audit, both from script agents missing `input:`:

1. **`review_router`** in `plan-design.yaml` — script agent referencing
   `technical_reviewer.output.score` and `readability_reviewer.output.score` in args.
   No `input:` block → `'technical_reviewer' is undefined`. Fixed by adding
   `input:` with both reviewer outputs plus `architect.output` and `workflow.input.intent?`.

2. **`plan_status_updater`** in `plan-design.yaml` — inline script referencing
   `architect.output.plan_path` in args. No `input:` block → `'architect' is undefined`.
   Fixed by adding `input: - architect.output`.

3. **`architect`** self-reference — missing `architect.output?` for revision loops
   (iter 2+). The prompt references `architect.output.plan_revision_count` and
   `architect.output.plan_path` when revising. Fixed.

4. **`execution_planner`** self-reference — missing `execution_planner.output?` for
   retry loops. Fixed.

Also added `input:` blocks to all 3 script agents in `planning-pr.yaml`:
`branch_check`, `branch_pusher`, `pr_submit`.

**Definitive audit method** — grep ALL `{{ X.output.Y }}` template refs in YAML
args/inline prompts and cross-reference against each agent's `input:` block.
This catches script agents that the earlier prompt-file-based audit missed
(inline `-Command` templates don't use `!file`).

### `gh pr create` hangs headless — need preflight checks (April 25, 2026)

Both #2069 and #2070 completed the full planning pipeline (architect → review →
approval → branch push) then hung indefinitely at `pr_submit` — `gh pr create`
was blocked on an auth prompt that would never receive input. The 30+ minutes of
Opus planning time was wasted because an external dependency was broken.

**Proposed fix: preflight check agent**

Add a `preflight_check` script agent as the very first node in `twig-sdlc-full.yaml`
(before `state_detector`) and optionally in each sub-workflow for decoupled usage.

```
preflight_check (script) → preflight_gate (human gate, conditional)
  → state_detector → ...
```

**Required checks (block if missing):**
- `gh auth status` — GitHub CLI authenticated and token valid
- `gh api user` — GitHub API actually reachable (not just cached token)
- `twig set <id>` — twig CLI working, ADO reachable
- `dotnet --version` — .NET SDK installed
- `git status` — repo accessible, not in broken state

**Strongly advised (warn via human gate):**
- `conductor --version` — expected version installed
- MCP server binary exists (`twig-mcp` at expected path)
- Worktree has `.twig/` with valid config
- Network connectivity to dev.azure.com and github.com

**Routing:**
- All required pass → proceed to `state_detector`
- Any required fail → `preflight_gate` (human gate) with failure details
  and remediation steps (e.g., "Run `gh auth login` to authenticate")
- Only warnings → proceed with a note in `progress_summary`

**Why at the apex level**: Sub-workflows (`plan-design`, `plan-seeding`,
`planning-pr`) are called from the planning workflow which is called from the
apex. Checking at the apex catches all dependencies before any sub-workflow runs.
Individual sub-workflows could also include lighter checks for decoupled usage.

**Status**: Filed as ADO Issue #2071. Includes `gh api repos/:owner/:repo` push
permission check (not owner match — contributors with push access are valid) and
`gh repo set-default` per worktree.

### `gh pr create --json` doesn't exist + wrong account (April 25, 2026)

Both #2069 and #2070 hung for 35+ minutes at `pr_submit` in `planning-pr.yaml`.
Two bugs:

1. **`--json number,url` flag** on `gh pr create` — doesn't exist (only on
   `gh pr list/view`). The command errored and hung waiting for input instead
   of exiting.

2. **Wrong `gh` account active** — `dangreen_microsoft` was active but repo is
   `PolyphonyRequiem/twig`. Push access denied (403). The `branch_pusher`
   script may have used cached git credentials, masking the account mismatch.

**Fixes:**
- Removed `--json` from `gh pr create`; parse PR number from the returned URL
  (`https://github.com/.../pull/100` → extract `100`)
- Switched active account: `gh auth switch --user PolyphonyRequiem`
- Set per-worktree default: `gh repo set-default PolyphonyRequiem/twig`
- Added `gh repo set-default` to the worktree setup in the launch sequence

**Lesson**: Script agents that shell out to CLI tools (`gh`, `twig`, `dotnet`)
must handle non-interactive failure gracefully. A script that hangs on stdin
is worse than one that crashes — the crash surfaces immediately, the hang
burns the entire workflow budget silently.

### `load-work-tree.ps1` — two critical fixes for Epic-level runs (April 25, 2026)

Both #2069 and #2070 completed the full planning pipeline, seeded work items, then
`pr_group_manager` declared `all_complete` without implementing anything. The
`pr_finalizer` correctly caught this (P7: Fail Honestly) and looped 10 times before
hitting the `verification_failure_gate`. Root cause: two bugs in `load-work-tree.ps1`.

**Bug 1: Zero tasks from Epic focus (the tree depth workaround)**

The script used `twig tree --depth 2` from Epic focus and expected grandchildren
(Tasks under Issues) in `$child.children`. Due to the twig tree depth bug (#2069),
this always returns 0 grandchildren → `total_tasks: 0` → PGs appear complete.

**Fix**: Added per-Issue drill-down fallback. When `$child.children` is empty,
the script now does `twig set <issue_id>` → `twig tree --depth 1` to fetch that
Issue's Tasks directly. This works because depth-1 (direct children) is fine —
only depth-2+ (grandchildren from a parent) is broken. After drilling into all
Issues, restores focus to the Epic.

**Bug 2: Loose branch matching falsely marks PGs as complete**

The PG completion check matched merged PRs using a regex:
```powershell
$_.headRefName -match ($pg.name -replace '-', '[-/]')
```
With the fallback `PG-1` name, this matched `PG[-/]1` against ANY branch containing
that pattern, including `feature/pg-1-conductor-sdlc-artifact-linking` (PR #99, a
completely unrelated merged PR). Result: PG-1 marked `completed=True` with
`merged_pr=99` even though no implementation code existed.

**Fix**: Changed to exact branch name match only:
```powershell
$_.headRefName -eq $branchSlug
```
The fallback PG generates a branch name from the work item title
(`feature/pg-1-twig-tree-depth-n-json-output-missing-grandchil`), which won't
match any existing merged PR unless it's the actual implementation branch.

**Result after both fixes**: `total_tasks: 6`, `pending_pgs: PG-1`,
`next_pg: PG-1`, `completed=False`. The `pr_group_manager` will now see pending
work and route to `task_manager` → `coder` for actual implementation.

**Chicken-and-egg note**: Issue #2069 IS the twig tree depth bug. The SDLC
pipeline couldn't implement #2069 because the pipeline itself depends on the
tree depth working. The drill-down workaround in `load-work-tree.ps1` breaks
the cycle — the pipeline can now implement #2069 even though the twig CLI fix
hasn't shipped yet.

### `execution_planner` prompt — unguarded `architect.output` on resume path (April 25, 2026)

#2070 crashed at `execution_planner` with `'architect' is undefined`. On resume,
`plan_check` routes directly to `execution_planner`, skipping `architect` entirely.
The `execution_planner` input block had `architect.output?` (optional), but the
**prompt** used `{{ architect.output.plan_path }}` unconditionally in three places
(lines 4, 13, 34).

**First fix attempt failed** — only fixed line 4, missed lines 13 and 34. This is
the third time we've shipped a partial prompt fix and had to relaunch. The pattern
is clear: fixing one occurrence of a template ref while leaving others is guaranteed
to fail.

**Correct fix**: Define a `{% set %}` variable ONCE at the top of the prompt:
```jinja
{% set _plan_path = architect.output.plan_path
   if (architect is defined and architect.output is defined)
   else (plan_check.output.plan_path
   if (plan_check is defined and plan_check.output is defined) else '') %}
```
Then use `{{ _plan_path }}` everywhere in the prompt body. This eliminates the
class of bug — no individual line can reference the raw agent output.

**Anti-pattern identified**: Fixing template refs line-by-line with `if X is defined`
guards. Every fix must grep the ENTIRE file for the reference and use a `{% set %}`
alias at the top. Line-by-line fixes are a trap — you always miss one.

**Verification method**: After every prompt fix:
```powershell
Select-String -Path <file> -Pattern '<agent>\.output\.' |
  Where-Object { $_.Line -notmatch 'if.*is defined|set _' }
```
If this returns any lines, the fix is incomplete.

### Task redispatch infinite loop — root cause and three fixes (April 25, 2026)

#### The observed loop

Issue #2070's run hit 25 task_manager iterations, 23 coder runs, 23 task_reviewer
runs — all for the same task #2078. Investigated via event log at
`$env:TEMP\conductor\conductor-twig-sdlc-full-*.events.jsonl`.

#### What we assumed vs what actually happened

**Assumed**: coder↔reviewer rejection loop (reviewer keeps rejecting).
**Actual**: Every single review was **APPROVE** (23 approvals, scores 92-100).
The loop was: reviewer approves → task_manager tries `twig state Done` → **fails**
→ task stays "Doing" → task_manager re-dispatches same task.

#### Root cause: expired ADO auth mid-run

`twig state Done` requires fetching the work item from ADO (for optimistic
concurrency) before transitioning. The auth token expired during the multi-hour
run, causing:
1. `twig state` → ADO HTTP 203 (login page redirect instead of JSON)
2. Fallback: "Process configuration not available" (process_types table empty
   because `twig sync` also failed with same auth issue)

The loop self-resolved after ~23 cycles when task_manager switched from MCP
tools to direct CLI calls, which may have triggered a token refresh.

#### Three fixes implemented

**1. Circuit breaker in task_manager** (`prompts/task-manager.prompt.md`)

Added "Circuit Breaker — Same-Task Redispatch Detection" section. Before
dispatching `implement_task`, task_manager must check: was this exact task_id
already dispatched AND approved? If yes:
- Try `twig state Done` one more time
- If still fails, report in `progress_summary` and set `action=pr_group_ready`
  to break the loop
- Add task to `completed_tasks` (code-complete even if ADO state is stale)

**2. Preflight check** (`scripts/preflight-check.ps1` + `twig-sdlc-full.yaml`)

New `preflight_check` script agent as the **entry point** of `twig-sdlc-full.yaml`
(before `state_detector`). Validates:
- `gh auth status` + `gh api user` — GitHub auth
- `gh api repos/:owner/:repo` — push permission (not owner match)
- `twig sync` + `twig set <id>` — ADO connectivity
- `dotnet --version` — SDK present
- `git branch` — repo accessible
- `gh repo set-default` — worktree default repo set

Routes to `preflight_gate` (human gate) on failure with remediation steps.
Routes to `state_detector` on success.

**3. Better error message** (`DynamicProcessConfigProvider.cs`, pushed to main)

Changed "Process configuration not available. Run 'twig init' to initialize."
to explain: empty process_types table, caused by missing `twig sync` or expired
ADO auth, with remediation: `twig sync`, `az login`, or `TWIG_PAT`.

#### Also: `.twig/config` checked into source control

Updated `.gitignore` to include `.twig/config` and `.twig/status-fields` while
excluding databases (`*/`), `prompt.json`. Worktrees now get twig config from
git — no more `Copy-Item .twig` for basic CLI functionality. Process types
still require `twig sync` (runtime data from ADO API), but the org/project/auth
settings are now portable.

#### Lesson: investigate before assuming

The initial hypothesis (reviewer rejection loop) was wrong. The event log showed
23 APPROVE decisions — the loop was caused by a downstream side effect (failed
state transition), not the review decision. Always check the event log before
designing a fix.
