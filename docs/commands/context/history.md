---
command: history
group: context
summary: Show the ADO revision history for a work item; read-only, never cached.
stability: stable
mutates: none
---

# `twig history`

Fetches a work item's revision history from the ADO Work Item Updates
API and renders it chronologically. History is downloaded on demand
every time — it is never cached, staged, or persisted, and the command
never mutates workspace, context, pending, or plan state.

## Synopsis

```
twig history <id> [--detail <ids>|all] [--field <fields>] [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `id` | yes | Work item ID whose revision history to display. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--detail` | string | none | Comma‑delimited update IDs (e.g. `8,11`), or `all`, to render with full field deltas. |
| `--field` | string | none | Comma‑delimited ADO field reference names (e.g. `System.State,System.AssignedTo`) to restrict the deltas to. |
| `-o`, `--output` | `human` \| `json` \| `json-full` \| `json-compact` \| `minimal` \| `ids` | `human` | Output format. Machine formats are routed through `WorkItemHistoryJsonWriter`. |

## Behavior

- **Argument required.** `id <= 0` exits `1` with
  `A work item ID is required: twig history <id>.`
  (`src/Twig/Commands/HistoryCommand.cs:83-87`).
- **Option parsing.** `--detail` and `--field` are parsed together by
  `WorkItemHistoryOptionsParser`; a parse failure exits `1` with the
  formatter's error (`src/Twig/Commands/HistoryCommand.cs:89-94`).
- **ADO fetch.** `IAdoWorkItemService.FetchHistoryAsync` calls the
  Work Item Updates API with the parsed options. There is no cache
  path — every invocation hits ADO
  (`src/Twig/Commands/HistoryCommand.cs:96`).
- **Complete‑or‑error.** Authentication, authorization, not‑found,
  network, and malformed‑response conditions surface as an explicit
  error and never degrade to an empty successful timeline
  (`src/Twig/Commands/HistoryCommand.cs:47-57`).
- **Machine formats** (`json`, `json-full`, `json-compact`, `minimal`,
  `ids`) are emitted through `WorkItemHistoryJsonWriter.Write`, the
  same AOT‑safe writer the `twig_history` MCP tool uses, so both
  surfaces produce an identical document
  (`src/Twig/Commands/HistoryCommand.cs:98-102`, `108-110`).
- **Human format** renders a chronological timeline directly rather
  than via RenderTree — lossless arbitrary‑JSON support there is
  deferred (`src/Twig/Commands/HistoryCommand.cs:19-23`, `104`).

## Examples

```
$ twig history 1234
#1234 Fix login redirect — 6 revisions

  #1  Jane      2026-08-14 09:12  System.State  'To do' → 'Doing'
  #2  Jane      2026-08-14 09:12  System.AssignedTo  (none) → 'Jane Doe'
  #3  Jane      2026-08-19 16:44  System.State  'Doing' → 'Review'
  ...

$ twig history 1234 --detail 3 --field System.State,System.AssignedTo
#3  Jane  2026-08-19 16:44
    System.State       'Doing'  → 'Review'
    System.AssignedTo  'Jane Doe' → 'Ravi Patel'
```

```
$ twig history 1234 --detail all --output json
{"id":1234,"updates":[{"id":1,"revisedBy":"Jane","revisedAt":"...","fields":{...}}, ... ]}
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| History rendered | `0` |
| `id` missing or non‑positive | `1` |
| `--detail` or `--field` failed to parse | `1` |
| ADO fetch threw (auth, network, 404, malformed response) | `1` |

## See also

- [`twig show`](./show.md) — current field values for the same item.
- [`../work-items/README.md`](../work-items/README.md) — the mutation
  commands that produce these revisions.
- [`../plans/README.md`](../plans/README.md) — the plan / proposal
  path each staged edit flows through.
