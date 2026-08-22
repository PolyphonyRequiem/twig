# MAP: What twig owes a customer process

**Chartered** 2026-08-22, from the twig-profile bring-up session.
**Status** — open. This map has a destination and a frontier; it does not have all its tickets yet.

---

## Destination

twig owns the generic systems for driving an ADO work-item process. **Hyperbright is customer
zero, not the product.** A design question is answered correctly when the answer would still be
right for a customer whose process we have never seen.

The way is clear when: a customer can declare their model to twig, twig can enforce and query it,
and none of twig's own behaviour assumes Hyperbright's shape.

---

## Decisions so far

Each of these was settled with evidence during the chartering session. They are gists — the
detail lives in the linked item.

1. **Work is tracked on the ADO board, GitHub is public record only.** `docs/agents/issue-tracker.md`.
   A tool inferring the tracker from `git remote -v` guesses wrong.

2. **ADO cannot enforce type-level parent/child policy.** All six avenues checked against primary
   sources — rules operate on fields only; no rule can see a link or a parent; `onSaved` is past
   tense so there is no synchronous veto; backlog levels govern display, not link legality.
   Evidence: `ado-parent-child-enforcement.md`. Consequence: **#615**.

3. **twig INVENTS its hierarchy constraint.** `BacklogHierarchyService.InferParentChildMap` assigns
   every type at level *i+1* as a valid child of every type at level *i*; `SeedFactory` then
   enforces that inference. ADO never sends a child-type list. This made a legal ADO shape
   unreachable through `twig seed`. Consequence: **#615**.

4. **No new backlog level can be inserted mid-hierarchy.** *"You can't insert a new custom backlog
   level within the existing set of defined backlogs."* Only `+ New top level portfolio backlog`,
   max 5. Evidence: `ado-backlog-levels.md`.

5. **Multi-level process inheritance does not exist.** Proven live with a control:
   `parentProcessTypeId` = a custom process → HTTP 500 `VS402372`; byte-identical request with a
   system parent → HTTP 201. Evidence: `ado-process-inheritance-probe.md`.

6. **One process, several teams — decided.** Types/fields/states/rules are process-level; backlog
   level *visibility*, area paths, iteration paths and board columns are team-level. Human Devs
   and AI Agents become teams differentiated by area path. Evidence: `ado-process-inheritance.md`.
   🔴 Both teams necessarily see the same TYPE SET — ADO offers no way around this.

7. **Responsibility is item-atomic.** Every work item has its own state, assignee and closure; a
   parent can be Done with open children; there is no cascade and no inherited accountability.
   Four advocates with opposed positions converged on this independently. The Hierarchy edge
   carries composition and rollup — never authority.

8. **Artifacts are level-less by design.** `Decision`, `Finding`, `Idea` — immutable point-in-time
   records, no children, no work lifecycle. *A backlog is a list of what to do next; an artifact
   never belongs on one.* Consequence: they never appear on Delivery Plans.

9. **Rollup cannot drift.** Computed by Analytics with no writable storage. The documented drift
   modes are lag and scope-exclusion only. This is the one aggregation in ADO that is not a mirror.

10. **Delivery Plans render dependency lines natively.** Corrects a round-one error that had been
    load-bearing for two positions.

---

## The frontier

Open questions, roughly in dependency order. Each should become a ticket.

### ~~F1 — Do stakeholders steer by Delivery Plans?~~ ✅ ANSWERED 2026-08-22
*"It's currently more of a report, but steering would be valuable."* Neither yes nor no —
**not today, but do not foreclose it.** That answer is what decided F2.

### ~~F2 — Execution Plan: what do its children mean?~~ ✅ DECIDED — **#633**
**Position B: parent by composition, link by sequence.** A Change parents to its *Feature*; the
Execution Plan reaches it by a Predecessor/Successor edge. The Plan's own children are only what
it produces — Specs, Decisions, Commitments.

