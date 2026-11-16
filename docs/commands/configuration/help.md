---
command: help
group: configuration
summary: Read offline root, group and leaf help matched to the installed executable.
stability: stable
mutates: none
---

# `twig help`

```sh
twig --help
twig proposal --help
twig proposal apply --help
twig help workspace area add
twig --skill operations
```

## Behavior

Offline discovery without workspace, authentication or network.
ConsoleAppFramework supplies declared syntax/defaults; embedded command Markdown
supplies behavior/effects/failures. Some retained declarations/examples still
describe retired options, so the [current catalog](../README.md) and actual
admission boundary take precedence.

**Known limitation:** the current guard refuses `--help-all`, despite its root
help advertisement. Use group/leaf help, not that escape hatch.
`--skill` prints the executable-matched foundation and optional
`operations`, `changes` or `presentation` reference. Bundled older presenter
guidance cannot qualify retired captured UI/preview flags.

Bare `twig` displays help outside an initialized workspace; inside, it can read
the selected item. Use explicit `--help` for discovery without data-command effects.

## Effects

Help/skill printing never initializes data, selects credentials, installs
companions or executes the requested work operation.

## Failure modes

Unknown commands/topics and the current `--help-all` refusal return nonzero.
An option following `--` remains argument data, not a help request.
