---
command: link unlink
group: links
summary: Remove any non-hierarchy link (predecessor, successor, or related) from a work item.
stability: stable
mutates: ado
---

# `twig link unlink`

Generic remover for non-hierarchy edges. Accepts any of `predecessor`,
`successor`, or `related` and dispatches to the same core as the named
`link unrelate` counterpart. Hierarchy edges are not accepted here —
[`link unparent`](./link-unparent.md) owns those.

## Synopsis

```
twig link unlink <linkType> <targetId> [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`linkType`|yes|Link type to remove: `predecessor`, `successor`, or `related`. Case-insensitive.|
|`targetId`|yes|Work item ID at the other end of the link.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Routes through the shared dependency core with `remove = true`
(`src/Twig/Commands/LinkCommand.cs:305`). Any other value for `linkType`
— including hierarchy names — is rejected with a message pointing at
`twig link unparent` (`src/Twig/Commands/LinkCommand.cs:332`). Self-links are
rejected before mutation; the target must resolve. Both endpoints are resynced
on success so the reverse edge disappears from the cache too.

## Examples

Remove a predecessor edge from the active item:

```
$ twig link unlink predecessor 65
Removed predecessor #65 from #1234.
```

Remove a related edge for a specific pair, JSON output:

```
$ twig link unlink related 615 --id 619 -o json
{
  "kind": "linkRemoved",
  "message": "Removed related #615 from #619.",
  "count": 0,
  "links": []
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Link removed|`0`|
|Unknown or hierarchy link type|`1`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Target work item not found in ADO|`1`|
|ADO rejected the remove (e.g. no such edge)|`1`|

## See also

- [`link predecessor`](./link-predecessor.md)
- [`link successor`](./link-successor.md)
- [`link related`](./link-related.md)
- [`link unrelate`](./link-unrelate.md)
- [`link unparent`](./link-unparent.md)
- [Group overview](./README.md)
