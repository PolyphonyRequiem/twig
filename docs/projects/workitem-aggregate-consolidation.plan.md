# WorkItem Aggregate Consolidation

**Epic:** #2114 — Domain Critique: WorkItem Aggregate Consolidation
**Status:** 📋 Planning
**Revision:** 0
**Revision Notes:** Initial draft.

---

## Executive Summary

The `WorkItem` class in `Twig.Domain.Aggregates` carries too many responsibilities: it simultaneously serves as entity, field bag, seed factory, and copy factory. The three `With*` copy methods (`WithSeedFields`, `WithParentId`, `WithIsSeed`) each manually reconstruct the full object and subtly differ in which properties they preserve — a guaranteed bug factory as properties are added. Additionally, `_seedIdCounter` is static mutable state on a domain entity, coupling all instances and making parallel tests nondeterministic. This plan introduces a `WorkItemCopier` helper to centralize copy logic with a single property-list, extracts seed creation from `WorkItem` into the existing `SeedFactory` service (converted from static to a DI-registered singleton), and adds a reflection-based property-preservation theory test as a permanent safety net.

---

## Background

### Current Architecture

`WorkItem` is a `sealed class` with init-only properties for identity (`Id`, `Type`, `Title`, `State`, `AssignedTo`, `IterationPath`, `AreaPath`, `ParentId`), seed metadata (`IsSeed`, `SeedCreatedAt`), cache staleness (`LastSyncedAt`), and internal state (`IsDirty`, `Revision`, `_fields`, `_pendingNotes`). Mutations flow through `ChangeState()`, `UpdateField()`, and `AddNote()` — direct methods that return `FieldChange` values and set `IsDirty`. The command queue pattern was recently simplified (Epic #2115) to these direct methods.

Three copy methods exist on `WorkItem`:

| Method | Overrides | Copies Fields | Preserves IsDirty | Preserves PendingNotes |
|--------|-----------|---------------|-------------------|----------------------|
| `WithSeedFields(title, fields)` | Title, Fields (replaces) | No — uses provided fields | ❌ No | ❌ No |
| `WithParentId(newParentId)` | ParentId | ✅ Yes (source.Fields) | ✅ Yes | ❌ No |
| `WithIsSeed(isSeed)` | IsSeed | ✅ Yes (source.Fields) | ❌ No (by design) | ❌ No |

Each method manually lists all 11 init-only properties in its `new WorkItem { ... }` initializer. When a new property is added, each method must be independently updated — but the compiler provides no warning if one is missed (init properties default to `default`).

Seed creation lives as `WorkItem.CreateSeed()` (static factory) with `_seedIdCounter` (static `int` with `Interlocked` access) and `InitializeSeedCounter()` (static). The existing `SeedFactory` service (static class in `Twig.Domain.Services`) already wraps `CreateSeed` with parent/child type validation, but delegates the actual construction to `WorkItem.CreateSeed()`.

### Call-Site Audit

#### `WithSeedFields` — 3 production + 5 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/NewCommand.cs:114` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `src/Twig/Commands/SeedEditCommand.cs:74` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `src/Twig/Commands/SeedNewCommand.cs:99` | `ExecuteAsync` | Apply editor-parsed fields to seed | Delegation — no API change |
| `tests/.../WorkItemTests.cs` | 5 test methods | Copy behavior validation | Unchanged — tests exercise public API |

#### `WithParentId` — 0 production + 10 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `tests/.../WorkItemCopyTests.cs` | 6 test methods | Copy behavior validation | Unchanged |
| `tests/.../SetCommandTests.cs` | 4 test setups | Creating child items for testing | Unchanged |

#### `WithIsSeed` — 2 production + 5 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:130` | `PublishSeedAsync` | Mark fetched-back item as seed | Delegation — no API change |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs:174` | Post-publish refresh | Mark refreshed item as seed | Delegation — no API change |
| `tests/.../WorkItemCopyTests.cs` | 5 test methods | Copy behavior validation | Unchanged |

#### `WorkItem.CreateSeed` — 2 production + ~40 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig.Domain/Services/SeedFactory.cs:67` | `Create` | Construct seed from parent context | Method moves into SeedFactory |
| `src/Twig.Domain/Services/SeedFactory.cs:93` | `CreateUnparented` | Construct seed with explicit paths | Method moves into SeedFactory |
| Tests (~40 sites across 10 files) | Various | Create seeds for test setup | Migrate to `TestSeedFactory` helper |

#### `WorkItem.InitializeSeedCounter` — 2 production + ~10 test call sites

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/SeedChainCommand.cs:69` | `ExecuteAsync` | Init counter from DB before batch | Call `seedFactory.InitializeSeedCounter()` |
| `src/Twig/Commands/SeedNewCommand.cs:67` | `ExecuteAsync` | Init counter from DB before create | Call `seedFactory.InitializeSeedCounter()` |
| Tests (~10 sites across 5 files) | Various | Init counter for test isolation | Migrate to `TestSeedFactory` helper |

---

## Problem Statement

1. **Copy method divergence**: Three `With*` methods each manually enumerate all 11 init-only properties in an object initializer. They subtly differ in state preservation: `WithSeedFields` doesn't preserve `IsDirty`; `WithParentId` does; `WithIsSeed` doesn't. These differences are intentional but undiscoverable — the only way to verify correctness is to read all three methods line-by-line. When a new property is added to `WorkItem`, the compiler provides no warning if one copy method omits it, because init properties silently default.

2. **Static mutable state in domain entity**: `_seedIdCounter` is a `static int` with `Interlocked` access inside `WorkItem`. This couples all `WorkItem` instances process-wide, makes parallel tests nondeterministic (tests that create seeds share the counter), and violates the principle that domain entities should not carry infrastructure concerns like ID generation.

3. **Misplaced responsibility**: `CreateSeed()` and `InitializeSeedCounter()` are static factory methods on `WorkItem` that deal with seed lifecycle — a concern that already has a dedicated `SeedFactory` service. Having the factory logic split between two classes creates confusion about where seed creation logic lives.

---

## Goals and Non-Goals

### Goals

1. **Single property list**: All `With*` copy logic goes through one central method that enumerates `WorkItem` properties exactly once.
2. **Compile-time or test-time safety**: A reflection-based theory test catches any `WorkItem` property not handled by the copier.
3. **No static mutable state in domain entity**: `_seedIdCounter` moves to `SeedFactory`, which becomes a DI-registered singleton.
4. **Preserved semantics**: All existing behavior — which properties each `With*` method preserves or overrides — remains identical. Existing tests pass without modification.

### Non-Goals

- **Field storage refactoring**: No changes to `_fields`, `ImportFields`, `SetField`, or `TryGetField`.
- **Identity pattern changes**: `Id` remains `int`, init-only. No ID generation strategy changes.
- **Domain invariant enforcement**: Adding state validation to `ChangeState()` is out of scope (future epic).
- **`WorkItemBuilder` (TestKit) consolidation**: The test builder is separate from the copier and unchanged.
- **PendingNotes copying**: None of the three methods currently copy pending notes. This is intentional and won't change.

---

## Requirements

### Functional

1. `WorkItemCopier` must produce identical output to the current `With*` methods for all property combinations.
2. `SeedFactory.CreateSeed()` must produce identical output to the current `WorkItem.CreateSeed()`.
3. `SeedFactory.InitializeSeedCounter()` must maintain the same thread-safety guarantees via `Interlocked`.
4. All existing tests must pass without behavioral changes.

### Non-Functional

1. AOT-compatible — no reflection at runtime (test-only reflection is fine).
2. No new external dependencies.
3. No changes to public API surface beyond deprecating `WorkItem.CreateSeed` and `WorkItem.InitializeSeedCounter`.
