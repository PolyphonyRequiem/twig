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
twig process <type> [-o <format>] [--org <org> --project <project>] [--refresh]
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
| `--refresh` | flag | `false` | AB#879. Force a targeted metadata-only sync (process types + field definitions) before rendering. Actionable rerun for the `Metadata not ready` message. Does not pull work items or flush pending writes. |

## Behavior

Twig looks up the named type in the locally cached process configuration, then renders its states, fields, and transition relationships. Human output shows the state list; JSON also carries the type's hidden/category metadata plus fields and transitions (`src/Twig/Commands/ProcessCommand.cs:162-242,247-320`). **AB#879**: when the type is absent from the local `IProcessTypeStore`, when its `States` list is empty, or when the required `IFieldDefinitionStore` catalog is empty, `twig process <type>` attempts one targeted metadata-only recovery via `ProcessTypeSyncService`/`FieldDefinitionSyncService` before deciding whether the request is a genuine unknown or a not-ready cache. A refreshed catalog that still lacks the type surfaces `Unknown work-item type '<name>'`; a refreshed record with no states, or an empty field-def catalog, surfaces `Metadata not ready: … Run 'twig process --refresh' to force a metadata-only sync.`; a failed recovery propagates the original exception text as `Metadata refresh failed while resolving process type: <original error>.` — no fabricated "check your auth" hint the code cannot justify at this layer. Cancellation preserves `OperationCanceledException` unchanged.

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
| Named type resolved after a targeted metadata-only recovery | Exit `0`. |
| Type absent from an authoritative refreshed catalog | Exit `1`; stderr `Unknown work-item type '<name>' — not present in the refreshed process-type catalog.` |
| Type present but its `States` list is empty (partial catalog) | Exit `1`; stderr `Metadata not ready: process-type record for '<name>' has no states after refresh. Run 'twig process --refresh' to force a metadata-only sync.` — AB#879. |
| Type resolved with states, but field-def catalog is empty and cannot be recovered | Exit `1`; stderr `Metadata not ready: field-definition catalog is empty (required to describe fields on '<name>'). Run 'twig process --refresh' to force a metadata-only sync.` — AB#879. |
| Recovery call threw (auth/network) | Exit `1`; stderr `Metadata refresh failed while resolving process type: <original error>.` — verbatim exception text. AB#879. |
| Only one of `--org` and `--project` is supplied | Exit `2`; override usage error. |
| Live process read fails | Exit `1`; render the ADO or authentication error. |

## See also

- [`twig process`](process.md) — list every visible type.
- [`twig process layout`](process-layout.md) — inspect its server-defined form layout.
- [`twig process description`](process-description.md) — emit the complete byte-stable process descriptor.
