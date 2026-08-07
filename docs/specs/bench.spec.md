# The Bench — Functional Specification

> **Status:** Draft, ready for ticket-breakdown.
> **Domain:** The Bench — a named, durable, saved backlog of work items.
> **Settled by:** wayfinder 0022 (what a Bench and a Context each are), 0023 (the two
> addressing rules that bind here), 1007 (the build brief), `CONTEXT.md` §4 (vocabulary).
> **Deliberately not settled here:** everything about the Context. See *Out of Scope*.

---

## Problem Statement

A person using twig builds up a view of what they are working on. They pin the items they
care about. They hide the ones cluttering the list. Over time that view becomes tuned to
one job — the sprint they are in, the bugs they own, the release they are trying to close.

**They only get one.**

There is exactly one view, it is assembled fresh on every command from one hard-coded
question ("what is in my current sprint, assigned to me"), and the only way to shape it is
to keep pinning and excluding. So the moment the job changes — a release goes hot, a
production bug lands, a planning day starts — the person has three bad options:

1. Work in a list full of things that are not the current job.
2. Un-pin the old job's items and pin the new job's, losing the old arrangement.
3. Keep everything pinned, and get a list that is the union of every job they have ever had.

There is no way to say *put that arrangement down and pick this one up*, and no way to
come back to the first one later. The arrangement is not a thing that exists. It has no
name, and nowhere to be.

### What is NOT the problem

🔴 **The build brief (1007) states that pins live in the droppable cache and are silently
destroyed on a schema bump, and makes that data-loss the reason to build the Bench first.
That is no longer true, and the spec says so rather than inheriting it.**

Pins and exclusions already moved out of the disposable mirror and into a file beside the
cache. That file is not touched when the cache is rebuilt, and a one-time import already
carried each user's existing rows across. The old tables still exist and are still listed
in the drop set, so a search of the code finds exactly what 1007 describes — but nothing
reads them. (The persistence ruling 0005 records the same fact from the other direction:
it calls that move a completed natural experiment, and says those two tables get deleted
once the one-time import is retired.)

Consequences, because this changes the plan and not just a sentence:

- **Bench-first is still correct, but for a different reason.** Not "it fixes silent data
  loss" — it does not, that is already fixed. It is that a Bench is a self-contained
  product change that needs no Context work to land, and leaves the computed view behaving
  identically throughout.
- **Two of the build brief's six mandatory red tests would PASS today.** "Pins survive a
  schema bump" and "exclusions survive a schema bump" are already true. Writing them as
  specified produces precisely the inert guard 1007 warns about in red. They are replaced
  in *Testing Decisions* by tests that can actually fail.
- **A migration is still mandatory, and it is a different migration** — out of the file,
  into the durable store. The silence argument is unchanged and still decisive: a lost pin
  prompts nobody.

---

## Solution

A **Bench** is a named, durable, saved backlog. The person names an arrangement once and
returns to it.

A Bench holds three things, and holds the **rule** rather than the results:

- **pins** — items put on the Bench by hand,
- **queries** — questions whose answers appear on the Bench, recomputed every time,
- **exclusions** — items removed from the Bench's view by hand.

Several Benches exist. The person selects one; it is never derived. Everything standing on
a Bench sees the same Bench — there are no private pins.

The view a person sees today becomes **the default Bench**: one query (current iteration,
filtered to them), plus their existing pins, plus their existing exclusions. Reconstructed,
not replaced.

🔴 **The acceptance bar for the whole change: with one Bench and no user action, twig
behaves exactly as it does today. Same items, same order, same output.** A person who never
learns the word "Bench" must not be able to tell this shipped.

### What a Bench is not

- **Not a sync unit.** Reconciliation scopes to the pending set, per Connection. Switching
  Bench never changes what twig pushes or pulls.
- **Not a record of interest.** Being on a Bench means one thing: you can stand there.
  Reading one work item does not add it to a Bench, and must not — a targeted read that
  quietly mutates the Bench moves the person's view out from under them.
- **Not a place where work can hide.** See the visibility guard below.

### The one guard that outranks the queries

