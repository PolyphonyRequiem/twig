# Plan: `twig new` — Create Unparented Top-Level Work Items

> **Date**: 2025-05-24  
> **Status**: Draft  
> **ADO Issue**: #1264  

---

## Executive Summary

Add a `twig new` command that creates unparented, top-level work items directly in Azure DevOps without requiring a parent context. Today, creating root-level items (e.g., a new Epic) requires manual workarounds — clearing the active context, creating a seed, patching area/iteration paths by hand, then publishing. This plan introduces a single command that resolves project-default area/iteration paths, creates a local seed, and immediately publishes it to ADO in one step.

---

## Background

### Current State

- `twig seed new --title "X" --type Epic` creates a **local** seed under the active parent. When no parent exists, `SeedFactory.Create` requires an explicit `--type` but produces a seed with **empty** `AreaPath` and `IterationPath` (both `default(AreaPath)`/`default(IterationPath)`).
- `AdoRestClient.CreateAsync` → `MapSeedToCreatePayload` omits area/iteration when their `.Value` is null/empty. ADO then defaults them to the project root, but the local SQLite row retains empty values — causing inconsistency after publish + refresh.
- Creating an Epic earlier this session required: clearing the context table manually in SQLite, running `seed new`, manually patching `area_path` and `iteration_path` in the DB, and then `seed publish`. This is fragile and error-prone.

### What This Solves

Users need a single command to create root-level work items (Epics, Issues) without touching the database directly or clearing context. The command should:
1. Not interfere with the current active context
2. Default area/iteration to project root (from config)
3. Publish immediately to ADO (no seed → publish two-step)
4. Optionally set the newly created item as the active context

---

## Problem Statement

There is no CLI path to create unparented, top-level work items. The `seed new` command is designed around parent-child relationships and the seed lifecycle (local draft → validate → publish). Root-level items like Epics don't fit this model — they have no parent and are typically created with immediate intent to use them, not as drafts.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | `twig new --title "X" --type Epic` creates a work item in ADO and saves it locally in one step |
| G2 | Area/iteration default to `config.Project` when not explicitly provided |
| G3 | Support `--area` and `--iteration` overrides for explicit path control |
| G4 | Support `--description` for inline description text |
| G5 | Output the created work item ID and title on success |
| G6 | Optionally set the new item as active context via `--set` flag |
| G7 | Support `--editor` flag to open field editor before publishing |
| G8 | Respect existing output format conventions (`--output json/human/minimal`) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Supporting parent-child creation (use `seed new` for that) |
| NG2 | Batch creation of multiple items |
| NG3 | Bi-directional sync or pull-from-ADO |
| NG4 | Replacing `seed new` — the seed workflow remains for draft-oriented child creation |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig new` is a top-level command (not under `seed`) |
| FR-02 | `--title` (required) specifies the work item title |
| FR-03 | `--type` (required) specifies the work item type (e.g., Epic, Issue, Task) |
| FR-04 | `--area` (optional) overrides the area path; defaults to `config.Defaults.AreaPath` → `config.Project` |
| FR-05 | `--iteration` (optional) overrides the iteration path; defaults to `config.Defaults.IterationPath` → `config.Project` |
| FR-06 | `--description` (optional) sets `System.Description` on the created item |
| FR-07 | `--set` (optional, default false) sets the new item as active context after creation |
| FR-08 | `--editor` (optional) opens the seed editor for field editing before publishing |
| FR-09 | The command creates a seed locally, publishes it via `SeedPublishOrchestrator`, and returns the ADO-assigned ID |
| FR-10 | On successful publish, the local seed row is replaced by the fetched ADO item (existing publish behavior) |
| FR-11 | Title and type are validated before any ADO call |
| FR-12 | Area/iteration are validated as well-formed paths before creation |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Native AOT compatible — no reflection |
| NFR-02 | Follows existing command pattern (sealed class, primary constructor, DI) |
| NFR-03 | Errors are reported through the output formatter |
| NFR-04 | Hint engine provides post-creation hints |

---

## Proposed Design

### Architecture Overview

```
CLI Layer                        Domain Layer                   Infrastructure
┌──────────┐                    ┌──────────────────┐           ┌──────────────┐
│ Program   │ ─── New() ──────► │ NewCommand       │           │              │
│ .cs route │                   │  (sealed class)  │           │ AdoRestClient│
└──────────┘                    │                  │           │              │
                                │ 1. Resolve       │           │ CreateAsync() │
                                │    area/iter     │           │ FetchAsync()  │
                                │ 2. SeedFactory   │           └──────────────┘
                                │    .Create()     │                  ▲
                                │ 3. Publish via   │                  │
                                │    orchestrator  │──────────────────┘
                                │ 4. Optionally    │
                                │    set context   │
                                └──────────────────┘
