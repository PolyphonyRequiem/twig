---
command: tui
group: experimental
summary: Launch the full-screen interactive TUI (requires twig-tui companion binary).
stability: experimental
mutates: local
---

# `twig tui`

`twig tui` hands control to the `twig-tui` companion binary, a full-screen
Terminal.Gui application that renders the workspace tree in a left pane and the
selected work item's form in a right pane. Reach for it when you want to
browse a workspace and stage field edits interactively without leaving the
terminal. Edits made in the form are captured as pending changes in the local
SQLite cache — the TUI never PATCHes ADO directly. Flush them with
[`twig sync`](../getting-started/sync.md) when you are done.

## Synopsis

```
twig tui [-h|--help] [--version]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|`-h`, `--help`|switch|—|Print help and exit.|
|`--version`|switch|—|Print the `twig` version and exit.|

## Behavior

- The CLI delegates to `BinaryLauncher.Launch("twig-tui", "Twig.Tui")` at
  [`src/Twig/Program.cs:1338`](../../../src/Twig/Program.cs). No flags are
  forwarded; the companion inherits stdin, stdout, and stderr, and its exit
  code becomes the calling shell's exit code.
- The companion binary is resolved in this order: adjacent to the running
  `twig` executable (via `AppContext.BaseDirectory`, then `Environment.ProcessPath`),
  then each entry of `PATH`
  ([`src/Twig/Program.cs:1848-1887`](../../../src/Twig/Program.cs)).
- Once launched, `twig-tui` discovers the workspace (`.twig/` or `twig.json`)
  from the current working directory, loads split configuration, and boots a
  full-screen `Terminal.Gui` `Application` with a menu bar, `TreeNavigatorView`
  in the left pane, and `WorkItemFormView` in the right pane
  ([`src/Twig.Tui/Program.cs:14-154`](../../../src/Twig.Tui/Program.cs)).
- Form edits are captured through `PendingChangeStoreSink` and written to the
  local pending change store — the same queue surfaced by
  [`twig pending`](../plans/pending.md). No ADO REST call is issued by the TUI
  itself.
- The window title reflects the active organization and project.
  `Esc` quits the application; the process exits with `0` on clean shutdown.

## Examples

Launch the TUI from a workspace root:

```console
$ twig tui
┌ Twig TUI — contoso/Twig (Esc to quit) ──────────────────────────────────┐
│ File   View                                                             │
│┌─ Tree ──────────────────────┐┌─ Work Item ──────────────────────────┐  │
││ ▸ Epic  #4712 Onboarding    ││ Title:  Add device-code login        │  │
││   ▸ Feature #4718 Sign-in   ││ State:  Doing                        │  │
│└─────────────────────────────┘└──────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

Discover a missing companion binary:

```console
$ twig tui
error: 'twig-tui' not found. Ensure the Twig.Tui project is built and on PATH or in the same directory as 'twig'.
$ echo $?
1
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|TUI ran and the user quit normally|`0`|
|`twig-tui` binary not found on PATH or adjacent to `twig`|`1`, stderr message names the missing binary|
|Process failed to start (spawn error)|`1`, stderr `error: Failed to start 'twig-tui'.`|
|Companion process launched but threw before init|`1`, stderr `error: Failed to launch 'twig-tui': <message>`|
|Companion process exited with a non-zero code|That exit code (propagated verbatim)|

## See also

- [`twig mcp`](./mcp.md) — the sibling companion-binary launcher.
- [`twig nav`](../navigation/nav.md) — the non-TUI interactive tree navigator.
- [`twig pending`](../plans/pending.md) — inspect the pending changes the TUI produces.
- [`twig sync`](../getting-started/sync.md) — flush pending changes to ADO.