F1's answer eliminated the other three on one criterion: which renders honestly *and* leaves the
steering door open. D renders nothing and could never become steerable. C shows planning progress
and nothing about execution — worse than blank, because it looks informative. A renders richest
today but corrupts every Feature rollup bar, a cost that stays invisible until someone starts
steering, i.e. it is deferred onto the exact future being preserved.

Carried forward: **if Hyperbright is customer zero, this rule is what Hyperbright DOES, not what
twig should ANSWER.** #615 is where it becomes declarable per-customer.

### F3 — Commitment needs a closure precondition ADO cannot supply
A Commitment can close while the work it promises is open. No position fixes this; the type is
under-designed independently of the children question.

### F4 — Is `Spec` an artifact or work?
It has real states (drafted, reviewed, revised) which is work-shaped, but Plans are said to
*deliver* specs, which is artifact-shaped. The model currently has it at Work.

### F5 — Does `Feature` join the Map category?
Execution Plan and Investigation are two variants of one shape. Feature was described as
"today's construction item" — if it moves up, what is the construction type beneath it? `Change`
was proposed and never settled.

### F6 — Retype the 32 existing Maps
Decided in principle ("we will retype"), not executed. Each existing Map becomes an Execution Plan
or an Investigation. Blocked on F2/F5.

### F7 — Area path scheme for the team split
Now load-bearing, since area path is what separates Human Devs from AI Agents.
Caution from the limits page: never assign the same area path to two teams.

### F8 — Verify swimlanes and working days are team-level
Flagged as *believed* but uncited in the research. Cheap to confirm in the UI.

---

## Not yet specified — fog

- What the declared-policy language looks like (#615). Whether the rule grammar and the query
  grammar (#619) are one language over one graph or two — flagged as worth catching early.
- Whether `twig process <type>` output is rich enough to drive a per-type skill generically, or
  whether each type needs its own skill.
- What `/hyperbright-work org project item #N` actually dispatches on, and how it loads the right
  skills for a type.
- Whether an `Idea` is promoted into a Plan/Feature or stays an inbox item forever.

---

## Out of scope

- **The Human Oversight board.** Explicitly out: *"every organization using twig would have their
  own approach."*
- **Retiring `workspace` as a word.** Owned by the sibling map, `map.md`, in this same directory.
- Anything requiring ADO to enforce a model. Settled as impossible — see Decision 2.

---

## Filed on the board this session

| Item | Type | What |
|---|---|---|
| #615 | Idea | twig needs a declared policy engine, not inferred hierarchy rules |
| #616 | Bug | `twig note` rejects a bare positional and names the first word |
| #617 | Bug | `twig note`/`new` lack `--file`/`--stdin` that `update` has |
| #618 | Bug | `twig show --output json` omits comments; a note write cannot be verified in twig |
| #619 | Idea | twig should own parameterised multi-hop queries |
| #620 | Bug | `twig link` cannot create a Related link — **priority**, in flight |

#615 ↔ #619 are Related-linked with a comment explaining why: ADO declines to *enforce* (#615) and
declines to *compose* (#619); both land in twig for the same reason.

---

## Evidence in this directory

| File | What it settles |
|---|---|
| `ado-parent-child-enforcement.md` | All six enforcement avenues, closed with citations |
| `ado-backlog-levels.md` | What backlog levels can and cannot do |
| `ado-audience-views.md` | Delivery Plans, rollup, audience-targeted views |
| `ado-process-inheritance.md` | Process/team split; what is shared vs per-team |
| `ado-process-inheritance-probe.md` | Live proof, with control, that multi-level inheritance fails |
| `ado-workflow-testing.md` | Testing ADO workflows; delete vs destroy; throttling returns 200 |
| `debate-*.md`, `r2-debate-*.md` | Seven position papers and two reviews on F2 |
| `map.md` | The sibling map: bench/workspace unification |
