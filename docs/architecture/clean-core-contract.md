# Clean core contract

Phases 1-4 and phase-5 implementation in progress for **Epic #1220, Core domain consolidation**. Updated 2026-10-30.

## Status and scope

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
application enforcement is in the shared Core workspace workflow; GitHub leased
native admission and complete retained-surface routing remain unfinished.

Phase 5 has also added the native ADO access/admission bridge with exact
digest/ordinal/attempt attribution and creation fences in the existing native
registry. It holds a real native binding/generation operation and refuses
token-only access or missing durable Applying intent. The rooted ADO slice has
actual CAF/production-DI coverage, but these changes are not a claim that every
retained command, UI surface or phase 5 is complete.

Fresh ADO checkouts can deliberately initialize this composition with
`twig init <org> <project> --core-store <new-absolute-file>`. The selected native
binding lease supplies collection, project and principal identities; the new
SQLite instance is pinned to that exact connection. Existing `.twig` directories,
existing selected files and reset flags are refused, not migrated or adopted.
Uninitialized/legacy CLI mutation admission refuses before legacy writer registration
with this preservation and initialization guidance. Preserved legacy proposal/journal
inspection does not authorize legacy application. GitHub initialization and the
remaining retained routes are implementation gaps, not supported empty results.

Offline Core file preview retains exact unconfirmed intent as Planned with no
attempt or receipt, and reports unavailable schema/rule evidence as blocking.
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

The conservative phase-5 retention assumption is to preserve all currently supported
CLI, Claims/coordination, TUI, Browser and companion behavior, not make a permanent
surface-pruning decision. Claims remain a host concern and MCP remains withdrawn.
The ADO/GitHub adapters' already disclosed unsafe/unqualified Delete limitation
must remain an explicit capability refusal, never a fictional strict revision
precondition or a reachable alternate legacy writer. No production surface has
been removed by the current in-progress changes.

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
