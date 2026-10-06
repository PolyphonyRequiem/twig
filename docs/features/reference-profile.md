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

**Only an absent block reports absence.** A `twig.json` with no `profile`
block is reported as `twig-json-profile-block-missing` to consumers that
require a profile. A present block with any required field omitted, null,
empty, or whitespace remains a declaration and fails validation with
`profile-schema-invalid`, before the embedded profile is loaded. `twig init`
does not repair or replace a present pin, even with `--profile`, `--force`, or
`--reinitialize`. Restoring the intended release's complete pin or migrating
to another release is a separate, explicit repository-owner decision; do not
remove the declaration to bypass the gate.

The distinction between *absent* and *broken* is load-bearing:

- **Absent** — this repository never claimed to run the reference process.
  The reference sprint-entry gate is exempt, but primary-scope attachment
  and claims are unavailable because their eligibility requires a profile.
- **Broken** — this repository *did* claim to, but twig cannot tell which
  release's rules apply, so profile-gated rules fail closed with
  `profile-schema-invalid` for incomplete pins or the specific mismatch identifier
  for complete but incompatible pins.

`SprintEntryPolicy` demonstrates the pattern at
`src/Twig.Domain/Services/ReferenceProfile/SprintEntryPolicy.cs:80-91`: it
reads the *identifier* rather than the boolean success flag, so a
one-character typo in a version pin cannot silently disable a structural
gate.

## Initialization and explicit selection

Fresh `twig init` has **no default profile selection**. Without `--profile`
and without an existing `profile` declaration, it creates the normal
worktree layout, cache, and registry entry without writing a new `profile`
block or materializing `policy.selectedProfile` / `policy.primaryScopeTypes`.
It does not interpret existing policy records as consent to declare a profile.

For a non-reference project, initialize without selecting the reference:

```sh
twig init contoso Fabrikam
```

This supports process metadata and ordinary work-item authoring using the
connected process's vocabulary. An absent declaration leaves the reference
sprint-entry gate out of scope; it does not bypass other authoring checks or
ADO rules. Primary-scope attachment and claims remain unavailable until the
repository explicitly selects a compatible released profile. There is no
profile-free allow-set fallback.

For a fresh or untracked manifest that intentionally declares the shipped
reference release:

```sh
twig init contoso Hyperbright --profile twig.reference-profile.hyperbright
```

The example identity is data from the shipped `profile.json`; identities are
opaque and an unknown selection fails with `profile-identity-unknown`, not a
fallback to the embedded profile. The currently shipped release is
`twig.reference-profile.hyperbright`, profile version `1.0.0`, with base
process tailoring version `basic:2026-08-24:1`.

`--profile` is an explicit **declaration and selection**, not automatic live
compatibility validation. Process-template discovery never chooses a profile;
stock Agile or Scrum is not made compatible with this Basic-derived reference
by supplying the flag. This capability adds no new compatible-profile support
or live-process discovery beyond the existing provider.

An existing declaration is validated before new workspace state is written.
Its values remain unchanged, including incomplete or mismatched pins, which
fail closed. An explicit switch of an existing selection is refused. Neither
`--force` nor `--reinitialize` is a profile migration mechanism.

Tracked-manifest protection remains intact: `--profile` cannot add a
declaration to an already tracked, unprofiled `twig.json`. Selecting a profile
there requires a separate, explicit reviewed manifest change before init.
A matching existing pin is preserved without rewriting it or re-materializing
policy records.

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
                   │      (sprint     (fail closed)               │
                   │       exempt; scope denied)                  │
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
  recovery is an explicit, reviewed pin migration or using the binary that
  matches the intended pin, never automatic repair by `init`.

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

This provider capability is distinct from `init --profile`: selecting an
identity does not establish that these live-process checks have passed.

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
| `profile-schema-invalid` | Embedded JSON is invalid, or a present repository pin has an omitted, null, empty, or whitespace required field. |
| `hierarchy-locked-vocabulary-violation` | `hierarchy` block does not match the locked T1 §3.2 layout. |
| `role-set-not-canonical` | `types[*].role` is not exactly the five vocabulary roles. |
| `link-kinds-not-canonical` | `linkKinds[*]` does not match the T1 §3.5 table. |
| `primary-scope-empty-allow-set` | `primaryScope.eligibleRoles` is empty. |
| `primary-scope-unknown-role` | `primaryScope.eligibleRoles` contains an unknown role. |
| `twig-json-profile-block-missing` | `twig.json` has no `profile` block. Incomplete blocks do not report absence. |
| `profile-identity-unknown` | Pin `identity` or explicit `--profile` selection does not match the shipped artifact's `identity`. |
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
the T3 seam used by `twig init` to resolve an explicitly selected identity or
an existing repository declaration. No process-template label selects it and
plain fresh init does not resolve a default binding.

