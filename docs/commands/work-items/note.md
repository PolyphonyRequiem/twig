---
command: note
group: work-items
summary: Add a note (ADO comment) to the active work item.
stability: stable
mutates: ado
---

# `twig note`

Add a comment to a work item. Body may come from an inline positional
argument, `--text`, a file, stdin, or an editor buffer opened when no source
is given. The note is pushed to ADO immediately when reachable; when ADO
is unreachable the note is staged in the local pending-change store and
flagged as pending until the next `twig sync`.

## Synopsis

```
twig note ["text"]
          [--text <text> | --file <path> | --stdin]
          [--id <int>] [--format markdown|raw] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[0]` | no | Inline note text as a positional. Quote multi-word text: `twig note "…"`. Equivalent to `--text`. Omitting every body source opens an editor. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--text <text>` | string | none | Note text spelled as an option. |
| `--file <path>` | string | none | Read the note body from a file. Empty file is an error, not a silent editor fall-through. |
| `--stdin` | bool | `false` | Read the note body from piped stdin. Empty stdin is an error. |
| `--id <int>` | int | (active item) | Work item ID to target. |
| `--format <mode>` | `markdown` \| `raw` | `markdown` | Convert the body before sending. `raw` sends pre-rendered HTML or plain text unchanged. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

`TextBodySource.ResolveAsync` is the shared inline/`--file`/`--stdin` source
resolver used by `twig note`, `twig update`, and `twig new --description`.
Two body sources on the same call are rejected before any active-item
lookup so the caller is told about a bad invocation even when no item is
active (`src/Twig/Commands/NoteCommand.cs:58-72`).

An **empty** `--file` or `--stdin` is a hard error, not a silent fall-through
to the editor. That guard is deliberate — a pipeline that dumps an empty
buffer must not open an interactive editor and hang while reporting success
(`src/Twig/Commands/NoteCommand.cs:74-87`).

When no inline text, file, or stdin source is given, an editor buffer is
opened with a `# Note for #<id> <title>` header. Comment lines (starting
with `#`) are stripped before the note is sent
(`src/Twig/Commands/NoteCommand.cs:104-125`). If the editor is cancelled
or the buffer collapses to empty after comment stripping, the note is
cancelled and the command exits `0`.

The default `--format markdown` converts Markdown to ADO's HTML flavor
(via `HtmlFieldFormatter.ResolveComment`). `--format raw` skips conversion
so pre-rendered HTML or plain text passes through unchanged.

Delivery is single-attempt to ADO. On success the workflow returns
`NoteOutcome.Pushed`. When ADO is unreachable the workflow returns
`NoteOutcome.Staged` with `WasOfflineFallback=true`, the note is written to
the local pending-change store, and the success line is suffixed with
`(pending)`. The next `twig sync` flushes staged notes
(`src/Twig/Commands/NoteCommand.cs:127-152`).

## Examples

Inline note on the active item:

```
$ twig note "Investigated root cause: retry loop in AuthClient.RefreshAsync."
Note added to #1234.
```

Read the note body from a Markdown file:

```
$ twig note --file notes/findings.md
Note added to #1234.
```

Pipe from stdin and skip conversion:

```
$ cat findings.html | twig note --stdin --format raw
Note added to #1234.
```

Open the editor:

```
$ twig note
# (editor opens; save/close)
Note added to #1234.
```

Offline fallback:

```
$ twig note "Quick observation"
Note staged locally (ADO unreachable): connection refused
Note added to #1234 (pending).
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Note pushed to ADO | Exit `0`. |
| Note staged locally (ADO unreachable) | Exit `0`, "(pending)" suffix, stderr warning. |
| Editor cancelled or buffer empty after `#` stripping | Exit `0`, info message. |
| Two body sources given, or `--file` path missing | Exit `2`. |
| Empty `--file` or `--stdin` body | Exit `2`. |
| Invalid `--format` value | Exit `2`. |
| Active item not set and no `--id` | Exit `1`. |
| `--id` refers to an item not in the cache | Exit `1`. |

## See also

- [`twig batch --note`](batch.md) — attach a note to a coordinated
  state-plus-fields update.
- [`twig discard`](discard.md) — drop a staged (pending) note without
  pushing it.
- [`twig sync`](../getting-started/sync.md) — flush residual staged notes.
