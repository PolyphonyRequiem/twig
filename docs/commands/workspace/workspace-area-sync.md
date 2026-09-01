---
command: workspace area sync
group: workspace
summary: Fetch team area paths from ADO and replace configuration.
stability: stable
mutates: local
---

# `twig workspace area sync`

Fetch the team's area paths from Azure DevOps and rebuild the workspace area
filter to match. Reach for it after your team is re-org'd, when you first
onboard onto a project, or whenever `workspace area list` no longer matches
what your team owns in ADO.

## Synopsis

```
twig workspace area sync [flags]
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

Delegates to `AreaCommand.SyncAsync`
(`src/Twig/Commands/AreaCommand.cs:256-291`):

1. When no `IIterationService` is wired (offline harness), fail with
   `Cannot sync area paths: not connected to Azure DevOps.` on stderr and
   exit `1` (`src/Twig/Commands/AreaCommand.cs:260-264`).
2. Call `IIterationService.GetTeamAreaPathsAsync(ct)`. On exception, print
   `Failed to fetch team area paths: <ex.Message>` on stderr and exit `1`
   (`src/Twig/Commands/AreaCommand.cs:267-275`).
3. When the team has zero configured area paths, print
   `No team area paths found in ADO.` on stderr and exit `1`
   (`src/Twig/Commands/AreaCommand.cs:277-281`).
4. On success, **replace** `config.Defaults.AreaPathEntries` with the fetched
   list — the previous entries are discarded, not merged — persisting via
   `config.SaveSplitAsync(paths, ct)`
   (`src/Twig/Commands/AreaCommand.cs:283-287`).
5. Emit the sync summary with the number of paths written.

This is the only command in the `workspace` group that reads from ADO. It
never pushes work-item mutations.

## Examples

```
$ twig workspace area sync
Synced 3 team area path(s) from ADO.
\Contoso\Team A  (under)
\Contoso\Team A\Reliability  (under)
\Contoso\Team B  (under)
```

```
$ twig workspace area sync -o json
{"areaPathSync":{"syncedCount":3,"entries":[…]}}
```

## Exit codes and failure modes

|Condition|Result|
|Success|`0`|
|Not connected to ADO|`1` with error on stderr|
|ADO fetch throws|`1` with `Failed to fetch team area paths: …` on stderr|
|Team has no area paths in ADO|`1` with `No team area paths found in ADO.` on stderr|

## See also

- [`workspace area list`](./workspace-area-list.md)
- [`workspace area add`](./workspace-area-add.md)
- [`workspace area remove`](./workspace-area-remove.md)
- [`workspace area`](./workspace-area.md)