🔴 **Seeds and unpushed edits stay visible even when no query on the current Bench selects
them.**

A Bench is a view. Unpushed work is a debt twig owes ADO. If switching Bench could hide a
staged edit, twig would be using a display preference to conceal work that will be lost if
the person forgets it. The Bench's queries decide what is *interesting*; they do not get to
decide what is *owed*.

---

## User Stories

**Living on one Bench (the default — no new vocabulary required)**

1. As a person who has never heard of a Bench, I want twig to show me exactly the items it
   showed me yesterday, so that this change costs me nothing.
2. As a person who has never heard of a Bench, I want my existing pins to still be pinned,
   so that an upgrade does not quietly discard arrangement I built by hand.
3. As a person who has never heard of a Bench, I want my existing exclusions still hidden,
   so that things I deliberately silenced do not come back.
4. As a person with a staged edit, I want that item visible in my list even after the
   sprint it belonged to has ended, so that I cannot forget work twig still owes ADO.
5. As a person with a draft item not yet pushed, I want it visible in my list regardless of
   what my Bench's queries select, so that unpublished work cannot be lost by being hidden.

**Having more than one arrangement**

6. As a person who works on several fronts, I want to create a named Bench, so that an
   arrangement becomes a thing I can return to instead of something I rebuild.
7. As a person starting a new piece of work, I want to name a Bench something I will
   recognise later ("release blockers", "bugs I own"), so that I can tell my arrangements
   apart in a month.
8. As a person switching jobs mid-morning, I want to switch to another Bench, so that my
   list becomes the new job's list without dismantling the old one.
9. As a person with several Benches, I want to list them, so that I can see what
   arrangements I have and which one I am standing on.
10. As a person who has finished a piece of work, I want to delete a Bench I no longer
    need, so that my list of arrangements reflects what I am actually doing.
11. As a person returning after two weeks, I want my Benches exactly as I left them, so
    that time away costs me no setup.

**Shaping a Bench**

12. As a person tuning a Bench, I want to pin an item onto it, so that something important
    stays in view whether or not a query selects it.
13. As a person tuning a Bench, I want to pin an item and its whole subtree, so that I can
    follow a piece of work without pinning each child by hand.
14. As a person tuning a Bench, I want to unpin an item, so that the Bench stops showing
    something I no longer care about.
15. As a person tuning a Bench, I want to exclude an item, so that something noisy stops
    cluttering a Bench even though a query keeps selecting it.
16. As a person tuning a Bench, I want to see what I have excluded, so that a hidden item
    is discoverable rather than mysteriously absent.
17. As a person tuning a Bench, I want to un-exclude an item, so that hiding something is
    reversible.
18. As a person building a Bench around a body of work, I want to give it a query, so that
    the Bench keeps up with reality instead of needing a pin for every new item.
19. As a person whose Bench has a stale query, I want to remove that query, so that the
    Bench stops showing a body of work that is finished.
20. As a person with a query on a Bench, I want its results recomputed each time I look, so
    that the Bench reflects what is true now and not what was true when I set it up.

**Not losing work**

21. As a person deleting a Bench, I want to be told what it holds before it goes, so that I
    do not discard pins and exclusions I would have kept.
22. As a person who has deleted a Bench, I want my staged edits untouched, so that a view
    operation cannot destroy work owed to ADO.
23. As a person upgrading twig, I want every pin and exclusion I had before to be on my
    default Bench afterwards, so that the change to durable storage costs me nothing.
24. As a person upgrading twig, I want to be told plainly if anything could not be carried
    across, so that a silent partial migration cannot happen.

**Being told when I am wrong**

25. As a person who typos a Bench name, I want twig to stop with an error naming what I
    asked for, so that I do not silently operate on the wrong Bench.
26. As a person who typos a Bench name, I want twig to not create it for me, so that a typo
    does not quietly become a new empty arrangement.
27. As a person who deleted a Bench and forgot, I want a later reference to it to fail
    loudly, so that being wrong is visible instead of silent.

**Scripting against a Bench**

28. As a script author, I want to name the Bench I operate on, so that my script does not
    depend on whatever the person's shell last pointed at.
