# Position C (revised): an Execution Plan contains the work needed to produce a plan

**Claim.** An Execution Plan's children are the *planning* activities — Grilling, Research, Prototype, Spec, Wayfinder Task — and its outputs are Specs, Decisions and Commitments. It does not parent the Changes, Validations and Bugs it schedules. It is deliberately non-committal about the *form* the resulting plan takes: a Spec document, a Bench selector, a set of Predecessor links, or all three.

## 1. New ground: the proposed model already answers this

Read the proposed type definitions literally, because the glossary rule says the code is the tiebreaker and here the definitions are the code.

**Change is defined as "a unit of work that can be pull-requested into main when completed."** That is a *repository* predicate, not a planning predicate. Nothing about a Change's identity, lifecycle or closure derives from the plan that scheduled it. A Change is complete when it merges. If a Plan parents it, the Plan's rollup now measures merge throughput and calls it planning progress — a metric that looks green because code landed, while proving nothing about whether the plan was any good. That is the false green, at the level of the type definition rather than the level of a broken check.

**Commitment is defined as "an external promise made to a third party."** A Commitment is listed as an *output* of an Execution Plan. Outputs and children cannot be the same relation under a tree: if the Plan parents the Commitment and also parents the Change that fulfils it, the Commitment cannot parent the Change. The single-parent constraint forces you to choose which of "the plan produced this promise" and "this promise is fulfilled by this work" gets to be Hierarchy. Position B's custody model spends the tree on the weaker of the two.

**Feature is defined as "captures actual designs as well as the changes and evidence they bear."** *Bear* is custody. The proposed model already assigns a custodian for Changes and evidence, and it is not the Plan. A Plan that parents Changes is competing with Feature for the one parent slot each Change has. Two Initiative-level types claiming custody of the same Work-level items is exactly "a mirror with no compiler forcing the copies to agree" — except worse, because ADO's tree *is* the compiler and it will silently let whichever link was written last win.

**Artifacts are level-less by design** because they are immutable point-in-time records with no lifecycle. Decision and Finding are already carved out this way. The plan-as-document is the same kind of thing: a record of what was decided about sequence at a point in time. Under my position that record is a Spec (Work level, has a lifecycle — specs get revised) or a Decision (Artifact, immutable). The model already has both slots. Position B needs the *Plan work item itself* to be the artifact, which makes an Initiative-level, lifecycle-bearing, rollup-aggregating item do the job the model reserves for level-less records.

## 2. Ownership: three meanings, and the honest cost to me

ADO's Hierarchy edge bundles three things, and they are genuinely three relations, not one:

- **Composition** — "this is part of that." Structural, static.
- **Rollup aggregation** — computed by Analytics, climbs Hierarchy only, no writable storage. Cannot drift. This is the one meaning ADO actually *implements*.
- **Custody/responsibility** — "someone is on the hook for this getting done."

The established facts settle the third. Every item has its own State, its own AssignedTo, its own closure. A parent can be Done with open children; ADO does not gate or cascade. **Responsibility is item-atomic.** There is no inherited accountability, and therefore no such thing as transferring custody by parenting.

I will not pretend this is clean vindication. It cuts both ways and the second edge is real.

*It vindicates me* in this sense: Position B's language of the Plan "taking custody" of planned work describes something the tool does not do. Nothing is transferred. What parenting actually buys is **rollup and Delivery Plan display** — reporting. So the debate was always about reporting, and Microsoft explicitly does not recommend creating work item types for reporting purposes. If the Execution Plan type exists to make a rollup number appear, it is a summary type by another name.

*It undermines me* in this sense: if custody never moves, then "the Plan should not take custody" is objecting to something that was never going to happen. Parenting is not theft. My objection has to be restated, and the restatement is narrower and weaker: parenting is not *harmful* because of custody, it is harmful because **the tree is a scarce resource**. Each work item has exactly one parent. Spending it on "which plan scheduled this" means not spending it on "which Feature bears this." Same-level parenting additionally disables backlog ordering — a named hazard. My real claim is not custody, it is **allocation of the single parent slot**, plus the honest observation that rollup over scheduled work measures the wrong thing.

That is a smaller claim than I made in round one. It is the one the evidence supports.

## 3. The existential objection: I bite the bullet, partly

My reasoning does imply that an Execution Plan and an Investigation are structurally the same shape: an Initiative-level container of Grilling / Research / Prototype / Spec, ending in a written record. If the only difference were the terminal condition, the honest answer would be one type with a mode field, per "don't invent a name to avoid a rename."

