---
command: show-batch
group: context
summary: Display multiple work items by ID from the local cache; missing IDs are disclosed as errors.
stability: stable
mutates: none
---

# `twig show-batch`

Cache-only bulk read: given a comma-separated list of IDs it emits the found
items. Missing numeric IDs are disclosed as errors and return exit `1`; a
cache miss is not proof that the item is absent from ADO.

## Synopsis

```
twig show-batch <ids>
twig show-batch --batch <ids> [--output <format>]
twig show-batch --batch <ids> --include-fields -o json
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `batchArg` | one of `batchArg` or `--batch` | Comma‑separated work item IDs used positionally, e.g. `1234,5678,9012`. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--batch` | string | none | Comma‑separated work item IDs; equivalent to the positional form. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format. |
| `--fields` | string | none | Comma-separated field references; opt into the selected-item JSON contract. |
| `--sections` | string | none | Comma-separated `links`, `children`, `parent`; opt into selected-item JSON. |
| `--include-fields` | flag | false | Export complete stored target fields, core, freshness and non-hierarchy link metadata. Requires `-o json`; incompatible with `--fields`, `--sections` and `--refresh`. |

## Behavior

- Ids are resolved from the positional argument and `--batch`; named
  wins on conflict (`src/Twig/Program.cs:547`,
  `src/Twig/Program.cs:562-566`). Neither supplied exits `1` with a
  usage error on stderr (`src/Twig/Program.cs:553-557`).
- The list is executed as a **cache‑only** read via
  `IWorkItemRepository`; there is no ADO fetch and no `--refresh` flag.
  Missing IDs are listed in a format-aware error on stderr and exit `1`.
  Without projection flags, the successful JSON array on stdout is preserved.
  Non-numeric segments retain their legacy ignored behavior.
- Per‑row `links` and `relations` share the exact wire shape used by
  `twig show` for the single‑item document (see
  `src/Twig/Commands/ShowCommand.cs:684-702`).
- Positional guard is deliberately disabled for this command because
  its argument is a comma‑separated ID list, not free text — see
  `src/Twig/Commands/StrayPositionalGuard.cs:59-66`.

### Target-only stored export

`--include-fields` is an opt-in, versioned cache export. Only the explicitly
requested item bodies are read: no parent, child, link-endpoint or global
work-item enumeration is performed. `parentId` comes from the target core;
`links` contains metadata only, including edges to IDs outside the request.
There is no remote fetch, refresh, selector change or cache mutation.
The validated export also bypasses binary-update cleanup and first-run companion
installation, so an initial invocation cannot download tools or write an install
marker. Other commands retain their existing startup behavior.

The JSON object always has `exportVersion: 1`, `connection` (`org/project`),
`requestedIds` in caller order, `items`, and `missing`. Each item has `id`,
integer `revision` (including `0` for an unsynced seed), `isSeed`, `type`,
`title`, `state`, nullable `assignedTo`, `areaPath`, `iterationPath`, nullable
`parentId`, `tags` (string array), `fields`, `freshness`, and `links`.

`fields` is the **entire stored field dictionary**, not a selected projection
or display rendering. Original reference-name case, nulls, empty strings,
HTML, Unicode and long values are preserved without truncation. Core `state`
is the aggregate display state; a stored `System.State` is exported separately
and is not rewritten to match it. Similarly, the convenience `tags` array does
not replace or normalize the raw stored `System.Tags` field.

`freshness` always contains `hasLocalChanges` (boolean or null), `lastSyncedAt`
and `linksVerifiedAt` (ISO timestamp strings or null). Known dirty target flags
and pending-ID metadata report local changes without loading other bodies.
Unavailable pending metadata remains unknown unless the target is known dirty.
An empty link array with a null verification timestamp is not proof of no edges.

Missing positive IDs and negative seed aliases are all listed in `missing`;
the complete found/missing envelope is still emitted with exit `1`. ID input
must be a non-empty comma-separated list of distinct nonzero integers. Invalid
IDs, unknown options and incompatible output/refresh modes fail with exit `2`
before repository reads. A configured organization and project are required.
Without `--include-fields`, the existing command contract is unchanged.

```json
{
  "exportVersion": 1,
  "connection": "org/project",
  "requestedIds": [-1],
  "items": [{
    "id": -1, "revision": 0, "isSeed": true,
    "type": "Task", "title": "Draft", "state": "Draft",
    "assignedTo": null, "areaPath": "", "iterationPath": "",
    "parentId": 42, "tags": [],
    "fields": {"Custom.Empty": "", "Custom.Null": null, "System.State": "New"},
    "freshness": {"hasLocalChanges": true, "lastSyncedAt": null, "linksVerifiedAt": null},
    "links": []
  }],
  "missing": []
}
```

## Examples

```
$ twig show-batch 1234,5678,9012 --output json
[
  {"id":1234,"title":"Fix login redirect","state":"Doing","type":"Task", ... },
  {"id":5678,"title":"Redirect loop on SSO","state":"Doing","type":"Bug",  ... },
  {"id":9012,"title":"Roll out MFA prompt","state":"To do","type":"Task",  ... }
]
```

```
$ twig show-batch --batch 42
#42  Broken avatar cache  [Doing]
Type: Bug     Assigned: paula@example.com
Pending: 0 field changes, 0 notes
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Every requested numeric ID found | `0` |
| One or more requested numeric IDs absent from cache | `1`; found items retained, missing IDs disclosed |
| No id list supplied on positional or `--batch` | `1` (usage error on stderr) |
| Invalid target-export IDs, options, output mode or connection | `2`; no repository reads |

## See also

- [`twig show`](./show.md) — single‑item detail card, with `--refresh`.
- [`twig tree-set`](./tree-set.md) — same input shape, forest render.
- [`twig sync`](../getting-started/sync.md) — refresh the local cache before
  running a batch read.
