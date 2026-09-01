---
command: query
group: context
summary: Search and filter work items via an ad-hoc WIQL query built from CLI flags.
stability: stable
mutates: local
---

# `twig query`

Assembles a WIQL query from the supplied flags, executes it against ADO,
writes the resulting rows into the local cache, and renders them in the
requested format. With no filters it prints a summary of available
filters and configured defaults.

## Synopsis

```
twig query [<text>] [--title <s>] [--description <s>] [--type <s>]
           [--state <s>] [--assigned-to <s>] [--area-path <s>]
           [--iteration-path <s>] [--created-since <dur>]
           [--changed-since <dur>] [--top <n>] [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `text` | no | Free‑text search that filters `System.Title` or `System.Description` via CONTAINS. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--title` | string | none | CONTAINS match on `System.Title`. |
| `--description` | string | none | CONTAINS match on `System.Description`. |
| `--type` | string | none | Exact match on work item type (e.g. `Task`, `Bug`). |
| `--state` | string | none | Exact match on state (e.g. `Doing`, `Done`). |
| `--assigned-to` | string | none | Exact match on assignee display name or email. |
| `--area-path` | string | none | UNDER match on area path. Falls back to configured defaults when omitted. |
| `--iteration-path` | string | none | UNDER match on iteration path. |
| `--created-since` | duration | none | Items created within `Nd`, `Nw`, or `Nm` (days/weeks/months). |
| `--changed-since` | duration | none | Items changed within `Nd`, `Nw`, or `Nm`. |
| `--top` | int | `25` | Maximum number of results to return. |
| `-o`, `--output` | `human` \| `json` \| `minimal` \| `ids` | `human` | Output format. `ids` prints one ID per line. |

## Behavior

1. **No filters** — when every filter flag is `null`, the command prints
   a human‑readable summary of available filters, configured default
   area paths, and examples, then exits `0`. `minimal` and `ids`
   suppress the summary output (`src/Twig/Commands/QueryCommand.cs:100-105`,
   `238-291`).
2. **Duration validation** — `--created-since` and `--changed-since`
   must match `^(\d+)([dwm])$`. An invalid value writes an error to
   stderr and exits `1` (`src/Twig/Commands/QueryCommand.cs:107-109`,
   `293-306`).
3. **Query parameter build** — when `--area-path` is omitted the
   command applies `TwigConfiguration.Defaults.ResolveAreaPaths()` as
   the default scope (`src/Twig/Commands/QueryCommand.cs:112-132`).
4. **WIQL execution** — `WiqlQueryBuilder.Build` produces the WIQL,
   `IAdoWorkItemService.QueryByWiqlAsync` returns IDs bounded by
   `--top`, and `FetchBatchAsync` retrieves the full rows
   (`src/Twig/Commands/QueryCommand.cs:134-143`).
5. **Local cache write** — non‑empty results are persisted via
   `IWorkItemRepository.SaveBatchAsync`, so subsequent `twig show` and
   `twig show-batch` reads see the freshly returned rows
   (`src/Twig/Commands/QueryCommand.cs:145-147`).
6. **Truncation** — `truncated` is set to `true` when the returned row
   count equals `--top` (`src/Twig/Commands/QueryCommand.cs:149-151`).
7. **Output branching** — `ids` skips the formatter entirely and prints
   one ID per line; other formats render a document with `query`,
   `count`, `truncated`, and an `items` table
   (`src/Twig/Commands/QueryCommand.cs:153-183`,
   `185-222`). Hints are emitted for human formats and suppressed for
   `json` / `minimal` by `HintEngine`.

## Examples

```
$ twig query "login bug"
query: title or description contains 'login bug'
count: 3   truncated: false

  ID    TYPE  TITLE                       STATE   ASSIGNED
  1234  Task  Fix login redirect          Doing   jane@…
  1290  Bug   Login timeout on Safari     Doing   paula@…
  1301  Task  Add login telemetry         To do   —
```

```
$ twig query --state Doing --changed-since 7d --top 50 --output ids
1234
1290
1367
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Query executed (including empty result set) or summary printed | `0` |
| `--created-since` / `--changed-since` does not match `Nd|Nw|Nm` | `1` |
| WIQL execution or batch fetch throws (ADO / auth / network) | propagates as non‑zero |

## See also

- [`twig show`](./show.md) — inspect a row from the result set.
- [`twig show-batch`](./show-batch.md) — pipe `--output ids` into another
  batch read.
- [`../workspace/README.md`](../workspace/README.md) — configure the
  default area paths that scope `--area-path` when omitted.
