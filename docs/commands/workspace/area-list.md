---
command: workspace area list
group: workspace
summary: List configured area paths with match semantics.
stability: stable
mutates: none
---

# `twig workspace area list`

Print every configured area path together with its match semantics
(`under` vs `exact`). Useful before running `workspace area sync` — which
replaces the list — or when debugging why an item is or is not showing up in
`workspace area`.

## Synopsis

```
twig workspace area list [flags]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `AreaCommand.ListAsync`
(`src/Twig/Commands/AreaCommand.cs:239-253`). Behavior:

- If no entries are configured, emit `No area paths configured.` as an
  `info`-severity `noAreaPathsConfigured` record and exit `0`
  (`src/Twig/Commands/AreaCommand.cs:245-249`).
- Otherwise emit each entry through `RenderAreaPathList`, which produces a
  streamed human list or a machine-format table depending on `--output`
  (`src/Twig/Commands/AreaCommand.cs:333-357`).

The command does not touch ADO and does not mutate configuration.

## Examples

```
$ twig workspace area list
\Contoso\Team A  (under)
\Contoso\Team A\Reliability  (exact)
2 area path(s) configured.
```

```
$ twig workspace area list -o json
{"areaPathList":{"count":2,"entries":[{"path":"\\Contoso\\Team A","includeChildren":true},{"path":"\\Contoso\\Team A\\Reliability","includeChildren":false}]}}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|

## See also

- [`workspace area add`](./area-add.md)
- [`workspace area remove`](./area-remove.md)
- [`workspace area sync`](./area-sync.md)
- [`workspace area`](./area.md)
