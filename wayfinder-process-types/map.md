# twig's ADO process: types, levels, fields, layouts and conventions — Wayfinder map

## Destination

The twig ADO process is settled for Hyperbright: which work item types exist, what each is for,
which backlog level each sits at, which fields and form layouts each carries, the conventions
governing them, and how each kind of team member uses twig to do their work.

**One layer, not two.** The destination is our own `ProcessConfiguration`. There is no separate
generic-vocabulary deliverable to write first — that reading was overturned by ticket
[0001](tickets/0001-generic-layer-vs-instance.md).

**The generic pressure is a GATE on every ruling, not a layer before them.** Every ruling on
this map records a verdict against the governing rule:

> *Would this still be right for a customer whose process we have never seen?*

- **Acceptable** — only the chosen *value* is ours. "Our pull-requestable type is named
  `Change`" is ours; a customer picking `Work Package` is served by the same mechanism. This is
  customer zero working as intended.
- **Defect** — the *mechanism* is ours. twig could not express another customer's choice at
  all. The ruling names the missing mechanism.

🔴 **A defect verdict does NOT block the ruling.** The gate is a ledger, not a veto. A veto
would stall the map behind ADO #615, which is explicitly out of scope — so defects are
recorded, the ruling stands, and the collected lines become #615's requirements list.

⚠️ **Ticket [0003](tickets/0003-team-types-and-experiences.md) is evidence, not a gated
ruling.** A description of *our* team is not the kind of claim that question can judge. Its
output is a demand-side test — *which role is worse off if this type does not exist?* —
applied by 0004, 0005 and 0011.

The map ends at a build-ready ruling set. **Creating the types, PATCHing the process and
writing the docs are the build that follows** — this map decides, it does not mutate the board.

## Notes

- **Domain vocabulary:** `CONTEXT.md` is authoritative for names. `AGENTS.md` §*Where work is
  tracked* governs the repo/board split and outranks any inference from a type list.
- **Governing rule (from the brief, and it binds every ticket):** *twig owns the generic systems
  for driving an ADO process; the board's process is **customer zero, not the product**.* A
  design is right when it would still be right for a customer whose process we have never seen.
- **This map is markdown, not a board item.** Per `docs/agents/issue-tracker.md` (commit
  `054e780b`): map is `map.md`, tickets are `tickets/NNNN-slug.md` with frontmatter, resolution
  is an `## Answer` in the ticket plus one line in *Decisions so far*. Scheduling a ruling means
  creating the ADO item(s), adding `tracked_in: [<ids>]`, naming the ticket in each item's
  description, and verifying with `tools/check-tracking.sh`.
- **Skills:** `wayfinder` (governing), `grilling` + `domain-modeling` (call both on every
  grilling ticket — this is a naming and taxonomy problem), `twig:testing-ado-workflows`,
  `twig:twig-benches` if Bench vocabulary is touched.
- **Never trust a write's own success message.** This board has produced a `copyFrom` probe
  returning `201 Created` that silently did nothing. Verify every process read/mutation with an
  independent GET. Process behaviour edits are **PUT**; PATCH returns 405.
- **Environment:** `export HOME=/home/polyphonyrequiem` in every shell command, or `az`/`gh`/
  `twig` fail with *misleading auth errors*. Run `twig sync` in a fresh worktree before the
  first `twig state`/`twig process` call. Builds (unlikely to be needed) want
  `DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5`.
- **Sandbox for live checks:** `PolyphonyRequiem/Sandbox` carries the same Hyperbright process
  and is safe to write. Never probe process mutations against the live `Twig` board.
- **Research already done — do not redo.** `ado-backlog-levels.md`,
  `ado-parent-child-enforcement.md`, `ado-process-inheritance.md` + `-probe.md`,
  `ado-audience-views.md`, all in
  `/home/polyphonyrequiem/repos/twig-bench-unify/wayfinder-bench-unify/`, all primary-source.
  Key findings that bind this map: **ADO cannot enforce type-level parent/child policy at all**
  (six avenues closed) and backlog level governs *display*, not link legality — which is why
  **ADO #615** ("twig needs a declared policy engine, not inferred hierarchy rules") exists.
  **Multi-level process inheritance does not exist** (proven live with a control: custom parent
  → `HTTP 500 VS402372`, byte-identical request with a system parent → `201`). One process,
  many teams. Do not re-derive these.
