# MCP boundary

The current MCP interface is the Windows daemon's **read-only `mcp-read-v1`** on
`POST /api/v1/views/{viewId}/mcp`, sharing the existing authenticated localhost
origin. The verified installed launcher can bridge stdio with `-Mcp`; it does
not create another listener or stop the daemon on EOF.

`read_bench` and `show_work_item_details` reuse native owner, source/principal,
Bench, snapshot and policy checks. They expose exact cached identities and
unavailable-source information, not caller-supplied item contents. Stale or
unsupported requests refuse. MCP does not draft work, change membership, Save
or Apply.

The optional `ui://twig/work-item-details` App resource comes from independently
verified canvas assets. Plain structured MCP reads do not require those assets.
See [daemon delivery](../../README.md#native-daemon).

The old standalone `twig-mcp` host, its legacy tool catalog and automatic
companion installation are retired. Historical MCP ADRs/specs/research are
archive material, not another supported server or permission model.
