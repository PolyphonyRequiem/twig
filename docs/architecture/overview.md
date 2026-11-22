# Architecture overview

Twig consists of a CLI, a pure work-management Core, SQLite persistence, provider
adapters, and an optional Windows server. The former `Twig.Domain`, standalone
MCP/TUI hosts, old cache/writer graph, and legacy browser are not the current
architecture.

## Projects and responsibilities

| Project | Responsibility |
| --- | --- |
| `Twig` | ConsoleAppFramework command entry point, argument admission, static terminal/JSON rendering, offline help and skills |
| `Twig.Core` | Immutable typed items, schema, drafts, plans, policy, canonical digests, execution and operation receipts |
| `Twig.Store.Sqlite` | Core store implementation, transactional state and journal persistence |
| `Twig.Providers.Ado` | ADO schema discovery, native reads, revision-bound writes and verification |
| `Twig.Providers.GitHub` | GitHub API adapter, opaque observations, explicit observed-non-atomic effects |
| `Twig.Providers.Native` | Isolated named owner-local authorities and explicit schema for the Twig provider |
| `Twig.Infrastructure` | Native authentication/bindings, workspace admission, Core host composition and host-facing APIs |
| `Twig.RenderTree` | Pure rendering vocabulary shared by consumers |
| `Twig.Server.*` | Windows same-owner service, application/domain/infrastructure, and pure client library |

The exact supported build inventory, including test projects and the external
RenderTree consumer sample, is [`Twig.slnx`](../../Twig.slnx).

## Work item identity

The [glossary](../../GLOSSARY.md#work-item-identity) distinguishes a **work
item**, which has a provider-issued identity, from a **seed**, which has only a
local identity before publication. An item issued by the Twig provider is a
work item even though that provider runs locally. Core's `ItemKey` supports
both kinds of identity; the `WorkItem` type does not by itself imply
provider-issued identity.

A work item key combines the provider, its resource scope, and the native ID.
GitHub issue `#123` in two repositories represents two work items. Two accounts
viewing `#123` in the same repository are viewing the same item: the connection
controls access, and login alone is not part of the key. A provider can define
an account-scoped namespace if that is where it assigns IDs.

A seed's negative CLI alias is shorthand; its local ID is the durable
identity. A revision describes the version observed for a particular work item,
not its identity or whether the provider can reject a stale write.

## Work item structure

Use the [glossary](../../GLOSSARY.md#work-item-structure) to distinguish schema
from observations. Core's `ItemType` lists `FieldDef` entries;
`WorkItem.Fields` holds values for a particular item. Interpret field references
in their schema and item type, not by display label: an ADO field and a Twig
provider field both called "Priority" need not have the same meaning or value
kind. Core can distinguish an explicit `FieldValue.Null` from an absent entry,
but interpreting an omission as unset requires provider evidence. A cached
observation is last-known, not a fresh read.

Preserve the exact state for display and workflow decisions, and use its category
to group items. Two process states such as "Implementing" and "In Review" can
both be In Progress without being interchangeable. Use the discovered or
declared category; do not infer it from the state name.

Preserve both the target and ownership of a link. Core's `Relationship` has item
keys as endpoints and tracks provider versus local provenance; a `ResourceLink`
has a locator. A pasted GitHub issue URL can remain a resource link, or serve as
input to an explicit relationship once resolved to an item key. ADO discovers
native relationship kinds and handles hyperlinks separately. The GitHub adapter
uses native hierarchy and dependency relationships where supported and keeps
`Related` local. Do not silently substitute a hyperlink or a local relationship
for an unsupported provider relationship.

A Twig-owned dependency from an ADO Bug to a GitHub issue and a work item
extension keyed to the original item are domain examples, not claims of current
adapter support. When a provider is unavailable, an extension's own data and
the last-known or unobserved provider data remain distinct.

## Plans

The [glossary](../../GLOSSARY.md#plans) uses **Plan** for a defined set of
intended work-item changes. Local edits and seeds are editable intent; a Plan
captures exact operations. The operation journal records what happened when
those operations were applied. Previewing a Plan does not execute it, and a
provider acknowledgement is not a verified outcome.

Each executable Core Plan binds to one connection. A Bench Plan session can
assemble operations for several connections; it does not make them a single
atomic change.

`plan apply --file` explicitly requests execution of the named Plan. Preview is
optional, and apply has no separate confirmation step. This does not remove
access, integrity, concurrency, or journal checks. See
[change plans](../features/plans.md) for the lifecycle.

## Benches and workspaces

A [Bench](../../GLOSSARY.md#benches) owns rules, not work items or a fixed
result list. A workspace limits what it can read or act on; a native view can
select a Bench, but selection grants no write authority.

For example, a Bench can include rules for an Azure DevOps connection and a
GitHub connection. A CLI workspace admitted to Azure DevOps shows only its
connection's projection. An exclusion in that workspace leaves the shared
Bench unchanged; retained local intent can still appear, marked as excluded.
A missing GitHub row does not mean the GitHub rule was removed, and an
unobserved field is not evidence that a query rule failed to match. See the
[Bench ownership contract](clean-core-contract.md#user-wide-bench-ownership).

## Execution boundary

The CLI admits arguments and the selected workspace before composing a provider.
Data calls require a fresh pinned Core workspace; there is no legacy repository
or writer fallback. Native host utilities such as named enrollment, binding
inspection, and user-wide Bench metadata have their own bounded routes.

Core owns the provider/store ports and mutation lifecycle. Infrastructure binds
an operation to the actual connection, principal, workspace, store, and policy.
Providers do not switch identities or retry uncertain writes. The SQLite adapter
persists drafts, plans, attempts, acknowledgements, and permanent creation
mappings; provider I/O does not run inside its transactions.

## Product boundaries

The CLI is NativeAOT, fully trimmed, and reflection-free for JSON. Serializable
host models use source-generated metadata. The server is a separate self-contained
Windows application, not the AOT CLI renamed or launched in a UI mode.

Twig owns server source and delivery. The independently packaged
`twig-bench-canvas` owns presentation assets. Item and Bench selection belongs
to the native server/Canvas session or view; it never supplies a CLI target.
The CLI renders static views, not a TUI. Hosts do not gain publication
authority from opening a view or receiving an SDK acknowledgement.
See [server architecture and operation](../server.md).

ADO process vocabulary is discovered, never assumed. GitHub API constraints and
configured Twig policy are not discovery of external repository automation.
Values from the Twig provider belong only to its explicit schema.

## Further reading

- [Storage and ownership](data-layer.md)
- [CLI and help composition](commands.md)
- [ADO integration](ado-integration.md)
- [GitHub provider](../../src/Twig.Providers.GitHub/README.md)
- [Build and release](build-and-release.md)
- [Detailed clean-core contract](clean-core-contract.md)
