# Clean core contract

Phases 1-4, phase-5 code closure and phase-6 source removal for **Epic #1220, Core domain consolidation**. Updated 2026-10-31.

## Status and scope

### User-wide Bench ownership

Native Bench definitions now live in `benches.db` beside the native user registry
(under `TWIG_USER_HOME`, or the existing per-user Twig home). The CLI and graphical
hosts use the public `UserBenchStore`; a viewer must not maintain an independent
authoritative copy. A repository/worktree does not own a Bench, and opening a
workspace creates no default Bench. Selection remains in that worktree's admitted
`core-context.json`, so switching one context never switches another.

Selectors retain item, subtree and typed query semantics and now carry their exact
connection. Area/iteration predicates stay rules, not frozen result lists.
One Bench can retain selectors from multiple connections; a native workspace's
store view projects only its admitted connection. Metadata listings report the
complete user-wide definition. A connection-scoped CLI workspace read remains a
cached projection of that connection, not a complete cross-connection refresh.

Previously saved native definitions are preserved once per native store instance,
with their IDs intact. Name/intent conflicts refuse rather than rename or
overwrite user choices. Deletion retains a tombstone so an older worktree cannot
resurrect the same Bench. Definition changes use revision checks and do not write
mirror copies of shared Benches, remote work items, proposals or journals.

Workspace exclusions are separately revisioned by workspace root and connection,
not subtracting Bench selectors. Cached views apply them after membership
resolution; retained drafts remain visible and marked when excluded. The
`workspace exclude` and `workspace exclusions` commands change only those local
view rules, without work HTTP or active-item/Bench selection changes.

User-wide `bench create/list/rename/delete` are native metadata commands and can
run without an initialized worktree. Selecting a Bench or editing contextual
pins still requires an admitted Core workspace. This foundation does not claim
that every retired area/iteration management or graphical surface is restored.

### Normal native ADO seed authoring

`NativeItemEditor.CreateSeedAsync` accepts one explicit discovered ADO type in
normal mode as well as the separate Plan path. It reserves a version-7 UUID in
the native capture before staging that same identity in the original Core store.
Creation starts with empty fields and no remote baseline; it never copies the
active item's type, state or assignment, infers a parent, or adds relationships,
resources or local-local dependencies. Connected creation requiredness is
evaluated against that null pre-state, separately from remote completion gates.
Incomplete fields may remain staged, but Save refuses them before Create.

`ReadOwnedSeedsAsync` supplies a temporary, exact-source/per-Bench projection
for native hosts. It includes only normal seed captures owned by that user
store, origin, Core store and Bench, not arbitrary CLI debt or isolated Plan
drafts. Hosts must overlay those drafts rather than save permanent local-UUID
Bench pins. A published seed's UUID is not a remote selector: Core Bench
resolution does not translate literal local pins through mappings.

Before Save dispatches, the sealed native capture retains the exact original
Core proposal and digest. `NativeEditResult.SeedPublication` exposes its durable
capture ID, UUID and digest, with `Protected`, `VerifiedDeliveryPending` or
`Complete` status. A lost response keeps the original draft, proposal, attempts
and receipts protected; no remote identity or successful outcome is guessed.
Verified-but-undelivered captures remain enumerable even if Core already
settled its draft. Reopen the same normal editor/source/Bench and use
`ObserveSeedAsync` or digest-bound `ContinueSeedDeliveryAsync` on that capture.
Neither operation prepares a proposal or calls Create/Apply again.

Normal and Plan delivery share native Infrastructure primitives for original
Verified journal/mapping attribution, original-source readback, additive latest
remote Bench pins and exact capture settlement. The capture is settled only
after readback and pin delivery succeed. Delivery recovery can complete local
settlement but cannot establish an uncertain remote outcome. Core remains the
only provider, policy, merge, proposal and journal publisher; there is no new
Core port, alternate executor, Plan scope for normal writes, or UI/install work.
GitHub seed creation and type/collection authoring remain unsupported; existing
scalar editing and isolated Plan review retain their previous boundaries.

### Normal native existing-item ADO relationships

`NativeItemEditor.SelectRelationshipsAsync(target, endpoint)` opens one explicit
pair of distinct, published items in the exact admitted ADO source and normal
Bench/mode. Its `NativeRelationshipForm` carries the target and endpoint
observations, canonical discovered `RelationKind` metadata, complete observed
incident edges, mechanically available `CreationLink` choices and retained
intent. Choices encode `Outgoing` and `Remove` explicitly: an incoming dependency
uses the same canonical reference, not a guessed inverse reference. Resource
relations, disabled or unavailable metadata, local UUIDs, cross-source endpoints
and unsupported providers/modes are refused, never represented by an empty
success or a simulated transport. Choices are guidance, not publication or
policy authority; Core rechecks the exact effect before Save.

`StageRelationshipsAsync(ticket, fields, intent)` retains one bounded pair's
explicit add/remove intent without provider writes. Existing-item links live in
the durable native capture's Batch operation; `Draft.Links` remains empty.
`SelectAsync` can reopen that normal ADO capture as read-only, exposing
`RelationshipIntent` and `RelationshipsReadOnly`; scalar Evaluate/Stage/Save
cannot mutate or publish it. Reopening both explicit endpoints restores the
qualified ticket without changing the original directed intent or observed
presence. A stale endpoint or protected capture is not silently rebound.
The qualified ticket also retains the displayed endpoint's original revision
and local draft fingerprint independently of later reads. Low-level native
staging compares that proof after acquiring endpoint evidence and again before
capture reservation, including on the first Save with no existing capture.
A late revision or local draft change refuses before replacing the original
capture or target draft; explicit relationship intent cannot recapture newer
endpoint evidence as if it had been displayed.
The existing `EvaluateAsync(ticket, fields)` accepts a qualified pair ticket
for read-only prospective field guidance. It reuses the captured native field
evaluator to expose discovered requiredness, allowed values and errors,
including missing close-gate fields, without staging the proposed values.
Current pair observations and the original ticket/source/store evidence must
still agree. Evaluation does not update the Core store, capture, proposal or
journal. Ordinary scalar reopening of retained relationship debt still refuses
evaluation; scalar Stage/Save also refuse even a qualified pair ticket.
Empty intent cannot discard protected relationships. Broader multi-endpoint
authoring and parent replacement are not part of this bounded surface.

`SaveRelationshipsAsync(ticket, fields, intent)` stages and calls the existing
native `PreviewEditorDraftAsync` / `SaveDraftAsync` / Core Apply path. Scalar
fields and incident deltas share one qualified ADO atomic Batch, original
digest, operation journal and verified target/inverse endpoint readback. The
fresh schema, actor/source, root/store, Bench/mode, ticket, target/endpoint
revisions, complete incidents, draft fingerprints and captured intent must
still agree. Core owns direction/presence validation and dependency-cycle
policy; hierarchy additions refuse an already-parented child rather than
removing or replacing its parent. No host copies these rules or adds a
publisher, port, executor or outcome type.

The existing Core/provider batch surface still supports an explicitly authored
atomic parent change that removes every observed old parent in the same operation
as the addition. That is not implicit reparenting and is not offered by this
single-pair editor.

An uncertain original attempt retains its proposal, receipts, draft and capture;
Save never replays or restages it. Unowned CLI debt and isolated whole-Plan
work are not adopted or flushed. Normal seed ownership, empty parentless
creation and original-proposal verified delivery remain unchanged. These new
forms are in-process typed API values, not new serialized transport envelopes;
the existing source-generated native capture registry is reused unchanged.
There is no private host/UI, install or runtime work in this increment.
GitHub local relationships use the separate surface below, never ADO atomic
Batch. Native GitHub relationship publication, seeds, types and collections,
seed parents/links/resources and local-local chains remain unsupported here.

### Normal native GitHub local-only Association

`SelectRelationshipsAsync(target, endpoint)` also qualifies one explicit,
distinct published GitHub pair in the same repository and normal Bench/source.
Both endpoints must already have complete uniquely numbered native observations;
refresh unobserved endpoints explicitly. Selection then fresh-reads both through
the captured provider/session metadata. Exactly one discovered symmetric
Association must be declared local-only, absent from native relationship
capabilities. Provider errors, denied access or unavailable semantics refuse;
they never select a local fallback. `NativeRelationshipForm.LocalOnly` and
`Verification` distinguish this form from ADO atomic editing. All scalar fields
are read-only, native `CreationLink` choices are empty, and generic scalar
Evaluate/Stage/Save cannot adopt its ticket or retained intent.

The separate human-host `ReviewLocalRelationshipAsync(ticket, adding)` reserves
a durable native capture and retains one canonical Core `Relate` or `Unrelate`
with `Origin.Local`, its exact digest and Planned journal row.
`ApplyLocalRelationshipAsync(captureId, exactDigest)` performs the first attempt
only through existing Core policy, native source/target admission and provider
journal/verification. There are no scalar fields, Batch, synthetic Draft or
seed publication references in this capture. The CLI and facade share the
Infrastructure local identity/Association/CAS preparation, not a second
publisher or new Core port.

