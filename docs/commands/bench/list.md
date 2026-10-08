---
command: bench list
group: bench
summary: List the Benches that exist, marking the current one.
stability: stable
mutates: none
---

# `twig bench list`

Show every Bench in the store, with a marker on the one you are currently
standing on. Reach for it when you have lost track of what you have named, or
before scripting against a Bench you expect to exist.

## Synopsis

```
twig bench list [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|—|—|—|—|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, or `minimal`. Declared by the caller — never sniffed from the terminal.|

## Behavior

Reads through `BenchWorkflow.ListAsync` and renders the returned `BenchListing`.
The current-pointer/default policy is shared with the workspace and pin workflows,
but listing reports stored metadata: it does not evaluate query membership or
rebind the default Bench's self selector. With an existing default, listing needs
no identity-profile or iteration HTTP request and leaves every stored selector
unchanged. Native connection admission and any captured binding/identity checks
still apply; stored metadata is not authority to evaluate a previous user's rules.

On first use, listing initializes the default with a validated canonical identity.
If that identity is missing or identity discovery fails, listing refuses without
creating a default. Workspace, pin and query evaluation still rebind the default's
self rule to the current admitted canonical principal on every evaluation, without
rewriting the stored selectors. No ADO mutation happens.

Human output prints one Bench per line, prefixed with `* ` for the current
Bench and two spaces otherwise, followed by a summary line naming the current
Bench (`src/Twig/Commands/BenchCommand.cs:292-302`).

Machine output (`json`, `json-full`, `json-compact`, `ids`) emits a `benchList`
document with three fields: `count`, `current` (the current Bench's name), and
`entries` — a table of rows keyed as `bench`, each with `name`, `isCurrent`,
`isDefault`, and `selectors` (`src/Twig/Commands/BenchCommand.cs:252-289`).
`isCurrent` is deliberately named apart from the document-level `current`: the
document field carries a name, the row field carries a boolean, so a script
reading either one always finds the type it expected
(`src/Twig/Commands/BenchCommand.cs:267-273`).

Current-Bench detection is case-insensitive, matching how the store compares
Bench names (`src/Twig/Commands/BenchCommand.cs:309-310`).

## Examples

List Benches at a prompt:

```
$ twig bench list
* default
  release blockers
  bugs I own
3 bench(es). Current: default.
```

Consume the listing from a script:

```
$ twig bench list -o json
{
  "kind": "benchList",
  "count": 3,
  "current": "default",
  "entries": [
    { "name": "default",         "isCurrent": "true",  "isDefault": "true",  "selectors": 5 },
    { "name": "release blockers","isCurrent": "false", "isDefault": "false", "selectors": 12 },
    { "name": "bugs I own",      "isCurrent": "false", "isDefault": "false", "selectors": 4 }
  ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Listing rendered (including zero Benches configured beyond the default)|`0`|
|First-use canonical identity missing or identity discovery fails|`1` — no default is created by the failed initialization|
|Native connection admission or captured binding/identity check refuses|`1` — the stale or unadmitted origin is not authorized|

## See also

- [`bench create`](./create.md)
- [`bench switch`](./switch.md)
- [`bench delete`](./delete.md)