```

### Path Resolution Strategy

The area and iteration paths follow this priority chain:

1. **Explicit CLI flag**: `--area "Twig\SubArea"` or `--iteration "Twig\Sprint 2"`
2. **Config defaults**: `config.Defaults.AreaPath` / `config.Defaults.IterationPath`
3. **Project root**: `config.Project` (e.g., `"Twig"`)

This ensures the command works out of the box for any configured workspace without requiring explicit flags.

### Command Flow

```
twig new --title "My Epic" --type Epic [--area X] [--iteration Y] [--description "..."] [--set] [--editor]
```

1. **Parse & validate** title, type, area, iteration
2. **Resolve defaults**: apply path resolution chain for area/iteration
3. **Initialize seed counter** from existing min seed ID (prevent ID collisions)
4. **Create seed**: `WorkItem.CreateSeed(type, title, parentId: null, areaPath, iterationPath)`
5. **Apply description**: if `--description`, set `System.Description` in seed fields
6. **Editor flow** (if `--editor`): generate buffer, launch editor, parse fields, apply to seed
7. **Save seed locally** (required for publish orchestrator to find it)
8. **Publish**: `SeedPublishOrchestrator.PublishAsync(seed.Id)` → creates in ADO, fetches back, replaces local
9. **Set context** (if `--set`): `contextStore.SetActiveWorkItemIdAsync(newId)`
10. **Output**: display created ID + title via formatter
11. **Hints**: emit relevant post-creation hints

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Reuse `SeedPublishOrchestrator` for ADO creation | Leverages existing create → fetch-back → replace pipeline including link promotion and ID remapping |
| DD-02 | Top-level `twig new` not `twig seed new --no-parent` | This is a fundamentally different workflow (create-and-publish) vs seed (local draft). Distinct command avoids flag-overload |
| DD-03 | `--type` is required | No parent means no type inference. Explicit type is clear and prevents errors |
| DD-04 | Area/iteration default chain: flag → config.Defaults → config.Project | Covers explicit override, per-workspace defaults, and zero-config fallback |
| DD-05 | Seed is created and immediately published | The local seed is a transient vehicle — never shown to the user as a draft |
| DD-06 | On publish failure, the local seed is cleaned up (discarded) | Avoids orphaned negative-ID seeds that the user didn't intend to draft |

---

## Implementation Plan

### Epic 1: Core `twig new` Command

| Task | Description | Files |
|------|-------------|-------|
| T1 | Create `NewCommand.cs` in `src/Twig/Commands/` with constructor DI for `SeedPublishOrchestrator`, `IWorkItemRepository`, `IProcessConfigurationProvider`, `IContextStore`, `TwigConfiguration`, `OutputFormatterFactory`, `HintEngine`, `IFieldDefinitionStore`, `IEditorLauncher` | `src/Twig/Commands/NewCommand.cs` |
| T2 | Implement `ExecuteAsync` with the 11-step flow described above | `src/Twig/Commands/NewCommand.cs` |
| T3 | Add `New(...)` method to `TwigCommands` in `Program.cs` — route to `NewCommand` | `src/Twig/Program.cs` |
| T4 | Register `NewCommand` in `CommandRegistrationModule.AddCoreCommands` | `src/Twig/DependencyInjection/CommandRegistrationModule.cs` |
| T5 | Add path resolution helper `ResolveAreaPath` / `ResolveIterationPath` in `NewCommand` that implements the flag → defaults → project chain | `src/Twig/Commands/NewCommand.cs` |
| T6 | Handle `--description` by calling `seed.WithSeedFields(title, fields)` where fields includes `System.Description` | `src/Twig/Commands/NewCommand.cs` |
| T7 | Handle `--editor` flow: reuse `SeedEditorFormat.Generate/Parse` and `IEditorLauncher` pattern from `SeedNewCommand` | `src/Twig/Commands/NewCommand.cs` |
| T8 | On publish failure, clean up the transient seed via `workItemRepo.DeleteByIdAsync(seed.Id)` | `src/Twig/Commands/NewCommand.cs` |

### Epic 2: SeedFactory Enhancement

| Task | Description | Files |
|------|-------------|-------|
| T9 | Add `SeedFactory.CreateUnparented(title, type, areaPath, iterationPath)` static method — creates a seed with no parent and explicit area/iteration paths | `src/Twig.Domain/Services/SeedFactory.cs` |

**Rationale**: While `SeedFactory.Create(title, parent: null, config, typeOverride)` exists, it requires a `ProcessConfiguration` and returns `default` area/iteration when parent is null. A purpose-built method avoids passing a process config that's irrelevant for unparented items and accepts already-resolved paths directly.

### Epic 3: Tests

| Task | Description | Files |
|------|-------------|-------|
| T10 | Unit tests for `NewCommand.ExecuteAsync` — happy path (creates + publishes), missing title, missing type, custom area/iteration, `--set` flag, `--description`, `--editor` flow, publish failure cleanup | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` |
| T11 | Unit tests for `SeedFactory.CreateUnparented` — validates area/iteration are set, type is assigned, parentId is null | `tests/Twig.Domain.Tests/Services/SeedFactoryTests.cs` |
| T12 | Unit tests for path resolution — flag overrides config, config overrides project root, empty config uses project | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` |

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Publish orchestrator assumes seed has parent-related links | Low | Medium | `twig new` seeds have `parentId: null` — orchestrator skips parent link check when parentId is null (existing behavior) |
| Area/iteration path validation rejects project root (single segment) | Low | High | `AreaPath.Parse("Twig")` is valid — single segment paths are accepted by the value object |
| Editor flow adds unexpected complexity | Low | Low | Reuse exact pattern from `SeedNewCommand` — already proven |
| Race condition if user runs `twig new` concurrently | Very Low | Low | Seed counter is thread-safe via `Interlocked.Decrement`; SQLite WAL handles concurrent writes |

---

## Success Criteria

1. `twig new --title "Test Epic" --type Epic` creates an Epic in ADO and prints the new ID
2. The created item has `area_path = "Twig"` and `iteration_path = "Twig"` (from config.Project)
3. `twig new --title "X" --type Issue --set` creates the issue and sets it as active context
4. `twig new --title "X" --type Epic --description "Details"` creates with description populated
5. `twig new` with no `--title` prints usage error and exits 2
6. `twig new --title "X"` with no `--type` prints error requiring explicit type
7. All existing tests pass (no regressions)
8. New tests cover happy path, error cases, and path resolution chain
