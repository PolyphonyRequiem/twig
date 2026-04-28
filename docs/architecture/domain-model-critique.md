# Domain Model Critique — April 2026

> **Purpose**: Honest architectural assessment of the Twig domain layer,
> identifying design friction, anti-patterns, and areas for remediation.
> Each section maps to a tracked Epic in ADO for investigation and resolution.

---

## 1. WorkItem Aggregate — God Object

**Severity**: High | **Blast Radius**: Core domain, all consumers

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

Five orchestrator/coordinator patterns existed with overlapping dependency subsets.
An audit was performed in April 2026 to evaluate each one for consolidation,
removal, or retention.

### Resolved

- **`StatusOrchestrator`** — Absorbed into `ContextTools.Status()` as inline
  logic. The orchestrator was a thin wrapper that duplicated resolution already
  available in `ActiveItemResolver`. `StatusSnapshot` is retained as a
  standalone DTO in `Services/Workspace/StatusSnapshot.cs`.
- **`SyncCoordinatorFactory`** — Renamed to `SyncCoordinatorPair` to accurately
  reflect its pair-holder semantics. The original name implied a factory pattern
  that did not match the actual behavior (holding two pre-configured
  `SyncCoordinator` instances).

### Retained (with rationale)

- **`SyncCoordinator`** — 211 lines, 6 dependencies, 20+ call sites.
  Load-bearing cache/ADO sync infrastructure with no consolidation target.
  Its broad usage and distinct responsibility (bidirectional sync lifecycle)
  make it unsuitable for inlining or merging.
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

The codebase has 9+ distinct result types with incompatible patterns: `Result<T>`,
`ActiveItemResult` (DU, 4 cases), `SyncResult` (DU, 5 cases),
`FlowResolveResult`/`FlowTransitionResult` (ad-hoc classes), `StatusSnapshot`
(boolean tri-state), `RefreshFetchResult`, and three Seed result types.

### Containment Practices

- Establish a convention: discriminated unions (abstract record) for operations
  with distinct outcome paths; `Result<T>` for simple success/fail.
- Migrate one result type at a time per PR. Do **not** attempt a bulk
  unification — the blast radius is enormous.
- Start with `StatusSnapshot` → convert to discriminated union pattern matching
  `ActiveItemResult`'s style.

---

## 8. Command Layer Bloat

**Severity**: High | **Blast Radius**: CLI Commands/

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

`Workspace.GetStaleSeeds()`, `GetDirtyItems()`, `ListAll()` are computation
methods on a read model. Read models should be inert projections.

### Containment Practices

- Move computation to the service that builds the `Workspace`, or to a
  dedicated `WorkspaceAnalyzer` service.
- Callers that invoke these methods are few — grep before changing.

---

## Recommended Remediation Order

1. **Item 3** (Process assumptions) — smallest, most surgical
2. **Item 4** (Value object cleanup) — isolated, low risk
3. **Item 10** (SprintHierarchy extraction) — isolated
4. **Item 11** (Workspace computation extraction) — isolated
5. **Item 2** (Command queue simplification) — medium scope
6. **Item 1** (WorkItem consolidation) — high impact, needs care
7. **Item 5** (Service folder structure) — namespace-only
8. **Item 7** (Result type convention) — incremental
9. **Item 6** (Orchestrator audit) — ✅ completed April 2026
10. **Item 8** (Command bloat) — after orchestrator cleanup
11. **Item 9** (DTO boundary) — last, highest risk
