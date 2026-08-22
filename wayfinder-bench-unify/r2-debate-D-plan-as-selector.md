# Position D — An Execution Plan is a selector, not a work item

## The claim

An Execution Plan is not a thing that gets done. It is a *statement about which things are in scope*. Statements about scope are rules. Rules belong in a selector — a twig Bench, or the same rule expressed as an ADO shared query — not in a row of a work item table whose children are a hand-maintained copy of that rule's output.

Everything else in the proposed model has a completion condition. A Change merges. A Validation passes or fails. A Commitment is fulfilled to a third party. A Decision is recorded and frozen. Ask what it means for an Execution Plan to be *Done* and the only honest answer is "when everything the rule matches is Done" — which is not a state, it is a query. Storing a query's answer in parent links and hoping humans re-run it by hand is precisely "a mirror with no compiler forcing the copies to agree."

## Why the rule beats the links

**No parent slot to fight over.** Hierarchy is a tree: one parent, hard constraint. Every rival position spends its argument negotiating who gets that slot — Feature or Plan, delivery structure or execution sequence. Position D does not compete for the slot at all. It leaves Hierarchy free for the one thing it is actually good at: composition and rollup along the delivery structure.

**Multi-plan membership is native.** A Change that lands in two Execution Plans is not exotic; it is the normal case for shared infrastructure. Under parent-links it is impossible — one parent — so the team will invent a workaround (Related links, tags, duplicate items) and the workaround becomes the real model while the Plan's children become decorative. Under selectors, membership in three Benches is the *default* behaviour of a union of rules, with no ceremony. And twig's `WorkItemGraph` already carries the right intuition: "a link is an edge between two items, so it belongs to the SET, not to a member of it." Plan membership is a property of the set.

**Deletes cleanly.** Delete a Bench and nothing else changes: no orphaned children, no re-parenting migration, no items stranded at the root of a backlog. Contrast a Plan work item with 40 children — deleting it is a data-migration project.

**Cannot drift, because there is nothing to drift.** Rollup is the team's own proof of this principle: computed by Analytics, no writable storage, therefore incapable of being stale. A selector is the same shape of thing. A copied set of parent links is the opposite shape — the false green in structural form: the Plan *looks* complete because its listed children are complete, while proving nothing about the items that should have been listed and weren't.

## Ownership — the owner's question

The facts settle it. Every work item has its own `System.State`, its own `System.AssignedTo`, its own closure. A parent can be Done with open children. ADO neither gates nor cascades. **Responsibility is item-atomic.** There is no inherited accountability anywhere in the platform.

So what does a parent link actually buy? Three things bundled into one edge:

1. **Composition** — "this is part of that." Real.
2. **Rollup** — Analytics climbs Hierarchy. Real, and valuable.
3. **A false impression of custody** — "the parent is responsible for the children." Not real. Nothing enforces it, nothing computes it, nothing fails when it is violated.

That third strand is the whole appeal of Plan-as-parent, and it is the strand with no mechanism behind it. It is a guard that cannot fail. A selector-based Plan is *honest about owning nothing*: it says "these items are in scope of this plan," which is exactly true, and claims no custody it cannot enforce.

**The hard case: Commitment.** A Commitment's purpose is that someone answers for it externally. Does item-atomic responsibility reduce it to a work item with an assignee? Largely yes — and that is a feature, because the answer-for-it property is carried by `AssignedTo` plus a State that closes only on fulfilment, both of which are item-local and real. What a Commitment needs *beyond* that is not a parent; it is **direction**: which work must land before the promise can be kept. That is Predecessor/Successor — directional, unlimited, and (per the correction) rendered natively as dependency lines on Delivery Plans. A Commitment gets stronger from dependency edges than from adopting children it cannot close. Custody of a Commitment lives with a person, not with an edge.

## The costs — stated plainly, not minimised

- **A Bench is invisible outside twig.** It appears on no board, no backlog, no Delivery Plan. A stakeholder who lives in the ADO web UI cannot see it *at all*. This is not a rough edge; it is a category failure for anyone not running the CLI.
- **No rollup.** Rollup climbs Hierarchy only. A selector-defined plan gets no automatic progress aggregation. Any percent-complete must be computed by twig, which is new code and new trust.
- **No Delivery Plan row.** If Delivery Plans are load-bearing for how this team communicates schedule, this disqualifies Position D outright and no amount of modelling elegance rescues it. I cannot argue around that; I can only say that the decision hinges on it.
- **Requires unbuilt tooling.** Selectors today are pins and queries. A declared policy/rule engine and parameterised multi-hop queries are *not built*. Without multi-hop, "the plan is this item's dependency closure" is not expressible, and the Bench degrades toward a hand-curated pin list — the same manual copy I criticise, minus the visibility.
- **Known defect.** twig currently double-writes pins to both a file and the Bench store, a transitional state. The storage layer this position depends on is not yet clean.

**Is an ADO shared query a better carrier than a Bench?** For this purpose, **yes.** A shared query stores the rule, is visible in the ADO web UI, is shareable by URL, and works for stakeholders who will never install twig. It loses twig's graph reasoning and cannot express multi-hop closure — but it is the same idea with the fatal visibility gap closed. If Position D is adopted, it should be adopted as *shared query first, Bench as the local power tool over the same rule*.

## Compatibility — and where it defeats me

Could an Execution Plan work item exist for Delivery Plan visibility while a selector defines real membership? Honestly: **only if the work item has no children.** A childless Execution Plan carrying dates, an owner, and Predecessor/Successor edges is a legitimate schedule marker, and the selector is the single source of membership — one concept, one home, no copies to disagree.

But the moment that work item is allowed to parent its members, Position D is dead and the team's own instinct is right: two representations of membership with no compiler forcing agreement, and the parent-link copy will win by default because it is the one on the board. There is no half-measure. Either children mean membership or the selector does.

## What would falsify this

1. **Delivery Plans are load-bearing.** If stakeholders steer by Delivery Plan rows, a plan with no row is not a plan. Falsified.
2. **Rollup is the primary progress signal.** If "what percent of the plan is done" must come from Analytics rather than twig, Hierarchy is required. Falsified.
3. **Multi-plan membership turns out to be rare.** If in practice items belong to exactly one plan, the tree constraint costs nothing and my central advantage evaporates.
4. **Rules can't be written.** If actual plan membership is inherently ad hoc — "these 14 items because I say so" — then there is no rule to store, the selector degenerates to a pin list, and parent links are the more visible way to keep the same list.
5. **The tooling doesn't get built.** If the policy engine and multi-hop queries stay unbuilt for two quarters, this position was a promise, not a mechanism — and the team's rule is evidence over assertion.
