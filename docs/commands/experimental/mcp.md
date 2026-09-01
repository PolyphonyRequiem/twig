---
command: mcp
group: experimental
summary: Launch the Model Context Protocol server (requires twig-mcp companion binary).
stability: experimental
mutates: ado
---

# `twig mcp`

`twig mcp` hands control to the `twig-mcp` companion binary, an MCP server
that exposes Twig's workspace and mutation surface to AI agents over stdio.
Reach for it when wiring VS Code / Copilot (or any MCP-aware host) to a local
Twig workspace. The server's tool catalog includes read-only views
(`twig_show`, `twig_tree`, `twig_query`) *and* destructive operations
(`twig_state`, `twig_update`, `twig_batch`, `twig_delete`, `twig_proposal_apply`),
so this command can — through downstream tool calls — mutate the ADO backing
store. Registration for VS Code lives in `.vscode/mcp.json` under the name
`"twig-mcp"`.

## Synopsis

```
twig mcp [--tool-profile <profile>] [-h|--help] [--version]
```

## Arguments

|Argument|Required|Description|
| --- | --- | --- |
| — | — | — |

## Flags

|Flag|Type|Default|Description|
| --- | --- | --- | --- |
|`--tool-profile <profile>`|string|`compact`|Advertised tool profile. `compact` (alias `core`) exposes the high-frequency subset; `full` (alias `all`) advertises the entire catalog. Hidden tools remain callable by name even under `compact`.|
|`-h`, `--help`|switch|—|Print help and exit.|
|`--version`|switch|—|Print the `twig` version and exit.|

## Behavior

- The CLI wraps the flag into an argument vector and delegates to
  `BinaryLauncher.Launch("twig-mcp", "Twig.Mcp", arguments: [...])` at
  [`src/Twig/Program.cs:1344-1352`](../../../src/Twig/Program.cs). Only
  `--tool-profile` is forwarded when supplied; nothing else on the CLI is
  proxied.
- The companion binary is resolved adjacent to `twig` first (via
  `AppContext.BaseDirectory` then `Environment.ProcessPath`), then along
  `PATH` ([`src/Twig/Program.cs:1848-1887`](../../../src/Twig/Program.cs)).
- On start, `twig-mcp` calls `WorkspaceGuard.CheckWorkspaceAmbient(cwd)`
  and boots even without a workspace — tools return informative errors when
  invoked in that state, which prevents MCP hosts from marking the server
  "failed" on unrelated sessions
  ([`src/Twig.Mcp/Program.cs:14-27`](../../../src/Twig.Mcp/Program.cs)).
- Tool profile resolution reads `--tool-profile` / `--tool-profile=<value>`
  from argv, then falls back to `TWIG_MCP_TOOL_PROFILE`, then defaults to
  `compact`. Unknown values throw `ArgumentException`
  ([`src/Twig.Mcp/Services/McpToolCatalog.cs:182-211`](../../../src/Twig.Mcp/Services/McpToolCatalog.cs)).
- Transport is **MCP over stdio**: MCP framing owns stdout and stdin;
  all logging is redirected to stderr with `LogToStandardErrorThreshold = Trace`
  ([`src/Twig.Mcp/Program.cs:47-48`](../../../src/Twig.Mcp/Program.cs)).
- A `ParentProcessWatchdog` hosted service self-terminates the server when
  the host process exits, preventing orphaned `twig-mcp` instances on VS
  Code reload or CLI exit
  ([`src/Twig.Mcp/Services/ParentProcessWatchdog.cs`](../../../src/Twig.Mcp/Services/ParentProcessWatchdog.cs)).
- Authentication is per-user, not per-workspace. If any registered workspace
  uses PAT, the server uses PAT; otherwise it falls through the MSAL-cache-first
  Azure CLI chain
  ([`src/Twig.Mcp/Program.cs:32-37`](../../../src/Twig.Mcp/Program.cs)).

## Examples

Start the server for an MCP host (VS Code launches it via `.vscode/mcp.json`):

```console
$ twig mcp
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

Force the full tool catalog (advertises destructive and low-frequency tools):

```console
$ twig mcp --tool-profile full
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|MCP host closed the connection; server shut down cleanly|`0`|
|`twig-mcp` binary not found on PATH or adjacent to `twig`|`1`, stderr `error: 'twig-mcp' not found. …`|
|Companion spawn failed|`1`, stderr `error: Failed to start 'twig-mcp'.`|
|Companion launched but threw before init|`1`, stderr `error: Failed to launch 'twig-mcp': <message>`|
|`--tool-profile` given an unknown value|Companion exits non-zero after `ArgumentException` (`Unknown MCP tool profile '<value>'. Valid profiles: compact, full.`)|
|Companion exited with a non-zero code|That exit code (propagated verbatim)|

## See also

- [`twig tui`](./tui.md) — the sibling companion-binary launcher.
- [MCP server architecture](../../architecture/mcp-server.md) — deeper design notes.
- [`twig proposal apply`](../plans/proposal-apply.md) — the mutation the
  `twig_proposal_apply` MCP tool wraps.
