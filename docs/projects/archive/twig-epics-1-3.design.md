---
goal: "TWIG CLI — Project Scaffold, Domain Model, and WorkItem Aggregate (EPICs 1–3)"
version: 3.0
date_created: 2026-03-11
last_updated: 2026-03-11
owner: author
tags: [scaffold, domain, aot, cli, architecture]
revision_notes: "Rev 3: Corrected CMMI Task StateShorthand mapping — CMMI Task has both Resolved and Removed states per official ADO category state docs, unified CMMI into one group for all types; corrected Agile groupings — Bug has no Removed, Feature/Epic have no Resolved — split Agile into 4 sub-groups (User Story, Bug, Feature/Epic, Task); recalculated ITEM-016 test matrix from ~40 to ~45 cases; fixed data flow example UpdateField int literal to string per RD-009; redesigned IWorkItemCommand — ToFieldChange returns FieldChange? (nullable) for AddNoteCommand compatibility, separated CreateSeedCommand from command queue pattern as static factory on WorkItem; corrected STS announcement attribution from September 2025 to December 2025."
---

# TWIG Solution Design — EPICs 1–3

> **Scope**: This document covers EPICs 1–3 of 9. Full design sections are included. The Implementation Plan contains ONLY EPICs 1–3. EPICs 4–9 are not addressed.

---

## Executive Summary

TWIG (Terminal Work Integration Gadget) is a C# .NET 9 Native AOT CLI for managing Azure DevOps work items from the terminal, inspired by Git's workflow model. This document covers the foundational three epics: (1) project scaffolding with Native AOT validation to fail fast on technology risks, (2) domain value objects and the ProcessConfiguration aggregate that encode ADO process rules, and (3) the WorkItem root aggregate with a command-queue pattern for atomic state changes. By the end of EPIC 3, the project will have a validated AOT build pipeline, a fully-tested pure domain layer, and the core aggregate that all subsequent features build upon.

---

## Background

Azure DevOps provides a web UI and REST API for work item management, but terminal-centric developers lack a fast, offline-capable CLI for common operations (state transitions, field updates, parent/child navigation). Existing tools either require the .NET runtime installed, depend on the deprecated VSTS SDK, or lack offline support.

TWIG fills this gap with a single native binary (< 30 MB) that caches work items locally in SQLite and syncs via the ADO REST API v7.1. The architecture follows clean-architecture principles with four layers: Presentation → Application → Domain → Infrastructure.

**Prior art in this repository**: The `docs/projects/` directory contains earlier planning artifacts (`twig-plan-part1.txt` through `twig-plan-part3.txt`) capturing initial design thinking. This document supersedes those with a concrete, implementation-ready design.

**Why now**: .NET 9 Native AOT has matured significantly, and ConsoleAppFramework v5 (Cysharp) provides a zero-reflection, source-generator CLI framework that eliminates reflection-based overhead. While System.CommandLine reached stable release (v2.0.0) in November 2025, ConsoleAppFramework v5's zero-dependency source-generator approach produces smaller binaries and simpler integration for AOT scenarios. The combination makes a performant single-binary CLI viable without runtime dependencies.

---

## Problem Statement

