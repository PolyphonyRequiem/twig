---
id: 1007
title: Build the Bench — move pins, queries and exclusions to the durable store
type: task
blocked_by: []
tracked_in: [144, 145, 146, 147, 148, 149, 150, 151]
---

## Why this is first

> 🔴 **CORRECTION (2026-08-06, during ADO #144). The data-loss premise below is FALSE. Do not
> inherit it.** The tables it names are declared and dropped, so a grep finds exactly what this
> brief describes — but **nothing reads them**. `ITrackingRepository` resolves to
> `FileTrackingRepository` (the only registration in `TwigServiceRegistration.cs`), and
> `SqliteTrackingRepository` has **zero construction sites**. Pins already moved to a file
> beside the cache that a rebuild does not touch, and a one-time import carried existing rows
> across. Persistence ruling 0005 records the same fact from the other direction.
>
> **Consequences, because this changes the plan and not only a sentence:**
>
> - **Bench-first is still right, for a different reason.** Not "it fixes silent data loss" —
>   that is already fixed. It is that a Bench is a self-contained product change needing no
>   Context work, leaving the computed view behaving identically throughout.
> - **Mandatory tests 1 and 2 below PASS TODAY** and must not be written as briefed. Doing so
>   produces exactly the inert guard this brief warns about in red. `docs/specs/bench.spec.md`
>   replaces them with tests that can actually fail.
> - **A migration is still mandatory and it is a DIFFERENT migration** — out of the file, into
>   the durable store (ADO #146). The silence argument is unchanged and still decisive.
> - **Exclusions were cut from the Bench entirely** (2026-08-06). `exclude` does not currently
>   exclude anything: nothing subtracts excluded items from the view. Building it into the
>   Bench would be *specifying a behaviour for the first time* wearing the costume of a data
>   move. The existing commands are left exactly as they are.
>
> The authoritative document is now `docs/specs/bench.spec.md`. Where the two disagree, the
> spec wins.

0022 staged the Bench pivot as *Context first, Bench second*. **Reversed by the owner
(2026-08-06)**, and the evidence supports the reversal rather than merely permitting it.

**`tracked_items` and `excluded_items` are in the DROPPABLE mirror**
(`SqliteCacheStore.cs:420` lists both in `DropAllTables`; declared at `:518` and `:524`).
They are the user's **hand pins and hand exclusions** — the one part of the working set ADO
**cannot** rebuild. A `SchemaVersion` bump destroys them silently.

That is #271's class wearing a quieter coat: a healthy-cache rebuild that eats work the user
did. It is louder for pending edits (0013 fixed those) and near-invisible for pins, which is
worse for detection, not better.

By 0005 §3a's own test — **can ADO rebuild it?** — both tables were always misfiled. Moving
them is not new scope; it is 0013 finishing its own sentence.

So Bench-first: it is a **data-loss fix that happens to be the feature**, it needs no Context
work to land, and it leaves `WorkingSet` behaving identically throughout.

## What a Bench is (settled — do not relitigate)

From 0022 and `CONTEXT.md` §4:

- A **Bench** is a named, durable, saved backlog: **pins + queries + exclusions**. It stores
  the RULE, never the results.
- Several exist. You select one. It is **shared by every Context standing on it** — no
  private pins.
- A Bench is **never a sync unit**. Reconciliation stays per Connection (0004 §2).
- Benches are **switchable**; concurrency lives at the Context level (0022).
- Vocabulary: what a Bench does to Contexts is **merge views for display**, never
  "reconcile" — that word belongs to 0004's module.

## Scope

### 1. The durable tables

Add to `pending.db` (schema `pending`, ATTACHed, never dropped) through the **additive
migration ledger** — `PRAGMA pending.user_version`. This is the first *new* durable table
since 0013; it can never be dropped-and-recreated, so it needs a real migration forever.

Shape (proposed, not ruled — the implementer may argue):

- `benches` — id, name, connection, created/updated.
- `bench_pins` — bench id + work item id. **Replaces `tracked_items`.**
- `bench_exclusions` — bench id + work item id. **Replaces `excluded_items`.**
- `bench_queries` — bench id + the query rule. The **current-iteration-for-this-user** query
  that `WorkingSetService` hard-codes today becomes the first row of this table, not a
  special case beside it.

### 2. Migrate the two mis-filed tables

`tracked_items` and `excluded_items` move OUT of the mirror and INTO the durable store, and
are removed from `DropAllTables`.

🔴 **Data must survive the move.** Unlike 0013 — which took a clean break because a non-empty
pending set could refuse the operation — pins are silent, so a clean break here loses work
with no prompt. **Write the migration.** If that proves impossible, this ticket blocks rather
than shipping a silent break.

### 3. The default Bench

`WorkingSetService.ComputeAsync` (`src/Twig.Domain/Services/Workspace/WorkingSetService.cs`,
100 lines) is **already a Bench**: one hard-coded query (current iteration, filtered to the
user), plus hand pins, plus hand exclusions, computed per access with nowhere to persist the
hand edits.

**Reconstruct it as the default Bench, so behaviour is unchanged on day one.** Same items,
same order, same output. `WorkingSet` remains a derived projection OF a Bench — promoted, not
replaced, so there are no call sites to rewrite (0022, 1006 §2).

**This is the acceptance bar for the whole ticket:** with one Bench and no user action, twig
behaves exactly as it does today.

### 4. Verbs — `create`, `name`, `switch`, `list`

Standing-command territory. Two constraints inherited from 0023, both non-negotiable:

- **An unknown Bench is a HARD ERROR.** Non-zero exit, name it, say what to do. Not a
  fallback, not a warning, not a silently-created Bench. twig is moving to the *handle*
  family; a Bench that always resolves reproduces the kubectl defect one level up.
- **Deleting a Bench never silently discards.** Pins and exclusions are the user's own work.
  Deleting a Bench with contents reports what it holds. No habitual `--force` (#271 class).

### 5. The mandatory guard

🔴 **Seeds and unpushed edits stay visible even when no query on the current Bench selects
them.** Otherwise switching Bench hides work twig owes ADO — the same objection 0004 §2 made
to reconciling per-Bench.

Cover this with a test that FAILS without the guard: stage an edit on an item outside the
Bench's queries, switch Bench, assert it is still surfaced.

## Tests — red first, and prove they are red

Per `AGENTS.md`: a regression test must **fail on the unfixed code**. Verify against a
detached worktree at the pre-fix SHA:

```bash
MSYS_NO_PATHCONV=1 git worktree add --detach ../twig-baseline <pre-fix-sha>
```

Minimum set, each of which must fail today:

1. **Pins survive a schema bump.** Pin an item, force a `SchemaVersion` mismatch, assert the
   pin is still there. **Fails today** — `DropAllTables` eats it. This is the defect.
2. **Exclusions survive a schema bump.** Same shape.
3. **Default-Bench parity.** With one Bench and no user action, the computed working set is
   byte-identical to today's. Compare against a captured baseline, not by eye.
4. **Unpushed edits outside the Bench stay visible** (§5).
5. **Unknown Bench is a hard error** — non-zero exit, not a fallback.
6. **Migration preserves existing pins and exclusions** across the move.

🔴 A structural guard that cannot fail is worse than none — 0021 shipped an IL-walk guard that
was silently inert and passed at the pre-fix SHA. **Run the suite at the baseline SHA and
report which tests failed there**, by name. "They should fail" is not evidence.

Verdict only via `tools/run-tests.sh`, grep `TWIG-VERDICT`. Never grep `Passed!`.

## Out of scope

- **Context work.** 0022 stage 1 (per-caller Contexts, killing the shared
  `active_work_item_id` slot) is a separate card. This ticket does not touch `IContextStore`.
- **Bench addressing.** 0023 ruled how a *Context* is addressed; whether a Bench is named by
  the same mechanism is deliberately unanswered here. Use the simplest thing that works and
  flag it.
- **Whether the pending set is stored per-Bench or per-Connection.** Still open (0004 §6,
  1006 §1). This ticket must not settle it by accident — if the implementation forces the
  question, **block and say so** rather than deciding it in code.
- **Whose job Bench management is** (1006 §4). CLI verbs for now.

## Prior reading, in order

`CONTEXT.md` §4 · `wayfinder/tickets/0022-bench-and-context.md` ·
`wayfinder/tickets/0023-context-addressing.md` (§ rulings) ·
`wayfinder/tickets/0013-split-store-by-durability-class.md` (the migration ledger) ·
`wayfinder-1.0/tickets/1006-what-is-a-bench.md` (superseded, but §1 and §4 are still live)
