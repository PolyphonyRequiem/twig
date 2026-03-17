# Oh My Posh Integration

Twig integrates with [Oh My Posh](https://ohmyposh.dev/) to display your active Azure DevOps work item directly in your shell prompt. The integration uses a **pre-computed state file** (`.twig/prompt.json`) — every twig command that modifies prompt-visible state writes this file as a side effect. A shell hook reads the file (~1ms) before each prompt render, sets environment variables, and an Oh My Posh `text` segment reads them via `{{ .Env.TWIG_PROMPT }}`.

> **Note:** This integration does **not** use a `command` segment or spawn a subprocess. It uses a `text` segment that reads environment variables populated by a shell hook that reads `.twig/prompt.json`.

## How It Works

```
┌───────────────────┐     ┌──────────────┐     ┌──────────────┐
│  Shell Hook       │────▶│  TWIG_PROMPT │────▶│  OMP text    │
│  reads prompt.json│     │  env vars    │     │  segment     │
└───────────────────┘     └──────────────┘     └──────────────┘
```

1. Every twig command that modifies prompt-visible state (e.g., `twig set`, `twig state`, `twig flow-start`) writes `.twig/prompt.json`.
2. Before each prompt render, a shell hook function reads `.twig/prompt.json` (~1ms, no subprocess).
3. The hook sets `TWIG_PROMPT`, `TWIG_TYPE_COLOR`, and `TWIG_STATE_CATEGORY` environment variables.
4. Oh My Posh renders the `text` segment, which reads `{{ .Env.TWIG_PROMPT }}`.

When there is no `.twig/prompt.json` file (no active work item, or twig not initialized), the hook sets empty variables and the segment is hidden.

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
# Twig Oh My Posh hook — reads .twig/prompt.json before each prompt render
function Set-TwigPrompt {
    $f = Join-Path $PWD ".twig" "prompt.json"
    if (Test-Path $f) {
        $p = Get-Content $f -Raw | ConvertFrom-Json
        $env:TWIG_PROMPT = $p.text
        $env:TWIG_TYPE_COLOR = $p.typeColor
        $env:TWIG_STATE_CATEGORY = $p.stateCategory
    } else {
        $env:TWIG_PROMPT = ""
        $env:TWIG_TYPE_COLOR = ""
        $env:TWIG_STATE_CATEGORY = ""
    }
}
New-Alias -Name 'Set-PoshContext' -Value 'Set-TwigPrompt' -Scope Global -Force
```

Oh My Posh calls `Set-PoshContext` automatically before each prompt render. The alias wires Twig's hook into that mechanism.

### Bash

Add the following to `~/.bashrc`, **after** your Oh My Posh initialization line (`eval "$(oh-my-posh init bash)"`):

```bash
# Twig Oh My Posh hook — reads .twig/prompt.json before each prompt render
set_poshcontext() {
    local f="$PWD/.twig/prompt.json"
    if [ -f "$f" ]; then
        export TWIG_PROMPT=$(jq -r '.text // empty' "$f" 2>/dev/null)
        export TWIG_TYPE_COLOR=$(jq -r '.typeColor // empty' "$f" 2>/dev/null)
        export TWIG_STATE_CATEGORY=$(jq -r '.stateCategory // empty' "$f" 2>/dev/null)
    else
        export TWIG_PROMPT=""
        export TWIG_TYPE_COLOR=""
        export TWIG_STATE_CATEGORY=""
    fi
}
```

Oh My Posh calls `set_poshcontext` automatically before each prompt render in bash.

> **Note:** The bash/zsh hooks require [`jq`](https://jqlang.github.io/jq/) to parse `prompt.json`. Install it via your package manager (e.g., `apt install jq`, `brew install jq`).

### Zsh

Add the following to `~/.zshrc`, **after** your Oh My Posh initialization line (`eval "$(oh-my-posh init zsh)"`):

```zsh
# Twig Oh My Posh hook — reads .twig/prompt.json before each prompt render
set_poshcontext() {
    local f="$PWD/.twig/prompt.json"
    if [ -f "$f" ]; then
        export TWIG_PROMPT=$(jq -r '.text // empty' "$f" 2>/dev/null)
        export TWIG_TYPE_COLOR=$(jq -r '.typeColor // empty' "$f" 2>/dev/null)
        export TWIG_STATE_CATEGORY=$(jq -r '.stateCategory // empty' "$f" 2>/dev/null)
    else
        export TWIG_PROMPT=""
        export TWIG_TYPE_COLOR=""
        export TWIG_STATE_CATEGORY=""
    fi
}
```

Oh My Posh calls `set_poshcontext` automatically before each prompt render in zsh.

### Fish

Add the following to `~/.config/fish/config.fish`, **after** your Oh My Posh initialization line (`oh-my-posh init fish | source`):

```fish
# Twig Oh My Posh hook — reads .twig/prompt.json before each prompt render
function set_poshcontext
    set -l f "$PWD/.twig/prompt.json"
    if test -f "$f"
        set -gx TWIG_PROMPT (jq -r '.text // empty' "$f" 2>/dev/null)
        set -gx TWIG_TYPE_COLOR (jq -r '.typeColor // empty' "$f" 2>/dev/null)
        set -gx TWIG_STATE_CATEGORY (jq -r '.stateCategory // empty' "$f" 2>/dev/null)
    else
        set -gx TWIG_PROMPT ""
        set -gx TWIG_TYPE_COLOR ""
        set -gx TWIG_STATE_CATEGORY ""
    end
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
2. **Check that `.twig/prompt.json` exists.** Run any mutating twig command (e.g., `twig refresh` or `twig set <id>`) to regenerate the file. If no work item is active, the file will contain `{}` and the segment will be hidden.
   - Ensure you are in a directory with a `.twig/` folder (or a subdirectory of one).
   - Ensure you have an active work item set (`twig set <id>`).
3. **Check the environment variable.** After running the hook manually, check the variable:
   - PowerShell: `$env:TWIG_PROMPT`
   - Bash/Zsh: `echo $TWIG_PROMPT`
   - Fish: `echo $TWIG_PROMPT`
4. **Verify the segment is in your OMP config.** Ensure the `text` segment with `{{ .Env.TWIG_PROMPT }}` is inside a `segments` array in one of your theme's blocks.

### Stale data

Prompt data is updated whenever a twig command modifies prompt-visible state (e.g., `twig set`, `twig state`, `twig save`). If you see stale data:

- Run any mutating twig command (e.g., `twig refresh`) to regenerate `.twig/prompt.json`.
- Ensure the hook function is defined **after** the Oh My Posh init line in your profile (bash/zsh/fish). If defined before, Oh My Posh may overwrite it.
- The `cache` property in the segment config (`"duration": "30s"`) tells Oh My Posh to cache the rendered segment for 30 seconds. Reduce or remove the `cache` block if you need real-time updates.

### Performance

The shell hook reads `.twig/prompt.json` directly — **no subprocess is spawned**. This is typically under 2ms:

- PowerShell: `Get-Content` + `ConvertFrom-Json` (~1-2ms for a <500 byte file).
- Bash/Zsh/Fish: `jq` (~1ms).
- No SQLite queries, no process startup, no network calls.

If you experience slow prompts, the issue is likely elsewhere in your Oh My Posh configuration (e.g., git segment with `fetch_status: true` on large repos).

## Reference

- `.twig/prompt.json` — Pre-computed state file written by twig commands. Contains work item summary, type badge, colors, and branch info.
- `twig ohmyposh init --shell <pwsh|bash|zsh|fish> --style <powerline|plain|diamond>` — Outputs shell hook function and Oh My Posh JSON segment snippet.
- [Oh My Posh — Templates (Environment Variables)](https://ohmyposh.dev/docs/configuration/templates#environment-variables) — Official documentation for the `Set-PoshContext` / `set_poshcontext` hook mechanism.
- [Oh My Posh — Text Segment](https://ohmyposh.dev/docs/segments/system/text) — Official documentation for the `text` segment type.
