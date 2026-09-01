---
command: show-batch
group: context
summary: Display multiple work items by ID from the local cache; missing IDs are silently skipped.
stability: stable
mutates: none
---

# `twig show-batch`

Cache‑only bulk read: given a comma‑separated list of IDs it emits a
document per item. Missing or non‑numeric segments are dropped silently
so the command is safe to feed with a machine‑generated list.

## Synopsis

```
twig show-batch <ids>
twig show-batch --batch <ids> [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `batchArg` | one of `batchArg` or `--batch` | Comma‑separated work item IDs used positionally, e.g. `1234,5678,9012`. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--batch` | string | none | Comma‑separated work item IDs; equivalent to the positional form. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

- Ids are resolved from the positional argument and `--batch`; named
  wins on conflict (`src/Twig/Program.cs:547`,
  `src/Twig/Program.cs:562-566`). Neither supplied exits `1` with a
  usage error on stderr (`src/Twig/Program.cs:553-557`).
- The list is executed as a **cache‑only** read via
  `IWorkItemRepository`; there is no ADO fetch and no `--refresh` flag.
  Missing IDs and non‑numeric segments are dropped without a warning —
  the command is intentionally lossy so it can be driven from a wide
  input list (`src/Twig/Commands/ShowCommand.cs:78-88`).
- Per‑row `links` and `relations` share the exact wire shape used by
  `twig show` for the single‑item document (see
  `src/Twig/Commands/ShowCommand.cs:684-702`).
- Positional guard is deliberately disabled for this command because
  its argument is a comma‑separated ID list, not free text — see
  `src/Twig/Commands/StrayPositionalGuard.cs:59-66`.

## Examples

```
$ twig show-batch 1234,5678,9012 --output json
[
  {"id":1234,"title":"Fix login redirect","state":"Doing","type":"Task", ... },
  {"id":5678,"title":"Redirect loop on SSO","state":"Doing","type":"Bug",  ... },
  {"id":9012,"title":"Roll out MFA prompt","state":"To do","type":"Task",  ... }
]
```

```
$ twig show-batch --batch 42
#42  Broken avatar cache  [Doing]
Type: Bug     Assigned: paula@example.com
Pending: 0 field changes, 0 notes
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Any items rendered (or empty result) | `0` |
| No id list supplied on positional or `--batch` | `1` (usage error on stderr) |

## See also

- [`twig show`](./show.md) — single‑item detail card, with `--refresh`.
- [`twig tree-set`](./tree-set.md) — same input shape, forest render.
- [`twig sync`](../getting-started/sync.md) — refresh the local cache before
  running a batch read.