There *is* a real difference, but it is narrower than the proposal implies, and it is about **output type, not children**:

- Investigation **ends when the fog clears** and produces **Findings** — level-less, immutable, no downstream obligation. An Investigation can conclude "we now know X" and be complete.
- Execution Plan ends when a **Commitment** or **Spec** exists — items that are Work-level, that have their own lifecycle, and that *bind someone else*. A Commitment is an external promise; it survives the Plan and is fulfilled independently.

So: an Investigation terminates in knowledge; an Execution Plan terminates in an obligation. That is a genuine type distinction — different output types, different closure predicates, different consequences for other people. It justifies two types.

But I concede the weaker form of the objection: if the team finds it cannot state a closure predicate for Execution Plan that differs from Investigation's other than by which artifact type appears, they should collapse them into one type with a `mode` field and delete the other. I would accept that outcome; it does not damage the substance of my position, which is about what the children mean.

## 4. Where sequencing lives — concretely

Three mechanisms, each for a distinct thing. None is the Hierarchy edge.

1. **Order between specific items: Predecessor/Successor on the planned work itself.** Directional, unlimited, and — per the corrected fact — **rendered natively as dependency lines on Delivery Plans**. There is no visualisation gap. The dependency graph is on the items that actually execute, which is where a stale link is visible to the person doing the work rather than to a reporting layer. This is where sequence *primarily* lives.
2. **The scope of the plan: a twig Bench.** A named, durable, saved backlog holding *selectors, never results*. "The work this plan covers" is a rule — tag, area path, iteration, link predicate — that re-evaluates. Work added later that matches is in scope automatically; work that stops matching drops out. A Hierarchy tree is a materialised result set that must be hand-maintained. Bench is a guard that can actually fail; a stale parent link cannot fail, it just quietly lies.
3. **The rationale and the shape: a Spec (revisable) or a Decision (immutable).** Why this order, what was rejected, what is not known. This is the plan-as-document, and it is a first-class item in the proposed model.

And in twig's own read model: `WorkItemGraph` is "a SET of work items and the non-hierarchy edges among them" — "a link is an edge between two items, so it belongs to the SET, not to a member of it." Sequencing is an edge property. Under Position B it becomes a property of the parent member. twig's own model says that is the wrong home.

## 5. What this costs

Honestly, four things.

- **No single-click rollup for "how much of the plan is done."** Rollup climbs Hierarchy only, and I have declined to build the Hierarchy. Progress over planned work must come from a Bench query or Analytics (which is flat, so multi-hop is manual). Parameterised multi-hop queries are an unbuilt Idea and I may not assume it.
- **Scope is soft.** A Bench selector answers "what matches this rule now," not "what the plan committed to on day one." Position B's tree gives you a frozen list. That is a real expressive loss — the difference between rule and snapshot.
- **The Plan's own closure gets vaguer.** "Done when the Spec/Commitment exists" is weaker than "done when children are done." I accept this; it is the price of not measuring merge throughput as planning progress.
- **Two edge types to maintain instead of one.** Predecessor links plus a Bench rule is more moving parts than parenting.

I cannot express, in ADO alone: "these eleven items, exactly these, were the agreed scope on 2026-08-22." That needs a Decision artifact listing them — which is a record, not a live edge, and will go stale. I would rather have a record that is visibly a snapshot than a tree that pretends to be live.

## 6. What would falsify this — in the proposed model

- **The closure predicate collapses.** If the team writes down Execution Plan's done-condition and Investigation's and they differ only in the artifact type name, then §3's distinction is cosmetic and Position C's Plan should be merged into Investigation. My type-justification fails.
- **Commitment turns out to need the tree.** If fulfilling a Commitment requires rollup over its Changes — an auditable "this promise is N% delivered" for a third party — then a Work-level item legitimately parents Changes, and the argument that Initiative-level containers shouldn't parent execution work loses its principle.
- **Feature does not, in practice, claim Changes.** My allocation argument depends on Feature being the custodian that "bears" changes and evidence. If teams route Changes under Plans and leave Features empty, the single parent slot is not contested and the strongest version of my objection is gone.
- **A rule engine ships.** One of the two open Ideas is a declared policy engine. If it lands and can enforce "Execution Plan may not parent Change," then parenting becomes a guard that can actually fail rather than a convention, and much of my worry about silent drift is answered by tooling instead of by type design.
