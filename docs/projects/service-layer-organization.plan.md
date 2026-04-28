# Service Layer Organization — Subfolder Taxonomy Refactor

| Field | Value |
|-------|-------|
| **Status** | ✅ Done |
| **Work Item** | #2118 |
| **Type** | Epic |
| **Author** | Copilot |
| **Plan Revision** | 3 |
| **Revision Notes** | Revision 3: Full codebase revalidation. All 56 service files read and re-categorized; 8-subfolder taxonomy confirmed accurate. Consumer audit refreshed (~160 files). Cross-subfolder dependency graph re-verified cycle-free. No changes to file classification or structure from revision 2. |

---

## Executive Summary

The `Services/` folder in `Twig.Domain` contains 56 files with no sub-organization,
ranging from tiny utilities (`CacheAgeFormatter`, `Pluralizer`) to complex orchestrators
(`RefreshOrchestrator`, `SeedPublishOrchestrator`). This plan introduces **8 cohesion-based
subfolders** — Sync/, Seed/, Workspace/, Navigation/, Field/, Process/, Git/, and
Rendering/ — moving 55 of the 56 files into logical groupings while leaving one
general-purpose mapper (`LinkTypeMapper`) at the root. The refactor is **namespace-only**:
file moves, namespace declaration updates, and `using` statement changes across ~160
consumer files. Zero behavioral changes. The test project mirrors the new structure.
The result is a discoverable, self-documenting folder layout that eliminates the
"where does this go?" problem.

---

## Background

### Current State

The `src/Twig.Domain/Services/` folder is a flat directory containing 56 files:

| Category | Files | Examples |
|----------|-------|---------|
| Sync/Refresh | 10 | `SyncCoordinator`, `RefreshOrchestrator`, `ConflictResolver` |
| Seed lifecycle | 10 | `SeedFactory`, `SeedPublishOrchestrator`, `SeedValidator` |
| Workspace/Status | 14 | `WorkingSet`, `StatusOrchestrator`, `SprintHierarchyBuilder` |
| Navigation | 5 | `ActiveItemResolver`, `ContextChangeService`, `PatternMatcher` |
| Field management | 3 | `FieldProfileService`, `FieldDefinitionHasher`, `FieldImportFilter` |
| Process/State | 6 | `StateTransitionService`, `FlowTransitionService`, `DynamicProcessConfigProvider` |
| Git integration | 4 | `BranchLinkService`, `BranchNamingService`, `CommitMessageService` |
| Rendering utilities | 3 | `CacheAgeFormatter`, `DeterministicTypeColor`, `TypeColorResolver` |
| General utility | 1 | `LinkTypeMapper` |

All files share the namespace `Twig.Domain.Services`. Every consumer uses a single
`using Twig.Domain.Services;` directive regardless of which specific services it needs.

### Consumer Audit

The namespace `Twig.Domain.Services` is referenced by **~160 files** across the solution:

| Project | File Count | Location Pattern |
|---------|-----------|-----------------|
| Twig (CLI) Commands | 39 | `src/Twig/Commands/*.cs` |
| Twig DI modules | 2 | `src/Twig/DependencyInjection/` |
| Twig Rendering | 3 | `src/Twig/Rendering/` |
| Twig Hints/Formatters | 2 | `src/Twig/Hints/`, `src/Twig/Formatters/` |
| Twig.Infrastructure | 3 | `TwigServiceRegistration.cs`, `PromptStateWriter.cs`, `AdoResponseMapper.cs` |
| Twig.Mcp Tools | 5 | `src/Twig.Mcp/Tools/` |
| Twig.Mcp Services | 2 | `src/Twig.Mcp/Services/` |
| Twig.Domain (internal) | 3 | `Extensions/`, `ReadModels/`, `Interfaces/` |
| Domain Tests (Services) | 53 | `tests/Twig.Domain.Tests/Services/` |
| CLI Tests | ~40 | `tests/Twig.Cli.Tests/Commands/`, `Rendering/`, `Formatters/` |
| MCP Tests | 6 | `tests/Twig.Mcp.Tests/` |
| TestKit | 1 | `tests/Twig.TestKit/` |

### Motivation

This refactor is Item 5 in the [Domain Model Critique](../architecture/domain-model-critique.md)
(Severity: Medium). The flat structure creates:

1. **Poor discoverability** — finding a service requires scanning 56 alphabetically-sorted files
2. **No cohesion signal** — sync services sit next to seed services sit next to rendering utilities
3. **Growing "where does this go?" problem** — each new service gets dumped into the flat folder
4. **IDE noise** — autocomplete for `Twig.Domain.Services.` returns 56+ types

### Prior Art

