---
command: show
group: context
summary: Display a work item without changing context; cache-only by default.
stability: stable
mutates: none
---

# `twig show`

Read‑only display of a work item's details, links, children, parent chain,
pending changes, and (when available) git context. Reads the local cache
by default and never changes which item is active. Pass `--refresh` to
sync from ADO before rendering.

## Synopsis

```
twig show [<id>] [--tree] [--refresh] [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `id` | no | Work item ID to display. Omit to show the active work item selected with [`twig set`](./set.md). |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format. |
| `--tree` | bool | `false` | Render the parent chain + children as a tree instead of the detail card. |
| `--refresh` | bool | `false` | Sync the item and its links from ADO before rendering. |

## Behavior

- **By ID.** The item is fetched from the local cache via
  `IWorkItemRepository.GetByIdAsync`. A cache miss exits `1` with a hint
  to run `twig set <id>` to fetch it
  (`src/Twig/Commands/ShowCommand.cs:97-108`).
- **No ID.** The active work item comes from `IContextStore`. Missing
  context prints a branch‑detection hint and exits `1`
  (`src/Twig/Commands/ShowCommand.cs:110-146`).
- **Cache‑only by default (wayfinder 0004 §3).** Without `--refresh` no
  ADO call is made. The read reports staleness through
  `SyncCoordinatorFactory.ReadOnly.ReadItemAsync`: a `Stale` result adds
  a `StaleHint` on stderr for human formats, and unverified links add an
  `UnverifiedLinksHint` (`src/Twig/Commands/ShowCommand.cs:148-180`).
- **`--refresh`.** The item, its links, and its parent (when present)
  are synced through the read‑only sync coordinator before rendering
  (`src/Twig/Commands/ShowCommand.cs:196-248`).
- **Enrichment.** After the item is resolved the command loads children,
  parent, links (with `linksVerifiedAt`), field definitions, status
  fields, child progress, pending changes, and git context — all
  best‑effort, all from cache
  (`src/Twig/Commands/ShowCommand.cs:157-194`).
- **Machine formats** (`json`, `minimal`) sync synchronously when
  `--refresh` is set and then emit a single complete output. Human TTY
  output renders cached data immediately, syncs in the background, then
  re‑renders.
- **`--tree`** hands off to `TreeRenderingService.RenderTreeAsync`,
  which produces the parent chain + child forest and honors the same
  `--refresh` semantics (`src/Twig/Commands/ShowCommand.cs:56-71`).

## Examples

```
$ twig show 1234
#1234  Fix login redirect  [Doing]
Type: Task     Assigned: jane@example.com
Area: Contoso\Web     Iteration: Contoso\Sprint 42

Parent:   #1200 Login reliability
Children: 2/3 done
Pending:  1 field change, 0 notes
Branch:   sdlc/1234 (PR #987: Active)
```

```
$ twig show --refresh --output json
{"id":1234,"title":"Fix login redirect","state":"Doing","type":"Task", ... }
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Item rendered | `0` |
| ID not present in local cache | `1` |
| No active work item set and no ID given | `1` |
| Active item unreachable from ADO under `--refresh` | `1` |
| Tree rendering requested but service unavailable | `1` |

## See also

- [`twig show-batch`](./show-batch.md) — multi‑ID cache‑only lookup.
- [`twig tree-set`](./tree-set.md) — render an arbitrary working set as
  annotated trees.
- [`twig set`](./set.md) — change which item `twig show` (no args) targets.
- [`twig history`](./history.md) — revision history for the same item.
