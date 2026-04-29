# P5: Type-Agnostic Workflow Structure

The full SDLC entry point must not hardcode assumptions about the work item type.
Workflows handle the ADO hierarchy naturally:

| Root Type | Planning Scope | Implementation Scope |
|-----------|---------------|---------------------|
| **Epic** | Plan Epic → plan Issues → plan Tasks | Implement all Issues/Tasks, PR per PG |
| **Issue** | Plan Issue → plan Tasks | Implement all Tasks, PR per PG |
| **Task** | Plan the Task directly | Implement the Task, single commit or PR |

The same workflow nodes handle all types — branching on type happens within agents
or scripts based on discovered state, not via separate workflow paths.
