---
command: help
group: configuration
summary: Grouped help fast-path — canonical form is `twig --help`.
stability: stable
mutates: none
---

# `twig help`

Fast-path that prints the grouped `twig` help — the same output the top-level `--help` flag
produces — without touching disk, network, or the ConsoleAppFramework command router. The
canonical spelling is `twig --help` (or `twig -h`); `twig help` is the accepted pseudo-command
alias, registered in `GroupedHelp.KnownCommands` alongside every real verb
(`src/Twig/Program.cs:1593-1594`).

## Synopsis

```
twig --help
twig -h
twig help
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
|—|—|—|—|

## Behavior

- Single-arg fast-path: when `args.Length == 1` and the argument is `-h`, `--help`, or the
  literal `help`, the CLI invokes `GroupedHelp.Show()` and returns immediately, before any
  service registration, self-update sweep, or companion first-run check runs
  (`src/Twig/Program.cs:131-135`).
- Zero-arg smart landing: `twig` with no arguments prints the same grouped help when no
  workspace has been initialized, and otherwise routes to `twig show`
  (`src/Twig/Program.cs:136-150`).
- Unknown-command guard: an unrecognized top-level verb is reported with
  `Unknown command: '<arg>'` followed by the grouped help and exit code `1`
  (`src/Twig/Program.cs:154-159`, `GroupedHelp.ShowUnknown` at
  `src/Twig/Program.cs:1652-1657`).
- Grouped output is organized into sections that mirror this documentation set — Getting
  Started, Views, Workspace, Bench, Context, Navigation, Work Items, Seeds, Proposals,
  System, Experimental (`src/Twig/Program.cs:1667-1785`).
- Multi-arg forms (`twig help <topic>`, `twig <command> --help`) are **not** handled by this
  fast-path — they fall through to ConsoleAppFramework's per-command help renderer, with
  additional examples appended by `CommandExamples.ShowIfPresent` after `app.Run`
  (`src/Twig/Program.cs:225-226`).
- No side effects: the fast-path exits above the block that runs `SelfUpdater.CleanupOldBinary`
  and `CompanionStartup.RunFirstRunCheck`, so `--help` never allocates a database, contacts
  ADO, or downloads a companion binary (`src/Twig/Program.cs:210-220`).

## Examples

Print grouped help via the canonical flag:

```
$ twig --help
twig 1.0.0

Usage: twig [command] [-h|--help] [--version]

Getting Started:
  init                 Initialize a new Twig workspace.
  sync                 Flush pending changes then refresh from ADO.
...
```

Use the pseudo-command form; output is byte-identical to `twig --help`:

```
$ twig help
twig 1.0.0

Usage: twig [command] [-h|--help] [--version]
...
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Grouped help printed (`--help`, `-h`, or `help`)|`0`|
|Zero-arg invocation in an uninitialized workspace|`0` with grouped help|
|Unknown top-level command|`1` with `Unknown command:` prefix on stderr|

## See also

- [`config`](./config.md)
- [`config status-fields`](./config-status-fields.md)
- [`migrate-config`](./migrate-config.md)
