# Domain Model Critique — April 2026

> **Purpose**: Honest architectural assessment of the Twig domain layer,
> identifying design friction, anti-patterns, and areas for remediation.
> Each section maps to a tracked Epic in ADO for investigation and resolution.

---

## 1. WorkItem Aggregate — God Object

**Severity**: High | **Blast Radius**: Core domain, all consumers
> **Status (2026-07, re-baselined at `55b02d32`)**: PARTLY — two of the three specific issues are
> gone, the aggregate-boundary complaint is not.
> FIXED: the divergent copy methods are consolidated — `WorkItemCopier.Copy` is the single copy
> path (`src/Twig.Domain/Aggregates/WorkItemCopier.cs:10`) and every `With*` delegates to it
> (`WorkItem.cs:141`, `:184`, `:192`, `:199`), with `preserveDirty` now an explicit parameter
> rather than an accident.
> FIXED: the static mutable `_seedIdCounter` no longer exists anywhere in `src/` — identity is
> minted per seed from the durable register (`src/Twig.Domain/Interfaces/IStagedIdentityRegistry.cs:15`)
> and carried on `WorkItem.StagedIdentity` (`WorkItem.cs:57`); the negative `Id` is a display alias
> only. The `SeedFactory` the critique asked for exists and takes the minted identity as a
> parameter (`src/Twig.Domain/Services/Seed/SeedFactory.cs:19`, `:30-36`) — it has no counter to
> initialise. The only surviving trace of `ISeedIdCounter` is a `*REMOVED*` line in
> `src/Twig.Domain/PublicAPI.Unshipped.txt:656`.
> STILL TRUE: `ChangeState` performs no process validation — it null-checks and assigns
> (`WorkItem.cs:89-96`); validation still lives outside in
> `src/Twig.Domain/Services/Process/StateTransitionService.cs:19`. `SetField`/`ImportFields`
> remain `internal` (`WorkItem.cs:70`, `:76`), so same-assembly code can still bypass the
> mutation methods.


The `WorkItem` class simultaneously serves as entity, command queue, field bag,
seed factory, and copy factory. It does not protect domain invariants — callers
can enqueue `ChangeState("garbage")` with no validation against process rules.
Validation lives entirely in external services (`StateTransitionService`,
`FlowTransitionService`), which means the aggregate root boundary is
architecturally meaningless.

### Specific Issues

- **Copy methods diverge silently**: `WithSeedFields` doesn't preserve `IsDirty`;
  `WithParentId` does; `WithIsSeed` doesn't. Each manually reconstructs all
  properties — a guaranteed bug factory as new properties are added.
- **Static mutable `_seedIdCounter`**: Global shared state inside a domain entity.
  Couples all instances, makes parallel tests nondeterministic.
- **No encapsulation of field bag**: `ImportFields` and `SetField` are `internal`,
  but any same-assembly code can bypass the command queue entirely.

### Containment Practices

- Introduce a `WorkItemBuilder` or `WorkItemCopier` that centralizes the
  `With*` copy logic in a single place, tested once.
- Extract seed creation to a dedicated `SeedFactory` service.
- Consider making `ChangeState` accept a `ProcessConfiguration` parameter so
  the aggregate can validate transitions at the boundary — but scope this
  carefully, as it introduces a domain dependency into the entity.
- Do **not** attempt to refactor `WorkItem` fields, identity, or init patterns
  in the same PR as behavioral changes.

---

## 2. Command Queue Pattern — Complexity Without Payoff

**Severity**: Medium | **Blast Radius**: WorkItem, Commands/, all mutating commands
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED — the pattern is gone. `git grep` for
> `IWorkItemCommand`, `ApplyCommands`, and `ToFieldChange` returns zero hits in `src/`, and there is
> no `Commands/` directory under `src/Twig.Domain`. It was replaced by exactly the direct-mutation
> shape the critique proposed: `ChangeState` and `UpdateField` mutate and return a `FieldChange`
> (`src/Twig.Domain/Aggregates/WorkItem.cs:89`, `:97`). The `IsDirty` tracking the critique flagged
> as load-bearing survives (`WorkItem.cs:36`) and `SyncGuard` still consumes it via
> `GetDirtyItemsAsync` (`src/Twig.Domain/Services/Sync/SyncGuard.cs:19`).


