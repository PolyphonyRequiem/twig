---
command: ohmyposh init
group: experimental
summary: Emit an Oh My Posh shell hook and text-segment JSON for the current shell.
stability: experimental
mutates: none
---

# `twig ohmyposh init`

`twig ohmyposh init` prints two things to stdout: a shell function that keeps
the `TWIG_PROMPT`, `TWIG_TYPE_COLOR`, and `TWIG_TYPE_TEXT_COLOR` environment
variables refreshed on every prompt, followed by the JSON for an Oh My Posh
`text` segment that renders those variables. Reach for it when integrating a
Twig work-item badge into your Oh My Posh prompt. The command is
read-only — it writes to stdout only, changes no files, and never touches ADO
or the local cache. Users typically pipe its output into their shell profile
or copy the JSON snippet into an Oh My Posh theme.

## Synopsis

```
twig ohmyposh init [--style <style>] [--shell <shell>] [-h|--help] [--version]
```

## Arguments

|Argument|Required|Description|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|`--style <style>`|string|`powerline`|Segment style. Valid: `powerline`, `plain`, `diamond`. Unknown values fall back to `powerline`.|
|`--shell <shell>`|string|`pwsh`|Target shell for the hook function. Valid: `pwsh`, `bash`, `zsh`, `fish`. Unknown values fall back to `pwsh`.|
|`-h`, `--help`|switch|—|Print help and exit.|
|`--version`|switch|—|Print the `twig` version and exit.|

## Behavior

- Registered as its own root command in the CLI at
  [`src/Twig/Program.cs:123`](../../../src/Twig/Program.cs)
  (`app.Add<OhMyPoshCommands>("ohmyposh")`), which routes `init` to
  [`OhMyPoshCommands.Init`](../../../src/Twig/Commands/OhMyPoshCommands.cs).
- The command writes, in order: the shell hook, a blank line, then the
  segment JSON — then returns exit code `0`
  ([`src/Twig/Commands/OhMyPoshCommands.cs:26-35`](../../../src/Twig/Commands/OhMyPoshCommands.cs)).
- Shell selection uses an ordinal-insensitive match. `zsh` shares the `bash`
  hook body. Unknown shells silently emit the PowerShell hook
  ([`src/Twig/Commands/OhMyPoshCommands.cs:37-47`](../../../src/Twig/Commands/OhMyPoshCommands.cs)).
- The emitted segment is an Oh My Posh `text` segment whose `template`,
  `foreground_templates`, and `background_templates` reference the
  `TWIG_PROMPT`, `TWIG_TYPE_TEXT_COLOR`, and `TWIG_TYPE_COLOR` environment
  variables the hook maintains
  ([`src/Twig/Commands/OhMyPoshCommands.cs:12-19`](../../../src/Twig/Commands/OhMyPoshCommands.cs)).
- `--style powerline` adds a Powerline separator glyph; `--style diamond`
  adds leading and trailing diamond glyphs; `--style plain` emits no glyph
  decoration. All three styles include a short cache TTL entry.
- The command performs **no I/O beyond stdout**. It does not open a
  workspace, hit ADO, or touch `.twig/`. Consequently it does not require
  `twig init` to have run.

## Examples

Wire the badge into PowerShell (append to `$PROFILE`):

```console
$ twig ohmyposh init --shell pwsh --style powerline
function Set-TwigPromptEnv {
    # sets $env:TWIG_PROMPT / TWIG_TYPE_COLOR / TWIG_TYPE_TEXT_COLOR
    # from `twig status` output before each prompt render
}

{
  "type": "text",
  "style": "powerline",
  "powerline_symbol": "",
  …
}
```

Emit the bash hook and pipe the segment JSON into a theme file:

```console
$ twig ohmyposh init --shell bash --style diamond > ~/twig-omp.sh
$ sed -n '/^{/,$p' ~/twig-omp.sh > ~/.poshthemes/twig-segment.json
$ head -n -$(wc -l < ~/.poshthemes/twig-segment.json) ~/twig-omp.sh > ~/.bash_twig_hook
$ source ~/.bash_twig_hook
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Any valid or invalid `--style` / `--shell` combination|`0` (unknown values silently fall back)|
|Stdout write fails (closed pipe)|Non-zero, surfaced as an unhandled `IOException` from `Console.WriteLine`|

## See also

- [Oh My Posh integration guide](../../ohmyposh.md) — end-to-end setup with a screenshot.
- [Sample theme](../../examples/twig.omp.json) — a working `omp.json` that embeds the emitted segment.
