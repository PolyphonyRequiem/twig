# Epic — Work Item Type Definition (Basic Process)

## Definition

An Epic is a feature initiative or major refactor spanning multiple Issues and typically lasting days to weeks. Epics represent the highest level of planned work and define the "why" and "what outcome" — goals, success criteria, and scope boundaries — while delegating the "what specifically" and "how" to their child Issues. Epics are containers for planning and tracking, not implementation; they should never contain Tasks directly.

## Purpose

An Epic answers: **"What capability or improvement are we investing in, and how will we know we succeeded?"** Epics translate project priorities into a set of Issues that, when completed together, deliver a meaningful outcome.

## Audience

| Role | How They Use Epics |
|------|-------------------|
| **Project Owner** | Creates Epics for feature initiatives and refactors. Defines scope and success criteria. Primary author. |
| **Contributor** | Reads Epics for strategic context when working on child Issues and Tasks. |
| **AI Agent** | Reads Epics to understand the goal of a planning or implementation session. |

## Ownership

- **Owner:** Project Owner or primary contributor
- **Assigned To:** The person driving the initiative
- **Review Cadence:** As needed — when scope changes or all children complete

## In Scope for an Epic

- Feature initiatives spanning multiple Issues
- Major refactors with a unifying technical objective
- Cross-cutting improvements (e.g., "adopt Result pattern everywhere")
- Work with measurable success criteria (tests pass, performance improves, API surface changes)

## Out of Scope for an Epic

- Single-session work (use an Issue)
- Technical implementation details (belong in Issues and Tasks)
- Bug fixes or quick patches (use an Issue directly)
- Individual task tracking (use Tasks)
- Epics should never directly contain Tasks

## Naming Conventions

- Describe the outcome or capability, not activities: "Add batch MCP operations" not "Do batch stuff"
- Keep under 100 characters
- Use plain language — no abbreviations unless universally understood in context

**Good examples:**
- "Batch ADO operations for sync performance"
- "MCP server multi-workspace support"
- "Domain-infrastructure DTO layer extraction"
- "Command layer bloat reduction"

**Bad examples:**
- "Q2 stuff"
- "Various improvements"
- "Epic for the thing"

## Description Template

See: `templates/epic-template.md`

## Language Guidelines

- **Strategic Objective:** Clear, outcome-oriented. "What does the project gain from this?"
- **Success Criteria:** Measurable and verifiable. "All commands return Result types" not "improve error handling."
- **Scope:** Bounded. Define what's explicitly included and excluded.
- **Child Issues:** Listed with links as planning progresses. Each should clearly map to a portion of the objective.

## Hierarchy Rules

- Epics contain **only Issues** — never Tasks directly
- An Epic typically contains 2–8 Issues
- Epics do not nest (no Epic-in-Epic)
- Related Epics may reference each other via links

## Relationship to Plan Documents

Epics generally do NOT have conductor plan documents. They are strategic containers. The planning lifecycle starts at the Issue level — each child Issue gets its own plan document in `docs/projects/`.

Epics may reference project-level documentation (PRDs, architecture docs, specs) as external references, not conductor plan artifacts.
