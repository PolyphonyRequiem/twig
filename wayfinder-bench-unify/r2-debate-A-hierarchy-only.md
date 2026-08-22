# Position A (revised): The Hierarchy IS the Plan

**Claim.** An Execution Plan is a process orchestration map. Its children *are* its
components. The Plan takes the Hierarchy parent slot for the work it sequences. There is no
second, parallel structure that says "and here is what the plan really contains."

---

## 1. The strongest case, against the proposed model

The proposed model gives Execution Plan a purpose — "produces or at least organizes the
sequences of follow-up work" — and then leaves open whether that organization is expressed in
the one edge ADO computes over, or in some looser membership. Position A closes it: the
organization is the Hierarchy.

Three things follow that no alternative gets for free.

**Rollup climbs Hierarchy only.** This is the single fact with the most leverage in the whole
brief. Rollup is computed by Analytics, has no writable storage, cannot be hand-edited, and
cannot drift; its only documented failure modes are lag and scope-exclusion. That is a guard
that can actually fail, in this team's language — it is a number that is *derived*, not
asserted. If the Plan is not the parent, the Plan has no rollup at all: its progress becomes
something a human types, or something twig recomputes and displays. That is a mirror with no
compiler forcing the copies to agree. Position A is the only position that gets a
non-drifting progress number for a plan, and it gets it by doing nothing.

**One concept, one name.** If Plan-membership lives in Related links or a Bench selector while
composition lives in Hierarchy, "what is in this plan" has two answers and the code is not the
tiebreaker — two codes are. Position A refuses the second name.

**Deletes cleanly.** Remove the Execution Plan work item and its children are orphaned in
exactly one place, visibly, in the tool. There is no residue: no stale Related comments
describing a plan that no longer exists, no selector referencing a dead id.

And note what Position A does *not* require: no new summary type (Microsoft explicitly
advises against those), no reporting-only artifact, no parallel store. It uses backlog levels,
rollup, and Delivery Plans — precisely the stack Microsoft recommends.

---

## 2. 🔴 Ownership: three meanings, one edge — and responsibility is not among them

The owner's question deserves a direct answer, and the honest answer weakens the *usual*
argument for Position A while strengthening the actual one.

