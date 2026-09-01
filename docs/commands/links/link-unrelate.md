---
command: link unrelate
group: links
summary: Remove a symmetric Related link between two work items.
stability: stable
mutates: ado
---

# `twig link unrelate`

Remove a `System.LinkTypes.Related` edge between the active (or targeted)
work item and another. A named counterpart to
[`link related`](./link-related.md); the generic form
[`link unlink related <id>`](./link-unlink.md) routes through the same code
path and produces the same result.

## Synopsis

```
twig link unrelate <targetId> [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID at the other end of the link.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to the shared dependency core with `linkType = Related` and
`remove = true` (`src/Twig/Commands/LinkCommand.cs:289`). Self-links are
rejected before mutation; the target must resolve. Removing a link that does
not exist is left to ADO to reject, and the command surfaces the failure to
stderr (`src/Twig/Commands/LinkCommand.cs:388`). Both endpoints are resynced
on success — the reverse edge is gone from the far side as well.

## Examples

Remove the related link from the active item to `#615`:

```
$ twig link unrelate 615
Removed related #615 from #1234.
```

Remove a specific pair, minimal output:

```
$ twig link unrelate 615 --id 619 -o minimal
Removed related #615 from #619.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Related edge removed|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Target work item not found in ADO|`1`|
|ADO rejected the remove (e.g. no such edge)|`1`|

## See also

- [`link related`](./link-related.md)
- [`link unlink`](./link-unlink.md)
- [Group overview](./README.md)
