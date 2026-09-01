---
command: tree-set
group: context
summary: Render an arbitrary working set of work items as a forest of annotated trees.
stability: stable
mutates: none
---

# `twig tree-set`

A pure‑render command for consent surfaces: given a set of IDs and an
optional annotation map, it draws them as a forest of trees so a
reviewer can approve a bulk operation against a single tree view. It
does not prompt, mutate, or hit ADO — the caller owns the review loop.

## Synopsis

```
twig tree-set --items <ids> [--annotate <json>] [--depth <n>]
              [--roots-only] [--icons unicode|nerd] [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| — | — | — | — |

`tree-set` accepts input only through flags.

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--items` | string | required | Comma‑separated IDs, `@file` (one ID per line), or `@-` for stdin. |
| `--annotate` | string | none | JSON map of `id` → `{ note, style, icon }`, `@file`, or `@-`. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format. |
| `--depth` | int | `0` | Levels of children to expand below each set member. `0` renders the induced subtree only. |
| `--roots-only` | bool | `false` | Skip connecting ancestors; render only the given items as roots. |
| `--icons` | `unicode` \| `nerd` | configured | Override the glyph mode for this invocation. |

## Behavior

- Missing `--items`, a negative `--depth`, or an unknown `--icons` value
  exit `1` with an error on stderr
  (`src/Twig/Commands/SetTree/WorkingSetTreeCommand.cs:57-80`).
- IDs are parsed by `WorkingSetIdParser`, which reads comma lists,
  `@file`, or `@-` stdin; annotations by `AnnotationMapParser`
  (`src/Twig/Commands/SetTree/WorkingSetTreeCommand.cs:82-99`).
- **Fail‑loud policy.** Because the output is used as a *consent
  surface*, unknown annotation IDs, unknown styles, and unknown icon
  IDs are all errors. The one exception is a cache miss on a requested
  ID, which renders as a placeholder so the rest of the tree remains
  usable and a stderr line lists the missing IDs
  (`src/Twig/Commands/SetTree/WorkingSetTreeCommand.cs:101-113`,
  `134-143`).
- **Cache‑only.** `WorkingSetTreeBuilder` reads from
  `IWorkItemRepository`; there is no ADO fetch. Populate the cache
  first with `twig sync` or `twig show <id>`.
- The requested `--icons` value is applied via a shallow copy of
  `DisplayConfig` so an override does not leak into other in‑process
  commands (`src/Twig/Commands/SetTree/WorkingSetTreeCommand.cs:118-128`).

## Examples

Render three items as a forest, with connecting ancestors:

```
$ twig tree-set --items 101,102,103
▾ #100 Login reliability
  ▸ #101 Fix login redirect          [Doing]
  ▸ #102 SSO redirect loop           [To do]
▾ #200 Avatar caching
  ▸ #103 Reset avatar cache          [Doing]
```

Read IDs from a file and annotate each node from a JSON map:

```
$ cat ids.txt
101
102
103

$ cat notes.json
{ "101": { "note": "verified in prod",   "style": "success" },
  "102": { "note": "needs repro steps",  "style": "warning", "icon": "flag" } }

$ twig tree-set --items @ids.txt --annotate @notes.json
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Tree rendered (with or without cache‑miss placeholders) | `0` |
| `--items` missing, `--depth < 0`, or bad `--icons` | `1` |
| ID list parse failure (bad `@file`, malformed segment) | `1` |
| Annotation map fails to parse or references an ID not in `--items` | `1` |

## See also

- [`twig show --tree`](./show.md) — parent chain + children for a single item.
- [`twig show-batch`](./show-batch.md) — flat list view over the same input.
- [`../plans/README.md`](../plans/README.md) — the typical caller when
  reviewing a bulk mutation.