The local capture binds the original source, Bench/mode, store instance,
generation/history/reference, canonical proposal, both native observations and
independent draft fingerprints, original logical UUID mapping and physical
edge/tombstone CAS. Unowned CLI or isolated Plan debt is not adopted. Original
UUID-keyed edges remain unchanged across publication mappings and symmetric
reverse-order edits; removals retain tombstones and re-addition advances the
version, refusing stale ABA intent. Changed proof refuses without recapture,
restaging or replacement intent. Fresh provider inputs are checked against the
native capture again after the durable Applying fence, before local CAS.
This does not grant GitHub revision CAS or an atomic field/link transaction.

`ObserveLocalRelationshipAsync(captureId, exactDigest)` returns the original
Core journal entry, settlement flag and any error; no new status or receipt is
invented. Reopen the same normal source/Bench and use
`ContinueLocalRelationshipAsync` for exact interrupted retention, separate
verification of an original durable Applied receipt, or Verified capture
settlement. It never prepares new intent or invokes Apply again.
Incomplete review errors retain `capture:` and `digest:` recovery references.
An unresolved original attempt remains protected; a new digest cannot bypass
it. Settled observations retain historical original journal proof even after
later scalar or relationship changes; they do not assert current item state.
Local **Verified means durable local-store CAS, not tracker publication**;
its receipts carry no published key or provider revision. Scalar GitHub
title/Markdown-body/discovered-state Save remains a separately requested
provider effect. Existing ADO StageRelationships/SaveRelationships continue to
refuse GitHub and retain their atomic Batch semantics.

### Explicit-team native planning

A Bench can retain rules from several teams on one connection as well as rules
from other connections. Each relative iteration, saved-query or backlog leaf
retains its canonical native team ID in `Query.PlanningTeam`. Nested AND/OR rules,
cached membership, workspace exclusions and explicit Bench pulls resolve each
leaf against that exact team's evidence; the configured workspace team is not a
substitute. A connection-scoped pull ignores another connection's selectors.

The public `NativeSourceReader` supports add-time selection without changing
workspace configuration or active context:

```csharp
Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, CancellationToken ct = default);
Task<Result<NativeSourceObservation>> ReadPlanningAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct = default);
Task<Result<NativeSourceObservation>> RefreshPlanningAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct);
Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct);
Task<Result<NativeSourceObservation>> ReadPlanningSourcesAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct = default);
Task<Result<NativeSourceObservation>> BrowsePlanningAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam,
    PlanningSourceKind kind, string id, CancellationToken ct);
Task<Result<NativeSourceObservation>> BrowsePlanningAsync(
    string workspaceRoot, NativeSourceStamp expectedSource, string? selectedTeam,
    PlanningSourceKind kind, string id, ProcessSchema mappingSchema, CancellationToken ct);
```

`PlanningTeam.Id` is the canonical GUID and `Name` is the actual ADO display name,
not a configured alias. Project-team enumeration exhausts native `$top`/`$skip`
pages of 100, with a 4,096-team limit; repeated identities, unsupported continuation,
permission failures and incomplete evidence refuse without returning a partial
roster. In the new overloads, non-null `selectedTeam` must be a canonical ID;
null means explicit project scope, never the configured team. Project scope
permits absolute paths and project saved queries, but cannot supply a team
calendar, team backlogs or team-dependent queries. Existing configured-team
overloads and the old browse overload's separate `expectedTeam` meaning remain.

`ReadPlanningAsync` validates fresh actor/target read access and team existence,
then returns the retained calendar without schema discovery. A missing retained
calendar requires explicit refresh; it is not an empty successful calendar.
Calendar refresh retains its full schema observation. Source catalogue
read/refresh and source execution return no fresh `Schema` or `SchemaObservedAt`.
All native
operations revalidate actor/source/store before releasing observations; refresh
and browse also perform that check before saving acquired calendar or membership
evidence. They expose no work-item mutation or persisted authorization grant.

`NativeSourceObservation.Planning` remains the selected/configured calendar.
Its `PlanningCalendars` and `Memberships`, and the corresponding `ProcessSchema`
properties, expose retained evidence for all teams on the admitted connection,
not other connections. `PlanningCalendars` contains one calendar per actual team
ID; Core uses that collection for exact per-leaf resolution. These observations
reuse the existing connection/team-keyed planning and membership tables.
Refreshing one canonical team preserves every other team's calendar and rules;
no additional cache or local-data migration is introduced.

### Durable native source catalogues and lightweight execution

The per-user `UserBenchStore` owns the durable `planning_sources` catalogue
table, keyed by the complete native `Connection` (including actor identity)
and the exact canonical team ID or explicit project-scope key. It is separate
from planning calendars and executed-source memberships. No local-data
migration, default-team alias, access grant or expiry is stored.

`NativeSourceObservation.PlanningSourcesCache` is
`NativePlanningSourcesCache(PlanningSources? Sources, DateTimeOffset? ObservedAt)`.
Both null means no retained catalogue, a successful recorded-metadata read
after fresh actor/target and final native source/store validation.
`PlanningSources` remains the compatible sources field. The cache retains native
saved-query/backlog IDs, names, paths, folder hierarchy (including empty folders),
query kinds, team execution requirements and declared gaps, never membership
item IDs. Explicit `DiscoverPlanningSourcesAsync` acquires catalogue metadata
only, then replaces one complete exact-scope row atomically after final native
admission. Partial, denied, failed, cancelled or replaced-authority refreshes
preserve the previous complete catalogue and return an error, not cached success.
An older concurrent observation cannot replace a newer retained catalogue.

Reading or refreshing a catalogue never executes a query/backlog or discovers
per-type process fields, rules, states, layouts or icons. Recorded catalogues
are not evidence that every source is still executable or every listed team is
still available. Explicit execution validates the current actor, immutable
project, selected team and the source's direct native ID endpoint rather than
rediscovering the whole query hierarchy. Native identity changes, unsupported
pagination, bounded acquisition and complete membership checks still refuse.

The browse overload accepting an already-admitted `ProcessSchema` reuses it
only as type/field read-mapping evidence. The native ADO type-name observations
already retained in that schema are required; unknown types or incident
relations refuse with an explicit request to refresh mapping metadata. One
bounded current relation-roster read supplies authoritative forward/reverse
endpoint semantics; no reverse names are guessed and no per-type process
discovery or presentation crawl runs. Connection mismatch refuses before
execution. Callers without recorded mapping can retain the original browse
overload, which still acquires the mapping it needs but no longer performs a
second native presentation discovery.

Hosts may reuse their admitted schema/presentation observation and compose
the current native calendars/memberships into it. They must not label reused
schema as freshly discovered or treat recorded catalogue timestamps or supplied
mapping metadata as authorization. Same-root native operation coordination
and all existing publication checks remain unchanged.

### Closed production command catalog

The final phase-5 closure narrows existing production owners, rather than adding
another facade or parity tranche. `WorkspaceRegistrationModule` installs the native
bootstrap and retained host commands independently of a marker; only a pinned
Core marker admits Core data owners. A no-marker data invocation refuses explicitly,
never installing `AddConnectionServices`, `AddTwigNetworkServices`,
`AddTwigCommandServices` or their old writer/projection graph. The retained static
RenderTree renderer does not register the asynchronous/aggregate workspace pipeline.
Actual descriptors and retained-owner resolution are proven both with and without a
marker. Core retains two ports and proposal format 3; the phase-6 source cut is described below.

### Optional process presentation

The consolidated `ProcessSchema` now carries optional type presentation, field
labels, state colors and a source web URL. Consumers opt in with
`IProvider.DiscoverSchemaAsync(connection, cancellationToken, includePresentation: true)`;
ordinary validation and mutation discovery does not acquire form layouts.
The native read-only source boundary opts in for browser/canvas consumers.
Full metadata refresh remains explicit. Ordinary online hosts can instead call
`NativeSourceReader.ReadOnlineAsync` to revalidate current actor/target read
access through `IProvider.ValidateReadAccessAsync` and final native authority
checks, while retaining their previously admitted presentation observation.
That operation returns no fresh schema and makes no schema freshness claim.
Unsupported providers fail closed. No additional port or schema model is added.

ADO presentation reads the exact process roster reference, never a similarly named
project/parent type. Type artwork is acquired from the native work-item icon route
as bounded SVG using the discovered icon ID and RGB color, not a guessed glyph.
The [documented process API string icon](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/work-item-types/list?view=azure-devops-rest-7.1)
and prior object/id observations retain the same native identity. Artwork is
deduplicated per observed icon/color pair during explicit presentation discovery;
missing icons remain unavailable, while malformed, oversized or denied images
fail explicitly. SVG parsing prohibits DTDs and external entity resolution.
It preserves server-defined pages, sections (columns), groups,
ordered controls and separate system controls, including visibility, read-only and
contribution markers. Served-but-empty, locked (`VS403115`) and unavailable layouts
remain distinct. Authentication, permission and other transport errors are failures,
not missing-layout fallbacks. Extension controls are metadata, not executable code.
GitHub carries its observed repository URL and issue type names without fabricating
an ADO form layout.

