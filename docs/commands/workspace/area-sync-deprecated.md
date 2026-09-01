---
command: area sync
group: workspace
summary: Deprecated alias for `workspace area sync`. Prints a deprecation hint on stderr.
stability: stable
mutates: local
---

# `twig area sync`

**Deprecated.** Use [`twig workspace area sync`](./workspace-area-sync.md)
instead. Registered as `[Hidden] [Command("area sync")]`; delegates after
emitting a hint (`src/Twig/Program.cs:1256-1262`).

## Synopsis

```
twig area sync [flags]
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

1. Emit `hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.`
   on stderr (`src/Twig/Program.cs:1260`).
2. Delegate to `AreaCommand.SyncAsync(output, ct)`
   (`src/Twig/Program.cs:1261`).

Reads team area paths from ADO and replaces the local configuration. All
other behavior, exit codes, and output shapes match
[`workspace area sync`](./workspace-area-sync.md).

## Examples

```
$ twig area sync
hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.
Synced 3 team area path(s) from ADO.
\Contoso\Team A  (under)
\Contoso\Team A\Reliability  (under)
\Contoso\Team B  (under)
```

```
$ twig area sync -o json
hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.
{"areaPathSync":{"syncedCount":3,"entries":[…]}}
```

## Exit codes and failure modes

Identical to [`workspace area sync`](./workspace-area-sync.md).

|Condition|Result|
|Success|`0`|
|Not connected to ADO|`1`|
|ADO fetch throws|`1`|
|Team has no area paths in ADO|`1`|

## See also

- [`workspace area sync`](./workspace-area-sync.md) — canonical form
- [`area`](./area-deprecated.md)
- [`area list`](./area-list-deprecated.md)
