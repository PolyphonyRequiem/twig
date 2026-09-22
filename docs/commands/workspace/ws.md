---
command: ws
group: workspace
summary: Short alias for `workspace` — show the current workspace.
stability: stable
mutates: local
---

# `twig ws`

`ws` is the short alias for [`twig workspace`](./workspace.md). It accepts the
same flags, including `--view table|tree`, and delegates to the same
`WorkspaceCommand.ExecuteAsync` (`src/Twig/Program.cs:1117-1128`). Reach for it
when you want a quick "what am I looking at right now?" glance from the
terminal without the extra keystrokes.

## Synopsis

```
twig ws [flags]
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
|`--view`|`string`|unset|Explicit current-Bench presentation: `table` or `tree`. Cannot be combined with `--all`, `--flat`, or `--tree`.|
|`--all`|`bool`|`false`|Show all team members' items, not just yours.|
|`--no-live`|`bool`|`false`|Disable live-refresh and render a static snapshot.|
|`--refresh`|`bool`|`false`|Sync from ADO before displaying, instead of reading cache only.|
|`--flat`|`bool`|`false`|Use flat (non-tree) output instead of hierarchical rendering.|
|`--tree`|`bool`|`false`|Render full backlog hierarchy tree instead of workspace table.|

## Behavior

Semantically identical to `twig workspace`, including the current-Bench
membership invariant. `--view table` and `--view tree` select presentation only;
they do not switch to the sprint/team layout or omit pins and seeds. Tree
hierarchy uses available parent IDs even when process metadata is absent. See
[`workspace`](./workspace.md) for the authoritative behavior notes.

## Examples

```
$ twig ws
Sprint (Iteration \Sprint 42):
  ● #4211  Wire retry telemetry             Doing    You
```

```
$ twig ws --view tree
```

```
$ twig ws --all --tree -o json
{"workspace":{"iterations":["…\\Sprint 42"],"tree":[…]}}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|`--view` combined with `--tree`, `--flat`, `--all`, or sprint layout|`1`|
|Invalid `--view` value|`1`|
|`--tree` combined with `--flat`|`1`|
|Tree rendering service unavailable when `--tree` is set|`1`|

## See also

- [`workspace`](./workspace.md) — canonical form
- [`workspace track`](./track.md)
- [`workspace area`](./area.md)
