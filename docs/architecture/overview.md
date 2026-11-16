# Architecture overview

Twig consists of a CLI, a pure work-management Core, SQLite persistence, provider
adapters, and an optional Windows daemon. The former `Twig.Domain`, standalone
MCP/TUI hosts, old cache/writer graph, and legacy browser are not the current
architecture.

## Projects and responsibilities

| Project | Responsibility |
| --- | --- |
| `Twig` | ConsoleAppFramework command entry point, argument admission, static terminal/JSON rendering, offline help and skills |
| `Twig.Core` | Immutable typed items, schema, drafts, proposals, policy, canonical digests, execution and operation receipts |
| `Twig.Store.Sqlite` | Core store implementation, transactional state and journal persistence |
| `Twig.Providers.Ado` | ADO schema discovery, native reads, revision-bound writes and verification |
| `Twig.Providers.GitHub` | GitHub API adapter, opaque observations, explicit observed-non-atomic effects |
| `Twig.Providers.Native` | Durable per-user Twig-native authority and explicit native schema |
| `Twig.Infrastructure` | Native authentication/bindings, workspace admission, Core host composition and host-facing APIs |
| `Twig.RenderTree` | Pure rendering vocabulary shared by consumers |
| `Twig.Daemon.*` | Windows same-owner service, application/domain/infrastructure, and pure client library |

The exact supported build inventory, including test projects and the external
RenderTree consumer sample, is [`Twig.slnx`](../../Twig.slnx).

## Execution boundary

The CLI admits arguments and the selected workspace before composing a provider.
Data calls require a fresh pinned Core workspace; there is no legacy repository
or writer fallback. Native host utilities such as named enrollment, binding
inspection, and user-wide Bench metadata have their own bounded routes.

Core owns the provider/store ports and mutation lifecycle. Infrastructure binds
an operation to the actual connection, principal, workspace, store, and policy.
Providers do not switch identities or retry uncertain writes. The SQLite adapter
persists drafts, proposals, attempts, acknowledgements, and permanent creation
mappings; provider I/O does not run inside its transactions.

## Product boundaries

The CLI is NativeAOT, fully trimmed, and reflection-free for JSON. Serializable
host models use source-generated metadata. The daemon is a separate self-contained
Windows application, not the AOT CLI renamed or launched in a UI mode.

Twig owns daemon source and delivery. The independently packaged
`twig-bench-canvas` owns presentation assets. Hosts do not gain publication
authority from opening a view or receiving an SDK acknowledgement.
See [daemon architecture and operation](../daemon.md).

ADO process vocabulary is discovered, never assumed. GitHub API constraints and
configured Twig policy are not discovery of external repository automation.
Twig-native values belong only to that provider's explicit schema.

## Further reading

- [Storage and ownership](data-layer.md)
- [CLI and help composition](commands.md)
- [ADO integration](ado-integration.md)
- [GitHub provider](../../src/Twig.Providers.GitHub/README.md)
- [Build and release](build-and-release.md)
- [Detailed clean-core contract](clean-core-contract.md)
