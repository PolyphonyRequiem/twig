# P1: Work Items Are the Source of Truth

Once plans are committed and work items are created, **ADO work items are the
authoritative source for state and context**. Agents must read work item state
(title, description, status, parent/child relationships, tags) from ADO — not
from plan files, agent memory, or prior outputs.

Plan documents are reference material, not operational state. They may be consulted
when implementers or reviewers need additional context to resolve ambiguity, but
they must never override what the work items say.

## Implications

- PG-to-task mapping belongs on work items (e.g., ADO tags), not in plan text
- Work item state transitions are the completion signal, not todo lists or agent outputs
- Stale plan references must not block or misdirect execution
