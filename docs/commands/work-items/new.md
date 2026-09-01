---
command: new
group: work-items
summary: Create a new work item in ADO.
stability: stable
mutates: ado
---

# `twig new`

Create a new work item and publish it to Azure DevOps immediately. Type,
title, area/iteration, description, parent link, and arbitrary
`FieldRef=value` pairs may be supplied on the command line, from a file,
from stdin, or through an editor buffer. Unlike `twig seed new`, `twig new`
does not stage — the item exists in ADO by the time the command returns.

## Synopsis

```
twig new [<type>] [<title>]
         [--title <title>] [--type <type>]
         [--area <path>] [--iteration <path>]
         [--description <text> | --description-file <path> | --description-stdin]
         [--parent <int>] [--field <key=value>]...
         [--format markdown|raw] [--editor] [--set]
         [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[0]` | no | Work item type (e.g. `task`, `bug`). Equivalent to `--type`. |
| `[1]` | no | Title as the second positional. Quote multi-word titles. Equivalent to `--title`. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--title <title>` | string | none | Title for the new item. |
| `--type <type>` | string | none | Work item type; required if not given positionally. Type inference from `--parent` is not implemented. |
| `--area <path>` | string | (parent > config) | Area path for the new item. |
| `--iteration <path>` | string | (parent > config) | Iteration path for the new item. |
| `--description <text>` | string | none | Description body. |
| `--description-file <path>` | string | none | Read the description from a file. |
| `--description-stdin` | bool | `false` | Read the description from piped stdin. |
| `--parent <int>` | int | none | Positive parent work item ID to link under. |
| `--field <key=value>` | repeatable | none | Extra field at creation time (`FieldRef=value`). Repeatable. Required for types with mandatory custom fields. |
| `--format <mode>` | `markdown` \| `raw` | auto | Convert `--description` before sending. `markdown` force-converts; `raw` never converts. Does **not** apply to `--field` values. |
| `--editor` | bool | `false` | Open an editor to fill in fields. When set, the description passed via `--description` stays raw until the editor step. |
| `--set` | bool | `false` | Activate the new item as the twig context after creation and extend the working set around it. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

Sequence (see `src/Twig/Commands/NewCommand.cs:38-338`):

1. Validate inputs before any network call. `--field` pairs are parsed via
   `FieldAssignment` and checked against the cached field-definition store
   so an unknown reference name is a local error, not a silent drop by
   ADO (`src/Twig/Commands/NewCommand.cs:100-122`).
2. Validate `--format` and resolve the description body via
   `TextBodySource` (shared with `twig note`/`twig update`), same
   "empty source is an error" rule as those commands.
3. Require an explicit `--type`. Parent inference is not supported.
4. Resolve area/iteration paths. When `--parent` is given, the parent is
   fetched (cache first, ADO second) and its `System.AreaPath` /
   `System.IterationPath` are used as defaults, ahead of `config.Defaults`.
   A flaky parent fetch degrades gracefully to the configured default with
   a single stderr warning.
5. Build an in-memory seed via `SeedFactory`, apply the description
   (auto-converted only when `System.Description` is HTML-typed, unless
   `--editor` is set and `--format` is auto, in which case the description
   stays raw until the editor step).
6. Apply `--field` values. **Field values are resolved by field type,
   deliberately not by the global `--format`** — the same call that
   converts `--description` to HTML would otherwise wrap picklist values
   like `AFK` in `<p>…</p>` and cause silent data loss on the picklist.
   `--field System.Description=…` overrides `--description`
   (`src/Twig/Commands/NewCommand.cs:219-244`).
7. If `--editor` is set, generate the seed editor buffer, launch the
   editor, and parse the edited fields.
8. Enforce the sprint-entry policy: only the reference profile's sprint-
   tier type may be committed directly to a sprint iteration
   (`src/Twig/Commands/NewCommand.cs:275-282`).
9. Call `IAdoWorkItemService.CreateAsync`. On failure, exit `1`. On
   success, fetch back the created item and save it to the local cache.
   If the fetch-back fails after create, exit `1` with instructions to
   run `twig sync`.
10. If `--set` was given, activate the new item as context and
    fire-and-forget an `ExtendWorkingSetAsync` to extend the working set
    around it.

## Examples

Create a Task under the active item's context, positionally:

```
$ twig new task "Write tests"
Created #4567 Task 'Write tests'
https://dev.azure.com/contoso/AppTeam/_workitems/edit/4567
```

Full form with description, parent, and a required custom field:

```
$ twig new --type Bug --title "Login flakiness" \
           --parent 1234 \
           --description-file docs/repro.md \
           --field Custom.Severity=High \
           --set
Created #4568 Bug 'Login flakiness'
```

Open an editor for a Feature and store the description via `--field` for
a Markdown-aware editor buffer:

```
$ twig new feature "Passwordless login" --parent 900 --editor --set
# (editor opens; save/close)
Created #4569 Feature 'Passwordless login'
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Item created and cached | Exit `0`. |
| Positional title missing and `--editor` not set | Exit `2`. |
| Invalid `--format` value | Exit `2`. |
| Ambiguous or missing description source | Exit `2`. |
| Malformed `--field` pair | Exit `2`. |
| Unknown `--field` reference name | Exit `1`. |
| Missing `--type` (or unsupported inference from `--parent`) | Exit `1`. |
| Invalid work item type | Exit `1`. |
| Non-positive `--parent` | Exit `1`. |
| Area/iteration resolution failed | Exit `1`. |
| Sprint-entry policy denied direct creation into a sprint iteration | Exit `1`. |
| ADO create failed | Exit `1`. |
| ADO created but fetch-back failed | Exit `1`, "run 'twig sync' to recover." |
| Editor cancelled | Exit `0`, cancelled message. |

## See also

- [`twig batch`](batch.md) — atomic updates to an existing item.
- [`twig edit`](edit.md) — interactive editor over existing fields.
- [`twig delete`](delete.md) — remove a work item created in error
  (irreversible; prefer `twig state Closed`).
