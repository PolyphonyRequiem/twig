---
command: update
group: work-items
summary: Update a single field on the active work item.
stability: stable
mutates: ado
---

# `twig update`

Set one field on a work item to a new value. The value may be inline, read
from a file, or piped from stdin. `update` fetches the item, resolves any
conflict, PATCHes ADO, auto-pushes staged notes, and refreshes the local
cache in one operation. Use this for a single field; use
[`twig patch`](patch.md) to write several fields atomically.

## Synopsis

```
twig update <field> [<value>]
            [--file <path> | --stdin]
            [--format markdown|raw] [--append]
            [--id <int>] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `<field>` | yes | ADO field reference name or alias (e.g. `System.Title`, `title`). |
| `<value>` | no | New value. Omit when using `--file` or `--stdin`; provide exactly one of the three sources. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--file <path>` | string | none | Read the value from a file. |
| `--stdin` | bool | `false` | Read the value from piped stdin. |
| `--format <mode>` | `markdown` \| `raw` | auto | Convert before sending. Auto = convert only HTML-typed fields. `raw` never converts. `markdown` force-converts. |
| `--append` | bool | `false` | Append to the existing value instead of replacing it. |
| `--id <int>` | int | (active item) | Work item ID to update. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

Sequence (see `src/Twig/Commands/UpdateCommand.cs:48-166`):

1. Reject the call when zero or more than one of `<value>`, `--file`,
   `--stdin` is supplied.
2. Resolve the value via `TextBodySource` (shared with `twig note` and
   `twig new --description`). Trailing newlines are trimmed when
   `--format` is left at auto.
3. Resolve the field's HTML-ness against `IFieldDefinitionStore`. Auto mode
   converts Markdown only when the field is HTML-typed in ADO. `--format
   markdown` force-converts; `--format raw` never does. Unknown fields
   emit a one-shot stderr warning and pass through unchanged.
4. Locate the target work item. Seeds are updated locally through
   `SeedMutationProvider` — no ADO round-trip
   (`src/Twig/Commands/UpdateCommand.cs:113-134`).
5. Fetch remote, run three-way conflict resolution (aborted/accepted-remote
   outcomes exit `0`), then hand off to `FieldUpdateWorkflow` which:
   - Applies `--append` on the remote value if requested (HTML-aware via
     `FieldAppender`).
   - PATCHes ADO with concurrency retry.
   - Auto-pushes any residual pending notes.
   - Resyncs the cache.

`--append` is HTML-aware: for HTML-typed fields it wraps and concatenates
so the result is well-formed markup; for plain-text fields it joins with a
newline.

`--file` and `--stdin` set the display `valueSource` shown in JSON output
to `"file"` or `"stdin"`; the emitted `value` becomes `[from file: <path>]`
or `[from stdin]` rather than the full body — the value itself is on ADO.

## Examples

Replace the title:

```
$ twig update System.Title "Fix login redirect regression"
#1234 Fix login redirect regression updated: System.Title = 'Fix login redirect regression'
```

Push a Markdown description from a file — HTML conversion is automatic
because `System.Description` is HTML-typed:

```
$ twig update System.Description --file docs/repro.md
#1234 Fix login redirect regression updated: System.Description = '[from file: docs/repro.md]'
```

Append a note into a plain-text custom field (no conversion):

```
$ echo "Deferred to next sprint." | twig update Custom.Notes --stdin --append --format raw
#1234 Fix login redirect regression updated: Custom.Notes = '[from stdin]'
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Update pushed to ADO | Exit `0`. |
| Seed field updated locally | Exit `0`. |
| Conflict-resolution flow aborted or accepted remote | Exit `0`. |
| Missing `<field>` | Exit `2`. |
| No value source, or more than one value source | Exit `2`. |
| Invalid `--format` value | Exit `2`. |
| `--file` path missing or ambiguous source | Exit `2`. |
| Active item not set and no `--id` | Exit `1`. |
| `--id` not in cache | Exit `1`. |
| Concurrency conflict after retry | Exit `1`, "Run 'twig sync' and retry." |
| Seed mutation failed | Exit `1`. |

## See also

- [`twig patch`](patch.md) — multiple fields atomically via JSON.
- [`twig batch`](batch.md) — fields together with a state transition.
- [`twig edit`](edit.md) — interactive editor over multiple fields.