Other folders in `Twig.Domain` are already well-organized: `Aggregates/`, `Common/`,
`Enums/`, `Extensions/`, `Interfaces/`, `ReadModels/`, `ValueObjects/`. The `Services/`
folder is the only flat mega-directory.

---

## Problem Statement

The `Services/` directory contains 56 files spanning 8+ distinct concerns with no
sub-organization. This creates:

1. **Discoverability friction**: Developers must scan all 56 files to find related services
2. **Missing cohesion boundaries**: No signal about which services collaborate (e.g., all
   sync-related services are interspersed with seed and workspace services)
3. **Namespace pollution**: A single `using Twig.Domain.Services` imports all 56 types into
   scope, making autocomplete noisy and hiding actual dependencies
4. **Scaling problem**: Every new service added to the flat directory worsens all three issues

---

## Goals and Non-Goals

### Goals

1. **Organize** all 56 service files into cohesion-based subfolders with clear naming
2. **Update namespaces** to match folder structure (e.g., `Twig.Domain.Services.Sync`)
3. **Update all consumers** with specific `using` directives for the sub-namespaces they need
4. **Mirror structure in tests** — move test files into matching subfolders
5. **Maintain build integrity** — solution builds and all tests pass after refactor
6. **Clean git history** — single commit with file renames tracked by git

### Non-Goals

1. **No behavioral changes** — zero logic, algorithm, or API changes
2. **No interface changes** — public APIs, DI registrations, and method signatures remain identical
3. **No new abstractions** — no new interfaces, base classes, or patterns introduced
4. **No orchestrator refactoring** — that's a separate epic (#6 in the critique)
5. **No test logic changes** — only namespace/using updates in test files

---

## Requirements

### Functional

- **FR-1**: Each service file resides in exactly one subfolder (or root) with a namespace
  matching its folder path
- **FR-2**: All consumer files compile with updated `using` directives
- **FR-3**: DI registration in `TwigServiceRegistration.cs` works unchanged (service types
  remain the same, only namespaces change)
- **FR-4**: Test project structure mirrors source project structure

### Non-Functional

- **NFR-1**: Zero behavioral regression — all existing tests pass without modification
  to test logic (only namespace/using updates)
- **NFR-2**: Git tracks all moves as renames (use `git mv` or equivalent)
- **NFR-3**: No new NuGet dependencies or project references
- **NFR-4**: AOT compilation continues to work (`PublishAot=true`)

---

## Proposed Design

### Architecture Overview

The refactor introduces **8 subfolders** under `Services/`, each representing a cohesion
boundary. One file (`LinkTypeMapper`) remains at the root as a cross-cutting utility.

```
Services/
├── Sync/           (10 files) — Cache synchronization, refresh, and conflict resolution
├── Seed/           (10 files) — Seed creation, validation, publishing, and reconciliation
├── Workspace/      (14 files) — Working set, status, sprint hierarchy, and tracking
├── Navigation/     (5 files)  — Active item resolution and context switching
├── Field/          (3 files)  — Field definitions, profiles, and filtering
├── Process/        (6 files)  — State transitions, process config, and verification
├── Git/            (4 files)  — Branch linking, naming, commits, and ID extraction
├── Rendering/      (3 files)  — Display formatting and color resolution
└── LinkTypeMapper.cs          — Cross-cutting ADO link type mapping (root)
```

### File Classification

#### Sync/ (10 files) — `Twig.Domain.Services.Sync`

| File | Responsibility |
|------|---------------|
| `SyncCoordinator.cs` | Orchestrates per-item sync between cache and ADO |
| `SyncCoordinatorPair.cs` | Creates read-only and read-write SyncCoordinator instances |
| `SyncGuard.cs` | Identifies dirty/pending items that must not be overwritten |
| `SyncResult.cs` | Discriminated union of sync operation outcomes |
| `ProtectedCacheWriter.cs` | Writes items to cache while protecting dirty items |
| `RefreshOrchestrator.cs` | Full refresh cycle: fetch → conflict → save → hydrate |
| `FieldDefinitionSyncService.cs` | Syncs field definitions from ADO to local store |
| `ProcessTypeSyncService.cs` | Syncs process type configuration from ADO |
| `ConflictResolver.cs` | Detects and classifies local-vs-remote merge conflicts |
| `WiqlQueryBuilder.cs` | Builds WIQL SELECT queries for ADO data fetching |

**Rationale**: All files participate in the data synchronization pipeline. `ConflictResolver`
handles sync merge conflicts. `WiqlQueryBuilder` builds queries consumed exclusively by
`RefreshOrchestrator`.

#### Seed/ (10 files) — `Twig.Domain.Services.Seed`

