---
command: ws
group: workspace
summary: Short alias for `workspace` — show the current workspace.
stability: stable
mutates: local
---

# `twig ws`

`ws` is the short alias for [`twig workspace`](./workspace.md). It accepts the
same flags and delegates to the same
`WorkspaceCommand.ExecuteAsync`
(`src/Twig/Program.cs:1083-1084`). Reach for it when you want a quick "what am I
looking at right now?" glance from the terminal without the extra keystrokes.

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
|`--all`|`bool`|`false`|Show all team members' items, not just yours.|
|`--no-live`|`bool`|`false`|Disable live-refresh and render a static snapshot.|
|`--refresh`|`bool`|`false`|Sync from ADO before displaying, instead of reading cache only.|
|`--flat`|`bool`|`false`|Use flat (non-tree) output instead of hierarchical rendering.|
|`--tree`|`bool`|`false`|Render full backlog hierarchy tree instead of workspace table.|

## Behavior

Semantically identical to `twig workspace`. Program routing is a direct
delegation:

```
=> services.GetRequiredService<WorkspaceCommand>()
      .ExecuteAsync(output, all, noLive, refresh, ct, flat: flat, tree: tree);
```

(`src/Twig/Program.cs:1082-1084`). See [`workspace`](./workspace.md) for the
authoritative behavior notes on `--tree` / `--flat` mutual exclusion, the
refresh path, live-region rendering, and cache side effects.

## Examples

```
$ twig ws
Sprint (Iteration \Sprint 42):
  ● #4211  Wire retry telemetry             Doing    You
```

```
$ twig ws --all --tree -o json
{"workspace":{"iterations":["…\\Sprint 42"],"tree":[…]}}
```

## Exit codes and failure modes

|Condition|Result|
| --- | --- |
|Success|`0`|
|`--tree` combined with `--flat`|`1`|
|Tree rendering service unavailable when `--tree` is set|`1`|

## See also

- [`workspace`](./workspace.md) — canonical form
- [`workspace track`](./track.md)
- [`workspace area`](./area.md)
