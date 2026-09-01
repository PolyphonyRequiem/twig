# Process description

`twig process description` writes a **byte-stable structural description** of the
work-item process an Azure DevOps project is on — every work-item type it defines,
every field on every type, requiredness and value constraints, states and their
transitions, every rule with its inherited-vs-authored tag, backlog-level
(behaviour) membership, and the whole form layout. The point is not to look at the
document; the point is to point an ordinary diff tool at two of them.

This page describes the feature. For the CLI verb — its flags, arguments, and
exit codes — see [`twig process description`](../commands/process/process-description.md).

## What the document is for

Twig is process-agnostic: it never hardcodes state names, type names, or process
templates. The whole `IProcessConfigurationProvider` layer discovers process shape
at runtime from ADO. That works well for a single workspace, but it leaves two
important questions unanswered when Twig is used across projects, forks, or over
time:

- **Have two projects drifted?** A reference process and a downstream project fork
  regularly need to be compared. A visual comparison in the ADO web UI cannot
  survey the whole shape — it renders one type at a time and hides inherited
  detail.
- **Did anything change since last time?** A process is edited by humans in a
  web form. Twig has no push notification when a required field appears, a state
  is removed, or a rule flips from `system` to `custom`.

The description artefact answers both by producing an ordered, deterministic
document a caller can diff against a captured baseline or against a second
project's description.

## The document model

The document is assembled from live ADO reads and projected to bytes through a
single seam:

- `AdoProcessDescriptionSource` (in the infrastructure layer) reads every
  process route with an api-version pinned per route (see
  [ADO integration § "Process description routes"](../architecture/ado-integration.md#4-field-enrichment)).
- `ProcessDescriptionAssembler` (`src/Twig.Domain/Services/Process/ProcessDescriptionAssembler.cs:14-32`)
  gathers those reads, sorts every collection on an explicit **ordinal** key,
  and produces the `ProcessDescription` model.
- `ProcessDescriptionDocument` (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:47-98`)
  projects that model into the render tree and renders it with `JsonRenderer`
  and a shared `Indented = true` setting.

Both the CLI (`twig process description`) and the MCP tool
(`twig_process_description`) go through the same assembler and the same
projection, so the two surfaces emit **byte-identical** bytes by construction
rather than by convention
(`src/Twig.Mcp/Tools/ProcessTools.cs:86-94`,
`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:13-23`).

### Header

The header is the metadata band a diff tool can be told to skip past. It carries:

| Key | Meaning |
|---|---|
| `organization` | Azure DevOps org the description was read from. |
| `project` | Project the workspace is bound to. |
| `processId` | Server-assigned process id — the key every process route is scoped by. |
| `processName` | The process's human name. |
| `capturedAt` | The **single permitted variance** between two runs; ISO-8601 round-trip (`O`), UTC, invariant culture. Injected via `TimeProvider` so tests can hold it fixed (`src/Twig/Commands/ProcessDescriptionCommand.cs:135-138`). |
| `descriptorVersion` | Currently **`0.1` — "under design"**. Bumping it is a contract decision, not a side-effect of adding content. |
| `routeApiVersions` | The api-version pinned for each route the document was built from, so two captures months apart cannot differ merely because the server moved. |
| `knownGaps` | Reservations this descriptor version makes about its own trustworthiness. |

`capturedAt` is deliberately the only line that varies between two runs. Every
other value is a function of the process; the timestamp is a function of the
clock, and it sits in the header where a diff tool can be pointed past it,
never interleaved into the body
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:172-181`).

### Known gaps

`knownGaps` declares what the document does not carry — the audit that keeps
"we ship it all" from becoming an implicit claim. At descriptor 0.1 the list is
one entry: rule identity (`ruleIdentity`), which is deliberately omitted because
each rule's server id is a per-process GUID and including it would make every
rule diff dirty between two projects
(`src/Twig.Domain/Services/Process/ProcessDescriptionAssembler.cs:96-116`).

An **empty** `knownGaps` list is itself a claim — "this document makes no
reservations" — and is rendered positively in the human form as one sentence
rather than as a bare heading with nothing under it. That distinction lets a
reader tell "no reservations" apart from "does not implement reservations"
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:203-232`).

### Per-type content

Every type in the process arrives with the same shape, whether it is a built-in
or a custom derivative:

- **Identity** — reference name (the diff key; display names lie), display name,
  description, `customization` (`system`/`inherited`/`custom`), `inherits` when
  derived, `isDisabled`.
- **Fields** — reference and display names, ADO data type, **merged**
  requiredness, value constraint, default value, `customization`, `isLocked`,
  description.
- **States** — name, `stateCategory`, `order`, colour, `customization`,
  `isHidden`.
- **Transitions** — `fromState` → `toState`. An empty `fromState` is the
  **initial transition** — the state a new work item enters — carried as-is
  because it is a real fact
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:310-321`).
- **Rules** — **every** rule the server returns, inherited ones included.
  Filtering here is the reversal the feature most fears: a derived type carries
  ~54 rules of which one or two were authored, so dropping the inherited ones
  is tempting and wrong — a difference that exists only in the omitted part
  would diff clean, and a reader handed a filtered document could not tell
  anything was gone. Filtering stays with the READER, via the `customization`
  tag on each row
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:323-352`).
- **Behaviours** — reference name, display name, `rank`, `isDefault` for each
  backlog level (Requirement / Portfolio / Task) the type is a member of.
- **Form layout** — one row per `layoutPage`, `layoutGroup`, `layoutControl`,
  and `layoutSystemControl`, each carrying its `page`/`section`/`group`
  address, `visible`/`inherited`/`isContribution` flags, and server-assigned
  `order`. Flat rather than nested because a line-oriented diff can compare
  flat rows; a nested tree shifts every descendant when one group moves. Every
  member of every layout level is emitted unconditionally — an earlier draft
  dropped empty pages and hidden groups and produced byte-identical documents
  for genuinely-different forms
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:372-499`).
- **Counts** — `fieldCount`, `stateCount`, `transitionCount`, `ruleCount`,
  `authoredRuleCount`, `behaviourCount`, `layoutControlCount`. The last one is
  a **`DisplayOnly` empty cell** rather than `0` when the layout could not be
  read — a form with zero controls and a form that failed to fetch are
  different facts, and blending them into `0` is the silent-omission failure
  the whole feature exists to prevent
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:532-540`).
- **`unfetched`** — the machine-readable label for what could not be read.
  Empty means everything was read. This is what stops an empty field list from
  reading as "this type has no fields" when the truth is "the call failed" —
  indistinguishable otherwise, and wrong in the silent direction
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:541-547`).

## Requiredness and value constraint

Two field-level attributes are unusual and worth explaining, because reading
either one directly from a single ADO route lies.

### Requiredness — three tokens, not a boolean

The per-type `fields` route reports **unconditional** requiredness only. A
field made mandatory only by a rule — `when State = Done → makeRequired` —
reads as not-required there. The description carries `requiredness` merged from
both the fields route and the rules route:

| Token | Meaning |
|---|---|
| `always` | Required in every state. |
| `conditional` | Required only when the paired `requiredWhen` conditions hold. |
| `never` | Not required in any state the process defines. |