- One ticket per session.

## Measured starting state

Read live against process `ba4e268d-7d67-43bd-8065-df7ab52fba0c` (Hyperbright, inherited) on
2026-08-22. **Measured, not assumed** — but re-measure rather than quoting this, as it drifts.

**Backlog levels** (renamed; the renames landed):

```
rank 30  Initiatives  Microsoft.VSTS.Basic.EpicBacklogBehavior  inherited
rank 20  Work         System.RequirementBacklogBehavior         inherited
rank 10  Tasks        System.TaskBacklogBehavior                system
```

🔴 The behaviour **reference names never change**. The Initiatives level is
`...EpicBacklogBehavior` in the API forever. Anyone reading the API and expecting "Initiatives"
gets a false green. Recorded, not a defect. (`Ordered` and `Portfolio` also exist at rank 0.)

**Types and levels, as they actually are:**

```
Initiatives : Epic, Map
Work        : Bug, Feature, Grilling, Prototype, Research, Spec, Wayfinder Task
Tasks       : Task
(no level)  : Decision, Idea            <- deliberate: artifacts, not backlog items
(no level)  : Issue, Test Case/Plan/Suite, and the four hidden Request/Response types
```

🔴 **Say which roster you mean — three routes return three different type lists.** Measured
2026-08-22: the **process roster** (`_apis/work/processes/{id}/workItemTypes`) returns **16**;
the **project WIT** roster (`_apis/wit/workitemtypes`) returns **22**, because it carries the
hidden helpers; and `_apis/wit/workitemtypecategories` is a third view again. The 16 above is
the process roster. Quoting a count without naming its route is how this map's first draft got
the Request/Response types wrong.

⚠️ **Category membership does not follow the type name.** `Microsoft.BugCategory` contains
**`Issue`**, not `Bug` — `Bug` sits in `Microsoft.RequirementCategory`, and `Issue` is itself
hidden. Verified live. Never infer a category from a name.

`Microsoft.HiddenCategory` holds 10 types here — `Issue`, `Code Review Request`,
`Code Review Response`, `Shared Steps`, `Shared Parameter`, `Test Suite`, `Test Plan`,
`Test Case`, `Feedback Response`, `Feedback Request`. Microsoft defines the category as *"the
set of WITs that you do not want users to create manually"*: they are **tooling back ends, not
namable vocabulary**. Already filed against twig, and **not this map's work**: **ADO #656** (no
twig surface reports category membership) and **ADO #657** (`twig process` lists 21 types, 10
hidden, unmarked — an omp session is working it in `/home/polyphonyrequiem/repos/twig-657`;
**do not touch that card or worktree**).

16 types total. Reference names are `Hyperbright.<Name>` for custom/inherited types and
`Microsoft.VSTS.WorkItemTypes.*` for the stock test types — ⚠️ **read the real `referenceName`
off `GET .../workitemtypes` before querying a type**; assuming the `Hyperbright.` form for a
stock type returns `VS402805: Cannot find work item type`.

`Feature`, `Bug`, `Prototype`, `Decision`, `Spec`, `Map` all measured the same three states:
`To do (Proposed) / Doing (InProgress) / Done (Completed)`.

**The gap — four types agreed but NEVER CREATED.** A prior session
(@session:twig/20260821_175214_910f0c) agreed **Change**, **Validation** and **Documentation**
as Work-level types and **Finding** as a level-less artifact alongside Decision and Idea.
**None of the four exists** — confirmed live this session against the 16-type list. The level
renames landed; the type creates did not. Found when an attempt to create implementation cards
of type `Change` for AB#644's five-unit handoff failed.

**The custom field × type matrix, measured live.** Every non-stock type carries custom fields;
none has zero:

```
Custom.ChangelogSummary            Feature, Bug
Custom.ClosingStatement            Map, Decision, Idea, Epic
Custom.DecisionStanding            Decision
Custom.FalsificationCriteria       Issue, Feature, Spec, Bug
Custom.IdeaOutcome                 Idea
Custom.Maturity                    Issue, Idea, Epic, Feature, Spec, Bug
Custom.MaturityNote                Wayfinder Task, Prototype, Research, Grilling
Custom.PriorityBand                Issue, Idea, Epic, Feature, Spec, Bug
Custom.SupersededBy                Decision
Custom.TerminalOutcome             Wayfinder Task, Task, Feature, Bug
Custom.VerificationMode            Issue, Feature, Spec, Bug
Custom.WayfinderAnswer             Wayfinder Task, Prototype, Research, Grilling
Custom.WayfinderDecisionMaturity   Wayfinder Task, Prototype, Research, Grilling
Custom.WayfinderDecisionsSoFar     Map
Custom.WayfinderDestination        Map
Custom.WayfinderExecutionMode      Wayfinder Task, Prototype, Task, Feature, Research, Bug
```

Two shapes fall straight out of that matrix and are ticketed rather than assumed:

1. **A clean two-cluster split.** `Maturity`/`PriorityBand`/`FalsificationCriteria`/
   `VerificationMode` sit on the *schedulable* types; `MaturityNote`/`WayfinderAnswer`/
   `WayfinderDecisionMaturity` sit on exactly the four *wayfinder* types. The clusters are
   almost disjoint — `WayfinderExecutionMode` and `TerminalOutcome` are the only crossers.
   That is either the type taxonomy already asserting itself in the fields, or drift. See
   ticket [0007](tickets/0007-fields-and-gates.md).
2. 🔴 **Three near-homonyms — `Maturity`, `MaturityNote`, `WayfinderDecisionMaturity` — with
   no type carrying more than one.** Three names for one concept split across two clusters is
   the shape of accreted drift, and it is exactly the kind of thing a new type inherits by
   copying its nearest neighbour. Also in ticket 0007.

**The Bug→Done close gate** is `Custom.FalsificationCriteria` (html) **and**
`Custom.VerificationMode` (string). The mechanism is pinned by ticket 0009's memo: **two custom
process rules**, `conditionType: "when"` on `System.State` = `Done` with
`actionType: "makeRequired"`. It is a **state** rule, not a transition restriction — every
transition is legal.

⚠️ **CORRECTION to the brief, and to this map's own first draft.** `Custom.VerificationMode`
**does** have enforced `allowedValues` — a five-item picklist
(`Not verified yet`, `Developer attested`, `Owner attested`, `Validation accepted`,
`Validation proven to catch failure`). The *process* API returns a stub with no values, which is
what produced the "free text" reading; the **project WIT** endpoint with `$expand=all` shows the
picklist. Independently re-verified this session against
`.../Twig/_apis/wit/workitemtypes/Bug/fields/Custom.VerificationMode?$expand=all`. **Read the
project WIT endpoint, not the process endpoint, before concluding a field is unconstrained.**

🔴 **But the gate is advisory, not inviolable.** Ticket 0009 measured `bypassRules=true` closing
a Bug with **both gate fields empty** — HTTP 200, confirmed by GET. Process rules do not survive
a privileged automation identity, and twig may be exactly such an identity. **Type-disabling was
the only mechanism found that `bypassRules` cannot walk through.** Any design in 0007 or 0008
that treats a `makeRequired` rule as a hard gate is wrong. Which types need which gates is
ticket 0007.

## The frontier as charted

Open tickets are found by reading `tickets/*.md` frontmatter (`status`, `blocked_by`), not from
a list here — this snapshot is the charting session's, and it goes stale on the first
resolution. Verified at charting: **11 tickets, no dangling `blocked_by` refs, no cycles.**

Takeable now: **0002** (the red UNDECIDED) and **0003** (team experiences). **0001 is closed** —
the destination is settled, one layer not two, with the customer-zero rule as a per-ruling gate.
**0009 is closed** — its memo is `ado-process-capabilities.md`, and 0007 and 0010 are
correspondingly one blocker lighter.

**0002 blocks four tickets — more than any other — which is the graph agreeing with the brief
that it is the highest-leverage question on the map.** 0004 blocks three more behind it.

## Decisions so far

<!-- one line per closed ticket; the detail lives in the ticket, never restated here -->

