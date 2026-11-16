# Human navigation

Only these targeted ADO navigation routes are retained:

- [`nav up`](nav-up.md): select the discovered parent.
- [`nav down`](nav-down.md): select a child, optionally by ID/title pattern.
- [`nav next`](nav-next.md) / [`nav prev`](nav-prev.md): select a sibling.

They change worktree-local human context, can fetch missing observations, and
are refused with structured output. They do not assign or claim an item.
GitHub navigation is not qualified.

Bare interactive `nav`, `nav back/fore/history`, and top-level
`up/down/next/prev/back/fore` are retired. Use explicit `show` targets for scripts.
