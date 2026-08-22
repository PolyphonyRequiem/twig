# Position B: An Execution Plan needs BOTH hierarchy and a dependency graph

## 1. The tree and the edges answer different questions

Hierarchy answers **"what is this made of, and who owns it?"** Predecessor/successor answers **"what has to be true before this can start?"** These are not two encodings of one fact. They are two facts.

Concretely, in this model:

- **The tree carries**: containment and ownership. Rollup (Analytics-computed, no writable storage, so it cannot be hand-edited or drift) sums child effort onto the parent. Backlog level placement — Initiative tier for Execution Plan — determines Delivery Plan rendering and board affordances. Deletion semantics: a subtree deletes cleanly as a unit. "Whose plan is this?" is a tree question.
- **The edges carry**: sequence, and specifically *partial* order. Predecessor/Successor links are directional. The frontier — open, unblocked, unclaimed — is computable *only* from them.

A tree gives you a *total* order at best (sibling rank), and rank is a lie about concurrency. This is the core loss.

### The scenario where tree-order is wrong

Execution Plan "Ship twig policy engine" has children:

```
EP: Ship policy engine
├─ Spec: policy DSL surface
├─ Change: rule evaluator
├─ Change: `twig policy check` command
├─ Change: CI wiring for policy check
└─ Validation: policy engine end-to-end
```

Backlog rank orders these 1–5. Read as sequence, that says: do the evaluator, *then* the command, *then* CI, *then* validation — five serial steps.

The truth is:

- `rule evaluator` and `twig policy check` both depend on `Spec: policy DSL surface`. They are **parallel** once the spec closes. Two agents can take them simultaneously. Rank-as-sequence hides this and idles an agent.
- `CI wiring` is a **fan-in**: it needs *both* Changes done. Rank only tells you it comes after the command; it says nothing about the evaluator. If the evaluator slips, rank-order gives the *wrong* green light.
- `Validation` depends on `CI wiring` **and** on a Change that lives in a *different subtree entirely* — a `Change: ADO link-type read path` owned by a Feature, not by this Plan. Tree order cannot express this at all. There is no rank relationship between nodes in disjoint subtrees. The graph expresses it with one directional edge, and twig's `WorkItemGraph` already **retains edges leaving the set** — the model was built for exactly this.

So: tree-order alone answers "what can I take right now?" with a *wrong* answer (serial, and blind to the cross-subtree blocker). The graph answers it correctly. That is not a nicety; a wrong frontier is a scheduling defect that costs real agent-hours.

## 2. The maintenance cost, head on

Every edge is state a human creates and must keep true. That is real and I won't soften it. But the team's aversion is specifically to **"a mirror with no compiler forcing the copies to agree"** — *duplicated* state. A dependency edge is not a duplicate of anything. There is no other place in the system where "the evaluator must land before CI wiring" is written down. Delete the edge and the fact is *gone*, not *contradicted*. That is the signature of primary state, not a mirror.

Compare with the alternative: encoding sequence in backlog rank. *That* is the mirror. Rank already means "priority for pulling off a backlog"; overloading it with "must happen after" gives one field two meanings that can silently disagree — reprioritise anything and your dependency claim quietly becomes false, with no failure. That is a false green: a plan that *looks* sequenced while proving nothing.

Honest concession: edges **can** go stale in one specific mode — an edge that was true and is no longer, e.g. the blocker was descoped rather than closed. This is a genuine drift risk and Position A is right to name it. Two things make it worth paying:

1. The team has an **open Idea for a declared policy/rule engine in twig**, precisely because ADO can't enforce their model. Edge invariants (no cycles; no edge into a closed-and-descoped item; every Change in an EP reachable from the EP) are exactly the kind of guard that *can actually fail* — the team's stated standard. A stale edge is detectable. A misread rank is not.
2. Stale edges fail **loudly and in the right direction**: they over-block. Work sits visibly unavailable and someone investigates. Missing sequence fails *silently*: work is taken too early and breaks downstream.

## 3. The one-parent constraint

