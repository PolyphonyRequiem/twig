# MCP: current daemon bridge versus retired server

The old standalone **`twig-mcp`** server and **`twig mcp`** launcher are retired.
Their Domain/cache mutation catalog is not part of the supported solution,
installer, or companion downloads.

The Windows daemon now has a separate **read-only MCP interface**, capability
`mcp-read-v1`, on its existing authenticated localhost origin. Its stdio bridge
connects to the already-running verified daemon. It exposes Bench reads and
work-item details, not drafting, membership changes, Save, or Apply.

For prerequisites, client configuration, tool behavior, and asset requirements,
use the [daemon MCP guide](../daemon.md#read-only-mcp). Do not install an old
`twig-mcp` binary or try `twig mcp` as a fallback.
