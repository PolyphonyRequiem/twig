# P4: Explicit User Intent — New / Redo / Resume

Workflows accept an explicit intent input that governs how existing assets are treated:

| Intent | Meaning | Behavior |
|--------|---------|----------|
| **new** | Starting fresh | Existing child work items, branches, and PRs under the root are treated as an error. The root work item should have no prior work. |
| **redo** | Do it again from scratch | Delete existing assets in scope (child items, branches, PRs) and re-implement without reading prior work into context. Avoids polluting agent sessions with prior knowledge. |
| **resume** | Continue where we left off | Discover state (P3), skip completed steps, pick up work in progress or the next pending unit. |

This replaces ad-hoc inputs like `skip_plan_review`, `plan_path`, `has_explicit_plan`,
etc. The intent signal is sufficient to determine the workflow's entry behavior.
