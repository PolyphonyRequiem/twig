---
goal: "TWIG (Terminal Work Integration Gadget) — Unified Implementation Plan"
version: 1.0
date_created: 2026-03-13
last_updated: 2026-03-15 (EPIC-014 complete)
owner: Daniel Green
tags: [feature, cli, ado, developer-tools, work-management, dotnet, aot, architecture]
sources:
  - twig.req.md (requirements baseline)
  - twig.prd.md (architecture + 9-EPIC roadmap — lower detail)
  - twig-epics-1-3.design.md (v3.0 detailed design — authoritative for EPICs 1–3)
---

# TWIG — Unified Implementation Plan

> **What this document is**: A single, authoritative implementation plan that synthesizes all three earlier planning artifacts. EPICs 1–3 are carried forward from the epics-1-3 design doc (v3.0) with minimal edits. EPICs 4–9 are expanded to the same depth. Where earlier docs conflict, this plan resolves them explicitly.
>
> **What this document is not**: A PRD. Requirements live in [twig.req.md](twig.req.md). Architecture narratives live in [twig.prd.md](twig.prd.md). This is the execution plan.

---

## Resolved Conflicts Between Source Documents

Before diving into the plan, these are the discrepancies found between the three source docs and how they are resolved here.

| # | Conflict | Resolution (authoritative source) |
|---|----------|-----------------------------------|
| 1 | **StateShorthand mapping** — req.md defines simple global `p/c/s/d/x`. Epics-1-3 doc adds per-template, per-type resolution with full error matrix. | **Epics-1-3 is authoritative.** `Resolve(char, ProcessTemplate, WorkItemType) → Result<string>`. Full mapping table adopted. |
| 2 | **contoso.Data.Sqlite version** — prd.md says v10.x. Epics-1-3 says v9.0.x (RD-012: v10.x targets .NET 10). | **Epics-1-3 is authoritative.** Use v9.0.x. |
| 3 | **IProcessConfigurationProvider** — prd.md has async multi-method (fetch from ADO). Epics-1-3 has sync single-method (hard-coded domain rules, RD-003). | **Split into two interfaces.** `IProcessConfigurationProvider` (sync, domain, hard-coded) + `IIterationService` (async, infra, ADO-fetched). |
| 4 | **IUnitOfWork** — prd.md returns void. Epics-1-3 returns `ITransaction` token. | **Epics-1-3 is authoritative.** `BeginAsync() → ITransaction`. |
| 5 | **CreateSeed** — prd.md puts it in SeedFactory. Epics-1-3 makes it a static factory on WorkItem (RD-013). | **Both exist.** `WorkItem.CreateSeed()` is the aggregate-level factory. `SeedFactory` is a domain service that validates parent/child rules then delegates to `WorkItem.CreateSeed()`. |
| 6 | **Test assertion library** — prd.md unresolved. Epics-1-3 selects Shouldly (RD-010). | **Epics-1-3 is authoritative.** Shouldly (Apache 2.0). |
| 7 | **ITEM numbering collision** — prd.md and epics-1-3 both use ITEM-031+. | **Renumbered.** Epics-1-3 numbering preserved (ITEM-001 through ITEM-046). EPIC-004+ starts at ITEM-047. |
| 8 | **IWorkItemCommand.ToFieldChange** — prd.md undefined. Epics-1-3 returns `FieldChange?` (nullable for AddNoteCommand). | **Epics-1-3 is authoritative.** |
| 9 | **IAdoWorkItemService method set** — epics-1-3 defines only `FetchAsync`, `PatchAsync`, `CreateAsync`. prd.md adds `AddCommentAsync`, `FetchChildrenAsync`, `QueryByWiqlAsync`. | **Merged.** Adopt epics-1-3 core + add the three prd.md methods needed by CLI commands (note push, tree fetch, refresh). |

---

## Architecture Summary

Full architecture is documented in [twig.prd.md § 3](twig.prd.md). Key points for implementation:

```
┌─────────────────────────────────────────────────────────────────┐
│  src/Twig/                (Presentation — CLI + Formatters)     │
│  src/Twig.Domain/         (Pure Domain — zero NuGet deps)       │
│  src/Twig.Infrastructure/ (ADO REST, SQLite, Auth, Config)      │
│  tests/Twig.Domain.Tests/                                       │
│  tests/Twig.Infrastructure.Tests/                               │
│  tests/Twig.Cli.Tests/                                          │
└─────────────────────────────────────────────────────────────────┘
```

**Dependency rule**: Domain has zero NuGet dependencies. Infrastructure references Domain. Presentation references both.

**Key domain interfaces** (defined in EPIC-002, implemented in EPICs 5–6):

| Interface | Sync/Async | Implemented In |
|-----------|------------|----------------|
| `IProcessConfigurationProvider` | Sync | Domain (hard-coded) |
| `IIterationService` | Async | Infrastructure (ADO) |
| `IWorkItemRepository` | Async | Infrastructure (SQLite) |
| `IAdoWorkItemService` | Async | Infrastructure (ADO REST) |
| `IAuthenticationProvider` | Async | Infrastructure (AzCli / PAT) |
| `IUnitOfWork` | Async | Infrastructure (SQLite transactions) |
| `IEditorLauncher` | Async | Infrastructure (OS process) |
| `IContextStore` | Async | Infrastructure (SQLite) |
| `IPendingChangeStore` | Async | Infrastructure (SQLite) |

---

## External Dependencies

| Dependency | Version | Purpose | AOT Status |
|------------|---------|---------|------------|
| .NET 9 SDK | 9.0.x | Build + AOT compiler | ✅ |
| ConsoleAppFramework | 5.7.13 | CLI framework (source gen) | ✅ |
| contoso.Data.Sqlite | 9.0.x | SQLite access (RD-012) | ⚠️ Needs `Batteries.Init()` |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.x | Native SQLite bundling | ✅ |
| System.Text.Json | 9.0.x (inbox) | JSON (source gen) | ✅ |
| xUnit | 2.9.x | Tests | N/A |
| Shouldly | 4.x | Assertions (RD-010) | N/A |

---

## Sequencing Constraints

```
EPIC-001 → EPIC-002 → EPIC-003 → EPIC-004
                                       ↓
                                  EPIC-005 → EPIC-006 → EPIC-007 → EPIC-008 → EPIC-009
```

- EPIC-001 MUST complete first (validates AOT tech stack)
- EPIC-002 before EPIC-003 (WorkItem depends on value objects + ProcessConfiguration)
- EPIC-003 before EPIC-004 (read models depend on WorkItem aggregate)
- EPIC-004 before EPIC-005 (infra implements domain interfaces). **Note**: EPIC-005 technically only depends on EPIC-003 (interfaces defined in EPIC-002, WorkItem in EPIC-003). EPIC-004 read models are not used by the persistence layer. These could run in parallel if needed, but serial ordering is safer for a single developer.
- EPIC-005 before EPIC-006 (ADO client needs persistence layer for caching)
- EPIC-006 before EPIC-007 (CLI commands needs both infra layers)
- EPIC-007 before EPIC-008 (formatters need commands to format)
- EPIC-008 before EPIC-009 (error handling builds on working system)

---

## EPIC-001: Project Scaffold + AOT Validation

> **Carried from**: [twig-epics-1-3.design.md](twig-epics-1-3.design.md) — minimal edits.

**Goal**: Create the solution structure, configure Native AOT, and validate that ConsoleAppFramework v5 and contoso.Data.Sqlite work correctly under AOT compilation. This is a **risk-reduction gate**.

