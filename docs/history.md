# Design and historical records

These directories preserve engineering decisions and evidence. They are not
current installation instructions, a supported command catalog, or proof that
a proposed feature shipped.

| Location | Interpretation |
| --- | --- |
| [`projects`](projects) | Implementation plans and audits; many predate the Core cutover |
| [`specs`](specs) | Design contracts, including older/withdrawn CLI and envelope designs; check the current implementation boundary before relying on one |
| [`research`](research) | Point-in-time investigations, including retired TUI technology |
| [`reference-process`](reference-process) | Versioned process fixtures, proposals and captured evidence; not the current universal process schema |
| [`handoffs`](handoffs/README.md) | Operator handoff records, including old TUI checks |
| [`incidents`](incidents) | Recorded failures and diagnostic lessons, not current success claims |
| [`adr`](adr) | Architecture decisions with their recorded scope |

The [clean-core contract](architecture/clean-core-contract.md) preserves the
detailed cutover record alongside current native API boundaries. Its phase
history must not be read as a list of supported alternate application paths.
The Domain/cache graph and old standalone MCP/TUI/browser projects it discusses
are not the current architecture.

Use the [documentation index](README.md) for current user/operator guidance and
the [architecture overview](architecture/overview.md) for current project ownership.