29. As a script author, I want a machine-readable listing of Benches, so that I can check
    what exists before acting.
30. As a script author, I want a non-zero exit when I name a Bench that does not exist, so
    that my pipeline stops instead of proceeding against the wrong list.
31. As a script author, I want reading one work item to need no Bench at all, so that the
    common scripted case stays a single call with no setup.
32. As a script author, I want reading one work item to never modify a Bench, so that my
    script cannot move somebody's view as a side effect.

---

## Implementation Decisions

### 1. A Bench lives in the durable store — decided 2026-08-06

Benches, their pins, their exclusions and their queries live in the **durable store** — the
one that is never dropped and is versioned by an additive migration ledger.

The alternative considered and rejected was the file where pins live today. The file's only
real advantage was surviving a cache rebuild, and the durable store has that property by
construction. Against it: a Bench must be looked up by name among several, must hold
queries, and must be able to change shape over time without losing what it holds. That is
what the durable store is for, and the file is weak at all three.

This is the first *new* durable table since the store split. It can never be
dropped-and-recreated, so it needs a real migration, permanently.

### 2. The computed view becomes a projection OF a Bench, not a replacement for it

The service that computes today's view is already a Bench in all but name: one hard-coded
query, plus hand pins, plus hand exclusions, assembled per access with nowhere to persist
the hand edits. It is **promoted, not replaced**.

Consequence that keeps this change small: the read model it returns keeps its shape, so
call sites do not get rewritten. The hard-coded "current iteration, assigned to me"
question becomes **the first query row of the default Bench**, not a special case sitting
beside the query mechanism. If it stays a special case, the default Bench is not really a
Bench and the parity bar is being met by a fiction.

### 3. The default Bench

One default Bench per Connection. It is the only Bench twig creates on its own; every other
Bench is created deliberately by a person. It cannot go missing, so it is never subject to
the unknown-Bench error.

### 4. Verbs: create, name, switch, list, delete

Standing-command territory, CLI for now. Two rules are inherited and non-negotiable:

- 🔴 **An unknown Bench is a hard error.** Non-zero exit, name what was asked for, say what
  to do. Not a fallback, not a warning, not a silently-created Bench. twig is deliberately
  moving to the family of tools where a reference that does not resolve *fails*, rather than
  the family where a name always resolves and therefore silently acts on the wrong target.
  A Bench that gets created on reference reproduces exactly the defect being escaped.
- 🔴 **Deleting a Bench never silently discards.** Pins and exclusions are work the person
  did by hand and cannot be rebuilt from ADO. Deleting a Bench that holds them reports what
  it holds. **No habitual force flag** — a flag that is needed routinely becomes a reflex,
  and the one time it matters the person types it without reading.

### 5. The migration is mandatory, and it moves pins out of the file

Existing pins and exclusions move from the file into the default Bench in the durable store.

🔴 **A clean break is not available here.** The earlier store split could take one, because
a non-empty pending set could refuse the operation and say so. Pins are silent: nothing
prompts, nothing refuses, and the person discovers the loss weeks later when they notice
something they pinned is not there. **Write the migration.**

If the migration proves impossible, **this spec blocks rather than shipping a silent
break.** That is a real outcome, not a formality.

Migration properties the implementing change must satisfy:

- Every pin and exclusion present before is present after, with its mode preserved.
- Running it twice does not duplicate or destroy anything.
- Anything that cannot be carried across is **reported, not dropped**.
- The file is not deleted until the data is verifiably in the durable store.

### 6. Where the Context touches this — one seam, and only one

**A Context stands on a Bench.** That is the entire relationship, and it is the only thing
this spec says about the Context.

Not specified here, deliberately: how a Context is created, addressed, or closed; how long
one lives; what reclaims an abandoned one. Those belong to the Context spec.

### 7. Bench addressing — simplest thing that works, and flagged

How a Context is addressed is ruled. Whether a Bench is named by the same mechanism is
**deliberately unanswered**. This spec uses the simplest thing that works — an explicit
name on the command — and flags it as unresolved rather than quietly establishing a
precedent that a later ruling has to undo.

---

## Testing Decisions

### What makes a good test here

