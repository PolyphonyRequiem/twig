---
command: process
group: process
summary: List work item types, or with a type argument show its states, fields, and transitions.
stability: stable
mutates: none
---

# `twig process`

List the work-item types in the current process. This list mode is the
starting point for discovering valid type names without assuming an ADO
process template. To inspect one type's states, fields, and transitions, use
the dedicated [`twig process <type>`](process-type.md) reference.

## Synopsis

```
twig process [-o|--output <format>] [--org <org> --project <project>] [--include-hidden] [--refresh]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| — | — | This list form accepts no positional argument. Use [`twig process <type>`](process-type.md) for type details. |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`string`|`human`|Output format. Accepts `human`, `json`, and `minimal`.|
|`--org`|`string?`|`null`|Azure DevOps organization to describe instead of this workspace's. Requires `--project`. Reads live from ADO (announced on stderr); writes nothing.|
|`--project`|`string?`|`null`|Azure DevOps project to describe instead of this workspace's. Requires `--org`.|
|`--include-hidden`|`bool`|`false`|Include types ADO reserves for its own tooling (Code Review, Feedback, Test Case and friends). Excluded by default because they cannot be created by hand. Ignored in the detail mode: naming a type always describes it.|
|`--refresh`|`bool`|`false`|AB#879. Force a targeted metadata-only sync (process types + field definitions) via `IIterationService` before rendering. This is the actionable "rerun me" surface behind the `Metadata not ready` message; it does not pull work items or flush pending writes. Prefer it over `twig refresh` (which is a full work-item pull) when only the process/field catalog is stale.|

## Behavior

### List mode — `twig process`

Reads every `ProcessTypeRecord` from the local cache via
`IProcessTypeStore.GetAllAsync` and renders a table of type name, state count,
child-type count, color, icon ID, hidden flag, and category membership
(`src/Twig/Commands/ProcessCommand.cs:120-143`, `167-228`).

- The cache is populated by `twig sync` or by `twig process --refresh`. When
  the local `IProcessTypeStore` catalog is empty and an `IIterationService`
  is available, `twig process` will attempt one targeted metadata-only
  recovery via `ProcessTypeSyncService.SyncAsync` before failing. If the
  recovery still yields an empty catalog (or is unavailable), the command
  aborts with exit 1 and stderr classifies the failure as either
  `Metadata not ready: process-type catalog is empty. Run 'twig process --refresh' to force a metadata-only sync.`
  or `Metadata refresh failed while resolving process type: <reason>.` — the
  original exception text is propagated so the caller can act on the concrete
  cause (auth, network, etc.) rather than a fabricated hint the code cannot
  justify at this layer. Cancellation preserves `OperationCanceledException`
  unchanged. AB#879.
- Hidden types are filtered out by default. Membership is read from
  `ProcessTypeRecord.IsHidden`, which itself derives from
  `Microsoft.HiddenCategory` — twig does not carry a name list of hidden
  types, so the filter travels correctly to processes twig has never seen
  (`src/Twig/Commands/ProcessCommand.cs:109-113`, `131-133`).
- A process whose every type is hidden reports an empty list at exit 0 —
  that is the true answer to "which types can I use" and is deliberately not
  an error (`src/Twig/Commands/ProcessCommand.cs:114-118`).
- Metadata readiness is checked **before** the hidden filter. Recovery uses
  failure-preserving field/configuration reads: persistent HTTP 401/403,
  transport failures and process-configuration failures produce nonzero exit
  with one structured JSON error when JSON output is requested. They do not
  become empty catalogs or a successful partial process result. Cancellation
  propagates. Existing best-effort enrichment callers retain their tolerant
  adapter reads; recovery bypasses those cached fallback values.


### `--org`/`--project` override

When both are supplied, the invocation is routed through
`ProcessOverrideHost.RunAsync`, which spins up a scoped provider that reads
the target process live from ADO instead of the workspace cache
(`src/Twig/Program.cs:617-621`). No workspace is required, and nothing is
written; a stderr banner announces the live read. Supplying only one of the
two flags is rejected by the host.

Read-only against ADO: recovery may populate local metadata stores, but never
pulls work items or flushes pending writes.

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

### Inspect a discovered type

After listing types, pass the selected name to the dedicated type form:

```
$ twig process Task
```

See [`twig process <type>`](process-type.md) for its state/field/transition
output, override behavior, examples, and failure modes.

### Describe another project's process live

```
$ twig process --org contoso --project Frontier -o json
```

Reads Frontier's process from ADO without a workspace; the workspace cache is
not consulted or updated (`src/Twig/Program.cs:617-621`).

## Exit codes and failure modes

|Condition|Result|
|---|---|
|List: cache empty (never synced) and no `IIterationService` available (`twig` invoked without a workspace connection).|Exit `1`; stderr classifies as `Metadata not ready: process-type catalog is empty. Run 'twig process --refresh' to force a metadata-only sync.` — AB#879.|
|List: cache empty and targeted metadata refresh failed (auth/network).|Exit `1`; stderr `Metadata refresh failed while resolving process type: <original error>.` — the exception text propagates verbatim. AB#879.|
|List: cache populated but every type hidden and `--include-hidden` not passed.|Exit `0` with an empty list — deliberately not an error.|
|Detail: process-type record for `<name>` present but its `States` list is empty (partial catalog).|Exit `1`; stderr `Metadata not ready: process-type record for '<name>' has no states after refresh. Run 'twig process --refresh' to force a metadata-only sync.` — an empty state list is not treated as a valid answer, per AB#879.|
|Detail: type-record resolved with states, but field-definition catalog is empty and cannot be recovered.|Exit `1`; stderr `Metadata not ready: field-definition catalog is empty (required to describe fields on '<name>'). Run 'twig process --refresh' to force a metadata-only sync.` — the previous "fields=[]" answer was a false completeness claim (AB#879).|
|Detail: unknown type name after an authoritative catalog read.|Exit `1`; stderr `Unknown work-item type '<name>' — not present in the refreshed process-type catalog.`|
|Override: only one of `--org` / `--project` supplied.|Rejected by `ProcessOverrideHost` before the command runs.|
|Otherwise successful invocation.|Exit `0`.|

## See also

- [`twig process layout`](./process-layout.md)
- [`twig process <type>`](process-type.md) — inspect one type's states, fields, and transitions.
- [`twig process description`](./process-description.md)
- [`twig states`](./states.md)