| File | Responsibility |
|------|---------------|
| `SeedFactory.cs` | Creates new seed work items with parent validation |
| `SeedValidator.cs` | Validates seeds against configurable publish rules |
| `SeedPublishOrchestrator.cs` | Orchestrates single/batch seed publishing to ADO |
| `SeedReconcileOrchestrator.cs` | Repairs stale seed links and parent references |
| `SeedEditorFormat.cs` | Converts seeds to/from section-header editor format |
| `SeedDependencyGraph.cs` | Topological sort of seeds for publish ordering |
| `SeedIdCounter.cs` | Thread-safe negative ID counter for unpublished seeds |
| `SeedLinkPromoter.cs` | Promotes virtual seed links to ADO relations |
| `SeedLinkTypeMapper.cs` | Maps seed link types to ADO relation type names |
| `DuplicateGuard.cs` | Checks for existing children to prevent duplicate creation |

**Rationale**: All files support the seed lifecycle. `DuplicateGuard` is primarily consumed
by `SeedPublishOrchestrator` and MCP creation tools during seed publish.

#### Workspace/ (14 files) — `Twig.Domain.Services.Workspace`

| File | Responsibility |
|------|---------------|
| `WorkingSet.cs` | Value object: aggregated set of relevant work item IDs |
| `WorkingSetService.cs` | Computes a WorkingSet from cache state |
| `WorkingLevelResolver.cs` | Determines if a type is above the working level |
| `StatusOrchestrator.cs` | Gathers data for status snapshots |
| `StatusFieldsConfig.cs` | Generates/parses status-fields configuration |
| `DirtyStateSummary.cs` | Builds one-line summaries of pending changes |
| `SprintHierarchyBuilder.cs` | Builds hierarchical sprint item trees |
| `BacklogHierarchyService.cs` | Infers parent-child type relationships from config |
| `BacklogOrderer.cs` | Positions published items in backlog order |
| `CeilingComputer.cs` | Determines ceiling backlog level for hierarchy trimming |
| `ColumnResolver.cs` | Produces ordered column specs for dynamic table rendering |
| `WorkItemExportFormat.cs` | Generates/parses markdown multi-item export format |
| `WorkTreeFetcher.cs` | Recursively fetches work item descendants from cache |
| `TrackingService.cs` | Manages manual work item tracking and exclusions |

**Rationale**: All files support workspace presentation — what the user sees when running
`twig status`, `twig workspace`, or `twig tree`. `TrackingService` manages which items
appear in the tracked section of workspace views.

#### Navigation/ (5 files) — `Twig.Domain.Services.Navigation`

| File | Responsibility |
|------|---------------|
| `ActiveItemResolver.cs` | Resolves the active work item from context store |
| `ActiveItemResult.cs` | Discriminated union for resolution outcomes |
| `ActiveItemResultExtensions.cs` | Extension methods for ActiveItemResult |
| `ContextChangeService.cs` | Extends working set around context changes |
| `PatternMatcher.cs` | Matches user patterns against candidate lists |

**Rationale**: All files support navigation — finding, selecting, and switching between
work items. `ContextChangeService` uses `SyncCoordinator` and `ProtectedCacheWriter`
from Sync/ but its primary concern is navigation context management.

#### Field/ (3 files) — `Twig.Domain.Services.Field`

| File | Responsibility |
|------|---------------|
| `FieldProfileService.cs` | Computes populated field profiles from cached items |
| `FieldDefinitionHasher.cs` | SHA256 hash of field definitions for change detection |
| `FieldImportFilter.cs` | Filters which ADO fields to import into WorkItem.Fields |

**Rationale**: All files deal with field definition management and filtering.

#### Process/ (6 files) — `Twig.Domain.Services.Process`

| File | Responsibility |
|------|---------------|
| `StateTransitionService.cs` | Validates state transitions against process rules |
| `StateCategoryResolver.cs` | Maps state names to state categories |
| `DynamicProcessConfigProvider.cs` | Builds ProcessConfiguration from dynamic type data |
| `FlowTransitionService.cs` | Encapsulates resolution + state transition for flow commands |
| `ParentStatePropagationService.cs` | Auto-activates parent when child starts |
| `DescendantVerificationService.cs` | Verifies all descendants are in terminal states |

**Rationale**: All files deal with process configuration, state management, and state
verification. `DescendantVerificationService` is used by `FlowCloseCommand` to verify
child states before closing a parent — a process verification concern.

#### Git/ (4 files) — `Twig.Domain.Services.Git`

| File | Responsibility |
|------|---------------|
| `BranchLinkService.cs` | Links git branches to work items as ADO artifact URIs |
| `BranchNamingService.cs` | Generates branch names from work items via templates |
| `CommitMessageService.cs` | Formats commit messages with conventional prefixes |
| `WorkItemIdExtractor.cs` | Extracts work item IDs from branch names via regex |

