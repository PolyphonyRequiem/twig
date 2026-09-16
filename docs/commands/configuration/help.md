---
command: help
group: configuration
summary: Task-oriented help with targeted group and leaf discovery.
stability: stable
mutates: none
---

# `twig help`

Help is available without a checkout, workspace, authentication or network.
Start with the short task-oriented root, select a group, then a leaf command.
The executable embeds the canonical command reference's behavior sections;
ConsoleAppFramework generates syntax, options and defaults from declarations.

## Synopsis

```text
twig --help
twig <group> --help
twig <command> --help
twig help <command>
twig --help-all
twig --skill
```

## Behavior

- `--help`, `-h` and `help` show the same short root. Bare `twig` does so only
  outside an initialized workspace; inside a workspace bare `twig` still runs `show`.
- Group help lists only that group's descendants. Groups with a bare operation,
  such as `process`, retain its options before the focused child list.
- Leaf help preserves generated usage, options and defaults, registered examples,
  and the bundled reference's behavior, conditional effects and failure sections.
  Hidden compatibility aliases remain reachable through targeted help.
- `--help-all` is the explicit complete generated catalog, including registered
  aliases. Unknown commands fail with a short discovery hint rather than dumping it.
- `--skill` prints the executable-matched generic `twig-cli` entry and package
  identity. See [skill delivery](../../features/skills.md) for installation of
  the generic family and separately supplied companions.
- Help exits before workspace services, native database initialization, self-update
  cleanup and companion downloads. Supplying work-item arguments with `--help`
  does not execute that operation. A help token following `--` remains argument data.

## Examples

```sh
twig proposal --help                 # choose a proposal operation
twig proposal apply --help           # exact syntax, gates, effects and errors
twig help workspace area add         # equivalent targeted-help spelling
twig --help-all                      # complete discovery escape hatch
twig --skill                         # offline entry guidance from this build
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Known root, group or leaf help|`0`|
|Unknown command or help topic|`1`, compact diagnostic on stderr|
|Missing or invalid arguments to an actual operation|That command's normal failure contract; help does not execute it|

## See also

- [Skill delivery](../../features/skills.md)
- [Command reference](../README.md)
