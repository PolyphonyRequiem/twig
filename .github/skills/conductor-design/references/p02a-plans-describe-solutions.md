# P2a: Plans Describe Solutions, Not Work Items

Plans describe *what needs to be done* — technical design, architecture, PR groupings,
acceptance criteria. Plans do not define work item hierarchies. A separate seeding
step reads the plan and creates work items (Epics, Issues, Tasks) as an execution
plan for the design. The plan is a design artifact; work items are the execution
artifact.

## Implications

- The architect agent designs the solution and defines PR groups
- The seeder agent reads the plan and creates work items, tagging each with its PG
- Plans should not contain ADO IDs, state tracking, or work item metadata
- If ADO IDs appear in a plan, they are informational cross-references only
