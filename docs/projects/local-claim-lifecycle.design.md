# Local claim schema and lifecycle design

> Status: settled design sub-spec for Task AB#737. Consumed by Task AB#739
> (implementation of local claim mint and reclaim) and adjacent to Task AB#736
> (worktree storage and initialization). Complements the settled decisions in
> Spec AB#728 and the discovery record in
> `docs/projects/workflow-process-architecture.discovery.md`.

## Scope and boundary reaffirmation

This document fixes the claim-schema questions Spec #728 defers. It closes
record shape, lifecycle states, single-active supersession, mint ordering
around ADO assignment, explicit reclaim, release-failure semantics, recovery
indexing, retention, concurrency, and the interface consumed by #739. It does
**not** redefine attachment storage or storage-domain roles — that seam is
owned by #736 and is consumed here through an abstract binding.

### Preserved settled boundary

The design preserves the local-first claim boundary settled in Spec #728:

- A Twig claim is a **local execution-attachment record**, not a portable
  distributed lease. The Twig installation that holds the record is the sole
  local authority for the `(Connection, primary scope)` pair.
- Azure DevOps `System.AssignedTo` remains the visible responsibility signal
  and continues to be projected by mint/release, but it never confers local
  authority on its own. A local claim record that cannot be recovered means
  Twig fails loudly rather than infers ownership from the assignment field.
- The identifier is opaque and high-entropy (ULID/UUID shape); it is never
  derived from label, holder identity, timestamps, work-item id, branch, or
  any other business fact. Timestamp/title alone is never sufficient identity.
