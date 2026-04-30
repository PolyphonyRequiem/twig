---
title: "Type-Agnostic SDLC Workflow"
type: Epic
status: approved
phases:
  - { id: 2580, title: "Phase 0: Foundations & Prerequisites" }
  - { id: 2581, title: "Phase 1: Polyphony Core Engine" }
  - { id: 2582, title: "Phase 2: Generic Workflow Scripts" }
  - { id: 2583, title: "Phase 3: Workflow YAML Refactoring" }
  - { id: 2584, title: "Phase 4: Validation & Testing" }
  - { id: 2585, title: "Phase 5: DU Preview Adoption" }
  - { id: 2586, title: "Phase 6: Cross-Repo Onboarding" }
---

# Type-Agnostic SDLC Workflow — Implementation Plan

## Problem Statement

The twig conductor SDLC workflow (`twig-sdlc-full`) hardcodes a 3-tier Epic → Issue → Task
hierarchy across ~9 scripts and ~9 workflow YAMLs. This prevents reuse across different ADO
process templates (Basic, Agile, Scrum, CMMI, custom). The workflow must be refactored to
reason about **hierarchy levels and roles** rather than specific type names.

## Proposed Architecture

### Three Pillars

```
┌────────────────────────────────────────────────────────────┐
│  1. POLYPHONY (new .NET CLI tool)                          │
│     Deterministic routing engine for hierarchical SDLC     │
│     - References Twig.Domain directly                      │
│     - Recursive state machine for any-depth hierarchies    │
│     - Consumes process config, outputs routing decisions   │
│     - CLI invocable by conductor scripts                   │
│     - C# (.NET 10 stable; DU preview in Phase 5)           │
└────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────┐
│  2. GENERIC WORKFLOW (twig-conductor-workflows registry)    │
│     Type-agnostic orchestration layer                      │
│     - Scripts call Polyphony for routing decisions         │
│     - Agents read type definitions for quality guidance    │
│     - Sub-workflows are recursive (plannable → plannable)  │
│     - PR review model is configurable per level            │
│     - No hardcoded type names, state names, or hierarchy   │
└────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────┐
│  3. PER-REPO CONFIGURATION (.conductor/ in target repo)    │
│     Process-specific definitions and templates             │
│     - profile.yaml: tech stack, build, estimation          │
│     - process-config.yaml: type capabilities, transitions  │
│     - work-item-types/*.md: type definitions               │
│     - work-item-types/templates/*.md: description formats  │
└────────────────────────────────────────────────────────────┘
```

### Type Capabilities Model

Instead of static role assignments, types declare **capabilities**. The "root" is always
the invocation target — ANY type can be root depending on what the user passes to the workflow.

| Capability | Meaning | Examples |
|-----------|---------|---------|
| **plannable** | Gets architect/decomposition, may be recursive | Epic, Scenario, Issue, User Story, PBI |
| **actionable** | Coordination/supplementary work, grouped steps | Task Group, Deliverable |
| **implementable** | Leaf-level work, directly coded | Task, Bug |

**Key principles:**
- **Any type can be root.** The workflow invocation target is always root — no static assignment.
- **Capabilities are per-type, not per-position.** A type's capabilities don't change based on
  where it sits in the hierarchy.
- **Multiple capabilities are allowed.** An Issue can be both `plannable` AND `implementable` —
  the routing engine decides which path based on size/children/state.
- **Self-containment is auto-discovered.** If ADO's `AllowedChildTypes` includes the type itself,
  it can recurse. The config only specifies `max_nesting_depth` and `decomposition_guidance`.
- **Filing eligibility.** Types that can receive closeout observations are marked `filing_eligible`.

**Routing logic for multi-capability types:**
1. Focus item is always "root" (the invocation target)
2. If focus has children → recurse into children based on THEIR capabilities
3. If focus has no children AND is `plannable` → plan/decompose it
4. If focus has no children AND is `implementable` → implement it directly
5. If focus is `plannable + implementable` → architect agent decides (guided by type definition)

### Polyphony — The Routing Engine

