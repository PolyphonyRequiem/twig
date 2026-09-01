---
command: process
group: process
summary: List work item types, or with a type argument show its states, fields, and transitions.
stability: stable
mutates: none
---

# `twig process`

Inspect the work item types this project's process serves. With no argument it
lists every type (with state counts, colors, icon IDs, and category
membership); with a positional type name it shows that type's states, fields,
and transitions. Everything reported is discovered from the process — twig
holds no hard-coded list of types, states, or categories, and the same command
gives the same shape of answer against any process template
(`src/Twig/Commands/ProcessCommand.cs:109-113`).

Both invocation modes share the same argument surface, so this page covers
`twig process` *and* the `twig process <type>` form together. Routing lives in
`src/Twig/Program.cs:617-621`; the switch between list and detail is made in
`ProcessCommand.ExecuteAsync` at `src/Twig/Commands/ProcessCommand.cs:54-57`.

## Synopsis

```
twig process [type] [-o|--output <format>] [--org <org> --project <project>] [--include-hidden]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`type`|no|Work item type name to describe. Omit to list every type in the process. The list mode and detail mode are dispatched by whether this argument is present (`src/Twig/Commands/ProcessCommand.cs:54-57`).|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`string`|`human`|Output format. Accepts `human`, `json`, and `minimal`.|
|`--org`|`string?`|`null`|Azure DevOps organization to describe instead of this workspace's. Requires `--project`. Reads live from ADO (announced on stderr); writes nothing.|
|`--project`|`string?`|`null`|Azure DevOps project to describe instead of this workspace's. Requires `--org`.|
|`--include-hidden`|`bool`|`false`|Include types ADO reserves for its own tooling (Code Review, Feedback, Test Case and friends). Excluded by default because they cannot be created by hand. Ignored in the detail mode: naming a type always describes it.|

## Behavior

### List mode — `twig process`

Reads every `ProcessTypeRecord` from the local cache via
`IProcessTypeStore.GetAllAsync` and renders a table of type name, state count,
child-type count, color, icon ID, hidden flag, and category membership
(`src/Twig/Commands/ProcessCommand.cs:120-143`, `167-228`).

- The cache is populated by `twig sync`. An empty store aborts with exit 1
  and prints `No process types found. Run 'twig sync' to refresh process
  data.` (`src/Twig/Commands/ProcessCommand.cs:125-129`).
- Hidden types are filtered out by default. Membership is read from
  `ProcessTypeRecord.IsHidden`, which itself derives from
  `Microsoft.HiddenCategory` — twig does not carry a name list of hidden
  types, so the filter travels correctly to processes twig has never seen
  (`src/Twig/Commands/ProcessCommand.cs:109-113`, `131-133`).
- A process whose every type is hidden reports an empty list at exit 0 —
  that is the true answer to "which types can I use" and is deliberately not
  an error (`src/Twig/Commands/ProcessCommand.cs:114-118`).
- The empty-store error is raised **before** the hidden filter, so the
  "run twig sync" hint still fires on a cache that has never been populated.

### Detail mode — `twig process <type>`

Looks up the named type with `IProcessTypeStore.GetByNameAsync`, then renders
three tables: states (name, category, color), fields (reference name, display
name, data type, read-only flag), and transitions (from → to, kind) computed
as every ordered pair of distinct states, marked `Cut` when the target state
is in the `Removed` category and `Forward` otherwise
(`src/Twig/Commands/ProcessCommand.cs:145-161`, `230-332`).

- Human output shows only the state list; fields and transitions are emitted
  to the machine surface only, alongside `type`, `isHidden`, and `categories`
  (`src/Twig/Commands/ProcessCommand.cs:234-240`, `300-329`).
- An unknown type name, or a type with no states, exits 1 with
  `No states found for type '<name>'. Run 'twig sync' to refresh process
  data.` (`src/Twig/Commands/ProcessCommand.cs:150-154`).
- `--include-hidden` is ignored: naming a type always describes it, hidden or
  not.

### `--org`/`--project` override

When both are supplied, the invocation is routed through
`ProcessOverrideHost.RunAsync`, which spins up a scoped provider that reads
the target process live from ADO instead of the workspace cache
(`src/Twig/Program.cs:617-621`). No workspace is required, and nothing is
written; a stderr banner announces the live read. Supplying only one of the
two flags is rejected by the host.

Read-only: this command performs no local writes and no ADO mutations.

## Examples

### List every visible type

```
$ twig process
  Bug                 4 states (#CC293D)
  Epic                4 states (#FF7B00)
  Feature             4 states (#773B93)
  Issue               4 states (#B4009E)
  Task                4 states (#F2CB1D)
  User Story          4 states (#009CCC)
```

Machine output (`-o json`) additionally carries `totalTypes`, per-type
`childTypeCount`, `iconId`, `isHidden`, and full `categories` arrays
(`src/Twig/Commands/ProcessCommand.cs:216-225`).

### Describe one type

```
$ twig process Task
  New                 Proposed (#B2B2B2)
  Active              InProgress (#007ACC)
  Resolved            Resolved (#FF9D00)
  Closed              Completed (#339933)
  Removed             Removed (#B2B2B2)
```

Machine output additionally emits the `type`, `isHidden`, `categories`,
`fields`, and `transitions` sections
(`src/Twig/Commands/ProcessCommand.cs:300-329`).

### Describe another project's process live

```
$ twig process --org contoso --project Frontier -o json
```

Reads Frontier's process from ADO without a workspace; the workspace cache is
not consulted or updated (`src/Twig/Program.cs:617-621`).

## Exit codes and failure modes

|Condition|Result|
|---|---|
|List: cache empty (workspace never synced).|Exit `1`; stderr `No process types found. Run 'twig sync' to refresh process data.`|
|List: cache populated but every type hidden and `--include-hidden` not passed.|Exit `0` with an empty list — deliberately not an error.|
|Detail: unknown type name, or type with zero states.|Exit `1`; stderr `No states found for type '<name>'. Run 'twig sync' to refresh process data.`|
|Override: only one of `--org` / `--project` supplied.|Rejected by `ProcessOverrideHost` before the command runs.|
|Otherwise successful invocation.|Exit `0`.|

## See also

- [`twig process layout`](./process-layout.md)
- [`twig process description`](./process-description.md)
- [`twig states`](./states.md)
