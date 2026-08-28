# Change Proposal — payload, digest, canonical review model, and journal record (T2, AB#741)

**Work Item:** #741 (Task, parent Spec #729 — "Change Recipe and Change Proposal")
**Type:** Design (closes deferred shape questions in #729 §Change Proposal module, §Canonical semantic review model, §Journal and audit)
**Status:** ⬛ Settled
**Plan Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive summary

This note fixes the shapes that AB#742 (T3) and AB#743 (T4) implement against. It
closes four deferred questions from Spec #729:

- the **Change Proposal payload** — field-level shape, wire representation, and
  storage format;
- the **digest algorithm** — canonicalization inputs, hash function, string
  encoding, and the invariant that adapter enrichment cannot alter it;
- the **canonical semantic review model** — serialized shape, versioning, and
  cross-adapter compatibility rules;
- the **journal record** for an applied Change Proposal — field by field, with
  each field's source at authorization time.

The headline finding is that **two of these are already built and correct.** Twig's
native Plan v1 surface on `origin/main` already implements an immutable,
digest-bound mutation document and a durable two-table journal. The digest
already satisfies Spec #729's stated invariant, and that is provable from the
code path rather than asserted. This note therefore **ratifies** the existing
payload and digest contracts as the Change Proposal contracts, and confines new
design to the two genuinely missing pieces: the canonical semantic review model,
which does not exist in any form, and four absent journal fields.

Ratifying rather than reinventing is the load-bearing decision here. The
alternative — designing a fresh payload and digest for the "new" Change Proposal
concept — would fork a second mutation document alongside a working one and
force T3 to ship a shim it would immediately have to remove.

**This note changes no code.** It fixes shapes only.

---

## Background

### What #729 settled and what it deferred

| # | Settled constraint | Effect on this note |
|---|--------------------|--------------------|
| S1 | A Change Proposal is an immutable value with concrete operations, target items, and a digest over its semantic content. | §2 ratifies the Plan v1 document as that value. |
| S2 | Validate and preview are non-mutating; preview yields the canonical semantic review model. | §4 defines the model preview must return. |
| S3 | Apply requires an authorization record whose bound digest exactly matches; any mismatch fails closed. | §3.5 records where that comparison already happens; §5 adds the authorization fields to the journal. |
| S4 | Adapters render the model in full; enrichment is additive only. | §4.4 makes the model a derived projection, explicitly outside the digest input. |
| S5 | Each applied proposal is recorded with canonical model, digest, authorization mode, authorizer identity, and rationale. | §5 maps all five onto the journal, three present and two absent. |

Deferred to this note (T2): concrete field-level payload, wire representation,
storage format, digest algorithm, the model's serialized shape and versioning and
compatibility rules, and the journal record schema, storage location, retention
policy, and handling of unapplied outcomes.

### What the codebase already fixes

The Plan v1 surface is implemented and shipping. The relevant seams:

- `PlanDocumentParser` (`src/Twig.Infrastructure/Plan/PlanDocumentParser.cs`) — the
  closed vocabulary of the document and its strict rejection of unknown keys.
- `PlanCanonicalizer` (`src/Twig.Infrastructure/Plan/PlanCanonicalizer.cs`) — canonical
  byte form and digest.
- `PlanLifecycleService` (`src/Twig.Infrastructure/Plan/PlanLifecycleService.cs`) —
  preview, apply, status, the operation state machine, and the apply-time digest gate.
- `plan_journals` / `plan_operations` (`src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`,
  durable migration `[6]`, extended by `[7]`) — the durable journal.

This note does not reshape any of them. It states which of their existing
guarantees are hereby the Change Proposal contract, and names the additive
changes required on top.

---

## 1. Naming boundary (scope guard)

Two unrelated types already carry the name `ChangeProposal`, and **neither is the
Spec #729 concept**:

- `Twig.Domain.Projections.ChangeProposal` — a public, shipped union of
  `FieldEdit | StateMove` used by the TUI edit path.
- `Twig.Infrastructure.Persistence.Transport.ChangeProposalRenderProposal` — an
  internal renderer DTO whose `Content` is explicitly opaque and passed through.

The immutable digest-bound Change Proposal is a **third, distinct concept**. It is
the Plan v1 document. Renaming either existing type onto it would be a category
error. The rename map and cutover mechanic belong to T1 (AB#740); this note only
fixes the boundary so T3 does not collide with it.

---

## 2. Change Proposal payload, wire representation, and storage format

**Decision: ratify the Plan v1 document as the Change Proposal payload, unchanged
in shape.**

### 2.1 Field-level shape

The document is a JSON object with exactly three top-level members, enforced by
`PlanDocumentParser`:

| Member | Type | Required | Meaning |
|---|---|---|---|
| `version` | integer | yes | Document schema version. Only `1` is accepted. |
| `workspace` | object | yes | `{ organization, project }`, both strings. |
| `operations` | array | yes | One or more operations. Empty is invalid. |

Five operation kinds are accepted, each with a closed field set:

| Kind | Fields |
|---|---|
| `batch` | `id`, `kind`, `workItemId`, `expectedRevision`, `fields` |
| `add-link` / `remove-link` | `id`, `kind`, `workItemId`, `expectedRevision`, `relation`, `otherId` |
| `publish-seed` | `id`, `kind`, `stagedIdentity`, `expectedFingerprint` |
| `delete` | `id`, `kind`, `workItemId`, `expectedRevision` |

`fields` values are `string | null`, where `null` clears. `relation` is drawn from
the closed set `parent | predecessor | successor | related`. Unknown keys at any
level are rejected rather than ignored — that strictness is what makes the digest
meaningful, and it is retained deliberately.

**Rationale.** This shape already expresses "concrete operations against target
items" (S1). Declared array order is execution order, and the parser's
unknown-key rejection means the digest covers the whole document with no
silently-ignored remainder.

### 2.2 Wire representation

The wire representation is the **canonical UTF-8 byte form** produced by
`PlanCanonicalizer` (§3), not the author's original file bytes. Two files
differing only in whitespace or property order are the same proposal. This is
already true and is hereby the contract across adapter and transport boundaries.

### 2.3 Storage format

A Change Proposal is stored in exactly two places, both existing:

- **In-flight, as authored** — a file under the workspace at
  `.twig/ado-plans/<opaque-id>/<sequence>.json`. Immutable once previewed.
- **Durably, as canonicalized** — `plan_journals.canonical_json`, with the
  per-operation slice in `plan_operations.request_json`.

The canonical form in the journal, not the file, is authoritative. The file may be
deleted without affecting audit; `plan_journals.source_path` records only where it
came from.

**Deliberately not decided here:** whether proposals are ever transported between
machines. Nothing in this shape prevents it — the canonical form is
self-contained — but no cross-machine transport is specified, and none should be
inferred.

---

## 3. Digest algorithm

**Decision: ratify the existing `PlanCanonicalizer` contract verbatim.**

Spec #729 requires this section state four things. Each is stated below as an
enforceable property, with the code that already implements it.

### 3.1 (a) Canonicalization inputs

The input is the **entire parsed plan document root**, and nothing else.

- Object property names are sorted **ordinal ascending**; every property is
  emitted exactly once. Duplicate property names are rejected before hashing.
- Array order is **preserved verbatim** — element order is significant, so
  reordering `operations[]` is a different proposal.
- Values are re-emitted **from the parsed `JsonElement`**, never from raw source
  text, so comments and incidental whitespace cannot survive into the hash.
- Output is **compact** — no whitespace between tokens.
- Numbers preserve their raw source syntax, so `1` and `1.0` are **distinct**.

That last point is a genuine sharp edge and is called out deliberately: the
canonical form normalizes whitespace and property order but does **not** normalize
numeric representation. Authors must not assume `1` and `1.0` interchange.

### 3.2 (b) Hash function

**SHA-256**, computed over the canonical UTF-8 bytes
(`PlanCanonicalizer.ComputeDigest`).

### 3.3 (c) String encoding

The canonical form is **UTF-8**. The digest is rendered as **exactly 64 lowercase
hexadecimal characters**, with **no prefix and no truncation**
(`Convert.ToHexStringLower` over the 32 hash bytes).

Note the contrast with `FieldDefinitionHasher`, which renders `sha256:` + lowercase
hex for process-metadata fingerprints. The plan digest carries **no prefix**. These
are different hashes for different purposes and must not be conflated.

**No Unicode normalization** (NFC/NFD) is applied. All comparisons are ordinal, so
the path is culture-independent, consistent with `InvariantGlobalization=true`.
Two proposals differing only by Unicode composition are different proposals. This
is a conscious ratification of current behaviour, not an oversight: introducing
normalization would silently change every existing digest.

### 3.4 (d) Invariant — adapter enrichment is outside the canonicalized input

**Property.** No value produced by an adapter, by ADO, or by any server
round-trip can alter a Change Proposal's digest.

**Proof from the code path**, not assertion:

1. The digest is computed in `PlanDocumentParser` from the source document at
   parse time, **before any adapter or ADO call is made**.
2. Preview hands the already-computed `(CanonicalJson, Digest)` pair to the
   journal unchanged.
3. Every value an adapter later learns — the new revision from a PATCH, a
   `publishedId` from a seed publish, readback warnings — is written **only** to
   `plan_operations.result_json` / `warning`. Nothing on the apply path reparses
   the document or recomputes the digest.

The three fields that *look* like enrichment are not:
`expectedRevision`, `stagedIdentity`, and `expectedFingerprint` are **author-supplied
inputs** that happen to describe server state. They enter the digest because the
author wrote them, and they are checked by CAS at apply time. A mismatch **fails
the operation**; it never mutates the digest.

`SeedFingerprintCalculator` deserves explicit mention because it *does* consult
adapter-side state (the publish map) when resolving endpoint identity tokens. It
is nonetheless **outside** this boundary: it computes an independent seed
fingerprint that is *compared against* the plan's authored `expectedFingerprint`.
Its result is never fed back into the plan digest. Drift causes a refusal, not a
new digest.

**The invariant therefore already holds.** T3 must not weaken it. The single
rule that preserves it: *anything learned after parse is journal data, never
digest input.*

### 3.5 Verification point and failure behaviour

Apply re-reads the file, re-parses, re-canonicalizes, and compares the recomputed
digest against the confirmed digest with `StringComparison.Ordinal`. On mismatch it
returns a failure naming both digests **before any journal transition occurs** — so
a mismatched apply leaves the journal exactly as preview left it.

The CLI requires a non-blank `--confirm`; a missing confirmation is a usage error
(exit 2), and a lifecycle failure is exit 1.

**Digest binds bytes, not board state.** It covers the proposal document only.
Remote revisions are enforced per-operation by CAS at apply time. Both checks are
required; neither substitutes for the other.

---

## 4. Canonical semantic review model

**Decision: define a new derived projection. This is the one piece with no existing
implementation.**

Today `plan preview` returns `digest`, `canApply`, `issues[]`, `operations[]`, and
`pendingChanges[]`, where each operation row carries only `{ordinal, id, kind}`.
That is far short of Spec #729's requirement of affected items, semantic
operations, preconditions, consequences, digest, rationale, and authorization
choices. The model below closes that gap.

### 4.1 Serialized shape (`modelVersion` 1)

```json
{
  "model": "twig.change-proposal.review",
  "modelVersion": 1,
  "digest": "<64 lowercase hex>",
  "workspace": { "organization": "<string>", "project": "<string>" },
  "rationale": "<string|null>",
  "affectedItems": [
    { "id": 729, "type": "Spec", "title": "<string>", "state": "<string>", "role": "target|peer" }
  ],
  "operations": [
    {
      "ordinal": 0,
      "opId": "<string>",
      "kind": "batch|add-link|remove-link|publish-seed|delete",
      "target": { "workItemId": 729 },
      "summary": "<short semantic phrase>",
      "preconditions": [ { "kind": "expectedRevision", "value": "7" } ],
      "consequences": [ { "kind": "field-set", "field": "System.State", "to": "Doing" } ]
    }
  ],
  "authorizationChoices": [ "apply", "revise", "decline" ],
  "blockers": [ { "kind": "pending|issue", "workItemId": 740, "detail": "<string>" } ]
}
```

Field rules:

| Field | Required | Source |
|---|---|---|
| `model`, `modelVersion` | yes | Constant discriminator + integer version. |
| `digest` | yes | Verbatim from the proposal. Never recomputed by a renderer. |
| `workspace` | yes | From the proposal document. |
| `rationale` | yes, nullable | Supplied by the author; `null` when absent. |
| `affectedItems` | yes | Proposal targets plus link peers, enriched by refreshed reads. |
| `operations` | yes | One entry per proposal operation, in declared order. |
| `preconditions` | yes, may be empty | `expectedRevision` / `expectedFingerprint`. |
| `consequences` | yes, may be empty | Derived from the operation's kind and fields. |
| `authorizationChoices` | yes | The choices actually available for this proposal. |
| `blockers` | yes, may be empty | Pending rows and preview issues. |

`target` carries `workItemId` for every kind except `publish-seed`, which carries
`stagedIdentity` — a seed has no id until it is published, and the model must not
pretend otherwise.

### 4.2 Versioning strategy

`modelVersion` is a single integer, incremented **only** on a breaking change.
Additive optional members do **not** increment it.

### 4.3 Compatibility rules across adapters

1. A renderer **MUST ignore unknown members** within a known `modelVersion`. This
   is what makes additive evolution safe.
2. A renderer **MUST fail closed on an unknown `modelVersion`** — refuse to render
   and refuse to authorize. It must never partially render a model it does not
   understand, because a silently-dropped operation is exactly the failure Spec
   #729 exists to prevent.
3. A renderer **MUST render every member of `operations`, `preconditions`,
   `consequences`, and `authorizationChoices`**. Eliding a material entry is a
   compliance failure, not a presentation choice.
4. Enrichment is **additive only**. An adapter may add visual affordances; it may
   never add or remove an authorization choice, and never alter the digest.

### 4.4 The model is derived, never hashed

**The review model is NOT part of the digest input.** It embeds the digest; it does
not contribute to it.

This follows necessarily from §3.4. `affectedItems` carries live titles and states
from refreshed reads, which change over time. If the model were hashed into the
proposal, a title edit on an unrelated item would change the digest and invalidate
a valid authorization. Keeping the model derived is what lets it be enriched with
live board context while the digest stays stable.

The model is therefore **reproducible but not immutable**: the same proposal
yields the same operations, preconditions, and consequences on any adapter, while
`affectedItems` reflects the board at render time.

---

## 5. Journal record for an applied Change Proposal

### 5.1 What exists today

Durable, in the attached `pending` database, at durable schema version 7:

`plan_journals` — `digest` (PK), `schema_version`, `organization`, `project`,
`source_path`, `canonical_json`, `state`, `previewed_at`, `confirmed_at`,
`completed_at`, `error`.

`plan_operations` — `digest` (FK, cascade), `ordinal`, `op_id`, `kind`, `state`,
`request_json`, `started_at`, `applied_at`, `verified_at`, `result_json`, `error`,
`warning`; PK `(digest, op_id)`.

### 5.2 Spec #729's five audit fields, mapped

| Required field | Status | Where |
|---|---|---|
| Canonical model snapshot | **PARTIAL** | `canonical_json` holds the *proposal*, not the *review model*. |
| Digest | **PRESENT** | `plan_journals.digest`, primary key. |
| Authorization mode | **ABSENT** | — |
| Authorizer identity | **ABSENT** | — |
| Rationale | **ABSENT** | — |

Plus the three this note must also settle:

| Required | Status |
|---|---|
| Storage location | **PRESENT** — `source_path` plus the fixed durable store. |
| Retention posture | **ABSENT** — no prune, vacuum, or expiry exists. |
| Unapplied outcomes | **PARTIAL** — see §5.5. |

### 5.3 Decision — additive migration `[8]`

Add to `plan_journals`, all nullable so existing rows migrate cleanly:

| Column | Type | Required at authorization | Source |
|---|---|---|---|
| `authorization_mode` | TEXT | **yes** | `human` or `model`. From the session's steering mode, consumed via the session/authorization seam. |
| `authorizer_identity` | TEXT | **yes** | The signing human's identity, or the model identity for an AFK authorization. |
| `rationale` | TEXT | no | Author-supplied; `NULL` when absent. |
| `review_model_json` | TEXT | **yes** | The §4 model serialized at authorization time. |
| `authorized_at` | TEXT | **yes** | ISO-8601, at the moment authorization was recorded. |

`authorization_mode` takes exactly `human` or `model` — a closed set of two. It is
**not** the same vocabulary as `Custom.WayfinderExecutionMode` (`HITL`/`AFK`), which
describes a *session*. Conflating a session property with an audit fact is how
audit trails become unreconstructable; they are kept distinct deliberately.

`review_model_json` is stored separately from `canonical_json` because they answer
different questions: `canonical_json` is *what was authorized*, `review_model_json`
is *what the authorizer was shown*. Spec #729's audit goal — reconstruct what
happened without replaying the tool — requires both.

**Why nullable columns on an additive migration.** The journal's durable store is
never dropped (unlike the disposable mirror, which is recreated on version
mismatch). Rows predating this migration are real audit history and must survive
it. Readers therefore treat `NULL` in these columns as "predates authorization
recording", never as "unauthorized".

### 5.4 Retention posture

**Decision: retain indefinitely. No prune, no vacuum, no expiry.**

This ratifies current behaviour, and the ratification is deliberate rather than
incidental. An audit record whose retention is "until something garbage-collects
it" is not an audit record. The journal is bounded in practice by the number of
proposals a workspace applies, each row is small, and `plan_operations` cascades
on delete if a journal row is ever removed by an explicit future operation.

**Deferred with an explicit trigger:** if a workspace's journal growth becomes a
measured problem, an *explicit operator-invoked* archival verb may be specified.
It must never be implicit, and time-based automatic expiry is ruled out here.

### 5.5 Unapplied outcomes

Behaviour today, established from the lifecycle code:

- **Pre-journal refusals** — invalid document, unreadable file, path outside the
  workspace, digest mismatch, pending rows present, apply with no prior preview —
  create **no journal row at all**. They are top-level refusals.
- **A previewed but never-confirmed proposal** stays `Planned` **indefinitely**.
- **Operation-level failures after apply begins** *are* journaled: the row takes
  `Failed` or `Indeterminate` with its error, and the header terminalizes to
  `Failed`.

**Decisions:**

1. **Pre-journal refusals stay unjournaled.** A document that never became a valid
   proposal has nothing to audit. Journaling every malformed file would fill the
   audit trail with non-events.
2. **`Planned` is retained as the resting state** for a previewed-but-unauthorized
   proposal. It is already the correct representation of "offered, not decided".
3. **An explicit decline/cancel/defer outcome is DEFERRED**, with a named trigger.

On (3): recording "a human was shown this and said no" requires a new terminal
state distinguishable from `Failed`, and Spec #729 **itself** defers
cancel/defer/decline journal outcomes under §Terminal/text fallback. Deciding it
here would pre-empt a deferral the spec makes deliberately, and would add a
lifecycle state T4 has no consumer for.

**Follow-on trigger:** when AB#743 (T4) specifies the fallback's cancel/defer/decline
interaction shape, that ticket must decide whether an explicit decline is journaled
and under which state name. Until then, a declined proposal is indistinguishable
from an unauthorized one, and that is an accepted, recorded limitation — not an
oversight.

### 5.6 What is NOT added

**No actor identity on `plan_operations`.** Authorization binds to the whole
proposal at one digest, so identity belongs on the header. Per-operation identity
would imply operations can be authorized separately, which contradicts S3.

---

## 6. Consequences for T3 and T4

**AB#742 (T3)** implements against §2 (payload), §3 (digest — ratified, must not be
weakened), and §4 (review model — new work; preview must return it).

**AB#743 (T4)** implements against §5 (journal fields and their sources at
authorization time) and consumes §4's model for the terminal/text fallback. It
owns the §5.5 follow-on trigger.

