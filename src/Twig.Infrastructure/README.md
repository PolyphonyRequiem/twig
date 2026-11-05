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

## Native authoring primitives

Core evaluated editor fields reuse native predicate/field validation and expose
effective required/read-only/allowed-value guidance and discovered transitions.
An empty constrained allowed set is distinct from an unconstrained field. These
are provisional rendering results, not cached permission to publish.
An active native supplier can satisfy requiredness without making a field
read-only; only the actual field/read-only rule controls that restriction.
Local-seed guidance evaluates the complete authored field set against Core's
null creation baseline, not a fictional remote state transition.

Concrete ADO/GitHub editor metadata captures retain their exact provider owner,
native actor/resource/acquisition context and schema. Explicit editor entry
acquires presentation once; bounded canonical-choice queries reuse that capture
without another process/layout/icon crawl. ADO identities retain native GUIDs;
GitHub assignees/labels/milestones retain native node IDs. Labels are display only.
Choices perform fresh native actor/resource admission and no mutation. Required
Save/Review/Apply authority, schema, policy and revision checks remain independent.

ADO compound `Batch.Links` requires explicit `AtomicRelationshipBatches`
capability as well as native field coupling. Directed incident add/remove and
reparent deltas use one source-revision-tested PATCH, descending removal indices,
and verification of every declared field and changed inverse endpoint.
Native empty incident relationships may omit `relations`; matching follows the
same complete-empty interpretation as native item reads, not missing-schema
fallback. Empty removals still refuse before dispatch.
GitHub does not advertise this compound capability; unsupported links cannot become a
field-only write. The original three-argument `CreationLink` constructor and
default add tuple bytes remain unchanged; only explicit removal adds a fourth
true tuple member. Proposal/store format versions are unchanged.

Remote Core Draft relationship restrictions remain unchanged. Durable NORMAL
and PLAN relationship capture, guarded editor/Plan APIs and fresh-scope
enlistment are separate native integration work, not provided by these primitives.
No browser/SDK editing or publication route is enabled here.

## Native execution safety contract (system registry v11)

Native registry upgrades add pending activation and Core source/attempt references
atomically with `layout_meta` v11. Original credentials, transport evidence and
Core proposals/journals are not rewritten or backfilled as successful outcomes.
Even a fresh registry stays pending: it does not prove historical roots are absent.

Before publishing, the native coordinator inventories system-worktree roots,
user-wide Bench workspaces, every retained Plan scope (including superseded or
deleted-Bench remnants), and explicitly supplied historical roots/store locators.
Only known native artifact directories are inspected; no filesystem hunting.
An unavailable locator, replacement store or unmatched original intent refuses
activation. Exact inventory/source revisions and Core versions bind the digest.
Completeness and old-process quiescence are explicit human delivery confirmations,
not mechanically inferred from currently registered roots or fabricated now.
Reads/status and local draft capture remain available while publishing is pending.
New previously unknown opened stores invalidate inventory confirmation.

Current native CLI, normal Save and Plan execution share this gate. Canonical
provider/resource/item and permanent UUID references retain exact owner-store,
proposal, operation and attempt attribution. Deterministically ordered native
leases coordinate roots/actors. Core journal state is execution authority: an
HTTP ACK cannot clear Applied, Partial or Indeterminate evidence. An unmapped
unknown create conservatively fences mutations in its immutable destination;
an attributed mapping additionally fences the remote key but is not verification.
UUID-owning references force authoritative mapping lookup across destinations;
this does not depend on a later inventory refresh or post-ACK index write.
Unattempted Planned references can be reclaimed only under an exclusive lease,
fresh exact store/journal proof and exclusion of original transport dispatch.
The native reference set has a separate integrity digest; missing index rows are
not absence proof or silently reconstructed. Monotonic observed Core versions and
ever-Applying admission witnesses cannot be downgraded into reservation-only
claims by restoring a same-instance store/transport backup. These are reference
provenance, not another outcome journal. The unpublished v11 schema requires this
metadata; incomplete older experimental v11 layouts fail closed without backfill.
Attributed native `Failed` receipts with no published identity/revision are
definitive no-effect results, including complete provider-classified HTTP
rejections. They allow only a fresh proposal, never replay of the failed request;
partial/indeterminate effects and attempted creation UUIDs remain fenced.
Historical exact-instance Core evidence can be inspected after its authoring
root disappears; that does not restore the old root's Stage/Apply authority.
Unattempted reclaim still requires the original transport root to be present.
Protected-intent reassociation and result delivery remain separate work.

The compiled compatibility fixture proves v10 fresh opens refuse registry11 and
cached v10 handles still accept their old connection. Already-open v10 processes are NOT
retroactively fenced by the version change. Safe activation requires quiescing
old processes and deploying the supported fence-aware CLI and panel together.
The minimum publishing contract is this schema11 target-admission implementation
in both runtimes, not a version-number-only check. Delivery must record their
exact fence-aware build identities; prior v10 binaries are unsupported publishers.
Executable-only rollback is unsupported. Never restore a preactivation database
after new effects: retain inspection backups and use forward-compatible recovery,
unless explicit evidence establishes that no new effects occurred. No old binary
or ADO/GitHub web-client enforcement is claimed; external non-atomic races remain.
These APIs stay native/internal; no new approval catalog or per-Save ceremony.

## License

MIT.