**Purpose:** Given a work item at any hierarchy depth, determine:
- Current SDLC phase (needs_planning, needs_seeding, ready_for_implementation, done, etc.)
- Next action (plan, seed, decompose, implement, review, close)
- Validation of state transition preconditions
- Recursive descent through the hierarchy

**Technology:**
- .NET 10 stable (DU preview deferred to Phase 5 — C4)
- References `Twig.Domain` and `Twig.Infrastructure` via ProjectReference
- Reads twig's local SQLite cache for work item data
- Reads `.conductor/` config for type capabilities and transition mappings
- Outputs structured JSON to stdout, uses exit codes for conductor routing
- Includes `workspace_hint` in routing output (branch names for scripts to use)
- AOT-compiled, single-file binary deployed to ~/.twig/bin/

**Key Commands:**
```bash
polyphony route --work-item 1234 --config .conductor/process-config.yaml
polyphony validate --work-item 1234 --transition done
polyphony hierarchy --work-item 1234 --depth 3
polyphony review-policy --pr-type planning --level plannable
```

**State Machine (per hierarchy level):**
```
Proposed → Planning → Seeded → Implementing → Reviewing → Completed
                ↑                                    │
                └────── Remediation (addendum) ──────┘
```

### Branch & PR Strategy

```
main
  └── feature/{root-id}-{slug}                    ← Root's integration branch
        ├── planning/{root-id}                    ← Plan PR
        ├── feature/{child-id}-{slug}             ← Child's sub-feature branch (if plannable)
        │     ├── pg-1/{child-id}-{pg-slug}       ← Child's PG-1
        │     └── pg-2/{child-id}-{pg-slug}       ← Child's PG-2 (parallel with PG-1)
        ├── pg-1/{root-id}-{pg-slug}              ← Root-level PG (if root is implementable)
        └── remediation/{root-id}-{pg-slug}       ← Addendum PG (post-review fixes)
```

Each plannable level that gets decomposed creates its own feature branch nested under
its parent's. Sub-feature branches merge up into the parent feature branch when complete.
PGs within a level execute in parallel.

**PR Review Configuration (per workflow phase):**
```yaml
review_policies:
  planning:
    plan_pr: { agent_review: true, human_review: true, auto_merge: false }
  implementation:
    pg_pr: { agent_review: true, human_review: false, auto_merge: true }
    feature_pr: { agent_review: true, human_review: true, auto_merge: false }
  remediation:
    pg_pr: { agent_review: true, human_review: false, auto_merge: true }
```

**Platform abstraction:** Separate sub-workflows for GitHub vs ADO PR operations.
The review loop (submit → review → fix → re-review) is platform-specific but the
policy (who reviews, can it auto-merge) is platform-independent.

### Feature PR Remediation

When human reviews the feature PR and requests changes:
1. **Create plan addendum** — New doc referencing the original plan, addressing feedback
2. **Create new work items** — Issues/Tasks for the remediation work
3. **New remediation PG** — Branch targets the feature branch
4. **Implement & merge** — Standard PG lifecycle
5. **Re-request review** — Feature PR updated, ready for re-review

### Recursive Planning Model

Planning is recursive: each plannable level gets an architect pass, review, and seed cycle.

```
Epic (root)
  └── [architect plans Epic → creates child Scenarios]
        └── Scenario (plannable, level 1)
              └── [architect plans Scenario → creates Deliverables/Tasks]
                    └── Deliverable (actionable, level 2)
                          └── [decompose Deliverable → create Tasks]
                                └── Task (implementable, leaf)
```

At each level, the workflow:
1. Calls Polyphony to determine phase
2. If `needs_planning`: invoke architect sub-workflow with type definition + template
3. If `needs_seeding`: invoke seeder sub-workflow
4. If children need planning: recurse into each child (sub-workflow per child)
5. If `ready_for_implementation`: invoke implementation sub-workflow
6. If `done`: validate and transition parent

### User Plan as First-Class Input

The v2 workflow supports an optional **user plan** — a pre-written plan document that the
architect agent uses as its primary input rather than starting from scratch.