The `IWorkItemCommand` → queue → `ApplyCommands()` pattern resembles Event
Sourcing prep but delivers none of its benefits. Commands are enqueued and
applied in the same process, never persisted, never replayed. The `ToFieldChange()`
precondition ("must call Execute first") is temporal coupling.

### Specific Issues

- Commands are stateful after execution (`_oldState` captured during `Execute`).
- `ToFieldChange()` returns misleading data if called before `Execute`.
- The pattern could be replaced by direct mutation methods returning `FieldChange`.

### Containment Practices

- If removing the pattern, ensure the `FieldChange` return path is preserved —
  several callers depend on the change list from `ApplyCommands()`.
- Refactor in a standalone PR that touches only `WorkItem`, `Commands/`, and
  their direct callers. Do not combine with other WorkItem structural changes.
- Retain the `IsDirty` tracking behavior — it's load-bearing for `SyncGuard`.

---

## 3. Hardcoded Process Assumptions

**Severity**: Medium | **Blast Radius**: ProcessConfiguration, TransitionKind
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED (main issue) / STILL TRUE (secondary).
> FIXED: `BuildTypeConfig` no longer compares against a magic string. The cut/forward decision is
> made on `stateEntries[j].Category == StateCategory.Removed`
> (`src/Twig.Domain/Aggregates/ProcessConfiguration.cs:184`), exactly the `StateEntry` category
> lookup the containment practice asked for. `private const string RemovedStateName` does not exist
> in `src/`; the only remaining `"Removed"` literals are the two ADO-wire parsers in
> `src/Twig.Domain/Services/Process/StateCategoryResolver.cs:50` and `:63`, which are parsing
> Microsoft's own category vocabulary and are correct there.
> STILL TRUE (as documented, not as defect): `WorkItemType` still declares 13 static well-known
> types and still case-normalizes through `NormalizeCasing`
> (`src/Twig.Domain/ValueObjects/WorkItemType.cs:69`), which the critique itself asked to leave in
> place — but the "document that they are advisory" half was not done.


Despite the explicit process-agnostic principle, `ProcessConfiguration.BuildTypeConfig`
hardcodes `"Removed"` as the cut/destructive state name. CMMI and custom processes
may not use this name. The `StateCategory.Removed` enum already exists and should
be used instead of the magic string.

### Specific Issues

- `private const string RemovedStateName = "Removed";` — string-based check.
- `WorkItemType` declares 13 static well-known types with case-normalization
  that silently overrides custom type names matching known ones.

### Containment Practices

- Replace `RemovedStateName` string comparison with `StateCategory` lookup —
  the `StateEntry` already carries category metadata.