Its `IProfileRegistrySource.Resolve` reads the loaded profile, requires the
requested opaque identity to match, and materializes:

- `Identity` — verbatim from the embedded `identity`.
- `ProfileVersion` — verbatim from the embedded `profileVersion`.
- Primary-scope allow-set — the concrete type-name list derived by joining
  `primaryScope.eligibleRoles` through `TypeByRole` (see
  `ReferenceProfile.PrimaryScopeAllowTypeNames`).

Nothing is synthesized: an unknown identity fails with
`profile-identity-unknown`, and a profile that fails to load propagates its
own named error. Init does not create partial workspace state on refusal.

For a new explicit declaration, `ManagedWorktreeInitializer` records the
materialized selected-profile binding into the checked-in `twig.json` policy block
(`policy.selectedProfile` and `policy.primaryScopeTypes`) as a **record of
what that binding produced**, not as the runtime authority. The runtime
authority is always the embedded profile; the policy block is retained so a
reviewer can see what shape the worktree was bound with.

The separate three-field top-level `profile` pin is what enforces the
coupling: `profile.identity`, `profile.profileVersion`, and
`profile.baseProcessVersion` must match the loaded artifact as described
above. `policy.selectedProfile` and `policy.primaryScopeTypes` are records,
not runtime overrides or substitutes for that declaration. Editing either
record cannot enable attachment or claims in an unprofiled repository or
widen a selected profile's allow-set. Narrowing the allow-set means publishing
a different profile identity, not editing a repository policy record.

## How other subsystems consume the profile

- **Sprint entry** — `SprintEntryPolicy` reads
  `ReferenceProfile.SprintTierTypeName` to gate direct sprint commitment.
  The rule is *the reference process's* structural rule, not ADO's, so it
  applies only where the repository declared the reference process; an
  absent `profile` block passes the gate untouched. See the top of
  `src/Twig.Domain/Services/ReferenceProfile/SprintEntryPolicy.cs`.
- **Primary-scope attachment and claims** — `IPrimaryScopePolicySource` is a
  thin adapter over `PrimaryScopeAllowTypeNames`. Its allow-set is a query on
  the profile, not on the checked-in policy block. A missing pin refuses
  eligibility with `twig-json-profile-block-missing`; a broken pin fails closed.
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
| Primary-scope attachment or claim is unavailable in an unprofiled repository | `twig-json-profile-block-missing` | Expected for plain non-reference init. Select a compatible shipped profile only if the repository intends to declare it: `--profile` for a fresh/untracked manifest, or a separate reviewed declaration in an already tracked manifest. There is no profile-free attachment/claim support. |
| A present pin is incomplete | `profile-schema-invalid` | Restore the complete pin for the intended release through an explicit repository-owner decision. Init preserves the broken declaration and refuses; it does not repair it. |
| The pin exists but does not match the binary | `profile-identity-unknown`, `profile-version-mismatch`, `base-process-version-mismatch` | Use the binary matching the intended pin, or make a separate reviewed migration decision. `--force`, `--reinitialize`, and `--profile` do not replace existing pins. |
| Twig refuses at command time complaining about state / type / fingerprint | `type-name-missing`, `live-has-extra-state`, `profile-has-extra-state`, `state-order-mismatch`, `state-category-mismatch`, `live-fingerprint-mismatch`, `base-process-parent-mismatch` | The live ADO process has drifted from the released profile. Either the process needs to be reconciled to the profile, or a new profile release needs to be issued and pinned. |

## Related commands

- [`twig init`](../commands/getting-started/init.md) — bootstraps an unprofiled
  workspace by default; `--profile <identity>` explicitly declares a shipped
  release only in a fresh/untracked manifest without an existing declaration.
  Existing pins are preserved and validated, not repaired.
- [`twig config`](../commands/configuration/config.md) — reads or sets
  workspace configuration keys. Profile migration remains a separate,
  explicit repository decision.
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
