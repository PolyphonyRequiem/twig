# P2: Plans Are Context, Not Control Flow

Plan documents should only be referenced when additional context is needed by
implementers and reviewers to resolve ambiguity that emerges during implementation
and validation. Plans do not drive routing, task assignment, or state transitions.

## Implications

- Workflows must not parse plan files to determine PG structure at implementation time
- PG membership, task ordering, and scope come from work items
- Plans provide design rationale, architectural decisions, and acceptance criteria