The hard constraint: a work item has exactly one Parent. So: a `Change` that a **Feature** designs and an **Execution Plan** sequences — who owns it?

Position A must choose one, and by choosing one it *destroys* the other relationship. If the Change parents to the Plan, the Feature loses its design-to-change trace ("Feature captures actual designs as well as the changes and evidence they bear" — that's the Feature's stated job). If it parents to the Feature, the Plan cannot see it at all, cannot roll it up, and cannot sequence it.

With the graph available, the question stops being a dilemma and becomes an assignment rule: **parent by composition, link by sequence.** The Change's parent is the Feature — the Feature *is what the Change is part of*. The Execution Plan reaches it by a directional edge. The Plan's frontier is computed over the edge set, not the child set, so the Plan sequences work it does not own. This is the same shape as twig's read model: "a link is an edge between two items, so it belongs to the SET, not to a member of it." The Plan is the set.

This also removes the pressure to invent a proxy work item to stand in for the out-of-subtree Change — which would violate "don't invent a name to avoid a rename" and Microsoft's explicit non-recommendation of extra types for structural/reporting purposes.

## 4. The wayfinder precedent — and its limits

The team's own process **already does this**: "Blocking uses the tracker's NATIVE dependency relationship: essential because it renders the frontier VISUALLY in the tracker's own UI." Tickets are children of the map; blocking is a separate edge. The map is "an INDEX, not a store." That is hierarchy-plus-graph, in production, chosen deliberately, with a stated reason.

Honest limit on this precedent: wayfinder maps are **small and short-lived** — the map "ENDS when the fog clears," and fog-of-war means it is deliberately incomplete, so the edge count stays bounded. An Execution Plan that organizes follow-up work across Features and Commitments may be larger and longer-lived, so edge maintenance scales worse than the precedent demonstrates. The precedent proves the *shape* works and that the team already pays this cost willingly. It does not prove the cost stays small at Execution Plan scale. Note also the 32 existing Maps being retyped: whatever edges they carry are evidence available *today* on actual volume.

## 5. What this costs, and what it cannot express

- **Cost**: two structures to keep true instead of one. Onboarding is harder: "why is this a child *and* that a predecessor?" needs teaching.
- **Cost**: Analytics views are FLAT — "work item hierarchies aren't supported" — and Delivery Plans render actual work items from backlog levels. Neither renders a dependency graph natively. Frontier computation therefore lives in **twig**, not in ADO reporting. That is a real tool-build obligation, and it interacts badly with the noted limitation that ADO saved queries hardcode their root and cannot chain hierarchy-then-related.
- **Cannot express**: edge *strength* or *reason*. Predecessor/Successor is a bare directional link. "Blocks because of a shared file" and "blocks because of a design decision" look identical. `Related` carries an optional comment; Predecessor's semantics here do not give you a first-class rationale field.
- **Cannot express**: conditional or probabilistic sequence ("only blocks if we take approach X"). Fog-of-war dependencies must be omitted until resolved, which means the graph is *also* deliberately incomplete — and unlike the map body, it has no "Not yet specified" section to record what is unknown.

## 6. What would falsify this

1. **Measured edge count near zero.** Inspect the 32 existing Maps. If their children carry few or no dependency links in practice, the graph is aspirational and rank ordering is what people actually use. That is decisive evidence against me.
2. **No real cross-subtree dependencies.** If every Change an Execution Plan sequences turns out to be one it could legitimately own, the one-parent argument collapses and Position A wins on simplicity.
3. **Measured staleness.** If an audit of existing blocking links shows a material fraction pointing at closed-or-descoped items, then edges *are* drifting, and my "primary state, not a mirror" claim weakens to "primary state that rots."
4. **A cheaper derivation.** If the policy engine can *derive* correct sequence from something already maintained — spec/artifact producer-consumer relations, say — then hand-authored edges are redundant and should be deleted, per "things that delete cleanly."

The falsifiers are cheap to run. Run 1 and 3 against the 32 Maps before committing.
