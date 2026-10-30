# Clean core contract

Phases 1 and 2 of **Epic #1220, Core domain consolidation**. Updated 2026-10-30.

## Status and scope

`Twig.Core` is an additive, standalone model and pure-function library in the supported
solution. Nothing in the CLI, TUI, Browser, Domain, Infrastructure or production dependency
injection uses it yet. Phase 2 adds `Twig.Store.Sqlite` as an isolated persistence adapter,
not a provider/command cutover, compatibility layer, or claim that the old domain is removed.

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

No ownership decision below implies that Claims/coordination, TUI or Browser survives
or is removed. Their retention, and the retained CLI command inventory, remain explicit
checkpoints blocking the affected phase-5 cutover and phase-6 deletions. Phase 1 changes none.

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

A proposal is immutable and connection-bound. Version 1 hashes:

- The format version, provider scope, immutable destination and identity reference.
- The complete schema fingerprint and expected local store CAS version.
- Every operation in declared order, including ID, kind, typed values/body, explicit clears,
  revision and revision target, relationship endpoints/provenance/local CAS, type and concurrency mode.
- A create's durable local UUID, carried in its item key.

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
retroactively manufacture atomicity**. All creation explicitly declares this limitation:
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
Phase 3 implements ADO; phase 4 GitHub; phase 5 retained commands; phase 6 legacy deletion.
Provider dispatch/recovery and production command composition must be qualified in those
later phases, not claimed by store or pure model tests.
