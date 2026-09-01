# Reference profile

The reference profile is the artifact that lets twig know what a particular
Azure DevOps process *means* without hard-coding a single work-item type,
state, or link name. It is the seam between "twig core, which is process-
agnostic" and "this repository, which has committed to one released profile."

Everything twig used to answer with a literal string — "the sprint tier is
`Task`", "`Bug` uses this state list", "`Related` is the well-known relation
name" — is answered through this profile instead. The strings still exist,
but they live *in the profile document*, not in code, and code compares them
byte-equal rather than reasoning about them.

## Why this exists

Twig runs against many different ADO process templates (Basic, Agile, Scrum,
CMMI, custom). It also carries structural rules of its own — sprint entry is
leaf-tier only, primary-scope attachment is restricted to a declared set of
roles, hierarchy has an apex/requirement/leaf shape. Those rules are twig's,
but the vocabulary they read is the process's.

A reference profile bridges the two by declaring:

- The **five vocabulary roles** twig speaks in (`Initiative`, `Investigation`,
  `Feature`, `Bug`, `Task`) — see `src/Twig.Domain/Enums/Role.cs:19-23`.
- For each role, the **live ADO type name** it binds to on this process.
- For each role, the ordered **state list** with each state's category
  (`Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`).
- The four **link kinds** twig cares about, each mapped to its well-known ADO
  relation reference name.
- The **primary-scope** kind and the roles eligible for attachment under it.

Twig core reads the profile through `IReferenceProfileProvider`
(`src/Twig.Domain/Interfaces/IReferenceProfileProvider.cs`) and never
otherwise embeds a type name, a state name, or a relation reference in code.

> ⚠️ The profile is a **vocabulary**, not a licence. Loading a profile does
> **not** authorize hard-coded process assumptions elsewhere. Every
> profile-sensitive decision — sprint-entry gate, primary-scope allow-set,
> role lookup, link-kind translation — MUST resolve through the provider at
> runtime. See `.github/copilot-instructions.md` under **Coding Conventions**:
> "process-agnostic. No hardcoded state names, type names, or process
> template assumptions."

## The document

The profile is a single JSON document embedded into the twig binary as an
assembly resource at
`Twig.Infrastructure.Resources.ReferenceProfile.profile.json`, alongside a
byte-exact SHA-256 sidecar `profile.json.sha256`. See
`src/Twig.Infrastructure/Services/ReferenceProfile/EmbeddedReferenceProfileProvider.cs:33-52`.

It exposes seven top-level fields, each maps 1:1 to a property on the
`ReferenceProfile` aggregate
(`src/Twig.Domain/ValueObjects/ReferenceProfile.cs:60-135`):

| Field | Meaning |
|---|---|
| `identity` | Opaque profile identity string. Repository pins match this byte-equal. |
| `profileVersion` | Opaque version stamp for the profile document itself. |
| `baseProcess.parentRef` | Opaque reference of the ADO parent (base) process the profile targets. |
| `baseProcess.tailoringVersion` | Opaque version stamp for the base-process tailoring. |
| `hierarchy` | The apex / requirement / leaf role blocks (locked to the T1 §3.2 canonical set). |
| `types[]` | For each role: live ADO type name, backlog role, backlog behaviour ref, ordered state list. |
| `linkKinds[]` | For each of the four canonical `LinkKind` values: meaning label plus forward/reverse ADO relation names. |
| `primaryScope` | Opaque scope kind plus the role allow-set. |
| `fingerprint.bytes` | Lowercase-hex SHA-256 of the profile's canonical structural form. Used as the T1 §7.3 fingerprint. |

Two things are worth noting:

- Every string on the document is opaque to twig core. The profile compares
  them byte-equal against the live process, never parses them.
- The `hierarchy` block is validated to *equal* the locked vocabulary
  (`apex=[Initiative]`, `requirement=[Investigation, Feature, Bug]`,
  `leaf=[Task]`). It exists to make review mechanical, not because twig
  reasons about it. See
  `src/Twig.Domain/ValueObjects/ReferenceProfile.cs:12-22`.

## The repository pin

A repository declares which released profile it runs by writing a `profile`
block to its checked-in `twig.json`. The block is exactly three fields
(`src/Twig.Infrastructure/Config/TwigConfiguration.cs:881-891`):