**Workflow inputs:**
```yaml
inputs:
  work_item_id:
    type: number
    description: ADO work item ID (any type)
  intent:
    type: string
    default: resume
    description: "new | redo | resume"
  user_plan_path:
    type: string
    default: ""
    description: "Path to a user-authored plan document. Architect treats this as strong input."
```

**Behavior by intent + user_plan_path combination:**

| Intent | user_plan_path | Architect Behavior |
|--------|---------------|-------------------|
| `new` | empty | Architect creates plan from scratch (researches codebase, designs solution) |
| `new` | provided | Architect **refines** user plan — validates feasibility, fills gaps, adds PR groupings, formats to standard. Does NOT discard user's design decisions. |
| `resume` | empty | Finds existing approved plan on disk (frontmatter match). If none, falls back to `new`. |
| `resume` | provided | Uses provided path as the plan (skips disk search). Validates plan status. |
| `redo` | empty | Deletes existing plan + children, architect starts fresh |
| `redo` | provided | Deletes existing plan + children, architect refines user plan (same as `new` + user_plan) |

**Key principle:** When a user plan is provided, the architect agent is instructed to:
1. **Preserve the user's design intent** — don't rewrite from scratch
2. **Add what's missing** — PR groupings, acceptance criteria, implementation details
3. **Flag disagreements** — if the architect thinks the user's approach has issues, it
   raises them as open questions (triggering the open_questions_gate) rather than silently
   overriding
4. **Produce a standard-format output** — the refined plan conforms to the `.plan.md` schema
   with frontmatter, regardless of the user plan's format

**User plan checked into repo:** The user plan is committed alongside the refined plan
in the planning PR. It lives at `docs/projects/<id>.user-plan.md` as an auditable record
of the original intent. The refined `.plan.md` is the authoritative planning artifact;
the user plan is context.

### Per-Repo Configuration Structure

```
<target-repo>/.conductor/
  profile.yaml                      # Tech stack, build, estimation
  process-config.yaml               # Type capabilities, transitions, review policies, branches
  work-item-types/
    epic.md                         # Type definition (semantics, scoping guidance)
    issue.md                        # Type definition (or scenario.md, user-story.md, etc.)
    task.md                         # Type definition
    templates/
      epic-template.md              # Description template for creating Epics
      issue-template.md             # Description template for creating Issues
      task-template.md              # Description template for creating Tasks
  logs/
    compaction-events.jsonl          # P12 compaction logging (auto-generated)
```

**process-config.yaml example (Basic process):**
```yaml
process_template: Basic

types:
  Epic:
    capabilities: [plannable]
    filing_eligible: false
    max_nesting_depth: 1
    decomposition_guidance: |
      Always decompose into Issues. Epics are never implemented directly.
  Issue:
    capabilities: [plannable, implementable]
    filing_eligible: true
    max_nesting_depth: 1
    decomposition_guidance: |
      Decompose into Tasks when scope exceeds a single PG (~2000 LoC).
      Implement directly when the change is focused and fits one PG.
  Task:
    capabilities: [implementable]
    filing_eligible: true

# Semantic state mapping — maps lifecycle events to concrete state names per type.
# States are PER WORK ITEM TYPE (different types may have different state names).
transitions:
  Epic:
    begin_planning: Doing
    all_children_complete: Done
    scope_removed: Removed
  Issue:
    begin_planning: Doing
    begin_implementation: Doing
    implementation_complete: Done
    scope_removed: Removed
  Task:
    begin_implementation: Doing
    implementation_complete: Done
    scope_removed: Removed

# Review policies are bound to WORKFLOW PHASES, not type capabilities.
review_policies:
  planning:
    plan_pr: { agent_review: true, human_review: true, auto_merge: false }
  implementation:
    pg_pr: { agent_review: true, human_review: false, auto_merge: true }
    feature_pr: { agent_review: true, human_review: true, auto_merge: false }
  remediation:
    pg_pr: { agent_review: true, human_review: false, auto_merge: true }

branch_strategy:
  feature_branch: "feature/{root_id}-{slug}"
  planning_branch: "planning/{root_id}"
  pg_branch: "pg-{n}/{root_id}-{slug}"
  target: main

platform: github  # or "ado" (GitHub primary, ADO stub for v1)
```

