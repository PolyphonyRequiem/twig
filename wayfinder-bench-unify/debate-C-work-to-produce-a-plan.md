# Position C — An Execution Plan contains the work needed to produce a plan

**Claim.** `Execution Plan` is a work-lifecycle container whose children are the *planning work*: the grillings, research, prototypes, specs and decisions that turn a fogged objective into a clear next move. Its children are **not** the work it plans. The type is deliberately silent about the *form* of its output — files on disk, Decisions, newly created work items, new predecessor/successor edges on existing work, or all four. It ends when the way is clear enough to execute.

---

## 1. A and B both prejudge the artifact

Position A says the plan *is* a hierarchy. Position B says the plan *is* a hierarchy plus a dependency graph. Both are decisions about the **shape of the output**, made before any plan has been written, and encoded in the one ADO relationship that is hardest to undo.

The evidence says that commitment is expensive and lossy:

- `System.LinkTypes.Hierarchy` is a tree with exactly one documented constraint: **one parent, many children**. That is the hard constraint in this design. Every work item you put under an Execution Plan is a work item you have *taken away* from every other parent. A `Change` that is genuinely part of a `Feature` cannot also be a child of the Execution Plan that sequenced it. A and B are therefore not additive structure — they are a custody transfer, and they force the plan to win a fight with the delivery hierarchy for ownership of the same items.
- The moment you need the same work to appear in a second plan — a re-plan, a superseding plan, a plan cut along a different axis (release vs. team vs. risk) — the tree cannot express it. There is no second parent. A and B are correct for exactly the plans whose scope happens to coincide with a subtree, and wrong for the rest. That is a premature commitment.
- The team already solved this problem in code and reached the opposite conclusion. `WorkItemGraph` exists because **"a link is an edge between two items, so it belongs to the SET, not to a member of it,"** and edges leaving the set are *retained, not filtered*. Position A/B re-litigate that: they push a set-level property (this plan's ordering) down into a member-level property (this item's parent). C is the position that agrees with the code, and **"the code is the tiebreaker."**
- Duplicating structure is their named worst mechanism: **"a mirror with no compiler forcing the copies to agree."** A plan-tree that mirrors the delivery-tree is precisely that mirror. Worse, if the plan's structure is expressed only as an ADO parentage that nobody re-checks after the plan closes, closing the plan renders it *green* while proving nothing about whether the sequence was followed — a **false green** with a work item type wrapped around it.

## 2. This is the wayfinder shape, pointed at execution

Their existing method is already Position C, exactly:

- A map is a tracker issue; **its children are the tickets, and each ticket resolves a decision.** The children are the work of clearing fog, not the work discovered.
- **"The map is an INDEX, not a store."** It gists and links; a decision lives in exactly one place, its ticket.
- **"Plan, don't do."** The map is done **"when the way is clear"** — defined by a *state*, not by a delivered structure.
- Fog **graduates** into tickets as the frontier advances. The map never declared in advance what its output would look like; output is whatever the fog turned into.

An Execution Plan is that same object with the terminal condition changed from *the decisions are made* to *the sequence is clear and committed*. Its children are Grillings, Research, Prototypes, Specs, Wayfinder Tasks. Its outputs are the associated artifacts the model already names for it: **Specs, Decisions, Commitments.** Note that the team's own draft of Execution Plan says it **"produces or at least organizes"** follow-up work — "or at least" is the tell that they already suspect the output form varies.

Adopting C means the team has **one** planning shape (map/Investigation/Execution Plan) instead of two mutually contradictory ones. "A concept has one name."

## 3. The hard objection: where does the sequence live?

If the plan doesn't own the planned work, an engineer arriving cold must still know what's next. Specifically:

1. **Predecessor/Successor links on the planned work items themselves.** This is not "somewhere else" — it is the wayfinder rule already in force: blocking uses **the tracker's native dependency relationship** because it **"renders the frontier VISUALLY in the tracker's own UI, so the human sees what's takeable without opening the map."** The frontier is *open, unblocked, unclaimed*. That definition needs no plan-parentage at all. Directional Predecessor/Successor edges are unlimited and cross-hierarchy — they express fan-in, fan-out, diamonds, and multi-plan overlap that a tree structurally cannot.
2. **A Decision (or Spec) as the plan's committed output.** Decisions are immutable point-in-time records with no backlog level — exactly right for "here is the sequence we chose, and why, and what we knew at the time." The sequence is *asserted* in the Decision and *enforced* by the links. Both are creatable by the plan's own child tickets.
3. **Files on disk** for the narrative — destination, out of scope, fog — pull-requestable, diffable, and reviewable via `Change`. Related links (symmetric, unlimited, with an optional **comment** attribute) attach them.
4. **twig.** `WorkItemGraph` already models "a set plus its non-hierarchy edges" — that *is* the plan rendering. Their two open Ideas — a **declared policy/rule engine** (because ADO cannot enforce their model) and **parameterised multi-hop queries** (because saved queries can't chain hierarchy-then-related) — are precisely the tooling that makes a link-defined plan legible. C needs no new ADO type to become visible; A and B need one anyway, because **Analytics views are FLAT — "work item hierarchies aren't supported"** — so the tree they buy doesn't report either.

Cold-start answer, concretely: open the Execution Plan → read its Decision/Spec for intent → run twig to get the frontier from Predecessor edges → take an open, unblocked, unclaimed item.

## 4. Is this a non-answer?

No — it is a *narrower* answer than A or B, and narrower is a design choice. C makes three falsifiable commitments: (a) children of an Execution Plan are planning work with a work lifecycle; (b) the plan does **not** take custody of the work it plans; (c) the plan closes on a clarity condition, and the sequence is carried by directional links plus an immutable artifact. What C declines to do is *pick one output form* — and that is deliberate, because Microsoft's documented recommendation is to serve different audiences with **teams, area paths, backlog levels, rollup, Delivery Plans and dashboards**, and explicitly **not** by minting work item types for reporting/summary purposes. A and B are, functionally, that mint.

Note also the migration reality: **32 existing Maps will be retyped.** They are wayfinder maps — their children are already planning tickets. C retypes them correctly and losslessly. A and B declare all 32 malformed on day one.

## 5. What C costs

Honestly:

- **No rollup of planned work.** Rollup climbs Hierarchy only. If you want "this plan is 40% done" as a computed number, C cannot give it; you get frontier size and closure counts from twig instead. This is a real loss for status reporting.
- **No Delivery Plan row for the plan's scope**, since the planned items sit under their delivery parents. (Level-less artifacts don't render either — but under A/B the plan does, which is a genuine advantage for A/B.)
- **Sequence lives in N link edges, not one document.** That is harder to eyeball, harder to review as a unit, and depends on tooling the team has only as Ideas, not code. Until twig ships those queries, C is more painful day-to-day.
- **C cannot express "these ten items constitute a bounded deliverable."** Set membership without hierarchy is weak in ADO — tags, area paths, or a twig-side set are the fallbacks, and none is enforced. If the plan's real job is scope demarcation rather than sequencing, C is the wrong shape.
- **Ambiguity risk.** With output form unspecified, two Execution Plans may produce very different artifacts. C must be paired with a written convention (a Decision, at minimum) or it degrades into "whatever the author felt like."

## 6. What would falsify C

- If, across the 32 retyped Maps, most children turn out to be *executable* work rather than planning work, the type's real usage is A, and C is a rationalisation.
- If the team's dominant need is a rolled-up percent-complete or a Delivery Plan bar for a plan's scope. Rollup is non-writable and cannot drift, which defuses the mirror objection — if that is the killer feature, take the tree.
- If, in practice, planned work rarely belongs to more than one plan and rarely has a competing natural parent, the one-parent constraint costs nothing and A's simplicity wins.
- If Predecessor/Successor edges prove unmaintained in the wild — set once, never updated as scope changes — the frontier becomes a false green and the "sequence lives in links" answer collapses.
- If Execution Plans routinely need to close before their planned work completes and someone still needs the plan's scope afterwards, and links alone can't reconstruct it.

**Summary.** A and B decide what a plan *is* before writing one, and pay for it in the single constraint ADO actually enforces: one parent. C decides what a plan *does* — clear the way to execution — and lets each plan emit the artifact its situation demands, with sequencing carried by directional links the team already trusts to render the frontier. It is the shape their wayfinder process already has, the shape their `WorkItemGraph` already models, and the only one of the three that retypes 32 existing Maps without declaring them wrong.