Test what a person can observe: which items appear, in what order, in what output. Do not
assert the shape of the storage or the number of queries issued — those are implementation
detail this spec deliberately leaves open, and pinning them makes the tests obstacles to
the refactor rather than a defence of behaviour.

### The seam: the existing mutation-workflow layer

**Prefer existing seams. Use the highest one. Fewest is best.** The repo already has the
right one.

Mutations run through a **workflow** object in the infrastructure layer, one per operation,
each returning a result type describing the outcome. Both the human CLI and the agent
surface route through the same workflow; the adapters only resolve the target and render
the outcome. That is the seam — it sits above storage, below presentation, and it is
already the place both surfaces meet.

**Bench operations get one workflow at that existing seam, and no new seam is proposed.**
The alternative — testing through the CLI adapters — would test the same logic twice, once
per surface, and let the two drift, which is the defect that made every agent-surface tool
name its own target.

The already-proven parity guarantee comes free: if the workflow is the only path, the two
surfaces cannot disagree about what a Bench is.

### The tests that must fail on unfixed code

Per the repo's convention, a regression test must fail before the fix. Verify against a
detached worktree at the pre-fix commit and **report which tests failed there, by name**.
"They should fail" is not evidence — this repo has already shipped a structural guard that
was silently inert and passed at the pre-fix commit.

🔴 **The build brief's tests 1 and 2 are struck.** "Pins survive a schema bump" and
"exclusions survive a schema bump" **pass today**, because pins already left the droppable
cache. Writing them as specified produces exactly the inert guard the brief warns against.
Replaced by:

| # | Test | Why it fails today |
|---|---|---|
| 1 | **Default-Bench parity.** With one Bench and no user action, the computed view is identical to today's — compared against a captured baseline, not by eye. | No Bench exists to compute from. |
| 2 | **Pins survive the move off the file.** Pins present before the migration are on the default Bench after it. | No migration exists. |
| 3 | **Exclusions survive the move off the file.** Same shape. | No migration exists. |
| 4 | **The migration is safe to run twice.** Running it again neither duplicates nor destroys. | No migration exists. |
| 5 | **A staged edit outside every query stays visible.** Stage an edit on an item no query on the current Bench selects, switch Bench, assert it is still surfaced. | Nothing to switch; the guard does not exist. |
| 6 | **A seed outside every query stays visible.** Same shape. | Same. |
| 7 | **An unknown Bench is a hard error.** Non-zero exit, names what was asked for, and **no Bench is created as a side effect**. | No Bench to be unknown. |
| 8 | **Deleting a Bench with contents reports what it holds** and does not silently discard. | No delete verb. |
| 9 | **A targeted read does not modify any Bench.** Read one item by id; assert pins, queries and exclusions are byte-identical afterwards. | Passes today — see below. |

🔴 **Test 9 passes today, and that is deliberate — it is a lock, not a regression test.**
It defends behaviour this change could plausibly break while adding write paths. It must be
labelled as such, and **must not be counted as evidence the change works**. Confusing a lock
with a regression test is how a suite grows while its defensive power does not.

### Prior art in the repo

Existing workflow tests at this seam substitute the repository and store, drive the
workflow, and assert on the returned outcome. Bench workflow tests follow that shape.

Fixture hazard worth stating up front, because this repo has been bitten by it: a fixture
that silently degrades into the happy path hollows the suite out. For test 5, the item must
genuinely be outside every query on the target Bench — assert that precondition explicitly,
so a later setup change cannot turn the test into a tautology.

### Verdict discipline

Test verdicts come only from the repo's test runner script, by its verdict line. Never grep
for a passing summary — an aborted run prints a clean-looking pass with a smaller total.

---

## Proposed Supersession — read this before implementing

🔴 **This spec makes parts of two shipped spec documents false. They stay authoritative
until this lands.** Naming them now is the point: this is the part that goes wrong quietly.

### `working-set-sync.spec.md`

**Still current until this ships. Made false by it:**

