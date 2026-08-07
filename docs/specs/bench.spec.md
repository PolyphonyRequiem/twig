# The Bench — Functional Specification

> **Status:** Draft, ready for ticket-breakdown.
> **Domain:** The Bench — a named, durable, saved backlog of work items.
> **Settled by:** wayfinder 0022 (what a Bench and a Context each are), 0023 (the two
> addressing rules that bind here), 1007 (the build brief), `CONTEXT.md` §4 (vocabulary).
> **Deliberately not settled here:** everything about the Context. See *Out of Scope*.

---

## Problem Statement

A person using twig builds up a view of what they are working on. They pin the items they
care about. Over time that view becomes tuned to one job — the sprint they are in, the bugs
they own, the release they are trying to close.

**They only get one.**

There is exactly one view, it is assembled fresh on every command from one hard-coded
question ("what is in my current sprint, assigned to me"), and the only way to shape it is
to keep pinning. So the moment the job changes — a release goes hot, a production bug
lands, a planning day starts — the person has three bad options:

1. Work in a list full of things that are not the current job.
2. Un-pin the old job's items and pin the new job's, losing the old arrangement.
3. Keep everything pinned, and get a list that is the union of every job they have ever had.

There is no way to say *put that arrangement down and pick this one up*, and no way to come
back to the first one later. The arrangement is not a thing that exists. It has no name,
and nowhere to be.

### What is NOT the problem

🔴 **The build brief (1007) states that pins live in the droppable cache and are silently
destroyed on a schema bump, and makes that data-loss the reason to build the Bench first.
That is no longer true, and the spec says so rather than inheriting it.**

Pins already moved out of the disposable mirror and into a file beside the cache. That file
is not touched when the cache is rebuilt, and a one-time import already carried each user's
existing rows across. The old tables still exist and are still listed in the drop set, so a
search of the code finds exactly what 1007 describes — but nothing reads them. (The
persistence ruling 0005 records the same fact from the other direction: it calls that move
a completed natural experiment, and says those tables get deleted once the one-time import
is retired.)

Consequences, because this changes the plan and not just a sentence:

- **Bench-first is still correct, but for a different reason.** Not "it fixes silent data
  loss" — it does not, that is already fixed. It is that a Bench is a self-contained
  product change that needs no Context work to land, and leaves the computed view behaving
  identically throughout.
- **Two of the build brief's six mandatory red tests would PASS today.** Writing them as
  specified produces precisely the inert guard 1007 warns about in red. They are replaced
  in *Testing Decisions* by tests that can actually fail.
- **A migration is still mandatory, and it is a different migration** — out of the file,
  into the durable store. The silence argument is unchanged and still decisive: a lost pin
  prompts nobody.

---

## Solution

A **Bench** is a named, durable, saved backlog. The person names an arrangement once and
returns to it. Several Benches exist; the person selects one, and it is never derived.
Everything standing on a Bench sees the same Bench — there are no private pins.

The view a person sees today becomes **the default Bench**. Reconstructed, not replaced.

🔴 **The acceptance bar for the whole change: with one Bench and no user action, twig
behaves exactly as it does today. Same items, same order, same output.** A person who never
learns the word "Bench" must not be able to tell this shipped.

### A Bench is a set of selectors — one mechanism, not two

A Bench holds **selectors**. A selector answers one question: *is this item on this Bench?*

A pin is not a different kind of thing from a query. **A pin is a selector that matches one
item; a query is a selector that matches a body of work.** They differ in how many items
they match, not in what they are. That collapse is the point — it means there is one
mechanism to build, one to test, and one to explain.

A Bench stores the **rule**, never the results. What its selectors match is recomputed on
every look, so a Bench keeps up with reality rather than going stale.

The Bench's membership is the **union** of its selectors. Order does not matter: two
Benches holding the same selectors show the same items. There is no evaluation sequence to
reason about, and no way for two arrangements to disagree because they were built in a
different order.

### Selectors are evaluated against the local cache, not against ADO

🔴 **This is the constraint that decides the design, so it is stated before the model and
not as a footnote.**

The obvious implementation is to make a Bench a set of ADO queries. It does not work, for
three reasons, and the first is fatal:

1. **ADO cannot see a seed.** A seed has never been pushed — ADO has never heard of it. So
   no server-side query can ever return one. But seeds must stay visible on every Bench
   (see the guard below). A Bench made of server-side queries would need that guard bolted
   on beside the query engine as a permanent special case.