Presentation is excluded from the mutation/schema digest: an icon, label or layout
change must not invalidate reviewed work-item mutation intent. This restores data
acquisition through the existing provider port, not the legacy Domain/service graph
or the retired CLI layout command.

The finite data catalog is process/description, cached show/show-batch with explicit
refresh, bounded query, update/patch/state/note/batch, pending/save/discard,
seed new/view/validate/publish/discard and original acknowledged recovery,
file proposal validate/preview/apply/status, Bench list/create/switch, single pins
and static workspace. Already qualified ADO pull/sync, editor/navigation,
seed-chain/creation links/targeted trees, Link and Core local Related remain.
GitHub's explicit earlier limitations and 100-total-candidate query bound remain.
Preserving those paths does not introduce another provider or UI parity requirement.

Native help/version/skills, fresh init, named enrollment/credential acquisition,
binding create/list/status and read-only native write evidence remain available.
The native registry, actual binding factory and generation/unknown-write guards
are retained, not the legacy identity-change eligibility graph. Recorded actor and
pinned target status is not freshly authenticated evidence. GitHub App login/status
is not verification of the pinned gh actor; GitHub inspection never falls back to ADO.
Pinned workspace identity/target switching, migration/default transitions and resets
are not supported.

Program startup no longer composes Browser separately, reopens its runtime with an
empty argument list, launches MCP or installs automatic companions. Legacy command
fallback resolutions are removed, not hidden behind missing DI. Retired root leaves
explicitly refuse without resolving a command. CAF/catalog admission refuses retired
verbs and captured envelopes before production composition or input/provider I/O.
The current help filters retired declarations; released/installable UI aliases and the
standalone TUI solution entry are withdrawn. Superseded UI source is deleted; older documents remain historical context.

Retired surfaces include quick new, Browser/TUI/MCP and captured draft/edit/review/save,
tree-set/full backlog/live/sprint/team/area UI, old policy assessment/repair,
native history/layout/behavior export, proposal latest/seed/old reconcile, migration,
reset/reinitialize and legacy shell presentation. Use seed new and explicit Core file
proposals instead. Finite delivery does not mean those surfaces were ported.

`connection writes` is inspect-only. ADO reads the existing native registry through
binding authority, without work HTTP or old seed/plan/eligibility constructors.
GitHub uses the same command owner for bounded original intent/ack sidecar inspection:
store instance, immutable connection, actor, digest, ordinal, original attempt,
canonical request hash and retained Core request must agree. Duplicate JSON,
orphan/oversized/redirected evidence and invalid hashes fail closed. Missing
acknowledgement stays unknown. An acknowledgement is only correlated transport
evidence, never Core Verified execution. Core proposal status/journal remains the
execution authority. Inspection performs no settlement, replay, adoption, expiry,
digest rewrite, fence clearing or evidence cleanup.

The tracking Epic and original unknown Feature publication remain open. Parent review,
integration and one phase-5 PR follow this code qualification; this contract does not
claim operational close-out.

### Phase-6 source and dependency boundary

The actual Domain source/project and references are removed from the solution,
CLI, Infrastructure, tests, public API inventory and release packing. The obsolete
TestKit, legacy ADO/cache/process/plan/provider/mutation graphs and their superseded
tests are deleted. This is not a renamed Domain library, a disabled build, or a
recreation of old interfaces solely for absence assertions.

Only host-required contracts change ownership: authentication and release lookup,
native provider target/binding records, attachment provenance, and declared-policy
data. Historical seed correlation retains its original serialized identity/tag
shape for inspection but cannot admit a new legacy create. The registry retains
native identity/generation/lease and original request/observation/receipt evidence;
obsolete claim executors and profile-cache readers/writers are absent while their
historical tables and safety fences are not erased. Core/provider/store source and
semantics stay unchanged, with one result/error family and digest-bound journal.

Real compiled CLI and Infrastructure PE regressions independently inspect assembly
and type references, including an exact phase-5 binary red fixture. The separate
`tools/verify-domain-cut.py` check inspects current source, project/build references
and public APIs; source absence alone is not binary/runtime proof. All existing
retained CI method groups remain required. The external .NET 10 sample now directly
uses the public RenderTree boundary and preserves typed, null, absent and complete
caller values without native services. These code boundaries do not close the
tracking Epic, merge a PR, or settle historical unknown publications.

### Historical qualification notes

The tranche-by-tranche boundaries below record earlier qualification and the safety
evidence behind retained paths. Their references to unfinished UI/parity are historical:
the closed catalog above retires those surfaces rather than promising a later phase-5 port.

`Twig.Core` is an additive, standalone model and pure-function library in the supported
solution. Production cutover is in progress. An explicitly initialized, pinned Core ADO
workspace now routes explicit-target `show`, `update`, `note`, artifact links,
single-hop `state`, `batch`, typed `patch`, and file-scoped `proposal preview/apply`
through the shared Core-only composition and application workflow. Other commands,
TUI and Browser are not fully cut over; this is not yet the
completed supported phase-5 surface. Phase 2 adds `Twig.Store.Sqlite` as an isolated persistence adapter,
not a provider/command cutover, compatibility layer, or claim that the old domain is removed.
Phase 3 adds the standalone `Twig.Providers.Ado` adapter and durable one-effect execution
over `IStore`; live command/native authentication wiring remains unchanged.
Phase 4 adds standalone `Twig.Providers.GitHub`, referencing only Core and using
the same durable store contract. It does not rewire commands or remove legacy code.
See its [authority, capabilities and practical limits](../../src/Twig.Providers.GitHub/README.md).

GitHub binds immutable API origin, owner/repository IDs and selected native actor,
never mutable repository locators. It implements bounded exact-AST reads, dynamic
API schema/type acquisition, native hierarchy/dependency effects and explicit local
missing-semantics/Related enrichment with durable provenance/tombstones. Runtime
denial never changes native transport to local. Opaque observations and explicitly
non-atomic requests never become exact revisions or atomic field batches.

GitHub process/workflow rule discovery remains explicitly Unsupported. The user
has approved the narrower validation projection: native API constraints plus
explicitly configured Twig policies, without claiming to validate external
repository automation. Host rule evidence names this scope and binds the actual
connection, actor, native fingerprint, policy identity/revision and each type.
An absent optional declaration means no declared local rules, not an empty
external ruleset or model-declared completeness. The new host acquisition reads
actual policy bytes and translates the supported mechanical rules; their
application enforcement is in the shared Core workspace workflow. The narrow GitHub
CLI profile now holds the actual native generation lease and admits canonical request
bytes from the retained proposal. Complete retained-surface routing remains unfinished.

Phase 5 has also added the native ADO access/admission bridge with exact
digest/ordinal/attempt attribution and creation fences in the existing native
registry. It holds a real native binding/generation operation and refuses
token-only access or missing durable Applying intent. The rooted ADO slice has
actual CAF/production-DI coverage, but these changes are not a claim that every
retained command, UI surface or phase 5 is complete.

Fresh ADO checkouts initialize this composition with
`twig init <org> <project>` at `.twig/core.db`, or an optional
`--core-store <new-absolute-file>` with an existing parent directory. The selected native
binding lease supplies collection, project and principal identities; the new
SQLite instance is pinned to that exact connection. Existing `.twig` directories,
existing selected files and reset flags are refused, not migrated or adopted.
Uninitialized/legacy CLI mutation admission refuses before legacy writer registration
with this preservation and initialization guidance. Preserved legacy proposal/journal
inspection does not authorize legacy application. Fresh GitHub worktrees can use
`twig init --provider github --host github.com --repository owner/name --account <gh-account>`.
This explicitly observes the selected native actor and immutable owner/repository,
attaches that target and pins a new store; default and explicit-path init use this same native host.
The explicit account selects the existing `gh` credential source; neither ambient
token variables nor the currently active account supply authority.

Initialization no longer runs a second legacy application. Production composition
branches on `init` before workspace runtime admission and the no-marker graph,
registering only paths/configuration, native binding bootstrap and rendering plus
the existing command. This also handles existing/corrupt Core markers as explicit
fresh-state refusals, not missing DI services. The same initializer retains native
ADO lease/actor/generation checks and GitHub immutable-target/account checks.
Only necessary native worktree attachment/registry metadata is initialized; no
legacy item store, managed/profile business initializer, process/field sync,
global profile, HintEngine, cache prefill or companion download
belongs to this path. Minimal local `.twig` ignore protection is retained without
global or repository Git configuration changes; custom external-store ignore risks
are reported explicitly. Retired reset/sprint/area/profile options are explicit usage
refusals. Existing manifest/policy bytes remain intact. Interrupted creation retains
evidence and refuses silent reuse. This finite initialization cut does not remove
the remaining no-marker catalog or claim phase-5 completion; Domain/source removal
is later work.

### Qualified GitHub CLI spine

