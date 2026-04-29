# P3: Re-Entry by State Discovery

Re-entry into workflows and sub-workflows should pick up where they left off by
inspecting observable state: approved plans, ADO work item states, local branches,
git worktree status, commit history, and merged PRs — as appropriate for the
workflow's domain of responsibility.

Use the **minimum set of checks** needed to deterministically understand current
state. Don't re-derive what can be read directly.

## Implications

- A resumed implementation workflow checks which Issues are Done, which PRs are
  merged, which branches exist — then starts from the next incomplete unit
- A resumed planning workflow checks which plans exist and which work items are seeded
- State discovery is deterministic (scripts preferred over LLM inference)
