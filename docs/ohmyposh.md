# Oh My Posh Integration

Twig integrates with [Oh My Posh](https://ohmyposh.dev/) to display your active Azure DevOps work item directly in your shell prompt. The integration uses the **environment variable + `text` segment** pattern — a shell hook runs `twig _prompt` before each prompt render, stores the output in the `TWIG_PROMPT` environment variable, and an Oh My Posh `text` segment reads it via `{{ .Env.TWIG_PROMPT }}`.

> **Note:** This integration does **not** use a `command` segment (Oh My Posh has no `command` segment type). It uses a `text` segment that reads an environment variable populated by a shell hook.

## How It Works

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Shell Hook  │────▶│  TWIG_PROMPT │────▶│  OMP text    │
│ twig _prompt │     │  env var     │     │  segment     │
└──────────────┘     └──────────────┘     └──────────────┘
```

1. Before each prompt render, a shell hook function runs `twig _prompt`.
2. `twig _prompt` reads the active work item from the local SQLite cache (no network calls) and outputs a compact summary.
3. The output is stored in the `TWIG_PROMPT` environment variable.
4. Oh My Posh renders the `text` segment, which reads `{{ .Env.TWIG_PROMPT }}`.

When there is no `.twig/` directory, no active work item, or the database is locked, `twig _prompt` outputs nothing and the segment is hidden.

## Quick Start

### 1. Add the shell hook to your shell profile

Run the init helper to get the exact hook function for your shell:

```bash
twig ohmyposh init --shell <pwsh|bash|zsh|fish>
```

Copy the hook function output and paste it into your shell profile (see [Shell Setup](#shell-setup) below for details).

### 2. Add the Twig segment to your Oh My Posh config

The init helper also outputs the Oh My Posh JSON segment snippet. Copy it into your Oh My Posh theme file (`.json` or `.yaml`) inside a `segments` array of any block.

You can also specify a style:

```bash
twig ohmyposh init --shell pwsh --style powerline   # default
twig ohmyposh init --shell bash --style plain
twig ohmyposh init --shell zsh  --style diamond
```

### 3. Restart your shell

Close and reopen your terminal, or reload your shell profile:

```powershell
# PowerShell
. $PROFILE

# Bash / Zsh
source ~/.bashrc   # or ~/.zshrc

# Fish
source ~/.config/fish/config.fish
```

Your prompt will now show the active Twig work item context, e.g.:

```
◆ #12345 Implement login flow… [Active] •
```

## Shell Setup

### PowerShell

Add the following to your `$PROFILE` (run `notepad $PROFILE` to open it):

```powershell
# Twig Oh My Posh hook — populates TWIG_PROMPT before each prompt render
function Set-TwigPrompt { $env:TWIG_PROMPT = (twig _prompt 2>$null) }
New-Alias -Name 'Set-PoshContext' -Value 'Set-TwigPrompt' -Scope Global -Force
```

Oh My Posh calls `Set-PoshContext` automatically before each prompt render. The alias wires Twig's hook into that mechanism.

### Bash

Add the following to `~/.bashrc`, **after** your Oh My Posh initialization line (`eval "$(oh-my-posh init bash)"`):

```bash
# Twig Oh My Posh hook — populates TWIG_PROMPT before each prompt render
set_poshcontext() {
    export TWIG_PROMPT="$(twig _prompt 2>/dev/null)"
}
```

Oh My Posh calls `set_poshcontext` automatically before each prompt render in bash.

### Zsh

Add the following to `~/.zshrc`, **after** your Oh My Posh initialization line (`eval "$(oh-my-posh init zsh)"`):

```zsh
# Twig Oh My Posh hook — populates TWIG_PROMPT before each prompt render
set_poshcontext() {
    export TWIG_PROMPT="$(twig _prompt 2>/dev/null)"
}
```

Oh My Posh calls `set_poshcontext` automatically before each prompt render in zsh.

### Fish

Add the following to `~/.config/fish/config.fish`, **after** your Oh My Posh initialization line (`oh-my-posh init fish | source`):

```fish
# Twig Oh My Posh hook — populates TWIG_PROMPT before each prompt render
function set_poshcontext
    set -gx TWIG_PROMPT (twig _prompt 2>/dev/null)
end
```

Oh My Posh calls `set_poshcontext` automatically before each prompt render in fish.

## Segment Styles

Oh My Posh supports three visual styles for segments. Choose the one that matches your theme.

### Powerline

```json
{
  "type": "text",
  "style": "powerline",
  "powerline_symbol": "\uE0B0",
  "foreground": "#ffffff",
  "background": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

### Plain

```json
{
  "type": "text",
  "style": "plain",
  "foreground": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

### Diamond

```json
{
  "type": "text",
  "style": "diamond",
  "leading_diamond": "\uE0B6",
  "trailing_diamond": "\uE0B4",
  "foreground": "#ffffff",
  "background": "#0078D4",
  "template": "{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}",
  "cache": {
    "duration": "30s",
    "strategy": "folder"
  }
}
```

> **Tip:** The `cache` property with `"strategy": "folder"` ensures the segment re-evaluates when you change directories, which is useful when switching between repositories with different active work items.

## Example Theme

See [`docs/examples/twig.omp.json`](examples/twig.omp.json) for a complete Oh My Posh theme file with the Twig segment pre-configured alongside common segments (path, git, time).

> **Note:** The example theme's git segment uses [Nerd Font](https://www.nerdfonts.com/) glyphs for branch and status icons. If your terminal font is not a Nerd Font, those icons will render as placeholder rectangles. You can either install a Nerd Font (e.g., "CaskaydiaCove Nerd Font") or remove/replace the Nerd Font glyphs in the git segment template.

## Troubleshooting

### Segment not showing

1. **Verify the hook is in your shell profile.** Open your profile file and confirm the hook function is present (see [Shell Setup](#shell-setup)).
2. **Test `twig _prompt` manually.** Run `twig _prompt` in your terminal. If it produces output, the hook is working. If it produces no output:
   - Ensure you are in a directory with a `.twig/` folder (or a subdirectory of one).
   - Ensure you have an active work item set (`twig set <id>`).
   - Run `twig status` to verify Twig is initialized and has data.
3. **Check the environment variable.** After running the hook manually, check the variable:
   - PowerShell: `$env:TWIG_PROMPT`
   - Bash/Zsh: `echo $TWIG_PROMPT`
   - Fish: `echo $TWIG_PROMPT`
4. **Verify the segment is in your OMP config.** Ensure the `text` segment with `{{ .Env.TWIG_PROMPT }}` is inside a `segments` array in one of your theme's blocks.

### Stale data

The `TWIG_PROMPT` environment variable is updated on **every prompt render** via the shell hook. If you see stale data:

- Ensure the hook function is defined **after** the Oh My Posh init line in your profile (bash/zsh/fish). If defined before, Oh My Posh may overwrite it.
- Run `twig refresh` to pull the latest data from Azure DevOps into the local cache.
- The `cache` property in the segment config (`"duration": "30s"`) tells Oh My Posh to cache the rendered segment for 30 seconds. Reduce or remove the `cache` block if you need real-time updates.

### Performance

`twig _prompt` is designed to execute in **under 50ms**:

- It is a Native AOT compiled binary (no JIT startup cost).
- It reads only from the local SQLite cache (no network calls).
- It executes exactly 2 SQL queries (active context + work item lookup).
- Stderr is suppressed by the hook (`2>$null` / `2>/dev/null`), so errors don't pollute the prompt.

If you experience slow prompts:

- Run `twig _prompt` with timing: `Measure-Command { twig _prompt }` (PowerShell) or `time twig _prompt` (bash/zsh).
- Ensure antivirus software is not scanning `.twig/twig.db` on every access.
- The `cache` property in the segment config reduces how often Oh My Posh re-evaluates the segment, which can help if the hook itself is slow.

## Reference

- `twig _prompt` — Hidden command (not shown in `--help`) that outputs the active work item summary for prompt use.
- `twig ohmyposh init --shell <pwsh|bash|zsh|fish> --style <powerline|plain|diamond>` — Outputs shell hook function and Oh My Posh JSON segment snippet.
- [Oh My Posh — Templates (Environment Variables)](https://ohmyposh.dev/docs/configuration/templates#environment-variables) — Official documentation for the `Set-PoshContext` / `set_poshcontext` hook mechanism.
- [Oh My Posh — Text Segment](https://ohmyposh.dev/docs/segments/system/text) — Official documentation for the `text` segment type.
