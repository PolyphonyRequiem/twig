# Impartial review — Execution Plan children: A vs B vs C

Reviewer note: I was assigned no position. Below, "A", "B", "C" refer to the three papers.

## 1. Fact-check

**All three are broadly faithful to the evidence files. There are no gross factual violations.** But there are several overstatements and one clear error.

**B, §5, is wrong on the record:** *"Analytics views are FLAT ... and Delivery Plans render actual work items from backlog levels. Neither renders a dependency graph natively."* The second clause is false. `ado-audience-views.md` documents Delivery Plans' **dependency lines**: *"view options let you 'Show and hide dependencies between work items.' Dependencies require Predecessor/Successor or custom dependency links."* ADO renders a dependency graph natively in exactly the surface B says it does not. This is a self-inflicted wound — the fact **supports** B and B conceded it away. It also damages A.

**A, §3, is the largest overreach in the set:** *"A dependency graph in ADO renders as a list of link rows on a form; you cannot see the frontier without opening every item... The Wayfinder process uses native blocking precisely because in *that* tracker it 'renders the frontier VISUALLY'... That justification is a property of the tool, not of planning — and in ADO it does not hold. Importing the mechanism without the property is cargo cult."* The premise is factually wrong per the dependency-lines quote above, and A's whole "legibility" section — one of its three pillars — rests on it. The "cargo cult" charge is rhetoric built on an unchecked assumption. A presented inference as fact and did not check the evidence file that refutes it.

**A, §2:** *"Membership in a Feature is a taxonomic fact, not an operational one; nobody schedules from it."* "Nobody schedules from it" is asserted, not evidenced. A's own falsifier #4 admits it is an empirical claim. Presenting it in the body as settled and in §5 as testable is having it both ways.

**A, §1:** *"Ordering-in-a-tree is a total order over siblings."* True, but A then says "a total order is strictly more decisive." Decisive, yes; *correct*, not established. B's §1 dismantles this: decisiveness bought by asserting an order you did not verify is precisely the team's "false green" — a plan that looks sequenced while proving nothing. B wins this exchange outright.

**C, §3.4:** *"C needs no new ADO type to become visible; A and B need one anyway, because Analytics views are FLAT."* Non sequitur. Analytics flatness is a reporting limitation shared by all three; it does not create a need for a new work item type under A/B, and C is already proposing to keep the Execution Plan type regardless. Rhetorical padding.

**C, §1:** *"if the plan's structure is expressed only as an ADO parentage that nobody re-checks after the plan closes, closing the plan renders it green while proving nothing"* — this is speculative and stacked with "if... only... nobody." A closed plan whose children are all closed *is* proving something (rollup is Analytics-computed and, per the evidence, cannot be hand-edited). C invokes "false green" where the mechanism does not apply. This is the clearest case in the three papers of **culture-flattering rhetoric that is not sound**: the false-green defect class requires a check that *cannot* fail; parent-child closure is a check that can.

**C, §2:** the "'produces or at least organizes' — 'or at least' is the tell" reading is textual inference presented as evidence of the owner's intent. Suggestive; not fact.

**B, §2:** *"Rank already means 'priority for pulling off a backlog'; overloading it with 'must happen after' gives one field two meanings."* This is the single best-argued point in any of the three papers, and it is properly grounded — `ado-backlog-levels.md` confirms rank is the backlog-ordering primitive with its own semantics, and the team's glossary rule is "a concept has one name." B turns A's own culture appeal against it, correctly.

**Neither A nor C mentions** the documented ordering hazard directly relevant to them: same-level parenting *"results in a nested item that disables the ordering feature"* (REORD), which the team already hit with Map→Grilling. A's entire scheme depends on stack-rank ordering under a Plan; if Execution Plan and its children land on the same backlog level, **A's ordering primitive is disabled by the product**. That is a live threat to A that A never addresses. C, whose children are Grillings/Specs, sits in the same hazard and also ignores it.

## 2. The crux: the one-parent constraint

- **A:** the Plan takes the parent slot; the Feature gets `Related`.
- **B:** the Feature takes the parent slot; the Plan reaches the Change by a directional edge and computes its frontier over the edge set.
- **C:** no transfer occurs — the Plan's children are *planning* work only, so it never competes for custody of the planned work.