- The worktree-local attachment (owned by #736) references the claim by its
  opaque identifier and by the primary scope it authorizes. The system-local
  registry indexes claim records for cross-worktree recovery.
- No automatic revocation or reaper is required for initial delivery. A
  reaper is a future extension and MUST NOT be relied on by callers.

### Process-agnostic contract

The design is process-agnostic. It never names a specific work-item type, a
specific state, or a specific person. State names, type eligibility, and the
identity that populates `System.AssignedTo` are supplied at runtime by the
active profile / connection identity resolver, not hard-coded. All examples
below use generic placeholders (`<holder>`, `<Doing>`) that a concrete
implementation replaces from the process description.

### Deferred, not ambiguous

Cloud/fleet leases, renewal, generation counters, expiry arbitration, and a
reaper remain deferred as noted in the discovery record. To keep initial
behavior unambiguous while those extensions are absent:

- Every claim record is created with `expiresAt = null`. `null` means
  "never expires locally"; no time-based reclaim occurs.
- Every claim record is created with `leaseGeneration = 0`. The generation
  counter exists in the schema so a future coordinator can increment it
  during arbitration without a migration; version 1 readers/writers treat any
  non-zero value as opaque and refuse to interpret it.
- Every claim record is stamped with an `origin` discriminator; version 1
  writes exactly `origin = "local"`. A future portable-lease value (for
  example `"coordinator"`) does not alter local semantics for version 1
  records and does not retroactively activate any reaper behavior.

These three fields are deliberately mint-time constants in version 1. They
give the future coordinator extension somewhere to land without changing the
local-first contract or invalidating any record written today.

## Canonical identifier and label

### Canonical identifier

- Field name: `claimId`.
- Shape: an opaque, high-entropy, URL-safe string. The recommended encoding
  is a Crockford-base32 ULID (26 characters) because it collates by mint
  time; a UUIDv4 (36 characters with dashes) is an acceptable alternate.
  Callers MUST treat the value as opaque.
- Generation: minted by the mint operation before any local or ADO write.
  It is never derived from another field.
- Uniqueness: globally unique across every claim record ever minted by this
  installation. The system-local registry enforces uniqueness on insert
  (`UNIQUE(claimId)`).
- Immutability: once written, `claimId` is never rewritten. Every lifecycle
  transition and every recovery lookup uses this exact value.
- Case sensitivity: byte-exact comparison; readers MUST NOT normalize case,
  strip padding, or reformat the value.

### Human-readable label

- Field name: `label`.
- Shape: short UTF-8 string, up to 200 code points, no control characters,
  no embedded newlines.
- Provenance: supplied by the caller at mint time (typically derived from
  the primary scope's current title). It is a display aid only.
- Mutability: label MAY be updated on an active claim without changing the
  identifier or the lifecycle. A label update is CAS-guarded exactly like
  every other write: it matches the observed `casToken`, mints a fresh
  `casToken` under the same transaction, and surfaces
  `ConcurrentClaimWrite` on mismatch. It never changes `state` and never
  produces a new claim.
- Never load-bearing: no lookup, no lifecycle decision, and no equality test
  reads `label`. Label collisions across records are allowed.

## Record shape (registry row)

Every claim is one row in the system-local registry. The row shape is fixed
below. The storage binding (owned by #736) realizes the row as a JSON
document in `~/.twig/system.db`'s `Claims.recordJson` column — one
registry row per claim record. The field set and invariants below MUST
match exactly. `schemaVersion` is the single integer that gates on-disk
compatibility for the row.

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `schemaVersion` | integer | no | On-disk shape version. Version 1 records are described here. Readers refuse a higher version rather than interpret unknown fields. |
| `claimId` | opaque string | no | Canonical identifier, byte-exact. Immutable. Unique. |
| `label` | UTF-8 string | yes | Human display only. May be updated in place; never load-bearing. |
| `connectionRef` | opaque string | no | Stable identifier of the ADO connection (`organization/project` binding). Supplied by the connection registry; never re-derived from URL text. |
| `primaryScopeId` | opaque string | no | Stable identifier for the primary scope this claim authorizes. For an ADO-backed scope this is the numeric work-item id rendered as a string; the schema treats it as opaque so future non-ADO scopes fit without migration. |
| `primaryScopeKind` | opaque string | no | Discriminator for the scope identifier (initial value: `"ado-workitem"`). Version 1 readers accept exactly the values the profile declares; unknown values fail loudly. |
| `holderIdentity` | opaque string | no | Captured principal that owns the claim (typically the connection's resolved identity descriptor). Never derived from OS username at claim-check time. |
| `holderDisplay` | UTF-8 string | yes | Optional display form of the holder captured at mint. Advisory only; never used for authorization. |
| `worktreeFingerprint` | opaque string | no | Stable local fingerprint identifying the managed worktree that minted the claim. Composed by #736's attachment binding (Git worktree identity + absolute path); this schema stores it opaquely. |
| `state` | enum string | no | One of `pending`, `active`, `released`, `superseded`. See lifecycle. |
| `origin` | enum string | no | Version 1: exactly `"local"`. Reserved for future coordinator extension. |
| `leaseGeneration` | integer | no | Version 1: exactly `0`. Reserved for future coordinator extension. |
| `expiresAt` | RFC 3339 timestamp | yes | Version 1: exactly `null` (no local expiry). Reserved for future coordinator extension. |
| `createdAt` | RFC 3339 timestamp | no | UTC, monotonic non-decreasing across a single writer. Set at mint (initial insert). |
| `activatedAt` | RFC 3339 timestamp | yes | UTC. Set exactly once when the record transitions `pending → active`. `null` before activation. |
| `releasedAt` | RFC 3339 timestamp | yes | UTC. Set exactly once when the record enters `released` or `superseded`. `null` otherwise. |
| `supersededByClaimId` | opaque string | yes | Set only when `state = superseded`; references the successor claim's `claimId`. Never set on `released`. Never rewritten. |
| `releaseReason` | enum string | yes | On terminal states, one of `explicit-release`, `explicit-reclaim`, `mint-abort`. `null` while `pending` or `active`. |
| `notes` | UTF-8 string | yes | Optional free-form provenance (e.g. the mint prompt or a release explanation). Advisory only; not consulted by any lifecycle decision. |
| `casToken` | opaque string | no | Concurrency token rewritten on every state or `casToken`-guarded field write. See concurrency section. |

### Invariants

- `(claimId)` is unique across the whole registry (all states).
- `(connectionRef, primaryScopeKind, primaryScopeId, state)` is unique when
  `state ∈ { pending, active }`. Terminal states (`released`, `superseded`)
  are not part of this uniqueness constraint and MAY accumulate.
- A row with `state = superseded` MUST have `supersededByClaimId` pointing at
  an existing row whose `state` was `active` at the moment of supersession.
- A row with `state = released` MUST have `supersededByClaimId = null`.
- `createdAt <= activatedAt <= releasedAt` where each next timestamp is
  defined; a defined `activatedAt` implies the record was `active` at least
  once.
- Every write to a lifecycle-relevant field bumps `casToken` under the same
  atomic transaction. Label updates and `notes` updates bump `casToken` too
  so a concurrent lifecycle write cannot silently overwrite them.

### JSON encoding (registry read/write)

For interop with #736's storage seam and #739's implementation the row
serializes as a flat JSON object with the field names above. Times are RFC
3339 UTC with a `Z` suffix. Unknown fields on read are rejected as
"schema-drift" rather than ignored.

## Lifecycle states

Every claim record lives in exactly one of four states:

```text
             mint()                activate()             release()
    (none) --------->  pending  ---------------->  active  --------->  released
                        |                          |
                        | mint-abort               | reclaim() supersedes
                        v                          v
                     released                   superseded
```

### `pending`

- Meaning: the mint operation has reserved local ownership of the
  `(Connection, primary scope)` pair and captured provenance, but the visible
  ADO projection has not yet succeeded.
- Observable behavior on ordinary commands:
  - The registry uniqueness constraint rejects any other `pending`/`active`
    row for the same `(Connection, primary scope)`.
  - `describe`/status reads MAY surface the pending claim as
    "reservation held; ADO projection incomplete".
  - No command that requires an active claim (e.g. `#739` mint validation)
    treats a `pending` row as authority: fail-loud on missing active claim
    still applies.
- Allowed transitions: `pending → active` (activation succeeded) or
  `pending → released` (abort; see mint ordering).

### `active`

- Meaning: local ownership is asserted **and** the ADO projection landed.
  Downstream commands authorized by claim identity treat this row as
  authority.
- Observable behavior: every lookup by `(Connection, primary scope)` returns
  this row; `label` updates apply here; validation code paths in #739 read
  `claimId` from the attachment and match it against this row byte-exactly.
- Allowed transitions: `active → released` (explicit release), or
  `active → superseded` (explicit reclaim mints a new active row and marks
  this one superseded in the same transaction).

### `released`

- Meaning: the holder explicitly relinquished the claim. The row carries no
  authority. It is retained for local diagnosis and history (see retention).
- Observable behavior: no lookup by `(Connection, primary scope, active)`
  matches this row; commands that require an active claim fail loudly; the
  registry may still list the record in history views.
- Terminal: released records are never reactivated. A subsequent mint uses a
  new `claimId`. `releaseReason` is `explicit-release` (or `mint-abort` when
  reached from `pending`).

### `superseded`

- Meaning: the holder explicitly reclaimed the same `(Connection, primary
  scope)` pair; a new active row exists and `supersededByClaimId` names it.
  The old row carries no authority.
- Observable behavior: identical to `released` for authority purposes; the
  registry retains it for audit and to make explicit reclaim traceable.
- Terminal: superseded records are never reactivated.

### Fail-loud on missing / mismatched claim

If the worktree attachment references a `claimId` that no registry row
matches, or references a row whose state is not `active`, every command that
depends on the claim MUST fail loudly (see `NamedFailure` values below).
Twig never infers ownership from `System.AssignedTo`, from an OS username,
or from a `pending` / `released` / `superseded` row.

## Single-active supersession

One Twig installation may hold at most one **active or pending** claim for a
given `(connectionRef, primaryScopeKind, primaryScopeId)` tuple. This is
enforced by:

1. The registry's uniqueness constraint on the tuple filtered by
   `state ∈ { pending, active }`. Attempted insert or state-change of a
   second row into that state fails at the storage layer.
2. The mint operation's precondition check, which reads the current row for
   the tuple **inside the same transaction** as the insert and refuses if any
   `pending` or `active` row exists.

Two callers acting concurrently within the same installation:

- Contend for the tuple lock the storage binding provides. The loser sees
  `PrimaryScopeAlreadyClaimed` (see failures below) with the current active
  or pending `claimId`. The loser never silently coalesces onto the existing
  record.

Branch association never overrides this rule (from #728). Two worktrees
minting for the same tuple with different branches produce the same failure.

## Mint ordering around ADO assignment

Mint is the sole operation that creates a new `claimId`. It runs in the
following ordered, transactional steps. Every step is observable to #739's
implementation as a distinct milestone; the ordering guarantees that a
failed ADO projection never leaves an active claim without a paired ADO
assignment, and never leaves ADO assignment without a local record.

1. **Uniqueness + reservation (local, atomic).**
   Open a storage transaction. Assert `(connectionRef, primaryScopeKind,
   primaryScopeId, state ∈ {pending, active})` uniqueness. Insert the new
   row with:
   - fresh `claimId`,
   - `state = "pending"`,
   - `createdAt = now`, `activatedAt = null`, `releasedAt = null`,
   - `origin = "local"`, `leaseGeneration = 0`, `expiresAt = null`,
   - `casToken = <fresh>`.
   Commit. If the insert fails on the uniqueness constraint, surface
   `PrimaryScopeAlreadyClaimed`; no ADO write is attempted.

2. **ADO projection.**
   Project the holder onto `System.AssignedTo` for the primary scope via
   the native ADO mutation path (through the connection binding, following
   the same plan/publish contract every other Twig mutation uses). This step
   does not run under the storage transaction: it is a network call, and
   the local pending row is what keeps the tuple reserved during it.

3. **Activation (local, atomic).**
   Reopen a storage transaction and rewrite the row from `pending → active`
   under CAS: match the `casToken` observed at step 1, set
   `state = "active"`, `activatedAt = now`, mint a fresh `casToken`. Commit.

4. **Attachment linkage.**
   Hand the new `claimId` to the attachment binding (owned by #736) so the
   worktree attachment references the active claim. Attachment write occurs
   after activation so a partial mint never leaves the attachment referencing
   a pending row.

If step 2 fails with any error (auth, network, ADO rule refusal, conflict),
mint takes the **mint-abort** path: rewrite the pending row to
`state = "released"`, `releasedAt = now`, `releaseReason = "mint-abort"`
under CAS, commit, then surface `AdoProjectionFailed` naming the underlying
error. The released `pending` row is retained for diagnosis. The
attachment is not updated; any pre-existing conformant active claim on
another scope is untouched (mint never rewrites an unrelated attachment).

If step 3 fails on CAS mismatch (another writer within the same
installation raced), abort with `ConcurrentClaimWrite` and leave the pending
row in place; retrying mint is not automatic. The pending row can be
resolved explicitly by an operator command that either drives step 2/3 to
completion or triggers mint-abort.

If step 4 fails (attachment binding rejects the write), the active claim
exists in the registry but no attachment references it. The mint call fails
with `AttachmentLinkFailed`; the operator sees a live registry row and no
attachment. The reclaim path below can rebind an attachment to an existing
active claim without minting a new one.

### No conformant claim without both sides

The composite rule required by #728 — "A mint whose ADO projection cannot be
written does not produce a conformant local claim" — is enforced by making
`active` the only state that grants authority, and by making step 3 the only
gate that enters `active`. A pending row is never authority. An ADO
assignment without a subsequent activation cannot be observed as a
conformant claim because no downstream command consults `System.AssignedTo`
for authorization.

## Explicit reclaim

Reclaim is the explicit path for a Twig installation to re-take a
`(Connection, primary scope)` tuple it previously released, or to replace
an existing `active` row with a fresh identifier. Reclaim never
"reactivates" a released row; it always mints a new `claimId`.

Two shapes are supported by version 1:

### Reclaim over `released` (or over no prior row)

Equivalent to a fresh mint: the uniqueness constraint sees no `pending` or
`active` row for the tuple, so mint step 1 succeeds. Steps 2–4 follow the
normal path. The prior released rows for the tuple remain in place with
their existing `claimId`s and `releaseReason`s.

### Reclaim over `active` (supersession)

The caller explicitly asks to replace the currently `active` claim for the
tuple. Steps 1' and 3' below extend the mint transaction to include a
supersession write; step 2 is unchanged.

1'. **Reservation with pinned predecessor.** Under one storage transaction,
    read the current `active` row for the tuple. Refuse if it is not in this
    installation's registry (i.e. a fleet-lease coordinator marker) — reclaim
    never crosses the local boundary. Capture its `claimId` as
    `predecessorClaimId` and `casToken` as `predecessorCas`. Insert the new
    `pending` row exactly as in step 1. Commit.

2. Same ADO projection call. If the current ADO assignment is already the
   intended holder, this step is a no-op-but-verified read that confirms
   the assignment; otherwise it rewrites `System.AssignedTo`.

3'. **Atomic supersession.** Under one storage transaction:
    - CAS-rewrite the pending row → `active` (as in mint step 3),
    - CAS-rewrite the predecessor row → `superseded`,
      setting `releasedAt = now`, `releaseReason = "explicit-reclaim"`,
      `supersededByClaimId = <new-claimId>`, matching `predecessorCas`.
    Both writes commit together. Either write failing on CAS aborts the
    whole transaction; surface `ConcurrentClaimWrite`.

4. Attachment linkage rebinds to the new `claimId`.

### No implicit reclaim

Nothing about the presence of a stale ADO assignment or a matching
`worktreeFingerprint` implicitly triggers reclaim. Reclaim only runs on
explicit caller action.

## Release and release-failure semantics

Release is the explicit path to relinquish an `active` claim without minting
a replacement. Release ordering mirrors mint ordering in reverse.

1. **ADO projection clear.**
   Clear `System.AssignedTo` on the primary scope through the same native
   ADO mutation path used by mint. If the projection succeeds, proceed. If
   the projection fails with any error, surface
   `ReleaseAdoProjectionFailed`. Per Spec #728's "Release projection
   failure" decision, the local claim MUST remain `active`; Twig MUST NOT
   report release complete while ADO still shows active responsibility. The
   caller is expected to resolve the ADO failure and re-run release.

2. **Local terminalization.**
   Under a storage transaction, CAS-rewrite the `active` row to
   `state = "released"`, `releasedAt = now`,
   `releaseReason = "explicit-release"`, matching the observed `casToken`.
   Commit. CAS mismatch surfaces `ConcurrentClaimWrite`; the row remains
   `active`.

3. **Attachment linkage.**
   Instruct the attachment binding to drop the reference to the released
   `claimId`. Attachment drop occurs after local terminalization so a
   partial release never leaves the attachment referencing a terminal row.
   Attachment-drop failure surfaces `AttachmentUnlinkFailed`; the released
   row is retained for diagnosis.

### Named release outcomes

- `Success`: ADO cleared, local `released`, attachment unlinked.
- `ReleaseAdoProjectionFailed(<underlying>)`: ADO not cleared, local
  remains `active`.
- `ConcurrentClaimWrite(<observed-cas>, <expected-cas>)`: ADO cleared but
  local write raced; the row is either still `active` or was terminalized
  by another writer. Caller re-reads and decides.
- `AttachmentUnlinkFailed(<underlying>)`: local `released`, attachment
  binding refused to drop. Caller re-runs the attachment drop; the released
  row is unambiguous.

Release MUST NOT be reported successful unless ADO is cleared and the local
row is terminal.

## Recovery indexing

The system-local registry is indexed to support the recovery scenarios
Spec #728 named:

- **Primary lookup by attachment.** The attachment binding hands the
  registry a `claimId` and expects the active row. Indexed on `claimId`
  (unique).
- **Tuple lookup.** Mint/reclaim/release read the current row for
  `(connectionRef, primaryScopeKind, primaryScopeId, state ∈ {pending,
  active})`. Indexed on the tuple restricted to those states.
- **Worktree lookup.** Cross-installation diagnostics may enumerate claims
  minted by a given worktree. Indexed on `worktreeFingerprint`.
- **Holder lookup.** Diagnostic commands may enumerate claims for a given
  holder identity. Indexed on `holderIdentity`.
- **History lookup.** Time-ordered browsing uses `createdAt` (dense, always
  set) with `claimId` as tie-breaker.

Concrete index realization is #736's storage seam: the `Claims` table
in `~/.twig/system.db` carries the row payload on `recordJson` and
exposes the tuple/`claimId`/`worktreeFingerprint`/`holderIdentity`/
`createdAt` indexes above as native SQLite indexes.

### Recovery scenarios and their reads

| Scenario | Read | Expected result |
|---|---|---|
| Fresh command loads its claim | `SELECT * FROM claims WHERE claimId = ?` from the attachment | Exactly one row in `active`. Otherwise: fail loud. |
| Mint contention | `SELECT * FROM claims WHERE (connectionRef, primaryScopeKind, primaryScopeId) = ? AND state IN ('pending','active')` | Zero rows: proceed to mint. One row: refuse with `PrimaryScopeAlreadyClaimed`. |
| Attachment lost | `SELECT * FROM claims WHERE worktreeFingerprint = ? AND state = 'active'` | Operator command may rebind attachment to an existing active row. |
| Historical audit | `SELECT * FROM claims WHERE (connectionRef, primaryScopeKind, primaryScopeId) = ? ORDER BY createdAt` | Full history including released and superseded rows. |

## Retention

- `pending`, `active`, `released`, and `superseded` rows are all retained by
  default. Nothing in version 1 deletes a claim row.
- Terminal (`released`, `superseded`) rows are unbounded in count; they are
  small (all fields fixed shape) and their retention is the mechanism by
  which explicit reclaim and history diagnostics work.
- An operator-only maintenance command MAY prune terminal rows older than a
  caller-specified cut-off (`releasedAt < cutoff`). Version 1 does not
  supply this command; it is safe to add later because no other code path
  reads terminal rows for authorization.
- `pending` rows are retained even after `mint-abort`; they carry
  `state = "released"` and `releaseReason = "mint-abort"` and become part of
  history. No time-based sweep removes them.

## Concurrency, CAS, and named failures

### Concurrency model

- **Intra-installation.** The registry lives in one system-local store
  (from #728: `~/.twig/system.db` or its storage-seam equivalent). Multiple
  Twig processes on the same machine may write concurrently; the storage
  binding provides a serializable transaction for each lifecycle write, and
  every write is guarded by an explicit CAS check against the row's
  `casToken`.
- **Inter-installation.** The registry is machine-local; a different
  installation on a different machine cannot write to it. Cross-installation
  arbitration is deferred to the future coordinator extension and is out of
  scope for version 1.

### CAS token

- `casToken` is opaque and monotonically fresh: every lifecycle write mints
  a new value that MUST differ from every prior value on the same row.
  A ULID or UUIDv7 is a fine realization; readers MUST NOT interpret it.
- Every lifecycle write reads the row, captures its `casToken`, computes
  the intended new state, then writes under a `WHERE claimId = ? AND
  casToken = ?` guard. A zero-row update is a CAS mismatch and MUST surface
  `ConcurrentClaimWrite`.
- Non-lifecycle field writes (label update, `notes` update) also bump
  `casToken` so a concurrent lifecycle writer does not silently drop the
  update. Callers MUST re-read on `ConcurrentClaimWrite` before retrying.

### Named failure vocabulary

All lifecycle failures surface as one of the following named errors. Each
carries an opaque payload sufficient for a caller to diagnose without
re-parsing free-form text.

| Name | Meaning | Emitted by |
|---|---|---|
| `PrimaryScopeAlreadyClaimed(existingClaimId, existingState)` | Mint/reclaim uniqueness violated; another `pending` or `active` row holds the tuple. | mint step 1, reclaim step 1' |
| `AdoProjectionFailed(underlying)` | Mint's ADO assignment write failed; the pending row was terminalized as `mint-abort`. | mint step 2 |
| `ConcurrentClaimWrite(observedCas, expectedCas)` | CAS mismatch on any lifecycle write. | mint step 3, reclaim step 3', release step 2, label writes |
| `AttachmentLinkFailed(underlying)` | Attachment binding refused to reference the newly active claim. | mint step 4, reclaim step 4 |
| `ReleaseAdoProjectionFailed(underlying)` | ADO clear on release failed; local row remains `active`. | release step 1 |
| `AttachmentUnlinkFailed(underlying)` | Attachment binding refused to drop the reference after release. | release step 3 |
| `ClaimNotFound(claimId)` | Attachment references a `claimId` no row matches. | validation path (#739) |
| `ClaimNotActive(claimId, currentState)` | Attachment references a row in a non-`active` state. | validation path (#739) |
| `SchemaDrift(schemaVersion)` | Row's `schemaVersion` is not recognized by this reader. | any read |
| `HolderMismatch(rowHolder, resolvedHolder)` | Optional; emitted by a diagnostic command that compares stored `holderIdentity` against the connection's currently resolved identity. Not a lifecycle failure. | diagnostic reads |

Every failure is deterministic given the row state at the failing step;
callers rely on the enumerated set rather than string matching.

## Interface consumed by #739

Version 1 implementation for Task #739 depends on the following interface.
It is defined in terms of the local-first contract and the storage seam
owned by #736; the actual C# surface is #739's to shape, but the operations,
inputs, outputs, and preconditions below MUST match this spec.

```text
mint(input: MintInput) -> Result<ActiveClaim, MintError>
    Preconditions:
      - MintInput carries: connectionRef, primaryScopeKind, primaryScopeId,
        holderIdentity, holderDisplay?, label?, worktreeFingerprint,
        notes?, adoProjection: AdoProjectionBinding.
      - MintInput has NO caller-supplied claimId; the mint operation
        generates it.
    Postconditions on Ok:
      - Registry contains exactly one active row for the tuple with
        the returned claimId.
      - ADO assignment on the primary scope is the resolved holder.
      - Attachment binding references the returned claimId.
    Errors:
      PrimaryScopeAlreadyClaimed | AdoProjectionFailed |
      ConcurrentClaimWrite | AttachmentLinkFailed.

reclaim(input: ReclaimInput) -> Result<ActiveClaim, ReclaimError>
    Preconditions:
      - ReclaimInput carries the same fields as MintInput plus a required
        allowSupersede flag.
      - allowSupersede=false: behaves like a fresh mint (over released or
        no prior row).
      - allowSupersede=true: supersedes an existing active row for the
        tuple; refuses if no active row exists in this installation.
    Postconditions on Ok:
      - New active row with a fresh claimId exists.
      - Previous active row (if any) is state=superseded with
        supersededByClaimId set to the new claimId.
      - Attachment binding references the new claimId.
    Errors: MintError | ClaimNotActive (when allowSupersede=true and no
      active row exists).

release(input: ReleaseInput) -> Result<ReleasedClaim, ReleaseError>
    Preconditions:
      - ReleaseInput carries the claimId to release and an
        adoProjection: AdoProjectionBinding.
      - The claimId must reference an active row owned by this installation.
    Postconditions on Ok:
      - ADO assignment on the primary scope is cleared.
      - Registry row is state=released with releaseReason=explicit-release.
      - Attachment binding no longer references the claimId.
    Errors:
      ClaimNotFound | ClaimNotActive | ReleaseAdoProjectionFailed |
      ConcurrentClaimWrite | AttachmentUnlinkFailed.

validate(claimId, connectionRef, primaryScopeId, primaryScopeKind)
    -> Result<ActiveClaim, ValidateError>
    Preconditions:
      - Called by every #739 code path that requires a claim (fail-loud
        validation).
    Postconditions on Ok:
      - Returned ActiveClaim's tuple matches the caller-supplied tuple
        byte-for-byte and its state is exactly "active".
    Errors:
      ClaimNotFound | ClaimNotActive | SchemaDrift |
      TupleMismatch (returned when the row exists but its tuple disagrees
      with the caller-supplied tuple; this is a corruption signal).

lookupByTuple(connectionRef, primaryScopeKind, primaryScopeId)
    -> Result<Option<ActiveOrPendingClaim>, LookupError>
    Reads at most one row in {pending, active} for the tuple. Used by mint
    contention diagnostics and by reclaim precondition checks.

updateLabel(claimId, newLabel, expectedCasToken)
    -> Result<ActiveClaim, UpdateError>
    Rewrites the label under CAS. Never changes state. CAS mismatch surfaces
    ConcurrentClaimWrite.

AdoProjectionBinding is the abstract seam supplied by the connection layer.
It exposes exactly two operations:
    projectHolder(primaryScopeId, holder) -> Result<Unit, AdoError>
    clearHolder(primaryScopeId) -> Result<Unit, AdoError>
Both are used by mint/reclaim/release and are the only ADO surface the
claim lifecycle touches.
```

### Contract with the attachment seam (#736)

The attachment binding is owned by #736 and provides at minimum:

```text
link(worktreeFingerprint, claimId) -> Result<Unit, AttachmentError>
unlink(worktreeFingerprint, claimId) -> Result<Unit, AttachmentError>
readClaimReference(worktreeFingerprint) -> Result<Option<claimId>, AttachmentError>
```

The claim lifecycle uses `link` at mint/reclaim step 4, `unlink` at release
step 3, and `readClaimReference` when a command starts up and needs to
locate its claim. The attachment binding stores nothing about the claim
except its opaque `claimId`; every other field lives in the registry.
#736 realizes the attachment binding as the worktree-local
`.twig/attachment.json` file (gitignored) that references the active
`claimId`, while the registry payload continues to live in
`~/.twig/system.db`'s `Claims.recordJson`.

## Cross-cutting rules for implementers

- **No hidden mutations.** Every lifecycle write goes through one of the
  named operations above. There is no "internal fix-up" path that changes
  `state` outside these operations.
- **Every write is CAS-guarded.** No lifecycle write may omit the CAS check;
  no `casToken` is ever accepted from outside the storage layer.
- **Every read is by tuple or by opaque id.** No lookup is by label,
  holder display, or notes.
- **No ambient identity.** `holderIdentity` is what the connection binding
  resolves at mint time; validation compares stored `holderIdentity`
  against the row itself, not against a re-resolved current identity.
  Diagnostic tools MAY surface `HolderMismatch` but authorization does not
  depend on it.
- **No implicit time behavior.** Version 1 never runs a background sweep,
  a reaper, or a time-based state change. `expiresAt` is always `null`.
- **No cross-installation coupling.** Nothing in version 1 reads or writes
  another installation's registry. Cross-installation arbitration is the
  future coordinator extension and is intentionally absent.

## Traceability

- Spec AB#728 — "Local-first claim decision", "Claim release projection",
  "Local duplicate claim rule", "Claim mint ordering", "Released claim
  retention", "Release projection failure".
- Discovery: `docs/projects/workflow-process-architecture.discovery.md`
  §§ Local-first claim decision, Future portable coordinator extension.
- Research: `docs/research/worktree-centered-workflow-architecture.md`
  §§ Provisional ownership map, Supplemental evidence: requirements and
  evidence traceability, Worktree identity and reclamation row.
- Consumed by AB#739 — Implement local claim mint and reclaim.
- Adjacent to AB#736 — Worktree storage and initialization; consumes the
  attachment/storage seam defined there.
