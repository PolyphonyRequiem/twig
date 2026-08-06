---
id: 1006
title: What is a Bench, concretely enough to build
type: decision
status: open
blocked_by: []
---

## Question

`Bench` is named, its meaning is agreed, and its storage is decided — but nobody has
decided what it *does*. Four questions remain open across two maps and `CONTEXT.md`, and
they are entangled enough that answering them one at a time produces contradictions.

This ticket answers all four together and leaves Bench buildable.

## What is already settled — do not relitigate

- **What a Bench IS** (`CONTEXT.md` §4, wayfinder 0001): a named, persistent, switchable
  set of work items. Plural, selected, not derived. From "workbench" — what is on your
  bench right now. Several may exist concurrently.
- **A Bench is NOT `WorkingSet`.** `WorkingSet` is singular, derived, recomputed on every
  access, with no identity or persistence. Adjacent ground, different concept.
- **A Bench does NOT scope the sync boundary** (0004 §2, owner 2026-07-27).
  Reconciliation scopes to the pending set, **per Connection**. A Bench is a *selection* —
  what you are looking at — and reconciling per-Bench would silently skip pending edits
  outside the current view. **This closes `CONTEXT.md` §4's "whether a Bench scopes the
  sync boundary as well as reads" — it does not.** §4 still lists it as open; that text is
  stale and this ticket should correct it.
- **Benches live in `pending.db`** (0005): the durable store, never dropped, real
  migrations — not the disposable `twig.db` mirror. A Bench cannot be rebuilt from ADO,
  so it is durable by 0005's own test.
- **`Sprig` is reserved** and is not a synonym for Bench. Do not spend it here.

## What to decide

1. **Is the pending set stored per-Bench or per-Connection?** Explicitly left open by both
   0004 (§ "still open") and 0005. Note the trap: 0004 settled that reconciliation
   *scopes* per-Connection, which does not by itself decide where the set is *stored*.
   Deciding "per-Bench storage" while keeping per-Connection reconciliation is coherent but
   needs saying out loud, because it means reconciliation reads across benches.
2. **Does `WorkingSet` survive as a Bench's derived projection, or die?**
   (`CONTEXT.md` §4, restated in 0004.) If it survives, what recomputes it and when. If it
   dies, what replaces its call sites.
3. **Must benches be concurrent in one process, or merely switchable?**
   (`CONTEXT.md` §4.) This is a cost question with a real blast radius — concurrent benches
   put a selection in scope on every read path; switchable makes it ambient process state.
4. **Whose job is Bench management?** The 1.0 map lists "whether Bench management is a TUI
   job" inside the unspecified-TUI cluster. Given 1003's ruling that *the TUI is not a
   place you go, it is what a command does when it is interactive*, the likely answer is
   that Bench management is CLI verbs that may become interactive — but it is unstated, and
   the four experiences in `CONTEXT.md` §4 name MCP as controlling "the Bench and pending
   set," which needs squaring with MCP being out of 1.0.

## Why this is one ticket and not four

Answers 1 and 3 constrain each other: per-Bench storage plus concurrent benches is a very
different data model from per-Connection storage plus a switchable ambient selection.
Answer 2 falls out of whichever shape wins. Answer 4 is cheap once 1–3 are fixed, and
expensive to guess at beforehand.

## Notes

- **Update `CONTEXT.md` §4 as part of resolving this.** It is the authoritative vocabulary
  document and currently carries a question 0004 already answered. Whatever this ticket
  decides, §4's "Open:" line should shrink to only what is genuinely still open.
- Is this a 1.0 blocker? Probably yes — a Bench is user-facing vocabulary twig has
  committed to and cannot quietly ship half of — but that is worth confirming rather than
  assuming, since `WorkingSet` works today.
- Prior reading, in order: `CONTEXT.md` §4, `wayfinder/tickets/0004` §2, then
  `wayfinder/tickets/0005` for where Benches live.

## Answer

<!-- empty until resolved -->
