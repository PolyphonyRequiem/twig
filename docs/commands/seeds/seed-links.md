---
command: seed links
group: seeds
summary: List virtual links, optionally filtered by item ID.
stability: stable
mutates: none
---

# `twig seed links`

Reads the workspace `seed_links` table and prints its rows. Pass an ID to filter to
links touching that item; omit it to list every link in the workspace. Read-only.

## Synopsis

```
twig seed links [<id>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`id`|no|Item ID (seed or published) to filter by. Omit to list all links.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- With an ID, calls `ISeedLinkRepository.GetLinksForItemAsync` returning every row where the item appears as source **or** target. Without an ID, calls `GetAllSeedLinksAsync` (`src/Twig/Commands/SeedLinkCommand.cs:222-225`).
- Machine formats (`json`, `json-full`, `json-compact`, `ids`) emit a `seedLinks` document with a Table containing `sourceId`, `targetId`, `linkType`, and `createdAt` (ISO 8601 `o` format) columns, plus a scalar `count` (`src/Twig/Commands/SeedLinkCommand.cs:229-258`).
- Human/minimal formats stream one line per link (`#<src> ──<type>──▶ #<tgt>`) followed by a total count. The empty result set prints a distinct `No links for #<id>.` or `No seed links.` info line (`src/Twig/Commands/SeedLinkCommand.cs:261-274`).

## Examples

List all links:

```
$ twig seed links
#-42 ──parent-child──▶ #5678
#-42 ──blocked-by──▶ #-43
#-43 ──successor──▶ #-44
3 link(s) total.
```

Filter to one seed as JSON:

```
$ twig seed links -42 -o json
{"kind":"seedLinks","links":[{"sourceId":-42,"targetId":5678,"linkType":"parent-child", ...}],"count":2}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Query completed (including the empty-result case).|`0`|

## See also

- [`seed link`](./seed-link.md) — create a new link.
- [`seed unlink`](./seed-unlink.md) — remove a specific link.
- [`seed view`](./seed-view.md) — see links alongside the full seed dashboard.
