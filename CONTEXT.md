# Twig — domain glossary

The names twig's code actually uses, and what they mean. Every entry is derived from source,
not from documentation — `docs/` has known drift, this file is the tiebreaker for *naming*.

Use these names in code, docs, issues, and design discussion. When a concept needs a new name,
add it here first.

> Baseline: audited 2026-07-26 against `fix/275-benchmarks-sdk` (985dd439). Findings ledgers in
> `%TEMP%\twig-review\`.

---

## Naming rules

1. **A concept has one name.** If two names exist for one thing, one is wrong — fix the code.
2. **The code is the tiebreaker.** If a doc and a type name disagree, the type name wins and the
   doc is rot.
3. **Don't invent a name to avoid a rename.** New synonyms are how `Workspace` got three meanings.

---

## 1. Core entities

| Noun | Definition | Defined at |
|---|---|---|
| **WorkItem** | Root aggregate for an ADO work item. Mutations (`ChangeState`, `UpdateField`, `AddNote`) take effect immediately and set `IsDirty`. | `src/Twig.Domain/Aggregates/WorkItem.cs:12` |
| **WorkItemSnapshot** | Immutable primitive-only carrier of raw work-item data, no domain behaviour. The boundary type both the ADO and SQLite mappers produce. | `src/Twig.Domain/ValueObjects/WorkItemSnapshot.cs:8` |
| **ProcessConfiguration** | Immutable aggregate encoding ADO process rules (states, allowed child types, transitions). | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs:42` |
| **TypeConfig** | Per-work-item-type slice of a `ProcessConfiguration`. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs:11` |

## 2. Staged / local-only state

Everything here exists **only in the local SQLite cache** until pushed.

| Noun | Definition | Defined at |
|---|---|---|
| **PendingChangeRecord** | A recorded, not-yet-pushed field change. Its `work_item_id` FK to `work_items` is the root cause of #269/#270. | `src/Twig.Domain/Common/PendingChangeRecord.cs:6` |
| **PendingNote** | An uncommitted comment staged against a work item (`Text`, `CreatedAt`, `IsHtml`). | `src/Twig.Domain/ValueObjects/PendingNote.cs:6` |
| **FieldChange** | Immutable record of a single field change; values stored as strings for AOT safety (RD-009). | `src/Twig.Domain/ValueObjects/FieldChange.cs:4` |
| **PublishIdMap** | Persisted seed-ID → published-ADO-ID mapping recorded at publish time; used by reconcile to repair stale negative IDs. | `src/Twig.Domain/Interfaces/IPublishIdMapRepository.cs:6` |

## 3. Seeds

**A seed is not a type.** It is `WorkItem.IsSeed` plus a negative ID — a local-only draft work
item that has never been pushed to ADO (`src/Twig.Domain/Aggregates/WorkItem.cs:45`).

| Noun | Definition | Defined at |
|---|---|---|
| **SeedIdCounter** | Thread-safe `Interlocked` counter producing unique negative sentinel IDs. | `src/Twig.Domain/Services/Seed/SeedIdCounter.cs:9` |
| **SeedFactory** | Creates seed work items, validating parent/child rules against `ProcessConfiguration` and inheriting area/iteration paths from the parent. | `src/Twig.Domain/Services/Seed/SeedFactory.cs:13` |
| **SeedLink** | Immutable virtual typed link between two seeds, or a seed and an ADO item — links that cannot yet exist in ADO. | `src/Twig.Domain/ValueObjects/SeedLink.cs:6` |
| **SeedPublishResult** | Outcome of publishing one seed to ADO, including the remapped real ID. | `src/Twig.Domain/ValueObjects/SeedPublishResult.cs:6` |
| **SeedValidationResult** | Result of validating one seed against `SeedPublishRules`. | `src/Twig.Domain/ValueObjects/SeedValidationResult.cs:6` |
| **SeedViewGroup** | Read model grouping a parent work item with its seed children. | `src/Twig.Domain/ReadModels/SeedViewGroup.cs:8` |

## 4. Workspace — ⚠ three unrelated meanings

`Workspace` currently names three different things. This is a known defect in the domain
language, not a subtlety. Until it is resolved, **always qualify the term**.

> **Decided 2026-07-26 (wayfinder 0001):** `Workspace` is being RETIRED, not disambiguated.
> Two replacement nouns are agreed:
>
> - **Connection** — one `{org}/{project}` ADO endpoint with its cache and credentials.
>   Replaces `WorkspaceKey`. Twig will support several. *Not* "Scope" — that collides with
>   ADO auth scopes (`vso.work`). *Not* "Project" — collides with `gitProject` in `init`.
> - **Bench** — a named, persistent, switchable set of work items the user is working on.
>   Several may exist concurrently; a bench is selected, not derived. From "workbench":
>   what is on your bench right now.
>
> `Bench` is NOT a rename of `WorkingSet` below. `WorkingSet` is singular, derived, and
> recomputed on every access with no identity or persistence. A Bench is plural, named,
> and persistent — a different concept that happens to occupy adjacent ground. Whether
> `WorkingSet` survives as a Bench's derived projection is an open design question.
>
> Rejected: Set (collides with the `twig set` command), Focus (implies exactly one),
> Track (collides with `TrackedItem`/`TrackingMode`), Thread, Branch, Board, Lane.
>
> **Reserved — `Sprig`.** Considered for Bench and set aside deliberately, not discarded.
> A sprig is a cluster growing off a twig, which is a closer fit for **planning work over
> seeds** — a drafted, still-unpublished chain of items — than for a set of items already
> tracked in ADO. Keep it available for a future planning mode; do not spend it on a
> synonym. (`CONTEXT.md` rule 3: don't invent a name to avoid a rename — the inverse also
> holds, don't burn a good name on the wrong concept.)
>
> ~~Open: whether the pending set is per-Bench or per-Connection; whether a Bench scopes
> the sync boundary as well as reads; whether benches must be concurrent in one process
> or merely switchable.~~
>
> **Resolved 2026-08-06 (wayfinder 0022), except one:**
>
> - **Does a Bench scope the sync boundary?** **No.** Reconciliation scopes to the pending
>   set, per Connection; a Bench is a view and is never a sync unit (0004 §2).
> - **Concurrent or merely switchable?** **Benches are switchable; Contexts are concurrent.**
>   Several Contexts may be open at once, each naming which Bench it stands on.
> - **Does `WorkingSet` survive as a Bench's derived projection?** **Yes — it IS one.**
>   Today's working set is a Bench with one hard-coded query plus hand pins and hand
>   exclusions, computed per access with nowhere to persist the hand edits. 0022 stage 2
>   promotes it rather than replacing it.
> - **Still open:** whether the pending set is *stored* per-Bench or per-Connection. Only
>   the reconciliation boundary is settled.
>
> **A Bench holds** pinned items, queries and exclusions. It stores the RULE, never the
> results. It is a saved backlog — *my sprint*, *the bugs I own*, *release blockers* — named
> once and returned to. Every Context standing on a Bench sees the same Bench; there are no
> private pins.
>
> **Context** (wayfinder 0022) — a **disposable place to stand**, opened by a caller and
> closed when done, holding only the active item and what derives from it. `tree`, `nav` and
> an untargeted `set` need one; any command that names its own work item needs a Connection
> and nothing else. **Being on a Bench means only that you can stand there** — it is a place,
> not a record of interest. A targeted read neither needs a Bench nor joins one.
>
> **Addressing a Context** (wayfinder 0023, owner 2026-08-06):
>
> | Caller | Standing command, no Context named | Non-default Context |
> |---|---|---|
> | human format | the default Context for the current Connection | must name it |
> | machine format | **hard error** | must name it |
>
> **One default Context per Connection**, and it is the only Context twig creates on its own
> — so it is never reaped. A machine-format caller names its Context ALWAYS, including the
> default: that is 0021's rule (every MCP tool names its work item) one level up. The format
> flag is a **declaration, not an inference** — twig must not sniff for a tty.
>
> **An unknown or expired Context is a HARD ERROR** — not a fallback, not a warning, not a
> silent fresh Context. Prior art splits by failure: a *handle* must resolve, so a stale one
> fails loud (docker, ssh-agent); a *name in a shared file* always resolves, so a stale one
> acts on the wrong target silently (kubectl, terraform, gh). twig is the second family
> structurally — a row always resolves — and 0023 moves it to the first.
>
> **Closing a Context never refuses and never gets a force flag.** The pending set belongs to
> the Connection, so close structurally cannot discard work; it exits 0 and reports what
> remains pending. Discard stays a separate explicit verb.
>
> ⚠ Do **not** say a Bench "reconciles" Contexts. `Reconciliation` is 0004's module
> (staged → published → reconciled → invalidated against ADO). What a Bench does is **merge
> views for display**.
>
> **The four experiences** (owner, 2026-07-26) — supersedes the loose
> "human/AI/toolchain/TUI" shorthand, which conflated audience with interaction model:
>
> 1. **Rich CLI** — a human at a terminal; rendered colour/tables/hints/interpretation.
> 2. **Script CLI** — a script or CI job; machine-readable **stdio AND fileio**, a stable
>    parseable contract. (File output is a contract too, not just stdout.)
> 3. **MCP** — an LLM controlling the Bench and pending set, and answering questions about
>    **local OR remote** data. Uniquely has REACH: it may be asked about data twig has
>    never cached.
> 4. **TUI** — a human in a **session launched from the CLI**, with multiple modes and
>    views. Conceptually a CLI thing (same user, same terminal, same mental model), but
>    **may still ship as its own product** — packaging is undecided.
>
> MCP is an **LLM toolkit, not a CLI proxy** (owner, 2026-07-26): a scripting interface
> plus high-level tools aimed at what an LLM reasonably wants to do, driving the
> underlying twig operations itself. Both shapes already exist as one-offs — `twig_batch`
> (sequence/parallel/step graph) and `twig_find_or_create` (encodes idempotent-creation
> intent) — while the other ~39 tools are per-command proxies. Consequence: CLI↔MCP
> **parity is partly the wrong goal**; deliberate divergence is correct for a toolkit.
>
> See wayfinder ticket 0002.

| Qualified name | Meaning | Defined at |
|---|---|---|
| **Workspace (read model)** | Display projection, no identity, no invariants: context item + sprint items + seeds + tracked + excluded. | `src/Twig.Domain/ReadModels/Workspace.cs:10` |
| **WorkspaceKey** | MCP routing unit — identifies a workspace as `{org}/{project}`. | `src/Twig.Mcp/Services/WorkspaceKey.cs:7` |
| **`.twig/` directory** | The on-disk workspace root. | `src/Twig.Mcp/WorkspaceGuard.cs`, `src/Twig.Mcp/Program.cs:15` |
| **WorkspaceContext** (MCP) | Bundle of all per-workspace services for one `(org, project)`; cached for the process lifetime. 27 public service properties. | `src/Twig.Mcp/Services/WorkspaceContext.cs:19` |

Related, unambiguous:

| Noun | Definition | Defined at |
|---|---|---|
| **WorkingSet** | The set of work items relevant to the current context; `AllIds` is the union of all ID collections, recomputed on every access. | `src/Twig.Domain/Services/Workspace/WorkingSet.cs:9` |
| **WorkspaceSection** | A mode-labelled slice of workspace items (Sprint, Area, Recent, Manual). | `src/Twig.Domain/ReadModels/WorkspaceSections.cs:10` |
| **TrackedItem** | A work item explicitly pinned into a workspace, with a `TrackingMode` (single vs subtree). | `src/Twig.Domain/ValueObjects/TrackedItem.cs:11` |
| **ExcludedItem** | A work item explicitly removed from a workspace view, with a reason. | `src/Twig.Domain/ValueObjects/ExcludedItem.cs:9` |

## 5. Navigation & hierarchy

| Noun | Definition | Defined at |
|---|---|---|
| **WorkTree** | Immutable composite read model for navigating a hierarchy. Navigation methods return **IDs, not mutated trees** — the caller rebuilds at the target ID. | `src/Twig.Domain/ReadModels/WorkTree.cs:13` |
| **SprintHierarchy** | Inert immutable read model organising sprint items into per-assignee trees; build logic lives in `SprintHierarchyBuilder`. | `src/Twig.Domain/ReadModels/SprintHierarchy.cs:50` |
| **SprintHierarchyNode** | Node in that tree: a `WorkItem` plus an in-sprint flag, **or** a virtual group header ("Unparented Features") with children but no work item. | `src/Twig.Domain/ReadModels/SprintHierarchy.cs:12` |
| **NavigationHistoryEntry** | One visited work item in the back/forward stack. | `src/Twig.Domain/ValueObjects/NavigationHistoryEntry.cs:3` |
| **WorkItemLink** | Immutable **non-hierarchy** link between two work items (related / predecessor / successor). | `src/Twig.Domain/ValueObjects/WorkItemLink.cs:6` |
| **AreaView** | Read model for the area-filtered view: items matching configured area paths plus hydrated parent context. | `src/Twig.Domain/ReadModels/AreaView.cs:10` |
| **DescendantVerificationResult** | Result of verifying every descendant of an item is terminal (Completed / Resolved / Removed). | `src/Twig.Domain/ReadModels/DescendantVerificationResult.cs:7` |

## 6. Classification value objects

| Noun | Definition | Defined at |
|---|---|---|
| **AreaPath** | Validated area path: non-empty, backslash-separated segments, with ancestor/descendant comparison. | `src/Twig.Domain/ValueObjects/AreaPath.cs:8` |
| **IterationPath** | Same contract for iteration paths; case-insensitive ordinal descendant check on segment boundaries. | `src/Twig.Domain/ValueObjects/IterationPath.cs:8` |
| **IterationExpression** | Validated sprint expression — relative (`@current`, `@current±N`) or absolute literal path. | `src/Twig.Domain/ValueObjects/IterationExpression.cs:20` |
| **TeamIteration** | One team iteration (sprint) from ADO team settings: path + optional dates. | `src/Twig.Domain/ValueObjects/TeamIteration.cs:7` |
| **WorkItemType** | A known ADO work item type, stored as a string. Well-known constants are **advisory only, not behavioural constraints**. | `src/Twig.Domain/ValueObjects/WorkItemType.cs:9` |
| **StateEntry** | One state in a type's workflow: name, resolved `StateCategory`, optional hex colour. | `src/Twig.Domain/ValueObjects/StateEntry.cs:9` |
| **FieldDefinition** | Cached metadata for an ADO field. | `src/Twig.Domain/ValueObjects/FieldDefinition.cs:7` |
| **FieldProfile** | How often a field is populated across cached work items of a type. | `src/Twig.Domain/ValueObjects/FieldProfile.cs:7` |

## 7. Query, sync, cache

| Noun | Definition | Defined at |
|---|---|---|
| **QueryResult** | Immutable read model of WIQL results. `IsTruncated` is a best-effort heuristic (count == requested `$top`). | `src/Twig.Domain/ReadModels/QueryResult.cs:13` |
| **CacheStatistics** | Freshness stats from the local SQLite store (last sync, pending count, tracked count, oldest age). | `src/Twig.Domain/ReadModels/CacheStatistics.cs:7` |
| **WorkItemHistory** | Chronologically ordered revision history for one work item; `Complete` flags whether the timeline is exhaustive. | `src/Twig.Domain/ValueObjects/WorkItemHistory.cs:13` |
| **GitContext** | Best-effort git enrichment (branch, linked PRs); all fields optional, detection failures swallowed. | `src/Twig.Domain/ValueObjects/GitContext.cs:9` |
| **ExportedWorkItem** | Round-trip carrier between `WorkItemExportFormat.Generate` and `.Parse` (the `$EDITOR` round trip). | `src/Twig.Domain/ValueObjects/ExportedWorkItem.cs:8` |
| **ProfileMetadata** | Metadata for a global process profile at `~/.twig/profiles/{org}/{process}/profile.json`. | `src/Twig.Domain/ValueObjects/ProfileMetadata.cs:7` |

---

## 8. Names the code does NOT use

Reaching for one of these means you're about to name something that already has a name.

| Don't say | Say | Why |
|---|---|---|
| `Note` | **PendingNote** (staged) or `WorkItem.AddNote` (the operation) | No `Note` type exists. |
| `PendingChange` | **PendingChangeRecord** | No `PendingChange` type exists. |
| `Seed` (as a type) | **`WorkItem.IsSeed`** + negative ID | Seed-ness is a flag, not a class. |
| bare `Workspace` | one of the four qualified forms in §4 | Three unrelated meanings. |

## 9. Surfaces and composition roots

Twig has **four consumer surfaces** and **three composition roots**. These are domain facts, not
implementation detail — they shape every naming decision.

| Surface | Project | Consumer | Wants |
|---|---|---|---|
| **Human** | `src/Twig` (`twig`) | a person at a terminal | colour, tables, truncation, hints, interpretation |
| **AI** | `src/Twig.Mcp` (`twig-mcp`) | an agent over MCP | structure + affordances, token-frugal |
| **Toolchain** | currently flags inside `src/Twig` | scripts, pipes, CI | stable, versioned, parseable; **no** hints or interpretation |
| **TUI** | `src/Twig.Tui` (`twig-tui`) | a person editing interactively | Terminal.Gui views over the same items |

The toolchain surface has a property the others don't: **it is a contract with a stable shape**.
Breaking it silently breaks a consumer's script.

**Three composition roots** — `src/Twig` (DI), `src/Twig.Mcp` (`WorkspaceContextFactory`, which
hand-mirrors CLI DI and says so at `WorkspaceContextFactory.cs:30-33`), and `src/Twig.Tui` (its own
`Microsoft.Extensions.DependencyInjection` wiring). The obstacle to unifying them is **cardinality**,
not DI-vs-manual: CLI is one workspace per process, MCP is N keyed by `WorkspaceKey`.

**Only `src/Twig` references `Twig.RenderTree`.** MCP and the TUI each hand-rolled their own output
stack rather than using the `IRenderer` + `RenderAudience` seam the CLI already has. The same
divergence, twice, independently.

## 10. Architecture vocabulary

For structural discussion — module, interface, depth, seam, adapter, leverage, locality — twig
uses the deep-module vocabulary rather than "component / service / API / boundary". Definitions
live with the design skill, not here; this file names the **domain**, that one names the
**structure**.
