---
command: bench switch
group: bench
summary: Stand on another Bench.
stability: stable
mutates: local
---

# `twig bench switch`

Put one Bench down and pick another up. The named Bench becomes the current one,
and every command that consults the current workspace view — `workspace`,
`ws`, and the rendered sprint list — will resolve against it from now on.

Naming a Bench that does not exist fails; nothing is created. Use `bench create`
first if that is what you meant.

## Synopsis

```
twig bench switch <name> [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`name`|yes|Name of an existing Bench. Compared case-insensitively against the stored name.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, or `minimal`. Declared by the caller — never sniffed from the terminal.|

## Behavior

Routes through `BenchWorkflow.SwitchAsync` and reports the outcome from the
shared workflow so the CLI and agent surface cannot disagree
(`src/Twig/Commands/BenchCommand.cs:88`).

On success the current Bench pointer moves and the previous Bench's name is
included in the message so you can see what you left
(`src/Twig/Commands/BenchCommand.cs:92-99`). Selectors on the previous Bench
are untouched — switching does not migrate pins.

On an unknown name the command exits non-zero, states what was asked for, lists
the Benches that do exist, and prints the exact command that would create it
(`src/Twig/Commands/BenchCommand.cs:101-108`). A non-zero exit is deliberate:
a script's pipeline stops rather than proceeding against the wrong list.

Output shape:

- `human` and `minimal` print `Now on Bench '<name>' (was '<previous>').`
- `json` emits a `benchSwitched` record with `name` and `message`
  (`src/Twig/Commands/BenchCommand.cs:312-327`).

## Examples

Stand on another Bench:

```
$ twig bench switch "release blockers"
Now on Bench 'release blockers' (was 'default').
```

Go back to the Bench you started with:

```
$ twig bench switch default
Now on Bench 'default' (was 'release blockers').
```

Attempt to switch to something that has never been created:

```
$ twig bench switch triage
error: There is no Bench named 'triage'. Benches that exist: default, release blockers. Create it with: twig bench create "triage"
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Switched to the named Bench|`0`|
|No Bench with that name exists|`1` with the list of Benches that do exist|
|Name rejected by the workflow (empty, whitespace, or otherwise invalid)|`2` with the rejection reason|
|Unrecognised workflow outcome|`1`|

## See also

- [`bench create`](./create.md)
- [`bench list`](./list.md)
- [`bench delete`](./delete.md)
