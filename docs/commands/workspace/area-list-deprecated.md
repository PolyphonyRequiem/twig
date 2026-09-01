---
command: area list
group: workspace
summary: Deprecated alias for `workspace area list`. Prints a deprecation hint on stderr.
stability: stable
mutates: none
---

# `twig area list`

**Deprecated.** Use [`twig workspace area list`](./workspace-area-list.md)
instead. Registered as `[Hidden] [Command("area list")]`; delegates after
emitting a hint (`src/Twig/Program.cs:1246-1252`).

## Synopsis

```
twig area list [flags]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

1. Emit `hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.`
   on stderr (`src/Twig/Program.cs:1250`).
2. Delegate to `AreaCommand.ListAsync(output, ct)`
   (`src/Twig/Program.cs:1251`).

All behavior, exit codes, and output shapes match
[`workspace area list`](./workspace-area-list.md).

## Examples

```
$ twig area list
hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.
\Contoso\Team A  (under)
1 area path(s) configured.
```

```
$ twig area list -o json
hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.
{"areaPathList":{"count":1,"entries":[{"path":"\\Contoso\\Team A","includeChildren":true}]}}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|

## See also

- [`workspace area list`](./workspace-area-list.md) — canonical form
- [`area`](./area-deprecated.md)
- [`area sync`](./area-sync-deprecated.md)
