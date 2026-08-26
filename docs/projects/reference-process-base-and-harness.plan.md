# Reference Process: Base-Process Selection, Tailoring, and Sandbox Validation Harness

| Field | Value |
|-------|-------|
| **Work Item** | #733 (T2 under Spec #727) |
| **Type** | Task (Design) |
| **Status** | Doing |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. Closes the deferred base-process rollout question in #727. |

---

## Executive Summary

Spec #727 pins a locked hierarchy — `Initiative → {Investigation, Feature, Bug} → Task`
— with sprint entry restricted to `Task`, and requires that the reference profile be
"exercised against a Sandbox project before it is treated as authoritative". This note
settles the operational half of that spec: **which ADO base process the reference
profile inherits from, the ordered tailoring steps that reach the locked hierarchy, and
the structure of the Sandbox validation harness that produces per-surface evidence.**

Decisions locked here:

1. **Base process:** an inherited process whose parent is the built-in **Basic** template.
2. **Tailoring path:** disable the two inherited leaf types (`Epic`, `Issue`) and add
   four custom types (`Initiative`, `Investigation`, `Feature`, `Bug`) that reuse the
   three Basic backlog behaviors (`Microsoft.VSTS.Basic.EpicBacklogBehavior`,
   `System.RequirementBacklogBehavior`, `System.TaskBacklogBehavior`). `Task` stays as
   the sole holder of `System.TaskBacklogBehavior`, which is what enforces
   sprint-entry-only-for-`Task`.
3. **Ordering:** every process-customization step is a human step (ADO REST or web UI —
   Twig has no plan verb for process customization). Every hierarchy/link seed step is
   a Twig plan operation (`publish-seed`, `add-link`, `batch`).
4. **Harness structure:** a per-surface evidence bundle stored under
   `docs/reference-process/harness/<version>/` in the Twig repo. Each of six required
   surfaces has a named `.json` evidence artifact plus a `.png` screenshot artifact and
   is produced by a specific plan operation against a dedicated `Twig-Reference-Sandbox`
   ADO project.