```json
{
  "profile": {
    "identity": "...",
    "profileVersion": "...",
    "baseProcessVersion": "..."
  }
}
```

- `identity` matches the embedded `identity`.
- `profileVersion` matches the embedded `profileVersion`.
- `baseProcessVersion` matches the embedded `baseProcess.tailoringVersion`.

All three are matched **byte-equal**, and any subset match is rejected. The
three fields exist separately — rather than one combined string — because the
profile schema (T1) and the base-process tailoring (T2) have independent
release cadences, and collapsing them would force one to move whenever the
other did.

**Absence is a named failure, not a permissive default.** A `twig.json` with
no `profile` block is reported as `twig-json-profile-block-missing`; that is
distinct from a block that is present but wrong. Any of the three fields
blank collapses to the same "absent" identifier — a partial pin asserts a
coupling it has not established, and reporting it as absent routes the fix
back through the same recovery path (`twig init`, or hand-write the pin).
See `src/Twig.Infrastructure/Config/TwigJsonReferenceProfilePinSource.cs:22-39`.

The distinction between *absent* and *broken* is load-bearing:

- **Absent** — this repository never claimed to run the reference process,
  so profile-gated rules cannot apply to it.
- **Broken** — this repository *did* claim to, but twig cannot tell which
  release's rules apply, so profile-gated rules fail closed with the
  specific mismatch identifier.

`SprintEntryPolicy` demonstrates the pattern at
`src/Twig.Domain/Services/ReferenceProfile/SprintEntryPolicy.cs:80-91`: it
reads the *identifier* rather than the boolean success flag, so a
one-character typo in a version pin cannot silently disable a structural
gate.

## Lifecycle

```
                       binary                              repository
                       ------                              ----------
  build time      profile.json         twig.json           .twig store
                  + sidecar
                        │                    │                    │
                        ▼                    ▼                    │
  load time    LoadCore ──► ValidatePin ◄────┘                    │
                   │             │                                │
                   │      ┌──────┴──────┐                         │
                   │      absent      broken                      │
                   │      (ok, out    (fail closed)               │
                   │       of scope)                              │
                   ▼                                              │
             ReferenceProfile                                     │
                   │                                              │
                   ▼                                              │
  command      ValidateAgainstLiveProcess(live, parentRef)        │
    time              │                                           │
                      ▼                                           ▼
                 ComputeLiveFingerprint  ──► compared to profile-declared
```

### Load-time (single load per process, cached)

`IReferenceProfileProvider.Load()` reads the embedded blob and, on first
call, validates:

1. **Resource present** — the assembly ships the profile JSON.
2. **Byte-exact sidecar match** — SHA-256 of the raw shipped bytes equals the
   sidecar (`.sha256`). This is the guard on raw bytes; the in-band
   `fingerprint.bytes` hashes a *normalized* form and is structurally blind
   to raw-byte edits like key order or role casing. See
   `src/Twig.Infrastructure/Services/ReferenceProfile/EmbeddedReferenceProfileProvider.cs:40-53`.
3. **Schema literal** — `$schema` equals `twig-reference-profile/v1`.
4. **Deserialization** — every required field present and typed correctly
   under the source-generated context.
5. **Structural fingerprint** — the canonical structural fingerprint over the
   profile's own declared shape equals its embedded `fingerprint.bytes`.
6. **Hierarchy locked vocabulary** — the `hierarchy` block equals the T1
   §3.2 canonical layout.
7. **Role set canonical** — `types[*].role` is exactly the five vocabulary
   roles.
8. **Link-kind table canonical** — `linkKinds[*]` equals the T1 §3.5 table.
9. **Primary scope** — non-empty allow-set of known roles.

Result is cached for the process lifetime and returned identically on every
subsequent call.

### Pin validation

`IReferenceProfileProvider.ValidatePin()` is deliberately separate from
`Load()`:

- `Load()` answers *is the shipped blob intact?* — repair path is "reinstall
  twig."
- `ValidatePin()` answers *does this repository agree with this binary?* —
  repair path is "bump the pin, or install the matching twig."

Keeping them apart is what lets `twig init` call `Load()` at a moment when it
could not yet have satisfied a pin. It is also what makes each failure
actionable: collapsing them would report a config drift as a corrupt install.
See `src/Twig.Domain/Interfaces/IReferenceProfileProvider.cs:47-66`.

Pin presence is checked *before* the blob is touched, so a corrupt install
does not make every repository look "unbound."

