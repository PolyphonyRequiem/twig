---
command: bench create
group: bench
summary: Create a Bench with a name you will recognise later.
stability: stable
mutates: local
---

# `twig bench create`

Create a new Bench — a named, durable, saved backlog you can return to later.
Reach for it when the job changes (a release goes hot, a bug lands, a planning
day starts) and you want to put one arrangement of pins and queries down and
pick up a fresh one, without losing the first.

## Synopsis

```
twig bench create <name> [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`name`|yes|Name for the new Bench, e.g. `"release blockers"`. Compared case-insensitively against Benches that already exist.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, or `minimal`. Declared by the caller — never sniffed from the terminal.|

## Behavior

Delegates to the shared `BenchWorkflow.CreateAsync` seam so the CLI and the
agent surface cannot disagree about what creating a Bench means
(`src/Twig/Commands/BenchCommand.cs:41`). The new Bench is empty; no selectors
are copied from the currently active Bench. Creation does not switch to the new
Bench — use `bench switch` for that.

Name resolution:

- A name that collides with an existing Bench (case-insensitive) is refused. The
  error names both the requested name and the stored name, since the two can
  differ only in case (`src/Twig/Commands/BenchCommand.cs:57-61`).
- A name the workflow rejects (empty, whitespace-only, or otherwise invalid per
  `BenchWorkflow`) is reported with the rejection reason and exits `2`
  (`src/Twig/Commands/BenchCommand.cs:63-65`).

Output shape:

- `human` and `minimal` print `Created Bench '<name>'.`
- `json` emits a `benchCreated` record with `name` and `message` fields
  (`src/Twig/Commands/BenchCommand.cs:312-327`).

## Examples

Create a Bench you can return to later:

```
$ twig bench create "release blockers"
Created Bench 'release blockers'.
```

Create one and consume the result from a script:

```
$ twig bench create "bugs I own" -o json
{"kind":"benchCreated","name":"bugs I own","message":"Created Bench 'bugs I own'."}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Bench created|`0`|
|A Bench with that name (any case) already exists|`1` with an error naming both spellings|
|Name rejected by the workflow (empty, whitespace, or otherwise invalid)|`2` with the rejection reason|
|Unrecognised workflow outcome|`1`|

## See also

- [`bench list`](./list.md)
- [`bench switch`](./switch.md)
- [`bench delete`](./delete.md)