The qualified native profile is process/schema inspection, explicit unparented
seed creation/publication, cache-only numeric `show`/`show-batch`, explicit numeric
refresh, typed `update`/`patch`, Markdown `note`, native `state` and supported `batch`,
remote-draft `save`/`discard`, bounded title/body/state queries, Bench list/create/switch,
single-item pins and static workspace tables. This is native identity and field-role
qualification, not old UI/envelope parity or completion of phase 5.

CLI integers are locators, not GitHub keys. The read-only `github.number` integer
comes from actual native `Issue.number: Int` metadata and observations; Core keys
retain opaque issue node IDs. Cache-only resolution requires one exact observed
number and performs no credential acquisition, schema or work HTTP. A miss requires
explicit refresh. Explicit refresh and number-targeted native mutations, including
cache hits and explicit draft save, requalify `issue(number:)` inside the immutable
repository, check owner/repository/kind/number, then completely observe the node again.
Missing or duplicate metadata and changed number-to-node bindings refuse. Original
UUIDs, permanent mappings, proposals and history are never rewritten as numbers.
Number lookup compares both sides of the captured cache binding before complete
acquisition and commits its mirror only against that capture's store CAS. A mismatch
does not refresh the cache, retain a proposal or dispatch an effect. Historical
cache-only reads remain available without claiming fresh native authority.

Title/body/assignee roles use the native field references and kinds; state comes from
the discovered type. GitHub body/comments retain Markdown, and default seed assignees
are native actor-ID lists, not logins or ADO identities. Native creation's OPEN-only
allowed rule supplies initial-state inference. Non-atomic concurrency is explicit in
every GH effect and journal; it grants neither exact revisions nor atomic close gates.
Already-required populated fields are not newly introduced completion gates. Newly
required unsupplied fields still ride atomically with a completion transition.
Pre-requiredness is evaluated only for an unsupplied completion-transition candidate;
ordinary edits and post-supplied requirements use their understood effective values,
not an invented obligation to observe every pre-condition operand.

The canonical `ws` alias uses the same static workspace owner. Version and help remain
offline utilities. Machine qualification refusals are one format-aware error, including
failures during CAF service composition. Quick `new` remains unqualified; use the
explicit `seed new` profile instead.

Tree/navigation, editor, chain/creation links, native CLI links, resource coupling,
sync/context expansion, browser/TUI and other unconverted families explicitly refuse
before provider HTTP or a legacy writer. Type/assignee/path/date query spellings are
also refused. Queries retain the existing **100 total candidates** bound, with explicit
refusal of further pages; no large-repository completeness is claimed.

### Qualified Core local Related CLI slice

The existing `LocalRelationshipCommand` now owns only the Core path:
`link local related <target> --id <source>`, `unrelate` with the same shape,
and explicit-target `link local list <target>`/`--id <target>`. Native numbers,
negative seed aliases and original canonical lowercase GUIDv7s replace the old
two-GUID positional/`LocalWorkItemId` registry UX. There is no active fallback.
Machine GUID `--id` admission is restricted to this local family; other commands'
integer targeting is unchanged.

The mutation owner uses actual discovered symmetric Association semantics and
the existing typed provider capabilities. Exactly one local-only, non-native
kind is required. ADO native Related/local-empty refuses; missing/denied schema
or native read evidence never selects local fallback. Only these three local
GitHub subcommands are newly qualified; native GitHub `link` remains unsupported.
Mutations may acquire native credentials, schema and read observations but send
zero native mutations. List performs zero total HTTP and changes no store,
journal or navigation state.

One existing proposal/op binds exact local edge CAS through shared
Prepare/Apply/Verify and the retained journal. Initial add expects no row;
remove/resurrect bind its exact positive version, including physical tombstones.
Duplicate/absent effects refuse instead of faking verification. Output is static
typed RenderTree with `LOCAL`, `local-only`, `non-native` provenance; Verified
means local CAS only. List IDs require explicit uniquely cached neighbor IDs.
JSON edge endpoints are typed original identity objects (`localId`, or
`provider`/`scope`/`nativeId`), not C# record debug text or number-derived keys.
Human/minimal display uses concise UUID/native-ID text.

Original Verified create mappings provide logical read/dedup after publication,
but existing edges always mutate their original UUID keys and local version.
Publication leaves local overlays in place rather than demanding native creation
coupling. Minimal pure validation admits a settled UUID only with its original
Verified create/mapping and an in-scope observed native endpoint; unknown,
unverified or historical-only UUIDs remain refused. Mapped native endpoints are
reacquired for provider validation without rewriting durable edge identities.
Unresolved effect fences apply across mapped and original endpoints/new digests.

Deletion map: the old owner constructor, legacy singleton registration and
owner-only legacy store/descriptor/outcome tests are removed; the same owner is
registered only in native Core composition. `LocalRelationshipService`, old
stores and UI/MCP source remain outside this proven CLI cut and are not registered
in the Core writer graph. No Core types/ports, schema/format versions, facade or
CoreCommandAdapter method were added. Core remains 56 types/two ports/format 3;
this is not phase-5 completion or a UI/native-link qualification.

One provider-owned pure `GitHubWriteIntent.Project` projects final mutation documents
and input bytes. Provider shape validation retains live capability/resource/edge
checks; native host admission compares exact UTF-8 bytes against that same projection
of the **retained** proposal/ordinal/attempt. Native fsynced intent/ack sidecars are
subordinate transport evidence, never a second executor or recovery authority.
Unknown creation remains fenced across root reopen without a second create.

Offline Core file preview retains exact unconfirmed intent as Planned with no
attempt or receipt, and reports unavailable schema/rule evidence as blocking.
Core `proposal validate` (and its `plan` alias) checks only canonical file format
offline and explicitly reports that application evidence was not checked; it does
not imply process/policy compliance or permission to apply. `proposal status` reads
the current file's exact digest from the same immutable-connection store, reports
Planned/attempt/receipt states including uncertainty, and never imports, settles or
replays. An absent journal is a nonzero, explicitly absent result, not an invalid-file
or successful execution claim. Original journal rows remain retained if file bytes
change; status never substitutes another digest. `pending` projects every captured
typed draft, original field presence/values, local UUID and frozen creation-bound
parent/resources/links, without native refresh, journal changes or implicit flush.
It preserves retained draft order, not a fabricated field-edit chronology. These
inspection routes support human/structured/minimal output and safe optional command
telemetry; bare-ID output is refused because it would discard the inspected evidence.
Proposal latest/seed/reconciliation and rich interactive review remain cutover gaps.
The fundamental smoke uses an isolated Core fixture and reaches that real
evidence gate plus wrong-principal admission; initialization refusal is not a
substitute. ADO `batch` fields/state and an authored note are one revision-tested
request. GitHub refuses that combined shape rather than silently losing its note.

Explicit `save <id>` and `save --all` now select remote Core drafts, freeze one
ordered proposal for the invocation, execute through that same journal, and consume
only unchanged captured drafts after every effect verifies. Local seeds remain a
distinct publication request. A partial/unknown outcome retains drafts and receipts;
later local edits prevent settlement rather than being silently cleared.
`discard <id>`/`--all` uses the reviewed local store CAS and draft content, preserves
seeds and every journal/fence, and requires `--yes` without an interactive confirmation.
The retained Delete verb refuses its already disclosed unqualified transport instead
of resolving the old delete writer. Cache-only `show` reads the Core mirror without
schema/work HTTP; missing cached items require an explicit `--refresh`. Active scope,
sync and the other remaining read/UI families are still incomplete. Cached
`show-batch` reads exact supplied targets and reports missing IDs with a nonzero
exit; neither missing items nor field export cause implicit work/schema HTTP.

Unparented `seed new --no-parent --type ...` now stages UUIDv7 drafts through Core.
The initial state comes from the adapter's existing native initial-state allowed
rule, not a guessed process category; the default assignee is the admitted native
actor. `seed view` and human `seed publish` use the same workspace and ordered
proposal/journal orchestration, not a separate publisher. Verified creation preserves
the original UUID and permanent native mapping. An unacknowledged create keeps the
draft and indeterminate UUID fence; another publication cannot recreate it.

The fresh Core host derives negative integer presentation aliases deterministically
from local UUIDs; UUIDs remain identity and all retained drafts, mappings, proposals,
relationships and Bench selectors reserve their aliases. Collisions refuse ambiguous
targeting rather than choose an item. `show --local-id` reads UUIDv7 drafts/mappings
cache-only; negative alias detail continues to resolve after publication. This is a
host compatibility strategy for new explicit stores, not a migration of old aliases.
Drafts now retain optional typed creation-bound parent/relation/resource intent.
The existing canonical `Op.Other`, `Relation` and `Resource` payloads bind those
effects without another Core port or a proposal/store version change. Older Core
drafts contain no such declaration; older local relationship intent remains
separate and is never reinterpreted as a native create instruction.
ADO parent/resource effects share the create POST and are independently read back.
GitHub refuses creation coupling not proven by its native schema; nothing is omitted.