### Command-time (live-process validation)

`IReferenceProfileProvider.ValidateAgainstLiveProcess(live, liveBaseProcessRef)`
compares the profile against a discovered live process. It fails fast on the
first mismatch. See
`src/Twig.Infrastructure/Services/ReferenceProfile/EmbeddedReferenceProfileProvider.cs:125-177`:

1. **Base-process parent** — `liveBaseProcessRef` equals the profile's
   `baseProcess.parentRef`, byte-equal.
2. **Type presence** — every profile-declared type name exists on the live
   process (case-insensitive, matching `WorkItemTypeComparer`).
3. **State names** — for each type, the set of live state names equals the
   set of profile state names.
4. **State order** — for each type, the ordered lists are the same length
   and equal position-by-position.
5. **State category** — each position's category matches.
6. **Structural fingerprint backstop** — the T1 §7.3 fingerprint recomputed
   from the *live* process using the profile's declared role order equals
   the fingerprint recomputed from the profile's own declared shape. This
   catches divergence along any axis the enumerated checks miss.

The `liveBaseProcessRef` is a required parameter fed by the caller that did
the ADO discovery; twig core does not otherwise expose raw ADO reference
names as strings, and echoing the profile's own value would make the
comparison structurally blind.

`ComputeLiveFingerprint(live, liveBaseProcessRef)` exposes the live-side hash
independently for tooling that needs to report drift without deciding on it.

## Named failure identifiers

Every failure the profile subsystem raises is a stable, byte-equal string
constant on `ReferenceProfileErrors`
(`src/Twig.Domain/ValueObjects/ReferenceProfileErrors.cs`). Callers may match
on them directly, and telemetry may surface them — they carry no
ADO-specific content.

### Load-time (T1 §7.1)

| Identifier | Meaning |
|---|---|
| `profile-blob-not-found` | Embedded profile resource missing from the assembly. |
| `profile-fingerprint-mismatch` | Canonical structural fingerprint does not match `fingerprint.bytes`. |
| `profile-schema-invalid` | JSON did not deserialize (missing field, wrong type, unknown role). |
| `hierarchy-locked-vocabulary-violation` | `hierarchy` block does not match the locked T1 §3.2 layout. |
| `role-set-not-canonical` | `types[*].role` is not exactly the five vocabulary roles. |
| `link-kinds-not-canonical` | `linkKinds[*]` does not match the T1 §3.5 table. |
| `primary-scope-empty-allow-set` | `primaryScope.eligibleRoles` is empty. |
| `primary-scope-unknown-role` | `primaryScope.eligibleRoles` contains an unknown role. |
| `twig-json-profile-block-missing` | `twig.json` has no `profile` block (or a partial one). |
| `profile-identity-unknown` | Pin `identity` does not match embedded `identity`. |
| `profile-version-mismatch` | Pin `profileVersion` does not match embedded `profileVersion`. |
| `base-process-version-mismatch` | Pin `baseProcessVersion` does not match embedded `baseProcess.tailoringVersion`. |

### Command-time (T1 §7.2)

| Identifier | Meaning |
|---|---|
| `base-process-parent-mismatch` | Live parent-process reference disagrees with the profile. |
| `type-name-missing` | A profile-declared type name is not on the live process. |
| `live-has-extra-state` | Live type has a state name the profile does not declare. |
| `profile-has-extra-state` | Profile declares a state name the live type does not have. |
| `state-category-mismatch` | A live state's category disagrees with the profile. |
| `state-order-mismatch` | State ordering (or count) does not match. |
| `live-fingerprint-mismatch` | Live structural fingerprint deviates from the profile's declared shape. |

Downstream policies raise their own identifiers on top. The sprint-entry
gate, for instance, emits `sprint-entry-not-sprint-tier` when a non-leaf
type is being committed to a sprint iteration
(`src/Twig.Domain/Services/ReferenceProfile/SprintEntryFailure.cs`).

## Resolution and materialization

`ReferenceProfileRegistrySource`
(`src/Twig.Infrastructure/Persistence/ReferenceProfileRegistrySource.cs`) is
the T3 cutover that lets `twig init` bind a fresh worktree to the embedded
profile rather than fail closed with `selected-profile-unavailable`.

It exposes an `IProfileRegistrySource` whose `Resolve` reads the loaded
profile and materializes:

- `Identity` — verbatim from the embedded `identity`.
- `ProfileVersion` — verbatim from the embedded `profileVersion`.
- Primary-scope allow-set — the concrete type-name list derived by joining
  `primaryScope.eligibleRoles` through `TypeByRole` (see
  `ReferenceProfile.PrimaryScopeAllowTypeNames`).

Nothing is synthesized: a profile that fails to load propagates its own
named error, so the "no synthetic identity, no partial workspace" rule
holds.

`ManagedWorktreeInitializer` records the materialized selected-profile
binding into the checked-in `twig.json` policy block
(`policy.selectedProfile` and `policy.primaryScopeTypes`) as a **record of
what that binding produced**, not as the runtime authority. The runtime
authority is always the embedded profile; the policy block is retained so a
reviewer can see what shape the worktree was bound with.

The separate three-field `profile` pin is what enforces the coupling.
Editing `policy.primaryScopeTypes` by hand does not widen or narrow what
twig will attach: narrowing the allow-set means publishing a different
profile identity, not editing a repository file.

## How other subsystems consume the profile

- **Sprint entry** — `SprintEntryPolicy` reads
  `ReferenceProfile.SprintTierTypeName` to gate direct sprint commitment.
  The rule is *the reference process's* structural rule, not ADO's, so it
  applies only where the repository declared the reference process; an
  absent `profile` block passes the gate untouched. See the top of
  `src/Twig.Domain/Services/ReferenceProfile/SprintEntryPolicy.cs`.
- **Primary-scope attachment** — `IPrimaryScopePolicySource` is a thin
  adapter over `PrimaryScopeAllowTypeNames`. Its allow-set is a query on
  the profile, not on the checked-in policy block.
- **Role lookup** — anywhere twig needs "what role does this ADO type name
  play?" it calls `RoleByTypeName`; anywhere it needs "what ADO type name
  does this role bind to on this profile?" it calls `TypeByRole`.
- **Link translation** — well-known ADO relation reference names are looked
  up through `LinkKinds`, keyed by the `LinkKind` enum.

If a subsystem needs to know a *specific* concrete string (a state name, a
type name, a relation ref), it obtains it from the profile at call time. It
never captures it into a constant, an enum member, or a switch arm.

## Failure and repair

| Symptom | Identifier | Fix |
|---|---|---|
| Twig refuses every command with a load-time error | `profile-blob-not-found`, `profile-fingerprint-mismatch`, `profile-schema-invalid`, or a `-locked-vocabulary-violation` / `-not-canonical` variant | The shipped binary is corrupt or tampered — reinstall twig. |
| Twig refuses profile-gated commands here but not everywhere | `twig-json-profile-block-missing` | Add the three-field `profile` block to `twig.json` (or re-run `twig init` to write one). |
| The pin exists but does not match the binary | `profile-identity-unknown`, `profile-version-mismatch`, `base-process-version-mismatch` | Bump the pin to the released profile that this binary embeds, or install the binary that embeds the pinned profile. |
| Twig refuses at command time complaining about state / type / fingerprint | `type-name-missing`, `live-has-extra-state`, `profile-has-extra-state`, `state-order-mismatch`, `state-category-mismatch`, `live-fingerprint-mismatch`, `base-process-parent-mismatch` | The live ADO process has drifted from the released profile. Either the process needs to be reconciled to the profile, or a new profile release needs to be issued and pinned. |

## Related commands

- [`twig init`](../commands/getting-started/init.md) — bootstraps a workspace
  and writes an initial `profile` pin plus the materialized selected-profile
  binding.
- [`twig config`](../commands/configuration/config.md) — reads or sets
  individual configuration keys; useful for inspecting or updating pin
  fields.
- [`twig process description`](../commands/process/process-description.md) —
  byte-stable structural description of the live process; the same shape
  the command-time validator compares against.
- [`twig process layout`](../commands/process/process-layout.md) — live
  process layout as twig sees it after profile-driven role binding.

## Related architecture

- [Architecture overview](../architecture/overview.md) — how the profile
  seam fits between commands, domain, and infrastructure.
- [ADO integration](../architecture/ado-integration.md) — how live process
  discovery is performed and cached, i.e. the input to
  `ValidateAgainstLiveProcess`.
- [Data layer](../architecture/data-layer.md) — where the materialized
  selected-profile binding is persisted per workspace.