5. **Harness gating:** the harness is a **hard gate for committing a new version of the
   reference profile** (owned by #732). It is **not** a runtime gate — Twig core still
   validates the live process against the profile at runtime; the harness only guards
   what enters the repo.
6. **`.conductor/process-config.yaml` retirement:** deferred to the migration spec. This
   note records which fields become owned by the profile and which stay owned by the
   conductor.

Nothing in this note reintroduces hardcoded process, type, or state names into Twig
core: every fact below is either (a) a fact about the specific reference profile the
Twig repo ships (which is data, not code), or (b) something the runtime discovers via
`IProcessConfigurationProvider`.

---

## Background

### The invariant this design must preserve

`.github/copilot-instructions.md` states the process-agnostic hard rule: *"No hardcoded
state names, type names, or process template assumptions [in Twig]. All process-specific
mapping comes from `IProcessConfigurationProvider` at runtime."* This note therefore
does not name types or states inside Twig source; it specifies the **data shape** the
reference profile carries and the **operational recipe** for producing a matching ADO
process.

### The current live process, as evidence

`twig process description -o json` against the current Twig ADO project reports:

- `processName: "Hyperbright"`, an inherited process.
- Every custom type carries `customization: "inherited"` or `"custom"`.
- Three backlog behaviors are present across the type set:
  - `Microsoft.VSTS.Basic.EpicBacklogBehavior` — displayed as "Initiatives", rank 30.
    Held only by `Hyperbright.Epic` (default).
  - `System.RequirementBacklogBehavior` — displayed as "Work", rank 20. Held by
    `Hyperbright.Feature`, `Hyperbright.Bug`, `Hyperbright.Spec`,
    `Hyperbright.WayfinderTask`.
  - `System.TaskBacklogBehavior` — displayed as "Tasks", rank 10. Held only by
    `Hyperbright.Task` (default).

Two facts follow directly from that evidence:

1. **Hyperbright is a Basic-inherited process.** The presence of
   `Microsoft.VSTS.Basic.EpicBacklogBehavior` is the tell — that behavior reference is
   the one Basic ships and Agile/Scrum/CMMI do not. The `_apis/work/processes` REST
   endpoint would confirm `parentProcessTypeId` on request; we do not depend on that
   confirmation for the design.
2. **The three backlog behavior refs are stable, well-known ADO identifiers**, not
   Hyperbright-specific. They will exist in any Basic-inherited process, including the
   Sandbox one this design provisions.

The Hyperbright process itself is **out of scope** for the reference profile — it is
the working project, not the reference. The reference profile is a fresh
Basic-inherited process shipped with the Twig repo, exercised against a fresh Sandbox
project.

### Twig plan verbs available to us

Native `twig plan` verbs and their kinds are limited to `batch`, `add-link`,
`remove-link`, `publish-seed`, `delete` (see `skill://ado-publish`). None of these
mutate the ADO process template; all of them mutate work items. Everything in the
"tailoring" section of this note is therefore a **human step**, executed via the ADO
web UI or a direct REST call, and cannot be represented as a plan operation. The Twig
plan verbs enter the story only from the Sandbox seeding step onward.

---

## Contract with #732 (profile schema)

This note owns the **process** side of #727. It does not own the **document** side.
The following facts are inputs to this design that the profile document (owned by #732)
must declare:

- The parent process id — Twig accepts a live process only if its
  `parentProcessTypeId` matches the value the profile pins.
- The four target type reference names (canonical: `Twig.Initiative`,
  `Twig.Investigation`, `Twig.Feature`, `Twig.Bug`) — the exact strings and their
  disable-set counterparts are declared in the profile, not here.
- The three required backlog behavior references and which type carries each — declared
  in the profile as `(referenceName, behavior)` pairs.
- The declared link-kind meanings (`System.LinkTypes.Hierarchy-Forward/Reverse` =
  decomposition, `System.LinkTypes.Related` = informs,
  `System.LinkTypes.Dependency-Forward/Reverse` = blocking/sequencing,
  `ArtifactLink` = primary evidence carrier).
+ **The middle-tier role bindings.** The profile binds each Requirements-tier type
  reference name to exactly one of the three declared roles `investigation`,
  `feature`, `bug`. There are exactly three such bindings, one per role. This
  design guarantees the tailoring produces exactly three types at
  `System.RequirementBacklogBehavior` for the profile to bind.
+ **The sprint-tier singleton.** The profile binds exactly one type reference name
  to the sprint role (`task`). This design guarantees `System.TaskBacklogBehavior`
  is held by exactly one type in the tailored process.
+ **The `baseProcessVersion` opaque string.** The profile's `baseProcessVersion`
  field is a free-form pin whose content and bump rules are owned by this design
  (§7). #732's `profileVersion` (semver `MAJOR.MINOR.PATCH`) is owned by #732;
  both are compared exact-match at profile load.

Any assertion this note makes about "the profile declares X" is a dependency on #732,
not a specification of the profile itself.

---

## Section 1 — Base-process selection

### The four candidates

Every ADO project is provisioned from one built-in template (Basic, Agile, Scrum,
CMMI) or an inherited process descended from one of them. We evaluate each against the
locked hierarchy `Initiative → {Investigation, Feature, Bug} → Task` and the
sprint-entry-only-for-`Task` rule.

| Candidate | Ships with … | Fit for the locked hierarchy |
|-----------|--------------|------------------------------|
| **Basic** (inherited) | `Epic` (Initiatives backlog), `Issue` (Requirements/"Work" backlog), `Task` (Tasks backlog). Exactly three levels. Three states per type (`To Do`, `Doing`, `Done`). | **Clean fit.** One type per backlog behavior. Sprint entry is already `Task`-only. Tailoring only needs to rename Epic→Initiative and split Issue into Investigation/Feature/Bug — four type-level operations. |
| **Agile** (inherited) | Epic, Feature, User Story, Task, Bug. Feature and User Story sit on **separate** portfolio backlogs. Bug can be configured to sit on either the User Story backlog or the Task backlog per team. | **Poor fit.** Agile injects a Feature portfolio level between the top and Requirements, giving four levels; we would have to disable that portfolio and re-home Feature onto the Requirements behavior, fighting the template. Bug placement is a team-scoped decision, which means the profile cannot pin it purely by process customization — it also has to describe the required team configuration. |
| **Scrum** (inherited) | Epic, Feature, Product Backlog Item, Task, Bug. Same portfolio-injection problem as Agile, plus `Product Backlog Item` naming and Scrum-specific states (`New`, `Approved`, `Committed`, `Done`, `Removed`). | **Poor fit.** Same portfolio problem; also the Scrum state model needs collapsing to match #727's three-state semantic (out of scope for this design). |
| **CMMI** (inherited) | Rich set: Requirement, Change Request, Risk, Issue, Review, plus Epic/Feature/Task. Heavy field defaults (`Committed`, `Original Estimate`, etc.). | **Poor fit.** Heavy field defaults and extra types create a large "disable list" for the profile. The reference profile becomes noisy and its diff against a live process is dominated by CMMI's opinion, not the four types we care about. |

### Decision

**Adopt an inherited process whose parent is Basic.** Reasons:

1. **Backlog behavior alignment is 1:1.** Basic ships exactly three backlog behaviors
   (`Microsoft.VSTS.Basic.EpicBacklogBehavior`, `System.RequirementBacklogBehavior`,
   `System.TaskBacklogBehavior`) and exactly one default type per behavior. That
   matches the locked hierarchy without renegotiation.
2. **Sprint-entry-only-for-Task is a Basic-native property.** In Basic, `Task` is the
   only type carrying `System.TaskBacklogBehavior`; the sprint board is driven off
   that behavior. Preserving this property costs zero tailoring work — we simply
   never assign `TaskBacklogBehavior` to any custom type.
3. **State model is already three-valued.** Basic ships three states per type
   (`To Do`, `Doing`, `Done`) matching #727's semantic three-state lifecycle
   (`Proposed → InProgress → Completed`). Twig core discovers the mapping via
   `IProcessConfigurationProvider`; the profile only has to declare that the state
   count is three and the reachability graph is total.
4. **Minimum disable list.** Basic ships two leaf-level types (`Epic`, `Issue`); we
   disable both. Every other candidate ships four or more leaf-level types and
   accumulates process noise the profile has to encode as "disabled".
5. **The live Twig project already demonstrates Basic inheritance.** The Hyperbright
   process is Basic-inherited; reproducing an inherited-from-Basic process in a
   Sandbox project is a known, exercised path — not a research spike.

### Non-goals of this section

- Deciding whether the reference profile is a single inherited process or a family
  (parametric variants). #727 pins one profile; this design carries the same
  assumption.
- Naming the inherited process. The Twig repo profile pins one string (e.g.
  `Twig Reference v1`); the exact string is data owned by #732.

---

## Section 2 — Tailoring steps

The customizations below are applied to a fresh inherited process whose parent is
Basic. All of them are **human steps** — ADO does not accept process-template
mutations through Twig plan verbs. Steps use the ADO **Process customization REST
API** (`_apis/work/processes/{processId}/…`) so the recipe can be scripted; the web UI
is an accepted equivalent for a one-shot bootstrap. Numbering is the required
execution order — do not reorder.

Preconditions for this section: an ADO organization exists; a person with Process
Administrator rights is available; the reference profile in `#732` is drafted (so we
know which type reference names to create).

### 2.1 Create the inherited process (human, REST or UI)

Create a new process whose `parentProcessTypeId` is Basic
(`b8a3a935-7e91-48b8-a94c-606d37c3e9f2`). Give it the reference-profile name declared
in #732. Evidence: `POST /_apis/work/processes` returns a fresh `processId`; recording
it as `processId.txt` under the harness evidence bundle.

### 2.2 Disable the inherited leaf types (human, REST)

`PATCH /_apis/work/processes/{processId}/workItemTypes/Microsoft.VSTS.WorkItemTypes.Epic`
with `{ "isDisabled": true }`. Repeat for
`Microsoft.VSTS.WorkItemTypes.Issue`. `Task` stays enabled (we reuse it in step 2.4).

### 2.3 Add the four custom types (human, REST)

`POST /_apis/work/processes/{processId}/workItemTypes` four times. For each type:

| Order | Custom type | Icon (recommended) | Color | Justification |
|-------|-------------|--------------------|-------|---------------|
| 2.3.a | `Initiative` | `icon_crown` | `#7B68EE` | Top-level, portfolio. |
| 2.3.b | `Investigation` | `icon_review` | `#5A8AC9` | Requirements-level exploratory work. |
| 2.3.c | `Feature` | `icon_trophy` | `#773B93` | Requirements-level product work. |
| 2.3.d | `Bug` | `icon_insect` | `#CC293D` | Requirements-level defect work. |

Reference names are minted as `Twig.Initiative`, `Twig.Investigation`, `Twig.Feature`,
`Twig.Bug` (exact strings owned by #732; this design only fixes the ordering). Each
type receives the three-state lifecycle Basic-inherited types get for free (`To Do`,
`Doing`, `Done`). No new states, no state renames — that is what keeps Twig core's
state-model discovery unchanged.

### 2.4 Assign backlog behaviors (human, REST)

`POST /_apis/work/processes/{processId}/workItemTypesBehaviors/{ref}/behaviors` once
per type:

| Type reference | Behavior reference | Portfolio level |
|----------------|-------------------|-----------------|
| `Twig.Initiative` | `Microsoft.VSTS.Basic.EpicBacklogBehavior` | Portfolio (Initiatives) |
| `Twig.Investigation` | `System.RequirementBacklogBehavior` | Requirements (Work) |
| `Twig.Feature` | `System.RequirementBacklogBehavior` | Requirements (Work) |
| `Twig.Bug` | `System.RequirementBacklogBehavior` | Requirements (Work) |
| `Microsoft.VSTS.WorkItemTypes.Task` | `System.TaskBacklogBehavior` (inherited default; verify only) | Sprint (Tasks) |

Behavior assignment is what places a type onto a backlog. Because we never assign
`System.TaskBacklogBehavior` to any custom type, **sprint entry is achievable only by
`Task`** — the rule from #727 is enforced structurally, not by convention.

### 2.5 Verify the required-fields set is empty on custom types (human, REST)

`GET /_apis/work/processes/{processId}/workItemTypes/{ref}/fields` for each new type
and confirm no field is required beyond the ADO-mandated system fields
(`System.Title`, `System.State`, `System.Reason`). Any inherited required field that
crept in — e.g. from a template that reused a legacy layout — is removed here. This
step is why we did not pick CMMI or Agile as parent: they contribute required fields
we would need to unrequire, and the profile diff has to encode those removals.

### 2.6 Publish the process (human, REST or UI)

Inherited processes are Draft until published. `POST` the publish transition or use
the Web UI "Publish" button. Publishing is the point at which a new project can be
created against the process.

### 2.7 Provision the Sandbox project (human, REST or UI)

Create an ADO project named `Twig-Reference-Sandbox` **using the inherited process
from 2.6 as its process template**. Record the resulting project id in the harness
evidence bundle. Evidence: `GET /_apis/projects/{project}` reports
`capabilities.processTemplate.templateTypeId == <processId>`.

### 2.8 Confirm link kinds are available on the project (human, UI check)

Basic (and every ADO process) ships with the four link-kind families this design
depends on: `System.LinkTypes.Hierarchy-Forward/Reverse`,
`System.LinkTypes.Related`, `System.LinkTypes.Dependency-Forward/Reverse`, and the
artifact link family (`ArtifactLink`). No customization is required. Evidence:
`GET /_apis/wit/workItemRelationTypes` returns all four. Presence, not creation, is
the check.

### 2.9 Record baseline via `twig process description` (automated: shell)

Run `twig process description --org <org> --project Twig-Reference-Sandbox -o json`
and save the output as `harness/evidence/00-sandbox-baseline.json`. This is the
canonical shape #732's profile document is compared against; anything downstream that
compares "reference profile vs live" reduces to comparing this JSON to
`twig process description` output on any other project.

---

## Section 3 — Ordering: base-process → tailoring → profile → Sandbox exercise

The steps below are the **only** legitimate order. Numbering combines Section 2's
tailoring with the seed/link operations that exercise the Sandbox.

| # | Step | Kind | Notes |
|---|------|------|-------|
| 1 | Draft the profile document schema (owned by #732). | Human (parallel doc work) | This design consumes the schema shape but not the values; #732 can proceed in parallel until step 5. |
| 2 | Create the inherited-from-Basic process. | Human, REST | §2.1 |
| 3 | Disable `Epic` and `Issue`. | Human, REST | §2.2 |
| 4 | Add `Twig.Initiative`, `Twig.Investigation`, `Twig.Feature`, `Twig.Bug`. | Human, REST | §2.3 — must precede step 5 so behaviors have types to attach to. |
| 5 | Assign backlog behaviors. | Human, REST | §2.4 — must precede step 8 so sprint-only-for-Task is enforced before publish. |
| 6 | Verify required-field set. | Human, REST | §2.5 |
| 7 | Publish the process. | Human, REST or UI | §2.6 |
| 8 | Provision the `Twig-Reference-Sandbox` project. | Human, REST or UI | §2.7 |
| 9 | Confirm link kinds available. | Human, UI check | §2.8 |
| 10 | Record the baseline process description. | Automated (`twig process description`) | §2.9 |
| 11 | Populate the profile document from step 10's JSON (owned by #732). | Human, doc | #732 ratifies the profile against Sandbox reality. |
| 12 | Seed the hierarchy in Sandbox: one `Initiative`, one each of `Investigation`/`Feature`/`Bug`, three `Task`s. | **Twig plan** (`publish-seed`) | §4.1 |
| 13 | Link the hierarchy with parent/child, then predecessor/successor, then related, then artifact links. | **Twig plan** (`add-link`, `batch`) | §4.2 |
| 14 | Capture per-surface evidence artifacts. | Automated (`twig show`, `twig tree`, headless browser) | §4.3 |
| 15 | Run the harness gate check. | Automated (script over the evidence bundle) | §5 |
| 16 | Ratify the profile document version. | Human, PR merge | §5 gating rule. |

Steps 12–14 are the only steps expressed as Twig plan operations. Everything numbered
2–9 is honestly a human step; no dishonest labelling of a UI step as "automated".

---

## Section 4 — Sandbox validation harness

### 4.1 Location and structure

The harness lives in the Twig repo under
`docs/reference-process/harness/<profile-version>/`. `<profile-version>` matches the
version string the profile document pins (owned by #732). Each version directory
contains:

```
docs/reference-process/harness/<version>/
├── README.md                     # human-authored: what was run, who, when
├── plan.yaml                     # the twig plan file executed against the sandbox
├── run-log.txt                   # stdout+stderr of the plan apply
└── evidence/
    ├── 00-sandbox-baseline.json          # from step 2.9
    ├── 01-initiative-backlog.json        # + .png
    ├── 02-investigation-work.json        # + .png
    ├── 03-feature-work.json              # + .png
    ├── 04-bug-work.json                  # + .png
    ├── 05-task-sprint.json               # + .png
    ├── 06-hierarchy-links.json           # + .png
    ├── 07-predecessor-successor.json     # + .png
    ├── 08-related-links.json             # + .png
    ├── 09-artifact-links.json            # + .png
    ├── 10-rank-before.json               # from twig tree
    ├── 10-rank-after.json                # from twig tree
    └── 10-rank-diff.txt                  # expected: empty
```

Each numbered file is the machine-consumable proof for one required observation
surface. The `.png` alongside is human-consumable secondary evidence (a screenshot of
the ADO backlog / board view rendering the seeded items). The `.json` proof is
authoritative — the harness gate reads JSON, not images.

### 4.2 Plan operations exercised

The plan file for a full harness run is small and expressed only in the five allowed
plan kinds. A representative shape:

```yaml
# plan.yaml — executed against Twig-Reference-Sandbox
version: 1
ops:
  # ---- step 12: seed the hierarchy ----
  - kind: publish-seed
    seed: sandbox/seeds/initiative.json          # type: Twig.Initiative
    alias: INIT
  - kind: publish-seed
    seed: sandbox/seeds/investigation.json       # type: Twig.Investigation
    alias: INV
  - kind: publish-seed
    seed: sandbox/seeds/feature.json             # type: Twig.Feature
    alias: FEAT
  - kind: publish-seed
    seed: sandbox/seeds/bug.json                 # type: Twig.Bug
    alias: BUG
  - kind: batch
    ops:
      - kind: publish-seed
        seed: sandbox/seeds/task-a.json          # type: Task, iterationPath: Sprint 1
        alias: TA
      - kind: publish-seed
        seed: sandbox/seeds/task-b.json
        alias: TB
      - kind: publish-seed
        seed: sandbox/seeds/task-c.json
        alias: TC
  # ---- step 13: exercise every link kind ----
  - kind: add-link
    from: "@INIT"
    to: "@INV"
    linkType: System.LinkTypes.Hierarchy-Forward     # child
  - kind: add-link
    from: "@INIT"
    to: "@FEAT"
    linkType: System.LinkTypes.Hierarchy-Forward
  - kind: add-link
    from: "@INIT"
    to: "@BUG"
    linkType: System.LinkTypes.Hierarchy-Forward
  - kind: add-link
    from: "@FEAT"
    to: "@TA"
    linkType: System.LinkTypes.Hierarchy-Forward
  - kind: add-link
    from: "@FEAT"
    to: "@TB"
    linkType: System.LinkTypes.Hierarchy-Forward
  - kind: add-link
    from: "@FEAT"
    to: "@TC"
    linkType: System.LinkTypes.Hierarchy-Forward
  - kind: add-link
    from: "@TA"
    to: "@TB"
    linkType: System.LinkTypes.Dependency-Forward    # predecessor/successor
  - kind: add-link
    from: "@INV"
    to: "@FEAT"
    linkType: System.LinkTypes.Related               # related
  - kind: add-link
    from: "@FEAT"
    to: "vstfs:///Git/Ref/{projectId}/{repoId}/GBharness"   # artifact link
    linkType: ArtifactLink
```

The plan runs unchanged against a fresh Sandbox; every op resolves against alias-only
references so no work-item IDs are hardcoded.

### 4.3 Required surfaces, evidence artifacts, capture recipe

| # | Surface | Capture recipe | Evidence artifact | Pass criterion |
|---|---------|----------------|-------------------|----------------|
| 1 | `Initiative` on Initiatives backlog | REST `GET /{project}/{team}/_apis/work/backlogs/Microsoft.EpicCategory/workItems` | `01-initiative-backlog.json` + `01-initiative-backlog.png` (backlog view) | JSON contains `@INIT`'s id; PNG shows it under "Initiatives" |
| 2 | `Investigation` on Requirements backlog | REST `GET /{project}/{team}/_apis/work/backlogs/Microsoft.RequirementCategory/workItems` | `02-investigation-work.json` + `.png` | JSON contains `@INV`'s id; PNG shows it under "Work" |
| 3 | `Feature` on Requirements backlog | Same REST call as row 2 | `03-feature-work.json` + `.png` | JSON contains `@FEAT`'s id |
| 4 | `Bug` on Requirements backlog | Same REST call | `04-bug-work.json` + `.png` | JSON contains `@BUG`'s id |
| 5 | `Task` on sprint board | REST `GET /{project}/{team}/_apis/work/teamsettings/iterations/{iterationId}/workitems` | `05-task-sprint.json` + `.png` (sprint task board) | JSON contains `@TA`/`@TB`/`@TC`; PNG shows the sprint task board rendering them under `@FEAT` |
| 6 | Native parent/child rendering | `twig tree @INIT -o json` + `twig show @INIT -o json` | `06-hierarchy-links.json` | JSON shows `@INIT` → {`@INV`, `@FEAT`, `@BUG`} → {`@TA`, `@TB`, `@TC`}; every link's `rel` is `System.LinkTypes.Hierarchy-Forward` from the parent side |
| 7 | Native predecessor/successor rendering | `twig show @TA -o json` and `twig show @TB -o json` | `07-predecessor-successor.json` | `@TA` has a `System.LinkTypes.Dependency-Forward` link to `@TB`; `@TB` has the reverse |
| 8 | Native related rendering | `twig show @INV -o json` | `08-related-links.json` | `@INV` has a `System.LinkTypes.Related` link to `@FEAT` |
| 9 | Artifact link | `twig show @FEAT -o json` | `09-artifact-links.json` | `@FEAT`'s relations contain an `ArtifactLink` matching the seeded branch vstfs URI |
| 10 | Rank preservation across publish/link | `twig tree @FEAT -o json` immediately after step 12's task publish, again after step 13's link ops | `10-rank-before.json`, `10-rank-after.json`, `10-rank-diff.txt` | The child order under `@FEAT` in `before` equals `after` (task publish order preserved through subsequent link mutations). `diff.txt` is empty. |

Every row in the table above corresponds to a surface #727 lists as "must be
observed". No surface is silently skipped. The evidence file names are canonical —
the harness gate script keys on them.

### 4.4 What the harness is *not*

- Not a Twig unit test. The Twig repo already has unit tests for
  `IProcessConfigurationProvider`; the harness lives beside them, not inside them.
- Not a live integration test. The harness runs on demand against a real Sandbox
  project by a human running the plan; it is not part of CI. Reference profiles
  change once per profile-version bump, and running against ADO requires an org.
- Not a replacement for `twig process description` diffing. The runtime check
  (owned by #732) is what compares a live process to the profile at command time.
  The harness proves the profile was born from an actual working configuration; it
  does not run every time Twig starts.

---

## Section 5 — Harness gating decision

### Decision

**The harness pass is a hard gate for publishing a new version of the reference
profile.** A `docs/reference-process/harness/<version>/` directory whose evidence
bundle is complete and green is a required precondition for merging a profile-version
bump into the Twig repo. This is enforced by:

1. A repo-side script (`tools/check-harness.ps1`) that fails PR checks if the
   `<version>` directory referenced by `#732`'s profile document is missing any of the
   ten canonical evidence filenames or if `10-rank-diff.txt` is non-empty.
2. A code-review rule captured in this design note: reviewers reject profile-version
   bumps whose harness bundle README does not identify the person, date, and Sandbox
   project id that produced it.

### What the gate is *not*

The harness is **not** a runtime gate. Twig core does not read
`docs/reference-process/harness/` at runtime; it validates the live process against
the profile document via `IProcessConfigurationProvider`. Making the harness a runtime
gate would (a) violate process-agnosticism (the harness contains process-specific data
of the one reference project) and (b) create a fragile runtime dependency on a
document that exists only in the source repo.

### Explicit deferrals

- The **exact shape of the profile-version bump PR check** (workflow name, script
  interface, failure messages) is deferred to the migration/tooling spec that #727
  already lists as out of scope. This design commits only that the check exists and
  what it verifies.
- Whether the harness must also verify **negative** cases (e.g. that assigning
  `System.TaskBacklogBehavior` to a non-Task type causes the profile check to fail)
  is deferred to the Sandbox validation spec (#734, T3). This design commits only to
  positive-case coverage — every #727 surface is observed to work; adversarial
  coverage is scoped to T3.

---

## Section 6 — `.conductor/process-config.yaml` retirement

### Current state

The file exists at `.conductor/process-config.yaml`, is 51 lines, and pins
`process_template: Basic` plus per-type capability lists, transition names, review
policies, branch strategy, and platform. It is consumed by the conductor SDLC
workflow, not by Twig core.

### What the reference profile absorbs

Once #732 lands, the profile document owns:

- `process_template` (subsumed by the profile's `parentProcessTypeId` pin).
- Per-type existence, backlog behavior, and state graph (subsumed by the profile's
  type/behavior/state declarations).
- Type transitions (`begin_planning`, `begin_implementation`, …) — these are now
  discovered via `IProcessConfigurationProvider` and mapped to semantic transitions
  in Twig core; the profile only pins that a semantic mapping exists.

### What the reference profile does *not* absorb

The following fields have no home in a process profile and must stay somewhere the
conductor can read:

- `review_policies` (agent_review / human_review / auto_merge per PR class).
- `branch_strategy` (feature_branch, planning_branch, merge_group_branch templates,
  target).
- `platform` (`github`).
- `capabilities` (`plannable`, `implementable`, `filing_eligible`), `max_nesting_depth`,
  and `decomposition_guidance` — these are **conductor-scoped policy on top of** the
  process's type set, not facts about the ADO process. They should move to a slimmer
  `.conductor/policy.yaml` (or equivalent) keyed by profile-declared type reference
  names, not by hardcoded strings.

### Retirement plan

**Deferred to the migration spec that #727 already lists as out of scope**, with the
following invariants pinned here so the migration cannot silently over- or
under-scope:

1. `.conductor/process-config.yaml` is not deleted by the work in this task-graph
   (#732–#735). It stays in place, unmodified, until the conductor is taught to
   consume the reference profile.
2. When the migration spec lands, it must (a) split the file into "profile
   references" (delete) and "conductor policy" (rename to `policy.yaml` or fold into
   the conductor workflow YAML), and (b) key the remaining policy fields on the
   profile-declared reference names, not on hardcoded `Epic`/`Issue`/`Task` strings.
3. Until the migration lands, the conductor may keep reading
   `process-config.yaml`. There is no interim compatibility shim; the file's
   hardcoded strings will simply drift out of sync with the reference profile if the
   Twig repo starts using the profile on any project other than Hyperbright — an
   acceptable drift because the conductor's use of this file is documented and
   scope-limited.

---

## Section 7 — `baseProcessVersion` content and bump rules

The profile document (owned by #732) carries a `baseProcessVersion` field whose
value is opaque to the profile schema. This design owns its content and its bump
rules.

### Content

`baseProcessVersion` is the string `basic:<yyyy-mm-dd>:<harness-version>` where:

- `basic` names the base ADO template chosen in §1.
- `<yyyy-mm-dd>` is the date the tailoring recipe in §2 was last modified in the
  Twig repo.
- `<harness-version>` matches the `<version>` directory name under
  `docs/reference-process/harness/<version>/` (§4.1) that most recently passed the
  harness gate.

### Bump rules

`baseProcessVersion` MUST be bumped when any of the following changes:

1. §1's base-process choice changes (e.g. switching parent from Basic to CMMI).
2. Any step in §2 is added, removed, reordered, or has its REST call shape
   changed such that a fresh Sandbox produces a materially different
   `twig process description` baseline.
3. The harness gate (§5) is re-run against a fresh Sandbox and produces a new
   `harness/<version>/` bundle.

`baseProcessVersion` MUST NOT be bumped for editorial changes to this note that
leave the recipe and the baseline unchanged. The distinction is behavioral, not
textual: if the tailoring output changes, bump; otherwise do not.

### Interaction with `profileVersion`

Both `profileVersion` (semver, owned by #732) and `baseProcessVersion` (opaque
string, owned here) are compared exact-match at profile load. A change to
`baseProcessVersion` without a `profileVersion` bump is a valid operation: the
profile document may need no schema-level change even when the underlying process
recipe evolves. A `profileVersion` bump without a `baseProcessVersion` bump is
similarly valid: schema refactors do not necessarily invalidate the process
recipe.

---

## Explicit out-of-scope for this design

The following are out of scope for #733 and belong to sibling or downstream tickets.
Restating them so #735 (T4 — implementation) does not accidentally over-scope:

- **Profile document schema, versioning, compatibility rules** — owned by #732 (T1).
- **Migration tooling from `.conductor/process-config.yaml`** — deferred (§6).
- **Automatic backlog resequencing** — out of scope per #727.
- **Types beyond the locked hierarchy** (`Objective, Outcome, Workstream, Spike,
  Change, Delivery Work, Wayfinder Task, Grilling, Prototype, Research, Map, Idea,
  Decision, Spec`) — explicitly not part of this reference profile per #727. The
  Hyperbright process's existing use of some of these names is orthogonal to the
  reference profile.
- **The runtime process-matching algorithm** — Twig core already has one via
  `IProcessConfigurationProvider`; this design does not alter it.
- **Adversarial / negative-case validation** — scoped to T3 (#734) per §5.
- **Twig plan verbs for ADO process customization** — Twig plan verbs remain
  `batch`, `add-link`, `remove-link`, `publish-seed`, `delete`. Adding a hypothetical
  `apply-process` plan verb is a separate design.

---

## Contract with sibling design #732

This design's `Twig.Initiative`, `Twig.Investigation`, `Twig.Feature`, `Twig.Bug`
strings are illustrative; the profile document owns the canonical reference-name
values. The following assertions are dependencies on #732 and MUST match its final
schema; if any disagrees on merge, this note's example plan.yaml is updated to match
the profile, not vice versa.

- The profile declares one parent process id (Basic), one process id (the profile's
  own), and one process display name.
- The profile declares four types with their reference names, their disable-state,
  and their backlog behavior reference.
- The profile declares the required state count (three) and the required link-kind
  references (`Hierarchy-Forward/Reverse`, `Related`, `Dependency-Forward/Reverse`,
  `ArtifactLink`).

Everything else — schema shape, compatibility rules, drift semantics — is owned by
#732 and referenced here only as "the profile document".

---

## References

- Spec #727 — Twig ADO reference process and profile.
- Sibling #732 (T1) — Profile document schema.
- `.github/copilot-instructions.md` — process-agnostic hard rule.
- `skill://ado-publish` — the five allowed plan-op kinds (`batch`, `add-link`,
  `remove-link`, `publish-seed`, `delete`).
- `skill://hyperbright-process` — evidence that the current live process is an
  inherited-from-Basic process with the three backlog behaviors this design
  reuses.
- `twig process description -o json` output on the current project — used as the
  baseline shape the profile document is compared against.