The Hierarchy edge is asked to carry three meanings: **composition** ("is part of"),
**rollup aggregation** (mechanical, climbs Hierarchy only), and **custody** ("who answers for
this reaching a terminal state"). Are these one relation or three?

They are **two relations and an illusion.** Composition and rollup are genuinely the same
relation — rollup is composition made arithmetic; it sums children because children are parts.
Those two are inseparable and correctly share an edge.

Custody is not on the edge at all. The facts settle it: every item has its own
`System.State`, its own `System.AssignedTo`, its own closure. A parent can be Done with open
children. ADO does not gate or cascade closure. There is no inherited accountability. If
Hierarchy conferred custody, "parent Done, children open" would be a contradiction the system
would refuse. It doesn't refuse it, because it isn't one.

So: **responsibility is item-atomic.** Each item is out for itself. The parent link tells you
where an item's numbers go, not who answers for it.

Does that help or hurt Position A? Honestly: **it helps, and it also shrinks the prize.**

It helps because the standard objection to Position A — "you are handing the Plan custody of
work the Feature is accountable for" — evaporates. The Plan is not taking accountability from
anyone; there is no accountability in the slot to take. The parent slot was only ever
composition + rollup. Giving it to the Plan costs materially less than the objection assumes,
because the assignee, the state, and the closure of every Change stay exactly where they were.
A Feature's designer remains the assignee of the Changes they wrote. Nothing moves.

It shrinks the prize because it means Position A *cannot* claim it is establishing
responsibility for the plan's execution. It is not. If a Plan is Done and three of its Changes
are open, ADO will shrug. Position A buys composition and a trustworthy rollup number. It does
not buy custody, and I will not pretend it does — claiming otherwise would be a false green:
a parent state that looks like it proved the children finished while proving nothing.

The honest formulation: **hierarchy answers "what is this made of and how much of it is
done"; it never answered "who answers for it."** Item-atomic responsibility is not a defect to
be patched — it is why a Plan can safely take the parent slot.

---

## 3. The one-parent constraint: a Change the Feature designed

A Change designed by a Feature and sequenced by a Plan can have one parent. Position A gives
it to the Plan. What the Feature loses is concrete and worth naming:

- The Change no longer rolls up into the Feature. Feature-level completion arithmetic is
  incomplete for any Change the Plan claimed.
- The Change disappears from the Feature's "add child" affordance and its child list.

What the Feature keeps: a Related link (symmetric, unlimited, with a comment attribute — "Change
designed by this Feature"), the Change's own state and assignee, area path, tags, and the
Change's own text. And crucially: twig's `WorkItemGraph` is exactly a set of items plus the
non-hierarchy edges among them, with edges leaving the set retained. Feature→Change design
provenance is a non-hierarchy edge. It is already a first-class citizen of the read model.

Why acceptable: a design association is *reference*; a plan membership is *composition with
arithmetic*. Only one of the two needs the edge that rollup climbs. Design provenance loses
nothing by being a Related edge because nobody wants to sum Changes into a design.

The cost is real, though: **a Feature's percent-complete becomes untrustworthy** wherever a
Plan has claimed its Changes. Position A should not hide that. It should say: Features are
design records; they should not be scored by rollup in the first place.

---

## 4. 🔴 The ordering hazard

Plan (Initiatives, rank 30) parenting Change/Spec/Validation (Work, rank 20) is cross-level:
ordering is safe. That is the designed case and it is fine.

The hazard is real where a Plan must sequence a **Feature** or **another Plan** — both at rank
30. Same-level parenting disables backlog ordering. The team has already hit this. I will not
argue it away.

Position A's answer is a scope rule, not a workaround: **an Execution Plan sequences Work-level
items only.** If a Plan appears to need a Feature as a child, that is the model telling you the
Plan is really sequencing that Feature's Changes, and the Plan should parent those directly.
Plan-of-Plans is likewise refused: a Plan that needs a sub-Plan is two Plans, related by
Predecessor/Successor — which Delivery Plans render natively as dependency lines.

This is a genuine narrowing. Position A is a claim about *Work-level orchestration*, and it
does not extend upward. A reviewer should weigh whether the team needs Initiative-level
sequencing; if it does, Position A cannot supply it without breaking ordering, and no amount of
argument changes that.

---

## 5. The missing dependency graph — concession, then the real argument

**Concession, explicitly:** Delivery Plans render dependency lines natively for
Predecessor/Successor. Any claim that ADO cannot visualise a dependency graph, or that a
frontier is invisible without opening items, is false. I withdraw it.

The real argument is different and, I think, stronger. Hierarchy and dependency answer
different questions and should not be collapsed:

- Hierarchy: *what is this composed of, and how much of it is done* — the only edge rollup
  climbs. Structural. Exactly one parent. Total.
- Predecessor/Successor: *what must precede what* — directional, unlimited, orthogonal.

Position A does not *lack* a dependency graph. It leaves the dependency graph free to be
exactly a dependency graph, on its own edge type, rendered natively — instead of overloading
composition to imply order. A hierarchy that also encodes sequence would be a concept with two
names. Under Position A, the Plan's children are its parts, its Predecessor/Successor links are
its order, and a `WorkItemGraph` over the Plan's subtree returns precisely the sequencing edges
among the plan's components with edges leaving the set retained. The two structures are
composable, not competing.

---

## 6. What Position A costs and cannot express

Stated plainly:

1. **A Change can belong to exactly one Plan.** Work genuinely sequenced by two plans must
   pick one and take a Related edge for the other.
2. **Feature rollup is degraded** wherever a Plan claims a Change.
3. **No Plan-of-Plans, no Plan-over-Features** — ordering forbids it. Position A is
   Work-level only.
4. **No custody.** A Done Plan proves nothing about its children's states. Position A's
   rollup number is trustworthy; its parent *state* is not.
5. **Nothing dynamic.** Membership is a set of stored links, not a Bench selector. A Plan
   cannot say "all open Changes in area X" — someone must re-parent by hand. That is a real
   loss against the team's own "rules, never results" principle, and the closest thing to a
   principled objection to this position.
6. **Artifacts.** Decision and Finding have no backlog level and do not appear on Delivery
   Plans. A Plan whose declared outputs include Specs, Decisions, and Commitments will render
   only partially. Hierarchy still holds the Decisions; the Delivery Plan just won't show them.

---

## 7. What would falsify Position A

Judged against the proposed model, not against any existing items:

1. **A Work-level type in the proposed model that legitimately needs two composition
   parents.** Concretely: if a Spec is genuinely *part of* both a Feature (which the model says
   may have Specs as children) and an Execution Plan (whose declared outputs include Specs),
   then the model already contains a two-parent composition and Hierarchy cannot hold it. This
   is the sharpest test available, and the model as written gestures at it. If the team decides
   Spec-under-Feature and Spec-under-Plan are both true composition, Position A is dead.
2. **A required Initiative-level sequence.** If the model needs Execution Plan to sequence a
   Feature or another Plan, ordering breaks and Position A cannot comply.
3. **Membership that must be a rule.** If the team decides plan membership should be a
   selector — consistent with Bench, "stores the rule, never the results" — then stored parent
   links are the wrong mechanism regardless of everything above.
4. **A demand that a Plan's Done state mean its children finished.** Position A cannot deliver
   that; ADO does not cascade closure. If that guarantee is required, it must come from a
   check outside the hierarchy, and Position A's parent state stays decorative.

If none of these hold, the Hierarchy is the plan, and every additional structure is a second
name for a concept that already has one.