**Rationale**: All files bridge git operations with work items.

#### Rendering/ (3 files) — `Twig.Domain.Services.Rendering`

| File | Responsibility |
|------|---------------|
| `CacheAgeFormatter.cs` | Formats cache age as human-readable strings |
| `DeterministicTypeColor.cs` | Hash-based ANSI color assignment for type names |
| `TypeColorResolver.cs` | Resolves type names to hex colors from config/ADO |

**Rationale**: All files produce display-layer output (formatted strings, color codes).
These are consumed by CLI rendering (`SpectreRenderer`, `SpectreTheme`) and formatters.

#### Root (1 file) — `Twig.Domain.Services`

| File | Responsibility |
|------|---------------|
| `LinkTypeMapper.cs` | Bidirectional mapping between friendly and ADO link types |

**Rationale**: `LinkTypeMapper` is a cross-cutting utility consumed by `LinkCommand`,
MCP tools, and seed publishing. It doesn't belong exclusively to any single subfolder.

### Namespace Changes

Each subfolder maps to a child namespace:

| Folder | Old Namespace | New Namespace |
|--------|--------------|---------------|
| `Services/Sync/` | `Twig.Domain.Services` | `Twig.Domain.Services.Sync` |
| `Services/Seed/` | `Twig.Domain.Services` | `Twig.Domain.Services.Seed` |
| `Services/Workspace/` | `Twig.Domain.Services` | `Twig.Domain.Services.Workspace` |
| `Services/Navigation/` | `Twig.Domain.Services` | `Twig.Domain.Services.Navigation` |
| `Services/Field/` | `Twig.Domain.Services` | `Twig.Domain.Services.Field` |
| `Services/Process/` | `Twig.Domain.Services` | `Twig.Domain.Services.Process` |
| `Services/Git/` | `Twig.Domain.Services` | `Twig.Domain.Services.Git` |
| `Services/Rendering/` | `Twig.Domain.Services` | `Twig.Domain.Services.Rendering` |
| `Services/` (root) | `Twig.Domain.Services` | `Twig.Domain.Services` (unchanged) |

### Consumer Update Strategy

Each consumer file currently has `using Twig.Domain.Services;`. After the refactor:

1. **Determine which sub-namespaces the file actually uses** by examining type references
2. **Replace** the single `using Twig.Domain.Services;` with specific sub-namespace usings
3. **Keep** `using Twig.Domain.Services;` only if the file references `LinkTypeMapper`
4. **Sort** using directives per .editorconfig conventions

Example transformation:
```csharp
// Before
using Twig.Domain.Services;

// After
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
```

### DI Registration Impact

`TwigServiceRegistration.cs` currently has a single `using Twig.Domain.Services;`.
After the refactor, it needs:
```csharp
using Twig.Domain.Services.Process;    // DynamicProcessConfigProvider
using Twig.Domain.Services.Seed;       // SeedFactory, SeedIdCounter
using Twig.Domain.Services.Workspace;  // SprintHierarchyBuilder, TrackingService
```

No registration code changes — only `using` directives.

### Design Decisions

1. **8 subfolders instead of 6**: The epic proposed 6 subfolders. Analysis revealed Git/
   (4 files) and Rendering/ (3 files) as natural cohesion groups that would otherwise
   remain as uncategorized files at the root. 8 subfolders with 1 root file is cleaner
   than 6 subfolders with 13 root files.