### New Conductor Design Principle

**P12: Short-Lived Agent Sessions**

Agent sessions should be designed short enough to never trigger context compaction.
If an agent triggers compaction:
1. It MUST log the event (tool, agent name, workflow, turn count, token usage)
2. The logged event should be queryable for future analysis
3. The workflow design should be revisited — compaction indicates the agent's scope is too broad

Implications:
- Prefer recursive sub-workflows over single agents handling deep hierarchies
- Each agent should have a focused, bounded scope
- If a single work item requires extensive context (large codebase exploration),
  the agent should write findings to a file and pass the path, not accumulate in context

**Compaction logging mechanism:** Write to a JSONL file at a known location
(e.g., `.conductor/logs/compaction-events.jsonl`) with timestamp, workflow name,
agent name, turn count, and approximate token usage. This can be queried later
for workflow design improvements.

### Removing Identity References

All hardcoded references to specific users or paths must be removed:
- `PolyphonyRequiem/twig` → derive from git remote at runtime
- `dangreen-msft/Twig` → read from twig config / workflow metadata
- `C:\Users\dangreen\...` → use relative paths or `workflow.dir`
- ADO project URLs → read from twig config (`project_url` metadata)

Scripts should derive these values from:
1. `git remote get-url origin` → GitHub repo slug
2. `twig` config → ADO organization, project, team
3. Workflow metadata (`-m project_url=...`) → links in gate prompts
4. `workflow.dir` template variable → script paths

### State Transition Conditions (Complete Reference)

Work item state transitions ONLY occur at well-defined lifecycle events. This is the
exhaustive set — no other code path may change a work item's state.

**Lifecycle Events:**

| # | Event | Trigger Condition | Who Transitions | Target State |
|---|-------|-------------------|-----------------|--------------|
| 1 | `begin_planning` | Workflow accepts focus item; architect agent starts | Focus item | mapped from `transitions.<type>.begin_planning` |
| 2 | `begin_implementation` | First PG branch created for this item | Focus item (if implementable) | mapped from `transitions.<type>.begin_implementation` |
| 3 | `implementation_complete` | All PGs for this item merged into feature branch | The item whose PGs completed | mapped from `transitions.<type>.implementation_complete` |
| 4 | `all_children_complete` | Every child is in a terminal state (Completed or Removed) | Parent item | mapped from `transitions.<type>.all_children_complete` |
| 5 | `review_approved` | Feature PR approved and merged to target branch | Focus (root) item | mapped from `transitions.<type>.review_approved` (if defined, else same as `all_children_complete`) |
| 6 | `scope_removed` | User explicitly cuts scope via human gate | The removed item | mapped from `transitions.<type>.scope_removed` |
| 7 | `begin_child_planning` | Child item accepted into planning | Child item | mapped from child's `transitions.<type>.begin_planning` |
| 8 | `blocked_on_dependency` | Predecessor link points to non-terminal item | NO transition (state unchanged) | Item stays in current state; human gate fires |

**Rules governing state transitions:**
1. **Transitions are validated before execution.** Polyphony checks that the target state
   is reachable from the current state per ADO's process rules. Illegal transitions are
   hard errors.
2. **No-op on already-in-target-state.** If the item is already in the target state
   (e.g., resuming after crash), the transition is skipped without error. (P3: re-entry)
3. **Missing mappings are hard errors.** If a lifecycle event fires for a type that has no
   mapping for that event in `transitions`, Polyphony fails closed. (C2)
4. **Terminal states are absorbing.** Once in Completed or Removed (per StateCategory),
   no further transitions are attempted. Remediation creates NEW items, not re-opens old ones.
5. **Parent transitions wait for ALL children.** A parent's `all_children_complete` only
   fires when every child (not grandchild) is in a terminal StateCategory.
6. **Closeout happens on the root.** The focus item is post-mortem'd after `review_approved`.
   Closeout observations create new work items (using `filing_eligible` types) — they never
   re-open the completed hierarchy.

**Semantic Mapping:**
States are per work item type. The same lifecycle event maps to different concrete state
names depending on the type AND the process template:

| Process | Type | `begin_planning` | `implementation_complete` | `all_children_complete` |
|---------|------|-------------------|---------------------------|------------------------|
| Basic | Epic | Doing | — | Done |
| Basic | Issue | Doing | Done | Done |
| Basic | Task | — | Done | — |
| Agile | Epic | Active | — | Resolved |
| Agile | User Story | Active | Resolved | Resolved |
| Agile | Task | Active | Closed | — |
| Scrum | Epic | — (no explicit start) | — | Done |
| Scrum | PBI | Committed | Done | Done |
| Scrum | Task | In Progress | Done | — |

**Note:** "—" means the event is not applicable for that type (e.g., Epics never have
`begin_implementation` because they're never implemented directly).

### Dependency Gates

When an implementable item has **predecessor links** (ADO's Predecessor/Successor relationship
type) pointing to items that are not yet in a terminal state, implementation is blocked.

**Detection:** Polyphony checks predecessor links as a routing precondition. If any predecessor
is not Completed/Removed (by StateCategory), the item's phase is `blocked_on_dependency`.

**Handling:**
1. Polyphony outputs the blocking predecessors in its routing JSON
2. The conductor script surfaces a human gate with options:
   - **Wait** — Skip this item, continue with other items in the PG/level, re-check later
   - **Override** — Proceed with implementation despite unresolved dependency (user accepts risk)
   - **Reassign** — The dependency is being handled elsewhere; mark it as externally tracked
3. If ALL items in a PG are blocked, the PG itself is blocked and the gate escalates

**Scope:** This covers ADO predecessor/successor links only. It does NOT track implicit
code-level dependencies between PGs (those are handled by the parallel PG rebase strategy).

**Re-entry:** On workflow resume, blocked items are re-checked. If predecessors have since
completed, the block clears automatically without user intervention (P3: re-entry by state).



After the focus item's feature PR merges (`review_approved`), a closeout phase runs:
1. **Post-mortem scoped to focus item** — Review what went well/poorly across the hierarchy
2. **Observation classification** — Each observation is classified by type using:
   - Type definitions (`.conductor/work-item-types/*.md`) for semantic understanding
   - `filing_eligible` flag to filter valid target types
   - AI classification with confidence threshold
3. **User confirmation** — Low-confidence classifications get a human gate presenting
   eligible types for the user to choose from
4. **Work item creation** — Filed observations become new work items under the appropriate
   parent (configured per repo, e.g., a "Closeout Findings" epic)

### Nested Feature Branches

When a plannable item is decomposed into child plannable items, each child gets its own
feature branch nested under the parent's:

```
main
  └── feature/epic-123-auth                    ← Epic's feature branch
        ├── planning/epic-123                  ← Epic's plan PR
        ├── feature/issue-456-login            ← Issue-A's sub-feature branch
        │     ├── pg-1/issue-456-form          ← Issue-A's PG-1
        │     └── pg-2/issue-456-api           ← Issue-A's PG-2
        ├── feature/issue-789-oauth            ← Issue-B's sub-feature branch
        │     └── pg-1/issue-789-flow          ← Issue-B's PG-1
        └── (sub-feature branches merge up into parent feature branch)
```

Each level's feature branch is the integration point for its children's work. When all
child sub-feature branches merge into the parent feature branch, the parent is complete.

**PG parallelism:** PGs within the same level execute in parallel (full parallel support
in v1). Merge conflicts are resolved by the implementing agent via rebase.

---

## Critical Design Constraints (from critique)

### C1: Freshness-Guarded Routing
Polyphony routing decisions must run against **fresh** ADO state. Invariant:
- `polyphony route` MUST succeed only after `twig sync` with freshness < 30 seconds
- If freshness cannot be verified, Polyphony fails closed (exit code error, not stale routing)
- Phase detection is the highest-risk operation — it determines the entire workflow path

### C2: Explicit Transition Mapping (Per Work Item Type)
State categories (Proposed/InProgress/Completed/Removed) are insufficient for routing.
States are **per work item type** — different types may use different state names even in
the same process. The process config must specify concrete target states per TYPE per
lifecycle event (semantic mapping):
```yaml
transitions:
  Epic:
    begin_planning: Doing
    all_children_complete: Done
    scope_removed: Removed
  Issue:
    begin_planning: Doing
    begin_implementation: Doing
    implementation_complete: Done
    scope_removed: Removed
  Task:
    begin_implementation: Doing
    implementation_complete: Done
    scope_removed: Removed
```
Polyphony validates that target states are legal per the ADO process type config before
attempting transitions. Missing mappings are hard errors, not silent fallbacks.

### C3: Recursion Depth Budget
Conductor max depth is 10. Budget allocation:
```
Depth 0: twig-sdlc-full (apex)
Depth 1: planning OR implementation (sub-workflow)
Depth 2: plan-level (recursive planning per level)
Depth 3–7: plan-level recursion (up to 6 nested plannable levels)
Depth 8: platform-pr (PR lifecycle sub-workflow)
```
This leaves 2 levels of headroom. Rules:
- Planning recursion is capped at depth 6 (7-tier hierarchy max)
- PR lifecycle is always a leaf sub-workflow (no further nesting)
- Remediation reuses the same implementation sub-workflow (no new depth)
- Deterministic helpers (branch ops, state transitions) are scripts, not sub-workflows

### C4: Phased Rollout (No Big Bang)
1. Polyphony v1 builds against **stable Twig.Domain** (current .NET 10, no DU preview)
2. Prove Basic/twig end-to-end with Polyphony routing
3. DU preview adoption is Phase 5 (future, after routing is stable)
4. Existing hardcoded workflow remains as fallback until new workflow is validated
5. Cloudvault onboarding is Phase 6 (after twig validation)

### C5: Polyphony v1 Scope (Minimal)
v1 owns ONLY:
- Hierarchy discovery (read work item tree, map types to roles)
- Phase detection (recursive state machine)
- Precondition validation (is this transition legal?)
- Freshness enforcement (fail if sync is stale)

v1 does NOT own:
- Branch naming (stays in scripts)
- Review policy resolution (stays in workflow config)
- PR operations (stays in platform-specific scripts)
- State transitions (stays in twig CLI calls)

These are promoted into Polyphony in later phases once routing is proven.

---

## Implementation Phases

### Phase 0: Foundations & Prerequisites

#### 0.1 — Create Polyphony repository
- New .NET 10 solution (**stable**, no DU preview yet — C4)
- ProjectReference to Twig.Domain and Twig.Infrastructure (current stable)
- CLI skeleton (ConsoleAppFramework, AOT, single-file)
- Process config schema definition and parser
- Freshness enforcement (verify twig sync recency — C1)
- Basic routing command: `polyphony route --work-item <id>`

#### 0.2 — Define twig repo's .conductor/ configuration
- Create `process-config.yaml` for Basic process (Epic/Issue/Task)
- Include explicit transition mappings (C2)
- Create `work-item-types/` definitions (adapted from cloudvault registry patterns)
- Create `work-item-types/templates/` description templates
- Create `profile.yaml` (tech stack for twig itself)

#### 0.3 — Add P12 principle to conductor-design skill
- Create `p12-short-lived-sessions.md` in references/
- Update SKILL.md index
- Define compaction logging format

### Phase 1: Polyphony Core Engine (v1 — minimal scope per C5)

#### 1.1 — Hierarchy discovery
- Read twig's process configuration (types, containment rules, state categories)
- Read `.conductor/process-config.yaml` for role assignments
- Build in-memory hierarchy model from work item tree
- Enforce freshness invariant (C1)

#### 1.2 — Phase detection (recursive state machine)
- For any work item, determine current SDLC phase
- Handle recursive descent (plannable containing plannable)
- Validate state transition preconditions against transition mappings (C2)
- Respect depth budget (C3) — max 6 plannable levels
- Output structured JSON with phase, next_action, and validation results

#### 1.3 — Transition validation
- Given work item + target lifecycle event, validate the transition is legal
- Check both ADO process rules AND SDLC preconditions
- Hard error on missing transition mappings (C2)

### Phase 2: Generic Workflow Scripts

#### 2.1 — Replace detect-state.ps1
- Call `polyphony route` instead of manual type checks
- Parse Polyphony's JSON output for phase routing
- Remove all type name literals

#### 2.2 — Replace load-work-tree.ps1
- Call `polyphony hierarchy` for tree + PG structure
- Polyphony handles type-specific tree loading logic internally
- Script becomes a thin wrapper

#### 2.3 — Replace pg-router.ps1 and task-router.ps1
- Call `polyphony route` with PG context
- Routing decisions are Polyphony's responsibility
- Scripts handle conductor-specific concerns (exit codes, output format)

#### 2.4 — Replace issue-closer.ps1
- Generalize to "scope-closer" — closes the appropriate level after PR merge
- Uses Polyphony to identify which items to transition

#### 2.5 — Remove identity references
- Replace `PolyphonyRequiem/twig` with `git remote` derivation
- Replace `dangreen-msft/Twig` with metadata variable
- Remove absolute paths

### Phase 3: Workflow YAML Refactoring

#### 3.1 — Apex workflow (twig-sdlc-full.yaml)
- Generic entry point accepting any type at any level
- Routes based on Polyphony's phase detection
- Remove type-specific routing branches

#### 3.2 — Planning sub-workflow (recursive)
- Single `plan-level.yaml` that handles any plannable level
- Injects type definition + template into architect prompt
- Recursively invokes itself for nested plannable children
- Replaces: plan-design.yaml, plan-child.yaml, plan-issue.yaml, task-decomposition.yaml

#### 3.3 — Implementation sub-workflow
- PG lifecycle driven by Polyphony routing
- Platform-specific PR sub-workflows (github-pr.yaml, ado-pr.yaml)
- Configurable review loops based on review policy

#### 3.4 — Feature PR and remediation
- Feature PR creation after all PGs merge
- Remediation cycle (addendum → new PG → merge → re-review)

### Phase 4: Validation & Testing

#### 4.1 — Unit tests for Polyphony
- State machine transitions for each process template
- Hierarchy discovery for Basic, Agile, Scrum, CMMI
- Transition validation (legal and illegal transitions)
- Freshness enforcement (stale cache → error)

#### 4.2 — Integration tests
- Polyphony + real twig cache (test workspace)
- Full routing scenario for 2-tier, 3-tier, 4-tier hierarchies
- End-to-end: polyphony route → conductor script → workflow routing

#### 4.3 — Workflow validation
- `conductor validate` on all refactored YAMLs
- Dry-run with mock inputs
- Regression test: twig repo (Basic process) still works end-to-end
- A/B comparison: old workflow vs new workflow on same work item

#### 4.4 — Cross-process validation
- Verify workflow works for cloudvault (CMMI: Scenario → Deliverable → Task)
- Verify workflow handles composite types (Scenario → Scenario → Deliverable)

### Phase 5: Future — DU Preview Adoption (post-validation)

#### 5.1 — Adopt DU preview in Polyphony
- Refactor state machine types to use discriminated unions
- Exhaustive pattern matching for routing decisions
- Validate AOT compatibility

#### 5.2 — Optionally adopt DU preview in Twig
- Only if Polyphony proves DUs work well with AOT
- Refactor StateCategory, routing results opportunistically

### Phase 6: Future — Cross-Repo Onboarding

#### 6.1 — Cloudvault onboarding
- Create `.conductor/` config for cloudvault (CMMI process)
- Validate Polyphony routing against cloudvault hierarchy
- Replace cloudvault's prototype workflows with generic versions

#### 6.2 — Config inheritance/templating
- Base profiles per process template (Basic, Agile, Scrum, CMMI)
- Per-repo overrides layer
- Schema versioning and validation/linting

---

## Migration & Cutover Strategy

The existing hardcoded workflow is preserved as a fallback:

1. **Parallel deployment:** New generic workflow registers as `twig-sdlc-v2-full@twig`
2. **Feature flag:** Process config presence (`.conductor/process-config.yaml`) determines
   which workflow to invoke. If config exists → v2. If not → legacy.
3. **Gradual cutover:** Prove on twig repo first. Then cloudvault. Then deprecate legacy.
4. **Rollback:** If v2 fails, invoke legacy with `twig-sdlc-full@twig` — no changes needed.

---

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Routing engine location | Separate tool (Polyphony) | Decouples SDLC logic from twig CLI; reusable across workflows |
| Language | C# (.NET 10 stable; DU preview deferred to Phase 5) | Shared types with Twig.Domain; stable AOT first, DUs later |
| Config location | Per-repo `.conductor/` | Different repos have different processes |
| Type definitions | Markdown files | Human-readable, inject-able into agent prompts |
| Branch strategy | Nested feature branches as integration points | Each plannable level gets its own feature branch |
| PR review | Configurable per workflow phase | Policies bind to phases (planning/implementation/remediation), not types |
| Recursion | Sub-workflows, depth-budgeted (max depth 5 of 10) | Leave headroom; flatten helpers into scripts |
| Identity references | Derive from git remote + twig config | No hardcoded users, orgs, or paths |
| Compaction logging | JSONL file in `.conductor/logs/` | Queryable, doesn't require infrastructure |
| Freshness | Polyphony fails closed if sync >30s stale | Prevents routing on stale cache (P1/P3) |
| Transition mapping | Explicit per-TYPE in config, hard error on missing | States are per type; no silent fallbacks |
| Rollout | Parallel v2 workflow, config-presence gated | Zero risk to existing workflow |
| Polyphony v1 scope | Route + validate + hierarchy ONLY | Prove core value before expanding |
| PG ordering | Full parallel in v1 | Maximize throughput; agents resolve conflicts via rebase |
| Polyphony packaging | Standalone binary via publish-local.ps1 to ~/.twig/bin/ | Consistent with twig deployment model |
| Routing AI | Fully deterministic | AI judgment belongs in agents; ambiguous routing escalates to human gate |
| ADO PR support | GitHub primary, ADO stub (interfaces defined) | Prove on GitHub first; ADO implementation deferred |
| Type capabilities | Declared per-type, not per-position | Any type can be root; capabilities don't change based on hierarchy position |
| Self-containment | Auto-discovered from ADO AllowedChildTypes | Config only specifies max_nesting_depth and decomposition_guidance |
| Closeout filing | AI classification + human gate for ambiguous cases | Types marked filing_eligible; type definitions guide classification |
| User plan input | First-class `user_plan_path` input, architect refines rather than discards | User's design intent preserved; disagreements raised as open questions |

## Resolved Questions

All previously open questions have been resolved:

1. **Polyphony packaging:** Standalone binary deployed alongside twig via `publish-local.ps1` to `~/.twig/bin/`.
2. **Config inheritance / role overlap:** Eliminated — review policies bind to workflow phases, not type capabilities.
   Any type can be root; the review policy is determined by which phase the workflow is in (planning, implementation, remediation).
3. **Parallel PGs:** Full parallel support in v1. Merge conflicts resolved by implementing agent via rebase.
4. **ADO PR support:** GitHub primary with ADO stub. Interfaces defined for platform abstraction, ADO implementation deferred.
5. **Polyphony AI capability:** Fully deterministic. AI judgment belongs in agents (architects, code reviewers).
   Genuinely ambiguous routing scenarios escalate to human gates (P6).

---

## Remaining Open Design Areas

1. **Workspace hint schema:** Exact JSON structure for Polyphony's `workspace_hint` output
   (will be defined during Phase 0 implementation).
2. **Conflict resolution strategy:** When parallel PGs create merge conflicts, the rebase
   approach needs a retry budget and failure escalation path.
3. **Config schema versioning:** How to handle breaking changes to process-config.yaml as
   the system evolves (likely a `schema_version` field with migration tooling).

---

## Dependencies

- .NET 10 SDK (stable; DU preview deferred to Phase 5)
- Twig.Domain / Twig.Infrastructure (ProjectReference from Polyphony)
- Conductor CLI (workflow execution)
- twig CLI (work item cache, process discovery)
- Git (branch operations)
- gh CLI (PR operations — GitHub primary)
- az repos (PR operations — ADO stub, deferred implementation)
