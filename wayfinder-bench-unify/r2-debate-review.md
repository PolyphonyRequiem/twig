# Round-two review: four positions on what an Execution Plan's children mean

Reviewer's stance: no assigned position. I have not weighted a paper for being moderate, cautious, new, or culturally fluent. Several culture appeals in these papers are rhetoric wearing a mechanism's clothes, and I say which.

---

## 1. FACT-CHECK

**No paper repeats the round-one dependency-rendering error.** All four state the corrected fact. A withdraws it explicitly ("I withdraw it"), B calls the old objection "false", C calls it "per the corrected fact", D "per the correction". Clean.

**Two papers smuggle in historical experience despite the ban.**
- A §4: *"The team has already hit this. I will not argue it away."*
- B §4: *"The team hit this hazard once; this rule makes it unreachable rather than merely unlikely."*

Both are appeals to the same-level-parenting incident that the historical Map items produced. The hazard itself is an established fact and needs no history; invoking "we hit it" adds rhetorical weight from a banned source. Minor, but both should have argued from the documented hazard alone. C and D are clean.

**B contains the one substantive factual overstatement.** §3: *"The Execution Plan renders as a card on the Initiatives row. Its Specs, Decisions and Commitments render beneath it as its children."* Two errors. (a) Decisions are level-less Artifacts and **never render at all** — B corrects itself two sentences later, which makes the first sentence careless rather than dishonest, but the sentence as written is wrong. (b) Delivery Plans do not nest children under a parent card. Rows are per team × backlog level; a Spec renders on the Work row on its *own* date span. The only documented parent-abstraction affordances are the `Parent` card field (shows the parent's Title) and the collapsed-row summary. "Render beneath it as its children" describes a backlog tree view, not a Delivery Plan.

**Unsupported-but-plausible inference presented as fact, all papers:** every paper omits the hard prerequisite that items need **Iteration Path or Start/End dates** to appear on a plan at all. This is not a footnote — it silently changes every rendering claim in §4 below. B's "in date order, across teams" assumes dates that nobody has said will exist on Specs, Commitments or Changes.

**D §41:** *"A shared query… is visible in the ADO web UI, is shareable by URL."* True. But D implies this substitutes for a Delivery Plan row. It does not: a Delivery Plan takes teams + backlog levels + field criteria, **not a shared query**. D's rescue is weaker than D thinks.

**A §1:** *"Position A is the only position that gets a non-drifting progress number for a plan."* Overstated — B gets rollup over the Plan's own children. A means rollup over *sequenced* work; as written it is a stronger claim than the evidence carries.

**C §29:** *"Microsoft explicitly does not recommend creating work item types for reporting purposes."* The evidence says "No Microsoft documentation reviewed here recommends creating extra work item types for reporting or summary purposes." Absence of a recommendation is not an explicit non-recommendation. C upgrades an absence-of-evidence finding into a positive Microsoft position. A does the same in §35 ("Microsoft explicitly advises against those"). Both are wrong in the same direction.

---

## 2. 🔴 THE OWNERSHIP QUESTION

**All four reach the identical conclusion: responsibility is item-atomic.** Not roughly similar — identical, and by identical reasoning: own State, own AssignedTo, own closure; parent can be Done with open children; no gate, no cascade. A: "responsibility is item-atomic." B: "responsibility is item-atomic." C: "Responsibility is item-atomic." D: "Responsibility is item-atomic."

**Is the agreement well-founded, or a shared unexamined assumption?** It is well-founded *as a claim about ADO's mechanism* — the established facts state it flatly and no paper stretches beyond them. But all four treat "ADO does not implement custody" as equivalent to "the parent slot carries no authority," and that inference is untested by any of them. A social fact can ride on an edge that the platform does not enforce. Nobody asked whether people read the tree as authority even though the machine does not. C comes closest ("parenting is not theft") but drops it.

**My answer.** Yes, responsibility is item-atomic in the machine. **No, that does not dissolve the one-parent crux** — and three of the four papers half-see why.

The crux was never *only* custody. Strip custody out and the parent slot still carries two contested goods: **composition** (a semantic claim about what a thing is part of) and **rollup** (the only non-drifting aggregate the platform computes). Both are scarce, because there is one slot. C states this correctly and it is the best sentence in the round: *"the tree is a scarce resource."* The fight does not end when custody leaves; it just becomes a fight about arithmetic and meaning rather than authority.

What item-atomicity *does* dissolve is the **moral** version of the argument — "if the Plan doesn't parent the work, nobody owns it" is now dead, and so is "the Plan is stealing the Feature's work." Both A and B kill that objection and both are right to. But A then over-collects: it treats the dissolution as a licence to take the slot cheaply, when the cost it must actually pay — degraded Feature rollup — is untouched by the ownership finding. A even concedes this ("Features are design records; they should not be scored by rollup in the first place"), which is an assertion, not an argument.

So: item-atomicity is the round's most valuable finding and it *narrows* the crux without dissolving it. The residue is real.

---

## 3. THE COMMITMENT HARD CASE

A does not address Commitment as a hard case at all — a gap, given the model lists it as a Plan output. That is A's largest omission.

C treats it only as a falsifier ("if fulfilling a Commitment requires rollup over its Changes…").

**B and D handle it, and B handles it best.** B's answer: item-atomic responsibility gives the Commitment its own assignee and closure — the promise-keeper is real — but gives **no guard**: the Commitment can close with all three blocking Changes open. B then refuses to inflate what dependency edges buy: *"Closing a Commitment over open predecessors is at least a visible lie. That is weaker than a guard and I will not call it one."* That is the round's most disciplined sentence. D reaches nearly the same place but calls it a feature ("Largely yes — and that is a feature") and does not name the missing guard as clearly.

**My view:** a Commitment is *not* just a work item with an assignee. It needs something the model does not have — a **closure precondition**: "may not enter Fulfilled while any Predecessor is open." ADO cannot supply it (no relational rule conditions, no pre-save veto, rules bypassable). Hierarchy cannot supply it either. So the gap is orthogonal to this debate, which is exactly B's point and is correct. But it means the Commitment type as specified is under-designed regardless of which position wins, and no paper says so plainly.

---

## 4. 🔴 DELIVERY PLANS — WHAT EACH POSITION ACTUALLY RENDERS

Ground rules from `ado-audience-views.md`: rows are team × backlog level; **only Initiatives (30), Work (20), Tasks (10) are selectable**; **Decision/Finding/Idea never render**; items without Iteration Path or Start/End **do not appear**; dependency lines render for Predecessor/Successor; rollup progress bars are available on Feature/Epic/portfolio cards and climb Hierarchy only; there is **no virtual/summary card**; up to 1,500 plans, each with its own teams, levels, field criteria, card fields, styles, markers.

**A — hierarchy is the plan.** Plan card on the Initiatives row with a **live rollup progress bar** over its Changes/Specs/Validations, plus dependency lines between them on the Work row. This is the richest render available: leadership sees one card, one percentage, and the sequence. What they miss: Decisions (level-less), and — importantly — **Feature cards whose progress bars are now wrong**, because A moved those Changes out from under the Feature. A's plan is legible; A's *other* plans are corrupted. That trade is invisible on the Plan's own board and highly visible on the Feature portfolio board a different audience uses. A does disclose it.

**B — parent composition, link sequence.** Plan card on Initiatives with a rollup bar over its Specs and Commitments only — i.e. a percentage of *paperwork*, not of delivery. Follow-up Changes render under Features with correct Feature rollup, and dependency lines connect Plan-adjacent items to them. Leadership sees the sequence and the frontier, and sees a Plan card whose progress number is real but answers a question nobody asked. B knows this and argues the frontier is the better signal — a defensible claim, unproven. Correct on Feature integrity; wrong on nesting (see §1).

**C — plan contains planning work.** Plan card on Initiatives with rollup over Grilling/Research/Prototype/Spec. Renders *only the planning phase*. Executed work is on the Work row with no visible tie to the Plan except any Predecessor lines someone authored, and the plan's actual scope lives in a Bench that renders nowhere. Leadership sees "planning 80% done" and cannot see whether the plan is being executed. This is the weakest leadership render, and C's own cost list concedes it.

**D — selector.** **No card. No row. Nothing.** Individual members render under their real parents with correct Feature rollup and dependency lines, but there is no object representing the Plan. D's half-measure — a childless Execution Plan work item with dates and Predecessor edges — does render a card and a bar on the Initiatives row, but the bar is empty (no children) and the card cannot be clicked through to membership.

**Ranking on Delivery Plan fitness alone: A > B > C > D.**

**Should it be decisive?** No — but it is not secondary either. Delivery Plans are the only audience-facing surface named in the whole brief, and the evidence is emphatic that ADO's *only* documented abstraction mechanism is hierarchy + rollup. That makes Delivery Plan fitness a hard constraint on any position that claims leadership legibility. It is decisive **conditionally**: it should decide the question if and only if the team can state that stakeholders steer by Delivery Plans. D names this exactly right as its own falsifier. Nobody has answered it. Ranking a position highly on rendering while the underlying composition claim is false would be optimising the report over the model — the team's own "false green" in structural form. So: necessary condition, not sufficient reason.

---

## 5. ARGUMENT QUALITY

**A.** Strongest: the rollup-is-non-drifting argument, which is the sharpest use of the facts in the round, and the honest reframing that item-atomicity *shrinks the prize*. Weakest: §3's "Features are design records; they should not be scored by rollup" — pure assertion, and it contradicts the proposed model's own definition of Feature as bearing changes and evidence. Overreach: "the only position that gets a non-drifting progress number."

**B.** Strongest: the Commitment paragraph, and the "two verbs want different edges" reading of the proposal. Weakest: the claim that a Plan's progress question is a frontier, not a sum — asserted, and B lists its own falsifier for it without testing it. Overreach: the Delivery Plan nesting description.

**C.** Strongest: "the tree is a scarce resource," and reading the type definitions as the code. Weakest: §3, where C concedes Execution Plan and Investigation may be one type with a mode field — that concession, if taken, largely dissolves C's own subject matter.

**D.** Strongest: "a selector-based Plan is honest about owning nothing." Weakest: the visibility failure, which D states honestly and then cannot repair; the shared-query rescue does not restore a Delivery Plan row.

**Objections raised and merely restated, not defeated:** A's cost #5 (no dynamic membership) is named as "the closest thing to a principled objection to this position" and then left standing — a candid restatement, not a defeat. B's "unlinked Plan degrades to a list, and nothing detects that" — same. D's tooling cost — same.

**The A and C concessions.** A's is **honest strength**: A conceded the dependency-rendering error, conceded item-atomicity kills its own custody claim, and still had a live argument (rollup + one name) afterwards. The position survives its concession. C's is **partial collapse**: C explicitly restates its claim as "smaller than I made in round one," abandons custody, and then concedes its type may not deserve to exist. What remains — slot allocation and "rollup over scheduled work measures the wrong thing" — is real but is now a subset of B's argument with a Bench bolted on. C is no longer a distinct position; it is B minus the Plan's dependency spine.

---

## 6. HIDDEN AGREEMENT AND THE REAL AXIS

They disagree far less than the framing suggests. **All four agree** that: responsibility is item-atomic; ADO has no accountability primitive under any model; Predecessor/Successor is where sequence lives; Decisions/Findings will never appear on a Delivery Plan; hierarchy and dependency are orthogonal; and no position gets a guard that can actually fail without new twig code.

That last one deserves emphasis. **The real axis is not "does the Plan own its children" — item-atomicity retired that. It is "what does the Plan's single parent slot buy, and is it worth the Feature's rollup?"** On that axis: A says yes, take it. B, C and D all say no — leave the slot with the Feature. So it is **1 v 3**, not a four-way split, and B/C/D differ only in what they put in the vacated place: B a dependency spine, C a Bench plus planning children, D a selector and no work item.

**Do all four depend on unbuilt tooling?** Effectively yes, and none of them fully owns it. D is explicit (policy engine, multi-hop queries). C requires Bench selectors plus multi-hop it admits is unbuilt. B requires something to detect unlinked Plans — "twig's rule engine is an unbuilt Idea." A claims to need nothing, and is the only position that is true for; but A's cost #5 concedes that its lack of dynamic membership is the principled objection, which is a dependency on tooling in disguise. **A is genuinely the only position deliverable today.** That is a real and underweighted asymmetry.

---

## 7. WHAT THEY ALL MISSED

1. **Dates.** Nothing renders on a Delivery Plan without Iteration Path or Start/End. Every rendering argument in this round assumes them. Who sets them on a Spec? On a Commitment? Unanswered by all four.
2. **Multiple plans, cheaply.** 1,500 Delivery Plans per project, each with its own levels and field criteria. The evidence's actual recommendation for audience targeting is *more views over the same work*, not a different tree. Three positions argue over one tree shape while the documented answer to "leadership sees the right granularity" is a second plan with a field criterion. Nobody noticed.
3. **Field criteria as a poor man's selector.** A Delivery Plan's field criteria (tag, area path, `Work Item Type <> X`) is a stored rule that renders natively in the ADO UI. That is 70% of D's proposal with D's fatal visibility gap closed — better than D's shared-query fallback, and D missed it.
4. **Rules are bypassable and there is no pre-save veto.** Every position that hopes a future twig policy engine will enforce shape must accept it can only detect after the fact. C names this; nobody draws the conclusion that *all* structural guarantees here are post-hoc detectors, which flattens much of the "guard that can actually fail" rhetoric across all four papers.
5. **The prior question:** *does the team need Initiative-level sequencing?* A cannot supply it (ordering hazard), B dodges it, C sidesteps it, D is indifferent. This must be answered before the children question, because a "yes" eliminates A outright.
6. **Nobody costed reparenting.** If a Change moves from Feature to Plan, its Feature rollup history changes retroactively in Analytics. No paper mentions it.

---

## 8. MY ASSESSMENT

**Recommendation: B, with two amendments — and adopt it only after answering one prior question.**

Reasoning:

- Item-atomicity is established. It removes the custody argument, which was the emotional core of A. What remains for A is rollup, and A pays for it with Feature rollup — a straight trade of one audience's correct number for another's. A's defence of that trade ("Features shouldn't be scored by rollup") contradicts the model's own definition of Feature. That is the decisive weakness, and it is a modelling failure, not a rendering one.
- C has, by its own words, shrunk to B-minus-the-spine, and its Delivery Plan render is the worst of the four while its distinctive contribution (Bench scope) is available to B as an addition rather than a replacement.
- D is correct about honesty and wrong about visibility, and its own falsifier #1 is very likely satisfied. But D's core insight — membership is a property of the set, and rules beat copies — should be kept.
- B is the only position whose composition claim matches the proposed model's own type definitions (Feature "bears" the changes; the Plan "produces" Specs/Decisions/Commitments) while preserving a real Delivery Plan render.

**Amendment 1 (from D):** define plan *scope* as a saved rule — an ADO **Delivery Plan field criterion** (tag or area path), not a Bench, so it renders in the web UI today and needs no unbuilt tooling. Bench becomes the local power tool over the same rule, not the source of truth.

**Amendment 2 (from B's own falsifier):** write down, before adopting, what an Execution Plan must *report*. If the answer is a percentage over follow-up work, B is disqualified and A wins by default.

**What would settle it** (not the 32 items):
1. Build one Delivery Plan per position over a small set of *newly created* items — 1 Plan, 2 Features, 6 Changes, 2 Specs, 1 Commitment, with dates — and show all four renders to the actual leadership audience. Cost: hours. This is direct evidence, and it answers the decisive question (are Delivery Plans load-bearing?) by observing whether anyone steers by them.
2. Answer: does any Execution Plan need to sequence a Feature or another Plan? A written yes/no from the owner. A "yes" kills A immediately.
3. Answer: is a Spec genuinely part of both a Feature and a Plan? A's own sharpest falsifier, and the model gestures at it.

**Confidence: moderate (≈65%) for B-with-amendments; high (≈85%) that the real axis is 1-v-3 and that the choice reduces to whether the Plan's rollup is worth the Feature's.** Low confidence that any paper has correctly guessed whether Delivery Plans are load-bearing for this team — which is why item 1 above is worth doing before the decision rather than after.

One caution I will state plainly: A is the only position implementable with zero new code, and this team's stated value is evidence over assertion. If the amendments to B slip and the twig side stays unbuilt, B degrades into "Plan with three paperwork children and some links nobody authored," which is worse than A. Adopt B only with Amendment 1 actually in place.