Explicit parented seed creation and ordered local-parent publication now use this
path. A local parent UUID resolves only through its original permanent mapping
after the preceding creation's acknowledgement is separately Verified. Retained
proposal bytes keep the UUID; neither a guessed native ID nor revision arithmetic
is used. Branch artifact capture now observes native immutable Git project/repository IDs
and the exact requested branch through the same leased ADO identity. It freezes
the qualified artifact into unchanged seed intent before proposal construction,
including dry-run intent; there is no best-effort post-create artifact writer.
Missing/ambiguous/paginated native metadata refuses without publishing.
Their model now captures a frozen `CreationLink` list with exact endpoint,
discovered canonical relation and direction. The optional nonempty canonical
`links` array binds list order and values; an empty list preserves existing bytes.
File parsing rejects malformed tuples and duplicate links. Validation requires
the exact retained draft capture, declared native creation coupling, complete
remote observations or an earlier captured local creation, and refuses links on
non-create effects. ADO emits each discovered canonical forward/inverse relation
in the same creation POST, resolves local endpoints only from original Verified
creation mappings, and checks every declared edge at the acknowledged immutable
revision and in complete reciprocal current observations. Missing readback remains
Partial; unknown creation stops all later effects. `seed link`, `seed unlink`, and
`seed links` now capture/list that exact draft intent through Core; parent replacement
and dependency publication ordering use one CAS-protected draft representation.
Cycles refuse before capture, and branch/field capture and proposal settlement retain
the frozen list. The production-root regression publishes a three-seed dependency
chain with an association and branch resources without a post-create writer.
GitHub still refuses unproven atomic creation-link coupling. `seed chain` now authors
the same typed drafts and predecessor links with an explicit parent/type, emits one
structured result, and preserves any staged prefix after an authoring failure.
`seed validate` acquires actual schema and declared-policy evidence for the ordered
captures without importing or dispatching a proposal. `seed reconcile` recovers only
existing Applying/Applied creation rows and settles unchanged captures from their
original Verified mappings; it never applies Planned successors, rewrites UUID
references, guesses native identities or replays an unknown creation. Human active
parent/type inference is still part of the unfinished context cutover.
Explicit seed discard now checks the exact draft/store
capture and refuses any attempted UUID or permanent mapping, even with `--yes`;
unattempted abandonment preserves journals and local relationship tombstones.
`seed new --editor`, `seed edit`, and explicitly targeted updates of unpublished
local aliases now use typed Core draft capture. The existing section editor format
is shared as a pure formatter; the native type's fields supply its editable
vocabulary. Duplicate/unknown/read-only sections and invalid typed values refuse.
Unchanged missing/null/empty values are not materialized, creation-bound links
survive field edits, and activation-capture/store CAS rejects concurrent overwrite.
An attempted creation refuses editing rather than replacing its UUID fence.
The native general editor now captures its actual observed baseline before launching
the existing editor, stages one typed draft against the activation store CAS, and
merges again before digest-bound application. A genuinely concurrent native field
change is retained as a conflict without retry or overwrite. Human `set` and active
selection use a separately persisted, source-generated host context bound to the
admitted connection and SQLite instance, not the old context/writer database.
Structured selection and active-dependent mutation remain refused; human update,
edit, show, note, save and state can resolve that selected identity. Incompatible
context is preserved and refused rather than adopted. Human hierarchy navigation
(`up/down/next/prev`, including `nav` aliases) now discovers the canonical hierarchy
and selects from the Core mirror plus explicit creation-bound parent intent.
History back/forward retain the original local UUIDs and never change proposal
identity; structured history inspection is read-only. None of these routes registers
the legacy navigation/context repositories or tracker writer. Interactive navigation,
native work-item history, compact detail sections and
complete lifecycle/UI retention remain unfinished. These gaps prevent phase-5 delivery.
Cached `show --tree` and targeted `tree` now compose the actual Core mirror,
ancestor chain and local creation-bound parent declarations through the existing
typed tree renderer. Missing cached descendants are explicit placeholders; native
relationship completeness and local origin are not fabricated. Depth counts below
the requested item, and repeated descendants/cycles refuse. Schema semantics come
from actual provider discovery; default tree rendering does not fetch work items.
Refresh-all, live UI refresh, working-set annotations and compact detail-section
projection still require their remaining Bench/read/UI cutover.

Retained `query` now builds typed Core operands, acquires a provider-observed
superset, and caches native observations without flushing drafts or using the old
WIQL writer composition. Case-insensitive text containment, path-component `Under`,
and numeric/instant `GreaterOrEqual` are explicit Core query operators; ordinal
typed `Equal` remains unchanged. Configured area scopes are retained, unavailable
native fields refuse, and top truncation is disclosed. ADO now acquires ordered
keyset pages of at most 200 item observations, using a 201st ID only as a
continuation sentinel. Cursors bind the exact connection, actor, schema and
canonical query bytes. Exact matches can come from later pages; each mirrored
page requires complete incident/resource evidence and preserves local drafts.
These observational pages are not a globally atomic snapshot. GitHub's existing
100-item acquisition bound remains an explicit limitation pending its host/query
completion; this is not yet the complete retained query, Bench and sync surface.

Core Bench create/list/switch and workspace single/subtree pinning now use
`StoreSnapshot.Benches` and the connection/store-instance-bound host context.
Creation does not select the new Bench; duplicate names and unknown selections
refuse without substituting another scope. Selector updates use the store CAS
and preserve queries and other pin kinds. Untracking is case-insensitive, removes
both explicit pin kinds when mode is omitted, and reports an absent pin as an
explicit unchanged result without advancing the store version. Removing a
subtree selector does not require current hierarchy discovery. Pinning an
uncached target reselects the current captured-ID Bench after the native read,
so concurrent selectors are not overwritten. Browser origin/settings envelopes
and the complete workspace/UI surface remain unfinished.

Core `workspace sync`, `sync --pull-only` and the retained `refresh` alias now pull
the selected Bench's captured query/pin/subtree scope. Existing draft debt is
always protected; native mirrors can refresh without rewriting draft baselines,
changes or journals. Machine pulls do not add the interactive active item or its
context. Human pulls use configured ancestor/child/sideways depths. Unknown
hierarchies, cycles, incomplete observations or changed Bench selection/selectors
refuse a complete-pull claim while preserving earlier cached observations.
Normal Core `sync` saves remote drafts through the same ordered proposal and
verification path as explicit `save`, then pulls the selected scope; it does not
publish local seeds. It emits one machine result rather than streaming separate
save/pull documents. Missing or stale selected scope refuses before draft
publication. Optional sync telemetry contains only generic command metadata and
cannot change command outcomes. Remaining workspace rendering, captured browser
admission, GitHub host and UI routes still prevent phase-5 completion.

The retained artifact-link path now has a typed `LinkResource` effect and
separately certified `WorkItem.Resources` observations. A URL is a locator, not
an invented work-item identity. The ADO adapter qualifies actual enabled native
Hyperlink/ArtifactLink shapes and sends one revision-tested JSON-Patch request;
exact attributes, acknowledgement, persistence and separate readback use the
same journal path. Remaining artifact command/UI wiring is still unfinished.

Feature publication remains operationally unresolved: the reviewed six-phase proposal's
first creation has an indeterminate outcome without an acknowledged native Feature ID.
The existing Epic temporarily tracks authorized implementation. This design neither settles
that attempt nor permits its replay. The Epic stays open.

The current SDK comes from `global.json` (.NET 11 RC3 at this baseline). Core follows the
shared-library convention: `net10.0;net11.0`, `IsAotCompatible=true`, nullable references and
warnings as errors. It has no project dependencies, reflection-based JSON, host services,
credential access, provider names, process-template assumptions, database access or UI types.
The executable keeps its existing NativeAOT, full-trimming and invariant-globalization settings.

## Layer ownership

| Owner | Responsibilities |
|---|---|
| Core | Identity, typed values, one schema, drafts, canonical proposal intent, receipts and journal transitions, mechanical validation, merge, Bench selectors, declared policy, two ports |
| Application/command orchestration (later) | Explicit targeting, acquire current schema/items, draft-to-proposal construction, exact-digest confirmation, ordered execution, durable attempts, cancellation/recovery, policy evidence acquisition |
| Provider adapters (phases 3 and 4) | Normalize native identities and field references, discover schema/capabilities, implement query translation and one-effect transport, classify dispatch/outcomes, authoritative readback |
| Store adapter (phase 2) | SQLite/WAL, one local CAS/transaction boundary, append-preserving evidence, immutable publish mappings, local relationship tombstones, source-generated persistence DTOs |
| Hosts/rendering | Human versus machine behavior, navigation/context, form layout and presentation hints, markup conversion/sanitization, colors, terminal/browser interaction, credentials and telemetry |

`ProcessSchema` is a model returned by discovery, **not another port**. There are exactly
two ports: `IProvider` and `IStore`. Optional clock/identity services are unnecessary here:
pure functions accept explicit observations and callers supply durable UUIDs.

