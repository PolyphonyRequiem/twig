---
command: process layout
group: process
summary: Show the server-defined form layout — tabs, boxes, and ordered fields — for a work item type.
stability: stable
mutates: none
---

# `twig process layout`

Reads the ADO-served form layout for a work item type and renders it as a tree
of pages → sections → groups → controls, with system controls (state, reason,
assigned-to, area/iteration path, history, links, attachments) emitted as a
sibling branch of the pages. This is a **structure-only** read: no work item
values are fetched or written — the layout endpoint returns field names and
arrangement, never field contents (`src/Twig/Commands/ProcessLayoutCommand.cs:22-26`).

The layout, tabs, groups, and control ordering are discovered dynamically from
the server; twig hard-codes nothing about which pages, sections, or fields any
type carries.

## Synopsis

```
twig process layout <type> [--out <path>] [-o|--output <format>]
                           [--org <org> --project <project>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`type`|yes|Work item type display name (e.g. `Task`) or its process reference name (e.g. `Niflheim.Task`). Both are accepted, matching the sibling `process description` verb (`src/Twig/Commands/ProcessLayoutCommand.cs:63-67`).|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--out`|`string?`|`null`|Write the rendered layout to this file instead of stdout. Directory is created if needed. Confirmation goes to stderr so stdout stays silent for pipelines (`src/Twig/Commands/ProcessLayoutCommand.cs:145-147`).|
|`-o`, `--output`|`string`|`human`|Output format. Accepts `human`, `json`, and `minimal`. The chosen format is what lands in `--out` verbatim (`src/Twig/Commands/ProcessLayoutCommand.cs:28-32`).|
|`--org`|`string?`|`null`|Azure DevOps organization to read the layout from instead of this workspace's. Requires `--project`.|
|`--project`|`string?`|`null`|Azure DevOps project to read the layout from instead of this workspace's. Requires `--org`.|

## Behavior

The command routes through `ProcessOverrideHost.RunAsync` and calls
`IFormLayoutProvider.GetFormLayoutAsync`, which returns one of three results
that are matched exhaustively (`src/Twig/Program.cs:629-634`,
`src/Twig/Commands/ProcessLayoutCommand.cs:83-120`):

- `Served` — the layout is rendered and printed (or written to `--out`).
- `Locked` — the type is locked by the process, so ADO's layout endpoint
  answers 400/VS403115. Exits 1 with a message directing the caller at
  `twig process description`, which reports the same types with
  `unfetched: formLayout`.
- `Unavailable` — the type may not exist in this project, or the process does
  not serve a layout at all. Exits 1 with a message that distinguishes this
  from an empty-layout `Served` (deliberately kept separate so the open
  question of whether stock processes serve layouts stays visible).

The rendered tree carries, for every page: `id`, `label`, `pageType`,
`visible`, and `isContribution`; for every section: `id`; for every group:
`id`, `label`, `visible`, `isContribution`; for every control: `id`, `label`,
`controlType`, `readOnly`, `visible`, `isContribution`
(`src/Twig/Commands/ProcessLayoutCommand.cs:189-231`). Human output collapses
sections' columns into a single top-to-bottom list because a terminal is one
column wide; the machine tree preserves the original nesting.

System controls (state, reason, assigned-to, area path, iteration path,
history, links, attachments) are attached as a sibling of `pages`, not merged
into them, because the server itself returns `systemControls` as a sibling —
merging would invent a placement the server never stated
(`src/Twig/Commands/ProcessLayoutCommand.cs:233-275`).

`--out` writes atomically to the target path (create-and-truncate); on IO,
permission, or `NotSupported` failure the command exits 1 with
`Could not write '<path>': <message>` (`src/Twig/Commands/ProcessLayoutCommand.cs:139-143`).

Read-only: no local writes beyond the optional `--out` file, no ADO mutations.

## Examples

### Print a Bug layout to the terminal

```
$ twig process layout Bug
Details [FormLayoutPage]
  Planning
    Priority                 Microsoft.VSTS.Common.Priority
    Severity                 Microsoft.VSTS.Common.Severity
  Classification
    Area Path                System.AreaPath
    Iteration Path           System.IterationPath
System controls
  State                       Microsoft.VSTS.Fields.State
  Assigned To                 System.AssignedTo
```

### Capture the machine layout for review

```
$ twig process layout Bug -o json --out bug-layout.json
Wrote form layout for 'Bug' to bug-layout.json
```

The banner is on stderr; stdout stays empty so the command composes in
pipelines (`src/Twig/Commands/ProcessLayoutCommand.cs:145-147`).

### Read another project's layout without switching workspaces

```
$ twig process layout Task --org contoso --project Frontier -o json
```

Routed through `ProcessOverrideHost`; the workspace cache is untouched.

## Exit codes and failure modes

|Condition|Result|
|---|---|
|`type` is empty or whitespace.|Exit `1`; stderr `A work item type is required. Try 'twig process' to list types.`|
|Provider returns `Locked` (VS403115).|Exit `1`; stderr message pointing at `twig process description`.|
|Provider returns `Unavailable`.|Exit `1`; stderr message noting the type may not exist or the process does not serve a layout.|
|`--out` path cannot be written (IO / permissions / unsupported).|Exit `1`; stderr `Could not write '<path>': <detail>`.|
|Provider returns `Served`.|Exit `0`; layout printed to stdout or written to `--out`.|

## See also

- [`twig process`](./process.md)
- [`twig process description`](./process-description.md)
- [`twig states`](./states.md)
