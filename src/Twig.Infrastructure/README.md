# PolyphonyRequiem.Twig.Infrastructure

Native host support for [twig](https://github.com/PolyphonyRequiem/twig), targeting
.NET 10 and .NET 11. The old Domain project and general-purpose application
registration graph are removed, not moved into this library.

The CLI composes the retained Core workspace through its closed native catalog.
`Twig.Core` owns the result/error family, process schema, three-way merge, proposals
and execution authority; the separate store and ADO/GitHub providers implement its
two ports. Infrastructure connects that spine to native host requirements:

- Explicit authentication and credential acquisition, named identities, bindings,
  generation and lease admission. Recorded selection is not fresh authentication.
- Native registry and attachment persistence, original request/observation/receipt
  evidence, and independent unknown-write fences. Existing native evidence and
  credentials are preserved; there is no legacy cache migration or evidence reset.
- Core workspace admission and provider access bound to the actual actor, immutable
  target, exact durable attempt and original request hash.
- Repository-declared mechanical policy data and projection into Core, without an
  old policy evaluator or repair-selection application.
- Native Markdown conversion, explicit self-update, and optional privacy-allowlisted
  telemetry. Automatic companion acquisition and retired UI hosts are absent.

`connection writes` is read-only correlated transport inspection, not settlement or
execution success. A receipt does not replace the Core journal. No inspection path
replays, adopts, rewrites a digest, clears a fence or deletes evidence.

The retained production CLI and real HTTP/SQLite boundary tests qualify the current
composition. Compiled PE regressions verify that CLI and Infrastructure have no
Domain dependency; `tools/verify-domain-cut.py` separately checks source/project
absence. The public, I/O-free rendering boundary and its external .NET 10 consumer
belong to `Twig.RenderTree`, not this library.

## Read-only native source boundary

External .NET hosts can call
`NativeSourceReader.ReadAsync(workspaceRoot, expectedSource, observeSchema, ct)`
in `Twig.Infrastructure.Services`. Supply an absolute, canonical existing
workspace root; no ancestor search, initialization or legacy-store fallback is
performed. The result uses existing `Twig.Core` types, not transport DTOs.

`NativeSourceObservation` contains the admitted `Source` stamp, one coherent
`StoreSnapshot` and its `CacheReadAt` time, plus optional schema evidence and
`SchemaObservedAt`. Native `Snapshot.Benches` are read evidence suitable for
explicit selector copying, not aliases or a mutation API. Items, relationships,
drafts, mappings, proposals and journal are retained unchanged. No active item,
default Bench, provider item refresh or publication is performed.

With `observeSchema: false` (the default), admission is local: no token acquisition
or HTTP is performed. This describes recorded selection, **not fresh upstream
authorization**. With `true`, existing provider metadata discovery acquires the
selected credential and observes the actual principal/target and schema. Schema
failure refuses the entire result, including HTTP 401/403 and ambiguous
unavailability; this boundary never selects an offline fallback. Schema evidence
is observed separately from the cached items and is not an atomic provider snapshot.

`NativeSourceReader.ReadOnlineAsync(workspaceRoot, expectedSource, ct)` requires a
captured stamp and fresh remote actor/target read-access validation, without
repeating schema or form/icon discovery. It returns no schema evidence and does
not advance `SchemaObservedAt`. ADO checks the same immutable collection,
principal and project as schema discovery, plus the project's work-item type
read route. GitHub checks the same viewer, repository and owner, plus an issues
count read without issue bodies. These are service-level read-access checks,
not proof of access to every cached item or area. Final local authority
revalidation still precedes release. Providers without this explicit
implementation refuse with `Unsupported`; permission, transport, malformed
responses and cancellation never become local/offline success.

Use the returned `NativeSourceStamp` as `expectedSource` on subsequent calls.
It binds the connection, store UUID, principal and an opaque native authority
stamp covering the workspace/database location, selection/binding revisions and
native generation. Ordinary snapshot-version changes do not change source identity.
Authority, marker location and store pin are revalidated before returning; a
replacement is refused, not adopted. A stamp is comparison evidence, not a
credential or permission to disclose content. Do not send stamps or native
snapshots to telemetry or expose them through agent actions by default.

Hosts own source registration and authorization policy, successful online
admission before permitting offline use, known-denial handling and any
source-partitioned cache of immutable schema observations. Offline reads cannot
discover an upstream revocation that is not locally known. `CacheReadAt` is not
a fabricated per-item provider observation time.

Each call owns and releases its native operation gate and provider resources;
there is no escaping lock, transaction, auth session or view/SSE lifetime lease.
SQLite capture completes before metadata HTTP. Contention is reported rather
than retried; caller cancellation propagates, with a thirty-second ceiling for
the whole operation. Acquire sources individually, never while holding another
source's gate. Read-only allows existing native housekeeping/lock files, but no
native Bench, work-item, context, journal, initialization or migration mutations.
Libraries are currently consumed through a pinned source dependency, not a
claim of published NuGet packages.

## Internal Bench Plan ownership

Native user-wide Bench metadata can retain an explicit Plan mode and independently
revisioned Plan descriptor. Each connection owns a separately reserved/pinned
`SqliteStore` instance under native user storage; the ordinary workspace database
and `.twig/core-workspace.json` are never replaced. Normal drafts, selection and
active history are not copied or changed. Leaving Plan mode or deleting its Bench
preserves the descriptor, scopes and permanent seed ownership.

Scope creation first retains its path/instance reservation. Incomplete creation
can complete only that exact reservation; mismatched files, paths, connection pins
and source stamps fail closed. Reassociation creates a new scope and preserves
the old store. Unresolved journal attempts and protected drafts/unsettled seed
ownership block reassociation until an explicit reviewed transfer is available.
Native seed ownership is permanent across Bench/mode changes and cannot be claimed
by another scope.

`ReadPlanSeedOwnerAsync` returns an explicit `NotFound` for an unregistered UUID;
callers must not treat absence as a successful mapping or borrow another scope.
`OpenPlanScopeAsync` can reopen an owned store for reads/status while Bench mode
is Edit. Its checks establish only store/source ownership, not Stage/Apply
permission. The final write facade must independently validate current mode,
capture revisions and fresh provider authority before staging or publication.

These internal persistence methods do not acquire credentials, refresh metadata,
authorize provider writes or expose an editor to external hosts. Source stamps
remain comparison evidence only. Human editor publication, result delivery and
global target admission require the separate native execution contract.

## License

MIT.