The initial inventory is **54 distinct declared core types** (including enums, ports,
the internal contract helper and one partial pure-function class), approximately 1.3k
nonblank C# lines. This is measured across `src/Twig.Core/*.cs`, counting each partial
type once, not hiding nested cases or counting the old Domain as removed. The roughly
45-type target and fewer-than-60 / roughly-5k-line guardrails leave room for safety
without reproducing the old per-operation result families.

Phase 5 adds `ResourceLink` and `CreationLink` records, bringing the current inventory to 56.
There are still exactly two Core ports. Host admission and policy-acquisition
types live outside Core rather than expanding that port surface.

There is **no preserve-all acceptance requirement** for phase 5. Retention of
TUI, Browser, companion and Claims/coordination remains undecided. This bounded
CLI correction authorizes neither new UI parity/Claims work nor deletion inferred
from silence. The user's updated guidance is that UI/UX is **potentially forfeit**
and a redesign candidate: presentation contracts, screens, DTO/envelope/layout
fidelity are not blocking acceptance criteria for this CLI replacement. Essential
capabilities and safety remain required. This is not blanket authorization to
delete UI, data or history; final removals and replacement designs must be stated
explicitly later. Existing legacy UI dependencies still block whole-Domain removal;
MCP remains withdrawn pending its separate redesign.
The ADO/GitHub adapters' already disclosed unsafe/unqualified Delete limitation
must remain an explicit capability refusal, never a fictional strict revision
precondition or a reachable alternate legacy writer.

### Deletion-first CLI correction

The existing `ProcessCommand` and `ProcessDescriptionCommand` now inspect the same
admitted Core workspace's live `ProcessSchema`, without legacy metadata stores,
work queries, sync, pending flushes or writers. `process` lists canonical type
references and exposes discovered states, fields and exact transition edges, not
an invented all-pairs graph. Human `states` resolves only cached Core active context;
structured callers must name a type with `process`.

Descriptions use **`core-schema-1`**, scope `provider-discovered-core-schema`, with
the schema fingerprint and explicit known gaps. This is intentionally not the legacy
0.1 completeness contract: field display names, colors, category membership,
behavior metadata and native form layout are not represented. Conditions, typed
rule operands/values, disabled rules and provider discovery gaps remain visible.
`--sections fields,requirements` and field filtering project that same schema;
unknown selectors/fields refuse instead of silently dropping intent. All JSON aliases
carry the projection. `--out` replaces a file only after discovery and rendering succeed.

Native layout is not in Core schema. `process layout` on a Core workspace explicitly
refuses before HTTP rather than failing DI or fabricating an empty layout. Pinned
Core `--org/--project` overrides also explicitly refuse before HTTP; initialize a
fresh isolated workspace for the requested target. Legacy/unpinned schema inspection
refuses with initialization guidance; legacy native layout reading is unchanged.
`--refresh` remains accepted, but Core schema inspection already discovers live metadata.

| Existing Core route | Deleted CLI ownership |
|---|---|
| `CoreCommandAdapter.StateAsync` -> shared Core proposal/journal workflow | `StateCommand.cs`, its singleton registration and Program fallback |
| `CoreCommandAdapter.NoteAsync` -> shared Core proposal/journal workflow | `NoteCommand.cs`, its singleton registration and Program fallback |
| `CoreCommandAdapter.PatchAsync` -> shared Core proposal/journal workflow | `PatchCommand.cs`, its singleton registration and Program fallback |
| `CoreCommandAdapter.UpdateAsync` -> shared Core proposal/journal workflow | `UpdateCommand.cs`, its singleton registration and Program fallback |
| `CoreCommandAdapter.SaveAsync` -> shared Core proposal/journal workflow | `SaveCommand.cs`, its singleton registration and Program fallback |
| `CoreCommandAdapter.DiscardAsync` -> local typed-draft CAS, preserving seeds and journals | `DiscardCommand.cs`, its singleton registration and Program fallback |
| Existing `LinkCommand` -> shared `CoreWorkspace.PrepareAsync/ApplyAsync` | Replaced its direct writer, active resolver, link repository, sync coordinator and hardcoded native hierarchy reference; removed superseded mock-based CLI-owner tests |

Only superseded command-shell tests were removed/replaced; no CLI public API
baseline entries existed for these wrappers. `CoreMutationAdmissionGuard` continues
to refuse unsupported legacy workspaces before old writer registration. Shared
`StateTransitionWorkflow`, `NoteWorkflow`, `PatchWorkflow`, conflict handling and
note-flush helpers remain: other consumers, including the withdrawn MCP draft,
still reference them. `FieldUpdateWorkflow`, `DiscardWorkflow` and
`PendingChangeFlusher` remain shared; in particular `SyncCommand` still uses the
flusher. Mixed Edit/prompt tests retain their unrelated cases. UI ownership is
outside this deletion; removing these six CLI wrappers does not authorize deleting
their shared workflows or resolving legacy writers into the Core graph.
The Core inventory stays at 56 types/two ports and store/proposal format 3.

The same native `link` owner is now registered in the pinned Core graph, not a
new adapter family or parallel facade. Association add/remove, predecessor and
successor add/remove, unparented parenting and single-parent unparenting discover
one unambiguous canonical shape. Forward dependency is blocker -> blocked;
hierarchy is parent -> child. The actual forward source's observed opaque revision
binds one `Relate`/`Unrelate` request. Native schema/declared-policy acquisition,
immutable acknowledgement readback and fresh reciprocal endpoint certification
use the existing workspace/provider journal workflow. Mirror refresh preserves
local edges and tombstones. A duplicate add refuses rather than claiming new
verification; partial and indeterminate effects fence both endpoints under new
digests without replay. Invalid/self/conflicting targets refuse with usage exit 2;
machine invocations need an explicit `--id` option. Human ambient selection uses only
the bound Core host context, resolving an original local UUID through its permanent
store mapping after publication without rewriting UUID history or creation intent.
Unmapped/unknown seeds still refuse; the shared proposal path retains authority
over partial/unknown native endpoint fences. All JSON aliases emit one document; bare IDs refuse
because they discard journal evidence. Optional telemetry contains static safe
command metadata, never endpoints or native references.

Correction to the earlier `c3446544` qualification: accepting `--child` alone
for machine writes was incorrect. Every non-human mutation requires `--id`;
`--child` and the positional child are human conveniences or matching cross-checks,
never alternative machine authorization. Missing machine `--id` refuses with
usage exit 2 before HTTP, including for a valid `--child`.

`link reparent` remains an explicit, pre-HTTP **unsupported gap**: the existing
typed effect cannot express a safe single-request replacement. The old remove-
then-add implementation was not atomic and has been deleted, not reproduced as
two journal operations. `link related --comment` likewise refuses before HTTP,
including an empty supplied value, because the typed relationship effect has no
comment payload. Unknown/ambiguous shapes, seeds, GitHub CLI relationships and
local-relationship/UI routes are not qualified by this slice. No Core types,
ports, operation kinds, versions or `CoreCommandAdapter` methods were added.
Shared legacy writers/sync/link repositories still serve other retained consumers;
they were not deleted or registered in the Core graph. This does not complete phase 5.

## Bounded CLI workspace-read correction

The existing `WorkspaceCommand` now owns a small static Core Bench read, registered
in the Core production DI branch. `workspace`/`ws` default to a cached item/draft
table through existing RenderTree renderers; provider schema metadata is still
acquired explicitly because `StoreSnapshot` has no persisted schema. No default
Bench is guessed or created. `--bench` reads an existing name/GUID without
switching context; `--expect-bench` guards that resolved GUID before HTTP.
Core `ResolveBench` and typed `Matches` remain the domain engine. All protected
draft debt, including local seeds, is added to selector membership. Query matching
uses cached values; display overlays keep typed proposed values separate from cached
fields, with no draft-baseline rewrite or false native freshness claim.

Permanent mappings project selectors and relationship endpoints only in memory.
The exact existing `KnownLocalKeys` and `LocalAlias` functions moved verbatim from
`CoreCommandAdapter` to existing host owner `CoreHostContext`; both commands reuse
the same reservation/collision rules. No Core type, port, schema or format changed.
The adapter did not gain another workspace method or workflow facade.

`--refresh` calls only existing `CoreWorkspace.PullBenchAsync`, freezing the current
Bench GUID and using the shared factory depths with active context disabled.
Named noncurrent refresh refuses without changing selection. Default reads do no
work-item HTTP/query/sync/flush. Every format excludes implicit active context,
but explicit Bench membership and draft debt still win. Missing/ambiguous scopes,
missing pins, unavailable query fields and incomplete/cyclic subtrees refuse
without broadening. JSON declares `core-bench-workspace-v1`, not browser parity or
complete server membership; `ids` emits only row IDs.

