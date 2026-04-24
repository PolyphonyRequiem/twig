---
name: conductor-design
description: Design principles and architectural guidance for conductor SDLC workflows. Load when designing, reviewing, or modifying conductor workflow YAMLs, agent prompts, or deterministic scripts. These principles govern how workflows handle state, routing, re-entry, and human interaction.
---

# Conductor SDLC Workflow Design Principles

These principles govern the design of all conductor SDLC workflows, agent prompts,
and deterministic scripts. They are non-negotiable constraints — any workflow change
that violates a principle requires an explicit exception with justification.

## P1: Work Items Are the Source of Truth

Once plans are committed and work items are created, **ADO work items are the
authoritative source for state and context**. Agents must read work item state
(title, description, status, parent/child relationships, tags) from ADO — not
from plan files, agent memory, or prior outputs.

Plan documents are reference material, not operational state. They may be consulted
when implementers or reviewers need additional context to resolve ambiguity, but
they must never override what the work items say.

**Implications:**
- PG-to-task mapping belongs on work items (e.g., ADO tags), not in plan text
- Work item state transitions are the completion signal, not todo lists or agent outputs
- Stale plan references must not block or misdirect execution

## P2: Plans Are Context, Not Control Flow

Plan documents should only be referenced when additional context is needed by
implementers and reviewers to resolve ambiguity that emerges during implementation
and validation. Plans do not drive routing, task assignment, or state transitions.

**Implications:**
- Workflows must not parse plan files to determine PG structure at implementation time
- PG membership, task ordering, and scope come from work items
- Plans provide design rationale, architectural decisions, and acceptance criteria

## P2a: Plans Describe Solutions, Not Work Items

Plans describe *what needs to be done* — technical design, architecture, PR groupings,
acceptance criteria. Plans do not define work item hierarchies. A separate seeding
step reads the plan and creates work items (Epics, Issues, Tasks) as an execution
plan for the design. The plan is a design artifact; work items are the execution
artifact.

**Implications:**
- The architect agent designs the solution and defines PR groups
- The seeder agent reads the plan and creates work items, tagging each with its PG
- Plans should not contain ADO IDs, state tracking, or work item metadata
- If ADO IDs appear in a plan, they are informational cross-references only

## P3: Re-Entry by State Discovery

Re-entry into workflows and sub-workflows should pick up where they left off by
inspecting observable state: approved plans, ADO work item states, local branches,
git worktree status, commit history, and merged PRs — as appropriate for the
workflow's domain of responsibility.

Use the **minimum set of checks** needed to deterministically understand current
state. Don't re-derive what can be read directly.

**Implications:**
- A resumed implementation workflow checks which Issues are Done, which PRs are
  merged, which branches exist — then starts from the next incomplete unit
- A resumed planning workflow checks which plans exist and which work items are seeded
- State discovery is deterministic (scripts preferred over LLM inference)

## P4: Explicit User Intent — New / Redo / Resume

Workflows accept an explicit intent input that governs how existing assets are treated:

| Intent | Meaning | Behavior |
|--------|---------|----------|
| **new** | Starting fresh | Existing child work items, branches, and PRs under the root are treated as an error. The root work item should have no prior work. |
| **redo** | Do it again from scratch | Delete existing assets in scope (child items, branches, PRs) and re-implement without reading prior work into context. Avoids polluting agent sessions with prior knowledge. |
| **resume** | Continue where we left off | Discover state (P3), skip completed steps, pick up work in progress or the next pending unit. |

This replaces ad-hoc inputs like `skip_plan_review`, `plan_path`, `has_explicit_plan`,
etc. The intent signal is sufficient to determine the workflow's entry behavior.

## P5: Type-Agnostic Workflow Structure

The full SDLC entry point must not hardcode assumptions about the work item type.
Workflows handle the ADO hierarchy naturally:

| Root Type | Planning Scope | Implementation Scope |
|-----------|---------------|---------------------|
| **Epic** | Plan Epic → plan Issues → plan Tasks | Implement all Issues/Tasks, PR per PG |
| **Issue** | Plan Issue → plan Tasks | Implement all Tasks, PR per PG |
| **Task** | Plan the Task directly | Implement the Task, single commit or PR |

The same workflow nodes handle all types — branching on type happens within agents
or scripts based on discovered state, not via separate workflow paths.

## P6: Human Gates for Genuine Decisions Only

Human gates are reserved for situations where:

1. **Agent confidence is low** — ambiguity cannot be resolved from code or work items
2. **Functional limitations** — user acceptance testing, manual validation required
3. **Decision points with multiple valid options** — e.g., architectural trade-offs
   during planning where 2-3 approaches are viable
4. **Irreversible actions** — destructive operations that warrant confirmation

Human gates must **not** be used for:
- Routine progress checkpoints
- Confirmation of work the agent is confident about
- Compensating for bugs or missing automation

When a gate is presented, it should include enough context for the human to decide
without investigating independently.

### Confidence thresholds by phase

The bar for triggering a human gate varies by lifecycle phase. Planning decisions
are cheaper to revisit; implementation errors are expensive to undo.

- **Planning phase** — trigger gates at **≤85% confidence**. Design decisions
  benefit from human steering. Open questions, architectural trade-offs, and
  scope ambiguity should surface early.
- **Implementation and beyond** — trigger gates at **≥95% confidence only**.
  By this phase, the plan is approved and the work is mechanical. Gates should
  be reserved for genuine blockers: user acceptance testing, irreversible
  destructive actions, or situations where the agent truly cannot proceed.

## P7: Fail Honestly, Don't Auto-Approve

Verification agents must report actual state. If verification fails, the workflow
must either retry the failed work or terminate with a clear failure report. 

