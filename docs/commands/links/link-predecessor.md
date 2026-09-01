---
command: link predecessor
group: links
summary: Mark the active (or targeted) item as blocked by another item.
stability: stable
mutates: ado
---

# `twig link predecessor`

Record a `Predecessor` edge — the named target must complete before this item
can proceed. Use it to model "blocked by" relationships that ADO understands
natively.

## Synopsis

```
twig link predecessor <targetId> [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID that must complete first (the blocker).|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Routes through the shared dependency core
(`src/Twig/Commands/LinkCommand.cs:242`), which resolves the source, rejects
self-links (`src/Twig/Commands/LinkCommand.cs:354`), verifies the target
exists in ADO (`src/Twig/Commands/LinkCommand.cs:362`), and short-circuits to
`linkUnchanged` if the predecessor edge is already present
(`src/Twig/Commands/LinkCommand.cs:374`). On success the edge is added,
both endpoints are resynced, and the current link set is rendered.

Cycle detection is deliberately not implemented. Only self-links are rejected;
a chain of predecessors can be made cyclic and neither ADO nor twig will stop
you (`src/Twig/Commands/LinkCommand.cs:225`).

## Examples

Mark the active item as blocked by `#65`:

```
$ twig link predecessor 65
#1234 now has predecessor #65.
  #1234 ──predecessor──▶ #65
```

Mark a specific item as blocked, minimal output:

```
$ twig link predecessor 65 --id 66 -o minimal
#66 now has predecessor #65.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Predecessor added, or already present|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Target work item not found in ADO|`1`|
|ADO rejected the write|`1`|

## See also

- [`link successor`](./link-successor.md)
- [`link unlink`](./link-unlink.md)
- [Group overview](./README.md)