Deletion map: replaced `Commands/WorkspaceCommand.cs` aggregate/WorkingSet,
SyncCoordinator, legacy HumanOutputFormatter/live/browser ownership and its
legacy DI constructor/property initialization; deleted
`Commands/WorkspaceCommand.BrowserV2.cs`, `WorkspaceCommand_TreeTests.cs` and
`UserScopedWorkspaceTests.cs`. Removed only superseded workspace-owner cases from
mixed cache-first, durable-overlay, structured-isolation, section-wiring and
refresh tests. Shared renderer/configuration cases and targeted `show --tree`
tests remain. `BrowserOriginGuard`, formerly embedded after WorkspaceCommand,
moved unchanged to its own host file because Tracking/Bench/browser operations
still consume it. Shared legacy Browser/TUI, WorkingSet, sync, formatters, stores,
fences and history remain; none were pulled into the Core graph.

`--all`, sprint/full-backlog tree, `--view tree`, browser envelopes and captured
browser binding/identity/settings flags explicitly refuse before HTTP. Only static table
spellings (`--no-live`, `--flat`, `--view table`) are accepted. Targeted
`show --tree` and `workspace sync` are unchanged. GitHub CLI workspace reads and
UI redesign are not qualified by this correction; phase 5 remains unfinished.

## Identity, values and drafts

`ItemKey` has exactly two forms: a durable local UUID, or a remote
`(ProviderId, resourceScope, nativeId)`. Every component is ordinal and case-preserving
after adapter normalization. Scope must be sufficient to make native IDs unambiguous;
a repository-local number cannot be used with a merely host-wide scope. Display names,
URLs, aliases, negative seed integers and mutable repository locators are not identity.

`Connection` binds provider, resource scope, immutable creation destination and stable
authentication identity reference. It contains no credential material. Adapters resolve
locators and authenticate the bound identity before transport, refusing a moved or
ambiguous target instead of substituting a current account or another repository.

`Revision` binds an opaque nonempty token to one exact remote key. Core never increments,
parses, orders or fabricates tokens. Missing means unknown, not zero; local store versions
and relationship CAS counters are separate. A provider with an observational token does
not thereby support atomic revision checks.

Publication retains `PublishMapping(Local, Remote, digest, ordinal)` permanently and
one-to-one. The UUID survives as an alias/provenance key after publication. Stored proposal
bytes and journal targets are never rewritten to the published ID. Pins, draft links and
other local references resolve through the mapping when the application builds later intent.

`FieldValue` is a closed tagged value with null, text, HTML, Markdown, integer, decimal,
boolean, offset-bearing instant, native identity, ordered string list and opaque raw cases.
It uses no `object` payload or reflection polymorphism. Lists/maps are defensively copied.
Null is an explicit clear/observed absence; an absent field key is unobserved (or unchanged
in an edit), distinct from null and empty text. Raw carries its provider type losslessly
but cannot be written through mechanical validation.

A `Draft` is either a local seed with no remote base, or edits against an immutable,
revision-bound remote `WorkItem`. The staged value, not the cached mirror, is local intent.
Merge adopts remote-only edits, retains local-only edits, accepts convergent changes,
and reports genuine double edits. Missing base/remote field evidence for a staged edit
is a conflict, not a guessed empty value. Equal provider revisions never discard staged
intent. Merge returns proposed fields and conflicts; it never pushes or rebases intent.

## One schema and schema-driven validation

`ProcessSchema` holds stable type references, field definitions, states/categories,
allowed transitions, conditional field rules, relation semantics, completeness gaps and
a canonical fingerprint. State field reference names come from each discovered type.
No custom field, state name or process template is built into Core.

Enabled field rules have conjunctive typed predicates: equal/not-equal against effective
post-state, was against pre-state, and changed/not-changed against both. Actions are
required, read-only, allowed values and supplied. Disabled rules remain represented but
do not fire. An unknown enabled rule/predicate or missing required observation is
`Unavailable`; unsupported provider rules must become explicit discovery gaps, not vanish.
A genuinely empty discovered ruleset is different from failed discovery.
Malformed state/field metadata and unsupported enabled rules block non-field effects
as well as field writes. Supplied constants must have the declared field kind; conflicting
active suppliers cannot invent a usable effective value.

Requiredness evaluates the effective post-batch value. A firing supplier can satisfy a
requirement, including provider-generated values that callers must not author. A disabled
supplier cannot. Allowed values retain their types; strings are not coerced into numbers.
Supplier effects are not guessed or executed by Core; a predicate depending on an
unobserved generated value remains unavailable.

State change and authored close gates are a **single `Batch`**, never two independent
operations. When entering a Completed-category state, active required fields without a
firing supplier must be explicitly present in that batch, even if an old cached value is
nonempty. A provider that cannot write this batch atomically must refuse it. All other
requiredness, read-only, value-kind and transition restrictions still apply. `Batch.Type`
may explicitly bind a replacement type; its target schema governs validation and the
adapter must include that type change in the same remote request.

`Validate` binds confirmation and the pre-import store version. Importing the proposal
itself advances that version; it does not invalidate its own consent. Before each first
attempt, `ValidateEffect` checks the retained Planned row, earlier Verified rows, current
observations and unchanged schema. Acquisition then uses the **current** store CAS.
It never resets the proposal's historical baseline version or bypasses the creation fence.

Core validation is advisory over supplied observations. The eventual application must
rediscover/read authoritative inputs before dispatch and enforce the provider's actual
revision guarantee; cache-based validation is not live authorization. Unknown schema
gaps block writes, even if the cached fields happen to look valid.

## Canonical proposals

A proposal is immutable and connection-bound. Version 3 hashes:

- The format version, provider scope, immutable destination and identity reference.
- The complete schema fingerprint and expected local store CAS version.
- Every operation in declared order, including ID, kind, typed values/body, explicit clears,
  revision and revision target, relationship endpoints/provenance/local CAS, type and concurrency mode.
- A create's durable local UUID, carried in its item key.
- A resource effect's exact relation, locator, nullable name and nullable comment.
- Paired configured-Twig-policy identity/revision; null/null denotes an observed
  absent optional declaration and must be rechecked against the actual source,
  never inferred from failed acquisition.

Version 2 introduced the resource slot; version 3 additionally binds configured
policy presence and bytes. Neither reinterprets an earlier digest. The SQLite
database/snapshot format now advances to version 3: old files are refused and preserved, never reset or
silently migrated. Explicit initialization must choose a genuinely new isolated
path while prior drafts, journals, creation fences and mappings remain intact.

The store has a genuine persisted file-instance UUID. Opening an expected
instance or using an already-open handle against a replaced file refuses.
Explicit host initialization pins the immutable connection before any snapshot
exists and records that exact instance; it cannot adopt an existing populated
partition. The production factory checks both marker and on-disk binding before
constructing the Core-only runtime.

`IProvider.ImportAsync` and `RecoverAsync` expose the adapters' existing common
operations through the same port, not new per-provider policy interfaces.
Capture-only reads suppress optional mirror commits during policy acquisition;
the application owns its explicit coherent mirror commit. The shared workflow
does not retry patches, flush pending notes or invoke the legacy seed publisher.

`Core.ProposalBytes` writes a fixed versioned UTF-8 JSON grammar directly with
`Utf8JsonWriter`; `Digest` is lowercase hexadecimal SHA-256 of those exact bytes.
Property order is fixed by that grammar; field maps are ordinal-sorted. Semantic arrays,
including operations and ordered lists, retain their order. Decimal scale is normalized
with invariant `G29`; integer versus decimal remains tagged. Instants retain offset;
strings, raw payloads and rich-text bytes are not normalized. Changing format rules
requires a new version, not quietly producing new meanings under old digests.

Schema hashing sorts types/fields/states/relations/transitions by ordinal identity, retains
rule order conservatively, and includes enabled/disabled rules and discovery gaps.
Returned byte buffers cannot mutate retained intent. Duplicate operation/field identities
are rejected at construction.

There is deliberately **no arbitrary JSON parser or wire serializer in Core**. Later
infrastructure DTOs must use source-generated `TwigJsonContext`, reject duplicate and
unknown members/versions, and construct these validated types. Reusing the legacy
`PlanCanonicalizer` would introduce an Infrastructure dependency and a second grammar;
neither old plan bytes nor old digests are silently translated.

## Provider execution and outcomes

`IProvider` exposes discovery, query, explicit-key read, apply of **one operation**, and
verification. Query/read envelopes carry item and relationship observations, continuation
and completeness. A missing/failed page is not an exhaustive empty result. An item's
`RelationshipsComplete` is a positive certificate for the incident relationship evidence
needed within that acquired scope, not an inference from a successful HTTP response.
Adapters must report contradictory/incomplete endpoint observations as unavailable.

Each apply call is one journalable remote effect/request, or a genuine atomic field batch.
It must not hide a multi-request sequence. If an adapter needs several requests (for
example GitHub edits with separate API calls), the application must expose their separate
effects before digest confirmation, or refuse that operation. `Create` binds only the
single-request initial shape; follow-up relationships/labels are later explicit effects.

