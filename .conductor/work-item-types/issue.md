# Issue — Work Item Type Definition (Basic Process)

## Definition

An Issue represents a coherent unit of work that delivers a meaningful change to the codebase. It is the primary unit of planning and delivery in the twig SDLC. Issues can be implemented directly (as a single PR group) or decomposed into Tasks when the scope is too large for a single focused session. Issues are the most frequently created and reviewed work item type and should be written to serve both implementation and review.

## Purpose

An Issue answers: **"What specific change are we making, why does it matter, and what does done look like?"** Issues bridge strategic Epics (what outcome we want) and tactical Tasks (what exactly to change). An Issue should be understandable on its own — a reader should know what problem it solves and how success is measured without reading the parent Epic.

## Audience

| Role | How They Use Issues |
|------|-------------------|
| **Project Owner** | Creates Issues, reviews scope and acceptance criteria. Primary author and reviewer. |
| **Contributor** | Implements Issues or their child Tasks. Reads for full context. |
| **AI Agent** | Plans and implements Issues. Reads the description as its instruction set for planning or coding. |

## Ownership

- **Driver:** The person or agent implementing the work
- **Assigned To:** The implementer (human or agent)
- **Reviewer:** Project Owner or peer reviews the implementation (code review on PRs)

## Structure

### Direct Implementation vs Decomposition

An Issue is either **directly implemented** or **decomposed**:

- **Direct Implementation** — The Issue is small enough to implement in a single PR group (~2000 LoC or less). The Issue description carries all context needed.
- **Decomposed** — The Issue is broken into Tasks, each representing an atomic unit of work. Used when scope exceeds a single PR group or when the work naturally separates into independent steps.

**Decomposition threshold:** Issues estimated above 5 dev-days should generally be decomposed into Tasks.

### Naming Conventions

- Start with a verb phrase or outcome: "Add batch execution engine", "Remove flow git integration"
- Be specific enough that the title alone communicates scope
- Don't prefix with "Issue:" — the work item type already says that
- Keep titles under 80 characters

**Good examples:**
- "Add batch MCP operations for bulk work-item updates"
- "Extract domain-infrastructure DTO layer"
- "Guard duplicate work-item creation during sync"
- "Migrate command queue to simplified pipeline"

**Bad examples:**
- "Fix the thing"
- "Issue: Do refactoring"
- "BATCH_OPERATIONS_REFACTOR"
- "Sprint 5 work"

## In Scope for an Issue

- A coherent set of code changes with a clear objective
- Work requiring a plan document when non-trivial
- Changes spanning multiple files but unified by a single purpose
- Work estimated at hours to days (not minutes, not weeks)
- Changes that are independently buildable, testable, and shippable

## Out of Scope for an Issue

- Single-line typo fixes with no design context (just make the change)
- Multi-week strategic initiatives (use an Epic containing Issues)
- Vague work like "improve quality" — must have concrete acceptance criteria
- Work that can't be described with clear success criteria

## Description Template

See: `templates/issue-template.md`

## Language Guidelines

- **Summary:** 2–3 sentences: what we're changing, why, expected outcome. Write for someone with codebase context but not necessarily familiar with this specific area.
- **Problem/Motivation:** Focus on the concrete problem. What's broken, slow, missing, or poorly structured? Why does it matter now?
- **Proposed Approach:** Technical but accessible. Reference file paths, class names, patterns. Link to the plan document for deeper rationale.
- **Acceptance Criteria:** Measurable, binary (pass/fail). Include build+test as baseline. Add domain-specific criteria that verify the change works.

## Relationship to Plan Documents

An Issue links to a conductor plan document in `docs/projects/`. The plan carries the detailed technical design, PR group strategy, and task breakdown. The Issue description carries the "what and why" — the plan carries the "how."

The plan path should be referenced in the description:
```markdown
## Context
**Plan:** `docs/projects/<slug>.plan.md`
```

Issues that are trivially small (< 1 dev-day, obvious approach) may skip the plan document and carry all context in their description.
