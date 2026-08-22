# Position A — The Execution Plan IS its hierarchy

**Claim:** An Execution Plan is a process orchestration map. Its children *are* the components of
the plan. Sequence is read off the tree — sibling order under a parent, depth for decomposition.
Predecessor/Successor edges are not needed at the Plan tier, and adding them costs more than it buys.

---

## 1. What the children actually are, and what a session does

An Execution Plan named "Ship Change work-item type" has children like:

```
Execution Plan: Ship Change work-item type
  1. Grilling: settle Change vs Task boundary       (HITL)
  2. Spec: Change type definition + field set       (AFK, after 1)
  3. Change: add Change type to process template    (AFK)
  4. Change: twig `wi create --type change`         (AFK)
  5. Validation: retype 3 sample Maps end-to-end    (HITL)
  6. Commitment: notify org of retype window        (external promise)
```

Every child is a real work item of a real type, with its own lifecycle. Nothing is a placeholder.
The Plan body carries what the Wayfinder map body carries — Destination, Decisions so far, Not yet
specified, Out of scope — and, per the team's own rule, it is an **index, not a store**: it gists and
links, it does not restate.

A session working this Plan does exactly one loop: **open the Plan, read the ordered child list top to
bottom, take the first open child that isn't already claimed, do it, close it, re-read.** That is the
whole protocol. It needs no query language, no graph traversal, no multi-hop join. It survives a
context reset: the tree is the state.

Sequence is expressed three ways, all native to the tree:

- **Sibling order** (`Microsoft.VSTS.Common.StackRank`) — "2 comes after 1." ADO already sorts backlogs
  and boards by it, and drag-to-reorder already writes it. It is *the* first-class ordering primitive
  in the product.
- **Depth** — a child with children is a sub-sequence; you finish its subtree before moving on.
- **Absence** — fog. Work that isn't a child yet isn't sequenced yet, and the team explicitly values
  "recording what is NOT known." An empty region of the tree says that out loud.

Ordering-in-a-tree is a *total* order over siblings. Dependency graphs give a *partial* order. A total
order is strictly more decisive: it answers "what next" with one item, not a set. For directing
engineers and agents — the stated purpose — decisiveness is the product.

## 2. The one-parent constraint — the Plan owns the child

This is the crux and it has a clean answer. **The Execution Plan is the parent. The Feature is
`Related`.**

The justification is not convenience, it is the semantics of the two links. `System.LinkTypes.Hierarchy`
is a tree with the documented hard rule "a work item can have only one Parent." That single slot should
go to whichever relationship is *operationally load-bearing* — the one a person or agent traverses to
decide what to do next. That is the Plan. Membership in a Feature is a *taxonomic* fact, not an
operational one; nobody schedules from it. `System.LinkTypes.Related` is symmetric, unlimited, and
carries a comment attribute — a perfect fit for "this Change belongs to Feature X," and the comment
field can even say why.

The alternative — Plan holds **proxy/component items** that point at the real work items — must be
rejected outright, and by the team's own doctrine. A proxy is *a mirror with no compiler forcing the
copies to agree*. Two states (proxy status, real item status) that drift silently, which the team blames
for several of their worst bugs. It also violates the glossary rule "a concept has one name": the Change
would exist twice under two names. And Microsoft explicitly does **not** recommend creating extra work
item types for reporting/summary purposes; there is no summary/virtual card type in ADO because the
product deliberately renders *actual* work items. Proxies are a false green in work-item form: a Plan
that looks complete because its shadows are closed.

So: **one parent, and it's the Plan.** Feature keeps its Specs and Decisions as children (those are
genuinely owned by the design, not sequenced by anyone) and relates to the Changes that realize it.
This is also cheap to enforce: the team's open Idea for a declared policy engine in twig can assert
"a Change parented to a Feature must have zero Plan relations, and vice versa" — a guard that can
actually fail.

## 3. Why no dependency graph is a feature

**Legibility.** A tree renders itself. The backlog, the board, the query results, Delivery Plans — all
already show hierarchy and stack rank without configuration. A dependency graph in ADO renders as a
list of link rows on a form; you cannot see the frontier without opening every item. The Wayfinder
process uses native blocking precisely because in *that* tracker it "renders the frontier VISUALLY in
the tracker's own UI." That justification is a property of the tool, not of planning — and in ADO it
does not hold. Importing the mechanism without the property is cargo cult.

**Edge cost is quadratic in attention.** n children can carry up to n(n-1)/2 edges. Every insertion,
split, or reorder invites re-editing edges. A tree reorder is one drag.

**Staleness is the killer.** A stale predecessor edge is a guard wired to nothing: it silently blocks
takeable work, or silently permits work whose real prerequisite moved. Nothing compiles it; nothing
fails. A stale *order*, by contrast, is visible the moment you read the list — the wrong item is at the
top, and a human sees it.

**It deletes cleanly.** Close the Plan, its children stand alone. Delete a graph and you have to know
which edges were structural and which were scheduling.

## 4. What this costs

Honestly:

- **True fan-in is inexpressible.** "C needs both A and B, which are unrelated" becomes "A, B, C in
  that order" — over-serialization. Parallelizable work looks sequential; two agents reading the same
  Plan must coordinate by claiming, not by computing an unblocked set.
- **Cross-Plan dependency has no home.** If Plan P's item 3 waits on Plan Q's item 7, the tree cannot
  say so. Only a `Related` link with a prose comment, which no tool can evaluate.
- **The Feature loses hierarchical rollup over its Changes.** Rollup follows hierarchy only, and
  Analytics views are flat ("work item hierarchies aren't supported"). Feature-level "% of my changes
  done" must be computed in twig over `Related` edges — exactly the multi-hop query the team has an
  open Idea for and does not yet have.
- **Reordering is a real edit** with no audit of *why* the order is what it is. A dependency edge is
  self-documenting; a stack rank is not.
- **It concentrates authority in the Plan author.** Order is an assertion, not a derivation.

## 5. What would falsify this

- **Frontier width.** Instrument it: if, across the 32 retyped Maps, the median Plan routinely has ≥3
  children that are genuinely concurrent, the total order is lying and a partial order is required.
- **Order churn.** If stack ranks are re-edited more often than a dependency graph would have been
  re-edited, the tree is not cheaper — it is just paying the cost in a different currency.
- **Contention.** If two agents working one Plan repeatedly collide or idle, "take the top item" has
  failed as a protocol.
- **The Feature reclaims the parent slot.** If, in practice, people navigate Feature→Changes far more
  than Plan→Changes, then the operationally load-bearing link is the Feature and Section 2's premise
  is wrong.
- **Reverse-drift.** If `Related` Feature links go unmaintained *more* than dependency edges would
  have, the drift argument cuts against me, not for me.
