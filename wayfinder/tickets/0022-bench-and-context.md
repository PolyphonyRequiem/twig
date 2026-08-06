---
id: 0022
title: Bench and Context — what replaces the Workspace read model
type: task
status: open
blocked_by: []
---

## Question

`CONTEXT.md` §4 retired `Workspace` in favour of **Connection** and **Bench**, and left three
questions open: whether the pending set is per-Bench or per-Connection, whether a Bench scopes
the sync boundary, and whether benches must be concurrent or merely switchable. This ticket
answers what a Bench and a Context each *are*, and in what order they get built.

It exists because "implement the bench pivot" is ambiguous between a user-facing capability
(create, name, switch, list) and a structural change underneath it. This ticket rules that it is
the structural change, and that the capability falls out of it.

## What was NOT the blocker

The brief that spawned this session asserted that benches have nowhere durable to live until
0013's durable store is built. **0013 is built.** `pending.db` is ATTACHed as schema `pending`,
is never dropped, and already carries `pending_changes`, `publish_id_map`, `seed_links`,
`staged_identities` and `publish_intents`, versioned by `PRAGMA pending.user_version` over an
additive migration ledger.

So there is **no prerequisite**. A Bench needs a table added to a store that is already
load-bearing — through the additive migration path, since that store can never be dropped and
recreated.

## Decision (Daniel, 2026-08-06)

### 1. The three nouns

- **Connection** — one `{org}/{project}` ADO endpoint with its cache and credentials. Owns the
  pending set. Is the sync unit.
- **Bench** — a named, durable, saved backlog. Holds **pinned items**, **queries** and
  **exclusions**. Several exist; you select one. Shared by everything standing on it. Never a
  sync unit.
- **Context** — a **disposable place to stand**, opened by a caller and closed when done. Holds
  only where you are: the active item, and what is derived from it (parent chain, children).

Daniel's framing, verbatim in shape: *a script creates a Context, works within it against a
given Bench, then cleans the Context up when done, or recycles it for the next script.*

### 2. A Bench is a saved backlog, not a scratch surface

Benches are used across scenarios "in the way that different backlogs or queries might" —
*my sprint*, *the bugs I own*, *release blockers*. Named once, returned to.

A Bench stores the **rule**, never the results. What its queries return is recomputed.

### 3. Not everything needs a Bench or a Context

Commands split into two kinds:

- **Standing commands** — `tree`, `nav`, `set` with no target named. These need a Context.
- **Targeted commands** — anything that names its own work item. These need a **Connection and
  nothing else**.

The rich CLI lives mostly in the first kind. The script CLI lives mostly in the second. MCP was
already forced into the second kind by 0021.

Consequences, stated so they are not re-litigated:

- Reading one work item does **not** need a Bench.
- It does **not** get added to a Bench.
- **Being on a Bench means one thing only: you can stand there.** It is a place, not a record of
  interest.
- A targeted read must not mutate a Bench as a side effect, or scripts silently move the user's
  view.

### 4. The single shared slot is the real defect

Today the active work item is **one row in a shared store** (`active_work_item_id`), not
per-connection and not per-session. 0021 patched this **at the MCP surface** by requiring every
MCP tool to name its target. The slot itself is still there.

Giving a Context an identity removes the slot **by construction**, at every surface, rather than
per surface. This is the architectural payoff and the reason the change is worth its size.

### 5. Shared Bench view — no private pins

Every Context standing on a Bench sees the same Bench. A pin is a change to the Bench and is
visible to everyone standing on it.

Rejected: per-Context private pins. Privacy is not a requirement, and it would re-split durable
state across two levels — the exact split stage two exists to undo. A caller that wants a
different view opens a **different Bench**; no second mechanism is needed.

### 6. Build order — model B, staged

Two models were compared:

- **A — durable Context, Bench as a label over a group of them.** Cheap; close to today's shape.
- **B — disposable Context, Bench durable and owning pins/queries/exclusions.**

B wins on the tests that matter: a script run twice inherits nothing; two Contexts on one Bench
agree by construction; an abandoned Context leaks nothing. A wins only on build cost.

**Ruling: B, staged.** A is the migration step, not a rival.

- **Stage 1 — Context gets an identity.** Every caller opens its own Context. The single shared
  `active_work_item_id` slot goes away. No user-visible feature ships. This alone closes the
  root cause 0021 patched at one surface.
- **Stage 2 — Bench becomes durable.** Pins, queries and exclusions move off the Context onto a
  Bench table in `pending.db`. `create`, `name`, `switch`, `list` ship here.

### 7. Mandatory guard

**Seeds and unpushed edits must remain visible even when no query on the current Bench selects
them.** Otherwise switching Bench hides work twig owes ADO — the same class of defect as 0004's
objection to reconciling per-Bench.

### 8. Vocabulary warning

Do **not** describe what a Bench does to Contexts as "reconciling" them. `Reconciliation` is the
module 0004 named, owning staged → published → reconciled → invalidated against ADO, and 0004
already ruled a Bench is **never** a sync unit. What a Bench does is **merge views for display**:
the same item reached by different routes is shown once.

## What this closes in the existing map

- **"Does a Bench scope the sync boundary, or only reads?"** — Already answered by 0004 §2:
  it does not. Reconciliation scopes to the pending set, per Connection. Recorded here so the
  map's *not yet specified* list can drop it.
- **"Must benches be concurrent in one process or merely switchable?"** (`CONTEXT.md` §4) —
  Answered: **switchable**. Concurrency lives at the Context level, not the Bench level. Several
  Contexts may be open at once, each naming which Bench it stands on.
- **"Whether `WorkingSet` survives as a Bench's derived projection"** (`CONTEXT.md` §4) —
  Answered: the current derived working set is **exactly** a Bench with one hard-coded query
  plus hand pins and hand exclusions, computed per access with nowhere to persist the hand
  edits. Stage 2 promotes it rather than replacing it.

## Still open — deliberately not decided here

- **Abandoned Contexts.** What reclaims a Context a script never closed. Not a blocker for
  stage 1.
- **Closing a Context that holds unpushed edits.** Whether close is refused, or the edits simply
  stay pending against the Connection. Must not silently discard.
- **Whether the pending set is *stored* per-Bench or per-Connection.** 0004 §6 left this open;
  only the *reconciliation* boundary is settled (per-Connection). Stage 2 will force the answer.

## Sequencing note

Stage 2 adds twig's first *new* durable table since 0013. It must go through the additive
migration ledger; `pending.db` can never be dropped and recreated.