2. **ConflictResolver → Sync/** (not root): `ConflictResolver.Resolve()` handles
   local-vs-remote merge conflicts during sync operations. It's exclusively used in
   the sync pipeline.

3. **DuplicateGuard → Seed/** (not root): Primary consumer is `SeedPublishOrchestrator`.
   MCP creation tools also use it, but the concern (preventing duplicate child creation)
   is core to the seed publish workflow.

4. **TrackingService → Workspace/** (not standalone): Tracking determines which items
   appear in workspace views. It's conceptually "what's in my workspace" alongside
   `WorkingSet` and `WorkingSetService`.

5. **DescendantVerificationService → Process/** (not Workspace/): Used by
   `FlowCloseCommand` to verify child states before allowing parent close. This is a
   process state verification concern, not a workspace display concern.

6. **LinkTypeMapper stays at root**: It's the only file that doesn't fit cleanly into
   any subfolder — used by link commands, MCP tools, and seed publishing across
   multiple concerns.

7. **Full namespace change** (not physical-only): The epic explicitly requires updating
   namespace declarations and using statements. Physical-only moves (keep old namespace)
   would improve folder discoverability but not address namespace pollution.

### Cross-Subfolder Dependencies

Several services reference types in other subfolders. These create cross-subfolder
`using` directives within `Twig.Domain.Services.*`:

| Source File | Subfolder | References Types In |
|-------------|-----------|-------------------|
| `ContextChangeService` | Navigation/ | Sync/ (`SyncCoordinator`, `ProtectedCacheWriter`) |
| `FlowTransitionService` | Process/ | Navigation/ (`ActiveItemResolver`), Sync/ (`ProtectedCacheWriter`) |
| `ParentStatePropagationService` | Process/ | Sync/ (`ProtectedCacheWriter`) |
| `StatusOrchestrator` | Workspace/ | Navigation/ (`ActiveItemResolver`), Sync/ (`SyncCoordinatorPair`) |
| `RefreshOrchestrator` | Sync/ | Workspace/ (`WorkingSetService`) |
| `SeedPublishOrchestrator` | Seed/ | Workspace/ (`BacklogOrderer`) |
| `StatusFieldsConfig` | Workspace/ | Field/ (`FieldImportFilter`) |
| `ProcessTypeSyncService` | Sync/ | Workspace/ (`BacklogHierarchyService`), Process/ (`StateCategoryResolver`) |

These cross-references are expected and acceptable — they represent legitimate
collaboration between cohesion groups. No circular dependency chains exist at the
subfolder level (verified by tracing the dependency graph).

---

## Dependencies

### External Dependencies

None — this is a pure namespace refactor with no new NuGet packages or tools.

### Internal Dependencies

- **Twig.Domain.csproj** — no project-level changes needed; the SDK auto-includes
  files from subdirectories
- **TwigServiceRegistration.cs** — using statement updates only
- **All consumer projects** — using statement updates only

### Sequencing Constraints

- No prerequisites — this refactor can be done at any time
- Should be done **before** adding more services to avoid re-work
- Should be done in a **quiet period** to minimize merge conflicts with in-flight PRs

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Merge conflicts with in-flight PRs | Medium | Medium | Coordinate timing; do during quiet period; communicate to team before starting |
| Missed consumer file | Low | High (build break) | Full solution build + test run validates all consumers updated |
| Git loses rename tracking | Low | Medium | Use `git mv` for moves; verify with `git diff --stat --diff-filter=R` |
| IDE/tooling cache issues | Low | Low | Clean rebuild after refactor; restart IDE/OmniSharp |

---

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should `DuplicateGuard` stay in `Seed/` or move to root given MCP tools also use it? | Low | Recommend Seed/ — primary consumer is seed publish pipeline |
| 2 | Is 14 files in `Workspace/` too many? Consider splitting `Tracking/` sub-subfolder? | Low | 14 files is manageable; splitting creates a 1-file subfolder |
| 3 | Should `LinkTypeMapper` move to a `Links/` subfolder or stay at root? | Low | Stay at root — creating a 1-file subfolder adds noise |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | No new source files — only directory creation and file moves |

### New Directories

| Directory Path | Purpose |
|----------------|---------|
| `src/Twig.Domain/Services/Sync/` | Sync/refresh services |
| `src/Twig.Domain/Services/Seed/` | Seed lifecycle services |
| `src/Twig.Domain/Services/Workspace/` | Working set and status services |
| `src/Twig.Domain/Services/Navigation/` | Active item and context services |
| `src/Twig.Domain/Services/Field/` | Field definition services |
| `src/Twig.Domain/Services/Process/` | State and process config services |
| `src/Twig.Domain/Services/Git/` | Git integration services |
| `src/Twig.Domain/Services/Rendering/` | Display utility services |
| `tests/Twig.Domain.Tests/Services/Sync/` | Mirror of source Sync/ |
| `tests/Twig.Domain.Tests/Services/Seed/` | Mirror of source Seed/ |
| `tests/Twig.Domain.Tests/Services/Workspace/` | Mirror of source Workspace/ |
| `tests/Twig.Domain.Tests/Services/Navigation/` | Mirror of source Navigation/ |
| `tests/Twig.Domain.Tests/Services/Field/` | Mirror of source Field/ |
| `tests/Twig.Domain.Tests/Services/Process/` | Mirror of source Process/ |
| `tests/Twig.Domain.Tests/Services/Git/` | Mirror of source Git/ |
| `tests/Twig.Domain.Tests/Services/Rendering/` | Mirror of source Rendering/ |

### Modified Files (Moved — namespace + location change)

**Source files (55 moved, 1 stays at root):**

| Old Path | New Path |
|----------|----------|
| `Services/SyncCoordinator.cs` | `Services/Sync/SyncCoordinator.cs` |
| `Services/SyncCoordinatorPair.cs` | `Services/Sync/SyncCoordinatorPair.cs` |
| `Services/SyncGuard.cs` | `Services/Sync/SyncGuard.cs` |
| `Services/SyncResult.cs` | `Services/Sync/SyncResult.cs` |
| `Services/ProtectedCacheWriter.cs` | `Services/Sync/ProtectedCacheWriter.cs` |
| `Services/RefreshOrchestrator.cs` | `Services/Sync/RefreshOrchestrator.cs` |
| `Services/FieldDefinitionSyncService.cs` | `Services/Sync/FieldDefinitionSyncService.cs` |
| `Services/ProcessTypeSyncService.cs` | `Services/Sync/ProcessTypeSyncService.cs` |
| `Services/ConflictResolver.cs` | `Services/Sync/ConflictResolver.cs` |
| `Services/WiqlQueryBuilder.cs` | `Services/Sync/WiqlQueryBuilder.cs` |
| `Services/SeedFactory.cs` | `Services/Seed/SeedFactory.cs` |
| `Services/SeedValidator.cs` | `Services/Seed/SeedValidator.cs` |
| `Services/SeedPublishOrchestrator.cs` | `Services/Seed/SeedPublishOrchestrator.cs` |
| `Services/SeedReconcileOrchestrator.cs` | `Services/Seed/SeedReconcileOrchestrator.cs` |
| `Services/SeedEditorFormat.cs` | `Services/Seed/SeedEditorFormat.cs` |
| `Services/SeedDependencyGraph.cs` | `Services/Seed/SeedDependencyGraph.cs` |
| `Services/SeedIdCounter.cs` | `Services/Seed/SeedIdCounter.cs` |
| `Services/SeedLinkPromoter.cs` | `Services/Seed/SeedLinkPromoter.cs` |
| `Services/SeedLinkTypeMapper.cs` | `Services/Seed/SeedLinkTypeMapper.cs` |
| `Services/DuplicateGuard.cs` | `Services/Seed/DuplicateGuard.cs` |
| `Services/WorkingSet.cs` | `Services/Workspace/WorkingSet.cs` |
| `Services/WorkingSetService.cs` | `Services/Workspace/WorkingSetService.cs` |
| `Services/WorkingLevelResolver.cs` | `Services/Workspace/WorkingLevelResolver.cs` |
| `Services/StatusOrchestrator.cs` | `Services/Workspace/StatusOrchestrator.cs` |
| `Services/StatusFieldsConfig.cs` | `Services/Workspace/StatusFieldsConfig.cs` |
| `Services/DirtyStateSummary.cs` | `Services/Workspace/DirtyStateSummary.cs` |
| `Services/SprintHierarchyBuilder.cs` | `Services/Workspace/SprintHierarchyBuilder.cs` |
| `Services/BacklogHierarchyService.cs` | `Services/Workspace/BacklogHierarchyService.cs` |
| `Services/BacklogOrderer.cs` | `Services/Workspace/BacklogOrderer.cs` |
| `Services/CeilingComputer.cs` | `Services/Workspace/CeilingComputer.cs` |
| `Services/ColumnResolver.cs` | `Services/Workspace/ColumnResolver.cs` |
| `Services/WorkItemExportFormat.cs` | `Services/Workspace/WorkItemExportFormat.cs` |
| `Services/WorkTreeFetcher.cs` | `Services/Workspace/WorkTreeFetcher.cs` |
| `Services/TrackingService.cs` | `Services/Workspace/TrackingService.cs` |
| `Services/ActiveItemResolver.cs` | `Services/Navigation/ActiveItemResolver.cs` |
| `Services/ActiveItemResult.cs` | `Services/Navigation/ActiveItemResult.cs` |
| `Services/ActiveItemResultExtensions.cs` | `Services/Navigation/ActiveItemResultExtensions.cs` |
| `Services/ContextChangeService.cs` | `Services/Navigation/ContextChangeService.cs` |
| `Services/PatternMatcher.cs` | `Services/Navigation/PatternMatcher.cs` |
| `Services/FieldProfileService.cs` | `Services/Field/FieldProfileService.cs` |
| `Services/FieldDefinitionHasher.cs` | `Services/Field/FieldDefinitionHasher.cs` |
| `Services/FieldImportFilter.cs` | `Services/Field/FieldImportFilter.cs` |
| `Services/StateTransitionService.cs` | `Services/Process/StateTransitionService.cs` |
| `Services/StateCategoryResolver.cs` | `Services/Process/StateCategoryResolver.cs` |
| `Services/DynamicProcessConfigProvider.cs` | `Services/Process/DynamicProcessConfigProvider.cs` |
| `Services/FlowTransitionService.cs` | `Services/Process/FlowTransitionService.cs` |
| `Services/ParentStatePropagationService.cs` | `Services/Process/ParentStatePropagationService.cs` |
| `Services/DescendantVerificationService.cs` | `Services/Process/DescendantVerificationService.cs` |
| `Services/BranchLinkService.cs` | `Services/Git/BranchLinkService.cs` |
| `Services/BranchNamingService.cs` | `Services/Git/BranchNamingService.cs` |
| `Services/CommitMessageService.cs` | `Services/Git/CommitMessageService.cs` |
| `Services/WorkItemIdExtractor.cs` | `Services/Git/WorkItemIdExtractor.cs` |
| `Services/CacheAgeFormatter.cs` | `Services/Rendering/CacheAgeFormatter.cs` |
| `Services/DeterministicTypeColor.cs` | `Services/Rendering/DeterministicTypeColor.cs` |
| `Services/TypeColorResolver.cs` | `Services/Rendering/TypeColorResolver.cs` |

**Consumer files (~160 files) — using statement updates only:**

All files listed in the Consumer Audit table above require `using` directive updates.
No source code changes beyond `using` lines.

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | Files are moved, not deleted — git tracks as renames |

---

## ADO Work Item Structure

### Epic #2118: Domain Critique: Service Layer Organization

---

### Issue 1: Design and validate subfolder taxonomy

**Goal**: Finalize the file-to-subfolder mapping, validate no circular dependencies,
and document the classification rationale.

**Prerequisites**: None

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1.1 | Catalog all 56 services with responsibility, dependencies, and proposed subfolder | This plan document | Small |
| T1.2 | Verify no circular subfolder-level dependency chains exist | Analysis only | Small |
| T1.3 | Review classification with stakeholders; resolve open questions | This plan document | Small |

**Acceptance Criteria**:
- [x] All 56 files have a designated subfolder assignment
- [x] Cross-subfolder dependency graph has no cycles
- [x] Classification rationale documented for non-obvious placements

**Audit Confirmation** (AB#2180):
All 56 `.cs` files in `src/Twig.Domain/Services/` have been individually verified against
the 8-subfolder taxonomy. The file count per subfolder matches the classification tables:
Sync/ (10), Seed/ (10), Workspace/ (14), Navigation/ (5), Field/ (3), Process/ (6),
Git/ (4), Rendering/ (3), Root (1). No orphans, no misclassifications. Ambiguous
placements (DuplicateGuard→Seed/, DescendantVerificationService→Process/,
TrackingService→Workspace/, WorkTreeFetcher→Workspace/, LinkTypeMapper→Root) have
documented rationale in the Design Decisions section above. The cross-subfolder
dependency graph remains cycle-free (verified in revision 3).

---

### Issue 2: Move source service files into subfolders

**Goal**: Move all 55 service files into their designated subfolders, update namespace
declarations, and resolve intra-Services cross-references.

**Prerequisites**: Issue 1 (taxonomy finalized)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T2.1 | Create 8 subdirectories under `src/Twig.Domain/Services/` | Directory creation only | Small |
| T2.2 | Move Sync/ files (10) via `git mv` and update namespace to `Twig.Domain.Services.Sync` | 10 source files | Small |
| T2.3 | Move Seed/ files (10) via `git mv` and update namespace to `Twig.Domain.Services.Seed` | 10 source files | Small |
| T2.4 | Move Workspace/ files (14) via `git mv` and update namespace to `Twig.Domain.Services.Workspace` | 14 source files | Medium |
| T2.5 | Move Navigation/ (5), Field/ (3), Process/ (6), Git/ (4), Rendering/ (3) files and update namespaces | 21 source files | Medium |
| T2.6 | Add cross-subfolder `using` directives within Services/ (e.g., `ContextChangeService` → `using Twig.Domain.Services.Sync`) | ~10 source files | Small |

**Acceptance Criteria**:
- [ ] All 55 files moved to correct subfolders
- [ ] Each file's `namespace` declaration matches its new folder path
- [ ] Intra-Services cross-references compile

---

### Issue 3: Update all consumer references

**Goal**: Update `using` directives in all projects that reference `Twig.Domain.Services`
so the solution compiles cleanly.

**Prerequisites**: Issue 2 (files moved)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T3.1 | Update CLI command files (~39 files in `src/Twig/Commands/`) | 39 source files | Medium |
| T3.2 | Update CLI DI, rendering, hints, and formatter files (~8 files) | 8 source files | Small |
| T3.3 | Update Infrastructure files (`TwigServiceRegistration.cs`, `PromptStateWriter.cs`, `AdoResponseMapper.cs`) | 3 source files | Small |
| T3.4 | Update MCP tools and services (~7 files in `src/Twig.Mcp/`) | 7 source files | Small |
| T3.5 | Update Twig.Domain internal consumers (`Extensions/`, `ReadModels/`, `Interfaces/`) | 3 source files | Small |

**Acceptance Criteria**:
- [ ] All source projects compile with zero errors
- [ ] No remaining `using Twig.Domain.Services;` directives that should be sub-namespaced
- [ ] `LinkTypeMapper` consumers retain `using Twig.Domain.Services;`

---

### Issue 4: Mirror structure in test projects

**Goal**: Move test files into matching subfolders and update test namespace declarations
and using directives.

**Prerequisites**: Issue 3 (consumers updated — needed for compilation)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T4.1 | Create 8 subdirectories under `tests/Twig.Domain.Tests/Services/` | Directory creation only | Small |
| T4.2 | Move domain test files (53 files) via `git mv` and update `namespace` to match subfolder | 53 test files | Medium |
| T4.3 | Update `using` directives in moved test files | 53 test files | Medium |
| T4.4 | Update `using` directives in CLI test files (~40 files) | ~40 test files | Medium |
| T4.5 | Update `using` directives in MCP test files and TestKit (~7 files) | ~7 test files | Small |

**Acceptance Criteria**:
- [ ] Test project folder structure mirrors source project structure
- [ ] All test namespaces match their folder paths
- [ ] All tests compile and pass

---

### Issue 5: Final validation and commit hygiene

**Goal**: Ensure the full solution builds, all tests pass, AOT compilation works,
and git history tracks file renames cleanly.

**Prerequisites**: Issues 2, 3, 4 (all moves and updates complete)

**Tasks**:

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T5.1 | Full solution build (`dotnet build Twig.slnx`) and fix any remaining compilation errors | All | Small |
| T5.2 | Run full test suite (`dotnet test Twig.slnx`) and verify zero failures | All | Small |
| T5.3 | Verify AOT publish (`dotnet publish -c Release` for CLI and MCP projects) | Build output | Small |
| T5.4 | Verify git rename tracking (`git diff --stat --diff-filter=R`) and squash into single commit | Git | Small |

**Acceptance Criteria**:
- [ ] `dotnet build Twig.slnx` succeeds with zero errors and zero warnings
- [ ] `dotnet test Twig.slnx` passes all tests
- [ ] AOT publish succeeds for CLI and MCP projects
- [ ] `git log --diff-filter=R` shows file renames, not delete+add

---

## PR Groups

### PG-1: Full service layer namespace refactor

| Field | Value |
|-------|-------|
| **Classification** | Wide — many files, mechanical changes |
| **Issues Covered** | Issue 2, Issue 3, Issue 4, Issue 5 |
| **Estimated LoC** | ~500 (namespace + using statement changes only) |
| **Files Touched** | ~220 |
| **Successors** | None |

**Note on file count**: This PG touches ~220 files, exceeding the typical ≤50-file
guideline. This is acceptable because:
1. Every change is a **mechanical namespace/using update** — no logic changes
2. The epic explicitly requires a **single PR** for clean git rename tracking
3. Splitting into multiple PRs would create intermediate build-broken states
4. Reviewers can verify correctness by checking: (a) all files moved to correct
   subfolders, (b) namespaces match folder paths, (c) build passes, (d) tests pass

**Review strategy**: Focus on the file classification (are services in the right
subfolders?) rather than individual using-statement correctness (validated by compiler).

---

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | full-namespace-refactor | Issues 2–5 (T2.1–T2.6, T3.1–T3.5, T4.1–T4.5, T5.1–T5.4) | None (Issue 1 is design/planning, complete via this plan) | Wide |

### Execution Order

**PG-1** is the sole PR. Issue 1 (design and taxonomy validation) is fulfilled by this plan
document and requires no code changes. All implementation work — file moves, namespace updates,
consumer updates, test mirroring, and final validation — ships together in a single PR.

This single-PR strategy is required because:
1. Any intermediate split would leave the solution in a broken build state
2. Git rename tracking works best when moves and using-update fixups are in one commit
3. All ~220 file changes are purely mechanical (namespace declarations and `using` lines)

### Validation Strategy per PG

**PG-1 — full-namespace-refactor**

| Step | Command | Pass Criteria |
|------|---------|---------------|
| Build | `dotnet build Twig.slnx` | Zero errors, zero warnings |
| Test | `dotnet test Twig.slnx` | All tests pass, no regressions |
| AOT | `dotnet publish -c Release` (CLI + MCP) | Publish succeeds |
| Rename tracking | `git diff --stat --diff-filter=R` | ~55 source renames + ~53 test renames visible |
| Residual using check | `grep -r "using Twig.Domain.Services;" src/ tests/` | Only `LinkTypeMapper` consumers remain |

---

## References

- [Domain Model Critique — Item 5](../architecture/domain-model-critique.md#5-service-layer-organization-56-flat-files)
- Epic #2118 in ADO