Neither ticket may alter the digest contract in §3 without superseding this note.

---

## 7. Explicitly still deferred

| Deferred | Why | Trigger |
|---|---|---|
| Decline/cancel/defer journal outcome | Spec #729 defers the interaction shape under §Terminal/text fallback. | AB#743 specifies fallback interaction. |
| Cross-machine proposal transport | Not required by #729; the canonical form permits it, nothing specifies it. | A concrete multi-machine requirement. |
| Journal archival verb | Retention is "retain indefinitely" until growth is a measured problem. | Measured journal growth complaint. |
| Recipe input schema, versioning, eligibility | Out of scope per #729 §Out of Scope; T1/recipe-authoring owns it. | Follow-on recipe-authoring spec. |
| Unicode normalization of canonical form | Would change every existing digest. | Only if a concrete cross-platform defect appears. |

---

## 8. Verification

This note is a design artifact and asserts no runtime behaviour of its own. Every
claim about current behaviour above was read from the source on `origin/main`
(`70752d0e`) at the cited locations, not inferred from documentation.

The load-bearing claim — §3.4's enrichment invariant — is falsifiable: if any code
path recomputes a plan digest after an ADO call, or feeds a server-supplied value
into `PlanCanonicalizer`, this note is wrong and T3 must not proceed on it. No such
path exists today.