2. **ADO cannot see unpushed edits.** The pending set is local. Same problem, same guard.
3. **Reads are cache-only by ruling** (0004 §3). A Bench evaluated server-side cannot be
   displayed offline, and every list becomes a network round trip. That reverses a decided
   position rather than implementing one.

So selectors are evaluated **against the local cache**. A query selector still *carries* an
ADO query — that is how matching items get into the cache in the first place — but the
query is a **refresh rule**, not the thing evaluated when the person looks at their Bench.

This is what makes the guard below fall out of the model instead of being welded onto it.

### The one guard that outranks every selector

🔴 **Seeds and unpushed edits stay visible even when no selector on the current Bench
matches them.**

This is an **invariant on evaluation, not a selector**. It is not a rule that happens to be
present on every Bench and could be removed from one; it is a property of what a Bench
evaluation returns.

The reason is that these two things are different in kind. A Bench's selectors decide what
is *interesting*. Unpushed work is what twig *owes* ADO. A display preference must not be
able to conceal a debt — if switching Bench could hide a staged edit, twig would be using a
view setting to hide work that is lost if the person forgets it.

**Where it lives (ADO #147).** The guard is implemented inside the evaluator, which reads the
seeds and the pending set on every evaluation and unions them into what the evaluation returns.
It is deliberately *not* a selector installed on each Bench at creation: a selector can be
removed by editing the Bench, and that would reproduce the defect while passing the same
acceptance sentences. A Bench with **no selectors at all** still surfaces owed work.

### What a Bench is not

- **Not a sync unit.** Reconciliation scopes to the pending set, per Connection. Switching
  Bench never changes what twig pushes or pulls.
- **Not a record of interest.** Being on a Bench means one thing: you can stand there.
  Reading one work item does not add it to a Bench, and must not — a targeted read that
  quietly mutates the Bench moves the person's view out from under them.
- **Not a place where work can hide.** See the guard above.

### Exclusions are OUT of scope — decided 2026-08-06

A Bench has selectors and nothing else. There is no subtracting selector, no exclusion, and
no way to remove an item a selector matched.

**This is a deliberate cut, made after investigating what exclusion does today.** The
finding that drove it:

🔴 **`exclude` does not currently exclude anything.** Nothing subtracts excluded items from
the view. The service that computes the working set never reads exclusions at all. The ids
are carried into the read model, handed to the formatters, and printed as a dim footer at
the bottom of the output — `3 excluded: #12, #40, #71` — while the items themselves appear
in the list looking exactly like everything else. The two halves never meet: the section
builder takes the excluded ids as a parameter and stores them without ever filtering
against them. Machine output has the same shape, a separate id array beside untouched items.

So exclusion today is a note-to-self with a misleading verb. That has two consequences:

- **There is nothing to migrate.** Building exclusion into the Bench would not be preserving
  a behaviour, it would be *specifying one for the first time* — a bigger change than it
  appears, wearing the costume of a data move.
- **The parity bar does not protect it.** Parity means the same items in the same order, and
  exclusion currently affects neither.

**The existing exclude commands are left exactly as they are** — not deleted, not moved,
not changed. They continue to write to the file and print their footer, outside the Bench.
Retiring or fixing them is separate work.

This cut also removes a genuine design problem rather than deferring it. With subtraction in
the model, a person can express two contradictory intentions about one item, and every way
of resolving that costs something. The resolution reached before the cut is recorded in
*Further Notes* so it is not re-derived from scratch if exclusions come back.

---

## User Stories

**Living on one Bench (the default — no new vocabulary required)**

1. As a person who has never heard of a Bench, I want twig to show me exactly the items it
   showed me yesterday, so that this change costs me nothing.
2. As a person who has never heard of a Bench, I want my existing pins to still be pinned,
   so that an upgrade does not quietly discard arrangement I built by hand.
3. As a person with a staged edit, I want that item visible in my list even after the sprint
   it belonged to has ended, so that I cannot forget work twig still owes ADO.
4. As a person with a draft item not yet pushed, I want it visible in my list regardless of
   what my Bench selects, so that unpublished work cannot be lost by being hidden.
5. As a person working on a train, I want my Bench to display with no network, so that
   losing connectivity does not cost me my view of my own work.

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
10. As a person who has finished a piece of work, I want to delete a Bench I no longer need,
    so that my list of arrangements reflects what I am actually doing.
11. As a person returning after two weeks, I want my Benches exactly as I left them, so that
    time away costs me no setup.

**Shaping a Bench**

12. As a person tuning a Bench, I want to pin an item onto it, so that something important
    stays in view whether or not anything else on the Bench selects it.
13. As a person tuning a Bench, I want to pin an item and its whole subtree, so that I can
    follow a piece of work without pinning each child by hand.
14. As a person tuning a Bench, I want to unpin an item, so that the Bench stops showing
    something I no longer care about.
15. As a person building a Bench around a body of work, I want to give it a query, so that
    the Bench keeps up with reality instead of needing a pin for every new item.
16. As a person whose Bench has a stale query, I want to remove that query, so that the
    Bench stops showing a body of work that is finished.
17. As a person looking at a Bench, I want to see what it is made of, so that I can tell why
    an item is on it and change the rule rather than guess.
18. As a person with a query on a Bench, I want its results recomputed each time I look, so
    that the Bench reflects what is true now and not what was true when I set it up.
19. As a person who pins an item a query already matched, I want one copy in my list, so
    that overlapping rules do not produce duplicates.

**Not losing work**

20. As a person deleting a Bench, I want to be told what it holds before it goes, so that I
    do not discard pins I would have kept.
21. As a person who has deleted a Bench, I want my staged edits untouched, so that a view
    operation cannot destroy work owed to ADO.
22. As a person upgrading twig, I want every pin I had before to be on my default Bench
    afterwards, so that the change to durable storage costs me nothing.
23. As a person upgrading twig, I want to be told plainly if anything could not be carried
    across, so that a silent partial migration cannot happen.

**Being told when I am wrong**

24. As a person who typos a Bench name, I want twig to stop with an error naming what I
    asked for, so that I do not silently operate on the wrong Bench.
25. As a person who typos a Bench name, I want twig to not create it for me, so that a typo
    does not quietly become a new empty arrangement.
26. As a person who deleted a Bench and forgot, I want a later reference to it to fail
    loudly, so that being wrong is visible instead of silent.

**Scripting against a Bench**

27. As a script author, I want to name the Bench I operate on, so that my script does not
    depend on whatever the person's shell last pointed at.
28. As a script author, I want a machine-readable listing of Benches, so that I can check
    what exists before acting.
29. As a script author, I want a non-zero exit when I name a Bench that does not exist, so
    that my pipeline stops instead of proceeding against the wrong list.
30. As a script author, I want reading one work item to need no Bench at all, so that the
    common scripted case stays a single call with no setup.
31. As a script author, I want reading one work item to never modify a Bench, so that my
    script cannot move somebody's view as a side effect.

---

## Implementation Decisions

### 1. A Bench lives in the durable store — decided 2026-08-06

Benches and their selectors live in the **durable store** — the one that is never dropped
and is versioned by an additive migration ledger.

The alternative considered and rejected was the file where pins live today. The file's only
real advantage was surviving a cache rebuild, and the durable store has that property by
construction. Against it: a Bench must be looked up by name among several, must hold
selectors of more than one kind, and must be able to change shape over time without losing
what it holds. That is what the durable store is for, and the file is weak at all three.

This is the first *new* durable table since the store split. It can never be
dropped-and-recreated, so it needs a real migration, permanently.

### 2. Selector kinds

Two to begin with, and the model must admit more without a schema change:

- **An item selector** — matches one work item. This is what a pin becomes.
- **A subtree selector** — matches an item and its descendants. This is what a tree pin
  becomes, and it is the reason "a pin is just an id" is too weak a model: a subtree pin
  matches items that did not exist when it was created.
- **A query selector** — carries an ADO query as a refresh rule, and matches the cached
  items that rule brought in.

The seeds-and-unpushed guard is deliberately **not** a selector kind. See the Solution.

### 3. The computed view becomes a projection OF a Bench, not a replacement for it

The service that computes today's view is already a Bench in all but name: one hard-coded
question, plus hand pins, assembled per access with nowhere to persist the hand edits. It is
**promoted, not replaced**.

Consequence that keeps this change small: the read model it returns keeps its shape, so call
sites do not get rewritten. The hard-coded "current iteration, assigned to me" question
becomes **the first query selector of the default Bench**, not a special case sitting beside
the selector mechanism. If it stays a special case, the default Bench is not really a Bench
and the parity bar is being met by a fiction.

### 4. The default Bench

One default Bench per Connection. It is the only Bench twig creates on its own; every other
Bench is created deliberately by a person. It cannot go missing, so it is never subject to
the unknown-Bench error.

### 5. Verbs: create, name, switch, list, delete

Standing-command territory, CLI for now.

**Shipped as of ADO #148: `twig bench create <name>` and `twig bench list`.** Creating names a
Bench; listing shows what exists with the current one marked, and carries the same facts in
machine-readable form (`-o json`) so a script can check what exists before acting. Both route
through one `BenchWorkflow` at the existing mutation-workflow seam, so the human and agent
surfaces cannot disagree about what a Bench is. A new Bench is EMPTY — creating one is not a way
to copy an arrangement — and a name already taken is refused with a non-zero exit rather than
adopted, which would be create-on-reference wearing a different name. Names are matched
case-insensitively, so a person cannot end up with two Benches a listing cannot tell apart.

**Switching (#149) and deleting (#150) are NOT shipped.** Until switching exists, the current
Bench is always the default one. The listing reports the current Bench as its own field rather
than leaving a reader to infer it from `is_default`, so that when switching lands only one call
site changes and no surface is left rendering a stale inference.

Two rules are inherited and non-negotiable:

- 🔴 **An unknown Bench is a hard error.** Non-zero exit, name what was asked for, say what
  to do. Not a fallback, not a warning, not a silently-created Bench. twig is deliberately
  moving to the family of tools where a reference that does not resolve *fails*, rather than
  the family where a name always resolves and therefore silently acts on the wrong target. A
  Bench that gets created on reference reproduces exactly the defect being escaped.
- 🔴 **Deleting a Bench never silently discards.** Pins are work the person did by hand and
  cannot be rebuilt from ADO. Deleting a Bench that holds them reports what it holds. **No
  habitual force flag** — a flag that is needed routinely becomes a reflex, and the one time
  it matters the person types it without reading.

### 6. The migration is mandatory, and it moves pins out of the file

Existing pins move from the file into the default Bench in the durable store as item and
subtree selectors. Exclusions are not migrated — they stay in the file, untouched, serving
the existing commands.

🔴 **A clean break is not available here.** The earlier store split could take one, because
a non-empty pending set could refuse the operation and say so. Pins are silent: nothing
prompts, nothing refuses, and the person discovers the loss weeks later when they notice
something they pinned is not there. **Write the migration.**

If the migration proves impossible, **this spec blocks rather than shipping a silent
break.** That is a real outcome, not a formality.

Migration properties the implementing change must satisfy:

- Every pin present before is present after, with single-vs-subtree preserved.
- Running it twice does not duplicate or destroy anything.
- Anything that cannot be carried across is **reported, not dropped**.
- The file is not deleted, since exclusions still live there.

### 7. Where the Context touches this — one seam, and only one

**A Context stands on a Bench.** That is the entire relationship, and it is the only thing
this spec says about the Context.

Not specified here, deliberately: how a Context is created, addressed, or closed; how long
one lives; what reclaims an abandoned one. Those belong to the Context spec.

### 8. Bench addressing — simplest thing that works, and flagged

How a Context is addressed is ruled. Whether a Bench is named by the same mechanism is
**deliberately unanswered**. This spec uses the simplest thing that works — an explicit name
on the command — and flags it as unresolved rather than quietly establishing a precedent
that a later ruling has to undo.

🔴 **PROVISIONAL, and shipped as such (ADO #148).** `twig bench create <name>` and
`twig bench list` take the name as a plain command argument. That is the simplest thing that
works and it is **not** a ruling that Bench addressing is separate from Context addressing. A
later ruling binding the two is expected to change this surface, and nothing may be built that
depends on the name being a bare argument.

The related settled part is the SURFACE PRINCIPLE, which does hold: **human simple, machine
strict.** A person who names no Bench gets the default; a script must name the Bench every
time, including the default, and omitting it is a hard error rather than a fallback. That is
wayfinder 0023's Context addressing rule one level up. 🔴 The output format is **declared** on
the command and never inferred from whether a tty is attached — twig does not sniff for a
terminal, so a command means the same thing in a pipe as at a prompt.

---

## Testing Decisions

### What makes a good test here

Test what a person can observe: which items appear, in what order, in what output. Do not
assert the shape of the storage or the number of queries issued — those are implementation
detail this spec deliberately leaves open, and pinning them makes the tests obstacles to the
refactor rather than a defence of behaviour.

### The seam: the existing mutation-workflow layer

**Prefer existing seams. Use the highest one. Fewest is best.** The repo already has the
right one.

Mutations run through a **workflow** object in the infrastructure layer, one per operation,
each returning a result type describing the outcome. Both the human CLI and the agent
surface route through the same workflow; the adapters only resolve the target and render the
outcome. That is the seam — it sits above storage, below presentation, and it is already the
place both surfaces meet.

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
| 2 | **Pins survive the move off the file.** Pins present before the migration are selectors on the default Bench after it, with single-vs-subtree preserved. | No migration exists. |
| 3 | **The migration is safe to run twice.** Running it again neither duplicates nor destroys. | No migration exists. |
| 4 | **A staged edit matched by nothing stays visible.** Stage an edit on an item no selector on the current Bench matches, switch Bench, assert it is still surfaced. | Nothing to switch; the guard does not exist. |
| 5 | **A seed matched by nothing stays visible.** Same shape. | Same. |
| 6 | **A Bench displays with no network.** Evaluate a Bench with the ADO endpoint unreachable; assert the same items as with it reachable. | No Bench; and this is the test that would catch a regression to server-side evaluation. |
| 7 | **Selector order does not change membership.** Build two Benches with the same selectors added in different orders; assert identical output. | No Bench. Locks the union semantics. |
| 8 | **Overlapping selectors produce one copy.** Pin an item a query selector already matches; assert it appears once. | No Bench. |
| 9 | **A subtree selector matches a child created after it.** Add a subtree selector, then add a child to that subtree; assert the child is on the Bench. | No Bench. This is what distinguishes a subtree selector from a set of item selectors, and a naive implementation gets it wrong. |
| 10 | **An unknown Bench is a hard error** — non-zero exit, names what was asked for, and **no Bench is created as a side effect**. | No Bench to be unknown. |
| 11 | **Deleting a Bench with contents reports what it holds** and does not silently discard. | No delete verb. |
| 12 | **A targeted read does not modify any Bench.** Read one item by id; assert the Bench's selectors are byte-identical afterwards. | Passes today — see below. |

🔴 **Test 12 passes today, and that is deliberate — it is a lock, not a regression test.** It
defends behaviour this change could plausibly break while adding write paths. It must be
labelled as such, and **must not be counted as evidence the change works**. Confusing a lock
with a regression test is how a suite grows while its defensive power does not.

Tests 7, 8 and 9 are the ones that earn the selector model. If they are dropped as
"obvious", the union semantics are unenforced and the first implementation to evaluate
selectors in sequence will pass everything else.

### Prior art in the repo

Existing workflow tests at this seam substitute the repository and store, drive the workflow,
and assert on the returned outcome. Bench workflow tests follow that shape.

Fixture hazard worth stating up front, because this repo has been bitten by it: a fixture
that silently degrades into the happy path hollows the suite out. For test 4, the item must
genuinely be matched by nothing on the target Bench — assert that precondition explicitly, so
a later setup change cannot turn the test into a tautology.

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
| The view is the union of configured sources and manual pins, minus exclusions — a single computed thing with no identity. | That view is **one Bench** — the default one. Several may exist. Its membership is the union of its selectors; the "minus exclusions" clause describes something that does not happen (see below). |
| Pins are user-local state stored in a file beside the cache. | They are **selectors on a Bench** in the durable store. Migrated out of the file. |
| Pin commands are described as operating on "the workspace". | They operate on **a Bench** — by default the current one. |
| Sprint sources and manual pins are separate mechanisms. | Both are **selectors**. The hard-coded sprint question is one query selector, not a special case. |
| Post-init state lists the tracking file as part of a fresh setup. | A fresh setup creates the **default Bench**. The file survives for exclusions only. |

🔴 **One line in that document is already false, independently of this change.** It describes
the view as sources and pins *minus exclusions*. Nothing subtracts exclusions — the working
set service never reads them. That is a documentation defect today, not something this spec
introduces, and it should be corrected whether or not the Bench ships.

**Not touched by this spec, and must remain intact:** the entire sync model — push ordering,
notes-before-fields, conflict resolution, protected items, the dirty-state lifecycle. A
Bench is never a sync unit, so nothing in that half moves.

### `context-commands.spec.md`

**Still current until this ships. Made false by it — partially, and the boundary matters:**

| What it says | Status |
|---|---|
| Setting the active item writes to a single shared active-item slot. | **Made false — but by the Context spec, not this one.** Named here so the two are not confused. |
| Reading an item by id does not change the active context. | **Stays true and is now load-bearing** — story 31 and test 12 defend it. |
| The pin vocabulary in its edge cases and outputs. | **Made false** where it implies one global set. |

🔴 **The dangerous confusion, stated so nobody hits it:** this spec and the Context spec both
touch that document, for different reasons and on different schedules. **This change must not
fix the shared-slot defect in passing.** That is the Context change, and quietly absorbing it
here makes both changes harder to review and hides which one to revert.

### What the implementing change must reconcile, together, in one change

Code, tests, and **both spec documents above** move as one unit. Shipping the behaviour and
leaving the specs describing the old world reproduces the documentation rot this repo has
already paid to clean up once — and worse, the next spec author reads the stale document and
inherits the falsehood.

Also to be reconciled in the same change:

- **The domain glossary** — the entry noting the computed view is a separate concept from a
  Bench becomes false when it *is* one. The open question it records is answered. A
  **selector** is a new noun and needs an entry.
- **The build brief's data-loss premise** — corrected at the top of this spec. The brief
  itself should be amended rather than silently contradicted, since it is what a reader
  reaches for first.
- **The two dead cache tables** — declared, dropped, read by nothing. Deleting them is the
  natural moment, and the persistence ruling already anticipated it. Note the exclusion half
  of the file survives, so this is a narrower cleanup than it first looks.

---

## Out of Scope

- **Exclusions, and any subtracting selector.** Decided 2026-08-06. The existing exclude
  commands are left exactly as they are. See the Solution for the finding that drove the cut,
  and Further Notes for the resolution reached before it, preserved for whoever brings
  exclusions back.
- **The Context, entirely.** Its lifetime, its addressing, its reaping, per-caller identity,
  and the shared active-item slot. Separate spec, separate problem. The single seam this spec
  states is: *a Context stands on a Bench*, and nothing more.
- **Whether the pending set is stored per-Bench or per-Connection.** Genuinely open. 🔴 If the
  implementation forces this question, **block and say so** rather than settling it in code.
  Only the reconciliation boundary is decided (per Connection); storage is not.
- **Whether Bench management is interactive.** The terminal-session question is unanswered.
  CLI verbs for now.
- **How a Bench is addressed**, beyond the simplest thing that works. Flagged in
  Implementation Decisions §8.
- **Query expressiveness.** What a query selector *can express* beyond what the current
  hard-coded question expresses is not specified here. The parity bar only requires that the
  existing question survives as one selector.
- **Sharing a Bench between people.** twig is a single-user local tool; nothing here crosses
  machines.

---

## Further Notes

**On sequencing.** This change lands before any Context work. It needs none, it leaves the
computed view behaving identically throughout, and it is a self-contained product change. The
build brief's stated reason for that order is falsified (see *Problem Statement*); the order
itself survives on these grounds.

**On the parity bar being the hard part.** The easy reading is that parity is a formality once
the Bench exists. It is the opposite: parity is the constraint that decides whether this
ships. It forces the default Bench to be a genuine Bench rather than the old code path with a
new name over it — because a special-cased default that skips the selector mechanism would
meet the bar and prove nothing.

**Preserved for whoever brings exclusions back.** Subtraction was cut, not solved. If it
returns, this is the problem waiting and the answer already reached, so it is not re-derived:

> With subtraction in the model, a person can hold two contradictory intentions about one
> item — excluded last month, pinned today. Absolute subtraction (`AND NOT`, order-free) makes
> the later pin silently do nothing: accepted, stored, no effect, forever. That is the same
> failure class 0023 rejects for a stale Context — an operation that succeeds and quietly
> achieves nothing. Ordered evaluation fixes it but makes two Benches with identical contents
> behave differently by construction order.
>
> **The ruling (Daniel, 2026-08-06): more-or-equally specific beats generic, and it is a
> change of mind rather than a mistake.** Pinning one item is more specific than whatever
> excluded it, so the pin wins. One rule, no error to explain, no order to reason about — and
> it keeps the union semantics intact, because specificity is a property of the selectors and
> not of the sequence they were added in.

**On what would falsify the design.** If the person's day-to-day requires more than one Bench
*open at once* — not switched between — then a switchable Bench is the wrong shape and
concurrency has been put at the wrong level. The ruling says concurrency lives with the
Context. If in use it turns out people want two Benches side by side, that reverses a ruling,
and should be recorded as such rather than patched around with a second mechanism.
