---
id: 0001
title: What is twig for?
type: grilling
status: closed
claimed_by: starbright-baseline (session 2026-07-26)
---

## Question

Is twig "a great local tool that happens to be usable by a team", or "a team's shared work-management substrate that happens to run locally"? Everything downstream hangs on this. Today the code says the former: state is a per-workspace SQLite cache, ADO is the source of truth, and `twig init --force` can discard local state without loss. But the stated purpose is work management ACROSS a dev team, and the audit found reconciliation is not a named concept anywhere — 11 scattered sites across 4 assemblies. If the answer is "shared substrate", the local/remote reconciliation module becomes the spine of the architecture and the persistence question becomes dependent on it. If it is "local tool", reconciliation stays a sync detail and the surface seam is the more valuable work.

## Answer

**Twig is a single-user local tool that each developer on a team runs independently. The
shared substrate is ADO itself — never twig.** Two teammates never reconcile with each
other; each reconciles with ADO on their own. Nothing is persisted into the repo.

The dichotomy in the question was false, and the "team substrate" horn was partly an
invention of the review rather than the owner's intent. But the correctness work the
review called for survives intact, for a reason the question missed: **twig already has a
second writer, and it is ADO.** The two-party sync problem is real at N=1 users.

### 1. The pending set is the only thing twig owns

Local state is disposable **except unpushed local changes**. The cache is a rebuildable
mirror of ADO; the pending set (staged seeds, staged notes, staged field edits) exists
nowhere else and is genuinely lost if destroyed.

These two kinds of state have opposite requirements and currently share a schema. That
shared schema — specifically `pending_changes.work_item_id` referencing `work_items(id)` —
is the documented root cause of #268, #269, #270 and #271, and the ID-space half of #280.
**"Disposable remote mirror" and "durable local drafts" should not be the same table.**

Consequence: `twig init --force` is safe if and only if the pending set is empty. Today it
is offered as generic advice (`Program.cs:324-328`), which is why #271 mattered.

### 2. There are not two modes — there is one tool with two states

The distinction that matters is **whether the pending set is empty**:

- **Pending empty** — twig is a fast reader over ADO. The cache is pure optimisation.
  None of the hard machinery needs to run.
- **Pending dirty** — twig is a local staging area with real unpushed work, and the whole
  lifecycle below applies.

This is the rule that decides where effort goes: the careful machinery is scoped to the
pending set, not to the whole store.

### 3. The lifecycle (owner's words, 2026-07-26)

> make local changes, selectively push changes, reconcile sequencing errors when batch
> operations are performed, recover safely, reconcile conflicts interactively where a user
> is in the loop, or warn and advise for follow up operations where non-interactive.

This is the named module the review found missing — the 11 scattered sync sites with no
owner. Four properties fall out of it:

**a. Selective push, per item.** The user-facing selection unit is **per item** ("push that
bug I edited"), decided. `pending_changes` stores field-level rows, so per-change is the
storage unit; batching is a property of the dependency graph, not of the selection.
Whether ADO's API permits a per-item slice cleanly is a research question — see
`%TEMP%\twig-review\research-ado-batch-push.md`.

**b. Sequencing is a real dependency graph, not a preference.** Owner: *"with seeds, we
need to create items before we can create a relationship between them."* Parent-before-
child is forced by the API, not chosen.

**c. Therefore push-and-recover, not validate-then-push.** Because a chain can fail
mid-flight on the network, no amount of up-front validation prevents a partial publish.
Twig must be able to **resume a half-published chain**. This is not a fallback; it is the
required design.

**d. Interactivity is a surface property, not a formatting one.** A human gets an
interactive conflict prompt; an agent or script gets a warning plus advice for follow-up.
This is the first surface distinction derived from *behaviour* rather than output shape,
and it reframes ticket 0002: the surfaces differ in **whether they can be asked a
question**.

### 4. Record intent before the call, not the result after it

The publish path today creates remote state at step 7 and records it at step 10. The code
concedes the consequence in its own comment (`SeedPublishOrchestrator.cs:~250`):

> the ADO item created in Step 7 — outside this transaction — was orphaned. Every retry
> then made another duplicate (PolyphonyRequiem/twig#270).

#270 patched the FK ordering *inside* step 10. **The window between 7 and 10 is still
open.** A crash there still orphans a real ADO item with no local trace.

The decided shape: **write the intent durably, make the call, record the outcome.** On
restart, an intent with no outcome is reconcilable — twig can ask ADO whether it landed.

This requires an **idempotency key**: something twig stamps on creation so it can later
ask "did my create already happen?" Verified absent today — nothing is stamped on create
(`AdoRestClient.CreateAsync:113`, no tag/field written by
`SeedPublishOrchestrator`). Mechanism (tag, field, or ADO-side dedupe) is undecided and
depends on the batch research.

### 5. Twig polls, and therefore the user owns the sync boundary

There is no self-servable event source to make refresh event-driven, which is *why* the
staleness clock ended up buried in a read path (`SyncCoordinator.SyncItemAsync:51`) —
nobody chose it; the absence of events pushed the decision into whatever code touched the
network. Polling is structural, so the sync boundary must be explicit and user-owned
rather than an implicit side effect of reading. Evidence pending in
`%TEMP%\twig-review\research-ado-notifications.md`.

### Consequences for the rest of the map

- **0004 (does reconciliation exist?)** — Yes, it must, and §3 is its specification.
  Scope it to the pending set. `ConflictResolver` already does the hard part and is simply
  unreachable from the paths that matter.
- **0005 (persistence model)** — The source-of-truth ambiguity that blocked it is
  resolved: ADO is truth, the cache is disposable, the pending set is owned. Files-on-disk
  **loses its strongest argument** (git-shared team state) since nothing is committed to
  the repo. The live question is narrower: should the pending set be stored separately
  from the cache?
- **0006 (team-scale baseline revision)** — **Mis-titled.** A persisted baseline revision
  is still valuable, but for local↔ADO three-way merge, not cross-teammate reconciliation.
  Rename.
- **0002 (four surfaces, one seam?)** — Gains a behavioural axis: interactive vs
  non-interactive (§3d) may matter more than output shape.
- **0003 (seed identity model)** — Now also owns the idempotency key (§4), not just the
  negative-ID recycling fixed by #280.
- **0011 (startup and observability)** — The README promises "sub-100ms cold start". The
  measured ~5.08s spike on `twig --help` is a violation of a stated promise, not merely a
  preference.

### Not decided here

- The storage mechanism for the pending set (separate table, separate file, separate DB).
- The idempotency key mechanism.
- Whether ADO's `$batch` is atomic, which decides how much recovery machinery is needed.
- Whether the TUI is committed or exploratory.