1. **No fast terminal-native ADO work item tool exists** — developers context-switch to the browser for simple operations (state changes, field updates, creating child items).
2. **AOT technology risk is unvalidated** — contoso.Data.Sqlite has a known open AOT compatibility issue ([dotnet/efcore#36068](https://github.com/dotnet/efcore/issues/36068)), and ConsoleAppFramework v5 is relatively new. These must be validated before investing in feature development.
3. **Domain complexity needs early modeling** — ADO process templates (Agile, Scrum, CMMI, Basic) each define different state names, allowed transitions, and work item type hierarchies. This logic must be modeled correctly in the domain before any commands can operate.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|----|------|---------|
| G-1 | Validate Native AOT build with ConsoleAppFramework v5 + contoso.Data.Sqlite | AOT binary compiles, runs, and passes a round-trip SQLite read/write |
| G-2 | Establish solution structure following 4-layer clean architecture | All projects compile, test runner executes, layer dependencies enforced |
| G-3 | Implement all domain value objects with full test coverage | ≥95% line coverage on Domain project |
| G-4 | Implement ProcessConfiguration aggregate with process template rules | All four ADO process templates (Agile, Scrum, CMMI, Basic) modeled and tested |
| G-5 | Implement WorkItem aggregate with command-queue pattern | State transitions, field updates, notes, and seed operations all testable in isolation |

### Non-Goals

| ID | Non-Goal | Rationale |
|----|----------|-----------|
| NG-1 | ADO REST API integration | Deferred to EPIC 4 |
| NG-2 | SQLite repository implementation | Deferred to EPIC 5 |
| NG-3 | CLI command handlers | Deferred to EPIC 6+ |
| NG-4 | Authentication / PAT management | Deferred to EPIC 4 |
| NG-5 | Version updates or changelog | Not applicable for initial scaffold |

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-001 | Solution MUST compile as a Native AOT single binary on Windows x64 | High |
| FR-002 | AOT binary MUST execute a ConsoleAppFramework command with argument parsing | High |
| FR-003 | AOT binary MUST open a SQLite database, write a row, and read it back | High |
| FR-004 | StateShorthand MUST map single-character codes (p/c/s/d/x) to full state names per process template **and work item type** | High |
| FR-005 | IterationPath and AreaPath MUST validate format (non-empty, backslash-separated segments) | High |
| FR-006 | WorkItemType MUST be constrained to known ADO types (Epic, Feature, User Story, Task, Bug, etc.) | High |
| FR-007 | ProcessConfiguration MUST define valid states, child types, and transition rules per work item type for Agile, Scrum, CMMI, and Basic templates | High |
| FR-008 | WorkItem aggregate MUST support ChangeState, UpdateField, AddNote, and CreateSeed commands via a command queue | High |
| FR-009 | State transitions forward (e.g., New→Active) MUST auto-apply; backward and cut transitions MUST require confirmation metadata | High |
| FR-010 | WorkItem MUST track a dirty flag and revision number for optimistic concurrency | Medium |

### Non-Functional Requirements

| ID | Requirement | Metric | Rationale |
|----|-------------|--------|-----------|
| NFR-001 | AOT binary cold start | < 100ms | CLI responsiveness |
| NFR-002 | Read operation latency | < 200ms | Terminal UX |
| NFR-003 | Single binary size | < 30 MB | Distribution simplicity |
| NFR-004 | Domain layer purity | Zero infrastructure dependencies | Testability, portability |
| NFR-005 | Test execution time | < 10s for domain tests | Developer feedback loop |
| NFR-006 | Cross-platform compatibility | Windows primary, Linux/macOS secondary | Developer reach |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    Twig.sln                              │
├─────────────────────────────────────────────────────────┤
│  src/Twig/              (Presentation — CLI + Formatters)│
│    ├── Program.cs       ConsoleAppFramework entry point  │
│    ├── Commands/        CLI command classes               │
│    └── Formatters/      Output formatting                │
├─────────────────────────────────────────────────────────┤
│  src/Twig.Domain/       (Pure Domain — zero dependencies)│
│    ├── Aggregates/      WorkItem, ProcessConfiguration   │
│    ├── ValueObjects/    StateShorthand, IterationPath,   │
│    │                    AreaPath, WorkItemType,           │
│    │                    FieldChange, PendingNote          │
│    ├── Services/        StateTransitionService,           │
│    │                    SeedFactory, PatternMatcher,      │
│    │                    ConflictResolver                  │
│    ├── ReadModels/      WorkTree, Workspace              │
│    └── Interfaces/      Repository & service contracts   │
│                         (incl. IEditorLauncher)          │
├─────────────────────────────────────────────────────────┤
│  src/Twig.Infrastructure/ (ADO REST, SQLite, Auth, Cfg) │
│    ├── Persistence/     SQLite repos, migrations         │
│    ├── Ado/             REST API client                  │
│    ├── Auth/            PAT / credential management      │
│    └── Config/          Settings, JSON serialization     │
├─────────────────────────────────────────────────────────┤
│  tests/Twig.Domain.Tests/                               │
│  tests/Twig.Infrastructure.Tests/                       │
│  tests/Twig.Cli.Tests/                                  │
└─────────────────────────────────────────────────────────┘
```

**Dependency rule**: Each layer depends only on the layer directly below it. Domain has zero external dependencies. Infrastructure references Domain and implements domain interfaces (including `IEditorLauncher` — editor launching is an infrastructure concern despite being consumed by domain services). Presentation references both.

### Key Components

#### 1. Value Objects (EPIC 2)

All value objects are `readonly record struct` types for zero-allocation equality and immutability.

**StateShorthand**
- Maps single-char codes to full state names, requiring **both** process template and work item type for correct resolution
- ADO workflow states differ by work item type within the same process template (e.g., Scrum PBI uses New→Approved→Committed→Done, but Scrum Task uses To Do→In Progress→Done)
- Factory method: `StateShorthand.Resolve(char code, ProcessTemplate template, WorkItemType workItemType) → Result<string>`
- Shorthand code mappings per template and type:

  | Code | Meaning | Basic (all) | Agile (Story) | Agile (Bug) | Agile (Feature/Epic) | Agile (Task) | Scrum (PBI/Bug) | Scrum (Feature/Epic) | Scrum (Task) | CMMI (all) |
  |------|---------|-------------|---------------|-------------|----------------------|-------------|----------------|---------------------|-------------|------------|
  | `p` | Proposed | To Do | New | New | New | New | New | New | To Do | Proposed |
  | `c` | Committed | Doing | Active | Active | Active | Active | Committed | In Progress | In Progress | Active |
  | `s` | Resolved | *(err)* | Resolved | Resolved | *(err)* | *(err)* | *(err)* | *(err)* | *(err)* | Resolved |
  | `d` | Done | Done | Closed | Closed | Closed | Closed | Done | Done | Done | Closed |
  | `x` | Removed | *(err)* | Removed | *(err)* | Removed | Removed | Removed | Removed | Removed | Removed |

  - `*(err)*` = Returns `Result.Fail` — that state concept does not exist for the given template+type combination (e.g., Basic has no "Resolved" equivalent; Agile Bug has no "Removed" state; Agile Feature/Epic and Agile Task have no "Resolved" state; Scrum PBI has Approved between New and Committed but no "Resolved")
  - **Agile sub-groups**: User Story has all 5 states (New, Active, Resolved, Closed, Removed); Bug has 4 states (no Removed); Feature/Epic have 4 states (no Resolved); Task has 4 states (no Resolved) — per [ADO workflow state categories](https://learn.microsoft.com/azure/devops/boards/work-items/workflow-and-state-categories) which lists `Removed (Epic, Feature, User Story)` excluding Bug, and `Resolved: Resolved (Bug)` excluding Feature/Epic
  - **CMMI unified**: All CMMI work item types (Requirement, Bug, Feature, Epic, Task) share the same 5-state workflow (Proposed, Active, Resolved, Closed, Removed) — per ADO category states table which lists `Resolved (Epic, Feature, Requirement, Task)` for CMMI and `Removed` without type qualifiers
  - Scrum PBI/Bug "Approved" state has no single-character shorthand because it is Scrum-specific; users must type the full state name or use the board

**IterationPath / AreaPath**
- Validated string wrappers ensuring non-empty, backslash-separated segments
- `Parse(string raw)` returns `Result<T>` (no exceptions for invalid input)
- Supports relative resolution from a project root

**WorkItemType**
- Constrained to known ADO types: Epic, Feature, UserStory, ProductBacklogItem, Requirement, Task, Bug, Issue, TestCase, plus process-specific types (Impediment, Change Request, etc.)
- Stored as a string internally for extensibility, validated against ProcessConfiguration

**FieldChange**
- Immutable record of `(string FieldName, string? OldValue, string? NewValue)`
- All field values are stored as their string representation (e.g., Priority → "2", Effort → "3.5", State → "Active") for AOT safety — avoids boxing of value types and trim issues with `object?`
- Matches ADO REST API patch document format where field values are JSON-serialized strings
- Used in the WorkItem command queue to track pending mutations

**PendingNote**
- Represents an uncommitted comment/note to attach to a work item
- Contains `Text`, `Timestamp`, `IsHtml` flag

#### 2. ProcessConfiguration Aggregate (EPIC 2)

```
ProcessConfiguration
├── ProcessTemplate (Agile | Scrum | CMMI | Basic)
├── WorkItemTypeConfigs: Dictionary<WorkItemType, TypeConfig>
│   └── TypeConfig
│       ├── States: List<string>           // ordered state sequence
│       ├── AllowedChildTypes: List<WorkItemType>
│       └── TransitionRules: StateTransitionMatrix
│           └── (FromState, ToState) → TransitionKind (Forward | Backward | Cut)
└── Factory: ProcessConfiguration.ForTemplate(ProcessTemplate) → ProcessConfiguration
```

- **Immutable after construction** — built via factory method for each process template
- **StateTransitionMatrix**: encodes which transitions are forward (auto), backward (confirm), or cut (confirm + reason)
- **No infrastructure dependencies** — all process rules are hard-coded domain knowledge derived from ADO documentation

#### 3. WorkItem Aggregate (EPIC 3)

```
WorkItem (Aggregate Root)
├── Identity
│   ├── Id: int                        // ADO work item ID
│   ├── Type: WorkItemType
│   └── Revision: int                  // optimistic concurrency
├── State
│   ├── Title: string
│   ├── State: string                  // current state name
│   ├── AssignedTo: string?
│   ├── IterationPath: IterationPath
│   ├── AreaPath: AreaPath
│   ├── ParentId: int?
│   └── Fields: Dictionary<string, string?>  // extensible fields (string for AOT safety — see RD-009)
├── Tracking
│   ├── IsDirty: bool
│   ├── IsSeed: bool                   // locally-created, not yet synced
│   └── PendingCommands: Queue<IWorkItemCommand>
└── Methods
    ├── ChangeState(newState, confirmation?) → Result
    ├── UpdateField(name, value) → Result
    ├── AddNote(text) → Result
    ├── CreateSeed(type, title, parentId?) → WorkItem  [static factory]
    ├── ApplyCommands() → List<FieldChange>             [flush queue]
    └── MarkSynced(revision) → void
```

**Command Queue Pattern**: Mutations are not applied immediately. Instead, each operation enqueues an `IWorkItemCommand`:

```
IWorkItemCommand
├── Execute(WorkItem target) → void
├── ToFieldChange() → FieldChange?     // nullable: AddNoteCommand returns null (notes are not field changes)
│
├── ChangeStateCommand { NewState, Confirmation? }
├── UpdateFieldCommand { FieldName, Value }
└── AddNoteCommand { Text }
```

`CreateSeed` is **not** part of the command queue — it is a static factory method on `WorkItem` that creates a new aggregate instance (it does not mutate an existing target). See `WorkItem.CreateSeed(type, title, parentId?)` above.

When `ApplyCommands()` is called, commands are validated and applied atomically, producing a `List<FieldChange>` that the infrastructure layer uses to build ADO REST API patch documents. `AddNoteCommand` returns `null` from `ToFieldChange()` and is excluded from the FieldChange list; notes are synced via a separate ADO REST endpoint.

**State Transition Logic**:
- Forward transitions (toward "done" states): auto-apply, no confirmation needed
- Backward transitions (toward "new" states): require confirmation flag
- Cut transitions (to "Removed"): require confirmation + reason string
- Validation uses `ProcessConfiguration.TransitionRules` to classify each transition

### Data Flow

**Command execution flow (domain only, EPICs 1–3)**:

```
1. Caller creates WorkItem (from cached data or seed factory)
2. Caller invokes mutation methods:
   workItem.ChangeState("Active")         → enqueues ChangeStateCommand
   workItem.UpdateField("Priority", "2")   → enqueues UpdateFieldCommand
   workItem.AddNote("Started working")    → enqueues AddNoteCommand
3. Caller applies all commands atomically:
   var changes = workItem.ApplyCommands() → validates, applies, returns FieldChange list
4. workItem.IsDirty == true (until MarkSynced called by infrastructure)
```

**Process configuration resolution**:

```
1. ProcessConfiguration.ForTemplate(ProcessTemplate.Agile)
   → builds immutable config with all Agile states, types, transitions
2. config.GetTransitionKind("User Story", "New", "Active")
   → returns TransitionKind.Forward
3. config.GetTransitionKind("User Story", "Active", "New")
   → returns TransitionKind.Backward
4. config.GetAllowedChildTypes(WorkItemType.Feature)
   → returns [UserStory, Bug]
```

### API Contracts (Domain Interfaces)

These interfaces are defined in EPIC 2–3 but implemented in later EPICs:

```csharp
// Defined in Twig.Domain/Interfaces/

public interface IWorkItemRepository
{
    Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> QueryAsync(WorkItemQuery query, CancellationToken ct = default);
    Task SaveAsync(WorkItem workItem, CancellationToken ct = default);
    Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default);
}

public interface IAdoWorkItemService
{
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, CancellationToken ct = default);
    Task<int> CreateAsync(WorkItem seed, CancellationToken ct = default);
}