| What it says | What becomes true |
|---|---|
| The view is the union of configured sources and manual pins, minus exclusions — a single computed thing with no identity. | That view is **one Bench** — the default one. Several may exist. |
| Pins and exclusions are user-local state stored in a file beside the cache. | They live on a Bench in the **durable store**. The file is migrated out. |
| Pin and exclusion commands are described as operating on "the workspace". | They operate on **a Bench** — by default the current one. |
| The view is computed fresh on each access from configured sources. | Still true, but the sources are **a Bench's queries**, and the hard-coded sprint question is one query row rather than a special case. |
| Post-init state lists the tracking file as part of a fresh setup. | A fresh setup creates the **default Bench** instead. |

**Not touched by this spec, and must remain intact:** the entire sync model — push
ordering, notes-before-fields, conflict resolution, protected items, the dirty-state
lifecycle. A Bench is never a sync unit, so nothing in that half moves.

### `context-commands.spec.md`

**Still current until this ships. Made false by it — partially, and the boundary matters:**

| What it says | Status |
|---|---|
| Setting the active item writes to a single shared active-item slot. | **Made false — but by the Context spec, not this one.** Named here so the two are not confused. |
| Reading an item by id does not change the active context. | **Stays true and is now load-bearing** — story 32 and test 9 defend it. |
| The pin/exclusion vocabulary in its edge cases and outputs. | **Made false** where it implies one global set. |

🔴 **The dangerous confusion, stated so nobody hits it:** this spec and the Context spec
both touch that document, for different reasons and on different schedules. **This change
must not fix the shared-slot defect in passing.** That is the Context change, and quietly
absorbing it here makes both changes harder to review and hides which one to revert.

### What the implementing change must reconcile, together, in one change

Code, tests, and **both spec documents above** move as one unit. Shipping the behaviour and
leaving the specs describing the old world reproduces the documentation rot this repo has
already paid to clean up once — and worse, the next spec author reads the stale document
and inherits the falsehood.

Also to be reconciled in the same change:

- **The domain glossary** — the entry noting the computed view is a separate concept from a
  Bench becomes false when it *is* one. The open question it records is answered.
- **The build brief's data-loss premise** — corrected at the top of this spec. The brief
  itself should be amended rather than silently contradicted, since it is what a reader
  reaches for first.
- **The two dead cache tables** — declared, dropped, read by nothing. This change is the
  moment to delete them, and the persistence ruling already anticipated it.

---

## Out of Scope

- **The Context, entirely.** Its lifetime, its addressing, its reaping, per-caller
  identity, and the shared active-item slot. Separate spec, separate problem. The single
  seam this spec states is: *a Context stands on a Bench*, and nothing more.
- **Whether the pending set is stored per-Bench or per-Connection.** Genuinely open. 🔴 If
  the implementation forces this question, **block and say so** rather than settling it in
  code. Only the reconciliation boundary is decided (per Connection); storage is not.
- **Whether Bench management is interactive.** The terminal-session question is unanswered.
  CLI verbs for now.
- **How a Bench is addressed**, beyond the simplest thing that works. Flagged in
  Implementation Decisions §7.
- **Query expressiveness.** What a query rule *can express* beyond what the current
  hard-coded question expresses is not specified here. The parity bar only requires that the
  existing question survives as one query.
- **Sharing a Bench between people.** twig is a single-user local tool; nothing here
  crosses machines.

---

## Further Notes

**On sequencing.** This change lands before any Context work. It needs none, it leaves the
computed view behaving identically throughout, and it is a self-contained product change.
The build brief's stated reason for that order is falsified (see *Problem Statement*); the
order itself survives on these grounds.

**On the parity bar being the hard part.** The easy reading is that parity is a formality
once the Bench exists. It is the opposite: parity is the constraint that decides whether
this ships. It forces the default Bench to be a genuine Bench rather than the old code path
with a new name over it — because a special-cased default that skips the query mechanism
would meet the bar and prove nothing.

**On what would falsify the design.** If the person's day-to-day requires more than one
Bench *open at once* — not switched between — then a switchable Bench is the wrong shape and
concurrency has been put at the wrong level. The ruling says concurrency lives with the
Context. If in use it turns out people want two Benches side by side, that reverses a
ruling, and should be recorded as such rather than patched around with a second mechanism.
