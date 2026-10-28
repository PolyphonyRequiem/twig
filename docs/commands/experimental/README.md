# Experimental commands

Commands under active development. Surfaces, flags, and behavior may change
without notice. Windows/Linux `twig tui` runs the native browser in-process;
macOS support is temporarily suspended. `ohmyposh init`
also runs in-process. MCP is withdrawn from supported delivery pending its
separate redesign; its draft reference remains for context.

| Command | Companion binary | Summary |
|---|---|---|
| [`twig tui`](./tui.md) | None | Open the native Bench browser on Windows/Linux. |
| [`twig mcp`](./mcp.md) | `twig-mcp` (withdrawn) | Retained redesign context, not a supported packaged companion. |
| [`twig ohmyposh init`](./ohmyposh-init.md) | — (in-process) | Emit an Oh My Posh shell hook and segment JSON. |

## Launch contract

- Windows/Linux `twig`, `twig-tui` and `twig-bench-tui` contain identical native
  executable bytes (`.exe` on Windows). Standalone names select `tui`; no
  companion subprocess or first-run download is involved.
- macOS invocations refuse explicitly before workspace access or companion launch.
- Explicit `twig upgrade` synchronizes native launch aliases on Windows/Linux
  from the archive's main `twig` image; macOS upgrades are disabled.
- `twig ohmyposh init` runs in the primary `twig` process without a companion.