- Leave the `WorkItemType` static fields in place (they're convenient for tests)
  but document that they are advisory, not behavioral.
- This is a small, surgical change — 1–2 files.

---

## 4. Value Object Structural Inconsistencies

**Severity**: Low | **Blast Radius**: ValueObjects/
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED. `AreaPath` is no longer a
> `readonly record struct` — it is a plain `readonly struct : IEquatable<AreaPath>` with
> hand-written `Equals`/`GetHashCode` over `Value` only, so the cached `_segments` array no longer
> fights generated equality (`src/Twig.Domain/ValueObjects/AreaPath.cs:8`, `:12`, `:44-46`).
> `IterationPath` has the same shape (`IterationPath.cs:8`). The duplicated `\`-separated
> validation is now a shared helper: both call `PathValidation.ValidateBackslashPath` and both get
> `IsUnder` from `PathValidation.IsUnder` (`src/Twig.Domain/ValueObjects/PathValidation.cs:41`;
> `AreaPath.cs:43`, `IterationPath.cs:31`).


`AreaPath` is a `readonly record struct` with custom `Equals`/`GetHashCode` to
work around the generated equality including a cached `_segments` array. This
fights the compiler. `IterationPath` is nearly identical structurally but lacks
segment caching and `IsUnder()`. Both validate `\`-separated paths identically
but share no code.

### Containment Practices

- Introduce a shared validation helper or base abstraction for path types.
- Converting `AreaPath` from `record struct` to a regular `readonly struct` or
  `sealed record` class would eliminate the equality workaround.
- Change only the ValueObjects in isolation; no command-layer changes needed.

---

## 5. Service Layer Organization (56 Flat Files)

**Severity**: Medium | **Blast Radius**: Services/ folder structure
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED. `src/Twig.Domain/Services/` now holds 70
> `.cs` files, of which only **4** sit at the top level (`SprintIterationResolver.cs`,
> `WorkItemHistoryJsonWriter.cs`, `WorkItemHistoryOptionsParser.cs`, `WorkItemMapper.cs`). The rest
> are organised into seven concern subdirectories — `Field/`, `Mutation/`, `Navigation/`,
> `Process/`, `Seed/`, `Sync/`, `Workspace/` — which is the sub-organisation the containment
> practice proposed. The named examples moved with it: `RefreshOrchestrator` is at
> `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs`, `SeedPublishOrchestrator` at
> `Services/Seed/SeedPublishOrchestrator.cs`, `CacheAgeFormatter` at `Services/Field/`, and
> `Pluralizer` left `Services/` entirely for `src/Twig.Domain/Common/Pluralizer.cs`. April's "56
> flat files" no longer describes the tree.


The `Services/` folder contains 56 files with no sub-organization — from tiny
utilities (`Pluralizer`, `CacheAgeFormatter`) to complex orchestrators
(`RefreshOrchestrator`, `SeedPublishOrchestrator`). Discoverability is poor
and the "where does this go?" problem grows with every addition.

### Containment Practices

- Introduce subdirectories by concern: `Services/Sync/`, `Services/Seed/`,
  `Services/Workspace/`, `Services/Navigation/`, etc.
- This is a **namespace-only** refactor — move files, update `namespace`
  declarations, update `using` statements. No behavioral changes.
- Do in a single, review-friendly PR with only file moves and namespace edits.
  No logic changes in the same PR.

---

## 6. Orchestrator Proliferation

**Severity**: Medium | **Blast Radius**: Multiple orchestrators + commands
> **Update (2026-07-29, `64ef6d08`)**: the audit now has an ENFORCEMENT POINT, which is what it
> was actually missing — see issue #318. `OrchestratorInventoryTests` declares every orchestrator
> in the domain, so adding, removing, or renaming one fails the build until the inventory is
> updated deliberately. That is the moment to apply this finding's retention criteria; without it
> the audit degraded into a stale snapshot within a day (`SeedDiscardOrchestrator` was added
> 2026-04-28, the day after this section was written, and was never assessed).
> Re-measured on `64ef6d08`, the counts also came back DOWN as 0013/0014/0015 pulled work out:
> `SeedPublishOrchestrator.cs` **527** (was 616 at re-baseline, 245 at audit),
> `SeedDiscardOrchestrator.cs` 125, `RefreshOrchestrator.cs` 215, `SyncCoordinator.cs` 341.
> `SeedReconcileOrchestrator` is gone — renamed to `SeedLinkRepair` by wayfinder 0004 slice 1.
> Whether `SeedPublishOrchestrator` at 2.1x its audited size is now too big for one boundary
> remains open and is NOT settled by the inventory guard.
>
> **Status (2026-07, re-baselined at `55b02d32`)**: PARTLY — the April audit's own findings hold,
> but the count went back up and wayfinder 0004's ruling has not landed.
> Holds: `StatusOrchestrator` is gone (zero hits in `src/`), and the five retained/absorbed verdicts
> still match the code, though the line counts have drifted —
> `SyncCoordinator.cs` 281 (was 211), `RefreshOrchestrator.cs` 191 (was 193),
> `SeedPublishOrchestrator.cs` **616** (was 245), `SeedReconcileOrchestrator.cs` 117 (was 110).
> `SyncCoordinatorFactory` still exists un-renamed (`Services/Sync/SyncCoordinatorFactory.cs`).
> STILL TRUE / new: a **sixth** orchestrator exists that the audit never covered —
> `src/Twig.Domain/Services/Seed/SeedDiscardOrchestrator.cs:1` (137 lines), created in `55ad9c2b`
> on 2026-04-28, the day *after* the audit was written to this document in `f8bee61c`
> (2026-04-27). Tracked as #318.
> STILL TRUE: wayfinder ticket 0004 ruled that reconciliation becomes a named module and that
> `SeedReconcileOrchestrator` be renamed to reflect that it is a seed-link GC
> (`wayfinder/tickets/0004-does-reconciliation-exist.md:185-192`, scope explicitly "decision only").
> No such module exists — `git grep Reconciliation` in `src/` returns only two display strings
> (`src/Twig/Commands/SeedReconcileCommand.cs:97`,
> `src/Twig/Formatters/HumanOutputFormatter.cs:1412`). It is still just a decision.


Five orchestrator/coordinator patterns existed with overlapping dependency subsets.
An audit was performed in April 2026 to evaluate each one for consolidation,
removal, or retention.

### Resolved

- **`StatusOrchestrator`** — Absorbed into `ContextTools.Status()` as inline
  logic. The orchestrator was a thin wrapper that duplicated resolution already
  available in `ActiveItemResolver`. `StatusSnapshot` is retained as a
  standalone DTO in `Services/Workspace/StatusSnapshot.cs`.

### Retained (with rationale)

- **`SyncCoordinator`** — 211 lines, 6 dependencies, 20+ call sites.
  Load-bearing cache/ADO sync infrastructure with no consolidation target.
  Its broad usage and distinct responsibility (bidirectional sync lifecycle)
  make it unsuitable for inlining or merging.
- **`SyncCoordinatorFactory`** — 13+ call sites. The name implies a factory
  pattern but the class is a pair-holder for two pre-configured `SyncCoordinator`
  instances. A rename to `SyncCoordinatorPair` would better reflect its semantics,
  but the rename is deferred — the class is load-bearing and the cost/risk of
  updating all call sites is not justified in a documentation-only change.
- **`RefreshOrchestrator`** — 193 lines, 9 dependencies, 1 consumer
  (`RefreshCommand`). Manages the full refresh lifecycle: WIQL fetch, conflict
  resolution, and ancestor hydration. Substantial logic with clean 1:1
  command delegation — not a thin wrapper.
- **`SeedPublishOrchestrator`** — 245 lines, 8 dependencies, 1 consumer
  (`SeedPublishCommand`). Handles transactional seed publish with topological
  ordering. Complex enough to justify its own orchestration boundary.
- **`SeedReconcileOrchestrator`** — 110 lines, 3 dependencies, 1 consumer
  (`SeedReconcileCommand`). Performs orphan detection and stale link repair.
  Appropriate scope with no overlap with other services.

### Containment Practices

- Future orchestrator additions should follow the pattern established by the
  retained orchestrators: substantial logic, clean 1:1 command delegation,
  and no overlap with existing services.
- Do not consolidate orchestrators in the same PR as behavioral changes.

---

## 7. Result Type Proliferation

**Severity**: Medium | **Blast Radius**: Cross-cutting (all services/commands)
> **Status (2026-07, re-baselined at `55b02d32`)**: PARTLY — both "In Progress" migrations landed;
> the "Deferred" bucket is untouched.
> FIXED: `StatusSnapshot` no longer exists in `src/` at all. It is now the DU the section specified,
> with exactly the three named cases: `public union StatusResult(StatusNoContext,
> StatusUnreachable, StatusSuccess)` (`src/Twig.Domain/Services/Workspace/StatusResult.cs:25`).
> FIXED: `BranchLinkResult` is no longer an enum+class hybrid — `BranchLinkStatus` is gone and it is
> `public union BranchLinkResult(Linked, AlreadyLinked, GitContextUnavailable, LinkFailed)`
> (`src/Twig.Domain/ValueObjects/BranchLinkResult.cs:32`). Only the fourth case is named
> `LinkFailed` rather than the planned `Failed`.
> STILL TRUE: the deferred types are unchanged data bags — `SeedPublishResult` is still a
> `public sealed class` (`src/Twig.Domain/ValueObjects/SeedPublishResult.cs:6`), alongside
> `SeedValidationResult.cs`, `SeedReconcileResult.cs`, `SeedPublishBatchResult.cs`; and
> `RefreshFetchResult` remains a single-consumer data bag
> (`src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs`, `src/Twig/Commands/RefreshCommand.cs`).
> Both were deliberate deferrals, so this is unfinished-by-design rather than drift.


> **Convention document**: [`docs/architecture/result-type-conventions.md`](result-type-conventions.md)
> establishes the three-tier taxonomy (Discriminated Union / `Result<T>` / Data Bag)
> and decision matrix for choosing between them.

The codebase has 9+ distinct result types with incompatible patterns: `Result<T>`,
`ActiveItemResult` (DU, 4 cases), `SyncResult` (DU, 5 cases), `StatusSnapshot`
(boolean tri-state), `RefreshFetchResult`, and three Seed result types.

### Resolved

- **`FlowResolveResult` / `FlowTransitionResult`** — removed during prior
  refactoring (git/flow command deletion). No longer exist in the codebase.
  These items from the original critique are fully resolved.

### In Progress

- **`StatusSnapshot`** — boolean tri-state with nullable fields. Scheduled for
  migration to a discriminated union (`StatusResult`) with `NoContext`,
  `Unreachable`, and `Success` subtypes. See the convention document for the
  target pattern.
- **`BranchLinkResult`** — enum+class hybrid (`BranchLinkStatus` enum alongside
  a sealed record). Scheduled for migration to a DU with `Linked`,
  `AlreadyLinked`, `GitContextUnavailable`, and `Failed` subtypes.

### Deferred

- **Seed result types** (`SeedPublishResult`, `SeedValidationResult`,
  `SeedReconcileResult`, `SeedPublishBatchResult`) — data bags with many
  properties and wide formatter surface area (4 formatter implementations each).
  Converting to DUs would touch 40+ files for marginal benefit. Candidates for
  a future epic.
- **`RefreshFetchResult`** — pure data bag (counters + conflict list) with a
  single consumer. No distinct outcome paths — not a DU candidate.

### Containment Practices

- Follow the convention document's three-tier taxonomy when choosing a result
  pattern for new code.
- Migrate one result type at a time per PR. Do **not** attempt a bulk
  unification — the blast radius is enormous.
- Start with `StatusSnapshot` → convert to discriminated union pattern matching
  `ActiveItemResult`'s style, then proceed to `BranchLinkResult`.

---

## 8. Command Layer Bloat

**Severity**: High | **Blast Radius**: CLI Commands/
> **Update (2026-07-29, `64ef6d08`)**: a RATCHET now stops this finding relocating again — see
> issue #319. `CommandConstructorSizeTests` caps command constructors at the current worst case
> (15) and pins the known offenders so none may grow: `ShowCommand` 15, `WorkspaceCommand` 14,
> `UpdateCommand` 14 — the third was not named by #319 and was found while setting the ceiling.
> **The remedy this document prescribes does not work on these.** `WorkspaceCommand` already takes
> `CommandContext` as its first parameter and is still at 14, so the aggregate-parameter object is
> not sufficient; splitting these needs a rendering/service seam and is a design slice in its own
> right, deliberately not attempted with the guard. Lower the ceiling as offenders are split;
> raising it to admit a new command is the failure mode the guard exists to prevent.
>
> **Status (2026-07, re-baselined at `55b02d32`)**: PARTLY.
> FIXED for the two named commands:
> `src/Twig/Commands/StatusCommand.cs`; only `StatusFieldConfigReader.cs` survives that name), and
> `SetCommand` is down to **7** constructor parameters across 192 lines
> (`src/Twig/Commands/SetCommand.cs:24-31`). The `CommandContext` aggregate parameter object the
> critique proposed exists and is the first parameter of both
> (`src/Twig/Commands/CommandContext.cs:13`), and inline infrastructure access was pulled into
> `src/Twig/Commands/StatusFieldConfigReader.cs:8`.
> STILL TRUE as a class-level finding: the bloat relocated rather than disappeared.
> `WorkspaceCommand` now takes **14** constructor parameters over 851 lines
> (`src/Twig/Commands/WorkspaceCommand.cs:34-48`), and `ShowCommand.cs` (792) and `InitCommand.cs`
> (739) are the same shape. No `CommandRenderingPipeline` exists. Tracked as #319.


`StatusCommand` and `SetCommand` each take 15–17 constructor parameters. Method
bodies exceed 200 lines with duplicated rendering paths (renderer vs. formatter),
inline infrastructure access (`File.ReadAllTextAsync`), and interleaved
orchestration + display + hints + telemetry + sync logic.

### Containment Practices

- Extract shared rendering/sync patterns into a `CommandRenderingPipeline` or
  similar — but only after stabilizing the orchestrator layer.
- Reduce constructor params via a `CommandContext` aggregate parameter object.
- Do **not** refactor command structure simultaneously with domain model changes.
- Address commands one at a time — `StatusCommand` first, then propagate
  patterns to others.

---

## 9. Domain ↔ Infrastructure Boundary Leak

**Severity**: Medium | **Blast Radius**: IAdoWorkItemService, persistence
> **Status (2026-07, re-baselined at `55b02d32`)**: PARTLY — a mapping layer now exists, but the
> interface boundary the finding is actually about is unchanged.
> FIXED (construction): infrastructure no longer constructs domain aggregates during
> deserialization. `AdoResponseMapper.MapToSnapshot` produces an infrastructure-agnostic
> `WorkItemSnapshot` (`src/Twig.Infrastructure/Ado/AdoResponseMapper.cs:47`), and the domain owns
> all value-object parsing and state restoration in `WorkItemMapper.Map`
> (`src/Twig.Domain/Services/WorkItemMapper.cs:10`). `AdoRestClient` just wires the two
> (`src/Twig.Infrastructure/Ado/AdoRestClient.cs:74-75`). `SqliteWorkItemRepository` goes through
> the same snapshot seam (`src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs:512`),
> and there is a real wire-DTO tier under `src/Twig.Infrastructure/Ado/Dtos/` (19 files).
> STILL TRUE: `IAdoWorkItemService` still returns domain `WorkItem` aggregates directly —
> `Task<WorkItem> FetchAsync`, `FetchWithLinksAsync`, `FetchChildrenAsync`, `FetchBatchAsync`
> (`src/Twig.Domain/Interfaces/IAdoWorkItemService.cs:12`, `:13`, `:14`, `:61`), so `WorkItem`
> changes still cascade to every consumer and test. Note the critique's own sequencing advice is now
> discharged: it said defer until Item 1's copy-method consolidation was done, and it is.


`IAdoWorkItemService` returns domain `WorkItem` aggregates directly. The
infrastructure ADO client constructs full domain objects during deserialization.
There's no mapping/DTO layer, so changes to `WorkItem` cascade through
infrastructure and all tests.

### Containment Practices

- This is the highest-risk refactor in this list. A DTO layer affects every
  test that constructs `WorkItem` instances.
- **Defer** this until the WorkItem copy-method consolidation (Item 1) is done,
  since both touch the same construction paths.
- When attempted, start by introducing DTOs for the ADO write path only
  (`PatchAsync`, `CreateAsync`) — the read path is harder to change.

---

## 10. SprintHierarchy.Build — Misplaced Complex Logic

**Severity**: Low | **Blast Radius**: ReadModels/, SprintHierarchy
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED — both containment practices landed.
> `SprintHierarchy.Build` no longer exists; `src/Twig.Domain/ReadModels/SprintHierarchy.cs` is down
> to 66 lines and exposes only a `Create` factory over an already-built dictionary (`:63`). The
> 200-line walk moved to the service the critique asked for:
> `src/Twig.Domain/Services/Workspace/SprintHierarchyBuilder.cs:15` behind
> `src/Twig.Domain/Interfaces/ISprintHierarchyBuilder.cs:10`, injected into commands
> (`src/Twig/Commands/WorkspaceCommand.cs:43`). The `.Any()`-in-`while` ceiling check is now a
> `HashSet<string>` with the reason stated inline: "Pre-compute ceiling type set for O(1) lookup
> instead of O(n) .Any()" (`SprintHierarchyBuilder.cs:70-71`).


`SprintHierarchy.Build` is a 200-line static method doing parent-chain walking,
virtual group creation, ceiling-type resolution, and LINQ `.Any()` inside
`while` loops. This is domain logic hiding inside a read model factory.

### Containment Practices

- Extract to a `SprintHierarchyBuilder` service.
- Replace `.Any()` ceiling check with a `HashSet<string>` lookup.
- Small, isolated refactor — no external API changes.

---

## 11. Workspace Read Model Does Computation

**Severity**: Low | **Blast Radius**: ReadModels/Workspace
> **Status (2026-07, re-baselined at `55b02d32`)**: FIXED. The finding is that a read model does
> computation; it no longer does. `src/Twig.Domain/ReadModels/Workspace.cs` is 78 lines of
> init-only projection state (`:13-31`) plus a `Build` factory (`:54`) and one trivial
> `IsTracked` membership check (`:69`) — none of `GetStaleSeeds`, `GetDirtyItems`, or `ListAll`
> is a member. They are extension methods in
> `src/Twig.Domain/ReadModels/WorkspaceExtensions.cs:15`, `:32`, and `:54`, whose own doc comment
> states the intent: "Pure computation methods extracted from `Workspace`. Keeps the read model
> as an inert projection" (`WorkspaceExtensions.cs:5-8`).
>
> Recorded deliberately: this is *not* the containment practice as written, which asked for the
> computation to move into the building service or a `WorkspaceAnalyzer` (no such type exists in
> `src/`). It went to extensions in the same `ReadModels` namespace instead. That was judged to
> discharge the finding on 2026-07-27 — the stated defect was inertness of the read model, and
> the extension-method placement achieves it; `WorkspaceAnalyzer` was one suggested means, not
> the finding itself. Reopen if the extensions start accumulating state or policy.
>
> One sub-claim is stale rather than fixed: "callers that invoke these methods are few — grep
> before changing" now understates it at 8 call sites —
> `src/Twig.Mcp/Services/McpResultBuilder.cs:180`, `:189`,
> `src/Twig/Commands/WorkspaceCommand.cs:687`, `:702`, `:741`, and
> `src/Twig/Formatters/HumanOutputFormatter.cs:491`, `:521`, `:677`.


`Workspace.GetStaleSeeds()`, `GetDirtyItems()`, `ListAll()` are computation
methods on a read model. Read models should be inert projections.

### Containment Practices

- Move computation to the service that builds the `Workspace`, or to a
  dedicated `WorkspaceAnalyzer` service.
- Callers that invoke these methods are few — grep before changing.

---

## Recommended Remediation Order

> **Status (2026-07, re-baselined at `55b02d32`)**: mostly obsolete. Items **2, 3, 4, 5, 10** are
> FIXED and drop off the list entirely. Item **6** is *not* "completed April 2026" as this list's ninth entry claims:
> the audit completed, but a sixth orchestrator it never covered exists
> (`src/Twig.Domain/Services/Seed/SeedDiscardOrchestrator.cs:1`, #318) and wayfinder 0004's named
> reconciliation module has not landed (#320). Item **7** is no longer "in progress" — both scheduled
> migrations shipped (`Services/Workspace/StatusResult.cs:25`,
> `ValueObjects/BranchLinkResult.cs:32`); only the explicitly deferred seed result types remain.
> Item **11** is FIXED and also drops off (ruled 2026-07-27: extension-method placement discharges
> the inertness finding even though `WorkspaceAnalyzer` was never built). What actually remains, in
> the order this section's own risk-ordering logic implies: **1**'s residual aggregate-boundary
> question, **8** (bloat relocated to `WorkspaceCommand`/`ShowCommand`/`InitCommand`), **6**'s
> reconciliation module, and **9** — which the original ordering placed last on the grounds that
> Item 1's copy consolidation had to come first, and that precondition is now met.

1. **Item 3** (Process assumptions) — smallest, most surgical
2. **Item 4** (Value object cleanup) — isolated, low risk
3. **Item 10** (SprintHierarchy extraction) — isolated
4. **Item 11** (Workspace computation extraction) — isolated
5. **Item 2** (Command queue simplification) — medium scope
6. **Item 1** (WorkItem consolidation) — high impact, needs care
7. **Item 5** (Service folder structure) — namespace-only
8. **Item 7** (Result type convention) — 🔨 in progress (convention document established; `StatusSnapshot` and `BranchLinkResult` migrations pending)
9. **Item 6** (Orchestrator audit) — ✅ completed April 2026
10. **Item 8** (Command bloat) — after orchestrator cleanup
11. **Item 9** (DTO boundary) — last, highest risk
