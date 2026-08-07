---
id: 1006
title: What is a Bench, concretely enough to build
type: decision
status: closed
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

**Superseded by [wayfinder 0022](../../wayfinder/tickets/0022-bench-and-context.md)
(2026-08-06).** This ticket asked the right four questions; 0022 answered three of them and
carried the fourth forward. Do not work this ticket — work 0022 and 0023.

Question-by-question:

1. **Pending set stored per-Bench or per-Connection?** **STILL OPEN**, and this ticket's
   framing of the trap is the better one and is carried into 0022 verbatim in substance:
   0004 settled that reconciliation *scopes* per-Connection, which does not by itself decide
   where the set is *stored*, and per-Bench storage with per-Connection reconciliation is
   coherent but means reconciliation reads ACROSS benches. 0022 stage 2 forces the answer.
2. **Does `WorkingSet` survive as a Bench's derived projection?** **ANSWERED — it IS one.**
   Today's derived working set is a Bench with one hard-coded query plus hand pins and hand
   exclusions, computed per access with nowhere to persist the hand edits
   (`WorkingSetService.ComputeAsync` steps 1-8). 0022 stage 2 PROMOTES it rather than
   replacing it, so there are no call sites to replace.
3. **Concurrent or merely switchable?** **ANSWERED — benches are switchable; Contexts are
   concurrent.** This ticket correctly identified it as a blast-radius question and
   correctly framed the two shapes. 0022 resolves it by introducing the missing noun:
   concurrency lives at the **Context** level (a disposable place to stand, opened and closed
   by its caller), not at the Bench level. So a selection is not in scope on every read path,
   and it is not ambient process state either — it is per-caller state named explicitly.
4. **Whose job is Bench management?** **STILL OPEN, and this ticket is the only place it is
   written down.** Carried forward. 0022 narrows it: Bench management is standing-command
   territory, and the MCP-controls-the-Bench line in `CONTEXT.md` §4 needs squaring against
   MCP being out of 1.0. Not resolved here.

**The stale line this ticket flagged is fixed.** `CONTEXT.md` §4's "Open:" line no longer
lists the sync-boundary question 0004 §2 already answered; it now lists only what is
genuinely open.

**What 0022 added that this ticket could not see.** The four questions above are all about the
Bench, and the Bench was never the blocker. The blocker is that the active work item is ONE
ROW in a shared store, read or written at 47 sites across 28 files — so the real unit of
concurrency had no name. Introducing **Context** as a disposable, per-caller place to stand
dissolves questions 2 and 3 rather than answering them on their own terms.

**And the definition sharpened.** This ticket carries 0001's "a named set of work items."
0022 rules that a Bench holds **pins, queries AND exclusions**, and stores the RULE rather
than the results. Not a contradiction — a superset. Use the 0022 wording.

**Is it a 1.0 blocker?** This ticket asked rather than assumed, correctly. Provisional answer:
0022 **stage 1** (give each caller its own Context, kill the shared slot) is a correctness fix
and is the 1.0-relevant half. **Stage 2** (Bench create/name/switch/list) is user-facing
capability and can follow. `WorkingSet` keeps working throughout, which is what makes the
staging legal. Confirm rather than inherit this.
