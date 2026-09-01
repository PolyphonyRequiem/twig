---
command: config status-fields
group: configuration
summary: Configure which fields appear in the status view.
stability: stable
mutates: local
---

# `twig config status-fields`

Generate a status-fields configuration file for the current process, open it in the user's
editor, and persist the edited result to `.twig/status-fields`. After a successful workspace
save the edited file is also written back to the global profile store so other workspaces on
the same organization + process template share the customization.

## Synopsis

```
twig config status-fields [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|—|—|—|—|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|`human` \| `json` \| `minimal`|`human`|Output format.|

## Behavior

- Loads every cached field definition through `IFieldDefinitionStore.GetAllAsync`; if the
  cache is empty the command exits `1` with `No field definitions cached. Run 'twig sync'
  first.` on stderr (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:62-66`).
- Generates the editor buffer with `StatusFieldsConfig.Generate`, merging any existing
  `.twig/status-fields` content and the workspace's process template
  (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:68-72`).
- Launches `IEditorLauncher.LaunchAsync`. If the editor closes without saving the command
  emits a `statusFieldsCancelled` record and returns `0`
  (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:74-79`).
- On save the edited buffer is written to `TwigPaths.StatusFieldsPath`
  (`.twig/status-fields` — see `src/Twig.Infrastructure/Config/TwigPaths.cs:46`) and parsed
  to count included fields (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:81-84`).
- FR-08 write-back: when both `Organization` and `ProcessTemplate` are set the edited
  content is copied to the global profile store and a `ProfileMetadata` record is refreshed
  with a fresh `FieldDefinitionHash`. FR-09 makes the write-back best-effort — failure never
  changes the exit code (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:92-121`).
- Telemetry: emits a single `CommandExecuted` event with `command=config-status-fields`,
  the exit code, output format, twig version, OS platform, and elapsed duration; no
  identifiers or process names leave the machine
  (`src/Twig/Commands/ConfigStatusFieldsCommand.cs:43-53`).
- Machine formats emit `statusFieldsSaved` and, when write-back succeeds, an additional
  `statusFieldsSavedGlobally` record.

## Examples

Interactive edit, human output:

```
$ twig config status-fields
Saved 12 fields to .twig/status-fields
Also updated global profile for the current organization + process.
```

Non-interactive JSON output after the editor closes with a save:

```
$ twig config status-fields -o json
{"kind":"statusFieldsSaved","count":12,"path":".twig/status-fields"}
{"kind":"statusFieldsSavedGlobally","organization":"contoso","process":"agile"}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Successful edit and save|`0`|
|Editor cancelled without saving|`0` with `statusFieldsCancelled` record|
|Field-definition cache empty|`1` with stderr error suggesting `twig sync`|
|Global profile write-back fails|Success unchanged; global save silently skipped (FR-09)|

## See also

- [`config`](./config.md)
- [`migrate-config`](./migrate-config.md)
- [`help`](./help.md)
