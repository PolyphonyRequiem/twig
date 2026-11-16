# Connection, Bench and context

A **connection** is an explicit registered provider/source/principal binding.
It is not inferred from a Git remote or whichever account signed in last.
Fresh workspaces use the current Core store, not
`.twig/{org}/{project}/twig.db` or legacy SQL tables.

A **Bench** is a named, durable user-wide saved view containing selectors/pins.
It stores the rule, not a result snapshot. Metadata management does not create
or select a default Bench, refresh work or claim ownership. Multiple contexts
can select the same definition; changing its rules is shared, changing one
context's selection is not.

A **context** is the local selection/view in a worktree or daemon session.
`twig set` changes the active-item pointer, never assignment or a work claim.
Structured machine operations must target items explicitly rather than rely on
that pointer.

`workspace`/`ws` render static cached projections, not a full-screen application.
Machine projections exclude incidental active-item relationships but preserve
explicit Bench scope and local drafts. Workspace exclusions are separate local
view rules, not subtracting Bench selectors or provider deletions.

Native planning commands explicitly inspect/refresh planning metadata and
acquire saved-query/backlog membership. `bench browse` is bounded acquisition,
not the retired `bench browser` or TUI. Unavailable sources/metadata remain
explicit; an empty projection is not proof of complete cross-source membership.

See [Bench commands](../commands/bench/README.md),
[workspace commands](../commands/workspace/README.md) and
[current vocabulary](../../CONTEXT.md).
