---
name: conductor-design
description: Design principles and architectural guidance for conductor SDLC workflows. Load when designing, reviewing, or modifying conductor workflow YAMLs, agent prompts, or deterministic scripts. These principles govern how workflows handle state, routing, re-entry, and human interaction.
---

# Conductor SDLC Workflow Design Principles

These principles govern the design of all conductor SDLC workflows, agent prompts,
and deterministic scripts. They are **non-negotiable constraints** — any workflow
change that violates a principle requires an explicit exception with justification.

For detailed rationale, examples, and implications of each principle, see the
individual documents in the `references/` directory.

---

## P1: Work Items Are the Source of Truth

ADO work items are the authoritative source for state and context — not plan files,
agent memory, or prior outputs. Plan documents are reference material only.

→ [Full details](references/p01-work-items-source-of-truth.md)

## P2: Plans Are Context, Not Control Flow

Plans provide design rationale and acceptance criteria. They do not drive routing,
task assignment, or state transitions — work items do.

→ [Full details](references/p02-plans-are-context.md)

## P2a: Plans Describe Solutions, Not Work Items

Plans describe technical design and architecture. A separate seeding step creates
the ADO work item hierarchy from the plan. Plans are design artifacts; work items
are execution artifacts.

→ [Full details](references/p02a-plans-describe-solutions.md)

## P3: Re-Entry by State Discovery

Resumed workflows discover current state by inspecting observable artifacts (work
item states, branches, merged PRs) rather than replaying history. Use the minimum
set of checks needed. Prefer scripts over LLM inference for state detection.

→ [Full details](references/p03-re-entry-by-state.md)

## P4: Explicit User Intent — New / Redo / Resume

Workflows accept an explicit intent (`new`, `redo`, `resume`) that governs how
existing assets are treated. This replaces ad-hoc skip/path flags.

→ [Full details](references/p04-explicit-intent.md)

## P5: Type-Agnostic Workflow Structure

The SDLC entry point must not hardcode work item type assumptions. The same workflow
nodes handle Epics, Issues, and Tasks — branching on type happens within agents or
scripts based on discovered state.

→ [Full details](references/p05-type-agnostic.md)

## P6: Human Gates for Genuine Decisions Only

Gates are reserved for low-confidence situations, functional limitations, genuine
multi-option decisions, and irreversible actions. Never use gates for routine
checkpoints or confident confirmations. Planning gates trigger at ≤85% confidence;
implementation gates at ≥95% only.

→ [Full details](references/p06-human-gates.md)

## P7: Fail Honestly, Don't Auto-Approve

Verification agents report actual state. Never auto-approve after N attempts.
Retry loops should have generous bounds (10+) but hard stops with failure reporting.

→ [Full details](references/p07-fail-honestly.md)

## P8: Prefer Scripts Over Agents for Deterministic Logic

If a decision is deterministic and straightforward, use a script — not an LLM agent.
Agents are for judgment and ambiguity; scripts are for routing and transformation.

→ [Full details](references/p08-scripts-over-agents.md)

## P9: Clear, Minimal Naming

Names should be unambiguous at a glance with minimal text. Short names are preferred
when workflow context disambiguates. Cross-boundary names may need more qualification.

→ [Full details](references/p09-naming.md)

## P10: Explicit Invariants

Every agent and script node must document preconditions, postconditions, and loop
invariants. Violations surface as errors, not silent degradation.

→ [Full details](references/p10-invariants.md)

## P11: Rubric-Based Scoring

All quality assessments use structured scoring rubrics with named dimensions,
explicit weights, and a 1–5 scale. Critical threshold: any dimension ≤ 2 is blocking.
Standard rubrics are defined for plan review, code review, and user acceptance.

→ [Full details](references/p11-rubric-scoring.md)

## P12: Short-Lived Agent Sessions

Agent sessions should be designed short enough to never trigger context compaction.
If compaction occurs, it must be logged and the workflow design should be revisited.

→ [Full details](references/p12-short-lived-sessions.md)

## P13: Human-Readable Gates

Gate prompts are Jinja2 templates rendered as Markdown. Templates own layout (no
JSON dumps), surface clickable artifact links, and consume structured agent outputs.

→ [Full details](references/p13-human-readable-gates.md)