`requiredWhen` renders the conditions in one stable line: alternatives joined
with `OR`, clauses within an alternative joined with `AND` — the server's own
semantics (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:592-597`).

Rendering `conditional` as `false` was the AB#236 defect; rendering it as
`true` would be wrong the other way (a caller would supply the field
unconditionally when the process does not ask for it). Three tokens keep both
misreadings unreachable
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:560-577`;
[ADO integration §3](../architecture/ado-integration.md#4-field-enrichment)).

### Value constraint — four tokens, not a boolean

No process route carries `allowedValues` or any picklist reference at any
api-version, with or without `$expand=all`. The association is readable
**field-first** off the org-scoped `_apis/wit/fields` route, which reports
`isPicklist` on every row. So the description carries `valueConstraint`
resolved from that third source:

| Token | Meaning |
|---|---|
| `list` | Value must come from `allowedValues`. |
| `suggested` | The web editor offers `allowedValues`; the server enforces nothing. |
| `unconstrained` | The server accepts anything. **A positive claim**, not a default. |
| `unknown` | The picklist call failed or the source contradicted itself. |

Four tokens are load-bearing:

- `unknown` must **not** collapse into `unconstrained`. That is the most
  dangerous wrong answer, because it tells a caller the server accepts
  anything when nobody successfully asked — and it fails at the server rather
  than locally.
- `suggested` must **not** collapse into `list`. A "suggested" picklist would
  tell a caller its write must come from the list when it need not.

The associated `valueList` (list name), `allowedValues` (joined in the
assembler's ordinal order), and `defaultValue` accompany the token
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:275-291`,
`599-619`; [ADO integration § value constraints](../architecture/ado-integration.md#4-field-enrichment)).

Fields whose picklist could not be resolved put `picklists` on the type's
`unfetched` list. Partial failures are labelled too, because `unfetched` is
derived from the resolved answers rather than from whether the call came back.

## Output formats and completeness

The description ships in two renderings, distinguished by the CLI's `-o` flag
(the MCP surface has no format choice — it always emits the complete JSON):

| `-o` value | Rendering | Completeness |
|---|---|---|
| `json`, `json-full`, `json-compact` | The complete document. Shared JSON renderer, `Indented = true`. | **Complete** — every content item on this page. |
| Any other format (e.g. `human`, `minimal`) | The abridged rendering: identity, `authored/total` rule count, and per-type counts, one line per type. | **Abridged**, and self-declares it in a banner. |
| `-o ids` | **Refused explicitly.** | — |

The complete-format constant is named once
(`ProcessDescriptionDocument.CompleteFormat = "json"`) and read by both the
banner and the completeness test, so the banner cannot come to name a format
that does not produce the complete document
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:49-59`;
`src/Twig/Commands/ProcessDescriptionCommand.cs:60-94`).

`-o ids` is refused rather than served badly: that renderer emits only integer
`id` cells and a process description has no numeric ids, so it would produce
an **empty file with a zero exit code and no notice** — worse than a hard
error (`src/Twig/Commands/ProcessDescriptionCommand.cs:113-125`).

The abridged rendering carries its own banner —
`ABRIDGED RENDERING — this is a summary and omits detail. The complete
document is produced by -o json.` — and the banner is machine-visible
(not `HumanOnly`) because `minimal` and `ids` are machine formats that also
render abridged, and tagging the notice human-only would hand a machine
consumer a truncated document carrying no notice that anything was dropped
(`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:114-131`).

## Why byte-stability

The document is deterministic **as a hard requirement**, not as a nice
property, because everything the artefact is for depends on it:

- **Diff signal.** If two runs against an unchanged process produced different
  bytes, every diff would surface phantom differences and drown the real ones.
- **Cross-project comparison.** Comparing a reference process to a downstream
  fork requires the same input shape to produce the same output shape.
- **Baseline capture.** A description checked in as a baseline is only useful
  if a subsequent capture from the same process still matches it byte-for-byte.

Byte-stability is defended in four load-bearing places:

1. **Single ordering authority.** `ProcessDescriptionAssembler` sorts every
   collection on an explicit `StringComparer.Ordinal` key before the projection
   sees the model. The projection walks collections in the order given;
   re-sorting or projecting through a dictionary at the projection layer would
   put a second ordering authority in the system and byte-stability would
   depend on both agreeing forever
   (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:100-105`).
2. **Ordinal, not culture-aware.** A culture-sensitive comparison can order the
   same two strings differently on two machines or two .NET versions, so a
   contributor's laptop and CI would diff dirty for no real reason
   (`src/Twig.Domain/Services/Process/ProcessDescriptionAssembler.cs:22-24`).
3. **Concurrency does not reach the ordering.** Whole-process runs fetch types
   concurrently (bounded at `MaxConcurrentTypeFetches = 4`) to keep latency
   under the ~20 s ruled ceiling, but results are re-sorted after the gather
   rather than appended as they complete. A test may drive the source to
   complete in exactly reversed order and the document must be byte-identical
   (`src/Twig.Domain/Services/Process/ProcessDescriptionAssembler.cs:27-32`,
   `118-156`).
4. **Shared renderer settings.** `Indented = true` is a constant read by every
   surface; a byte-identity test reds if the CLI and the MCP surface disagree,
   which is the mechanism that makes byte-identity a structural fact rather
   than a hope
   (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:88-98`).

The MCP surface adds a fifth: it writes the document into its response
envelope with `WriteRawValue`, not by re-parsing and re-writing, because the
envelope uses `UnsafeRelaxedJsonEscaping` while `JsonRenderer` uses the default
encoder — a re-parse would silently re-encode `\u0027 → '`, `\u0026 → &`,
`\u002B → +`. Every one is valid JSON carrying the same string value, which
is precisely why it would have survived any structural assertion and shipped
(`src/Twig.Mcp/Tools/ProcessTools.cs:146-164`).

Atomic writes to `--out` protect the same invariant on disk: a renderer that
throws mid-render never leaves a truncated document at the target path. The
CLI writes to a `.tmp-<random>` scratch file and moves it into place, and
cleans up the scratch on every failure arm
(`src/Twig/Commands/ProcessDescriptionCommand.cs:187-219`).

## Runtime discovery and process-agnosticism

The description is discovered live from the live process; nothing in the
description assumes a specific process template (Agile, Scrum, CMMI, Basic,
or a custom one). Consequences worth naming:

- **Reference names are the join key**, not display names. Display names are
  culture- and rename-sensitive; reference names are what the server uses to
  key rules, layouts, and cross-type relationships. The document leads every
  type row with `referenceName`
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:505-513`).
- **State categories, not state names.** State names differ across templates
  (`Active` vs. `Doing`), but state categories (`Proposed`, `InProgress`,
  `Resolved`, `Completed`, `Removed`) are the process-agnostic invariant. The
  document carries both — the name for identity, the category so a consumer
  can reason about lifecycle without hardcoding names
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:295-307`).
- **`customization` tags** distinguish `system` / `inherited` / `custom` /
  `unknown` on types, fields, and rules. An unrecognised server token renders
  as `unknown:<token>` rather than as a bare `unknown` — Twig does not own
  this vocabulary, and a class it has not seen is a fact worth showing rather
  than an error worth erasing
  (`src/Twig.Domain/Services/Process/ProcessDescriptionDocument.cs:634-661`).
- **No cache.** `AdoProcessDescriptionSource` is a separate class from
  `AdoIterationService` deliberately: that service memoizes every route it
  calls, and the description must not cache anything — a stale description is
  a wrong description, and the artefact is a truth claim about a process at a
  moment in time
  ([ADO integration § process description routes](../architecture/ado-integration.md#4-field-enrichment)).

## Diffing workflow

The purpose of the artefact is a diff. Practical steps:

### Capture two descriptions

Same tool, twice — once per side of the comparison. Use `-o json` (or its
`json-full`/`json-compact` aliases) so the document is complete:

```
twig init --org contoso --project ReferenceProcess
twig process description -o json --out /tmp/proc-ref.json
```

```
twig init --org contoso --project DownstreamFork
twig process description -o json --out /tmp/proc-fork.json
```

Confirmation goes to stderr; the file is the output. `--out` writes atomically
via a `.tmp-<random>` scratch file, so a crash during render never leaves a
truncated document you'd then diff against.

### Point a diff tool at them

Any line-oriented diff tool works. Practical picks:

```
diff /tmp/proc-ref.json /tmp/proc-fork.json
git diff --no-index /tmp/proc-ref.json /tmp/proc-fork.json
delta /tmp/proc-ref.json /tmp/proc-fork.json
code --diff /tmp/proc-ref.json /tmp/proc-fork.json
```

The `header.capturedAt` line will differ — it is the single permitted variance.
Skip past it, or filter it out with the tool of your choice (e.g. `diff -I
capturedAt`). Every other difference is a real difference.

### Baseline capture and drift detection

Commit a captured description to a repository as a baseline, then re-capture
periodically:

```
twig process description -o json --out ops/baseline/process.json
git add ops/baseline/process.json && git commit -m "Baseline process on 2026-09-01"
```

Later:

```
twig process description -o json --out /tmp/process-latest.json
diff ops/baseline/process.json /tmp/process-latest.json
```

Any diff (other than `capturedAt`) is a live process change worth attention.

### Compare a single type

The description accepts a type's reference name for cheaper single-type
captures, symmetric with `twig process layout`:

```
twig process description Niflheim.Grilling -o json --out /tmp/grill-ref.json
twig process description Niflheim.Grilling -o json --out /tmp/grill-fork.json
diff /tmp/grill-ref.json /tmp/grill-fork.json
```

This is the cheap path when the question is about one type. Naming a type that
does not exist is a hard error with no partial file
(`src/Twig/Commands/ProcessDescriptionCommand.cs:148-154`).

### From an agent

The MCP tool `twig_process_description` is byte-identical to `-o json`
(`src/Twig.Mcp/Tools/ProcessTools.cs:87-94`,
`103-104`). An agent can capture two descriptions and diff them without paying
for a subprocess:

- `twig_process_description` — omit `types` for the whole process, pass an
  array of reference names for one-or-more specific types. An empty array is
  rejected (`InvalidInput`), because both silent readings — "every type" and
  "no types" — are bad
  (`src/Twig.Mcp/Tools/ProcessTools.cs:114-129`).
- The document arrives under the response envelope's `description` key,
  written verbatim (no re-encoding), so an agent that persists it and later
  invokes the CLI on the same process gets identical bytes both ways.

## Failure semantics

The document is either complete or the command fails — there is no such thing
as a partial description standing in for a failed fetch. The assembler returns
one of four outcomes, each carrying its own remedy
(`src/Twig/Commands/ProcessDescriptionCommand.cs:127-177`):

- **Assembled** — the description is projected and rendered.
- **Type not found** (`ProcessDescriptionTypeNotFound`) — the named type does
  not exist in this process. Hard error, no partial file: a script that banked
  "this process has nothing" when the truth is "you asked for something that
  is not here" would be worse than a failure.
- **Process unresolved** (`ProcessIdentityUnresolved`) — the workspace's
  project does not resolve to a process. A configuration problem; the message
  says so and does not suggest retrying.
- **Types unfetchable** (`ProcessTypesUnfetchable`) — the type list route did
  not answer. Transient or auth; the message says so, and points at `twig
  auth`. That is the **opposite** advice to the arm above, which is the whole
  reason these are two arms rather than one
  (`src/Twig/Commands/ProcessDescriptionCommand.cs:164-172`).

Per-type read failures below the top-level route (a field call fails on one
type, a picklist won't resolve, the form layout is unreachable) do **not**
fail the command. Instead, that fact is labelled: the affected type carries a
non-empty `unfetched` list, and downstream counts render as `DisplayOnly`
empty cells rather than `0`. A reader can see, in the document, exactly which
parts are missing and can decide what to do about it. This is the whole point
of the `unfetched` design: an unread part is a fact, and pretending otherwise
is the silent-omission failure the artefact exists to prevent.

## See also

- [`twig process description`](../commands/process/process-description.md) — the CLI verb reference.
- [`twig process`](../commands/process/process.md) — cached local view of types and states.
- [`twig process layout`](../commands/process/process-layout.md) — one type's form layout, alone.
- [`twig states`](../commands/process/states.md) — states and transitions for a single type.
- [ADO integration layer](../architecture/ado-integration.md) — how the routes are read, api-versions pinned, requiredness merged, and picklist values joined.
- [MCP server](../architecture/mcp-server.md) — the `twig_process_description` tool and its envelope contract.
