# Navigation

Commands for moving the active work item around the sprint tree and along the
navigation history. Every command in this group mutates only the local workspace
context — the active work item pointer, the navigation history cursor, and the
optional prompt-state file. Nothing here writes to Azure DevOps.

Two coordinate systems live in this group:

- **Tree navigation** (`nav up`, `nav down`, `nav next`, `nav prev`) walks the
  parent/child/sibling structure of the sprint. These commands delegate to the
  same `set` machinery you would use by hand, so they **record** a new entry in
  the navigation history each time they land on a new item.
- **History navigation** (`nav back`, `nav fore`, `nav history`) walks the
  chronological stack of items you have visited. Back and fore write the active
  context directly and **do not** record new history entries, so you can move
  the cursor within the stack without truncating it.

Bare `nav` launches an interactive tree navigator when the process is attached
to a TTY.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`nav`](./nav.md)|Launch the interactive tree navigator.|local|
|[`nav up`](./nav-up.md)|Navigate to the parent work item.|local|
|[`nav down`](./nav-down.md)|Navigate to a child work item.|local|
|[`nav next`](./nav-next.md)|Navigate to the next sibling.|local|
|[`nav prev`](./nav-prev.md)|Navigate to the previous sibling.|local|
|[`nav back`](./nav-back.md)|Move backward in the navigation history.|local|
|[`nav fore`](./nav-fore.md)|Move forward in the navigation history.|local|
|[`nav history`](./nav-history.md)|Display or pick from the navigation history.|local|

## Deprecated aliases

The bare verbs below are retained backward-compatibility aliases for the `nav *`
subcommands. They are marked `[Hidden]` in the CLI framework (they do not appear
in the top-level help listing) and expose fewer flags than the canonical form —
notably, they omit the short `-o` alias for `--output`. Prefer the `nav *` form
in new scripts.

|Alias|Canonical|
|---|---|
|[`up`](./up.md)|`nav up`|
|[`down`](./down.md)|`nav down`|
|[`next`](./next.md)|`nav next`|
|[`prev`](./prev.md)|`nav prev`|
|[`back`](./back.md)|`nav back`|
|[`fore`](./fore.md)|`nav fore`|
