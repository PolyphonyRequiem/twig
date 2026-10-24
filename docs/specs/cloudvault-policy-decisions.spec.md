# Declared Policy Decisions — CloudVault Inventory and Engine Contract

> **Status:** Implemented (first slice), AB#1169.
> **Domain:** Declared, repository-owned work-management policy evaluated natively in the
> change-proposal path.
> **Settled by:** Epic #1169 Phase 0 rulings D1–D5 (approved by Daniel), Idea #615
> (declared policy engine, closed into Spec #727 / Task #732), the existing
> `PlanProcessRuleGate` (AB#673/#720/#721/#803/#881) and `ProposalAuthorizationGate` (AB#743).
> **Deliberately not settled here:** authorizer authenticity and independence (Spec #729,
> `docs/projects/change-proposal-authorization-audit.plan.md`), reconciliation of historical
> proposal journals and the disposition of #726/#674/#727/#850 (Epic #1171), and any rule
> that requires judgment rather than mechanical evaluation.

---

## Problem

CloudVault teams drive work through policy-governed skills (create, update, review,
closeout, publication). Before this work Twig enforced two narrow mechanical gates — required
close-gate fields (`PlanProcessRuleGate`) and digest/mode/authorizer presence
(`ProposalAuthorizationGate`) — and everything else lived in agent prose. A rule written only
in prose is enforced only when an agent remembers it, and a reviewer cannot see from a
proposal preview whether it was checked.

Idea #615 is closed (merged into Spec #727 / Task #732), but closure is a tracker
disposition: at baseline `f74d3d58` there was **no general declared-policy engine** in
source. This spec records what was inventoried, what Twig now enforces, and what remains a
host judgment, with rationale.

## Rulings (Phase 0)

| Id | Ruling |
|---|---|
| D1 | Twig enforces declared **mechanical** rules natively. Judgment calls (fit, meaning, sufficiency, readiness) stay with reviewers or the host skill. |
| D2 | Rules live in a **repository-owned** policy file, `twig.policy.json` at the repository root, loaded at runtime. Twig code names no CloudVault type, label or field. |
| D3 | A violated rule **refuses**. When a rule cannot be evaluated the outcome is **`unavailable`**, which also blocks — never a silent permit. |
| D4 | Pre-existing violations are **reported, not blocked**. A proposal is refused only when it **adds or worsens** a violation. |
| D5 | Follow-up decomposition of #1169 is published as Issues and Tasks through the proposal path, each write separately approved and authorized. |

## Not a DSL

CloudVault's enforcement map forbids a policy DSL, and so does this design. The file selects
from a **fixed set of rule kinds** implemented in Twig and supplies their data (type names,
label prefixes, limits). There are no expressions, conditions, or user-defined code. A new
kind of rule is a Twig code change with tests, reviewed like any other.

## Inventory

Category key: **Native** — enforced by Twig; **Existing** — already enforced before #1169;
**Host** — judgment, stays with the reviewer/host skill; **Out** — owned elsewhere.

| # | CloudVault decision | Source | Category | Mechanism / rationale |
|---|---|---|---|---|
| 1 | A Task's parent must be a User Story or Bug (type constraint on parent links) | `relationships.md` *Type constraints* | Native | `parent-type` rule kind |
| 2 | No self-dependency, duplicate dependency, or dependency cycle within scope | `relationships.md` *Validate within scope* | Native | `dependency-integrity` rule kind |
| 3 | `orchestration:` labels only on User Story / Bug, one value | `task-creation.md` *Orchestration and execution* | Native | `label-applicability` rule kind |
| 4 | `execution:` labels only on Task, one value; labels do not inherit | `task-creation.md` *Orchestration and execution* | Native | `label-applicability` rule kind (evaluated per item, never inherited) |
| 5 | Close-gate fields must be supplied with the Done transition | `closeout.md`, `enforcement.md` | Existing | `PlanProcessRuleGate`; rule-load failure now refuses (D3) instead of permitting; decisions surface in proposal previews |
| 6 | Unknown/unverifiable state is distinct from violated | `enforcement.md` | Native | `unavailable` outcome, blocking (D3) |
| 7 | Exact digest, expected revision, journaled apply | `publication.md`, `enforcement.md` | Existing | proposal digest, revision guards, journal |
| 8 | Human vs model authorization mode | `publication.md` | Existing | `ProposalAuthorizationGate` |
| 9 | Whether a Task is meaningful / right-sized | `task-creation.md` | Host | judgment (D1) |
| 10 | Attestation sufficiency at closeout | `closeout.md` | Host | judgment (D1) |
| 11 | Review readiness; whether review was material | `review.md` | Host | judgment (D1) |
| 12 | Type fit (is this really a Bug?) | `relationships.md`, type references | Host | judgment (D1) |
| 13 | Human-visible field/form placement | `platform-evidence.md` | Host | process layout, not a proposal property |
| 14 | Bench scope and reviewer isolation | `bench-management.md`, `review.md` | Host | Bench visibility grants no authority; isolation is a host/session property |
| 15 | Authorizer authenticity and independence | `publication.md` | Out | Spec #729 / authorization audit plan; CLI `--authorize` is caller-supplied |
| 16 | Historical journal reconciliation | — | Out | Epic #1171 |
| 17 | A Bug correction is normally a Related link | `relationships.md` | Host | "normally" — no universal mechanical gate |
| 18 | Scope of a planned-set approval | `publication.md` | Host | approval is a human act over a concrete set |

## Engine contract

### Decision

Every evaluated rule produces zero or more `PolicyDecision` records:

| Field | Meaning |
|---|---|
| `ruleId` | Id from the policy file (`policy-load` for a file that could not be read) |
| `kind` | Rule kind |
| `outcome` | `permit`, `warn`, `refuse`, `unavailable` |
| `reason` | Human-readable explanation, naming the item and the fix |
| `workItemId` | Affected item (negative for a seed in the proposal), when applicable |
| `opId` | Proposal operation that introduced the change, when applicable |

`refuse` and `unavailable` are **blocking**. A rule declared with `"enforcement": "warn"`
reports `warn` in place of `refuse`; `unavailable` is never downgraded. Pre-existing
violations that the proposal does not worsen are reported as `warn` (D4).

### Surfacing

* **Preview** — decisions appear in the review model (`policyDecisions`), in JSON, human,
  table and browser renderings. Each blocking decision is also a `policy` blocker, and a
  proposal with a blocking decision cannot be applied.
* **Apply** — the policy is re-evaluated immediately before authorization; a blocking
  decision refuses the apply before any write.

Evaluation is over the **whole proposal's post-change state**, not operation order.

### Policy file

```json
{
  "version": 1,
  "rules": [
    { "id": "task-parent", "kind": "parent-type",
      "childType": "Task", "allowedParentTypes": ["User Story", "Bug"] },
    { "id": "dependencies", "kind": "dependency-integrity", "maxNodes": 200 },
    { "id": "orchestration-label", "kind": "label-applicability",
      "prefix": "orchestration:", "allowedTypes": ["User Story", "Bug"], "exclusive": true },
    { "id": "execution-label", "kind": "label-applicability",
      "prefix": "execution:", "allowedTypes": ["Task"], "exclusive": true }
  ]
}
```

* **Missing file** — no policy; no decisions; existing gates unchanged.
* **Unreadable or malformed file, unknown kind, unsupported version, duplicate id** — one
  blocking `unavailable` decision with rule id `policy-load`. A team that declared policy
  must not lose it silently to a typo (D3).
* Type and label comparisons are case-insensitive. Labels are read from `System.Tags`
  (semicolon-separated) unless a rule sets `field`.
* Every rule accepts `"enforcement": "refuse"` (default) or `"warn"`. A `warn` rule reports a
  violation as a non-blocking `warn` decision; `unavailable` stays blocking regardless.
* Policy is evaluated at preview and before authorization at apply, only while the proposal
  journal is still `Planned`. A resumed apply does not re-judge operations already confirmed.
* Decisions appear in the review model and in JSON as `policyDecisions`: an array of
  `{ ruleId, kind, outcome, blocking, reason, workItemId, opId }` with `outcome` one of `permit`, `warn`,
  `refuse`, `unavailable`. Any `refuse` or `unavailable` sets `canApply` to false.

### Rule kinds

**`parent-type`** — evaluates each *new* parent edge in the proposal (`add-link` with relation
`parent`, or a seed published with a parent). If the child's type is `childType` and the
parent's type is not in `allowedParentTypes`, refuse. Existing edges are not re-judged (D4),
except that a `batch` changing an item's type re-judges that item's existing parent edge,
because the change can bring it under a rule. A parent whose type is not known locally is
`unavailable`. Known limit: retyping a *parent* does not re-judge its children.

**`dependency-integrity`** — evaluates each new predecessor/successor edge. Self-dependency and
duplicate edges (already cached or repeated in the proposal) refuse. A cycle is found by a
bounded search over cached links plus proposed adds minus proposed removes; a cycle refuses.
If the search reaches an item whose links were never fetched, or exceeds `maxNodes`, the
outcome is `unavailable` and the reason says to refresh links.

**`label-applicability`** — evaluates items whose label field the proposal sets (a `batch`
writing the field, or a published seed carrying it). A label with `prefix` on a type outside
`allowedTypes` is a violation; with `exclusive`, more than one value of the prefix is a
contradiction that requires resolving intent. Refuse only when the post-change violations are
not a subset of the pre-change ones (D4).

## Process-rule gate change (D3)

`PlanProcessRuleGate` previously returned *permit* when the process rule set could not be
loaded. It now returns *refresh required*: plan preview reports an `unavailable` decision,
and the per-operation pre-write gate re-evaluates before an ADO write while preserving its
retryable or terminal journal outcome. `twig state` also refuses with an explanation. An
unknown work-item type or a missing rule set is `unavailable` because required-field gates
cannot be evaluated. A null provider (no process-rule support configured) still permits.

## Boundaries

* **#1171** audits historical policy/process work and journal reconciliation. #1169 does not
  re-close, re-scope or reassign #726, #674, #727 or #850.
* **#729** owns authorizer authenticity. Nothing here claims to prove who authorized.
* Host skills keep every **Host** row above; Twig reports mechanical facts they can rely on.