public interface IProcessConfigurationProvider
{
    ProcessConfiguration GetConfiguration(ProcessTemplate template);
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
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | Use `readonly record struct` for value objects | Zero-allocation equality, immutable by default, AOT-friendly. Records provide `Equals`/`GetHashCode` via source generation. |
| RD-002 | Command queue pattern for WorkItem mutations | Enables atomic application of changes, produces a clean `FieldChange` list for ADO patch documents, and supports undo/preview scenarios. |
| RD-003 | Hard-coded process template rules (no API fetch) | ADO process templates rarely change. Hard-coding avoids a bootstrap API call, enables offline operation, and keeps the domain pure. Custom process templates can be supported later via configuration override. |
| RD-004 | Result type for validation instead of exceptions | Domain operations return `Result<T>` for expected failures (invalid state transitions, bad input). Exceptions reserved for unexpected/infrastructure failures. AOT-friendly (no reflection-based exception handling). |
| RD-005 | ConsoleAppFramework v5 over System.CommandLine | System.CommandLine 2.0.0 reached stable release in November 2025 and supports AOT. However, ConsoleAppFramework v5 is zero-dependency, uses source generators exclusively (no runtime reflection), and produces smaller binaries (~2–5 MB less). Both are viable; ConsoleAppFramework is chosen for its minimal footprint and source-gen-only design. |
| RD-006 | Direct `contoso.Data.Sqlite` over EF Core | EF Core AOT support is experimental (precompiled queries). Direct Sqlite is simpler, smaller binary, and sufficient for a local cache. |
| RD-007 | `SQLitePCLRaw.bundle_e_sqlite3` with explicit `Batteries.Init()` | Required workaround for AOT — automatic SQLite initialization is trimmed by the AOT compiler. Explicit init ensures the native library is loaded. |
| RD-008 | xUnit over NUnit/MSTest for testing | xUnit is the most widely used .NET test framework, has strong community support, and works well with `dotnet test`. |
| RD-009 | FieldChange uses `string?` not `object?` for OldValue/NewValue | `object?` causes boxing of value types (int Priority, double Effort), defeating zero-allocation goals, and creates AOT trim risks. `string?` is AOT-safe and aligns with ADO REST API JSON patch format where all values are serialized. Non-string fields are stored as their `ToString()` representation (e.g., Priority → "2"). |
| RD-010 | Shouldly over FluentAssertions for test assertions | FluentAssertions v8+ requires a paid Xceed commercial license ($129.95/dev/year). Shouldly is free (Apache 2.0), provides similar fluent assertion syntax, and is well-maintained. AwesomeAssertions (FA community fork) was also considered but Shouldly has longer track record. |
| RD-011 | EditorLauncher as domain interface with infrastructure implementation | EditorLauncher involves OS process spawning — an infrastructure concern. Defining `IEditorLauncher` in Domain/Interfaces maintains domain purity (NFR-004) while allowing infrastructure to provide the OS-specific implementation. |
| RD-012 | contoso.Data.Sqlite 9.0.x (not 10.x) for .NET 9 compatibility | contoso.Data.Sqlite 10.x targets .NET 10. Since TWIG targets .NET 9, version 9.0.x is used for framework alignment. Migration to v10.x will occur alongside the planned .NET 10 LTS migration. |
| RD-013 | CreateSeed as static factory, not IWorkItemCommand | `IWorkItemCommand.Execute(WorkItem target)` mutates an existing aggregate, but CreateSeed is a static factory that produces a **new** `WorkItem` instance — it does not operate on an existing target. Keeping CreateSeed in the command queue would violate the interface contract. Additionally, `IWorkItemCommand.ToFieldChange()` returns `FieldChange?` (nullable) to accommodate `AddNoteCommand` which returns `null` since notes are not field changes (synced via separate ADO REST comment endpoint). |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **System.CommandLine** for CLI | contoso-backed, stable release (2.0.0), rich middleware, AOT-compatible | Heavier dependency chain, runtime reflection for some features, larger binary (~2–5 MB more) | Rejected — ConsoleAppFramework v5 is zero-dependency, source-gen-only, and produces smaller binaries |
| **Spectre.Console.Cli** for CLI | Beautiful output, popular | Reflection-heavy, AOT warnings, larger dependency surface | Rejected — not AOT-safe without significant workarounds |
| **EF Core with SQLite** | Higher-level ORM, migrations | AOT support is experimental, 150+ MB binary, heavy for a cache | Rejected — overkill for a local cache; direct Sqlite is sufficient |
| **Dapper** for SQLite access | Lightweight ORM, popular | Uses reflection for mapping, AOT trim warnings | Rejected — manual mapping is trivial for our few tables |
| **Exceptions for domain validation** | Idiomatic C#, familiar | Performance cost, AOT reflection concerns with catch filters | Rejected — Result type is more explicit and AOT-friendly |
| **Fetch process config from ADO API** | Always current, handles custom templates | Requires network for bootstrap, slower startup, harder to test | Rejected for now — hard-coded covers 99% of cases; API override deferred |
| **FluentAssertions 8.x** for test assertions | Rich API, widely used, excellent diagnostics | Requires paid Xceed commercial license ($129.95/dev/year) for commercial use since v8.0 (January 2025) | Rejected — licensing cost and compliance overhead; Shouldly selected (Apache 2.0, free) |
| **AwesomeAssertions** (FluentAssertions community fork) | Drop-in FA replacement, Apache 2.0, same API | Newer project, smaller community, long-term maintenance uncertain | Considered — Shouldly preferred for longer track record |
| **`object?` for FieldChange values** | Preserves native types (int, double) | Causes boxing (defeats zero-allocation), AOT trim risks, `ToString()` needed for ADO PATCH anyway | Rejected — `string?` is AOT-safe and matches ADO REST JSON patch format (RD-009) |

---

## Dependencies

### External Dependencies

| Dependency | Version | Purpose | AOT Status |
|------------|---------|---------|------------|
| .NET 9 SDK | 9.0.x | Build toolchain, AOT compiler | ✅ Full support |
| ConsoleAppFramework | 5.7.13 | CLI framework (source generator) | ✅ AOT-native |
| contoso.Data.Sqlite | 9.0.x | SQLite database access (.NET 9 compatible; v10.x targets .NET 10 — see RD-012) | ⚠️ Requires workaround ([#36068](https://github.com/dotnet/efcore/issues/36068)) |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.x | Native SQLite binary bundling (transitively pulled by contoso.Data.Sqlite 9.0.x; v3.0.x exists but introduces breaking changes and is not required) | ✅ With `Batteries.Init()` |
| System.Text.Json | 9.0.x (inbox) | JSON serialization (source generators) | ✅ With `JsonSerializerContext` |
| xUnit | 2.9.x | Test framework | N/A (not in published binary) |
| xUnit.Runner.VisualStudio | 3.x | Test discovery/execution | N/A |
| Shouldly | 4.x | Test assertions (free, Apache 2.0 license) | N/A |

### Internal Dependencies (Layer Order)

```
Twig (CLI) → Twig.Domain (pure domain) ← Twig.Infrastructure (infra)
                    ↑                              ↑
            Twig.Domain.Tests              Twig.Infrastructure.Tests
                                                   ↑
                                           Twig.Cli.Tests
```

### Sequencing Constraints

- EPIC 1 MUST complete before EPICs 2–3 (validates that the tech stack works under AOT)
- EPIC 2 MUST complete before EPIC 3 (WorkItem depends on value objects and ProcessConfiguration)
- EPICs 4–9 depend on EPICs 1–3 (not covered in this document)

---

## Impact Analysis

| Area | Impact |
|------|--------|
| **New codebase** | Greenfield — no backward compatibility concerns |
| **Build pipeline** | New AOT publish step required; CI must build + test + publish AOT binary |
| **Binary size** | Expected 10–25 MB for AOT single binary (validated in EPIC 1) |
| **Cold start** | Expected < 100ms (validated in EPIC 1) |
| **Developer setup** | Requires .NET 9 SDK; no additional tools for EPICs 1–3 |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| contoso.Data.Sqlite AOT native library loading fails | Medium | High | EPIC 1 validates this first. Workaround: explicit `SQLitePCLRaw.bundle_e_sqlite3` + `Batteries.Init()`. Fallback: use `sqlite-net-pcl` which has proven AOT support. |
| ConsoleAppFramework v5 source generator produces AOT warnings | Low | Medium | EPIC 1 validates. The library is explicitly designed for AOT. Fallback: raw `args` parsing with manual dispatch. |
| .NET 9 STS end-of-support (November 10, 2026) | Medium | Low | contoso extended STS support from 18 to 24 months (December 2025 DevBlogs announcement; InfoQ covered it earlier in September 2025). .NET 9 is supported until November 10, 2026. Plan migration to .NET 10 LTS in EPIC 7+. Timeline is comfortable for initial development and stabilization. |
| AOT binary too large (>30 MB) | Low | Low | Use `TrimMode=full`, `StripSymbols=true`, `InvariantGlobalization=true`. Monitor in EPIC 1. |
| ProcessConfiguration hard-coding misses edge cases in custom templates | Medium | Low | Design allows future override via config file. Covers standard templates (Agile, Scrum, CMMI, Basic) which represent >95% of usage. |
| FluentAssertions 8.x commercial licensing (**mitigated**) | N/A | N/A | FluentAssertions v8+ requires paid Xceed license ($129.95/dev/year). **Mitigated by selecting Shouldly** (Apache 2.0, free). See RD-010. |

---

## Open Questions

| ID | Question | Impact | Owner |
|----|----------|--------|-------|
| OQ-1 | Should `InvariantGlobalization=true` be used? It saves ~5–10 MB but breaks culture-specific date/string formatting. | Binary size vs. i18n | author |
| OQ-2 | Should the Result type be a custom implementation or use an existing library (e.g., `FluentResults`)? | Domain design | author |
| OQ-3 | Should ProcessConfiguration support loading custom templates from a JSON config file in EPIC 2, or defer to a later EPIC? | Scope | author |
| OQ-4 | What test coverage threshold should be enforced in CI? | Quality gate | author |
| OQ-5 | Should the WorkItem command queue support undo/revert operations in EPIC 3, or defer? | Scope | author |

---

## Implementation Phases

| Phase | Epic | Goal | Exit Criteria |
|-------|------|------|---------------|
| 1 | EPIC-001 | Project scaffold + AOT validation | AOT binary compiles, runs a ConsoleAppFramework command, performs SQLite round-trip, all with zero AOT warnings |
| 2 | EPIC-002 | Domain value objects + ProcessConfiguration | All 6 value objects implemented with tests; ProcessConfiguration covers 4 templates with ≥95% coverage |
| 3 | EPIC-003 | WorkItem aggregate + command queue | WorkItem supports all 4 command types; state transition logic validated against all templates; ≥95% coverage |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `Twig.sln` | Solution file referencing all projects |
| `Directory.Build.props` | Shared build properties (LangVersion, Nullable, ImplicitUsings, WarningsAsErrors) |
| `Directory.Packages.props` | Central package version management |
| `src/Twig/Twig.csproj` | CLI project — AOT-enabled console application |
| `src/Twig/Program.cs` | Entry point — ConsoleAppFramework bootstrap + `Batteries.Init()` |
| `src/Twig.Domain/Twig.Domain.csproj` | Pure domain class library (zero dependencies) |
| `src/Twig.Domain/ValueObjects/StateShorthand.cs` | Shorthand-to-state mapping per process template |
| `src/Twig.Domain/ValueObjects/IterationPath.cs` | Validated iteration path value object |
| `src/Twig.Domain/ValueObjects/AreaPath.cs` | Validated area path value object |
| `src/Twig.Domain/ValueObjects/WorkItemType.cs` | Constrained work item type value object |
| `src/Twig.Domain/ValueObjects/FieldChange.cs` | Immutable field change record |
| `src/Twig.Domain/ValueObjects/PendingNote.cs` | Pending note value object |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Process template rules aggregate |
| `src/Twig.Domain/Aggregates/WorkItem.cs` | Root aggregate with command queue |
| `src/Twig.Domain/Commands/IWorkItemCommand.cs` | Command interface: `Execute(WorkItem target)` and `ToFieldChange() → FieldChange?` (nullable for AddNoteCommand) |
| `src/Twig.Domain/Commands/ChangeStateCommand.cs` | State transition command |
| `src/Twig.Domain/Commands/UpdateFieldCommand.cs` | Field update command |
| `src/Twig.Domain/Commands/AddNoteCommand.cs` | Note attachment command (ToFieldChange returns null) |
| `src/Twig.Domain/Enums/ProcessTemplate.cs` | Agile, Scrum, CMMI, Basic enum |
| `src/Twig.Domain/Enums/TransitionKind.cs` | Forward, Backward, Cut enum |
| `src/Twig.Domain/Common/Result.cs` | Result monad for domain validation |
| `src/Twig.Domain/Interfaces/IWorkItemRepository.cs` | Repository contract |
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | ADO service contract |
| `src/Twig.Domain/Interfaces/IProcessConfigurationProvider.cs` | Config provider contract |
| `src/Twig.Domain/Interfaces/IAuthenticationProvider.cs` | Auth provider contract |
| `src/Twig.Domain/Interfaces/IUnitOfWork.cs` | Unit of work contract |
| `src/Twig.Domain/Interfaces/IEditorLauncher.cs` | Editor launcher contract (domain interface; implementation deferred to Infrastructure — RD-011) |
| `src/Twig.Domain/Services/StateTransitionService.cs` | Transition classification + validation |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Infrastructure class library |
| `tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj` | Domain unit tests |
| `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | StateShorthand tests |
| `tests/Twig.Domain.Tests/ValueObjects/IterationPathTests.cs` | IterationPath tests |
| `tests/Twig.Domain.Tests/ValueObjects/AreaPathTests.cs` | AreaPath tests |
| `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | WorkItemType tests |
| `tests/Twig.Domain.Tests/ValueObjects/FieldChangeTests.cs` | FieldChange tests |
| `tests/Twig.Domain.Tests/ValueObjects/PendingNoteTests.cs` | PendingNote tests |
| `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | ProcessConfiguration tests |
| `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | WorkItem aggregate tests |
| `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | Transition service tests |
| `tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj` | CLI smoke tests |
| `tests/Twig.Cli.Tests/AotSmokeTests.cs` | AOT validation smoke tests |
| `tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj` | Infrastructure test project (placeholder) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| (none — greenfield project) | |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: Project Scaffold + AOT Validation

**Goal**: Create the solution structure, configure Native AOT, and validate that ConsoleAppFramework v5 and contoso.Data.Sqlite work correctly under AOT compilation. This is a risk-reduction gate — if AOT fails, we pivot before investing in domain modeling.

**Prerequisites**: .NET 9 SDK installed.

**Tasks**:

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `Twig.sln` solution file with `dotnet new sln` | `Twig.sln` | TO DO |
| ITEM-002 | IMPL | Create `Directory.Build.props` with shared settings: `<LangVersion>13</LangVersion>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<IsAotCompatible>true</IsAotCompatible>` | `Directory.Build.props` | TO DO |
| ITEM-003 | IMPL | Create `Directory.Packages.props` for central package management with pinned versions: ConsoleAppFramework 5.7.13, contoso.Data.Sqlite 9.0.x (see RD-012: v10.x targets .NET 10), SQLitePCLRaw.bundle_e_sqlite3 2.1.x, xUnit 2.9.x, Shouldly 4.x | `Directory.Packages.props` | TO DO |
| ITEM-004 | IMPL | Create `src/Twig/Twig.csproj` — console app targeting `net9.0` with `<PublishAot>true</PublishAot>`, `<PublishTrimmed>true</PublishTrimmed>`, `<TrimMode>full</TrimMode>`, `<StripSymbols>true</StripSymbols>`. Reference ConsoleAppFramework, contoso.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3. | `src/Twig/Twig.csproj` | TO DO |
| ITEM-005 | IMPL | Create `src/Twig/Program.cs` with `SQLitePCL.Batteries.Init()` as first line, then `ConsoleApp.Run(args, …)` with a `version` command and a `smoke` command that opens an in-memory SQLite DB, writes a row, reads it back, and prints the result. | `src/Twig/Program.cs` | TO DO |
| ITEM-006 | IMPL | Create `src/Twig.Domain/Twig.Domain.csproj` — class library targeting `net9.0` with zero NuGet dependencies. | `src/Twig.Domain/Twig.Domain.csproj` | TO DO |
| ITEM-007 | IMPL | Create `src/Twig.Infrastructure/Twig.Infrastructure.csproj` — class library targeting `net9.0`, referencing Twig.Domain, contoso.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3. | `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | TO DO |
| ITEM-008 | IMPL | Create test projects: `tests/Twig.Domain.Tests/`, `tests/Twig.Infrastructure.Tests/`, `tests/Twig.Cli.Tests/` — all targeting `net9.0`, referencing xUnit, Shouldly, and their respective source projects. | `tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj`, `tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj`, `tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj` | TO DO |
| ITEM-009 | IMPL | Add all projects to `Twig.sln` via `dotnet sln add`. | `Twig.sln` | TO DO |
| ITEM-010 | TEST | Write AOT smoke test in `tests/Twig.Cli.Tests/AotSmokeTests.cs`: compile, verify no AOT warnings, verify binary < 30 MB. | `tests/Twig.Cli.Tests/AotSmokeTests.cs` | TO DO |
| ITEM-011 | TEST | Validate: `dotnet build` succeeds with zero warnings, `dotnet test` passes, `dotnet publish -r win-x64` produces AOT binary, binary executes `smoke` command successfully. | (manual validation) | TO DO |

**Acceptance Criteria**:

- [ ] `dotnet build Twig.sln` completes with zero errors and zero warnings
- [ ] `dotnet test Twig.sln` passes all tests
- [ ] `dotnet publish src/Twig -c Release -r win-x64` produces a Native AOT binary
- [ ] AOT binary runs `smoke` command: SQLite round-trip succeeds, output confirms write/read
- [ ] AOT binary runs `version` command: prints version string
- [ ] Binary size is < 30 MB
- [ ] No AOT analysis warnings in publish output

---

### EPIC-002: Domain Value Objects + ProcessConfiguration

**Goal**: Implement all domain value objects and the ProcessConfiguration aggregate with complete test coverage. This establishes the domain vocabulary and process rules that all subsequent features depend on.

**Prerequisites**: EPIC-001 complete (solution compiles).

**Tasks**:

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Implement `Result<T>` and `Result` types in `Twig.Domain/Common/Result.cs`. `Result<T>` has `IsSuccess`, `Value`, `Error` properties. Factory methods: `Result.Ok(value)`, `Result.Fail(error)`. | `src/Twig.Domain/Common/Result.cs` | TO DO |
| ITEM-013 | IMPL | Implement `ProcessTemplate` enum in `Twig.Domain/Enums/ProcessTemplate.cs`: `Agile`, `Scrum`, `CMMI`, `Basic`. | `src/Twig.Domain/Enums/ProcessTemplate.cs` | TO DO |
| ITEM-014 | IMPL | Implement `TransitionKind` enum in `Twig.Domain/Enums/TransitionKind.cs`: `Forward`, `Backward`, `Cut`. | `src/Twig.Domain/Enums/TransitionKind.cs` | TO DO |
| ITEM-015 | IMPL | Implement `StateShorthand` as `readonly record struct` in `Twig.Domain/ValueObjects/StateShorthand.cs`. Static method `Resolve(char code, ProcessTemplate template, WorkItemType workItemType) → Result<string>`. Mappings are type-aware: e.g., Scrum PBI `c`→"Committed" vs. Scrum Task `c`→"In Progress" vs. Scrum Feature `c`→"In Progress". CMMI uses "Proposed" not "New". Returns `Result.Fail` for invalid combinations (e.g., `s` on Basic, `s` on Agile Task, `x` on Agile Bug, `s` on Agile Feature/Epic). See StateShorthand mapping table in Proposed Design. | `src/Twig.Domain/ValueObjects/StateShorthand.cs` | TO DO |
| ITEM-016 | TEST | Write `StateShorthandTests.cs`: test all 5 codes × 4 templates × key work item types per template. Test matrix covers distinct state-set groups: Basic (all types share states, 5×1=5), Agile (User Story + Bug + Feature/Epic + Task = 4 groups, 5×4=20), Scrum (PBI/Bug + Feature/Epic + Task = 3 groups, 5×3=15), CMMI (all types share states, 5×1=5). Total: ~45 unique cases + invalid code cases + error-returning combinations (e.g., `s` on Basic, `x` on Agile Bug, `s` on Agile Feature). | `tests/Twig.Domain.Tests/ValueObjects/StateShorthandTests.cs` | TO DO |
| ITEM-017 | IMPL | Implement `IterationPath` as `readonly record struct` in `Twig.Domain/ValueObjects/IterationPath.cs`. `Parse(string raw) → Result<IterationPath>`. Validation: non-null, non-empty, no leading/trailing backslash anomalies. Store normalized string. | `src/Twig.Domain/ValueObjects/IterationPath.cs` | TO DO |
| ITEM-018 | TEST | Write `IterationPathTests.cs`: valid paths, empty string, null, various edge cases. | `tests/Twig.Domain.Tests/ValueObjects/IterationPathTests.cs` | TO DO |
| ITEM-019 | IMPL | Implement `AreaPath` as `readonly record struct` in `Twig.Domain/ValueObjects/AreaPath.cs`. Same pattern as IterationPath. | `src/Twig.Domain/ValueObjects/AreaPath.cs` | TO DO |
| ITEM-020 | TEST | Write `AreaPathTests.cs`. | `tests/Twig.Domain.Tests/ValueObjects/AreaPathTests.cs` | TO DO |
| ITEM-021 | IMPL | Implement `WorkItemType` as `readonly record struct` in `Twig.Domain/ValueObjects/WorkItemType.cs`. Known constants: `Epic`, `Feature`, `UserStory`, `ProductBacklogItem`, `Requirement`, `Task`, `Bug`, `Issue`, `TestCase`, plus process-specific types (Impediment, Change Request, etc.). `Parse(string raw) → Result<WorkItemType>` validates against known types. | `src/Twig.Domain/ValueObjects/WorkItemType.cs` | TO DO |
| ITEM-022 | TEST | Write `WorkItemTypeTests.cs`: all known types, unknown type, case sensitivity. | `tests/Twig.Domain.Tests/ValueObjects/WorkItemTypeTests.cs` | TO DO |
| ITEM-023 | IMPL | Implement `FieldChange` as `readonly record struct` in `Twig.Domain/ValueObjects/FieldChange.cs`: `FieldName` (string), `OldValue` (string?), `NewValue` (string?). | `src/Twig.Domain/ValueObjects/FieldChange.cs` | TO DO |
| ITEM-024 | TEST | Write `FieldChangeTests.cs`: equality, construction, null values. | `tests/Twig.Domain.Tests/ValueObjects/FieldChangeTests.cs` | TO DO |
| ITEM-025 | IMPL | Implement `PendingNote` as `readonly record struct` in `Twig.Domain/ValueObjects/PendingNote.cs`: `Text` (string), `CreatedAt` (DateTimeOffset), `IsHtml` (bool). | `src/Twig.Domain/ValueObjects/PendingNote.cs` | TO DO |
| ITEM-026 | TEST | Write `PendingNoteTests.cs`. | `tests/Twig.Domain.Tests/ValueObjects/PendingNoteTests.cs` | TO DO |
| ITEM-027 | IMPL | Implement `ProcessConfiguration` aggregate in `Twig.Domain/Aggregates/ProcessConfiguration.cs`. Factory method `ForTemplate(ProcessTemplate)`. Contains `Dictionary<WorkItemType, TypeConfig>` where `TypeConfig` holds `States` (ordered list), `AllowedChildTypes`, and `TransitionRules` (state-pair → TransitionKind). Implement all 4 templates with accurate ADO process data. | `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | TO DO |
| ITEM-028 | TEST | Write `ProcessConfigurationTests.cs`: verify each template has correct states for each work item type, correct child type hierarchies, correct transition classifications (forward/backward/cut). Cover all 4 templates exhaustively. | `tests/Twig.Domain.Tests/Aggregates/ProcessConfigurationTests.cs` | TO DO |
| ITEM-029 | IMPL | Implement domain interfaces (contracts only, no implementation): `IWorkItemRepository`, `IAdoWorkItemService`, `IProcessConfigurationProvider`, `IAuthenticationProvider`, `IUnitOfWork`, `IEditorLauncher` in `Twig.Domain/Interfaces/`. `IEditorLauncher` is defined in Domain but implemented in Infrastructure (RD-011). | `src/Twig.Domain/Interfaces/*.cs` | TO DO |
| ITEM-030 | TEST | Run full domain test suite, verify ≥95% line coverage on `Twig.Domain`. | (test execution) | TO DO |

**Acceptance Criteria**:

- [ ] All 6 value objects compile and have full test suites
- [ ] `StateShorthand.Resolve` correctly maps all 5 codes for all 4 process templates across all relevant work item types
- [ ] `IterationPath.Parse` and `AreaPath.Parse` reject invalid input and normalize valid input
- [ ] `WorkItemType.Parse` accepts known ADO types and rejects unknown types
- [ ] `ProcessConfiguration.ForTemplate` returns correct configuration for Agile, Scrum, CMMI, Basic
- [ ] Each template's `TypeConfig` has accurate states, child types, and transition rules
- [ ] Domain interfaces are defined with XML documentation
- [ ] `dotnet test` passes all domain tests
- [ ] Domain project has zero NuGet dependencies (verified in .csproj)

---

### EPIC-003: Domain WorkItem Aggregate + Command Queue

**Goal**: Implement the WorkItem root aggregate with the command-queue pattern. WorkItem is the central entity that all CLI commands will operate on. State transition logic is validated against ProcessConfiguration.

**Prerequisites**: EPIC-002 complete (value objects and ProcessConfiguration available).

**Tasks**:

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-031 | IMPL | Implement `IWorkItemCommand` interface in `Twig.Domain/Commands/IWorkItemCommand.cs`: `void Execute(WorkItem target)` and `FieldChange? ToFieldChange()`. The nullable return type accommodates commands like `AddNoteCommand` where notes are not field changes (returns `null`). | `src/Twig.Domain/Commands/IWorkItemCommand.cs` | TO DO |
| ITEM-032 | IMPL | Implement `ChangeStateCommand` in `Twig.Domain/Commands/ChangeStateCommand.cs`: holds `NewState` (string) and optional `Confirmation` (string? reason for backward/cut). `Execute` sets `WorkItem.State`. `ToFieldChange` returns `FieldChange("System.State", oldState, newState)`. | `src/Twig.Domain/Commands/ChangeStateCommand.cs` | TO DO |
| ITEM-033 | IMPL | Implement `UpdateFieldCommand` in `Twig.Domain/Commands/UpdateFieldCommand.cs`: holds `FieldName` and `Value`. `Execute` updates `WorkItem.Fields[FieldName]`. | `src/Twig.Domain/Commands/UpdateFieldCommand.cs` | TO DO |
| ITEM-034 | IMPL | Implement `AddNoteCommand` in `Twig.Domain/Commands/AddNoteCommand.cs`: holds `PendingNote`. `Execute` appends to `WorkItem.PendingNotes`. `ToFieldChange` returns `null` (notes are not field changes — they are synced via a separate ADO REST endpoint for work item comments). | `src/Twig.Domain/Commands/AddNoteCommand.cs` | TO DO |
| ITEM-035 | IMPL | Implement `WorkItem.CreateSeed` static factory method directly in `Twig.Domain/Aggregates/WorkItem.cs` (NOT as an `IWorkItemCommand` — seed creation produces a new aggregate instance rather than mutating an existing target). Factory returns a new `WorkItem` with `IsSeed = true`, `Id = -1` (sentinel), and the specified type/title/parentId. | `src/Twig.Domain/Aggregates/WorkItem.cs` | TO DO |
| ITEM-036 | IMPL | Implement `StateTransitionService` in `Twig.Domain/Services/StateTransitionService.cs`: takes `ProcessConfiguration`, classifies transitions as Forward/Backward/Cut, validates that a transition is allowed, checks confirmation requirements. | `src/Twig.Domain/Services/StateTransitionService.cs` | TO DO |
| ITEM-037 | TEST | Write `StateTransitionServiceTests.cs`: test forward transitions auto-allow, backward transitions require confirmation, cut transitions require confirmation + reason, invalid transitions rejected. Cover all 4 templates. | `tests/Twig.Domain.Tests/Services/StateTransitionServiceTests.cs` | TO DO |
| ITEM-038 | IMPL | Implement `WorkItem` aggregate in `Twig.Domain/Aggregates/WorkItem.cs`. Properties: `Id`, `Type`, `Title`, `State`, `AssignedTo`, `IterationPath`, `AreaPath`, `ParentId`, `Revision`, `Fields`, `IsDirty`, `IsSeed`, `PendingNotes`, `PendingCommands` (private queue). Methods: `ChangeState(newState, confirmation?)`, `UpdateField(name, value)`, `AddNote(text)`, `ApplyCommands()`, `MarkSynced(revision)`. Static factory: `CreateSeed(type, title, parentId?)` (see ITEM-035). Each mutation method validates input, creates a command, and enqueues it. `ApplyCommands()` executes all commands, sets `IsDirty = true`, returns `List<FieldChange>` (filtered — excludes null entries from commands like AddNoteCommand where `ToFieldChange()` returns null). | `src/Twig.Domain/Aggregates/WorkItem.cs` | TO DO |
| ITEM-039 | TEST | Write `WorkItemTests.cs` — construction and property tests: create WorkItem with all properties, verify initial state (IsDirty = false, empty command queue, empty pending notes). | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-040 | TEST | Write `WorkItemTests.cs` — state transition tests: `ChangeState` forward auto-applies, backward requires confirmation, cut requires confirmation + reason, invalid transition returns error Result. Test against each process template. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-041 | TEST | Write `WorkItemTests.cs` — field update tests: `UpdateField` enqueues command, `ApplyCommands` applies it, verify FieldChange output, verify IsDirty flag. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-042 | TEST | Write `WorkItemTests.cs` — note tests: `AddNote` enqueues command, `ApplyCommands` adds note to PendingNotes list. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-043 | TEST | Write `WorkItemTests.cs` — seed tests: `CreateSeed` returns WorkItem with `IsSeed = true`, negative or sentinel Id, correct type and title. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-044 | TEST | Write `WorkItemTests.cs` — multi-command atomic apply: enqueue ChangeState + UpdateField + AddNote, call `ApplyCommands()` once, verify all changes applied. FieldChange list contains entries for ChangeState and UpdateField; AddNote produces `null` from `ToFieldChange()` and is excluded from the FieldChange list but note is present in PendingNotes. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-045 | TEST | Write `WorkItemTests.cs` — `MarkSynced` clears dirty flag and updates revision. Verify IsDirty = false after MarkSynced. | `tests/Twig.Domain.Tests/Aggregates/WorkItemTests.cs` | TO DO |
| ITEM-046 | TEST | Run full test suite, verify ≥95% line coverage on `Twig.Domain`, zero warnings. | (test execution) | TO DO |

**Acceptance Criteria**:

- [ ] `IWorkItemCommand` interface and all 3 command implementations compile (ChangeStateCommand, UpdateFieldCommand, AddNoteCommand — CreateSeed is a static factory, not a command)
- [ ] `StateTransitionService` correctly classifies transitions for all 4 process templates
- [ ] `WorkItem.ChangeState` validates transitions via StateTransitionService and ProcessConfiguration
- [ ] Forward state transitions apply without confirmation
- [ ] Backward state transitions require confirmation metadata
- [ ] Cut transitions require confirmation + reason
- [ ] `WorkItem.UpdateField` correctly enqueues and applies field changes
- [ ] `WorkItem.AddNote` correctly enqueues and applies notes; `AddNoteCommand.ToFieldChange()` returns `null`
- [ ] `WorkItem.CreateSeed` produces a valid seed WorkItem (static factory, not command queue)
- [ ] `WorkItem.ApplyCommands()` applies all queued commands atomically and returns FieldChange list (excluding null entries from AddNoteCommand)
- [ ] `WorkItem.MarkSynced(revision)` clears dirty flag and updates revision
- [ ] Multiple commands applied in one `ApplyCommands()` call produce correct cumulative FieldChange list
- [ ] `dotnet test` passes all domain tests with ≥95% line coverage
- [ ] Zero compiler warnings across all projects

---

## References

| Resource | URL |
|----------|-----|
| ConsoleAppFramework v5 (GitHub) | https://github.com/Cysharp/ConsoleAppFramework |
| ConsoleAppFramework v5 Blog Post | https://neuecc.medium.com/consoleappframework-v5-zero-overhead-native-aot-compatible-cli-framework-for-c-8f496df8d9d1 |
| contoso.Data.Sqlite AOT Issue #36068 | https://github.com/dotnet/efcore/issues/36068 |
| Multiplatform AOT + SQLite Guide | https://www.mostlylucid.net/blog/multiplatform-aot-sqlite |
| .NET Native AOT Deployment Overview | https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ |
| System.Text.Json Source Generators | https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation |
| Azure DevOps REST API v7.1 | https://learn.microsoft.com/en-us/rest/api/azure/devops/ |
| ADO Process Template States | https://learn.microsoft.com/en-us/azure/devops/boards/work-items/guidance/choose-process |
| ADO Workflow State Categories | https://learn.microsoft.com/en-us/azure/devops/boards/work-items/workflow-and-state-categories |
| System.CommandLine 2.0.0 Stable Release | https://github.com/dotnet/command-line-api/discussions/2725 |
| .NET STS 24-Month Support Announcement | https://devblogs.microsoft.com/dotnet/dotnet-sts-releases-supported-for-24-months/ |
| FluentAssertions v8 License Change | https://www.infoq.com/news/2025/01/fluent-assertions-v8-license/ |
| Shouldly Assertion Library | https://shouldly.readthedocs.io/ |
