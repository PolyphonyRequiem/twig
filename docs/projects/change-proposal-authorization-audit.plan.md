# Change Proposal — authorization, terminal/text fallback, and audit record (T4, AB#743)

**Work Item:** #743 (Task, parent Spec #729 — "Change Recipe and Change Proposal")
**Type:** Implementation record + one settled deferral
**Status:** ⬛ Settled
**Predecessor:** AB#742 (T3, commit `25fa1455`) — the Change Recipe seam, the immutable
`ChangeProposal`, and the canonical semantic review model this ticket consumes.

---

## What this ticket built

Three things on top of T3's core, plus the one design question T2 §5.5 explicitly deferred
to it.

**1. The authorization gate.** `ProposalAuthorizationGate.Evaluate` is a pure function of
the supplied authorization, the digest recomputed from the proposal file, and the session's
steering mode. It runs in `PlanLifecycleService.ApplyAsync` as the last top-level refusal —
after the digest, pending-row, and journal-exists guards, and *before* the first
`Planned → Confirmed` transition, because confirming a journal is already a claim that
someone released the proposal.

Every path fails closed. There is no permissive branch:

| Condition | Outcome |
|---|---|
| No authorization supplied | Refused |
| Authorization bound to a different digest | Refused |
| Mode is not what the session's steering requires | Refused |
| Authorizer identity blank | Refused |

**2. The terminal/text fallback.** `ChangeProposalReviewRenderer` renders the T2 §4.1 model
— the one T3 already built — and is what `twig proposal preview` shows a human. It fails
closed on an unknown `modelVersion`, renders every operation, precondition, consequence,
affected item, blocker and authorization choice, echoes the digest verbatim, and never adds
or removes a choice the model did not offer.

The previous thin human summary (digest, `canApply`, one line per operation id) was
**removed rather than kept alongside**. It showed a reviewer an operation's id and kind but
never the field values it would write — which is exactly the "authorized a mutation they
were never shown" failure the model exists to prevent. Two presentations of one proposal
would have left the shorter one as the thing a hurried reviewer actually reads.

**3. The audit record.** Durable migration `[9]` adds `authorization_mode`,
`authorizer_identity`, `rationale`, `review_model_json`, and `authorized_at` to
`proposal_journals`, per T2 §5.3. `DurableSchemaVersion` is now 9.