**Prerequisites**: .NET 9 SDK installed.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `Twig.sln` with `dotnet new sln` | `Twig.slnx` | Done |
| ITEM-002 | IMPL | Create `Directory.Build.props`: `LangVersion=13`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `IsAotCompatible=true` | `Directory.Build.props` | Done |
| ITEM-003 | IMPL | Create `Directory.Packages.props` for central package management — pin ConsoleAppFramework 5.7.13, contoso.Data.Sqlite 9.0.x, SQLitePCLRaw.bundle_e_sqlite3 2.1.x, xUnit 2.9.x, Shouldly 4.x | `Directory.Packages.props` | Done |
| ITEM-004 | IMPL | Create `src/Twig/Twig.csproj` — console app targeting `net9.0` with `PublishAot=true`, `PublishTrimmed=true`, `TrimMode=full`, `StripSymbols=true`. Reference ConsoleAppFramework, contoso.Data.Sqlite, SQLitePCLRaw. | `src/Twig/Twig.csproj` | Done |
| ITEM-005 | IMPL | Create `src/Twig/Program.cs` — `SQLitePCL.Batteries.Init()` first, then `ConsoleApp.Run(args, …)` with a `version` command and a `smoke` command (in-memory SQLite write/read round-trip). | `src/Twig/Program.cs` | Done |
| ITEM-006 | IMPL | Create `src/Twig.Domain/Twig.Domain.csproj` — class library `net9.0`, zero NuGet deps. | `src/Twig.Domain/Twig.Domain.csproj` | Done |
| ITEM-007 | IMPL | Create `src/Twig.Infrastructure/Twig.Infrastructure.csproj` — class library `net9.0`, references Twig.Domain + SQLite packages. | `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Done |
| ITEM-008 | IMPL | Create test projects: `Twig.Domain.Tests`, `Twig.Infrastructure.Tests`, `Twig.Cli.Tests` — all `net9.0`, reference xUnit + Shouldly + their source project. | `tests/*/` | Done |
| ITEM-009 | IMPL | Add all projects to `Twig.sln` via `dotnet sln add`. | `Twig.slnx` | Done |
| ITEM-010 | TEST | Write AOT smoke test: compile, verify no AOT warnings, verify binary < 30 MB. | `tests/Twig.Cli.Tests/AotSmokeTests.cs` | Done |
| ITEM-011 | TEST | Validate: `dotnet build` zero warnings, `dotnet test` passes, `dotnet publish -r win-x64` produces AOT binary, binary runs `smoke` command. | (manual) | Done (AOT publish blocked by missing C++ build tools in dev env) |

**Exit Criteria**: AOT binary compiles + runs CAF command + SQLite round-trip + zero warnings + < 30 MB.

---

## EPIC-002: Domain Value Objects + ProcessConfiguration

> **Carried from**: [twig-epics-1-3.design.md](twig-epics-1-3.design.md) — minimal edits.

**Goal**: Implement all domain value objects and the ProcessConfiguration aggregate. Establishes domain vocabulary and process rules.

**Prerequisites**: EPIC-001 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Implement `Result<T>` / `Result` — `IsSuccess`, `Value`, `Error`. Factory: `Ok(value)`, `Fail(error)`. | `src/Twig.Domain/Common/Result.cs` | Done |
| ITEM-013 | IMPL | Implement `ProcessTemplate` enum: Agile, Scrum, CMMI, Basic. | `src/Twig.Domain/Enums/ProcessTemplate.cs` | Done |
| ITEM-014 | IMPL | Implement `TransitionKind` enum: Forward, Backward, Cut. | `src/Twig.Domain/Enums/TransitionKind.cs` | Done |
| ITEM-015 | IMPL | Implement `StateShorthand` (`readonly record struct`). `Resolve(char code, ProcessTemplate template, WorkItemType workItemType) → Result<string>`. Full per-template per-type mapping table. Error for invalid combos. | `src/Twig.Domain/ValueObjects/StateShorthand.cs` | Done |
| ITEM-016 | TEST | `StateShorthandTests.cs` — ~45 cases (5 codes × 9 type-groups) + invalid-code + error-returning combos. | `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | Done |
| ITEM-017 | IMPL | Implement `IterationPath` (`readonly record struct`). `Parse(string) → Result<IterationPath>`. Non-empty, backslash-separated. | `src/Twig.Domain/ValueObjects/IterationPath.cs` | Done |
| ITEM-018 | TEST | `IterationPathTests.cs` — valid, empty, null, edge cases. | `tests/Twig.Domain.Tests/ValueObjects/IterationPathTests.cs` | Done |
| ITEM-019 | IMPL | Implement `AreaPath` (`readonly record struct`). Same pattern. Includes `Segments` property. | `src/Twig.Domain/ValueObjects/AreaPath.cs` | Done |
| ITEM-020 | TEST | `AreaPathTests.cs`. | `tests/Twig.Domain.Tests/ValueObjects/AreaPathTests.cs` | Done |
| ITEM-021 | IMPL | Implement `WorkItemType` (`readonly record struct`). Known constants for all standard ADO types. `Parse(string) → Result<WorkItemType>`. | `src/Twig.Domain/ValueObjects/WorkItemType.cs` | Done |
| ITEM-022 | TEST | `WorkItemTypeTests.cs` — all types, unknown type, case sensitivity. | `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | Done |
| ITEM-023 | IMPL | Implement `FieldChange` (`readonly record struct`): `FieldName`, `OldValue` (string?), `NewValue` (string?). | `src/Twig.Domain/ValueObjects/FieldChange.cs` | Done |
| ITEM-024 | TEST | `FieldChangeTests.cs` — equality, construction, nulls. | `tests/Twig.Domain.Tests/ValueObjects/FieldChangeTests.cs` | Done |
| ITEM-025 | IMPL | Implement `PendingNote` (`readonly record struct`): `Text`, `CreatedAt` (DateTimeOffset), `IsHtml` (bool). | `src/Twig.Domain/ValueObjects/PendingNote.cs` | Done |
| ITEM-026 | TEST | `PendingNoteTests.cs`. | `tests/Twig.Domain.Tests/ValueObjects/PendingNoteTests.cs` | Done |
| ITEM-027 | IMPL | Implement `ProcessConfiguration` aggregate — `ForTemplate(ProcessTemplate)` factory. Contains `Dictionary<WorkItemType, TypeConfig>` (States, AllowedChildTypes, TransitionRules). All 4 templates, accurate ADO process data. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Done |
| ITEM-028 | TEST | `ProcessConfigurationTests.cs` — each template: states per type, child types, transition classifications. Exhaustive. | `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | Done |
| ITEM-029 | IMPL | Implement domain interfaces (contracts only) in `src/Twig.Domain/Interfaces/`. Full method signatures below. | `src/Twig.Domain/Interfaces/*.cs` | Done |

**ITEM-029 Interface Contracts:**

```csharp
public interface IWorkItemRepository
{
    Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetByIterationAsync(IterationPath iterationPath, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetParentChainAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FindByPatternAsync(string pattern, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetDirtyItemsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetSeedsAsync(CancellationToken ct = default);
    Task SaveAsync(WorkItem workItem, CancellationToken ct = default);
    Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default);
}

public interface IAdoWorkItemService
{
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default);
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default);
    Task<int> CreateAsync(WorkItem seed, CancellationToken ct = default);
    Task AddCommentAsync(int id, string text, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default);
}

public interface IProcessConfigurationProvider
{
    ProcessConfiguration GetConfiguration(ProcessTemplate template);
}

public interface IIterationService
{
    Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default);
    Task<ProcessTemplate> DetectProcessTemplateAsync(CancellationToken ct = default);
}

public interface IAuthenticationProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(CancellationToken ct = default);
    Task CommitAsync(ITransaction tx, CancellationToken ct = default);
    Task RollbackAsync(ITransaction tx, CancellationToken ct = default);
}

public interface IEditorLauncher
{
    Task<string?> LaunchAsync(string initialContent, CancellationToken ct = default);
}

public interface IContextStore
{
    Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default);
    Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default);
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);
    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}

public interface IPendingChangeStore
{
    Task AddChangeAsync(int workItemId, string changeType, string? fieldName, string? oldValue, string? newValue, CancellationToken ct = default);
    Task<IReadOnlyList<PendingChangeRecord>> GetChangesAsync(int workItemId, CancellationToken ct = default);
    Task ClearChangesAsync(int workItemId, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetDirtyItemIdsAsync(CancellationToken ct = default);
}
```
| ITEM-030 | TEST | Full domain test suite — verify ≥95% line coverage on Twig.Domain. | (test execution) | Done |

**Exit Criteria**: 6 value objects + ProcessConfiguration tested. ≥95% domain coverage. Domain project has zero NuGet deps.

---

## EPIC-003: WorkItem Aggregate + Command Queue

> **Carried from**: [twig-epics-1-3.design.md](twig-epics-1-3.design.md) — minimal edits.

**Goal**: Implement the WorkItem root aggregate + command queue pattern. State transition logic validated against ProcessConfiguration.

**Prerequisites**: EPIC-002 complete.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-031 | IMPL | `IWorkItemCommand` interface — `void Execute(WorkItem target)` and `FieldChange? ToFieldChange()`. Nullable return for AddNoteCommand. | `src/Twig.Domain/Commands/IWorkItemCommand.cs` | Done |
| ITEM-032 | IMPL | `ChangeStateCommand` — `NewState`, optional `Confirmation`. Execute sets state. ToFieldChange → `FieldChange("System.State", old, new)`. | `src/Twig.Domain/Commands/ChangeStateCommand.cs` | Done |
| ITEM-033 | IMPL | `UpdateFieldCommand` — `FieldName`, `Value`. Execute updates `Fields[FieldName]`. | `src/Twig.Domain/Commands/UpdateFieldCommand.cs` | Done |
| ITEM-034 | IMPL | `AddNoteCommand` — holds `PendingNote`. Execute appends to PendingNotes. `ToFieldChange()` → `null`. | `src/Twig.Domain/Commands/AddNoteCommand.cs` | Done |
| ITEM-035 | IMPL | `WorkItem.CreateSeed` — static factory. Returns WorkItem with `IsSeed=true`, `Id=-1`, `SeedCreatedAt=UtcNow`. | `src/Twig.Domain/Aggregates/WorkItem.cs` | Done |
| ITEM-036 | IMPL | `StateTransitionService` — takes ProcessConfiguration, classifies Forward/Backward/Cut, validates, returns `TransitionResult { Kind, IsAllowed, RequiresConfirmation, RequiresReason }`. | `src/Twig.Domain/Services/StateTransitionService.cs` | Done |
| ITEM-037 | TEST | `StateTransitionServiceTests.cs` — forward/backward/cut/invalid × 4 templates. | `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | Done |
| ITEM-038 | IMPL | `WorkItem` aggregate — all properties (including `PendingNotes: List<PendingNote>` for notes accumulated by `AddNoteCommand.Execute`), command queue (private `Queue<IWorkItemCommand>`), methods: `ChangeState`, `UpdateField`, `AddNote`, `ApplyCommands()` (filters null FieldChange entries), `MarkSynced(revision)`. | `src/Twig.Domain/Aggregates/WorkItem.cs` | Done |
| ITEM-039 | TEST | WorkItem construction + property tests. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-040 | TEST | State transition tests — forward/backward/cut/invalid per template. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-041 | TEST | Field update tests — enqueue, apply, verify FieldChange + IsDirty. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-042 | TEST | Note tests — enqueue, apply, verify PendingNotes list. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-043 | TEST | Seed tests — CreateSeed returns IsSeed, sentinel Id, correct type/title. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-044 | TEST | Multi-command atomic apply — ChangeState + UpdateField + AddNote. FieldChange list = 2 (state + field). Note in PendingNotes. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-045 | TEST | MarkSynced clears dirty + updates revision. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | Done |
| ITEM-046 | TEST | Full domain suite ≥95% coverage, zero warnings. | (test execution) | Done |

**Exit Criteria**: WorkItem supports all commands. Transitions validated for all templates. ≥95% coverage.

---

## EPIC-004: Domain Read Models + Remaining Services

**Goal**: Implement `WorkTree`, `Workspace` read models and the remaining domain services (`SeedFactory`, `PatternMatcher`, `ConflictResolver`). Completes the domain layer.

**Prerequisites**: EPIC-003 complete.

### WorkTree

Immutable composite — navigation methods return IDs, not mutated trees. The CLI layer builds a new tree at the target ID.

```
WorkTree
├── FocusedItem: WorkItem
├── ParentChain: IReadOnlyList<WorkItem>   (ordered root → immediate parent)
├── Children: IReadOnlyList<WorkItem>
├── static Build(focus, parentChain, children) → WorkTree
├── FindByPattern(pattern) → MatchResult   (delegates to PatternMatcher)
├── MoveUp() → int?                        (parent ID or null)
└── MoveDown(idOrPattern) → Result<int>    (child ID or error)
```

### Workspace

Projection/composite for display — no identity, no invariants.

```
Workspace
├── ContextItem: WorkItem?
├── SprintItems: IReadOnlyList<WorkItem>
├── Seeds: IReadOnlyList<WorkItem>          (always included)
├── static Build(context, sprint, seeds) → Workspace
├── GetStaleSeeds(thresholdDays) → IReadOnlyList<WorkItem>
├── GetDirtyItems() → IReadOnlyList<WorkItem>
└── ListAll() → IReadOnlyList<WorkItem>     (deduplicated union)
```

### PatternMatcher

```
static Match(string? pattern, IReadOnlyList<(int Id, string Title)> candidates) → MatchResult
```

`MatchResult` — discriminated: `SingleMatch(int id)`, `MultipleMatches(list)`, `NoMatch`. Logic: if `int.TryParse(pattern)` → exact ID match. Else → `string.Contains(pattern, OrdinalIgnoreCase)`.

### SeedFactory

```
Create(title, parentContext?, processConfig, typeOverride?) → Result<WorkItem>
```

Validates parent/child rules via ProcessConfiguration, infers child type if no override, delegates to `WorkItem.CreateSeed()`, inherits AreaPath + IterationPath from parent.

### ConflictResolver

```
Resolve(local, remote) → MergeResult
```

`MergeResult` — discriminated: `NoConflict` (revisions match), `AutoMergeable(mergedChanges)` (different fields changed), `HasConflicts(conflictingFields)` (same field diverged).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-047 | IMPL | Implement `PatternMatcher` — static `Match` method with numeric ID passthrough and case-insensitive substring match. `MatchResult` as discriminated type. | `src/Twig.Domain/Services/PatternMatcher.cs` | Done |
| ITEM-048 | TEST | `PatternMatcherTests.cs` — single match, multiple matches, no match, case insensitivity, numeric ID passthrough, empty pattern, empty candidates. | `tests/Twig.Domain.Tests/Services/PatternMatcherTests.cs` | Done |
| ITEM-049 | IMPL | Implement `SeedFactory` — validate parent/child via ProcessConfiguration, infer default child type, delegate to `WorkItem.CreateSeed()`, inherit area/iteration from parent. Return `Result.Fail` for invalid combos. | `src/Twig.Domain/Services/SeedFactory.cs` | Done |
| ITEM-050 | TEST | `SeedFactoryTests.cs` — valid parent (Task under Feature), invalid parent (Epic under Task), explicit type override, no parent (requires explicit type), area/iteration inheritance, null parent. All 4 templates. | `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` | Done |
| ITEM-051 | IMPL | Implement `ConflictResolver` — compare revisions, diff fields, produce `MergeResult`. `FieldConflict { FieldName, LocalValue, RemoteValue }`. | `src/Twig.Domain/Services/ConflictResolver.cs` | Done |
| ITEM-052 | TEST | `ConflictResolverTests.cs` — same revision, disjoint field changes (auto-merge), overlapping same-value (no conflict), overlapping different-value (conflict), multiple conflicts. | `tests/Twig.Domain.Tests/Services/ConflictResolverTests.cs` | Done |
| ITEM-053 | IMPL | Implement `WorkTree` — immutable, static `Build`, navigation methods delegate to PatternMatcher for `MoveDown`/`FindByPattern`. `MoveUp` returns `ParentChain.LastOrDefault()?.Id`. | `src/Twig.Domain/ReadModels/WorkTree.cs` | Done |
| ITEM-054 | TEST | `WorkTreeTests.cs` — build with 3-level tree, MoveUp from child → parent ID, MoveUp from root → null, MoveDown exact ID, MoveDown pattern single match, MoveDown pattern multi-match → MultipleMatches, MoveDown no match → NoMatch, FindByPattern, empty children. | `tests/Twig.Domain.Tests/ReadModels/WorkTreeTests.cs` | Done |
| ITEM-055 | IMPL | Implement `Workspace` — static `Build`, `GetStaleSeeds` (compare SeedCreatedAt vs threshold), `GetDirtyItems` (union where IsDirty), `ListAll` (deduplicate by Id). | `src/Twig.Domain/ReadModels/Workspace.cs` | Done |
| ITEM-056 | TEST | `WorkspaceTests.cs` — ListAll deduplicates, GetStaleSeeds filters correctly, GetDirtyItems filters correctly, seeds always in ListAll regardless of sprint, null context, empty sprint/seeds, mixed stale/fresh. | `tests/Twig.Domain.Tests/ReadModels/WorkspaceTests.cs` | Done |
| ITEM-057 | TEST | Full domain suite ≥95% coverage, zero warnings. | (test execution) | Done |
| ITEM-057a | IMPL | Implement `HardCodedProcessConfigProvider` — implements `IProcessConfigurationProvider`, delegates to `ProcessConfiguration.ForTemplate()`. Trivial wrapper, but needed for DI registration in EPIC-007. | `src/Twig.Domain/Services/HardCodedProcessConfigProvider.cs` | Done |

**Exit Criteria**: Complete domain layer — all aggregates, value objects, read models, services implemented and tested.

---

## EPIC-005: Infrastructure — SQLite Cache + Configuration

**Goal**: Implement the persistence layer: SQLite repository, context store, pending change store, unit of work, schema management, and JSON config loader.

**Prerequisites**: EPIC-004 complete (all domain interfaces defined).

### SQLite Schema

Full DDL from [twig.prd.md § 3.4](twig.prd.md). Key tables:

| Table | Purpose | Hot-path? |
|-------|---------|-----------|
| `metadata` | Schema version tracking | Startup |
| `work_items` | Indexed columns + `fields_json` blob | Every read |
| `pending_changes` | Command queue persistence | Write commands |
| `process_types` | Cached type definitions | Init + refresh |
| `context` | Active work item, iteration | Every command |

### Connection Management

Single `SqliteConnection` opened once per CLI invocation. WAL mode (`PRAGMA journal_mode=WAL`) for read concurrency. No thread-safety needed — CLI is single-threaded per command. `SQLitePCL.Batteries.Init()` in static constructor.

### Schema Versioning

On open: read `metadata.schema_version`. If missing or ≠ compiled constant `SchemaVersion = 1` → drop all tables, recreate DDL, display "Cache rebuilt. Run `twig refresh` to repopulate."

### SqliteWorkItemRepository — Key SQL

| Method | SQL |
|--------|-----|
| `GetByIdAsync(id)` | `SELECT * FROM work_items WHERE id = @id` |
| `GetChildrenAsync(parentId)` | `SELECT * FROM work_items WHERE parent_id = @parentId ORDER BY type, title` |
| `GetByIterationAsync(path)` | `SELECT * FROM work_items WHERE iteration_path = @path` |
| `GetParentChainAsync(id)` | Iterative: load item → read `parent_id` → load parent → repeat until null. Return ordered root → parent. |
| `FindByPatternAsync(pattern)` | `SELECT * FROM work_items WHERE title LIKE '%' \|\| @pattern \|\| '%' COLLATE NOCASE` |
| `GetDirtyItemsAsync()` | `SELECT * FROM work_items WHERE is_dirty = 1` |
| `GetSeedsAsync()` | `SELECT * FROM work_items WHERE is_seed = 1` |
| `SaveAsync(item)` | `INSERT OR REPLACE INTO work_items (...)` — serialize Fields dict to `fields_json` via TwigJsonContext |
| `SaveBatchAsync(items)` | Wrap in single transaction |

All queries use parameterized `@param` — no string interpolation.

### TwigConfiguration

POCO matching `.twig/config` JSON (see [twig.prd.md § 3.5](twig.prd.md)). `LoadAsync(path)` + `SaveAsync(path)`. `SetValue(string dotPath, string value)` for `twig config` — reflection-free switch on known paths (`"seed.staleDays"`, `"display.hints"`, etc.).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-058 | IMPL | `SqliteCacheStore` — create/open DB at `.twig/twig.db`, execute DDL if missing, check schema version + drop/recreate on mismatch, WAL mode, expose `GetConnection()`. Static `Batteries.Init()`. | `src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs` | Done |
| ITEM-059 | TEST | `SqliteCacheStoreTests.cs` (`:memory:`) — schema creation, version written, mismatch triggers rebuild, WAL mode. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteCacheStoreTests.cs` | Done |
| ITEM-060 | IMPL | `SqliteWorkItemRepository` — implements `IWorkItemRepository`. All SQL per table above. Row ↔ WorkItem mapping with `fields_json` serialization. | `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Done |
| ITEM-061 | TEST | `SqliteWorkItemRepositoryTests.cs` (`:memory:`) — insert, get by ID, children, iteration, parent chain (3 levels), pattern match (case insensitive), dirty items, seeds, save batch, not-found → null, update via save. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteWorkItemRepositoryTests.cs` | Done |
| ITEM-062 | IMPL | `SqliteContextStore` — `IContextStore` impl. `INSERT OR REPLACE` + `SELECT` on `context` table. | `src/Twig.Infrastructure/Persistence/SqliteContextStore.cs` | Done |
| ITEM-063 | TEST | `SqliteContextStoreTests.cs` — set/get active ID, set/get arbitrary key, overwrite, missing → null. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteContextStoreTests.cs` | Done |
| ITEM-064 | IMPL | `SqlitePendingChangeStore` — `IPendingChangeStore` impl. `PendingChangeRecord` DTO: `{ Id, WorkItemId, ChangeType, FieldName, OldValue, NewValue, CreatedAt }`. | `src/Twig.Infrastructure/Persistence/SqlitePendingChangeStore.cs` | Done |
| ITEM-065 | TEST | `SqlitePendingChangeStoreTests.cs` — add, retrieve in order, clear by item, get dirty item IDs, empty results. | `tests/Twig.Infrastructure.Tests/Persistence/SqlitePendingChangeStoreTests.cs` | Done |
| ITEM-066 | IMPL | `SqliteUnitOfWork` — `IUnitOfWork` impl. `ITransaction` wraps `SqliteTransaction`. Begin/Commit/Rollback pattern. | `src/Twig.Infrastructure/Persistence/SqliteUnitOfWork.cs` | Done |
| ITEM-067 | TEST | `SqliteUnitOfWorkTests.cs` — commit persists, rollback discards. | `tests/Twig.Infrastructure.Tests/Persistence/SqliteUnitOfWorkTests.cs` | Done |
| ITEM-068 | IMPL | `TwigConfiguration` — POCO, `LoadAsync`, `SaveAsync`, `SetValue` (switch on known paths). | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Done |
| ITEM-069 | IMPL | `TwigJsonContext` — `[JsonSerializable]` for `TwigConfiguration`, `Dictionary<string, string?>`, ADO DTOs (stubs, filled in EPIC-006). CamelCase, `WhenWritingNull`. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Done |
| ITEM-070 | TEST | `TwigConfigurationTests.cs` — load, defaults for missing optionals, save+reload round-trip, SetValue known/unknown paths. | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Done |
| ITEM-071 | TEST | Full infrastructure test suite passes. | (test execution) | Done |

**Exit Criteria**: All SQLite persistence + config classes implemented with in-memory tests. Schema versioning works.

---

## EPIC-006: Infrastructure — ADO REST Client + Authentication

**Goal**: Implement the anti-corruption layer for ADO REST API and auth providers. First epic with real network calls.

**Prerequisites**: EPIC-005 complete.

### ADO REST Endpoints (all `api-version=7.1`)

| Operation | Method | URL | Notes |
|-----------|--------|-----|-------|
| Get work item | GET | `{org}/{project}/_apis/wit/workitems/{id}?$expand=relations` | Relations needed for parent link |
| Create | POST | `{org}/{project}/_apis/wit/workitems/${type}` | JSON Patch body |
| Update | PATCH | `{org}/{project}/_apis/wit/workitems/{id}` | JSON Patch body, `If-Match: {rev}` header |
| Comment | POST | `{org}/{project}/_apis/wit/workitems/{id}/comments` (preview.4) | `{"text": "..."}` |
| WIQL | POST | `{org}/{project}/_apis/wit/wiql` | `{"query": "SELECT ..."}` |
| Current iteration | GET | `{org}/{project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current` | |
| Work item types | GET | `{org}/{project}/_apis/wit/workitemtypes` | For template detection |

### Parent ID Extraction

ADO stores parent as a link relation, not a field. `AdoResponseMapper` MUST find `System.LinkTypes.Hierarchy-Reverse` in the `relations` array and parse the item ID from the URL suffix.

### HTTP Error Handling

| Status | Exception | Action |
|--------|-----------|--------|
| 200/201 | — | Success |
| 400 | `AdoBadRequestException(msg)` | Parse response body for ADO message |
| 401 | `AdoAuthenticationException` | Trigger auth error flow |
| 404 | `AdoNotFoundException(id)` | Item not found |
| 412 | `AdoConflictException(serverRev)` | Optimistic concurrency failure |
| 429 | `AdoRateLimitException(retryAfter)` | Rate limited |
| 5xx | `AdoServerException` | Transient — no auto-retry in V1 |

### Auth Providers

- **AzCli**: `az account get-access-token --resource 499b84ac-... --query accessToken -o tsv`. In-memory cache per invocation. Not found → clear error. Non-zero exit → "Run `az login`". 10s timeout.
- **PAT**: Precedence: `$TWIG_PAT` env → `.twig/config` `auth.pat`. Format for header: `Basic base64(:PAT)`.

### Template Detection

`DetectProcessTemplateAsync` fetches work item types, infers: has "User Story" → Agile, has "Product Backlog Item" → Scrum, has "Requirement" → CMMI, else → Basic.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-072 | IMPL | Set up personal ADO test org. Create project with standard types. Create sample hierarchy: Epic → Feature → 2 User Stories → 3 Tasks. Document in `tests/test-config.md` (.gitignored). | (manual) | Done |
| ITEM-073 | IMPL | ADO REST DTOs — `AdoWorkItemResponse`, `AdoWiqlResponse`, `AdoCommentRequest`, `AdoPatchOperation`, `AdoIterationResponse`, `AdoWorkItemTypeResponse` + nested types. All `[JsonPropertyName]`. Register in `TwigJsonContext`. | `src/Twig.Infrastructure/Ado/Dtos/*.cs`, `TwigJsonContext.cs` | Done |
| ITEM-074 | IMPL | `AdoResponseMapper` — static methods: `MapWorkItem(dto) → WorkItem` (all fields + parent from relations), `MapPatchDocument(changes) → List<AdoPatchOperation>`, `MapSeedToCreatePayload(seed, parentId?) → List<AdoPatchOperation>`. | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Done |
| ITEM-075 | TEST | `AdoResponseMapperTests.cs` (unit, no network) — construct DTO manually, verify mapping. Parent extraction from relations. Patch doc generation. Seed payload with/without parent. Missing/nullable fields. AssignedTo identity object parsing. | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Done |
| ITEM-076 | IMPL | `AdoRestClient` — `IAdoWorkItemService` impl. Constructor: `HttpClient`, `IAuthenticationProvider`, org/project. URL construction per table. Error handling per matrix. `If-Match` header on PATCH. Content-Type `application/json-patch+json`. 30s timeout. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |
| ITEM-077 | IMPL | WIQL constants in `AdoRestClient`. `FetchChildrenAsync` = WIQL query → batch GET returned IDs with `$expand=relations`. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |
| ITEM-078 | IMPL | `AdoIterationService` — `IIterationService` impl. `GetCurrentIterationAsync` (current sprint). `DetectProcessTemplateAsync` (type-name heuristic). | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Done |
| ITEM-079 | IMPL | `AzCliAuthProvider` — `IAuthenticationProvider` impl. Process.Start az, parse stdout, cache in-memory. Error cases: not installed, non-zero exit, 10s timeout. | `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | Done |
| ITEM-080 | IMPL | `PatAuthProvider` — `IAuthenticationProvider` impl. `$TWIG_PAT` → config fallback. Error if neither. | `src/Twig.Infrastructure/Auth/PatAuthProvider.cs` | Done |
| ITEM-081 | TEST | `AzCliAuthProviderTests.cs` — fake process returning known token, caching (no second spawn), error on missing az. | `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | Done |
| ITEM-082 | TEST | `PatAuthProviderTests.cs` — env var precedence, config fallback, missing both throws. | `tests/Twig.Infrastructure.Tests/Auth/PatAuthProviderTests.cs` | Done |
| ITEM-083 | TEST | `AdoRestClientTests.cs` — integration, `[Trait("Category","Integration")]`. Fetch, create, patch, comment, WIQL, iteration. Personal org only. | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientIntegrationTests.cs` | Done |
| ITEM-084 | TEST | Full infra suite passes. | (test execution) | Done |
| ITEM-085 | FIX | Batch chunking: `FetchBatchAsync` splits IDs into groups of ≤200 to respect ADO batch limit. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |
| ITEM-086 | TEST | `AdoErrorHandlerTests.cs` — URL ID extraction, revision parsing, auth header application, status-code dispatch. | `tests/Twig.Infrastructure.Tests/Ado/AdoErrorHandlerTests.cs` | Done |
| ITEM-087 | FIX | Auth header duplication: extracted `AdoErrorHandler.ApplyAuthHeader` shared by `AdoRestClient` and `AdoIterationService`. | `src/Twig.Infrastructure/Ado/AdoErrorHandler.cs` | Done |
| ITEM-088 | FIX | `AdoNotFoundException.WorkItemId` changed to `int?` — null for non-work-item 404s, positive int for work item 404s. | `src/Twig.Infrastructure/Ado/Exceptions/AdoExceptions.cs` | Done |
| ITEM-089 | FIX | Integration test `FetchAsync_ExistingWorkItem` no longer hardcodes ID 1 — creates a work item first. | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientIntegrationTests.cs` | Done |
| ITEM-090 | TEST | `AdoRestClientBatchTests.cs` — unit tests for `FetchBatchAsync` chunking with mock HttpClient. Covers: exactly 200 IDs (1 batch), 201 IDs (2 batches), 400 IDs (2 batches), 401 IDs (3 batches), result concatenation, URL ID verification, empty/single ID cases. | `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientBatchTests.cs` | Done |
| ITEM-091 | FIX | Removed redundant `chunkSize` variable in `FetchBatchAsync` — LINQ `Take(MaxBatchSize)` naturally handles boundary. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |
| ITEM-092 | TEST | `AdoIterationServiceTests.cs` — unit tests for `DetectProcessTemplateAsync` (all 4 template heuristic branches + case insensitivity + precedence) and `GetCurrentIterationAsync` error paths (empty/null value list, invalid iteration path, valid path). | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Done |
| ITEM-093 | FIX | `AzCliAuthProvider` — read stdout and stderr concurrently via `Task.WhenAll` to prevent deadlock on large pipe buffers. Added comment documenting intentional non-thread-safe cache design. | `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | Done |
| ITEM-094 | FIX | `AdoRestClient.FetchBatchAsync` — replaced O(n²) `Skip/Take` chunking with direct index-based range slicing. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |

**Exit Criteria**: ADO client fetches/creates/patches against test org. Auth works for az cli + PAT. Mapper handles all fields including parent-from-relations.

---

## EPIC-007: CLI Commands — Core Operations

**Status: Done**

**Goal**: Wire up all CLI commands via ConsoleAppFramework, connecting presentation → domain → infrastructure. This is where the user-facing tool comes together.

**Prerequisites**: EPIC-006 complete.

### ConsoleAppFramework v5 Pattern

```csharp
// Program.cs
SQLitePCL.Batteries.Init();
var services = new ServiceCollection();
// ... register all infra + domain services ...
var app = ConsoleApp.Create(args, services.BuildServiceProvider());
app.Add<TwigCommands>();
app.Run();
```

Commands are methods on injected classes with `[Command("verb")]` attributes. Constructor injection for dependencies.

### DI Registration

| Registration | Interface → Implementation |
|-------------|---------------------------|
| Singleton | `SqliteCacheStore` (owns connection) |
| Singleton | `IWorkItemRepository` → `SqliteWorkItemRepository` |
| Singleton | `IContextStore` → `SqliteContextStore` |
| Singleton | `IPendingChangeStore` → `SqlitePendingChangeStore` |
| Singleton | `IUnitOfWork` → `SqliteUnitOfWork` |
| Singleton | `IProcessConfigurationProvider` → `HardCodedProcessConfigProvider` |
| Singleton | `IIterationService` → `AdoIterationService` |
| Singleton | `IAdoWorkItemService` → `AdoRestClient` |
| Singleton | `IAuthenticationProvider` → resolve from config (AzCli or PAT) |
| Singleton | `IEditorLauncher` → `EditorLauncher` |
| Singleton | `IOutputFormatter` → resolve from `--output` flag |
| Singleton | `HintEngine` |

Exit codes: 0 = success, 1 = error, 2 = usage error. Errors → stderr.

### Command Flows

**`twig init`**: Check `.twig/` exists (warn if so, `--force` to reinit) → create dir → accept `--org`/`--project` (or prompt) → detect process template → get current iteration → write config → init SQLite → append `.twig/` to `.gitignore` → success + hints.

**`twig set <idOrPattern>`**: Parse (numeric = ID, else = pattern) → check cache → fetch from ADO if not cached → pattern: single-match use, multi-match disambiguate, no-match error → fetch parent chain + children → save to cache → set active context → display + hints.

**`twig status`**: Get active ID → load from cache → count pending changes → format (title, state+shorthand, type, assigned, area, iteration, dirty `•`, notes count).

**`twig state <shorthand>`**: Get active → resolve shorthand → validate transition → prompt if backward/cut → fetch latest from ADO → conflict-resolve → apply → push via PatchAsync → auto-push pending notes → update cache → display + hints.

**`twig tree`**: Get active → build WorkTree from cache → format with box-drawing, `●` active marker, `[s]` state shorthand, `•` dirty, depth from config.

**`twig up`/`twig down <idOrPattern>`**: Delegate to WorkTree.MoveUp/MoveDown → re-run SetCommand logic with result ID.

**`twig seed [--type <type>] "title"`**: Get active as parent → SeedFactory.Create → push to ADO → save to cache → display new ID + hints.

**`twig note ["text"]`**: Text arg → PendingNote. No arg → `IEditorLauncher`. Store in pending changes. Mark dirty. Display hint about push.

**`twig update <field> <value>`**: Pull latest → conflict-resolve → apply change → push → auto-push notes → clear pending → update cache.

**`twig edit [field]`**: Generate YAML temp file (RD-037) → `IEditorLauncher` → parse diff → store pending changes → mark dirty → hint to `save`.

**`twig save`**: Load pending → if empty "Nothing to save" → pull latest → conflict-resolve → push field changes → push notes → clear pending → mark clean.

**`twig refresh`**: Fetch workspace scope from ADO → update cache. Skip seeds (local-only).

**`twig workspace [show]`** / **`twig show`** / **`twig ws`**: Get active (nullable) → get iteration → query sprint items → query seeds → build Workspace → format with stale warnings.

**`twig config <key> [<value>]`**: Read mode: display. Write mode: SetValue + save.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-085 | IMPL | `Program.cs` — DI wiring, `Batteries.Init()`, global `--output` option, exit code handling, stderr for errors. **BUG**: SQLite services (IWorkItemRepository, IContextStore, etc.) are only registered when `.twig/` exists, but ConsoleAppFramework resolves ALL command constructors eagerly at startup. Running `twig init` (which creates `.twig/`) fails because `SetCommand` needs `IWorkItemRepository`. Fix: register all services unconditionally with lazy/factory patterns that throw "Run twig init first" if `.twig/` doesn't exist. | `src/Twig/Program.cs` | Done |
| ITEM-086 | IMPL | `InitCommand` — full init flow per above. MUST append `.twig/` to `.gitignore` (create if missing, skip if line already present) per SEC-001. **BUG**: `InitCommand` takes `IIterationService` from DI, but the DI-registered `AdoIterationService` is constructed with org/project from the empty default config (not the `--org`/`--project` args passed to init). Fix: `InitCommand` should construct its own `AdoIterationService` using the provided org/project args, or accept `IAuthenticationProvider` and build the service internally. Test constructor should remain compatible with existing tests using mock `IIterationService`. | `src/Twig/Commands/InitCommand.cs` | Done |
| ITEM-087 | IMPL | `SetCommand` — ID/pattern resolution, cache-or-fetch, parent chain, children, set context. | `src/Twig/Commands/SetCommand.cs` | Done |
| ITEM-088 | IMPL | `StatusCommand` — active item display with pending counts. | `src/Twig/Commands/StatusCommand.cs` | Done |
| ITEM-089 | IMPL | `StateCommand` — shorthand resolution, transition validation, confirmation prompts, push, auto-push notes. | `src/Twig/Commands/StateCommand.cs` | Done |
| ITEM-090 | IMPL | `TreeCommand` — build WorkTree, box-drawing format, active marker, depth config. | `src/Twig/Commands/TreeCommand.cs` | Done |
| ITEM-091 | IMPL | `NavigationCommands` — `up`/`down`, delegate to set logic. | `src/Twig/Commands/NavigationCommands.cs` | Done |
| ITEM-092 | IMPL | `SeedCommand` — SeedFactory, push, cache, display ID. | `src/Twig/Commands/SeedCommand.cs` | Done |
| ITEM-093 | IMPL | `NoteCommand` — inline or editor, store pending, mark dirty. | `src/Twig/Commands/NoteCommand.cs` | Done |
| ITEM-094 | IMPL | `UpdateCommand` — pull, conflict-resolve, push field + notes, clear pending. | `src/Twig/Commands/UpdateCommand.cs` | Done |
| ITEM-095 | IMPL | `EditCommand` — YAML temp file, editor, parse diff, stage changes. | `src/Twig/Commands/EditCommand.cs` | Done |
| ITEM-096 | IMPL | `SaveCommand` — push pending fields + notes, clear, mark clean. | `src/Twig/Commands/SaveCommand.cs` | Done |
| ITEM-097 | IMPL | `RefreshCommand` — fetch workspace scope from ADO, update cache. | `src/Twig/Commands/RefreshCommand.cs` | Done |
| ITEM-098 | IMPL | `WorkspaceCommand` — show workspace with stale seed warnings. Aliases: `show`, `ws`. | `src/Twig/Commands/WorkspaceCommand.cs` | Done |
| ITEM-099 | IMPL | `ConfigCommand` — read/write config. | `src/Twig/Commands/ConfigCommand.cs` | Done |
| ITEM-100 | TEST | `InitCommandTests.cs` — creates `.twig/`, writes config, appends `.gitignore`, `--force` reinit, already initialized warning. | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Done |
| ITEM-100a | TEST | `SetCommandTests.cs` — numeric ID from cache, numeric ID from ADO, pattern single match, pattern multi-match disambiguation, pattern no match, empty cache + offline. | `tests/Twig.Cli.Tests/Commands/SetCommandTests.cs` | Done |
| ITEM-100b | TEST | `StateCommandTests.cs` — forward auto-apply, backward prompt, cut prompt + reason, stale revision re-fetch, auto-push notes on state change. | `tests/Twig.Cli.Tests/Commands/StateCommandTests.cs` | Done |
| ITEM-100c | TEST | `SeedCommandTests.cs` — valid seed, no active context error, invalid parent/child type, type override. | `tests/Twig.Cli.Tests/Commands/SeedCommandTests.cs` | Done |
| ITEM-100d | TEST | `NoteCommandTests.cs` — inline text, editor launch, editor abort returns null, pending note stored. | `tests/Twig.Cli.Tests/Commands/NoteCommandTests.cs` | Done |
| ITEM-100e | TEST | `UpdateCommandTests.cs` — pull-apply-push, conflict detection, auto-push notes. | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Done |
| ITEM-100f | TEST | `EditSaveCommandTests.cs` — edit opens editor, parse diff stages changes, save pushes, save with no pending = no-op. | `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Done |
| ITEM-100g | TEST | `WorkspaceCommandTests.cs` — shows context + sprint + seeds, stale seed warnings, aliases (`show`, `ws`). | `tests/Twig.Cli.Tests/Commands/WorkspaceCommandTests.cs` | Done |
| ITEM-100h | TEST | `TreeNavCommandTests.cs` — tree display, `up` at root error, `down` with pattern. | `tests/Twig.Cli.Tests/Commands/TreeNavCommandTests.cs` | Done |
| ITEM-101 | TEST | Full CLI suite passes. Validate with `dotnet run --project src/Twig/Twig.csproj -- init --org contoso-dev --project Twig` — must not throw DI resolution errors. | (test execution) | Done |

**Exit Criteria**: All 14 CLI commands working. DI complete. Commands produce output via formatters.

---

## EPIC-008: Hint Engine + Output Formatting

**Status: COMPLETE**

**Goal**: Implement contextual hints and three output formatters (human, JSON, minimal). Polish the UX.

**Prerequisites**: EPIC-007 complete.

### IOutputFormatter

```csharp
public interface IOutputFormatter
{
    string FormatWorkItem(WorkItem item, bool showDirty);
    string FormatTree(WorkTree tree, int maxDepth, int? activeId);
    string FormatWorkspace(Workspace ws, int staleDays);
    string FormatFieldChange(FieldChange change);
    string FormatError(string message);
    string FormatSuccess(string message);
    string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches);
}
```

### Formatter Specs

| Formatter | WorkItem | Tree | Disambiguation |
|-----------|----------|------|----------------|
| **Human** | Multi-line, ANSI color, bold title, state colored (green/yellow/red), dirty `•` | Box-drawing (`├─` `└─`), `●` active, `[s]` shorthand, `•` dirty, collapse counts | Numbered: `[1] #12345 My Item (Task) [s]` |
| **JSON** | `{"id":123,"title":"...","state":"...","isDirty":false}` — stable schema | `{"focus":{...},"parentChain":[...],"children":[...]}` | `{"matches":[{"id":123,"title":"..."}]}` |
| **Minimal** | `#12345 Active "My Task" Task @jdoe` | One line per item, indent: `  #12345 [s] My Task` | `#12345 "My Item"` per line |

### Hint Rules

| After | Condition | Hint |
|-------|-----------|------|
| `set` | Always | `Try: twig status, twig tree, twig state <shorthand>` |
| `state d` | All siblings done | `All sibling tasks complete. Consider: twig up then twig state d` |
| `state d` | Has pending notes | `You have {n} pending notes. Run twig save to push them.` |
| `state x` | Always | `Item cut. Consider: twig up to return to parent` |
| `seed` | Always | `Created #{id}. Try: twig set {id} to switch context` |
| `note` | Always | `Note staged. Will push on next twig update or twig save` |
| `edit` | Always | `Changes staged locally. Run twig save to persist to ADO` |
| `status` | Has stale seeds | `⚠ {n} stale seeds. Consider completing or cutting them.` |
| `workspace` | Has dirty items | `{n} dirty items. Run twig save to push changes.` |

Hints suppressed when: `config.display.hints == false`, or `--output json`, or `--output minimal`.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-102 | IMPL | `IOutputFormatter` interface. | `src/Twig/Formatters/IOutputFormatter.cs` | Done |
| ITEM-103 | IMPL | `HumanOutputFormatter` — ANSI colors, box-drawing trees, `●`/`•` markers, section headers for workspace, numbered disambiguation. | `src/Twig/Formatters/HumanOutputFormatter.cs` | Done |
| ITEM-104 | IMPL | `JsonOutputFormatter` — serialize via `TwigJsonContext`, stable schema. | `src/Twig/Formatters/JsonOutputFormatter.cs` | Done |
| ITEM-105 | IMPL | `MinimalOutputFormatter` — single-line, no ANSI, pipe-friendly. Section prefixes: `CTX`, `SPR`, `SEED`. | `src/Twig/Formatters/MinimalOutputFormatter.cs` | Done |
| ITEM-106 | IMPL | `HintEngine` — constructor takes config (for hints flag). `GetHints(commandName, item?, workspace?) → IReadOnlyList<string>`. Returns empty if disabled/JSON/minimal. Implements rules per table above. | `src/Twig/Hints/HintEngine.cs` | Done |
| ITEM-107 | TEST | `HumanOutputFormatterTests.cs` — tree box-drawing, dirty indicator, active marker, workspace sections, ANSI present. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Done |
| ITEM-108 | TEST | `JsonOutputFormatterTests.cs` — valid JSON, all required fields, nulls handled, parse and assert structure. | `tests/Twig.Cli.Tests/Formatters/JsonOutputFormatterTests.cs` | Done |
| ITEM-109 | TEST | `MinimalOutputFormatterTests.cs` — single-line, section prefixes, no ANSI. | `tests/Twig.Cli.Tests/Formatters/MinimalOutputFormatterTests.cs` | Done |
| ITEM-110 | TEST | `HintEngineTests.cs` — correct hints per command, suppressed when disabled, suppressed for JSON/minimal, stale seed trigger. | `tests/Twig.Cli.Tests/Hints/HintEngineTests.cs` | Done |
| ITEM-111 | TEST | Full CLI suite with all formatters passes. | (test execution) | Done |
| ITEM-112 | REFACTOR | Clean up `AzCliAuthProvider` Windows process-start workaround (lines 61-67). Currently shells through `cmd.exe /c az` on Windows to work around `az.cmd` resolution. Refactor to resolve the az executable path once (e.g. search PATH for `az.cmd` on Windows) and remove the duplicated argument strings. | `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | Done |

**Exit Criteria**: Three formatters produce correct output. Hints work and are suppressible.

---

## EPIC-009: Error Handling, Offline Mode + Edge Cases

**Status: Complete**

**Goal**: Implement all failure mode handling (FM-001 through FM-009), offline degradation, editor fallback, conflict UX, corruption recovery, version display. Hardens the tool for real use.

**Prerequisites**: EPIC-008 complete.

### Offline Mode (FM-001)

Detect: catch `HttpRequestException` / `TaskCanceledException` in `AdoRestClient` → wrap as `AdoOfflineException`. CLI commands catch this → banner: `"⚠ ADO unreachable. Operating in offline mode."`. Reads work from cache. Writes queue in `pending_changes`. `twig status` shows `"offline"` indicator.

### Auth Errors (FM-002, FM-003)

Catch `AdoAuthenticationException`. AzCli: `"Run 'az login' to refresh."`. PAT: `"Update PAT in .twig/config or $TWIG_PAT."`.

### ADO Errors (FM-004, FM-005, FM-009)

- 404: `"Work item #{id} not found."`.
- 400 (state transition): parse body → `"Transition not allowed. Valid from '{state}': {list}"`.
- Process config stale (FM-009): hint `"Run twig refresh to update cache"`.

### Conflict UX (FM-006, FM-007)

`ConflictResolver` returns `HasConflicts` → display each: `"Conflict on '{field}': local='{v}', remote='{v}'"`. Prompt: `"Keep [l]ocal, [r]emote, or [a]bort?"`. In `--output json`: return conflicts as JSON, exit 1. Stale cache on state change (FM-007): re-fetch → re-validate → proceed if still valid.

### Corruption Recovery (FM-008)

`SqliteCacheStore`: wrap open in try-catch. `SqliteException` → `"⚠ Cache corrupted. Run twig init --force."`. `--force`: delete `twig.db`, recreate, preserve `.twig/config`. Warn if pending changes exist.

### Editor Fallback

Resolution: `$VISUAL` → `$EDITOR` → `notepad.exe` (Windows) / `nano` (Unix). Write to `.twig/EDIT_MSG`. Wait with 5-minute timeout. Exit 0 → return content. Non-zero → return null. No editor → throw `EditorNotFoundException`.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-112 | IMPL | Offline detection + graceful degradation — catch in AdoRestClient, banner in commands, queue writes locally, offline indicator in status. | `AdoRestClient.cs`, `Program.cs` | Done |
| ITEM-113 | IMPL | Auth error handling — catch `AdoAuthenticationException`, display remediation per provider type. | `Program.cs` | Done |
| ITEM-114 | IMPL | ADO error handling — 404 message, 400 parse + allowed transitions, FM-009 refresh hint. | `AdoRestClient.cs`, `Program.cs` | Done |
| ITEM-115 | IMPL | Conflict resolution UX — display conflicts, l/r/a prompt per field, JSON output for non-interactive. | `StateCommand.cs`, `UpdateCommand.cs`, `SaveCommand.cs` | Done |
| ITEM-116 | IMPL | Corruption recovery — catch on SqliteCacheStore open, `--force` reinit, preserve config, warn about pending changes. | `SqliteCacheStore.cs`, `InitCommand.cs` | Done |
| ITEM-117 | IMPL | `EditorLauncher` — `IEditorLauncher` impl. Resolution chain, temp file at `.twig/EDIT_MSG`, 5-min timeout, exit code handling, cleanup. | `src/Twig/Commands/EditorLauncher.cs` | Done |
| ITEM-118 | IMPL | Version display — `<Version>0.1.0</Version>` in Directory.Build.props, `twig --version` via `AssemblyInformationalVersionAttribute`. | `Directory.Build.props`, `Program.cs` | Done |
| ITEM-119 | TEST | `EditorLauncherTests.cs` — resolution chain, exit code 0 returns content, non-zero returns null, no editor throws, temp file cleanup. | `tests/Twig.Cli.Tests/Commands/EditorLauncherEnhancedTests.cs` | Done |
| ITEM-120 | TEST | Offline mode tests — mock HttpClient throws, reads succeed from cache, writes queue locally, status shows offline. | `tests/Twig.Cli.Tests/Commands/OfflineModeTests.cs` | Done |
| ITEM-121 | TEST | Auth error tests — correct messages for az cli / PAT / no auth. | `tests/Twig.Cli.Tests/Commands/AuthErrorTests.cs` | Done |
| ITEM-122 | TEST | Conflict UX tests — display format, l/r/a flow, JSON output for conflicts. | `tests/Twig.Cli.Tests/Commands/ConflictUxTests.cs` | Done |
| ITEM-123 | TEST | Corruption recovery tests — garbage DB file, detection, config preserved, pending change warning. | `tests/Twig.Infrastructure.Tests/Persistence/CorruptionRecoveryTests.cs` | Done |
| ITEM-124 | TEST | Version display test — `twig --version` outputs version string. | `tests/Twig.Cli.Tests/Commands/VersionDisplayTests.cs` | Done |
| ITEM-125 | TEST | Full end-to-end suite across all projects, zero warnings. | (test execution) | Done |
| ITEM-126 | TEST | Performance benchmarks — measure p95 latency for `twig status`, `twig tree`, `twig workspace` from local cache (target: < 200ms per NFR-001). Measure cold start time of AOT binary (target: < 100ms per NFR-004). Record results, fail CI if thresholds exceeded. | `tests/Twig.Cli.Tests/Benchmarks/` | Deferred |
| ITEM-127 | IMPL | Remove temporary `smoke` and `version` commands from EPIC-001's `Program.cs` (superseded by real commands + `--version` flag from ITEM-118). | `src/Twig/Program.cs` | Done |

**Exit Criteria**: All FM-001 through FM-009 handled. Offline degrades gracefully. Editor fallback works. Conflicts prompt user. Version displays. Performance meets NFR targets.

---

## EPIC-010: Team Configuration

**Status: Done**

**Goal**: Add `Team` property to `TwigConfiguration` so the iteration API uses the correct team name. Today the code defaults `team = project`, which returns 404 when the ADO default team follows the `{ProjectName} Team` convention. This EPIC adds team configuration, a `--team` flag on `twig init`, and passes it through all DI‑wired services that hit team‑scoped ADO endpoints.

**Prerequisites**: EPIC-008 complete.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-128 | IMPL | Add `Team` property (default empty) to `TwigConfiguration`. Add `"team"` case to `SetValue` switch. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Done |
| ITEM-129 | IMPL | Register `TwigConfiguration.Team` in `TwigJsonContext` source-gen if needed (verify `Team` serializes). | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Done |
| ITEM-130 | IMPL | Pass `cfg.Team` to `AdoIterationService` constructor in `Program.cs` DI registration. When `Team` is empty, default to `"{Project} Team"` (ADO convention). | `src/Twig/Program.cs` | Done |
| ITEM-131 | IMPL | Add `--team` optional parameter to `twig init` (`InitCommand.ExecuteAsync`). Store in config. Pass to `AdoIterationService` constructor. | `src/Twig/Commands/InitCommand.cs`, `src/Twig/Program.cs` | Done |
| ITEM-132 | IMPL | Support `twig config team <value>` to override team name post-init. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` (already covered by ITEM-128 `SetValue`) | Done |
| ITEM-133 | TEST | Unit tests: `TwigConfiguration` serializes/deserializes `Team`; `SetValue("team", ...)` works; default team derivation in DI. | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Done |
| ITEM-134 | TEST | Integration test: `twig init --org X --project Y --team "Z Team"` persists team in config. | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Done |

**Exit Criteria**: `twig ws` works against projects where default team name ≠ project name. `--team` flag on init. `twig config team X` override. All existing tests pass.

---

## EPIC-011: Multi-Context Database & SQLite Resilience

**Status: Done**

**Goal**: Support multiple ADO org/project contexts in a single workspace without clobbering cached data. Today `twig init --force --org B --project Y` destroys `twig.db` which held all cached data for org A. Additionally, add `Busy Timeout` to the connection string so concurrent CLI invocations (or external DB viewers) don't throw "file is being used by another process".

**Prerequisites**: EPIC-010 complete.

### Design

**Directory layout** — derive DB path from `{org}/{project}/twig.db`:

```
.twig/
  config                              ← active context (org/project/team)
  contoso-dev/
    Twig/
      twig.db                         ← cache for contoso-dev/Twig
  contoso/
    MyProject/
      twig.db                         ← cache for contoso/MyProject
    BackendService/
      twig.db                         ← cache for contoso/BackendService
```

**Context switching** — `twig init --org X --project Y` updates `.twig/config` and creates/opens `.twig/X/Y/twig.db`. Previous DBs remain untouched. `--force` rebuilds only the *current* context's DB.

**Busy timeout** — Add `Busy Timeout=5000` to the SQLite connection string so concurrent processes wait up to 5 seconds instead of failing immediately.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-135 | IMPL | Update `TwigPaths` to derive `DbPath` from `config.Organization` and `config.Project`: `.twig/{org}/{project}/twig.db`. Sanitize org/project names for filesystem safety (strip/replace `/ \ : * ? " < > \|` chars). Create subdirectories on init. | `src/Twig/Program.cs`, `src/Twig.Infrastructure/Config/TwigPaths.cs` | Done |
| ITEM-136 | IMPL | Add `Busy Timeout=5000` to the SQLite connection string in `Program.cs` DI registration. | `src/Twig/Program.cs` | Done |
| ITEM-137 | IMPL | Update `InitCommand` to create the nested directory structure `.twig/{org}/{project}/` before writing the DB. When `--force`, delete only the context-specific DB, not the entire `.twig/` tree. | `src/Twig/Commands/InitCommand.cs` | Done |
| ITEM-138 | IMPL | Migrate legacy flat `twig.db`: on startup, if `.twig/twig.db` exists and no nested DB exists for the current config context, move it to `.twig/{org}/{project}/twig.db`. | `src/Twig/Program.cs` or new `MigrationHelper` | Done |
| ITEM-139 | TEST | Unit tests for path sanitization, nested directory creation, legacy migration, and busy-timeout connection string. | `tests/Twig.Cli.Tests/`, `tests/Twig.Infrastructure.Tests/` | Done |
| ITEM-140 | TEST | Integration test: init with org A, add data, re-init with org B, verify org A's DB is untouched. Switch back to org A, verify data intact. | `tests/Twig.Cli.Tests/Commands/MultiContextInitTests.cs` | Done |

**Exit Criteria**: `twig init` with different org/project combos preserves each context's cached data. `Busy Timeout` prevents "file in use" errors. Legacy `twig.db` auto-migrated. All existing tests pass.

**Code Review Fixes (approved 2026-03-15)**:
- `LegacyDbMigrator.MigrateIfNeeded`: wrapped migration in `try/catch(Exception)` — writes warning to stderr instead of crashing on startup (HIGH)
- `TwigPaths.SanitizePathSegment`: changed parameter from `string` to `string?` to match null-acceptance behavior (LOW)
- `SqliteCacheStore.EnableWalMode`: split batched PRAGMAs into two separate `ExecuteNonQuery` calls for robustness (LOW)
- `MultiContextInitTests`: renamed `SwitchBackToOrgA_DataIntact` → `Force_ReInitOrgA_DoesNotDeleteOrgBDatabase`; added `SwitchBackToOrgA_WithoutForce_PreservesOrgAData` (MEDIUM)
- `LegacyDbMigratorTests`: added `MigrateIfNeeded_DoesNotCrash_WhenMigrationFails` test (HIGH coverage)
- `TwigPathsTests`: removed null-forgiveness operator (`!`) from null test since parameter is now nullable

---

## EPIC-012: Refresh Performance — Area-Path Scoping & Batch Fetch

**Status: Complete**

**Prerequisites**: EPIC-011 complete.

### Design

**Area-path scoping** — During `twig init`, fetch the team's area paths from ADO REST API (`GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues?api-version=7.1`). Store the default area path in `.twig/config` via `defaults.areapath`. During `twig refresh`, add an `AND [System.AreaPath] UNDER '{areaPath}'` clause to the WIQL query to restrict results to the team's scope.

**Batch fetching** — `AdoRestClient` already has a private `FetchBatchAsync` that chunks into groups of 200 IDs and uses `_apis/wit/workitems?ids={csv}&$expand=relations`. Make this public to satisfy the `IAdoWorkItemService.FetchBatchAsync` contract. Replace the `foreach` loop in `RefreshCommand` with a single `FetchBatchAsync` call + `SaveBatchAsync`.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-141 | IMPL | Make `FetchBatchAsync` public on `AdoRestClient` to implement the `IAdoWorkItemService.FetchBatchAsync` interface method. | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Done |
| ITEM-142 | IMPL | Create `AdoTeamFieldValuesResponse` and `AdoTeamFieldValueDto` DTOs for the team field values API response. Register in `TwigJsonContext`. | `src/Twig.Infrastructure/Ado/Dtos/AdoTeamFieldValuesResponse.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Done |
| ITEM-143 | IMPL | Implement `GetTeamAreaPathsAsync` on `AdoIterationService` — call `GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues?api-version=7.1`, parse response, return list of area path strings. | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Done |
| ITEM-144 | IMPL | Update `InitCommand` to fetch team area paths via `GetTeamAreaPathsAsync` and store the default area path in config via `config.SetValue("defaults.areapath", defaultPath)`. Graceful fallback if API fails. | `src/Twig/Commands/InitCommand.cs` | Done |
| ITEM-145 | IMPL | Update `RefreshCommand` WIQL to add `AND [System.AreaPath] UNDER '{areaPath}'` when `config.Defaults?.AreaPath` is set. Replace serial `foreach` + `FetchAsync` loop with `FetchBatchAsync` + `SaveBatchAsync`. | `src/Twig/Commands/RefreshCommand.cs` | Done |
| ITEM-146 | TEST | Unit tests for `GetTeamAreaPathsAsync` — mock HTTP response, verify area path parsing. Test `FetchBatchAsync` public access. | `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs`, `tests/Twig.Infrastructure.Tests/Ado/AdoRestClientBatchTests.cs` | Done |
| ITEM-147 | TEST | Integration tests for `RefreshCommand` — verify WIQL contains area path filter, verify batch fetch is used instead of serial, verify `InitCommand` stores default area path. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Done |

**Exit Criteria**: `twig refresh` on a large project completes in seconds instead of minutes. Only team-scoped items are fetched. Serial fetch replaced with batch API calls. All existing tests pass.

---

## EPIC-013: User-Scoped Workspace & Sprint View

**Status: Done**

**Goal**: `twig ws` currently shows ALL team items from the sprint cache — up to 35 items across the whole team. Users expect `twig ws` to show **their** work items by default (assigned to them). A separate `twig sprint` command (or `twig ws --all`) should show the full team sprint view. This EPIC also adds user identity detection so the tool knows who "me" is.

**Prerequisites**: EPIC-012 complete.

### Design

**User identity** — Detect the current user's identity from the Azure CLI (`az account show`) or the ADO connection info endpoint (`GET {org}/_apis/connectionData`). Store the user's display name and/or email in `.twig/config` under a new `user` section. Provide `twig config user.name` override.

**Workspace default = my items** — `twig ws` filters cached items by `AssignedTo` matching the configured user identity. The `assigned_to` column is already indexed in SQLite. Add `GetByIterationAndAssigneeAsync` to `IWorkItemRepository`.

**Sprint view = team items** — `twig ws --all` (or a new `twig sprint` command) shows all team items in the current iteration, grouped by assignee. This is the current behavior — all cached items for the sprint.

**WIQL `@Me` alternative** — For the refresh query, `@Me` is a WIQL macro that ADO resolves server-side. Could use `AND [System.AssignedTo] = @Me` for a personal refresh, but the full team refresh is better for sprint views and the cache. Filter at display time instead.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-148 | IMPL | Add `UserConfig` section to `TwigConfiguration` with `DisplayName` and `Email` properties. Add `user.name` and `user.email` to `SetValue`. Register in `TwigJsonContext`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Done |
| ITEM-149 | IMPL | Detect user identity during `twig init` — call `GET {org}/_apis/connectionData?api-version=7.1` to get `authenticatedUser.providerDisplayName`. Store in `config.User.DisplayName`. Graceful fallback. | `src/Twig/Commands/InitCommand.cs`, `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Done |
| ITEM-150 | IMPL | Add `GetByIterationAndAssigneeAsync(IterationPath, string assignee)` to `IWorkItemRepository` and `SqliteWorkItemRepository` — filters `WHERE iteration_path = @iter AND assigned_to = @assignee`. | `src/Twig.Domain/Interfaces/IWorkItemRepository.cs`, `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Done |
| ITEM-151 | IMPL | Update `WorkspaceCommand` to accept `--all` flag. Default: filter by `config.User.DisplayName` using `GetByIterationAndAssigneeAsync`. With `--all`: use `GetByIterationAsync` (current behavior). Show assignee column in `--all` mode. | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Program.cs` | Done |
| ITEM-152 | IMPL | Add `twig sprint` command as an alias for `twig ws --all`. Group output by assignee with subtotals. | `src/Twig/Commands/WorkspaceCommand.cs`, `src/Twig/Program.cs` | Done |
| ITEM-153 | TEST | Unit tests for user identity detection, assignee-scoped repository query, workspace filtering, and sprint view grouping. | `tests/Twig.Cli.Tests/Commands/`, `tests/Twig.Infrastructure.Tests/Persistence/`, `tests/Twig.Cli.Tests/Formatters/` | Done |

**Exit Criteria**: `twig ws` shows only the current user's items by default. `twig ws --all` and `twig sprint` show all team items. User identity auto-detected during init. `twig config user.name` allows override. All existing tests pass.

---

## EPIC-014: Tree View Rendering Fixes

**Status: Done**

**Completed**: 2026-03-15. All 7 review feedback items addressed: (1) fixed misleading comment in `Program.cs` — now accurately states Unicode bytes will still be emitted and rendering depends on terminal capabilities; (2) ancestor hydration loop added to `RefreshCommand` after the main fetch — calls `GetOrphanParentIdsAsync()`, batch-fetches from ADO via `FetchBatchAsync()`, saves via `SaveBatchAsync()`, repeats up to 5 levels until no orphans remain, loop placed outside the if/else block so it executes for both sprint and non-sprint refreshes; (3) `TypeColors` bridge added to `RefreshCommand` after `TypeAppearances` update, populating `config.Display.TypeColors` from `config.TypeAppearances` using case-insensitive dictionary (matching `InitCommand` logic); (4) `TreeCommandTests` rewritten to capture stdout and assert actual output content (`'... +3 more'`, all 20 children, no truncation markers); (5) `HumanOutputFormatterTests` badge assertion fixed to assert actual badge characters (◆, ✦, □) instead of type name strings; (6) `SqliteWorkItemRepositoryTests` extended with `ExistsByIdAsync` and `GetOrphanParentIdsAsync` coverage; (7) `RefreshCommandTests` updated to assert `config.Display.TypeColors` populated and ancestor hydration capped at 5 levels.

**Goal**: The `twig tree` command has 5 rendering issuesthat make it unusable in practice: (1) Unicode type badges (◆, ▪, ●, ✦, □) render as `?` because `Console.OutputEncoding` is never set to UTF-8, (2) the parent chain stops at the first uncached ancestor because `refresh` never hydrates parent items outside the WIQL result set, (3) the tree indentation is visually misaligned — parents at 2-space indent, focused item at 0 indent, children at 2-space indent, (4) work items aren't color-coded by type because `TypeAppearances` (with ADO hex colors) are stored in `config.TypeAppearances` but `HumanOutputFormatter` reads from `config.Display.TypeColors` which is never populated, and (5) children are silently truncated to `TreeDepth=3` with no way to control it from the CLI.

**Prerequisites**: None (all issues are in existing code).

### Design

**Unicode output encoding** — Set `Console.OutputEncoding = System.Text.Encoding.UTF8` at application startup in `Program.cs`, before any output is written. This enables Windows console to render the Unicode badge characters. `InvariantGlobalization=true` does not affect output encoding — it only affects culture-specific string comparisons and formatting. If UTF-8 encoding is unavailable under `InvariantGlobalization`, fall back to ASCII-safe badges (`*`, `-`, `o`, `!`, `.`).

**Ancestor hydration during refresh** — After the main WIQL fetch, collect all `parent_id` values from fetched items. For any parent ID not already in the cache, fetch it individually (or in batch) from ADO. Repeat recursively until all ancestors up to the root (no `parent_id`) are cached. This ensures `GetParentChainAsync` can walk the full chain. Cap at 5 levels deep to prevent runaway recursion. Only fetch — don't overwrite existing cached items.

**Tree indentation alignment** — Redesign the tree layout so that depth level drives indentation consistently. Each depth level adds 2 spaces. The active marker (`●`) should be placed inline, not taking a separate column. Parent chain items show their depth from root. The focused item shows at its natural depth. Children show at focused+1 depth. Box-drawing connectors (`├──`, `└──`) for children align under the focused item.

**Type color bridge** — During `twig init`, after fetching `TypeAppearances`, also populate `Display.TypeColors` from the appearance data: `config.Display.TypeColors = typeAppearances.Where(a => !string.IsNullOrEmpty(a.Color)).ToDictionary(a => a.Name, a => a.Color)`. This bridges the gap so `HumanOutputFormatter.GetTypeColor()` can use the ADO hex colors. During `twig refresh`, also refresh type appearances if they're stale.

**Tree depth flag** — Add `--depth <n>` and `--all` flags to the `twig tree` command. `--all` sets depth to `int.MaxValue`. Increase the default `TreeDepth` from 3 to 10. If neither flag is provided, use `config.Display.TreeDepth`.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-154 | IMPL | Set `Console.OutputEncoding = Encoding.UTF8` at program startup before any output. Add fallback to ASCII badges if UTF-8 encoding fails. | `src/Twig/Program.cs` | Done |
| ITEM-155 | IMPL | Ancestor hydration in `RefreshCommand` — after main WIQL fetch, collect orphan `parent_id` values not already cached, batch-fetch from ADO, repeat up to 5 levels. Store hydrated ancestors in SQLite. | `src/Twig/Commands/RefreshCommand.cs`, `src/Twig.Domain/Interfaces/IWorkItemRepository.cs`, `src/Twig.Infrastructure/Persistence/SqliteWorkItemRepository.cs` | Done |
| ITEM-156 | IMPL | Fix tree indentation in `HumanOutputFormatter.FormatTree()` — use consistent depth-based indentation. Parents indent by their depth from root, focused item at its natural depth, children at focused+1. Active marker (`●`) appears inline, not as a separate column. | `src/Twig/Formatters/HumanOutputFormatter.cs` | Done |
| ITEM-157 | IMPL | Bridge `TypeAppearances` → `Display.TypeColors` during `twig init`. After fetching type appearances, populate `config.Display.TypeColors` dictionary from appearance hex colors. Ensure `HumanOutputFormatter` receives the colors via DI. | `src/Twig/Commands/InitCommand.cs`, `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Done |
| ITEM-158 | IMPL | Add `--depth` and `--all` flags to `TreeCommand`. Increase default `TreeDepth` from 3 to 10. `--all` overrides to show all children. Pass resolved depth to `FormatTree()`. | `src/Twig/Commands/TreeCommand.cs`, `src/Twig/Program.cs`, `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Done |
| ITEM-159 | IMPL | Update `MinimalOutputFormatter.FormatTree()` and `JsonOutputFormatter.FormatTree()` to match the indentation and depth changes. JSON should include full parent chain and all children (no truncation). | `src/Twig/Formatters/MinimalOutputFormatter.cs`, `src/Twig/Formatters/JsonOutputFormatter.cs` | Done |
| ITEM-160 | TEST | Tests for: UTF-8 badge rendering, ancestor hydration (multi-level parent chase), tree indentation layout, type color bridging, depth flag behavior, and formatter consistency. | `tests/Twig.Cli.Tests/Commands/TreeCommandTests.cs`, `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Done |

**Exit Criteria**: `twig tree` renders Unicode badges correctly on Windows. Parent chain shows full hierarchy from root Epic down. Tree indentation is visually consistent and aligned. Work items are color-coded by their ADO type color. Children default to 10 visible with `--depth` and `--all` flags available. All existing tests pass.

---

## Research Areas

Items that MAY need human decision when the relevant EPIC is reached. Not blocking.

| ID | Area | EPIC | Current Stance |
|----|------|------|----------------|
| RA-001 | `InvariantGlobalization` — saves 5-10 MB, breaks culture formatting. TWIG uses ISO 8601. | 001 | Likely safe. Validate in smoke test. |
| RA-002 | `Result<T>` — custom vs FluentResults. | 002 | Custom. FluentResults may have AOT issues. |
| RA-003 | Custom process template support (OS project has 32 types). | Post-V1 | Deferred. Standard templates cover ~95%. Future config override. |
| RA-004 | Mocking library — NSubstitute vs Moq vs hand-written fakes. | 003+ | Hand-written for domain (no mocks needed). Re-evaluate for infra. |
| RA-005 | CI coverage threshold. | 001 | 95% domain, 80% infra. |
| RA-006 | Command queue undo. | Post-V1 | Not needed V1 — ApplyCommands is one-way. |
| RA-007 | CAF v5 DI wiring. | 001/007 | Constructor injection via ServiceCollection. Validate in smoke. |
| RA-008 | HTTP retry for transient failures (5xx, timeout). | Post-V1 | No auto-retry V1. Callers show "try again". |
| RA-009 | OS custom process template. | Post-V1 | Config JSON override or 5th template. |
