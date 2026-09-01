---
command: discard
group: work-items
summary: Drop pending changes for a single work item or all dirty items.
stability: stable
mutates: local
---

# `twig discard`

Erase locally staged changes — pending notes, pending field edits, and any
stale "dirty" flag — for a single work item or every dirty item in the
cache. `discard` never touches ADO; it only clears the local pending-change
store. Seeds are explicitly excluded — use `twig seed discard` for those.

## Synopsis

```
twig discard [<id>] [--all] [--yes] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[0]` | no | Work item ID to discard changes for. Mutually exclusive with `--all`. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--all` | bool | `false` | Discard pending changes across every dirty non-seed item. Mutually exclusive with a positional ID. |
| `--yes` | bool | `false` | Skip the interactive confirmation prompt. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. `json` emits a structured summary of what was discarded. |

## Behavior

Exactly one of a positional ID or `--all` must be given; zero or both is
an exit-`1` usage error (`src/Twig/Commands/DiscardCommand.cs:51-66`).

### Single-item mode

1. Load the item from the cache. Not found → exit `1`.
2. If the item is a seed, exit `1` with "Use 'twig seed discard <id>'
   instead." Seeds are excluded from `discard` by design.
3. Compute the pending-change summary (note count + field-edit count).
4. If there are staged changes, prompt for confirmation unless `--yes` is
   set. Non-`y` responses exit `0` without touching anything.
5. Delegate to `DiscardWorkflow.ExecuteAsync`. Three outcomes:
   - `NoChanges` — nothing to do, info message, exit `0`.
   - `PhantomDirtyCleared` — the "dirty" flag was set with no matching
     staged changes; the flag is cleared, exit `0`.
   - `Discarded` — staged notes and field edits are removed. Warnings from
     the workflow (if any) print to stderr.

### `--all` mode (`src/Twig/Commands/DiscardCommand.cs:141-184`)

1. Enumerate every dirty item in the cache, skipping seeds.
2. If nothing qualifies, clear any phantom dirty flags and exit `0` with
   "No pending changes to discard."
3. Aggregate the count of items, notes, and field edits into a summary
   sentence and prompt for confirmation (unless `--yes`).
4. Clear the whole pending-change store and reset phantom dirty flags in
   one shot. Emits a `WriteJson` payload when `--output json*` is set,
   otherwise a single human success line.

`discard` is the correct verb for backing out a mistaken `note` or `edit`
that offline-staged locally. It has no `--id` selector — use the positional
form to target one item.

Telemetry counts an "item_count" of the items actually discarded (single or
all-mode aggregate).

## Examples

Discard staged changes on a specific item, confirming interactively:

```
$ twig discard 1234
Discard 2 notes, 3 field edits for #1234 'Fix login redirect'? [y/N] y
Discarded 2 notes, 3 field edits for #1234 'Fix login redirect'.
```

Discard everything without a prompt:

```
$ twig discard --all --yes
Discarded all pending changes for 4 items (5 notes, 12 field edits).
```

Nothing to discard:

```
$ twig discard --all
No pending changes to discard.
```

Trying to discard a seed:

```
$ twig discard 987654321
#987654321 is a seed. Use 'twig seed discard 987654321' instead.
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Changes discarded | Exit `0`. |
| Nothing to discard (single or all mode) | Exit `0`, info message. |
| Phantom dirty flag cleared without staged changes | Exit `0`. |
| Confirmation declined | Exit `0`, no mutation. |
| Both positional ID and `--all` given | Exit `1`. |
| Neither positional ID nor `--all` given | Exit `1`. |
| Work item not in cache | Exit `1`. |
| Target is a seed | Exit `1`, redirects to `twig seed discard`. |

## See also

- [`twig note`](note.md) / [`twig edit`](edit.md) — commands that may
  stage locally when ADO is unreachable.
- [`twig sync`](../getting-started/sync.md) — flush staged changes instead of
  discarding them.
- `twig seed discard` — discard local-only seeds.