Capabilities, not provider-name switches, distinguish native relation kinds, explicitly
local kinds, exact revision support, supported operations and atomic field batches.
The existing GitHub catalog declares native hierarchy/dependency support, but declaration
alone is not proof an adapter implements every operation. This phase neither expands
GitHub behavior nor silently emulates unsupported relationships. A local effect is
explicitly local in the proposal and receipt; it uses store CAS, never a remote revision.
Its `ExpectedLocalVersion` also binds the exact edge/tombstone: null requires no prior
local row, while an existing row requires its positive version. Internal journal commits
cannot invalidate this precondition, and another writer's edge change cannot evade it.

`ObservedNonAtomic` must be explicit in digested mutation intent when exact CAS is
unavailable. The adapter checks the observed token immediately before dispatch but
cannot promise the race is closed. Later readback can verify observed effects, **not
retroactively manufacture atomicity**. Independent fields may share one native
request with this explicitly non-atomic intent; the capability remains non-atomic
and readback must check every intended field. Type changes still require atomic
support, and an authored Completed-state gate requires proven atomic coupling
even when the fields appear in one non-atomic request. All creation explicitly declares this limitation:
neither current provider has a conditional-create/idempotency guarantee.

Operations retain their exact declared revision. There is no implicit carry-forward,
rebase, generated revision arithmetic or mutation retry. If an earlier operation moves a
later operation's revision, the later effect refuses and needs newly reviewed intent.
Dependent publication follows verified mappings; unbound local endpoints cannot masquerade
as remote IDs. Rich-text conversion must occur before confirmation, or the adapter must
send the exact format as declared. It cannot change body meaning after review.

Before any transport, the orchestrator atomically records `Applying` and its attempt UUID.
Concurrent acquisition uses store CAS; the loser sends nothing. `Result.Fail` at the
provider boundary means a definite pre-dispatch failure. Once dispatch may have happened,
the provider must return an evidence-bearing receipt: acknowledgement (`Applied`),
definite no-effect rejection (`Failed`), known partial effects (`Partial`) or unknown
outcome (`Indeterminate`). The error/evidence describe what is known; cancellation,
timeout, malformed responses and lost acknowledgements are not proof of no effect.
If the process dies before returning, the durable Applying row retains that uncertainty.

`Applied` is not `Verified`. Readback appends a receipt and advances Applied to Verified,
Partial or Indeterminate without deleting the acknowledgement. Failure to read back an
acknowledged effect cannot become `Failed`/safe-to-replay. Receipts bind digest, ordinal
and attempt UUID; an unrelated successful request is not evidence for this attempt.
Creates additionally retain attributable remote identity, not title-based matching.

After failure, partial or uncertainty, later operations do not start. No journal transition
in phase 1 resets a row to Planned or retries it. A stale Applying lease allows a later
recovery implementation to inspect authoritative evidence, **never** to assume non-dispatch.
The local UUID fence spans replacement digests. Native settlement, if added later, must be
an append-only evidence-backed decision with distinct semantics; retirement is not application,
and a negative search is not proof permitting another create. There is no settlement
shortcut or fallback provider path in this skeleton.

## Store transactions and durability

`IStore.ReadAsync` returns one coherent `StoreSnapshot`. `CommitAsync` compares the supplied
expected version and swaps one coherent next snapshot at version +1, or commits nothing.
This intentionally small first contract defines the unit of correctness, not a requirement
to issue full-table SQL rewrites. The SQLite adapter may compute a row delta internally.

The adapter must enforce `Core.ValidateCommit` and its transaction invariants. Important units:

1. Import proposal plus exactly one Planned journal row per declared operation.
2. Win the CAS and persist Applying plus attempt identity **before** sending any request.
3. Persist acknowledgement/receipt plus journal advance and create mapping together; include
   any available mirror update in that same transaction.
4. Persist verification receipt and journal advance plus acquired observations together.

No provider network call belongs inside a SQLite transaction. There is no cross-provider
or database-plus-HTTP transaction, and WAL does not make one. Receipt persistence failure
after dispatch leaves Applying recoverable/unknown rather than authorizing replay.

Journal history, proposals and publish mappings are durable and append-preserving.
Creation acknowledgement cannot commit without its permanent mapping; a later unavailable
readback retains that mapping. Local relationships have their own monotonic CAS versions;
deletion retains a tombstone so refresh/stale writers cannot resurrect an edge. A mirror
refresh can replace provider observations but cannot delete local overlays, drafts, Bench
definitions, proposals, mappings or journal evidence. Explicit draft/Bench editing remains
a separate deliberate local operation, not a side effect of selecting or refreshing a view.

Phase 2 implements this boundary in `Twig.Store.Sqlite`, referencing only Core and the
central SQLite dependencies. An exact connection binding keys one source-generated,
versioned snapshot document. An IMMEDIATE transaction checks CAS and commit invariants
before atomically replacing that document. Read transactions return coherent observations;
WAL with FULL synchronous writes supplies local durability, not HTTP atomicity. Whole
snapshot serialization is intentional for the first adapter; SQL row deltas/indexed queries
are deferred until scale warrants them.

Initialization requires a new explicit absolute file path in an existing directory and
reserves it without overwriting existing files. Opening requires the clean-core application
ID, schema version 1 and WAL. Foreign/legacy, missing and incompatible files fail explicitly;
there is no migration, reset, auto-discovered workspace path or CLI integration. Failed new
files remain for inspection. See [the adapter lifecycle and verification guide](../../src/Twig.Store.Sqlite/README.md).

Persistence reconstructs journal rows through legal transitions and verifies retained proposal
digests. Unknown/duplicate/missing required DTO members, unknown formats and malformed payloads
are refused. The only phase-2 Core adjustment compares retained receipt/error evidence by
ordinal contents rather than immutable-array backing identity, allowing unchanged rehydrated
history while still refusing evidence rewriting. Phase 1 itself created/deleted no databases.

## Bench and policy

Bench stores selector rules, never membership snapshots: pin, subtree pin, and a typed
provider-neutral query AST. Membership is their order-independent union over explicit
local observations. It never consults an active item, grants a claim, starts a sync or makes
a network request. Missing pinned items, unobserved query fields and incomplete/cyclic
subtrees are explicit failures. Provider query translation must refuse unsupported predicates
rather than broadening the filter. Time/calendar-dependent resolution belongs to adapters
and explicit application inputs, not an implicit clock in Core.

Declared policy preserves the existing D1-D4 contract: mechanical rules only, repository
declaration, unavailable evidence blocks, and unchanged pre-existing violations warn.
The initial evaluator covers parent type constraints and dependency integrity (self-links,
duplicate observations and witnessed cycles), with bounded acquired evidence. It does not
certify a whole board. Native/local provenance stays on witness edges; duplicate observations
from different provenance are one logical edge, not a fabricated duplicate mutation.
An empty policy cannot certify compliance. Each rule bounds acquired nodes (default 200,
maximum 1,000); more than 4,000 physical relationship observations is unavailable evidence.

Definite findings survive alongside unavailable evidence. Warning-only rules do not turn
missing evidence into permission or violations into compliance. Repairs are explicit
alternatives bound to evidence and proposals; they neither authorize themselves nor prove
post-write compliance. Repair selection/acquisition and richer provider rule translation
belong to later application/provider work, not new policy-language machinery here.

## Verification and next-phase boundaries

Focused Core tests exercise immutable identity/value semantics, golden canonical bytes and
SHA-256, schema fingerprints, same-batch gates/suppliers, unknown rules, explicit non-atomic
limits, real three-way intent, journal transition/creation fences, atomic store commit
validation, Bench and evidence-backed policy. There are no fake provider/store adapters
masquerading as production integration coverage.

Routine gates remain the supported bounded solution build, focused Core and real-SQLite tests through
the repository TRX reconciler, and the 15-second offline pre-push smoke. Core tests run in
both supported CI platform jobs, as do the phase-2 store tests. These phases make no native publish or release-candidate
qualification claim; AOT compatibility is enforced by shared-library analyzers and the
existing application composition remains unchanged.

Phase 2 implements SQLite and qualifies actual local rollback/concurrency, source-generated
roundtrips, receipt/mapping atomicity and durable uncertain-create fences with real SQLite.
Phase 3 implements ADO discovery, bounded exact queries, item/relationship reads and
revision-tested Create/Batch/Relate/Unrelate/history-comment effects. Applying acquisition,
acknowledgement/mapping/mirror commits and separate readback use the accepted store CAS.
Unknown outcomes fence their original targets, including replacement digests; recovery
continues only durable acknowledgement readback and never searches titles or replays.
Delete, unsupported native rule semantics and oversize query acquisition are explicit
capability limits, not success stubs. The mandatory leased auth/native admission seam has
no permissive default; a live-native bridge is deferred to command cutover, not claimed
by the HTTP/auth-only boundary tests. See [ADO lifecycle, supported shapes and verification](../../src/Twig.Providers.Ado/README.md).

Phase 4 implements GitHub; phase 5 retained commands; phase 6 legacy deletion.
Production command composition must be qualified in phase 5, not claimed by these additive adapters.
