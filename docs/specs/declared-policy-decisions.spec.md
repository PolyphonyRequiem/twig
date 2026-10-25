# Declared Policy Decisions — Reference Inventory and Engine Contract

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

Teams can drive work through policy-governed skills (create, update, review,
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
| D2 | Rules live in a **repository-owned** policy file, `twig.policy.json` at the repository root, loaded at runtime. Twig code hardcodes no team's type, label or field. |
| D3 | A violated rule **refuses**. When a rule cannot be evaluated the outcome is **`unavailable`**, which also blocks — never a silent permit. |
| D4 | Pre-existing violations are **reported, not blocked**. A proposal is refused only when it **adds or worsens** a violation. |
| D5 | Follow-up decomposition of #1169 is published as Issues and Tasks through the proposal path, each write separately approved and authorized. |

## Not a DSL

This design does not introduce a policy DSL. The file selects
from a **fixed set of rule kinds** implemented in Twig and supplies their data (type names,
label prefixes, limits). There are no expressions, conditions, or user-defined code. A new
kind of rule is a Twig code change with tests, reviewed like any other.

## Inventory

Category key: **Native** — enforced by Twig; **Existing** — already enforced before #1169;
**Host** — judgment, stays with the reviewer/host skill; **Out** — owned elsewhere.

The inventory below uses an anonymized reference service, SampleService. Its conventions
are examples, not built-in policy or authority over Twig's design. Source names identify
reference documents, not dependencies required to configure or use Twig.

| # | Reference decision | Reference document | Category | Mechanism / rationale |
|---|---|---|---|---|
| 1 | A Task's parent must be a User Story or Bug (type constraint on parent links) | `relationships.md` *Type constraints* | Native | `parent-type` rule kind |
| 2 | No self-dependency, duplicate dependency, or dependency cycle within scope | `relationships.md` *Validate within scope* | Native | `dependency-integrity` rule kind |
| 3 | `orchestration:` labels only on User Story / Bug, one value | `task-creation.md` *Orchestration and execution* | Native | `label-applicability` rule kind |
| 4 | `execution:` labels only on Task, one value; labels do not inherit | `task-creation.md` *Orchestration and execution* | Native | `label-applicability` rule kind (evaluated per item, never inherited) |
| 5 | Close-gate fields must be supplied with the Done transition | `closeout.md`, `enforcement.md` | Existing | `PlanProcessRuleGate`; unavailable rules block writes and surface as blocking preview decisions |
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

### Current-state assessment and reconciliation

Native proposal enforcement remains mandatory. Separately, `policy assess` examines existing
published items, rather than interpreting an empty proposal as evidence of compliance.

```sh
twig policy assess --ids 42,43 -o json
twig policy reconcile --ids 42,43 --evidence <evidenceDigest> --finding f1 --strategy reparent --parent 43 -o json
twig policy reconcile --ids 42 --evidence <evidenceDigest> --finding f1 --strategy remove-labels -o json --proposal-only > repair.json
twig proposal preview --file repair.json -o json
# Review the proposal's digest; authorize and apply through the ordinary proposal lifecycle.
# After apply: refresh the affected items and their links, then assess the same scope again.
```

Both commands support `human`, `json`, and `minimal`; JSON aliases are accepted. Assessment
exits 0 only for complete compliant observations, 1 for noncompliance or unavailable evidence,
and 2 for invalid usage. Reconciliation exits 0 when a proposal was rendered, **not** when
compliance was achieved. `--proposal-only` requires JSON and emits only the native version-1
proposal for deliberate redirection; refusal emits no proposal bytes. Do not overwrite an
existing draft with shell redirection. Without that flag JSON includes `before`, `projected`,
`proposal`, and the ordinary proposal digest. Human output includes alternatives and native
proposal JSON. Commands do not refresh, apply, authorize, or journal anything automatically.
MCP integration remains withdrawn.

The assessment names its exact `scope`, `freshness: cached`, item revisions, sync timestamps,
and link verification timestamps. It is **not a live or whole-board compliance claim**.
Scope is 1–200 explicit positive IDs. Parent/label rules assess those IDs, with supporting
parent observations; they do not certify out-of-scope children, including children of a
retyped parent. Dependency integrity examines their incident cached dependency component
(both source and target directions), reports acquired IDs as `dependencyScope`, and is
capped by the rule's `maxNodes`, a hard 1,000-item ceiling, and 4,000 physical relations.
A limit, missing item/type/sync observation, local
modification, absent label field, or unverified link set preserves an `EvidenceUnavailable`
finding. An explicitly observed empty/null field or verified empty link set is different
from missing evidence. Missing/empty policy declarations cannot certify compliance.

Link storage contains source-local observations, not synthesized reciprocal rows. Traversal
discovers outside sources whose cached dependency rows target the frontier, then acquires
each source's complete relation set, item revision and verification stamp. Those source facts
are included in the evidence digest. If two verified endpoint observations disagree about a
dependency's presence, assessment preserves definite findings but reports
`EvidenceUnavailable`; it does not silently union contradictory facts into compliance.
A verified empty set is source-local evidence, not a global absence certificate. Uncached
relations and changes made after the recorded observations remain outside this cached claim.

Findings carry a typed `code`, rule/item identity and structured `evidence`: parent/type,
labels/field, cycle edges, counts or missing IDs. Strategy generation consumes those facts,
**never parses `Reason` prose**. Warning enforcement does not turn a current violation into
compliance. Definite violations remain visible alongside unavailable evidence.

The fixed strategies are intentionally bounded, not a DSL:

* `parent-type`: explicitly detach the disallowed parent, or choose `reparent` with a
  caller-supplied allowed parent. Include that parent and its ancestry in the assessed
  scope. Hierarchy cycles and incomplete ancestry refuse. No parent target is guessed.
* `label-applicability`: remove the declared prefix's labels while retaining unrelated
  labels; for exclusivity, explicitly select which observed label to retain. No label
  priority is inferred. At most 32 alternatives are shown with `strategiesTruncated`
  when there are more; evidence is not truncated by this presentation limit.
* `dependency-integrity`: remove an explicitly selected self-link, duplicated dependency
  (all occurrences, not an inferred choice to retain one), or witnessed cycle edge.
  Mirrored predecessor/successor observations are one
  logical edge, not duplicates. A cycle witness is not an exhaustive enumeration of
  every cycle, and removing one edge may leave others. No preferred edge is selected.
  The cache stores distinct relations, so it cannot certify physical multiplicity at the
  server. Duplicate findings are emitted when supplied evidence preserves duplicates;
  ordinary cache assessments certify only the observed logical graph. Native strict
  `remove-link` removes all matching occurrences in one revision-fenced PATCH, consistent
  with its existing absence readback contract.

Every selection requires the assessment's `evidenceDigest` and its finding/strategy IDs.
Reconciliation reacquires the policy and cached facts, rejects stale choices, projects the
chosen operations over that observation, and reassesses **all declared rules**. It refuses
unknown effects, no improvement, or a new/worsened violation; unrelated pre-existing findings
remain visible and do not prevent an improving repair. It checks the observation again before
emitting. The digest includes policy data and observed evidence; it is distinct from the
proposal digest that binds confirmation and authorization.

Repairs use the existing `batch` and `remove-link`/`add-link` operations with expected
revisions. Reparenting removes the old edge then adds the explicitly chosen new edge at the
next revision; this is a two-operation proposal, **not an atomic reparent**. A partial failure
requires the ordinary journal recovery path. Proposal preview/apply retain native declared
policy, process, pending-change, origin, confirmation, authorization and journal gates.
A successful write/readback proves the authored effect, not policy compliance: only a new
assessment after refresh can establish observed compliance for the named scope.

This module does not merge local/remote fields (`ThreeWayMerge`), calculate process-dependent
safe clears (`DependentFieldReconciler`), or settle failed/indeterminate operation outcomes
(`proposal reconcile`). Those meanings of reconciliation remain separate.

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

* **Preview** — declared policy and process-rule decisions appear in the review model
  (`policyDecisions`), in JSON, human, table and browser renderings. Each blocking decision
  is also a `policy` blocker, and a proposal with a blocking decision cannot be applied.
* **Apply** — declared policy is re-evaluated immediately before authorization; a blocking
  decision refuses the apply before any write. Process rules are re-evaluated at the
  per-operation pre-write gate.

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

* **Missing file** — no policy; no decisions; existing gates unchanged. Only a missing file
  or directory is treated as absence; other read failures are unavailable.
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
cannot be evaluated. A null provider (no process-rule provider registered) is also
`unavailable`: an absent provider is indistinguishable from a forgotten registration, so it
fails closed rather than permitting. A provider whose process has no rules must say so
explicitly by registering `EmptyProcessRuleProvider.Instance` (or any provider returning an
empty rule set), which permits.

## Boundaries

* **#1171** audits historical policy/process work and journal reconciliation. #1169 does not
  re-close, re-scope or reassign #726, #674, #727 or #850.
* **#729** owns authorizer authenticity. Nothing here claims to prove who authorized.
* **#1167 / #1170** provide provider capability contracts, a read-only GitHub adapter, and
  a durable local relationship overlay. Those capabilities do not themselves provide policy
  facts. The evaluator consumes cached local work-item and link repositories, while process
  rules arrive through `IProcessRuleProvider` (work-item type → `ProcessRule` list). A provider
  with no process rules must register `EmptyProcessRuleProvider.Instance` (or another provider
  returning an empty rule set); an unregistered provider fails closed. Provider-native
  revision-bound reads and provider-gated write composition remain outside this slice and are
  owned by #1189, which depends on #1177 and #1178. Relationship overlay facts are not treated
  as native policy facts here.
* Host skills keep every **Host** row above; Twig reports mechanical facts they can rely on.
