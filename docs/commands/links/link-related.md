---
command: link related
group: links
summary: Add a symmetric Related link between two work items, optionally with a comment.
stability: stable
mutates: ado
---

# `twig link related`

Record a symmetric `System.LinkTypes.Related` edge between two published work
items. Related is non-directional: ADO materialises the reverse edge itself,
so writing it from either endpoint makes it visible from both. There is no
"relate the other way" command.

## Synopsis

```
twig link related <targetId> [-c <comment>] [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`targetId`|yes|Work item ID to relate this item to.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-c`, `--comment`|`string?`|`null`|Why the two items are related. Persisted on the link itself.|
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to the shared dependency core with `linkType = Related` and the
optional `comment` piped through to `AddLinkWithCommentAsync`
(`src/Twig/Commands/LinkCommand.cs:266`, `src/Twig/Commands/LinkCommand.cs:386`).
The same guards apply as for `link predecessor` / `link successor`: self-links
are rejected, the target must exist in ADO, and a duplicate edge short-circuits
to `linkUnchanged` and exits `0`. Both endpoints are resynced on success
because the reverse edge lands on the other side too
(`src/Twig/Commands/LinkCommand.cs:261`).

## Examples

Relate the active item to `#615`:

```
$ twig link related 615
#1234 now has related #615.
  #1234 ──related──▶ #615
```

Relate two items and record why, JSON output:

```
$ twig link related 615 --comment "same root cause" --id 619 -o json
{
  "kind": "linkAdded",
  "message": "#619 now has related #615.",
  "count": 1,
  "links": [ { "sourceId": 619, "targetId": 615, "linkType": "related" } ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Related edge added, or already present|`0`|
|Active/target item not found in cache|`1`|
|Self-link attempted|`1`|
|Target work item not found in ADO|`1`|
|ADO rejected the write|`1`|

## See also

- [`link unrelate`](./link-unrelate.md)
- [`link unlink`](./link-unlink.md)
- [Group overview](./README.md)