**B's answer is the most defensible**, and not because it is the middle option. A and B both accept that the item has exactly one natural containment parent; the question is which relationship is *lossy* when demoted. A demotes composition to `Related` — a symmetric, untyped link that destroys direction and, critically, destroys **rollup**, which A concedes in §4 ("the Feature loses hierarchical rollup over its Changes"). B demotes sequence to Predecessor/Successor — a link type that is *already* directional and *already* means sequence. B loses nothing in the demotion because there is no demotion: it puts each fact in the link type designed for that fact. A's move is a genuine semantic downgrade.

A's justification — the parent slot goes to whatever is "operationally load-bearing" — is a reasonable principle but A never establishes that the Plan is that thing, and the principle cuts the other way once you note Plans are transient and Features are durable. Handing the permanent structural slot to the shorter-lived object is the weaker trade.

**Costs.** A costs: no Feature rollup, no cross-plan dependency, no fan-in, and it needs the not-yet-built multi-hop query to answer "what's in this Feature." B costs: two structures to maintain, no Plan-level rollup of planned work (B is quiet about this — its §1 claims rollup as a tree benefit while its §3 puts the work outside the tree, so the Plan rolls up nothing; **that is an unacknowledged internal tension in B**), and frontier computation in twig. C costs: no rollup, no Delivery Plan row for plan scope, and no way to say "these ten items are a bounded deliverable" — which C states honestly and which may be fatal if scope demarcation is the real job.

C's avoidance is legitimate, not evasion — it dissolves the dilemma rather than answering it. But dissolving it re-raises the question one level up: under C, *something* still has to express "the sequence we committed to," and C's answer (a Decision plus edges) is B's mechanism with the container removed. **C is B minus the plan-level set.** Which means C does not actually escape B's maintenance cost; it inherits it and gives up the container that would have made it legible.

## 3. Argument quality

**A — strongest:** the staleness asymmetry. "A stale order is visible the moment you read the list; a stale edge is a guard wired to nothing." That is a real and underrated point, well matched to a team that values guards that can actually fail. **Weakest:** the legibility argument, which is factually wrong (§1 above). **Overreach:** "cargo cult," and "nobody schedules from it." **Objection handling:** A *restates* the fan-in objection in §4 rather than defeating it — "true fan-in is inexpressible" is a concession, correctly labelled, but A never argues fan-in is rare, which is what it would need.

**B — strongest:** the rank-overloading argument (§2), which is the best single paragraph in the debate: it identifies that A's proposal creates the exact duplicated-meaning defect A uses to attack proxies. Second strongest: the worked example in §1 showing rank gives the *wrong* green light on fan-in. **Weakest:** the unacknowledged loss of Plan-level rollup, and the false claim that ADO renders no dependency graph. **Overreach:** "stale edges fail loudly and in the right direction: they over-block." Over-blocking is only loud if someone is *looking* at the blocked item; a permanently over-blocked item is invisible in exactly the way an unstarted item is. B asserts a failure mode's visibility without evidence. **Objection handling:** B genuinely *defeats* the mirror objection ("delete the edge and the fact is gone, not contradicted" — that is a correct and sharp test for primary vs. mirrored state) and genuinely *concedes* the precedent-scale objection in §4 rather than papering over it. B is the most intellectually honest of the three on its own weak points, one factual error aside.

**C — strongest:** the migration argument. "32 existing Maps ... C retypes them correctly and losslessly. A and B declare all 32 malformed on day one." That is concrete, checkable, and neither rival addresses it. Second: the appeal to `WorkItemGraph` — "a link belongs to the SET, not to a member of it" — is the closest thing to "the code is the tiebreaker" in the debate. **Weakest:** C never says what an Execution Plan is *for* that a wayfinder map is not. C's §2 argues the two are the same shape with a different terminal condition — which is a strong argument that the team should **not create the type at all** and just keep using Maps. C does not notice that its own argument undercuts the premise of the exercise. **Overreach:** the false-green attack in §1 (mechanism doesn't apply), and §4's Analytics non-sequitur. **Objection handling:** §4 ("Is this a non-answer?") is the weakest section in any paper — it answers "no, it's narrower" and then lists commitments that are mostly negative ("does not take custody"). It restates rather than defeats.

## 4. Hidden agreement

The three disagree far less than the framing suggests.

