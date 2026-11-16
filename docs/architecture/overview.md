# Current architecture

Twig ships three distinct surfaces: the Windows/Linux x64 Core CLI, the Windows
native daemon, and the public RenderTree SDK. Presentation assets and the
presentation host are separately packaged. See the [delivery matrix](../../README.md#current-delivery).

## Composition

| Project | Responsibility |
|---|---|
| `Twig` | ConsoleAppFramework source-generated CLI, explicit targeting, static/structured rendering and native utilities |
| `Twig.Core` | Provider-neutral semantics, identity/revision rules, schema, policy, proposals and operation journal |
| `Twig.Store.Sqlite` | Core persistence and local transactional state |
| `Twig.Providers.Ado` | ADO discovery, native observations and qualified provider effects |
| `Twig.Providers.GitHub` | Qualified GitHub issue observations and explicitly bounded observed-non-atomic effects |
| `Twig.Providers.Native` | User-native work-item authority and durable creation ledger |
| `Twig.Infrastructure` | Internal native services, initialization, authentication, user metadata and application composition |
| `Twig.Daemon.Domain/Application/Infrastructure` | Native host domain, application and infrastructure boundaries |
| `Twig.Daemon` | Windows authenticated localhost service, built as `Bench.Server.exe` |
| `Twig.Daemon.Client` | Same-machine startup and package verification without importing provider composition |
| `Twig.RenderTree` | Supported public rendering API, independent of Infrastructure |
| `Twig.DetailHost` sample | .NET 10 external consumer probe referencing only RenderTree |

The supported solution inventory is `Twig.slnx`. Internal projects are not a
general-purpose public SDK. Only RenderTree is published as the supported NuGet
package. CLI and daemon compose shared application services but own separate
entrypoints, lifetime and delivery.

## Authority and data

Fresh initialization creates the native Core workspace store. No marker means
initialization is required, never a legacy repository fallback. Bindings capture
the selected provider/source/principal. User-wide Bench definitions live in
`benches.db`; native-provider authority lives in `native-authority.db`, separate
from per-workspace mirrors, drafts and journals.

Core owns provider effects, digest confirmation, policy and execution journals.
Hosts must not manufacture remote identities, treat cached evidence as a fresh
read, replay an uncertain write, or create a second publisher. Developer-local
stores are disposable; current delivery has no migration/adoption path.

## Host boundaries

The CLI is NativeAOT/full-trim, uses source-generated JSON and discovers process
vocabulary dynamically. Structured calls are explicitly scoped, never driven by
interactive active context.

The daemon verifies the Windows owner and trusted pinned localhost TLS, with no
remote listener. Its read-only `mcp-read-v1` tools share existing source, snapshot,
Bench and policy checks; they do not expose Save/Apply. Independent canvas assets
are hash/protocol verified. Closing a viewer or stdio bridge does not stop the
shared daemon.

RenderTree renders caller-supplied presentation models. It is not a provider,
store, host or mutation authority. The .NET 10 DetailHost sample must continue to
build without Infrastructure, Spectre or daemon composition.

## Retired boundaries

Legacy Domain/Shared composition, standalone MCP, full-screen TUI/Herdr,
OMP/item-companion adapters, captured UI command envelopes, `plan` aliases and
Infrastructure package distribution are not current application routes.
Historical design/evidence remains in the archive areas listed by the
[documentation index](../README.md).
