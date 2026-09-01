---
command: patch
group: work-items
summary: Atomically patch multiple fields on a work item via JSON input.
stability: stable
mutates: ado
---

# `twig patch`

Set several fields on one work item in a single atomic ADO PATCH, driven by
a JSON object mapping field reference names to values. Reach for this when
you need multi-field writes without a state transition, or when you're
scripting from another tool and already have a JSON payload.

## Synopsis

```
twig patch [<id>]
           (--json '{"Field":"value",...}' | --stdin)
           [--id <int>] [--format markdown|raw] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[0]` | no | Work item ID as a positional (matches `twig show`/`set`/`state` shape). Omit to use `--id` or the active work item. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--json <object>` | string | none | JSON object with `FieldRef → value` pairs. Mutually exclusive with `--stdin`. |
| `--stdin` | bool | `false` | Read the JSON payload from piped stdin. Mutually exclusive with `--json`. |
| `--id <int>` | int | (active item) | Work item ID; equivalent to the positional form. |
| `--format <mode>` | `markdown` \| `raw` | auto | Convert values before sending. `markdown` force-converts all fields; auto converts HTML-typed fields only; `raw` never converts. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

Exactly one of `--json` or `--stdin` must be given; zero or both is a usage
error (`src/Twig/Commands/PatchCommand.cs:95-105`).

Sequence (see `src/Twig/Commands/PatchCommand.cs:82-189`):

1. Read the JSON payload from `--json` or stdin and deserialize into a
   `Dictionary<string,string>` via source-generated `TwigJsonContext`
   (AOT-safe). An empty object is a usage error.
2. Resolve each field's effective value against `IFieldDefinitionStore`.
   Auto mode converts Markdown only when the field type is HTML in ADO;
   plain-text fields pass through unchanged. `--format markdown`
   force-converts every value; `--format raw` never converts. Unknown
   fields emit a one-shot stderr warning per field.
3. Locate the target work item (positional > `--id` > active item).
4. Seeds route entirely through `PatchWorkflow` — no ADO call.
5. Otherwise fetch the item, run the interactive conflict-resolution flow
   (aborted or accepted-remote outcomes exit `0`), and let `PatchWorkflow`
   PATCH the fields atomically. Conflict retry, auto-note push, and cache
   resync are handled by the workflow.

A single PATCH means every field lands or none of them do — this is the
atomicity `twig update` cannot provide when you have several fields.
`twig patch` does not accept `System.State`; use `twig state` or
`twig batch --state` for state transitions.

## Examples

Inline JSON, multiple fields:

```
$ twig patch --json '{"System.Title":"New title","Priority":"1"}'
#1234 New title patched: 2 field(s) updated
```

Piped payload with Markdown conversion for HTML fields:

```
$ cat payload.json | twig patch --stdin --format markdown --id 1234
#1234 Fix login redirect patched: 3 field(s) updated
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| PATCH pushed to ADO | Exit `0`. |
| Seed patched locally | Exit `0`. |
| Conflict-resolution aborted or accepted remote | Exit `0`. |
| No input source, or both `--json` and `--stdin` | Exit `2`. |
| Invalid JSON | Exit `2`. |
| Empty JSON object | Exit `2`. |
| Invalid `--format` value | Exit `2`. |
| Active item not set and no ID given | Exit `1`. |
| Work item not in cache | Exit `1`. |
| Concurrency conflict after retry | Exit `1`. |

## See also

- [`twig update`](update.md) — single-field variant.
- [`twig batch`](batch.md) — atomic PATCH that also carries a state
  transition.
- [`twig edit`](edit.md) — interactive editor equivalent.