**All three endorse Predecessor/Successor edges on the work.** A rejects them "at the Plan tier" (§ title: "Why no dependency graph is a feature") but its §4 concedes cross-plan dependency needs *something*, and A never proposes forbidding blocking links on the underlying Changes. B and C both build on them explicitly. **The live disagreement is not "graph or no graph" — it is "does the Plan own its children."** A says yes, C says no, B says no-but-the-Plan-is-a-set. On the actual crux, **B and C are on the same side**, and B is closer to C than to A.

**All three assume unbuilt twig tooling.** A §4: Feature rollup "must be computed in twig over `Related` edges — exactly the multi-hop query the team ... does not yet have." A §2 leans on the policy engine to enforce its parenting rule. B §2 leans on the policy engine for edge invariants and §5 concedes frontier computation "lives in twig, not in ADO." C §3.4 names both open Ideas as "precisely the tooling that makes a link-defined plan legible" and §5 admits "until twig ships those queries, C is more painful day-to-day." **Every position is a promissory note on two Ideas that are not code.** This is the single largest shared risk in the debate and no paper treats it as a reason to defer the decision. A is the least dependent (its core loop needs no tooling); C is the most dependent and says so.

**All three accept:** that ADO enforces nothing, that proxy/summary items are forbidden, that rollup follows hierarchy only, and that the wayfinder process is the reference model. All three cite the *same* wayfinder facts for opposite conclusions — B and C read "blocking uses native dependency links" as endorsement of edges; A reads it as tool-specific and inapplicable. Only one of those readings survives the dependency-lines fact, and it is not A's.

## 5. The falsifiers, ranked by information-gained-per-cost

Thirteen falsifiers are named. Most reduce to four measurements against the 32 Maps. Ranked:

