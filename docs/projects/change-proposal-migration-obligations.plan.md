# Remaining Change Proposal migration obligations (T1, AB#740)

**Work Item:** #740 (Task, parent Spec #729 — "Change Recipe and Change Proposal")
**Type:** Design / findings record (closes the migration-obligation question in #729)
**Status:** ⬛ Settled
**Plan Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive summary

This ticket asked a scoped question: *current `ChangeProposal` terminology and
projection types already exist — identify only the remaining external API,
documentation, or persisted-contract migration obligations; otherwise close this
task.*

**Bottom line: the task is NOT closeable as a no-op. Real migration obligations
exist**, and the premise embedded in the question needs one correction before the
inventory below is usable.

The correction: the pre-existing types named `ChangeProposal` are **not** early or
partial versions of the Spec #729 Change Proposal. There are **three** distinct
concepts here, two of which already own the name. Treating the existing types as
"the ChangeProposal that already exists" and renaming around them would be a
category error, and would produce a migration that silently rewires the TUI edit
path into the mutation-authorization path.

The mutation surface that Spec #729 actually renames is the one currently called
**`plan`** — CLI verbs, MCP tools, the Plan v1 document, and the SQLite journal.
That is where the obligations are.

**This note changes no code.** It fixes the rename map's boundaries and enumerates
what a cutover must cover.

---

## Background

### What #729 settled and what it deferred

Spec #729 settles the vocabulary — `Change Recipe` for the reusable parameterized
template, `Change Proposal` for the immutable digest-bound mutation document, and
`Plan` reserved for design/planning language. It explicitly defers, under
§Out of Scope:

> **Concrete rename of CLI verbs, MCP tool names, journal file names, telemetry
> keys, and user-visible strings.** The vocabulary is settled; the exact rename
> list, cutover mechanics, and historical-journal compatibility are follow-on
> decisions.

This note is that follow-on inventory. It does not perform the rename; AB#742 (T3)
executes it against this list.

### Method

Every entry below was read from source on `origin/main` (`70752d0e`). Absences are
reported as absences — several plausible obligations turned out not to exist, and
those are as decision-relevant as the ones that do.

---

## 1. The three concepts (scope correction)

| # | Concept | Type | Visibility | Is it the #729 Change Proposal? |
|---|---|---|---|---|
| A | `Twig.Domain.Projections.ChangeProposal` | union of `FieldEdit \| StateMove` | **public, shipped** | **No** |
| B | `Twig.Infrastructure.Persistence.Transport.ChangeProposalRenderProposal` | renderer DTO | internal | **No** |
| C | The Plan v1 document | JSON mutation document | public via CLI/MCP | **Yes** |

**A** is the TUI edit path. `EditCapability.Validate(ChangeProposal)` dispatches
`FieldEdit`/`StateMove`, and `IChangeSink.SubmitAsync(ChangeProposal)` stages the
result into `pending_changes`. It carries no digest, no operation list, no
immutability guarantee, and no work-item id. It is a *proposed edit to one item*,
not an authorizable mutation document.

**B** is a presentation seam. `ChangeProposalRenderer` explicitly does not inspect
the payload — `_ = proposal; // opaque payload, not inspected here` — and selects a
presentation purely from the transport attachment record. Its `Content` is
documented as opaque and owned by the Change Proposal design. It therefore renders
a *future* Change Proposal; it does not render **A**, and it does not render plan
operations.

**C** is the concept Spec #729 describes: immutable, digest-bound, carrying
concrete operations against target items.

**Consequence for the rename map.** The Spec #729 Change Proposal is introduced by
renaming **C**. **A** keeps its name or is renamed on its own merits as a separate
decision, and **B** is the seam that will eventually carry **C**'s serialized review
model. Renaming **A** onto **C** would break the TUI edit path and mislead every
future reader.

---

## 2. Migration obligations — externally observable

These are real contracts. Changing them is a breaking change for someone.

### 2.1 Public shipped API (highest cost)

`src/Twig.Domain/PublicAPI.Shipped.txt` — concept **A** is already shipped:

- `:344-348` — `Twig.Domain.Projections.ChangeProposal`, its three constructors, and `.Value`
- `:452` — `EditCapability.Validate(ChangeProposal) -> ValidationOutcome`
- `:476` — `IChangeSink.SubmitAsync(ChangeProposal, CancellationToken) -> Task<SubmitOutcome>`

Renaming a *shipped* public type is a breaking API change. **This obligation exists
only if concept A is renamed**, which §1 argues against. Recorded so the decision is
explicit rather than accidental.

In-repo consumers that would move with it: `src/Twig.Tui/PendingChangeStoreSink.cs`,
`samples/Twig.DetailHost/ReviewQueueSink.cs`,
`tests/Twig.Domain.Tests/Projections/EditCapabilityTests.cs`,
`tests/Twig.Tui.Tests/PendingChangeStoreSinkTests.cs`.

### 2.2 CLI verbs and help text

`src/Twig/Program.cs` — five registrations at `:1359`, `:1366`, `:1374`, `:1381`,
`:1388`: `[Command("plan validate")]`, `plan preview`, `plan apply`, `plan status`,
`plan seed`. Plus the command list and help text later in the same file, and
`src/Twig/CommandExamples.cs` examples.

Every documented invocation and every muscle-memory command line changes.

### 2.3 MCP tool names

`src/Twig.Mcp/Services/McpToolCatalog.cs:40-44` — `twig_plan_apply`,
`twig_plan_preview`, `twig_plan_seed`, `twig_plan_status`, `twig_plan_validate`.
The same names recur in the read-only set (`:100-102`), the destructive set
(`:122`), and the idempotent set (`:139`), and are routed by string name in
`Services/Batch/ToolDispatcher.cs`.

**MCP tool names are a wire contract with agent clients.** Renaming them breaks
every agent configuration that names a tool — including the skills in this
repository's own toolchain. This is the obligation most likely to be
underestimated, because it breaks consumers outside the repo that cannot be
migrated by editing this repo.

### 2.4 Machine-readable output keys

`src/Twig/Commands/PlanCommand.cs` — document kinds `planValidate`, `planPreview`,
`planApply`, `planStatus`, `planSeed`, plus the `-o json` field names
(`digest`, `canApply`, `issues`, `operations`, `pendingChanges`) and the journal
operation keys (`ordinal`, `opId`, `kind`, `state`, `startedAt`, `appliedAt`,
`verifiedAt`, `resultJson`, `error`, `warning`).

Anything parsing `twig plan … -o json` depends on these. The `plan*` document kinds
are the rename-sensitive part; the field names are mostly vocabulary-neutral.

### 2.5 Persisted contract — Plan v1 document

`src/Twig.Infrastructure/Plan/PlanDocumentParser.cs:29-75` fixes `version: 1`, the
root members `version` / `workspace` / `operations`, and the operation-kind
vocabulary `batch | add-link | remove-link | publish-seed | delete`.

The parser **rejects unknown keys and any version other than 1**. There is no
tolerant-reader path, so any renamed member is a hard break for existing plan
files on disk.

### 2.6 Persisted contract — SQLite journal schema

`src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs`, durable migration `[6]`
(extended by `[7]`): tables `plan_journals` and `plan_operations`, indexes
`idx_plan_journals_state`, `idx_plan_operations_ordinal`, `idx_plan_operations_state`.

Critically, **the durable store is never dropped**, unlike the disposable mirror
which is recreated on schema-version mismatch. Existing journal rows are real audit
history. A table rename is therefore a **data migration**, not a schema recreation,
and it must preserve historical rows.

### 2.7 User-visible strings

Error and help text in `PlanCommand.cs`, and the string `Unrecognised change
proposal.` in `EditCapability.cs`, `PendingChangeStoreSink.cs`, and
`ReviewQueueSink.cs` — the latter three belong to concept **A** and move only if
**A** moves.

---

## 3. Obligations that do NOT exist

Reported explicitly, because each was a plausible cost that turned out to be zero.

| Candidate | Finding |
|---|---|
| Telemetry command keys | **None.** `TelemetryHelper` emits only the allowlisted generic properties (`command`, `exit_code`, `output_format`, `twig_version`, `os_platform`, `duration_ms`, optional `operation_id`). No plan-specific key exists, and none should be added — the telemetry rules forbid identifiers. |
| A `plans.json` index as a shipped contract | **None.** The `.twig/ado-plans/<id>/plans.json` file is a toolkit/skill-owned session index, not a Twig contract. Twig itself neither reads nor writes it. |
| Existing aliases / `[Obsolete]` / legacy readers | **None** for the plan surface. The only versioning is the strict `version: 1` rejection. The legacy aliases that do exist (`note`/`add_note`, `field`/`set_field` in `IPendingChangeStore`) are unrelated. |
| Plan-surface documentation under `docs/` | **None.** No file in `docs/` mentions "Change Recipe" or documents the plan verbs, so there is no prose to migrate. |

The absence of any alias mechanism is the important one: **there is no existing
cutover machinery to reuse.** T3 must supply the compatibility policy itself.

---

## 4. Recommended cutover mechanic

Offered as input to AB#742 (T3), which owns execution.

1. **Do not rename concept A.** Decide it separately on its own merits. Bundling it
   into this rename risks breaking the TUI edit path for a naming tidy-up.
2. **Rename C's surface in one cutover**, not incrementally. A half-renamed
   mutation surface is exactly the ambiguity Spec #729 exists to remove.
3. **Accept a dual-name window on MCP tool names only.** They are the one contract
   with consumers outside this repository. Everything else can cut over cleanly
   because every consumer is in-tree.
4. **Migrate journal tables by data migration**, preserving historical rows. Never
   by drop-and-recreate — the durable store is deliberately never dropped.
5. **Keep the Plan v1 document's `version: 1` meaning exactly what it means today.**
   If the document's member names change, that is `version: 2`, with `1` still
   readable. The parser's strictness makes a silent reshape impossible, which is
   the correct behaviour.

---

## 5. Answer to the ticket's question

> *Identify only remaining external API, documentation, or persisted-contract
> migration obligations; otherwise close this task.*

**Remaining obligations exist. The task closes with this inventory, not as a no-op.**

- **External API:** CLI verbs (§2.2), MCP tool names (§2.4 wire contract, §2.3),
  machine output document kinds (§2.4), and — conditionally, only if concept A is
  renamed — shipped Domain public API (§2.1).
- **Persisted contract:** the Plan v1 document vocabulary (§2.5) and the SQLite
  journal schema (§2.6), the latter requiring a row-preserving data migration.
- **Documentation:** none in `docs/` (§3). The user-visible strings in §2.7 are the
  only prose obligation.

The scope premise is corrected in §1: the existing `ChangeProposal` types are not
the Spec #729 concept, and the surface actually being renamed is `plan`.

---

## 6. Verification

This note asserts no runtime behaviour. Every claim about current state was read
from source on `origin/main` (`70752d0e`) at the cited locations.

The falsifiable claim is §1: that concepts **A** and **B** are unrelated to the Spec
#729 Change Proposal. If either is later found to be an intended early version, the
rename map in §4 is wrong and must be re-derived. The evidence against that today is
direct — **A** has no digest, no operation list, and no work-item binding; **B**
explicitly does not inspect its payload.
