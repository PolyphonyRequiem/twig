---
command: edit
group: work-items
summary: Edit work item fields interactively in an external editor.
stability: stable
mutates: ado
---

# `twig edit`

Open a text buffer of the active work item's fields in `$EDITOR`, let the
user change them, then push the resulting diff to ADO — or, when the target
is a seed or the push fails, stage the changes to the local pending-change
store. When a field is named, only that field is opened; otherwise the
common editable set (Title, State, AssignedTo) is presented.

## Synopsis

```
twig edit [<field>] [--field <field>] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[0]` | no | Field to edit, e.g. `twig edit System.Title`. Equivalent to `--field`. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--field <field>` | string | none | Specific field to edit; omit to edit the common editable set. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

Always operates on the active work item — there is no `--id` selector.

Sequence (see `src/Twig/Commands/EditCommand.cs:41-178`):

1. Resolve the active item. If missing, exits `1` with "No active work item.
   Run 'twig set <id>' first."
2. Generate an editor buffer:
   - With a named field, the buffer is `<Field>: <current value>` with a
     header comment.
   - Otherwise it contains `Title`, `State`, and `AssignedTo` lines. `#`
     comment lines are stripped on parse.
3. Launch the editor via `IEditorLauncher.LaunchAsync`. A cancelled or
   unchanged buffer results in "Edit cancelled" with exit `0`.
4. Parse `Field: value` lines. Short aliases `Title`, `State`, `AssignedTo`
   map to `System.*`. Only fields whose value actually changed are staged
   as `FieldChange` entries. No changes → "No changes detected." exit `0`.
5. If the item is a published (non-seed) work item, fetch remote, run the
   interactive conflict-resolution flow, PATCH ADO with concurrency retry,
   auto-push any staged notes, and resync the cache. On PATCH failure the
   changes are staged locally instead and the outcome switches to
   `editStaged` with a stderr warning.
6. If the item is a seed, the changes are always staged locally through
   the pending-change store.

`twig edit` intentionally has no `--format` toggle: the parser reads plain
key/value lines from the editor buffer without a Markdown-to-HTML pass. For
HTML-typed fields where you need Markdown conversion, use `twig update`
with `--file`/`--stdin` and `--format markdown`.

Named field can be spelled positionally (`twig edit System.Title`) or as
`--field`. The shipped help lists both spellings.

## Examples

Edit the common editable set:

```
$ twig edit
# (editor opens with Title/State/AssignedTo)
Pushed 2 change(s) for #1234.
```

Edit a single field positionally:

```
$ twig edit System.Title
Pushed 1 change(s) for #1234.
```

Cancel the buffer:

```
$ twig edit --field System.Title
Edit cancelled (unchanged or editor aborted).
```

Push failure falls back to local staging:

```
$ twig edit
Changes staged locally (push failed): connection refused
Staged 2 change(s) for #1234.
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Diff pushed to ADO | Exit `0`. |
| Diff staged locally (seed or push failure) | Exit `0`, staged message. |
| Editor cancelled or buffer unchanged | Exit `0`, info message. |
| Parsed diff was empty | Exit `0`, "No changes detected." |
| Conflict-resolution aborted or accepted remote | Exit `0` or `1` per flow. |
| No active work item | Exit `1`. |
| Active item not in cache | Exit `1`. |
| Conflict emitted as JSON | Exit `1`. |

## See also

- [`twig update`](update.md) — single-field non-interactive write with
  Markdown/HTML awareness.
- [`twig patch`](patch.md) — atomic multi-field JSON write.
- [`twig discard`](discard.md) — drop the locally staged changes produced
  by an offline `edit`.