- **[Is the destination the generic layer, the Hyperbright instance, or both in order?](tickets/0001-generic-layer-vs-instance.md)**
  (grilling, closed 2026-08-22): **the instance — one layer, not two.** The brief's
  customer-zero rule is an *acceptance test applied to each ruling*, not a generic layer to
  build first. Defect = the *mechanism* is ours; acceptable = only the *value* is ours. 🔴 The
  gate is a **ledger, not a veto** — a defect never blocks a ruling, it emits a line for ADO
  #615. Ticket 0003 is **evidence, not a gated ruling**. Nine of ten tickets were already
  instance questions and none was rescoped.

- **[What can an ADO inherited process actually express?](tickets/0009-ado-process-capabilities.md)**
  (research, closed 2026-08-22, memo `ado-process-capabilities.md`, 552 lines): a type can be
  created with **no parent and no backlog level**, but **cannot sit at two levels**
  (`400 VS403194`), and `inheritsFrom` a custom type is refused. The Bug→Done gate is **two
  `makeRequired` state rules, not a transition restriction**, and 🔴 **`bypassRules=true` closes
  a Bug with both gate fields empty** — type-disabling is the only block that survives it.
  `Custom.VerificationMode` **does** have an enforced five-item picklist; the process API returns
  a stub, the project WIT endpoint shows it. Custom states can be added to inherited types.
  Rename left OPEN. ⚠️ Its finding that the four hidden Request/Response types cannot be disabled
  or removed is **true but no longer load-bearing** — they are tooling back ends never offered to
  a chooser, so the collision it was gathered to inform does not exist.

## Not yet specified

- **States beyond `To do / Doing / Done`.** Every measured type has the same three. Whether a
  `Change` needs a review/merged state, whether a `Validation` needs a failed state, and whether
  a level-less artifact needs a *superseded* state distinct from `Done` — cannot be phrased
  sharply until the type set is settled (0004, 0005, 0006).
- ~~**The dormant Request/Response types' fate.**~~ **Void — the premise was a factual error,**
  corrected by the author 2026-08-22 and confirmed measured. The four sit in
  `Microsoft.HiddenCategory` (*"WITs that you do not want users to create manually"*) and are
  **tooling back ends, not namable vocabulary** — a chooser is never offered them, so there was
  never a collision. Ticket 0009's finding that they cannot be disabled or removed remains true
  but is no longer relevant here. Already filed and **not this map's work**: ADO #656 and #657.
- **Naming convention for types and fields.** `Custom.WayfinderAnswer` vs `Custom.Maturity` —
  is a domain prefix a rule? Bears on every new field, but not sharp until 0007 rules on which
  fields survive.
- **Area paths, iterations, tags and the five canonical roles.** `docs/agents/triage-labels.md`
  applies roles as `System.Tags` because ADO has no labels. Whether roles are part of "how each
  kind of team member uses twig" (ticket 0003) or a separate convention layer is unclear until
  0003 lands.
- **Seeds in the settled process.** A seed is `WorkItem.IsSeed` plus a negative id, not a type.
  Whether the settled conventions mandate seeding for particular types (a `Change` chain, say)
  is downstream of 0004.
- **What twig's generic policy engine (ADO #615) must express**, given the map's rulings. This
  map decides the vocabulary; #615 is where the enforcement lives. The seam between them will
  sharpen as 0001 and 0007 land.

## Out of scope

- **Creating the types, PATCHing the process, editing form layouts on the board.** This map
  produces rulings; the mutation is the build that follows.
- **Creating AB#644's five implementation units.** They are blocked on a Work-level type for
  "a unit of work that can be pull-requested into main" (proposed as `Change`, ticket 0004), so
  this map is upstream of them. The handoff is in `Custom.WayfinderAnswer` on work item 644
  (20,308 chars; §5 holds the five units); map #621 is `Doing` with all five design children
  Done. **Note the dependency and move on — do not create them here.**
- **Building the declared policy engine (ADO #615).** This map may say what it must express; it
  does not design or build it. Per ticket 0001, every ruling that fails the customer-zero gate
  emits a line naming the missing mechanism, and the map **collects** those lines as #615's
  requirements list rather than leaving them scattered per-ticket. Collecting is inside this
  boundary; designing and building are not.
- **Re-deriving the closed research.** Parent/child enforcement, backlog levels, process
  inheritance and audience views are answered with primary sources (see Notes).
- **Migrating existing work items onto whatever types are ruled in.** A data migration is a
  build, and it cannot be scoped before the type set exists.
