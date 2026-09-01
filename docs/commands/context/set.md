---
command: set
group: context
summary: Set the active work item by ID or title pattern.
stability: stable
mutates: local
---

# `twig set`

Selects the "active" work item that other commands — `twig show` (no ID),
`twig web`, mutation commands, prompt integration — operate on. A numeric
ID is looked up in the cache and, on a miss, fetched from ADO; a non‑numeric
argument searches cached titles only.

## Synopsis

```
twig set <idOrPattern> [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `idOrPattern` | yes | Numeric work item ID (e.g. `1234`) or a title substring to match against the local cache. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format for the confirmation line. |

## Behavior

1. Empty or whitespace input exits `2` with a usage error
   (`src/Twig/Commands/SetCommand.cs:47`).
2. **Numeric argument** — `ActiveItemResolver.ResolveByIdAsync` looks in the
   cache and, on a miss, fetches the item from ADO. A `FetchedFromAdo`
   result emits a `Fetching work item <id> from ADO...` hint before the
   confirmation (`src/Twig/Commands/SetCommand.cs:55-70`).
3. **Non‑numeric argument** — `IWorkItemRepository.FindByPatternAsync`
   searches the local cache only. Zero matches exits `1`. A single match
   is used directly. Multiple matches trigger an interactive
   disambiguation prompt on a TTY with a human format; otherwise the list
   is written to stderr and the command exits `1`
   (`src/Twig/Commands/SetCommand.cs:72-113`).
4. On a resolved item the command writes three pieces of local state,
   in order: the active work item ID via `IContextStore`, a visit in
   `INavigationHistoryStore` (enables `nav back`/`nav fore`), and the
   shell prompt state via `IPromptStateWriter`
   (`src/Twig/Commands/SetCommand.cs:115-118`).
5. A one‑line confirmation is rendered in the requested format. `set`
   never loads children, parents, links, or field definitions and never
   runs a working‑set sync.

## Examples

```
$ twig set 1234
Set active item: #1234 Fix login redirect [Doing]
```

```
$ twig set "login redirect" --output json
{"id":1234,"title":"Fix login redirect","state":"Doing","type":"Task"}
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Active context updated | `0` |
| Numeric ID not found in cache and ADO fetch failed | `1` |
| Pattern matched zero cached items | `1` |
| Multiple matches on a non‑TTY or machine format | `1` (matches listed on stderr) |
| Empty or whitespace argument | `2` |

## See also

- [`twig show`](./show.md) — display the item selected here.
- [`twig web`](./web.md) — open the active item in the browser.
- [`../navigation/README.md`](../navigation/README.md) — `nav back` / `nav fore`
  walk the history `set` records.