All five columns are nullable, and that is load-bearing rather than lenient: the durable
store is never dropped, so rows written before AB#743 are genuine audit history. A `NOT
NULL` column with a backfilled default would have **manufactured an authorization that
never happened**, and no reader could then distinguish the invented record from a real one.
`NULL` therefore means *predates authorization recording* — never *unauthorized*.

`review_model_json` is stored beside `canonical_json`, not instead of it: the latter is
**what was authorized**, the former is **what the authorizer was shown**. Spec #729's audit
goal — reconstruct what happened without replaying the tool — needs both, because the
proposal alone cannot show what the reviewer saw and the review model alone is not what the
apply was bound to. The write happens *before* the first operation runs, so a crash
mid-apply still leaves a record of who released the proposal.

The write is **first-authorization-wins**. A resumed apply re-presents the same digest, and
the fact worth keeping is the authorization that originally released the proposal, not the
moment a crashed run was picked back up.

---

## The steering-mode source stays deferred, on purpose

Spec #729 §Authorization defers where the session steering mode comes from, and instructs
this ticket to consume it through whatever seam is contracted — and, if none exists, to
**name the minimum consumption interface and stop there** rather than invent a source. No
such seam existed anywhere in `src/`, so this ticket names
`ISessionSteeringModeProvider` (`SessionSteeringMode Resolve()`) and stops.

The production binding is `UnresolvedSessionSteeringModeProvider`, which resolves
`Unresolved`. That is a complete implementation of the contract, not a placeholder: "no
session/authorization contract has supplied a mode" is exactly what it reports, and per
Spec #729 anything that does not resolve affirmatively to AFK takes the human-steered path.
Guessing `Afk` would have been the only unsafe answer available.

🔴 **An implementation MUST NOT derive the mode from a transport attachment** — the
worktree, agent session id, terminal host, or an environment variable a pane inherited. A
transport is how a session was delivered; it is not evidence of how that session is being
steered. Binding the two would let moving a session between panes silently change who may
authorize a mutation from it. Two tests defend this by asserting the gate's decision and the
fallback's output are unchanged when transport markers are present in the environment.

`authorization_mode` (`human` | `model`) is deliberately **not** the
`Custom.WayfinderExecutionMode` vocabulary (`HITL` | `AFK`). One is an audit fact about a
single apply; the other describes a session, which can change or be resolved late.
Conflating a session property with an audit fact is how audit trails become
unreconstructable.

**Not introduced here:** additional AFK preflight gates beyond steering-mode selection —
refresh-read, primary-scope match, local-claim ownership, rationale content. Spec #729 puts
them out of scope, and adding one silently would change what "authorized" means for every
existing caller.

---

## The T2 §5.5 follow-on trigger — decided

T2 §5.5 deferred the decline/cancel/defer journal outcome with a named trigger: *"when
AB#743 specifies the fallback's cancel/defer/decline interaction shape, that ticket must
decide whether an explicit decline is journaled and under which state name."*

**Decision: an explicit decline is NOT journaled, and no new terminal state is introduced.
`Planned` remains the resting state for a proposal that was offered and not released.**

Reasoning:

1. Recording "a human was shown this and said no" needs a terminal state distinguishable
   from `Failed`. Adding a lifecycle state costs every reader that switches on
   `PlanOperationState` — CLI, MCP, recovery — and T4 ships **no consumer** for it.
2. The interaction shape that would give the state meaning is itself still deferred. Spec
   #729 defers the fallback's prompt shape and cancel/defer/decline mechanics, and AB#743's
   own scope guard repeats that they are not specified here. A state name settled before the
   interaction that produces it is a guess dressed as a decision.
3. Nothing is lost that can be recovered later. A decline leaves the journal `Planned`,
   which is already the correct representation of *offered, not decided*.

**Accepted limitation, recorded rather than overlooked:** a declined proposal remains
indistinguishable from an unauthorized one. **New trigger:** the first ticket that specifies
the fallback's interactive prompt shape owns introducing the outcome state, and it will then
have a consumer for it.

---

## Verification

- 9,383 tests pass. New behavioral tests cover the gate (digest binding both ways, mode
  mismatch both ways, blank identity, unresolved-steering fallback, transport independence),
  migration `[9]` row preservation, the write-once audit record, the audit row's content on
  a real apply, and the fallback's material-entry completeness and fail-closed version check.
  Each names the bug it defends against.
- Smoke-tested against the real CLI binary and a real durable store:
  - `proposal preview` renders the fallback with every operation, precondition, consequence,
    affected item and authorization choice.
  - `proposal apply` without `--authorize` refuses, leaves the journal `Planned`, and issues
    no ADO call.
  - `proposal apply` with a mismatched `--confirm` refuses on the digest.
  - An authorized apply wrote the full audit row — mode `human`, authorizer, rationale,
    timestamp, and a 748-byte review model beside the 216-byte canonical proposal — while
    the operation's stale `expectedRevision` stopped the board write, so the target's
    revision was unchanged.
  - A **copy of a real durable store at version 7** (the two plans that closed AB#742 and
    claimed AB#743) upgraded 7 → 9 through the real binary with both journal rows, both
    operation rows and every timestamp intact, and all five audit columns `NULL`.

## Not changed

The digest contract (T2 §3) is untouched. The review model embeds the digest and never feeds
it — hashing it would let an unrelated title edit invalidate a still-valid authorization.
`PlanCanonicalizer` was not modified.
