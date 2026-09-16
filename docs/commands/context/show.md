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
| `--fields` | string | none | Comma-separated field reference names; opt into compact JSON. |
| `--sections` | string | none | Comma-separated `links`, `children`, `parent`; opt into compact JSON. |

## Behavior

- **By ID.** Reads the local cache first. With `--refresh`, a cache miss
  fetches the item and its links through the protected, pull-only sync path;
  no active item is needed. Without `--refresh`, a miss exits `1` and
  suggests `twig show <id> --refresh`, not a context-changing `set`.
- **No ID.** The active work item comes from `IContextStore`. Missing
  context prints a branch‑detection hint and exits `1`
  (`src/Twig/Commands/ShowCommand.cs:110-146`).
- **Cache‑only by default (wayfinder 0004 §3).** Without `--refresh` no
  ADO call is made. The read reports staleness through
  `SyncCoordinatorFactory.ReadOnly.ReadItemAsync`: a `Stale` result adds
  a `StaleHint` on stderr for human formats, and unverified links add an
  `UnverifiedLinksHint` (`src/Twig/Commands/ShowCommand.cs:148-180`).
- **`--refresh`.** Fetches the item and its links, materializes immediate
  link targets, and refreshes its parent when present. It never flushes
  pending writes or changes active selection, navigation history or Bench.
  Dirty/pending item fields remain local; refreshed links carry
  `linksVerifiedAt`. Missing or inaccessible items do not discard local
  work or pending rows. Seeds are not fetched from ADO.
- **Enrichment.** After the item is resolved the command loads children,
  parent, links (with `linksVerifiedAt`), field definitions, status
  fields, child progress, pending changes, and git context — all
  best‑effort, all from cache
  (`src/Twig/Commands/ShowCommand.cs:157-194`).
- **Machine formats** (`json`, `minimal`) sync synchronously when
  `--refresh` is set and then emit a single complete output. Failed or
  incomplete refresh exits `1` with a format-aware error on stderr rather
  than presenting stale cache data as a successful fresh read. The error
  retains the underlying network/permission failure and affected IDs;
  cancellation propagates. Human TTY output can render cached data first
  and then revise it; an uncached item is fetched before its first render.
- **`--tree`** hands off to `TreeRenderingService.RenderTreeAsync`,
  which produces the parent chain + child forest and honors the same
  `--refresh` semantics (`src/Twig/Commands/ShowCommand.cs:56-71`).

### Selected JSON reads

`twig show 1234 -o json --fields System.Title,System.State` returns only the
requested facts plus connection, revision, freshness, completeness and a
`fullRead` command. Both flags require JSON and are incompatible with `--tree`.
Core fields use the same field-value resolver as the detail document.
`requestedFields` entries distinguish `present` (with the complete value),
`absent` (known empty), and `unknown` (not carried by Twig). A field's absence
from the cached dictionary alone is never proof of absence on ADO.

`--sections links` includes cached edges and their verification timestamp;
unverified edges are `unknown`, not an authoritative empty set. Cached children
are a `partial` view. Freshness includes `hasLocalChanges` so a protected local
body is not mistaken for a clean server snapshot. Omit both flags for the
unchanged full-detail path; `--refresh` retains the protected pull-only behavior.

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
$ twig show 1234 --refresh --output json
{"id":1234,"title":"Fix login redirect","state":"Doing","type":"Task", ... }
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Item rendered | `0` |
| ID not present in local cache and refresh not requested | `1` |
| No active work item set and no ID given | `1` |
| Item inaccessible, or machine refresh failed/incomplete | `1` |
| Tree rendering requested but service unavailable | `1` |

## See also

- [`twig show-batch`](./show-batch.md) — multi‑ID cache‑only lookup.
- [`twig tree-set`](./tree-set.md) — render an arbitrary working set as
  annotated trees.
- [`twig set`](./set.md) — change which item `twig show` (no args) targets.
- [`twig history`](./history.md) — revision history for the same item.
