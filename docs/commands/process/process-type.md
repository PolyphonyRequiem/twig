---
command: process <type>
group: process
summary: Describe one dynamically discovered work-item type.
stability: stable
mutates: none
---

# `twig process <type>`

Inspect the states, fields, and transitions for one work-item type. Use this form before automating a state change or a type-specific field write: type names, state names, categories, and field metadata are discovered from the selected process rather than assumed by twig.

## Synopsis

```
twig process <type> [-o <format>] [--org <org> --project <project>]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<type>` | yes | Exact work-item type name to describe. A named type is described even when it is hidden from the default list. |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output <format>` | enum | `human` | Render `human`, `json`, or `minimal` output. |
| `--org <org>` | string | current workspace | Read a different Azure DevOps organization; requires `--project`. |
| `--project <project>` | string | current workspace | Read a different Azure DevOps project; requires `--org`. |
| `--include-hidden` | flag | `false` | Accepted for the shared command surface but unnecessary here: explicitly naming a type always describes it. |

## Behavior

Twig looks up the named type in the locally cached process configuration, then renders its states, fields, and transition relationships. Human output shows the state list; JSON also carries the type's hidden/category metadata plus fields and transitions (`src/Twig/Commands/ProcessCommand.cs:145-161,234-240,300-329`). An unknown type, or one without states, exits with a refresh hint rather than guessing a process rule (`src/Twig/Commands/ProcessCommand.cs:150-154`).

Supplying both `--org` and `--project` selects `ProcessOverrideHost`, which reads the target process live from ADO without a workspace and writes nothing (`src/Twig/Program.cs:617-621`). Supplying only one override is rejected. No invocation of this command changes local cache state or Azure DevOps.

## Examples

Describe a type from the current workspace:

```
$ twig process Task
Task
  To do       Proposed
  Doing       InProgress
  Done        Completed
```

The human view is deliberately concise; use JSON when an automation client needs fields and transition data:

```
$ twig process Bug -o json
{
  "type": "Bug",
  "states": [ ... ],
  "fields": [ ... ],
  "transitions": [ ... ]
}
```

Inspect a different project's process without initializing a local workspace there:

```
$ twig process "User Story" --org contoso --project Frontier -o json
```

The command announces the live read on stderr and leaves both the current workspace and the target process unchanged.

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Named type is found and rendered | Exit `0`. |
| Type is unknown or has no states in the local cache | Exit `1`; advise `twig sync` to refresh process data. |
| Only one of `--org` and `--project` is supplied | Exit `2`; override usage error. |
| Live process read fails | Exit `1`; render the ADO or authentication error. |

## See also

- [`twig process`](process.md) — list every visible type.
- [`twig process layout`](process-layout.md) — inspect its server-defined form layout.
- [`twig process description`](process-description.md) — emit the complete byte-stable process descriptor.