**Never auto-approve after N attempts.** If a verifier cannot confirm that work is
complete after a reasonable number of retries, the workflow should stop and surface
the failure — not force-pass to avoid loops. The number of retries should be generous
(10+) to account for transient failures, but the final answer must be honest.

**Implications:**
- `pr_finalizer` must not set `verified: true` when PGs have no merged PRs
- Close-out must not tag versions when children are incomplete
- Retry loops should have high bounds but hard stops with failure reporting

## P8: Prefer Scripts Over Agents for Deterministic Logic

When a decision is deterministic and the implementation is straightforward, use a
script (PowerShell, Python, etc.) rather than an LLM agent. Agents are for judgment,
ambiguity resolution, and creative work — not for `if/else` routing or data
transformation that can be expressed as code.

**Implications:**
- State detection, phase routing, and input validation are scripts
- PG grouping by tag, work tree loading, and plan file parsing are scripts
- Architectural decisions, code review, and user-facing content are agents
- If you find yourself writing a prompt that says "output X if condition Y" with
  no ambiguity, it should be a script

## P9: Concise, Contextual Naming

Inputs, outputs, and fixed value sets should be named concisely without losing
clarity within the existing context. Avoid redundant prefixes or overly verbose
names when the workflow or agent scope already provides context.

**Implications:**
- Within a workflow about work items, `id` is clearer than `work_item_id` if
  there's only one kind of ID in scope
- Enum values like `new`, `redo`, `resume` are preferred over
  `intent_new`, `intent_redo`, `intent_resume`
- Output field names should be short when the agent name provides context:
  `state_detector.output.phase` not `state_detector.output.detected_phase`
- Cross-boundary names (workflow inputs, sub-workflow contracts) may need more
  qualification to avoid ambiguity between contexts

## P10: Explicit Invariants

Each agent and script node must document its **invariants** — conditions that must
be true before, during, and after execution. Invariants are non-negotiable contracts
that the workflow enforces. Great effort should be taken to uphold them.

**Types of invariants:**
- **Preconditions** — what must be true before the node executes (inputs valid,
  work item in expected state, branch exists, etc.)
- **Postconditions** — what must be true after the node completes (items created,
  tags written, state transitioned, etc.)
- **Loop invariants** — what remains true across iterations in retry/revision loops

**Implications:**
- System prompts and prompt templates should state invariants explicitly
- Downstream nodes may assert upstream postconditions as their preconditions
- Violations of invariants should surface as errors, not silent degradation
- Tests and verification steps should check invariants, not just outputs

## P11: Rubric-Based Scoring

All quality assessments — plan reviews, code reviews, acceptance checks — must use
a **structured scoring rubric** with named dimensions and explicit weights. Prefer
rubrics grounded in academic standards (IEEE 1016, ISO/IEC 25010) where applicable.

**Rubric structure:**
- **Dimensions** — named quality aspects (e.g., Correctness, Feasibility, Clarity)
- **Weights** — percentage importance per dimension (must sum to 100%)
- **Scale** — 1-5 per dimension (1=Poor, 2=Needs Improvement, 3=Satisfactory, 4=Strong, 5=Excellent)
- **Composite score** — weighted sum mapped to 0-100
- **Critical threshold** — any dimension scored ≤ 2 is a blocking issue

**Why:**
- Single 0-100 scores are opaque — reviewers can't explain what failed
- Rubrics make feedback actionable (dimension X failed → fix X)
- Weights encode organizational priorities explicitly
- Academic grounding reduces subjective drift across agent sessions

**Standard rubrics by review type:**

### Plan Technical Review (IEEE 1016 / ISO 25010 informed)
| Dimension | Weight | Measures |
|-----------|--------|----------|
| Correctness | 30% | Addresses requirements, no contradictions with codebase |
| Feasibility | 25% | Implementable given project constraints |
| Completeness | 20% | All affected components identified |
| Testability | 15% | Clear test strategy, verifiable acceptance criteria |
| Risk awareness | 10% | Breaking changes, edge cases surfaced |

### Plan Readability Review
| Dimension | Weight | Measures |
|-----------|--------|----------|
| Clarity | 30% | Unambiguous, no vague language |
| Actionability | 25% | Concrete enough for agent execution |
| Structure | 20% | Logical organization, consistent formatting |
| Traceability | 15% | Requirements → Issues → Tasks → PGs mapping clear |
| Scoping | 10% | Boundaries explicit — in/out/deferred |

### Code Review (implementation phase)
| Dimension | Weight | Measures |
|-----------|--------|----------|
| Correctness | 30% | Logic is right, handles edge cases |
| Safety | 25% | No regressions, no broken invariants, AOT/trim safe |
| Completeness | 20% | All acceptance criteria addressed, tests written |
| Conventions | 15% | Follows project patterns, naming, structure |
| Reviewability | 10% | Changes are minimal, well-scoped, clear commit messages |

### User Acceptance (implementation phase, P6 ≥95% confidence)
| Dimension | Weight | Measures |
|-----------|--------|----------|
| Functional correctness | 35% | Feature works as specified |
| UX coherence | 25% | Output formatting, help text, error messages are clear |
| Non-regression | 20% | Existing features still work |
| Documentation | 10% | Help text, README, command examples updated |
| Edge cases | 10% | Graceful handling of unusual inputs |

**Implications:**
- Reviewer prompts must include the rubric and instruct dimension-by-dimension scoring
- Review router uses composite scores and critical issue counts for gating
- Rubrics are versioned in the conductor-design skill and referenced by prompts
- Custom rubrics can be added for specialized reviews (security, accessibility, etc.)
