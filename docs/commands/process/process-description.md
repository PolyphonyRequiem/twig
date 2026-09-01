---
command: process description
group: process
summary: Write a byte-stable structural description of the process, for diffing against another.
stability: stable
mutates: none
---

# `twig process description`

Assembles a structured document that describes the workspace's ADO process —
types, states, fields, rules, behaviour membership, form layouts, picklist
constraints, conditional requiredness — into a byte-stable rendering that
ordinary diff tools can compare against another project's document. Every
element of the description is discovered from the live process via
`ProcessDescriptionAssembler`; the command itself resolves the type argument
and the output target and decides nothing about the document
(`src/Twig/Commands/ProcessDescriptionCommand.cs:15-27`).

Byte stability matters: two runs against the same process must produce
identical bytes so the diff is meaningful, and the timestamp used in the
header is injected via `TimeProvider` for tests to hold fixed
(`src/Twig/Commands/ProcessDescriptionCommand.cs:135-138`).

## Synopsis

```
twig process description [<type>] [--out <path>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`type`|no|Work item type **reference name** (e.g. `Niflheim.Grilling`); the display name is accepted too, matching `twig process layout`. Omit to describe every type in the process (`src/Twig/Commands/ProcessDescriptionCommand.cs:99-102`).|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--out`|`string?`|`null`|Write the rendered description to this file instead of stdout. Directory is created if needed. Writes go via a `.tmp-<random>` scratch file and atomically move into place so a mid-render crash never leaves a truncated document at the target path (`src/Twig/Commands/ProcessDescriptionCommand.cs:187-219`).|
|`-o`, `--output`|`string`|`human`|Output format. **Only `json` (and aliases `json-full`, `json-compact`) produces the complete document**; every other format renders an abridged summary and self-declares the abridgement in its banner. `-o ids` is refused explicitly (`src/Twig/Commands/ProcessDescriptionCommand.cs:78-125`).|

## Behavior

The command delegates the document to `ProcessDescriptionAssembler.AssembleAsync`,
which returns one of four outcomes, matched exhaustively so each arm gets its
own remedy (`src/Twig/Commands/ProcessDescriptionCommand.cs:127-177`):

- `ProcessDescriptionAssembled` — the document is projected via
  `ProcessDescriptionDocument.BuildTree` and rendered.
- `ProcessDescriptionTypeNotFound` — the named type does not exist in this
  process. Exit 1, hard error, no partial file: a script that banked "this
  process has nothing" when the truth was "you asked for something that is
  not here" would be worse than a failure
  (`src/Twig/Commands/ProcessDescriptionCommand.cs:148-154`).
- `ProcessIdentityUnresolved` — the workspace's project has no process at
  all. A configuration problem; the message says so, and does not suggest
  retrying (`src/Twig/Commands/ProcessDescriptionCommand.cs:156-162`).
- `ProcessTypesUnfetchable` — transient or auth failure fetching the type
  list. Exit 1; the message specifically suggests retrying or checking
  `twig auth` — the opposite advice from the arm above, which is the whole
  reason these are two arms (`src/Twig/Commands/ProcessDescriptionCommand.cs:164-172`).

The projection lives in the domain layer (`ProcessDescriptionDocument`) so
the MCP surface emits the same bytes as the CLI; the completeness decision
stays here because it is a function of `-o`, which only the CLI has
(`src/Twig/Commands/ProcessDescriptionCommand.cs:240-256`).

`-o ids` is rejected up front: that renderer emits only integer-valued `id`
cells, and a process description carries no numeric ids at all, so it would
produce an empty file with a zero exit code and no notice
(`src/Twig/Commands/ProcessDescriptionCommand.cs:113-125`).

`-o json`, `-o json-full`, and `-o json-compact` all produce the **complete**
document and share a JSON renderer; only truly abridged formats print the
abridged banner (`src/Twig/Commands/ProcessDescriptionCommand.cs:78-94`).

Descriptor version is currently **0.1** (`under design`), and known gaps are
declared positively — `KnownGaps` is emitted even when empty, so a future
non-empty list reads as meaningful rather than as a suddenly-appearing
warning (`src/Twig/Commands/ProcessDescriptionCommand.cs:28-44`).

Unlike `twig process` and `twig process layout`, this command does **not**
take `--org`/`--project`; it operates on the current workspace's process
only.

Read-only: no ADO mutations. Local writes are limited to the optional `--out`
target (and its scratch sibling, which is deleted on the failure paths).

## Examples

### Summarise this project's process to the terminal

```
$ twig process description
process description — abridged summary (descriptor 0.1)
use -o json for the complete document (12 types, 84 fields, 3 behaviours).
```

The abridged banner names `json` explicitly — the same constant the
completeness test asserts against, so the banner cannot ever name a format
that does not produce the complete document
(`src/Twig/Commands/ProcessDescriptionCommand.cs:60-76`).

### Capture the complete document for diffing

```
$ twig process description -o json --out proc-a.json
Wrote process description (12 type(s), descriptor 0.1) to proc-a.json
```

Then repeat on the other project and run any structural diff:

```
$ twig process description -o json --out proc-b.json
$ diff proc-a.json proc-b.json
```

### Describe one type by its reference name

```
$ twig process description Niflheim.Grilling -o json --out grilling.json
```

The reference name is preferred; display names are accepted for symmetry with
`twig process layout` (`src/Twig/Commands/ProcessDescriptionCommand.cs:99-102`).

## Exit codes and failure modes

|Condition|Result|
|---|---|
|`-o ids` requested.|Exit `1`; stderr `'-o ids' cannot render a process description: the document contains no numeric ids. Use '-o json' for the complete document.`|
|Named type does not exist in this process.|Exit `1`; stderr `Work item type '<ref>' does not exist in this process. Run 'twig process' to list types.` No partial file.|
|Workspace project has no ADO process.|Exit `1`; stderr configuration message, no retry hint.|
|Type list route did not answer (transient / auth).|Exit `1`; stderr retry message pointing at `twig auth`.|
|`--out` path cannot be written.|Exit `1`; stderr `Could not write '<path>': <detail>`. Scratch file is cleaned up.|
|Assembler returns a description.|Exit `0`; document printed to stdout or written to `--out` (atomically).|

## See also

- [`twig process`](./process.md)
- [`twig process layout`](./process-layout.md)
- [`twig states`](./states.md)
