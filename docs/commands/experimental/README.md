# Experimental commands

Commands under active development. Surfaces, flags, and behavior may change
without notice. Each command in this group launches — or emits configuration
for — a **companion binary** distributed alongside `twig`. The launcher
(`BinaryLauncher` in [`src/Twig/Program.cs:1797`](../../../src/Twig/Program.cs))
searches the directory containing the running `twig` binary first, then falls
back to `PATH`; failures are reported to stderr with a non-zero exit.

| Command | Companion binary | Summary |
|---|---|---|
| [`twig tui`](./tui.md) | `twig-tui` | Launch the full-screen tree/form TUI. |
| [`twig mcp`](./mcp.md) | `twig-mcp` | Launch the Model Context Protocol server over stdio. |
| [`twig ohmyposh init`](./ohmyposh-init.md) | — (in-process) | Emit an Oh My Posh shell hook and segment JSON. |

## Companion-process contract

- `twig tui` and `twig mcp` shell out to independent processes. Both `twig`
  and its companion binaries are versioned and released together; a mismatch
  can leave the companion unable to read the local cache.
- Companion binaries must be resolvable either next to `twig` or on `PATH`.
  `./publish-local.ps1` deploys both `twig`, `twig-tui`, and `twig-mcp` to
  `~/.twig/bin/`.
- Stdin/stdout are inherited by the launched process. `twig mcp` uses stdio
  as its MCP transport, so nothing else may write to stdout.
- `twig ohmyposh init` runs in the primary `twig` process — no companion
  binary is required.
