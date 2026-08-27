# Twig reference profile — schema, versioning, and compatibility (T1, AB#732)

**Work Item:** #732 (Task, parent Spec #727 — "Twig ADO reference process and profile")
**Type:** Design (closes deferred schema/versioning question in #727)
**Status:** ⬛ Settled
**Plan Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive summary

This note settles the shape and rules of the **Twig reference profile** — the
versioned, machine-readable document Twig core matches against a live Azure
DevOps process to decide whether a checkout may run managed operations. It fixes:

- the document schema, field by field;
- the canonical location (embedded JSON resource in the Twig release, parsed
  through `TwigJsonContext`);
- the version identifiers carried on the profile, on `twig.json`, and on the
  base ADO process;
- the exact-match compatibility rules Twig core enforces at load time;
- the drift-detection contract — direct comparison plus canonical structural
  fingerprint — against the live ADO process, with every failure mode
  classified **load-time** or **command-time**;
- a compatibility matrix of concrete accept / reject rows keyed on inherited
  ADO process changes.

The profile is **process-agnostic in shape**: type names, state names, link
reference names, and backlog-behavior refs enter as opaque declared strings
inside a fixed schema. Twig core never hard-codes any of them, and the
schema itself is authored in **profile roles** (`initiative`, `investigation`,
`feature`, `bug`, `task`), never in ADO type names.

The tailoring transformation from a base ADO process to the target one, the
sandbox harness that validates it, and the choice of base process are **not**
in this note — those belong to the T2 base-process/harness design (AB#733)
and are referenced here by contract only.

---

## Background

### What #727 settled and what it deferred

Spec #727 fixed three constraints and deferred the rest of the schema:

| # | Settled constraint | Effect on this note |
|---|--------------------|--------------------|
| S1 | Exact live-process matching. | Compatibility policy is exact-match; §6. |
| S2 | Embedded reference profile and exact `twig.json` version pin. | Canonical location is the embedded JSON resource; §4. `twig.json` pins one exact profile identity + version; §5.1. |
| S3 | Tailoring separated from the reference profile. | The profile carries opaque references to what the harness produced; the harness (T2) owns their content. §4.5, §5.3. |

Deferred to this note (T1): schema field layout, drift-detection contract,
version identifier shapes, compatibility policy including the concrete
accept/reject cases, and the declaration inventory the T3 lookup seam (AB#734)
and T4 shipped-profile artifact (AB#735) must consume.

### What the codebase already fixes

`IProcessConfigurationProvider.GetConfiguration()` (`src/Twig.Domain/Interfaces/IProcessConfigurationProvider.cs`)
is the sole runtime entry point for process metadata. It returns a
`ProcessConfiguration` aggregate built dynamically from the SQLite
`process_types` cache; every downstream service — `StateCategoryResolver`,
`FlowTransitionService`, `HintEngine`, `MutationTools` — reaches process
knowledge only through it. This note does **not** reshape that provider;
the T3 (AB#734) lookup seam sits **alongside** it and answers a different
question (*"what does the reference profile require?"*), never *"what is
in the live process right now?"*.

Existing types this note re-uses verbatim:

- `WorkItemType` (`src/Twig.Domain/ValueObjects/WorkItemType.cs`) — opaque
  string wrapper; its listed statics are advisory, per its own doc comments,
  so nothing about naming a role `feature` here binds to `WorkItemType.Feature`.
- `StateCategory` (`src/Twig.Domain/Enums/StateCategory.cs`) — the exact
  5-category enum Twig core reasons in.
- `StateEntry` (`src/Twig.Domain/ValueObjects/StateEntry.cs`) — the
  `(Name, Category, Color)` triple already cached in `process_types`.
- `ProcessConfigurationData` and `BacklogLevelConfiguration`
  (`src/Twig.Domain/ValueObjects/ProcessConfigurationData.cs`) — how the
  provider models ADO backlog levels; a profile expresses backlog assignment
  in these same terms.
- `LinkTypeMapper` (`src/Twig.Domain/Services/Navigation/LinkTypeMapper.cs`)
  — the friendly ↔ ADO reference-name mapping that already exists for the five
  link kinds this profile speaks of. The profile declares *meaning*; the
  mapper stays the sole place ADO reference names appear.

### The T2 contract this note commits to

Per the sibling design (AB#733), T2 owns the base ADO process and the
tailoring harness. The profile treats what T2 produces as opaque:

- `baseProcess.parentRef` — a string identifier T2 chooses (T2 has selected
  ADO **Basic** for the initial reference profile). This note never inspects
  its value; it is compared byte-equal against the live process's parent
  reference at load time.
- `baseProcess.tailoringVersion` — a string T2 versions and bumps. Same rule.
- `backlogLevels[*].behaviorRef` — the ADO backlog-behavior refs the tailoring
  installed (T2 declared: `EpicBacklogBehavior` for the Initiative tier,
  `RequirementBacklogBehavior` for the Investigation/Feature/Bug tier,
  `TaskBacklogBehavior` for the Task tier). This note never invents them.

Whatever names those tiers's *types* eventually carry in a real installation
is declared **inside** this profile (§5.3) — the harness only guarantees that
the tailoring produces three declarable middle-tier types plus an apex type
plus one leaf type.

---

## Locked vocabulary (shared with T2)

Both design notes commit to these definitions; the profile schema is
authored in these words.

| Term | Meaning |
|---|---|
| **Role** | A profile-level abstract identity: exactly one of `initiative`, `investigation`, `feature`, `bug`, `task`. All profile-level rules speak in roles; type names are declared bindings. |
| **Hierarchy** | `initiative → { investigation, feature, bug } → task`. Locked. The profile rejects any live process that cannot host this exact three-tier shape. |
| **Sprint-entry-only-for-`task`** | `task` is the sole role permitted on the iteration/sprint execution backlog. Enforced by Twig core against the profile's declared `backlogRole` for each type. |
| **`parent/child`** | Decomposition edge. ADO carriers: `System.LinkTypes.Hierarchy-Reverse` (parent), `System.LinkTypes.Hierarchy-Forward` (child). |
| **`predecessor/successor`** | Blocking / sequencing edge. ADO carriers: `System.LinkTypes.Dependency-Reverse` (predecessor), `System.LinkTypes.Dependency-Forward` (successor). |
| **`related`** | Informs (nonblocking influence). ADO carrier: `System.LinkTypes.Related`. |
| **Artifact link** | Primary evidence carrier — pull request, build, commit, wiki, or attachment. Meaning is fixed; per-artifact-kind attribute schema is out of scope for this note (see §9). |
| **Primary scope** | The single ADO work item a managed worktree is currently organized around; see AB#728 / AB#736. This profile publishes only the *role* allow-set that determines eligibility. |

---

## Problem statement

Without this note, Twig core has three unbounded surfaces:

1. **What is a profile?** Every consumer (`IPrimaryScopePolicySource`,
   `IProfileRegistrySource`, the future `IReferenceProfileProvider` seam)
   would invent its own answer.
2. **When does Twig accept a profile against a live ADO process?** The
   settled "exact match" constraint has no operational meaning until each
   comparison axis is enumerated.
3. **What does #735 ship?** The shipped artifact is either compact and
   opaque or wide and structural. Without a declared schema, it drifts.

This note closes all three by pinning **one** document schema, **one**
compatibility policy, and **one** version identifier shape that #734 and
#735 consume verbatim.

---

## Goals and non-goals

### Goals

- **G1** — Specify the profile document field-by-field, including field
  types, cardinalities, and required/optional status.
- **G2** — Choose the canonical location and justify it against the AOT +
  `JsonSerializerIsReflectionEnabledByDefault=false` constraint.
- **G3** — Define `profileIdentity`, `profileVersion`, and `baseProcessVersion`
  shapes; specify the `twig.json` pin format that exact-matches all three.
- **G4** — Define the load-time compatibility rules Twig core applies.
- **G5** — Define drift detection between the reference profile and the live
  ADO process, splitting failures into load-time vs command-time.
- **G6** — Publish a compatibility matrix of concrete accept/reject rows,
  so #734 tests and #735 sample profiles have an authoritative reference.
- **G7** — Enumerate every declaration the T3 seam must expose (a checklist
  #734 consumes) and every version identifier T4 must ship (a checklist
  #735 consumes).

### Non-goals

- **NG1** — Base ADO process choice, tailoring sequence, sandbox harness
  (AB#733).
- **NG2** — Runtime seam wiring — what interface, in what DI graph, with
  what caching (AB#734).
- **NG3** — The shipped profile blob itself, its packaging, and the
  `dotnet publish` inclusion (AB#735).
- **NG4** — Migration tooling from any prior `.conductor/process-config.yaml`,
  automatic backlog resequencing (both explicitly deferred by #727).
- **NG5** — Remote-dependency link kinds (out of scope until an artifact
  model lands; see §9).
- **NG6** — Claim field bindings — those live with the local-first claim
  spec (AB#728) and its downstream storage designs (AB#736 / AB#737).
- **NG7** — Any of these types: `Objective, Outcome, Workstream, Spike,
  Change, Delivery Work, Wayfinder Task, Grilling, Prototype, Research,
  Map, Idea, Decision, Spec`. Excluded by #727.

---

## Proposed design

### 1. Canonical location

The reference profile is a **JSON document embedded as an assembly resource
in the Twig release**, parsed at process start through `TwigJsonContext`.

```
src/Twig.Infrastructure/Resources/ReferenceProfile/
├── profile.json           # the profile document (§5)
└── profile.json.sha256    # covered by §7.3 embedded fingerprint
```

Both files ship as `<EmbeddedResource>` entries in
`Twig.Infrastructure.csproj`; the `.sha256` is computed at build time and
covers `profile.json` byte-exact.

**Rationale (justifies each of the three location choices settled by #727):**

- **Repository-hosted file, not an ADO-hosted asset.** Twig core must load
  the reference profile before it opens any authenticated ADO connection;
  an offline `twig show`, `twig set`, or `twig status` still consults the
  profile. An ADO-hosted asset would create an authentication and
  latency dependency on read-only paths that must not have one.
- **Assembly resource, not an in-repo runtime path.** `PublishAot=true` +
  `TrimMode=full` inline every dependency; a filesystem path relative to
  the binary is fragile across `dotnet tool install`, `dotnet publish`,
  and single-file publish. An embedded resource survives all three
  without a `TrimmerRootDescriptor` fight, and the AOT compiler treats
  it as a blob rather than reachable code.
- **JSON, not compile-baked constants.** Reviewing a profile change in a
  PR must remain a diff of a human-readable file. Compile-baked constants
  would obscure the review surface and force a code-generator maintenance
  burden with no offsetting benefit — the runtime already source-generates
  its serializer.
- **`TwigJsonContext`, not a hand-written parser.**
  `JsonSerializerIsReflectionEnabledByDefault=false` (see
  `.github/copilot-instructions.md` line 8) prohibits reflection-based
  deserialization; every profile record type in §5 is registered as
  `[JsonSerializable]` in `TwigJsonContext`. This is a T4 responsibility
  to actually add; this note pins the requirement.

The document is loaded once per process, via the T3 seam (AB#734), which
holds it behind an `IReferenceProfileProvider` returning an immutable
in-memory `ReferenceProfile` aggregate. No component consumes the JSON
directly.

### 2. Version identifier shape

Three identifiers cover the three axes of change; each is exact-matched.

| Identifier | Owned by | Format | Change discipline |
|---|---|---|---|
| `profileIdentity` | T1 (this note) | Reverse-DNS opaque string, `twig.reference-profile.<name>`. Initial value: `twig.reference-profile.hyperbright`. | Immutable per identity. A rename means a new identity, not a version bump. |
| `profileVersion` | T1 (this note) | SemVer `MAJOR.MINOR.PATCH`. Initial value: `1.0.0`. | MAJOR bump for any schema field addition/removal or any change that would flip an accepted profile→process pair to rejected; MINOR bump for a documentation-only clarification that changes canonical fingerprint whitespace but not compared bytes; PATCH bump for a fix that changes neither the schema nor the semantics. In practice the exact-match rule collapses MAJOR/MINOR/PATCH into "any change requires a repository bump", but the segments carry review intent. |
| `baseProcess.tailoringVersion` | T2 (AB#733) | Opaque string; T2 has locked the format as `basic:<yyyy-mm-dd>:<harness-version>` (e.g. `basic:2026-08-24:1`). This note never parses it. | Bumped by T2 whenever the harness produces a different tailoring output; that bump forces a fresh profile release and a fresh `twig.json` pin. |

The **`twig.json` pin** carries all three:

```jsonc
{
  "$schema": "twig.json/v1",
  "version": 1,
  "profile": {
    "identity":            "twig.reference-profile.hyperbright",
    "profileVersion":      "1.0.0",
    "baseProcessVersion":  "basic:2026-08-24:1"
  }
}
```

(The rest of `twig.json` — `connection`, `defaults`, `policy` — is owned
by the AB#736 storage design and unchanged here. §4.1 of that design
already carries `profile.identity` and `profile.version` as opaque
strings; this note commits to a **three-field** `profile` block. #736's
existing shape was two-field; T4 (AB#735) is responsible for the
`profile` block's shape landing in code — see §8.)

**Why three and not two.** Collapsing `profileVersion` and
`baseProcessVersion` into one string ties T1 and T2 releases; every
correction on either side would rev the other. Keeping them separate
makes T1↔T2 the same kind of contract as
`Twig.Domain`↔`Twig.Infrastructure`: independently versioned, exact-matched
at load.

### 3. Profile document schema

Field-by-field. The complete JSON follows the layout below; every property
is required unless marked "optional".

```jsonc
{
  "$schema": "twig-reference-profile/v1",     // string; profile-format version
  "identity": "twig.reference-profile.hyperbright",   // §2
  "profileVersion": "1.0.0",                  // §2

  "baseProcess": {                             // §3.1
    "parentRef": "<opaque T2-declared string>",
    "tailoringVersion": "<opaque T2-declared string>"
  },

  "hierarchy": {                               // §3.2 — declared for review; validated to match the locked vocabulary
    "apex":       ["initiative"],
    "requirement":["investigation", "feature", "bug"],
    "leaf":       ["task"]
  },

  "types": [                                   // §3.3 — one entry per role
    {
      "role":        "initiative",             // enum: initiative|investigation|feature|bug|task
      "typeName":    "<opaque declared type name>",
      "backlogRole": "portfolio",              // enum: portfolio|requirement|task
      "backlogBehaviorRef": "EpicBacklogBehavior",   // opaque — T2's binding
      "states": [                              // §3.4 — ordered, one per accepted state
        { "name": "<opaque declared state name>",
          "category": "Proposed" },            // StateCategory enum
        …
      ]
    },
    …
  ],

  "linkKinds": [                               // §3.5 — one entry per accepted edge
    {
      "kind":       "parent-child",
      "meaning":    "decomposition",
      "forwardRel": "System.LinkTypes.Hierarchy-Forward",
      "reverseRel": "System.LinkTypes.Hierarchy-Reverse"
    },
    { "kind": "predecessor-successor", "meaning": "blocking-sequencing",
      "forwardRel": "System.LinkTypes.Dependency-Forward",
      "reverseRel": "System.LinkTypes.Dependency-Reverse" },
    { "kind": "related",  "meaning": "informs",
      "forwardRel": "System.LinkTypes.Related", "reverseRel": null },
    { "kind": "artifact", "meaning": "evidence",
      "forwardRel": null, "reverseRel": null,
      "artifactCategory": "any"                // opaque; §9 non-goal for shapes
    }
  ],

  "primaryScope": {                            // §3.6 — AB#728 policy hook
    "kind": "ado-workitem",
    "eligibleRoles": ["initiative", "investigation", "feature", "bug", "task"]
  },

  "fingerprint": {                             // §7.3 — canonical structural fingerprint
    "algorithm": "twig-profile-fp/v1",
    "bytes":     "<64-char lowercase hex>"
  }
}
```

#### 3.1 `baseProcess`

Two opaque strings, owned by T2. Twig core stores them and compares them
byte-equal against what the live process reports (see §6.2). Twig core
never parses either string.

#### 3.2 `hierarchy`

Declared for reviewer clarity; validated at profile-load to exactly equal
the locked vocabulary:

- `apex` MUST be `["initiative"]`;
- `requirement` MUST be `["investigation", "feature", "bug"]` (set-equality, order preserved);
- `leaf` MUST be `["task"]`.

A profile that fails this check is a **build-time** authoring bug that
Twig fails on the first `IReferenceProfileProvider` call — the block
exists only to make the check reviewable, not extensible. §7.1 classifies
this as `hierarchy-locked-vocabulary-violation`, load-time.

#### 3.3 `types`

Exactly five entries — one per role. Each declares:

- `role` — one of the five vocabulary roles. The set of `role` values MUST
  equal `{initiative, investigation, feature, bug, task}` exactly.
- `typeName` — the string ADO uses for this type in the live process
  (e.g. `Initiative`, `Investigation`, `Feature`, `Bug`, `Task`). Compared
  case-insensitive against `System.WorkItemType` values, matching
  `WorkItemTypeComparer` in `ProcessConfiguration.cs`. This string is
  what the T3 seam surfaces when a caller asks "what type name is the
  `feature` role on this profile?"; it never leaks to telemetry.
- `backlogRole` — `portfolio`, `requirement`, or `task`. This is Twig's
  abstract backlog-tier name. The `initiative` role's `backlogRole` is
  `portfolio`; the three middle-tier roles get `requirement`; the `task`
  role gets `task`. This is validated against the locked hierarchy
  (§3.2) — a mismatch is `backlog-role-tier-mismatch`, load-time.
- `backlogBehaviorRef` — T2-declared opaque string. Compared against the
  live process's declared backlog-behavior refs at command-time; see
  §7.2.
- `states` — ordered list of `(name, category)` pairs, one per accepted
  live state. `category` is the domain `StateCategory` (`Proposed`,
  `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`). Compared
  set-equal by name (case-insensitive per `StatePairComparer`) and
  category-equal per-entry at command-time; see §7.2.

Sprint-entry enforcement (§Locked vocabulary): Twig core rejects any
non-`task` role from the iteration/sprint execution path by looking up
role by `backlogRole == "task"`. The lookup is answered by the reference
profile, not by the live process, so the invariant holds even if the
live process is misconfigured.

#### 3.4 `states`

Same `StateEntry`-shaped triple `process_types` already caches, minus the
color (Twig core does not reason about color — that stays a rendering
concern owned by the live process metadata via `ProcessTypeRecord.ColorHex`).
Ordering is part of the record: state ordering influences transition
classification in `ProcessConfiguration.BuildTypeConfig` (`src/Twig.Domain/Aggregates/ProcessConfiguration.cs:237-263`) and MUST match live
ordering at command-time; §7.2 classifies mismatched order as
`state-order-mismatch`, command-time (because that check requires
`IProcessConfigurationProvider` to be initialized).

#### 3.5 `linkKinds`

Exactly four entries, one per vocabulary edge:

| `kind` | `meaning` | `forwardRel` | `reverseRel` |
|---|---|---|---|
| `parent-child` | `decomposition` | `System.LinkTypes.Hierarchy-Forward` | `System.LinkTypes.Hierarchy-Reverse` |
| `predecessor-successor` | `blocking-sequencing` | `System.LinkTypes.Dependency-Forward` | `System.LinkTypes.Dependency-Reverse` |
| `related` | `informs` | `System.LinkTypes.Related` | `null` |
| `artifact` | `evidence` | `null` | `null` |

The `System.LinkTypes.*` reference names are the *only* ADO strings this
profile contains — they are ADO's own well-known link-type refs, present
today in `LinkTypeMapper` and `AdoResponseMapper`, and not a
process-template opinion. Their meaning is fixed by ADO and by the
locked vocabulary, so declaring them here is not a hard-coded process
assumption — it is the canonical way to say "these three edges are the
carriers Twig understands".

The `artifact` entry has no ADO relation ref because ADO carries artifact
links via `ArtifactLink` relation attributes rather than a link-type
reference name. The `artifactCategory` field is opaque with initial
value `"any"`; per-artifact-kind attribute schemas (pull-request,
build, commit) are out of scope — see §9.

#### 3.6 `primaryScope`

Feeds `IPrimaryScopePolicySource` (already present in AB#738's design):

- `kind` — string, `"ado-workitem"`. Reserved for future non-ADO primary
  scopes. AB#737's `PrimaryScopeKinds.AdoWorkItem` binds against this.
- `eligibleRoles` — allow-set of profile roles. This note declares the
  initial value as `["initiative","investigation","feature","bug","task"]`
  (every role is eligible — the discovery record §Primary scope
  attachment decision matches this). A repository fork that wants to
  narrow the allow-set publishes a different profile with a different
  `profileIdentity`; narrowing is not a version bump.

The allow-set is authored in **roles**, not type names. `IPrimaryScopePolicySource.GetAllowSet()` returns the concrete type-name allow-set
by resolving each role through `types[*]` (§3.3) — that resolution lives
in the T3 seam, not here.

---

## `twig.json` pin (§5.1 fold-in)

The reference profile is coupled to the checked-in repo state by the
three-field `profile` block introduced in §2. `twig.json` is committed;
so any profile change requires a code review of both the shipped profile
diff (T4 releases it) and the repo-side pin bump (repository owner
commits it). There is no automatic upgrade path — per #727 settled
constraint S2 and the discovery record "Reference profile version pin"
decision.

Interactions with AB#736's checked-in `twig.json` design:

- The `profile.identity` field name in this note is **the same field
  slot** #736 §4.1 already sketched as opaque `profile.identity`. This
  note refines its semantics: the value MUST byte-equal a `profileIdentity`
  of a profile embedded in the running Twig binary; no other value is
  accepted.
- The `profile.version` slot in #736 §4.1 is renamed here to
  `profile.profileVersion` and joined by `profile.baseProcessVersion`.
  T4 (AB#735) is responsible for landing the two-field split when it
  ships the pin migration; #736 was written before this note existed.

---

## Compatibility rules

Twig core accepts a profile against a live process when **every** rule
below holds. A single failure is a hard reject — no partial acceptance,
no "warnings", per #727 S1.

### 6.1 Pin match (load-time)

- `twig.json.profile.identity`         == embedded `profileIdentity` for
  exactly one profile in the release binary. If zero: `profile-identity-unknown`.
  If more than one: build-time impossible (see §7.3 fingerprint).
- `twig.json.profile.profileVersion`   == embedded `profileVersion` byte-equal.
  Mismatch: `profile-version-mismatch`.
- `twig.json.profile.baseProcessVersion` == embedded
  `baseProcess.tailoringVersion` byte-equal. Mismatch:
  `base-process-version-mismatch`.

All three checks run before any ADO call. All three are **load-time**.

### 6.2 Process identity match (command-time — first check that consults `IProcessConfigurationProvider`)

- Live process `parentProcessRef` (already reachable via
  `AdoProcessConfigurationResponse`; see the DTOs `AdoProcessWorkItemTypeResponse` /
  `AdoProcessTypeFieldResponse` in `TwigJsonContext`) MUST byte-equal
  `baseProcess.parentRef`. Mismatch: `base-process-parent-mismatch`, command-time.

### 6.3 Type presence and role binding (command-time)

For each profile `types[*]` entry:

- `typeName` MUST exist as a `WorkItemType` key in
  `ProcessConfiguration.TypeConfigs`. Missing: `type-name-missing`,
  command-time.
- The live type's declared `backlogBehaviorRef` MUST byte-equal the
  profile's. Mismatch: `backlog-behavior-mismatch`, command-time.
- The live type's `backlogRole` (derived from
  `BacklogHierarchyService.GetTypeLevelMap`) MUST resolve to the
  profile's `backlogRole` after mapping (`portfolio` = level 0,
  `requirement` = level `PortfolioBacklogs.Count`, `task` = last level).
  Mismatch: `backlog-tier-mismatch`, command-time.

### 6.4 State-map match (command-time)

For each profile `types[*].states`:

- `states[*].name` set-equality (case-insensitive) against
  `TypeConfig.StateEntries[*].Name`. A superset (live has extra states):
  `live-has-extra-state`. A subset (profile has extra states):
  `profile-has-extra-state`. Both command-time.
- Per-state `category` match (`StateEntry.Category` equality). Mismatch:
  `state-category-mismatch`, command-time.
- `states[*]` **order** MUST match the live order. Mismatch:
  `state-order-mismatch`, command-time. (This is why the states array is
  ordered — see §3.4.)

### 6.5 Link-kind availability (load-time — no ADO call needed)

The four `linkKinds[*]` entries MUST have exactly the reference names in
§3.5. Twig core does not depend on ADO reporting these; they are the
platform's well-known refs. A mutated `linkKinds` array is a shipped-blob
integrity failure, caught by the §7.3 embedded fingerprint at load —
`profile-fingerprint-mismatch`.

### 6.6 Primary-scope allow-set (load-time — profile only)

`primaryScope.eligibleRoles` MUST be a non-empty subset of the five
vocabulary roles. Empty: `primary-scope-empty-allow-set`. Unknown role:
`primary-scope-unknown-role`. Both load-time; both are shipped-blob
integrity failures (fingerprint-covered).

---

## Drift-detection contract

### 7.1 Load-time failure inventory

These fire before any ADO request. Every one is a hard fail with a named
error identifier the T3 seam surfaces on `Result.Error`.

| Identifier | Trigger | Recovery hint (user) |
|---|---|---|
| `profile-blob-not-found` | Resource missing from the loaded assembly. | Reinstall twig. |
| `profile-fingerprint-mismatch` | §7.3 embedded fingerprint fails. | Reinstall twig. |
| `profile-schema-invalid` | JSON does not deserialize under the source-generated context (missing required field, wrong type, unknown role). | Reinstall twig. |
| `hierarchy-locked-vocabulary-violation` | §3.2 check fails. | Reinstall twig. |
| `role-set-not-canonical` | `types[*].role` set != the five roles. | Reinstall twig. |
| `link-kinds-not-canonical` | §3.5 table not matched exactly. | Reinstall twig. |
| `primary-scope-empty-allow-set` | §3.6 empty. | Reinstall twig. |
| `primary-scope-unknown-role` | §3.6 unknown value. | Reinstall twig. |
| `profile-identity-unknown` | `twig.json.profile.identity` not in the embedded index. | Bump `twig.json` to a supported profile identity. |
| `profile-version-mismatch` | §6.1. | Align `twig.json.profile.profileVersion` with the installed twig. |
| `base-process-version-mismatch` | §6.1. | Align `twig.json.profile.baseProcessVersion` with the installed twig. |
| `twig-json-profile-block-missing` | The three-field `profile` block is absent from `twig.json`. | Run `twig init` (managed) or hand-write the pin per §5. |

### 7.2 Command-time failure inventory

These fire the first time a command consumes `IProcessConfigurationProvider`
alongside the reference profile. They are also hard fails, but the
retry surface is different — the user can plausibly repair ADO, whereas
§7.1 failures require reinstall or config edit.

| Identifier | Trigger | Recovery hint (user) |
|---|---|---|
| `base-process-parent-mismatch` | §6.2. | Re-run the T2 harness (AB#733) on this ADO project, or bump the profile pin to one built from this project's base process. |
| `type-name-missing` | §6.3. | Run the harness on this project — a role is unbound. |
| `backlog-behavior-mismatch` | §6.3. | Same. |
| `backlog-tier-mismatch` | §6.3. | Same. |
| `live-has-extra-state` | §6.4. | Retire the extra state or bump the profile. |
| `profile-has-extra-state` | §6.4. | Introduce the missing state in ADO or bump the profile. |
| `state-category-mismatch` | §6.4. | Re-run the harness — a state's category shifted. |
| `state-order-mismatch` | §6.4. | Same. |
| `live-fingerprint-mismatch` | §7.3 live fingerprint deviates from the embedded copy. | Re-run the harness, then bump the profile. |

The load-time / command-time split is **not** cosmetic: load-time checks
guarantee that any read-only path (`twig show`, `twig set`, `twig status`,
tab-completion, MCP `twig_workspace`) can produce a coherent
"unrepairable" error without ever opening an ADO connection.

### 7.3 Canonical structural fingerprint

Two independent fingerprint uses:

**Embedded blob fingerprint (load-time).** The build embeds the SHA-256
of `profile.json` as `fingerprint.bytes` inside the profile itself.
Because the field is inside the file it hashes, the algorithm is:

```
canonical_bytes  = the profile JSON with fingerprint.bytes set to the empty string
fingerprint.bytes = SHA-256(canonical_bytes) as lowercase hex
```

The T3 seam recomputes this at load and compares. Mismatch:
`profile-fingerprint-mismatch`, load-time. Purpose: catches release
tampering and half-updated build artifacts.

**Live process fingerprint (command-time).** Twig computes a structural
fingerprint of the live process by feeding the following into SHA-256:

1. `baseProcess.parentRef`
2. For each role in canonical role order (`initiative`, `investigation`,
   `feature`, `bug`, `task`):
   - `typeName` lowercased
   - `backlogBehaviorRef`
   - `backlogRole`
   - `states[*].name.ToLowerInvariant() + "|" + states[*].category` joined
     by `\n` in declaration order
3. `linkKinds[*]` in §3.5 declaration order (`kind + "|" + forwardRel + "|" + reverseRel`, `null` rendered as the empty string)

The profile's `fingerprint.bytes` MUST match this recomputation over the
profile's own contents; any live-process value that yields a different
result is `live-fingerprint-mismatch`. Purpose: guarantees §6.2–§6.4
enumerated checks cannot miss an anomaly that shifted the process shape
along a dimension one of those checks does not cover in isolation. The
enumerated checks fire first for diagnostic clarity; the fingerprint is
the final backstop.

---

## Compatibility matrix — worked examples

Every row is a **concrete inherited-process change** applied to the
Basic-derived Hyperbright process (T2's initial target); the profile is
the same version throughout. Accept means the profile still loads and
commands run; reject names the exact identifier from §7.1 / §7.2.

| # | Scenario | Verdict | Failure identifier |
|---|---|---|---|
| 1 | The ADO admin renames the `Feature` type's `New` state to `Proposed` while keeping the state's `StateCategory=Proposed`. Profile still names `New`. | ❌ Reject | `live-has-extra-state` + `profile-has-extra-state` (name set differs). |
| 2 | Same as #1 but the admin also bumps the tailoring and T2 ships a new base-process version + a new profile PATCH. Repository bumps `twig.json.profile.baseProcessVersion` to match. | ✅ Accept | — |
| 3 | The ADO admin *adds* a `Blocked` state to `Feature` with `StateCategory=InProgress`, above `Active`. Profile has no `Blocked`. | ❌ Reject | `live-has-extra-state`. |
| 4 | The ADO admin *reorders* `Feature` states — moves `Resolved` before `Active`. Profile keeps original order. | ❌ Reject | `state-order-mismatch`. |
| 5 | The ADO admin renames the `Bug` **type** to `Defect`. Profile still declares `Bug`. | ❌ Reject | `type-name-missing` for the `bug` role. |
| 6 | The ADO admin changes the `Task` backlog behavior ref (e.g. from `TaskBacklogBehavior` to a custom one with the same underlying meaning). Profile still declares `TaskBacklogBehavior`. | ❌ Reject | `backlog-behavior-mismatch`. |
| 7 | The ADO admin *demotes* `Investigation` from the Requirements backlog to the Tasks backlog. Profile still declares `requirement`. | ❌ Reject | `backlog-tier-mismatch`. |
| 8 | The ADO admin adds a new state to `Task` with `StateCategory=Removed`. Profile does not enumerate it. | ❌ Reject | `live-has-extra-state`. (No implicit accept for "just another removed state" — exact match is exact.) |
| 9 | A repository has no `profile` block in `twig.json`. | ❌ Reject | `twig-json-profile-block-missing` (load-time). |
| 10 | Repository declares `twig.json.profile.identity = twig.reference-profile.acme`. The running Twig ships only `twig.reference-profile.hyperbright`. | ❌ Reject | `profile-identity-unknown` (load-time). |
| 11 | Repository declares `twig.json.profile.profileVersion = 1.0.1`. The running Twig ships `1.0.0`. | ❌ Reject | `profile-version-mismatch` (load-time). |
| 12 | Repository declares `twig.json.profile.baseProcessVersion = basic:2026-08-24:1`, matching the shipped profile, but the live ADO process was re-tailored yesterday to `basic:2026-08-25:1` without a shipped profile update. | ❌ Reject | `base-process-parent-mismatch` OR `live-fingerprint-mismatch`, whichever T3 checks first (see §7.2 ordering note). |
| 13 | Twig ships two profiles: `hyperbright@1.0.0` and `hyperbright@1.0.1`. Repository pins `1.0.0`. Live process matches `1.0.0` fingerprint exactly. | ✅ Accept | — (multiple embedded profiles is a normal shipping shape; §6.1 selects by identity+version.) |
| 14 | Same as #13, but repository pins `1.0.1` and the live process still matches `1.0.0`. | ❌ Reject | `live-fingerprint-mismatch`. |

The matrix intentionally shows the "minor inherited-process change is
not silently accepted" property #727 requires. Every ADO-side change to
the tailored process forces a T2 harness re-run and a co-ordinated T1
release; there is no implicit "compatible-with" range.

---

## Impact on adjacent components

### 8.1 Declaration inventory for T3 (AB#734 — profile-lookup seam)

The T3 seam MUST expose exactly these queries. Each maps 1:1 to a §3
field; #734 has no authoring latitude on this list — this is the
consumption contract.

| Query on `IReferenceProfileProvider` | Returns | Backed by |
|---|---|---|
| `Identity` | `string` | §3 `identity` |
| `ProfileVersion` | `string` | §3 `profileVersion` |
| `BaseProcess` | `(string parentRef, string tailoringVersion)` | §3.1 |
| `Hierarchy` | `(IReadOnlyList<Role> apex, requirement, leaf)` | §3.2 |
| `TypeByRole(role)` | `(string typeName, string backlogRole, string backlogBehaviorRef, IReadOnlyList<StateEntry> states)` | §3.3 |
| `RoleByTypeName(name)` | `Role?` (nullable when the live type is unbound) | §3.3 reverse index |
| `LinkKinds` | `IReadOnlyList<(LinkKind, meaning, forwardRel, reverseRel)>` | §3.5 |
| `PrimaryScopeAllowSet` | `(string kind, IReadOnlyList<Role> eligibleRoles)` | §3.6 |
| `PrimaryScopeAllowTypeNames` | `IReadOnlyList<string>` (materialized by joining §3.6 through §3.3) | Derived |
| `EmbeddedFingerprint` | `string` (lowercase hex) | §3 `fingerprint.bytes` |
| `ComputeLiveFingerprint(IProcessConfigurationProvider)` | `string` | §7.3 |
| `ValidateAgainstLiveProcess(IProcessConfigurationProvider)` | `Result` with named errors from §7.2 | §6.2–§6.4 |

`Role` is a new domain enum introduced by T3: `Initiative`, `Investigation`,
`Feature`, `Bug`, `Task`. `LinkKind` is a new enum: `ParentChild`,
`PredecessorSuccessor`, `Related`, `Artifact`. Both live in `Twig.Domain/
ValueObjects`. Neither is a `WorkItemType` — they are profile-level, not
ADO-level.

The seam MUST cache the loaded profile per process (single load); T3 has
no repeat-load requirement. Twig-CLI is short-lived, matching the
`DynamicProcessConfigProvider` pattern (`src/Twig.Domain/Services/Process/DynamicProcessConfigProvider.cs`).

The existing `IPrimaryScopePolicySource` (AB#738) becomes a thin adapter
over `IReferenceProfileProvider.PrimaryScopeAllowTypeNames` — no
independent policy source remains.

The existing `IProfileRegistrySource` (AB#738) is what today's `twig
init` uses to look up `(identity, version, allow-set)`. Once T3 lands
that seam collapses into `IReferenceProfileProvider` too. The T3 note is
responsible for the cutover.

### 8.2 Version identifier inventory for T4 (AB#735 — shipped reference profile)

T4 MUST ship every one of these in the release binary. Missing any is a
build break — this note is the reference.

| Where | Field | Format | Bump discipline |
|---|---|---|---|
| Embedded `profile.json` | `identity` | reverse-DNS opaque string | Immutable per identity. |
| Embedded `profile.json` | `profileVersion` | SemVer `MAJOR.MINOR.PATCH` | §2. |
| Embedded `profile.json` | `baseProcess.parentRef` | Opaque, T2-owned | Bumped by T2. |
| Embedded `profile.json` | `baseProcess.tailoringVersion` | Opaque, T2-owned | Bumped by T2. |
| Embedded `profile.json` | `fingerprint.bytes` | 64-char lowercase hex | Recomputed on every build. |
| Registered in `TwigJsonContext` | `ReferenceProfile` aggregate root type + every §5 nested record | (source-generated) | Added when T4 lands. |
| `twig.json` schema | `profile.identity` | string | Bumped per repository. |
| `twig.json` schema | `profile.profileVersion` | SemVer string | Bumped per repository. |
| `twig.json` schema | `profile.baseProcessVersion` | Opaque string | Bumped per repository. |

The tuple `(profile.identity, profile.profileVersion,
profile.baseProcessVersion)` is the **exact** pin. Any subset match is
rejected per §6.1.

### 8.3 Non-impacts

- `IProcessConfigurationProvider` shape does not change. `ProcessConfiguration`
  gains no fields. All process discovery keeps running unchanged.
- `LinkTypeMapper` does not change. It remains the sole friendly ↔ ADO
  ref-name mapping.
- `StateCategoryResolver` does not change. It answers the process-agnostic
  category question. The reference profile piggy-backs on the same
  answers via `TypeConfig.StateEntries`.
- Telemetry does not change. Nothing in the profile leaves the local
  machine — the allowlist in `.github/copilot-instructions.md#L21-L44`
  already prohibits type/state/field/process/user identifiers.

---

## Out of scope

- **T2's decisions (AB#733).** Base ADO process choice, tailoring sequence
  and ordering, sandbox validation harness, and the concrete values of
  `baseProcess.parentRef`, `baseProcess.tailoringVersion`, and
  `types[*].backlogBehaviorRef`.
- **T3's decisions (AB#734).** The `IReferenceProfileProvider` C# shape,
  DI registration, caching lifetime, and how it composes with
  `IProcessConfigurationProvider`. §8.1 fixes the *queries*, not the
  interface.
- **T4's decisions (AB#735).** How the JSON is loaded (`Assembly.
  GetManifestResourceStream` vs `.resx` vs `.dll` binary blob), the
  `dotnet publish` inclusion mechanism, whether one release ships one
  profile or several, and the `twig-doctor` reporting.
- **Remote-dependency link kinds.** Cross-repository predecessor/successor
  edges, cross-project artifact links, and any URL-based external
  reference schema. Deferred — no artifact model yet.
- **Claim field bindings.** The `System.AssignedTo` projection contract,
  the `holderIdentity` shape, and claim payload fields. These live with
  the local-first claim spec (AB#728) and its downstream storage designs
  (AB#736 storage tiers; AB#737 claim record schema).
- **Migration tooling from `.conductor/process-config.yaml`.** #727 defers
  this. If a repository had a legacy `process-config.yaml`, it is
  ignored — nothing in this note reads it.
- **Automatic backlog resequencing.** Twig never resequences a live
  process to match the profile; the harness re-runs, or the repository
  bumps the pin. #727 defers this.
- **Multi-profile-per-repository.** A repository has exactly one
  `twig.json.profile` block; no per-branch or per-worktree profile
  override exists. Any override would break the checked-in
  reviewability property.

---

## Alternatives considered

- **Minimum-capability matching (subset accept).** Rejected — #727 S1
  fixes exact-match. Recorded here because a future non-reference
  third-party profile flavor may need it; this note does not preempt it.
- **Compile-baked reference profile (C# constants).** Rejected — §4.
- **ADO-hosted reference profile.** Rejected — §4.
- **Single monolithic version identifier.** Rejected — §2. Bumping T1
  and T2 independently is the whole point.
- **Sending the fingerprint on the wire instead of enumerated checks.**
  Rejected — §7.2 recovery hints require enumerated checks; the
  fingerprint is a backstop, not a substitute.

---

## Open questions

| # | Question | Owner | Recommendation |
|---|---|---|---|
| OQ-1 | Should the profile carry a per-role `System.AssignedTo` projection hint (e.g. "on `task`, project onto `System.AssignedTo`; on `initiative`, project onto `Custom.Owner`")? | AB#728 (claim spec) | Defer to the claim spec; keep the profile mute here. Adding it later is a MINOR bump. |
| OQ-2 | Should `linkKinds` gain a per-role allow-set (e.g. "`related` is not accepted between two `initiative` items")? | Future | Defer; no evidence of the constraint biting. Add later as a MINOR bump. |
| OQ-3 | Should `profile.json` be canonicalized (RFC 8785 JCS) before hashing, or is stable ordering + `System.Text.Json` sufficient? | T4 (AB#735) | Recommendation: stable-ordering + `System.Text.Json` writer with `WriteIndented=false, PropertyNamingPolicy=CamelCase, sorted-keys` at build time is sufficient — RFC 8785 adds ULP-agonizing float rules the profile does not need. |

---

## Acceptance checklist (for this note)

- [x] Every declaration T3 (AB#734) must expose is enumerated in §8.1.
- [x] Every version identifier T4 (AB#735) must ship is enumerated in
      §8.2.
- [x] Rejection cases are enumerated concretely (§7.1 + §7.2) and each
      is classified load-time or command-time.
- [x] The compatibility matrix (§7-Worked-examples) shows both accepted
      and rejected inherited-process changes.
- [x] Out of scope named explicitly (§9).
- [x] Nothing in the note reintroduces a hard-coded process/type/state
      assumption. The five vocabulary roles and the four link kinds are
      Twig's *own* abstractions — not an ADO-process opinion.
- [x] Consistent with the T2 (AB#733) sibling note: T2 owns
      `baseProcess.parentRef`, `baseProcess.tailoringVersion`, and
      `backlogBehaviorRef` content; T1 owns their schema slots and
      compatibility handling.