**1. Count Predecessor/Successor links among the children of the 32 Maps.** (B-1, and it bears on A §3, C's fourth falsifier.) One twig query or WIQL run; minutes. It is the highest-value measurement in the debate because it discriminates in *both* directions: near-zero edges kills B and badly damages C (which relies on edges for sequence); substantial edges kills A's "edges cost more than they buy" and, more importantly, kills A's claim that the team doesn't want them. **Highest information per cost by a wide margin.**

**2. Classify the children of the 32 Maps: planning work vs. executable work.** (C's first falsifier, and the inverse of A's premise.) One query returning child types plus a human eyeball over ~32 parents; an hour at most. This *directly settles* the C-vs-(A,B) axis, which is the primary axis of disagreement. It is nearly as cheap as #1 and answers a bigger question. I rank it second only because #1 is cheaper and also informs it. **Do both, in this order, before anything else.**

**3. Count children whose natural parent is a Feature (or another non-Map item).** (B-2, A-4, C's third falsifier.) Requires judgement per item, so a few hours. If Map children rarely have a competing parent, the entire one-parent crux is a phantom and A wins on simplicity — and that would be the most consequential finding available. Costlier than #1–2 but resolves §2 of this review.

**4. Audit existing blocking links for staleness — edges pointing at closed-or-descoped items.** (B-3, C's fourth falsifier.) Cheap *if* #1 returns a non-trivial edge count; meaningless if it returns zero. Conditional on #1, so it ranks fourth by ordering, not by value. It is the only test that adjudicates A's strongest argument (staleness) against B's strongest rebuttal (primary state, fails loudly).

**5. Order churn — revision history on `StackRank` for Map children.** (A-2.) Available from work item history but needs a script; medium cost, and the result is hard to interpret because it has no baseline to compare against (the counterfactual dependency-edge churn does not exist). Moderate value.

**Not cheap, not runnable now, ignore for decision purposes:** A-3 (agent contention) and A-5 (reverse-drift of `Related` links) both require the new model to already be in production. C's fifth falsifier (plans closing before their work completes) needs longitudinal data. B-4 ("a cheaper derivation exists") is not a measurement, it is a research project.

**Empirical verdict on the falsifier sets:** B's are the best-constructed — all four are stated as measurements against the 32 Maps, and B explicitly says "run 1 and 3 against the 32 Maps before committing." C's are good but two of five are counterfactual. A's are the weakest: three of five require deploying A first, which means A has named falsifiers that cannot fail before commitment. **For a team that values "guards that can actually fail," A's falsifier set is itself a false green.** That is a fair application of the team's own standard, and it counts against A.

## 6. What all three missed

**(a) The backlog-level ordering hazard.** As noted in §1: same-level parenting *"disables the ordering feature."* The team has already hit this with Map→Grilling. No paper addresses where Execution Plan sits relative to its children on the level hierarchy. This is a **precondition question**: A is unimplementable as written if Plan and children share a level, and C's children (Grillings, Specs) are exactly the case that already broke.

**(b) A fourth option: no new type at all.** C's §2 argues the Execution Plan is the wayfinder map with a different terminal condition. If that is true, the honest conclusion is *rename Map to Execution Plan and change nothing else* — or don't rename. Microsoft's documented non-recommendation of extra types for structural purposes, which all three cite against their rivals, cuts against *creating a distinguishable type* at all. None of the three considers "the answer is that this is the same object."

**(c) A fifth option: the Plan as a saved query / bench, not a work item.** The team's own product has `Bench` — "a named, durable, saved backlog holding selectors (rules, never results)" (map.md). A plan-as-selector has no parent slot to fight over, expresses multi-plan membership natively, deletes cleanly, and cannot drift because it stores rules not results. It loses the ADO-native rendering and the Delivery Plan row. That this option is absent from all three papers, in a repo whose current map is *about* Benches, is a striking gap.

**(d) Nobody asked who reads the plan.** A optimises for an agent's next-action loop. B optimises for frontier correctness. C optimises for migration fidelity and optionality. These are three different consumers and the papers never adjudicate between them. "Who is the primary reader — a human status audience, an engineer picking up work, or an agent with no context?" should be answered *before* the structural question, because it determines whether rollup and Delivery Plan rendering (which only A/B provide) are load-bearing or decorative.

**(e) Reversibility asymmetry.** Parent links can be rewritten in bulk via REST at any time. Nobody costed the actual cost of being wrong. C treats the parent slot as "hardest to undo" (§1) — it is not; it is one field. That weakens C's central "premature commitment" framing more than C realises.

## 7. Assessment

**I do not think the question is underdetermined — but I think it is being asked in the wrong order, and I decline to endorse a position before two cheap measurements are run.** That is a reasoned conclusion, and here is what it rests on.

On the merits as argued: **B is the strongest paper**, and A is the weakest. B wins the crux (§2), wins the sharpest single exchange (rank-overloading, §3), has the best falsifier set, and is the most candid about its own limits. A's central legibility argument is factually refuted by the team's own evidence file, and three of its five falsifiers cannot fail before commitment. C is the most interesting paper and the least conclusive: its migration argument is the best unanswered point in the debate, but its own reasoning implies the type may not need to exist, which it never confronts.

But "B is the best-argued" is not the same as "B is right," and the gap is empirical, not rhetorical. Every position is a bet on a distribution — how many Execution Plan children have a competing natural parent, and how much genuine concurrency exists — and **that distribution is sitting on disk in 32 work items right now, unmeasured.** All three advocates knew this: B says "run 1 and 3 before committing"; C's first falsifier is the same query. Choosing now, with the measurement one query away, would be the team's own named defect — asserting a green without running the check.

**My recommendation is an experiment, with a pre-committed decision rule:**

1. Run falsifier #1 (edge count) and #2 (child-type classification) against the 32 Maps. Cost: under two hours.
2. Then #3 (competing-parent count) on the subset that #2 shows to be executable work.
3. Pre-commit: if **>60% of Map children are planning work** → C, and seriously consider option (b), that this is a rename and not a new type. If children are mostly executable **and** competing parents are rare (<20%) → A, on simplicity, with the ordering-hazard question (§6a) resolved first. If children are mostly executable **and** competing parents are common → B.

Separately and immediately, resolve §6(a): confirm empirically whether Execution Plan and its intended children can sit on different backlog levels. If they cannot, A is not implementable as written and the debate has two options, not three.

**Confidence.** High (≈85%) that the two cheap measurements will discriminate decisively between C and (A,B) — the planning-vs-executable split is a fact about existing data, not a matter of taste. Moderate (≈60%) that they will discriminate between A and B, since edge count could be low for reasons of *habit* rather than *need*, and a low count would be genuinely ambiguous. Lower (≈45%) on my own prior, which — for what it is worth and stated so it can be checked against the data — is that the measurements will show Map children are predominantly planning work, that C's migration argument will hold, and that the team will discover the Execution Plan they are designing is the Map they already have. If that is what the data says, the right output of this debate is not a winner but a rename.
