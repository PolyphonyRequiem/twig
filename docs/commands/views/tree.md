---
command: tree
group: views
summary: Hidden backward-compat alias that routes to show --tree, or workspace --tree with --all.
stability: stable
mutates: local
---

# `twig tree`

`twig tree` is a hidden backward-compat alias kept alive so older muscle memory and pre-existing scripts keep working. It has no rendering logic of its own: it inspects `--all` and dispatches to another canonical command (`src/Twig/Program.cs:676-680`).

- Without `--all`, it is exactly `twig show --tree`, rendering the hierarchy centered on the given work item ID (or the active item if none is passed). It calls `ShowCommand.ExecuteAsync(id, output, tree: true, refresh, ct, depth, noLive)`.
- With `--all`, it becomes `twig workspace --tree`, rendering the full workspace backlog as a hierarchy. It calls `WorkspaceCommand.ExecuteAsync(output, all: true, noLive, refresh, ct, tree: true)`.

The command is marked `[Hidden]` on the CLI surface, so it does not appear in generated help listings, but it is still accepted and shipped in the accepted-command allow list (`src/Twig/Program.cs:1596-1597`). New scripts should prefer `twig show --tree` or `twig workspace --tree` directly.

## Synopsis

```
twig tree [id] [-o|--output <format>] [--depth <n>] [--all] [--no-live] [--refresh]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `id` | no | Work item ID to root the tree at. Omit to use the active work item. Ignored when `--all` is set, because the workspace hierarchy has no single root. |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output <format>` | string | `human` | Output format: `human`, `json`, or `minimal`. |
| `--depth <n>` | int? | unset | Maximum tree depth to display. Passed through to `ShowCommand`'s tree renderer; unused when `--all` selects the workspace path. |
| `--all` | bool | `false` | Route to `workspace --tree` instead of `show --tree`. The workspace variant ignores `id` and shows every team item as a hierarchy (`src/Twig/Program.cs:678-679`). |
| `--no-live` | bool | `false` | Disable live-refresh and render a static snapshot. Forwarded to whichever underlying command is dispatched. |
| `--refresh` | bool | `false` | Sync from ADO before displaying, instead of reading cache only. |

## Behavior

- Alias-only. `twig tree` never renders anything itself; the arm chosen by `--all` fully determines the rendering, exit code, error surface, and telemetry (`src/Twig/Program.cs:678-680`).
- Without `--all`, the target is `ShowCommand` in tree mode. `id` (or the active item) sets the root; `--depth` bounds the descent; `--refresh` triggers a sync-first pass; `--no-live` opts out of the live Spectre renderer (`src/Twig/Commands/ShowCommand.cs:51-70`).
- With `--all`, the target is `WorkspaceCommand` with `tree: true` and `all: true`. It walks the workspace cache and renders the full backlog. `id` and `--depth` are silently unused because the workspace tree has no single root and no depth cap on this arm.
- Telemetry is emitted by the dispatched command, not by `tree` itself. On the `show` arm the command name recorded is `show` with a `tree=true` property (`src/Twig/Commands/ShowCommand.cs:68-69`).
- Because it is a routing shim, `twig tree --flat` is not accepted: the flag is only defined on `sprint` and `workspace`. Prefer `twig workspace --flat` for a non-tree layout.

## Examples

### Show the tree for a specific work item

```
$ twig tree 1234 --depth 2
Feature 200  Rendering pipeline overhaul
├── Task 1234  Wire status-field renderer  (Doing) ←
├── Task 1235  Batch adaptive layout       (To do)
└── Bug  1240  Live layout jitters on resize (Doing)
```

Equivalent to `twig show 1234 --tree --depth 2`. The active-item marker is `←`.

### Show the full workspace hierarchy

```
$ twig tree --all --refresh
Epic  100  Wayfinder 1.0
├── Feature 200  Rendering pipeline overhaul
│   ├── Task 1234  Wire status-field renderer
│   └── Bug  1240  Live layout jitters on resize
└── Feature 210  Sprint view polish
```

Equivalent to `twig workspace --all --tree --refresh`. `--refresh` runs a sync pass before rendering.

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Successful render | `0` |
| No active item and no `id`, without `--all` | `1`, propagated from `ShowCommand` with a branch-detection hint on stderr. |
| Tree rendering service unavailable | `1`, with "Tree rendering is not available." on stderr (`src/Twig/Commands/ShowCommand.cs:58-62`). |
| Refresh sync fails (`--refresh`) | Non-zero exit propagated from the sync coordinator; render is skipped. |
| Workspace not found (either arm) | `1`, with a "workspace not found" hint on stderr. |

## See also

- [`twig sprint`](./sprint.md) — sibling view; use `twig sprint --tree` for the sprint-scoped hierarchy.
- [`twig show`](../context/show.md) — canonical target when `--all` is not set. Prefer `twig show --tree` in new scripts.
- [`twig workspace`](../workspace/README.md) — canonical target when `--all` is set. Prefer `twig workspace --tree` in new scripts.
- [`twig tree-set`](../context/tree-set.md) — render an arbitrary set of work items as annotated trees.
