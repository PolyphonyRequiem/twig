# Opportunistic DU Preview Adoption in Twig

> **Issue:** #2587 — Opportunistic DU preview adoption in Twig
> **Status**: 🔨 In Progress
> **Revision:** 3

---

## Executive Summary

This plan adopts C# discriminated unions (DUs) — via the `union` keyword — in the Twig CLI
as a preview feature. **Critical finding from research:** DUs are a C# 15 / .NET 11 feature,
not .NET 10 as originally stated in the work item. The `union` keyword requires the .NET 11
Preview SDK, `TargetFramework=net11.0`, and `LangVersion=preview`, plus a compiler polyfill
(`UnionAttribute` + `IUnion`). The codebase already uses sealed abstract record hierarchies
as manual DUs in 9 types — these are the natural first conversion candidates, not enums like
`StateCategory`. Per stakeholder feedback, the first deliverable is a **research spike**:
validate .NET 11 Preview SDK feasibility, test DU + AOT compatibility, and produce a
candidate inventory with migration playbook. Only if the research proves viability does a
proof-of-concept conversion of `MergeResult` proceed. If the .NET 11 Preview SDK is
unavailable, the deliverable reduces to the candidate inventory and playbook documentation.

## Background

### Current State

Twig targets .NET 10 (`net10.0`) with `LangVersion=latest` (C# 14) and `PublishAot=true`.
The SDK is pinned at `10.0.104` via `global.json`. The codebase has two distinct patterns
for representing domain outcomes:

**1. Sealed abstract record hierarchies (manual DUs) — 9 types:**

| Type | Variants | Location | Serialized? |
|------|----------|----------|-------------|
| `MergeResult` | 3 (`NoConflict`, `AutoMergeable`, `HasConflicts`) | `Services/Sync/ConflictResolver.cs` | No |
| `SyncResult` | 5 (`UpToDate`, `Updated`, `Failed`, `Skipped`, `PartiallyUpdated`) | `Services/Sync/SyncResult.cs` | No |
| `ActiveItemResult` | 4 (`Found`, `NoContext`, `FetchedFromAdo`, `Unreachable`) | `Services/Navigation/ActiveItemResult.cs` | No |
| `StatusResult` | 3 (`NoContext`, `Unreachable`, `Success`) | `Services/Workspace/StatusResult.cs` | No |
| `BranchLinkResult` | 4 (`Linked`, `AlreadyLinked`, `GitContextUnavailable`, `Failed`) | `ValueObjects/BranchLinkResult.cs` | No |
| `MatchResult` | 3 (`SingleMatch`, `MultipleMatches`, `NoMatch`) | `Services/Navigation/PatternMatcher.cs` | No |
| `WorkspaceDataChunk` | 5 (`ContextLoaded`, `SprintItemsLoaded`, `SeedsLoaded`, `RefreshStarted`, `RefreshCompleted`) | `Rendering/IAsyncRenderer.cs` | No |
| `BatchNode` | 3 (`StepNode`, `SequenceNode`, `ParallelNode`) | `Mcp/Services/Batch/BatchModels.cs` | No |
| `TemplateSegment` | 2 (`LiteralSegment`, `ExpressionSegment`) | `Mcp/Services/Batch/TemplateModels.cs` | No |

These achieve DU semantics via `abstract record` + `sealed record` subtypes with private
constructors. They require ~5 lines of boilerplate per type and don't get compiler-enforced
exhaustiveness — switches still need `_ =>` catch-alls.

**2. Simple enums with switch statements:**

| Enum | Values | Usage Sites | Serialized? |
|------|--------|-------------|-------------|
| `StateCategory` | 6 (`Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`, `Unknown`) | 22+ | Yes (JSON + SQLite) |
| `TransitionKind` | 3 (`None`, `Forward`, `Cut`) | 3 | No |
| `SeedPublishStatus` | 5 (`Created`, `Skipped`, `DryRun`, `ValidationFailed`, `Error`) | ~8 | No |
| `ConflictOutcome` | 4 (`Proceed`, `AcceptedRemote`, `Aborted`, `ConflictJsonEmitted`) | ~4 | No |
| `NavigatorAction` | 11 values | ~4 | No |
| `ParentPropagationOutcome` | 5 values | ~5 | No |

### Critical Discovery: DUs Require .NET 11

Research via the [Oksala blog post](https://oksala.net/2026/04/18/a-first-look-at-c-unions-in-net-11/)
and the [C# language design discussion](https://github.com/dotnet/csharplang/discussions/9663) reveals:

- The `union` keyword is a **C# 15 / .NET 11** feature, not C# 14 / .NET 10
- Requires **three things simultaneously**:
  1. .NET 11 Preview SDK installed
  2. `<TargetFramework>net11.0</TargetFramework>`
  3. `<LangVersion>preview</LangVersion>`
- A **polyfill** is needed in the current preview (types not fully wired up yet):
  ```csharp
  namespace System.Runtime.CompilerServices
  {
      [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
      public sealed class UnionAttribute : Attribute;
      public interface IUnion { object? Value { get; } }
  }
  ```
- Top-level statements may not work with unions (irrelevant — Twig uses ConsoleAppFramework)
- **Boxing**: value-type union cases may be boxed through an `object?` access path. Acceptable
  for domain result types (reference types), but noteworthy for future struct candidates.
- The `union` can contain **classes, records, structs, or interfaces** as member types

### DU Syntax (from research)

```csharp
// Definition — lists the types the union can contain
public union MergeResult(NoConflict, AutoMergeable, HasConflicts)
{
    // Optional: union can contain member variables and methods
}

// Member types are standalone (not nested)
public sealed record NoConflict;
public sealed record AutoMergeable(IReadOnlyList<string> MergedFields);
public sealed record HasConflicts(IReadOnlyList<FieldConflict> ConflictingFields);

// Usage — pattern matching (exhaustive, compiler-enforced)
switch (result)
{
    case NoConflict:
        // ...
        break;
    case AutoMergeable am:
        Console.WriteLine($"Merged: {am.MergedFields.Count}");
        break;
    case HasConflicts hc:
        // handle conflicts
        break;
    // No _ => needed! Compiler enforces all cases covered.
}
```

### Why Abstract Record Types Are Better First Candidates Than Enums

The work item suggests `StateCategory` (an enum) as the first candidate. Research shows
this is suboptimal:

| Factor | Abstract Record DU (e.g., MergeResult) | Enum (e.g., StateCategory) |
|--------|---------------------------------------|---------------------------|
| Structural fit | Already models a closed set of alternatives with data | Simple discriminator, no per-case data |
| Serialization | Not JSON-serialized | `[JsonStringEnumConverter]`, registered in `TwigJsonContext`, stored in SQLite |
| Ordinal semantics | None | Has ordinal values (`Proposed = 0`, etc.) used for ranking |
| Call-site count | 3-4 files | 22+ sites across 11 files |
| Conversion risk | Low — subtypes already exist | High — fundamental type change with serialization implications |
| Consumer syntax | Pattern matching stays identical | Equality checks (`==`) need pattern matching conversion |

`MergeResult` is the ideal first candidate because:
1. It's already a manual DU — the case types exist, pattern matching is in use
2. It has exactly 3 cases — small enough to verify quickly
3. It's **not serialized** — no JSON/SQLite compatibility concerns
4. It has only 4 call sites in 3 files — contained blast radius
5. Converting it exercises the core value proposition (exhaustiveness on data-carrying variants)

### MergeResult Call-Site Audit

| File | Method | Current Usage | Impact |
|------|--------|--------------|--------|
| `Domain/Services/Sync/ConflictResolver.cs` | `Resolve()` | Returns `new MergeResult.NoConflict()`, `new MergeResult.HasConflicts(...)`, `new MergeResult.AutoMergeable(...)` | Construction syntax: `new NoConflict()` etc. |
| `Commands/ConflictResolutionFlow.cs` | `ResolveAsync()` | `is not MergeResult.HasConflicts conflicts` | Pattern match: `is not HasConflicts conflicts` |
| `Commands/BatchCommand.cs` | Merge handling | Pattern matches on MergeResult subtypes | Pattern match updates |
| `Tests/Domain.Tests/Services/Sync/ConflictResolverTests.cs` | Multiple tests | `is MergeResult.NoConflict`, `is MergeResult.HasConflicts` etc. | Test pattern match updates |

### Prior Art

- **Result Type Convention plan** (`result-type-convention.plan.md`): Established
  discriminated unions via `abstract record` as the standard pattern for domain result types.
  This DU adoption plan extends that convention with native language support.
- **Existing manual DU pattern**: 9 types already follow the sealed-abstract-record idiom,
  proving the conceptual pattern is well-established in the codebase.

## Problem Statement

The codebase has 9 types that model closed sets of alternatives via the manual
`abstract record` + `sealed record` pattern. While this provides type safety at the
domain modeling level, it lacks compiler-enforced exhaustiveness — every switch expression
over these types requires a `_ =>` discard arm, and the compiler cannot warn when a new
variant is added. This has practical consequences: adding a 6th variant to `SyncResult`
(e.g., `Throttled`) would compile successfully with no warnings at any of its ~10 call
sites, silently falling through to `_ =>` arms.

Additionally, each manual DU type requires ~5 lines of boilerplate (abstract base + private
constructor + sealed subtypes). The native `union` keyword collapses this to a single
declaration line while adding genuine exhaustiveness guarantees.

The primary blocker is that the `union` keyword is a .NET 11 feature, and Twig targets
.NET 10. This plan addresses both the framework upgrade and the first DU adoption as a
unified spike to validate feasibility.

## Goals and Non-Goals

### Goals

1. **Upgrade to .NET 11 Preview SDK** — update `global.json`, `TargetFramework`, and verify
   the full build, test, and AOT publish pipeline succeeds on the new framework.
2. **Enable `LangVersion=preview`** in `Directory.Build.props` for C# 15 preview features.
3. **Add DU polyfill** — provide `UnionAttribute` and `IUnion` in
   `System.Runtime.CompilerServices` until the types are fully wired up in the runtime.
4. **Produce a research deliverable** documenting: SDK feasibility results, DU AOT
   compatibility findings, candidate inventory with fitness rankings, and a migration
   playbook with before/after patterns and adoption criteria. This is the primary output.
5. **Prove the DU pattern** by converting `MergeResult` from the manual abstract-record
   pattern to a native `union` declaration, with exhaustive matching at all call sites
   (conditional on SDK feasibility).
6. **Verify AOT compatibility** — `dotnet publish` with `PublishAot=true` and
   `TrimMode=full` must succeed for both `Twig` and `Twig.Mcp` projects.
7. **Document adoption guidelines** — establish criteria for when and how to convert
   future types to DU syntax, including the candidate backlog.

### Non-Goals

- Converting all 9 existing sealed record DU types in this issue (future opportunistic work).
- Converting any enums (`StateCategory`, `TransitionKind`, etc.) to DUs in this issue.
- Changing the `Result<T>` convention (remains a readonly record struct for simple success/fail).
- Introducing third-party DU libraries (OneOf, etc.).
- Changing the process-agnostic design principle.
- Production deployment on .NET 11 Preview — this is an exploratory spike on a branch.

## Requirements

### Functional

- **FR-1**: .NET 11 Preview SDK builds cleanly with `TreatWarningsAsErrors=true` for all
  6 projects (after adding any necessary preview warning suppressions).
- **FR-2**: `MergeResult` uses native `union` keyword with 3 member types:
  `NoConflict`, `AutoMergeable`, `HasConflicts`.
- **FR-3**: All existing switch/match sites on `MergeResult` compile without `_ =>`
  catch-alls and the compiler enforces exhaustiveness.
- **FR-4**: Consumer pattern-matching syntax at call sites remains natural
  (e.g., `case HasConflicts hc:` or `is not HasConflicts`).

### Non-Functional

- **NFR-1**: AOT publish succeeds for `Twig` and `Twig.Mcp` projects on `net11.0`.
- **NFR-2**: All existing tests pass on .NET 11 (beyond type reference updates for MergeResult).
- **NFR-3**: Build time delta ≤ 15% (framework upgrade + preview features may affect compilation).
- **NFR-4**: Binary size delta documented (no strict cap — framework upgrade may shift baseline).

## Proposed Design

### Architecture Overview

The change has two layers: (1) a framework upgrade from .NET 10 to .NET 11 Preview, and
(2) a single DU type conversion to validate the feature.

```
global.json                    ← SDK version → 11.0.100-preview.X
Directory.Build.props          ← TargetFramework=net11.0, LangVersion=preview
    │
    ▼
CompilerPolyfill.cs            ← UnionAttribute + IUnion (new file)
    │
    ▼
MergeResult.cs                 ← abstract record hierarchy → union declaration
    │
    ├── ConflictResolver.cs    ← Construction: new MergeResult.X() → new X()
    ├── ConflictResolutionFlow ← Pattern match syntax update
    ├── BatchCommand.cs        ← Pattern match syntax update
    │
    ▼
ConflictResolverTests.cs       ← Test assertion updates
```

### Key Design Decisions

**D1: MergeResult as first candidate (not StateCategory)**

Rationale: `MergeResult` is already a manual DU — its subtypes exist, pattern matching is
in use, and it's not serialized. This exercises the core DU feature (exhaustiveness on
data-carrying variants) without JSON/SQLite compatibility concerns. `StateCategory` is
deferred as a more complex future candidate because it's an enum with ordinal values,
JSON serialization, and 22+ call sites.

**D2: .NET 11 Preview upgrade scoped to a feature branch**

Rationale: The `union` keyword is only available on .NET 11. Upgrading on a feature branch
allows validation without committing the entire project to a preview framework. If .NET 11
Preview proves unstable, the branch can be shelved until GA (November 2026).

**D3: Polyfill in a dedicated file, not a NuGet package**

Rationale: The polyfill is 6 lines of code. A NuGet dependency adds versioning complexity
for something that will be removed when .NET 11 GA ships the types natively. The polyfill
file can be `#if`-guarded or simply deleted later.

**D4: Case types become top-level (not nested)**

The current pattern nests subtypes inside the abstract record:
```csharp
public abstract record MergeResult {
    public sealed record NoConflict : MergeResult;
    // ...
}
```

With the `union` keyword, member types are standalone:
```csharp
public sealed record NoConflict;
public sealed record AutoMergeable(IReadOnlyList<string> MergedFields);
public sealed record HasConflicts(IReadOnlyList<FieldConflict> ConflictingFields);

public union MergeResult(NoConflict, AutoMergeable, HasConflicts);
```

This changes the fully-qualified name from `MergeResult.NoConflict` to just `NoConflict`.
Call sites need updating, but the pattern-match syntax in switch statements is essentially
the same. A `using static` or namespace alias could ease the transition if name collisions
arise.

**D5: Research-first approach per stakeholder feedback**

The user confirmed that DUs are likely not in .NET 10 and may need a .NET 11 preview build.
They directed that "the rest should be a research deliverable as our first issue(s)." This
restructures the plan: T1-T3 are research tasks producing documentation, and T4-T5 are
conditional implementation tasks that only proceed if the research validates feasibility.
This ensures the candidate inventory and migration playbook are delivered regardless of
SDK availability.

### Data Flow

No data flow changes. `MergeResult` values flow through the same paths:
1. `ConflictResolver.Resolve()` → produces `MergeResult`
2. `ConflictResolutionFlow.ResolveAsync()` → pattern-matches on result
3. `BatchCommand` → pattern-matches on result

The DU changes the *declaration* of the type, not the data flow.

## Alternatives Considered

### A1: Convert StateCategory (enum) as first candidate

**Pros:** Exercises DU on the simplest type (tag-only, no data). **Cons:** StateCategory
is JSON-serialized with `JsonStringEnumConverter`, has ordinal values for ranking, is
registered in `TwigJsonContext`, and has 22+ call sites. High risk for a first adoption.
The entire point of "proving the pattern" is to minimize risk.

### A2: Stay on .NET 10, use LangVersion=preview

**Pros:** No framework upgrade. **Cons:** Research confirms `union` is a C# 15 / .NET 11
feature. `LangVersion=preview` on .NET 10 enables C# 14 preview features but not `union`.
This path provides no DU capability.

### A3: Big-bang refactor of all 8 manual DU types

**Pros:** Uniform codebase in one pass. **Cons:** High risk, large blast radius, blocks
other work. The work item explicitly calls for opportunistic adoption.

### A4: Keep manual DU pattern, skip native DUs until .NET 11 GA

**Pros:** No preview feature risk, no framework upgrade. **Cons:** Misses the opportunity
to validate the feature early. If issues surface (AOT incompatibility, boxing performance),
better to discover them now than during a .NET 11 GA upgrade with a larger codebase.

## Dependencies

### External

- **.NET 11 Preview SDK** — must be installed on dev machines and CI agents. Available from
  [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/11.0).
- **C# 15 `union` keyword** — requires `LangVersion=preview` on .NET 11

### Internal

- **No blocking dependencies** — this issue is self-contained
- **Result Type Convention** (`result-type-convention.plan.md`) — complementary, not
  dependent. DU adoption refines the convention.

### Sequencing

- Task T1 (SDK feasibility) gates T2 and T4 — if SDK is unavailable, only T3 proceeds
- Task T2 (polyfill + AOT test) depends on T1
- Task T3 (candidate inventory) can run in parallel with T1 and T2
- Task T4 (MergeResult conversion) depends on T1 + T2 succeeding
- Task T5 (test validation + guidelines) depends on T4

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| All projects (6) | TargetFramework upgrade to net11.0 | Medium — framework upgrade may surface breaking changes |
| `Twig.Domain` | `MergeResult` type refactor, polyfill addition | Low — contained change |
| `Twig` (CLI) | `ConflictResolutionFlow.cs`, `BatchCommand.cs` pattern match updates | Low |
| `Twig.Mcp` | No MergeResult usage | None |
| Test projects | `ConflictResolverTests.cs` assertion updates | Low |
| `global.json` | SDK version bump | Low |
| `Directory.Build.props` | TFM + LangVersion changes | Low |

### Backward Compatibility

- **Wire format**: `MergeResult` is never serialized — no compatibility concerns.
- **API surface**: Pattern matching syntax changes from `MergeResult.NoConflict` to
  `NoConflict`, but this is an internal type (not in any public API contract).
- **.NET 11 Preview**: Not suitable for production release. This is a feature-branch spike.

### Performance

- No expected performance impact for `MergeResult` conversion (reference types, no boxing).
- Framework upgrade may change baseline performance characteristics — measure with benchmarks
  if any regressions are observed.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| .NET 11 Preview breaks existing code (API changes, removed APIs) | Medium | High | Run full test suite immediately after upgrade; fix issues before proceeding |
| DU types incompatible with AOT/trimming | Low | High | Verify with `dotnet publish -c Release` before committing; Task 4 is dedicated to this |
| Preview `union` syntax changes between SDK versions | Medium | Medium | Pin SDK version in `global.json`; isolated to one type (MergeResult) for easy revert |
| Preview warnings leak through `TreatWarningsAsErrors` | Medium | Medium | Add specific preview warning codes to `<NoWarn>` |
| .NET 11 Preview not available on CI agents | Medium | Medium | Document manual verification steps; defer CI integration to .NET 11 GA |
| Name collision when un-nesting case types | Low | Low | Use namespace organization or `using` aliases if needed |

## Open Questions

| # | Question | Severity | Status | Notes |
|---|----------|----------|--------|-------|
| OQ-1 | ~~What is the exact DU syntax?~~ | ~~Moderate~~ | **Resolved** | Syntax is `public union TypeName(Case1, Case2, ...)`. Requires .NET 11 SDK, net11.0 TFM, LangVersion=preview, and polyfill. |
| OQ-2 | Does AOT publish work with `union` types on .NET 11 Preview? | **Moderate** | Open | The blog post doesn't discuss AOT. Must verify empirically in Task T2. This gates whether the PoC (T4) can proceed. |
| OQ-3 | Which .NET 11 Preview SDK version should be pinned in global.json? | Low | Open | Use the latest available preview at implementation time. |
| OQ-4 | Does the `union` keyword support `sealed` on the union type itself? | Low | Open | May affect Twig's convention of `sealed` classes. Verify during implementation. |
| OQ-5 | Are there .NET 11 breaking changes that affect Twig's dependencies (Spectre.Console, ConsoleAppFramework, SQLitePCLRaw)? | **Moderate** | Open | Must verify during Task T1 framework upgrade. |
| OQ-6 | Is the .NET 11 Preview SDK available for download yet? | **Major** | Open | User indicated DUs are "likely not" in .NET 10 and "may need a preview .NET 11 build". If .NET 11 Preview is not yet available, only T3 (documentation) can proceed. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Common/CompilerPolyfill.cs` | `UnionAttribute` + `IUnion` polyfill for .NET 11 Preview DU support |
| `docs/research/du-feasibility-report.md` | Research findings: SDK feasibility, AOT compatibility, candidate inventory with rankings |
| `docs/research/du-migration-playbook.md` | DU adoption conventions: before/after patterns, adoption criteria, gotchas |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `global.json` | SDK version from `10.0.104` to .NET 11 Preview SDK version |
| `Directory.Build.props` | `TargetFramework` to `net11.0`, `LangVersion` from `latest` to `preview` |
| `src/Twig.Domain/Services/Sync/ConflictResolver.cs` | Extract `MergeResult` subtypes to top-level records; replace `abstract record MergeResult` with `union MergeResult(...)` declaration; update `Resolve()` construction syntax |
| `src/Twig/Commands/ConflictResolutionFlow.cs` | Update `is not MergeResult.HasConflicts` to `is not HasConflicts` |
| `src/Twig/Commands/BatchCommand.cs` | Update MergeResult pattern matching syntax |
| `tests/Twig.Domain.Tests/Services/Sync/ConflictResolverTests.cs` | Update test assertions for new type names |
| `REVIEW_GUIDELINES.md` | Add DU adoption guidelines section |

### Deleted Files

*None.*

## ADO Work Item Structure

### Issue #2587: Opportunistic DU Preview Adoption in Twig

**Goal:** Validate C# discriminated union adoption in Twig by upgrading to .NET 11 Preview,
producing a research deliverable documenting feasibility and candidates, and (conditionally)
converting one manual DU type (`MergeResult`) to the native `union` keyword.

**Prerequisites:** None (self-contained issue).

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | **Research: .NET 11 Preview SDK feasibility spike**: Install .NET 11 Preview SDK. Update `global.json` to the new SDK version. Change `TargetFramework` from `net10.0` to `net11.0` in `Directory.Build.props`. Change `LangVersion` from `latest` to `preview`. Run `dotnet build` for all 6 projects. Fix any breaking changes from the framework upgrade. Add preview-specific warning suppressions to `<NoWarn>` if needed. Document all failures, breaking changes, and workarounds. Verify `TreatWarningsAsErrors` still passes. This is the gate — if .NET 11 Preview is not yet available or DUs are not in the preview, document findings and stop. | `global.json`, `Directory.Build.props` | Medium |
| T2 | **Research: DU polyfill and AOT compatibility test**: Add `CompilerPolyfill.cs` in `Twig.Domain/Common/` with `UnionAttribute` and `IUnion` in `System.Runtime.CompilerServices` namespace. Create a minimal standalone DU type (`TestUnion`) in the test project to validate basic DU + AOT. Run `dotnet publish -c Release` with AOT for `Twig` and `Twig.Mcp`. Document binary size before/after. Verify the published binaries execute correctly. Document whether DU types survive `TrimMode=full`. | `CompilerPolyfill.cs` (new), test file (new) | Medium |
| T3 | **Research: DU candidate inventory and migration playbook**: Audit all 9 hand-rolled DU types and 10 enums. Rank each by DU fitness (strong/moderate/weak) with effort estimates and risk assessment. Document before/after patterns for `union` syntax. Write adoption criteria (when to convert, when not to). Document JSON serialization considerations for types registered in `TwigJsonContext`. Assess `Result<T>` and `SprintHierarchyNode` as future candidates. Commit as `docs/research/du-feasibility-report.md` and `docs/research/du-migration-playbook.md`. | `docs/research/du-feasibility-report.md` (new), `docs/research/du-migration-playbook.md` (new) | Medium |
| T4 | **Convert MergeResult to union type** (conditional on T1+T2 success): Extract `NoConflict`, `AutoMergeable`, and `HasConflicts` from nested sealed records to top-level sealed records. Replace `abstract record MergeResult` with `public union MergeResult(NoConflict, AutoMergeable, HasConflicts)`. Update `ConflictResolver.Resolve()` construction syntax. Update pattern matching in `ConflictResolutionFlow.cs` and `BatchCommand.cs`. Remove `_ =>` catch-all arms and verify compiler enforces exhaustiveness. | `ConflictResolver.cs`, `ConflictResolutionFlow.cs`, `BatchCommand.cs` | Medium |
| T5 | **Validate and document** (conditional on T4): Run `dotnet test` for all test projects. Update `ConflictResolverTests.cs` for new type names. Verify AOT publish still succeeds. Add a "Discriminated Unions" section to `REVIEW_GUIDELINES.md` documenting: the `union` keyword syntax, candidate backlog (all 9 manual DU types), when to convert opportunistically, when NOT to convert (serialized types, hot paths), AOT verification checklist. List `StateCategory` as a deferred candidate with rationale. | `ConflictResolverTests.cs`, `REVIEW_GUIDELINES.md` | Small |

**Task Dependencies:**
- T1 gates everything — if SDK is unavailable, only T3 proceeds (as documentation-only)
- T2 depends on T1 (need preview SDK for polyfill + AOT test)
- T3 can run in parallel with T1 and T2 (inventory is research, not code)
- T4 depends on T1 + T2 (must confirm feasibility before converting production code)
- T5 depends on T4

**Acceptance Criteria:**
- [ ] .NET 11 Preview SDK research documented (availability, version, install path)
- [ ] Build/test/AOT results documented for .NET 11 Preview (or blockers if unavailable)
- [ ] DU candidate inventory committed to `docs/research/`
- [ ] Migration playbook committed to `docs/research/`
- [ ] (Conditional) `MergeResult` uses native `union` syntax
- [ ] (Conditional) All call sites updated — no `_ =>` catch-alls on `MergeResult` switches
- [ ] (Conditional) `dotnet build` succeeds for all 6 projects with zero errors
- [ ] (Conditional) `dotnet publish -c Release` succeeds with AOT for `Twig` and `Twig.Mcp`
- [ ] (Conditional) All existing tests pass (with MergeResult assertion updates)
- [ ] (Conditional) DU adoption guidelines documented in `REVIEW_GUIDELINES.md`

## PR Groups

### PG-1: Planning Artifacts (Research Plan + Candidate Inventory)

**Tasks included:** T3 (candidate inventory documentation, pre-implementation)
**Classification:** Deep (documentation-only, no source changes)
**Estimated LoC:** ~0 (docs only)
**Files:** ~2 (plan documents)
**Successors:** PG-2

**Review guidance:**
- Verify the research plan accurately captures the .NET 11 / C# 15 finding
- Confirm the candidate inventory lists all 9 manual DU types with correct file paths
- Confirm `StateCategory` is listed as deferred (not a first candidate) with rationale
- Check that `MergeResult` is identified as the recommended first PoC candidate
- Review the migration playbook for actionable patterns and clear adoption criteria
- **This is a planning PR** — review for accuracy and coherence of findings

### PG-2: Research Deliverables (SDK Spike + AOT Validation)

**Tasks included:** T1, T2
**Classification:** Deep (few files, investigation-heavy with technical validation)
**Estimated LoC:** ~300–500 (config changes + polyfill + documentation)
**Files:** ~8
**Predecessor:** PG-1
**Successors:** PG-3

**Review guidance:**
- Verify .NET 11 Preview SDK version is pinned correctly in `global.json`
- Check that `TargetFramework=net11.0` and `LangVersion=preview` are set globally
- Verify DU polyfill matches the blog post's specification exactly
- Review feasibility report for thoroughness (AOT results, SDK version, compatibility findings)
- If SDK is unavailable, confirm the report documents the blocker clearly
- **This is a research spike** — review for accuracy and completeness of findings

### PG-3: DU Proof-of-Concept Conversion (Conditional)

**Tasks included:** T4, T5
**Classification:** Deep (few files, complex type-system changes)
**Estimated LoC:** ~150–200 (type refactor + test updates + guidelines doc)
**Files:** ~6
**Predecessor:** PG-2

**Review guidance:**
- Confirm `union MergeResult(...)` declaration compiles and enforces exhaustiveness
- Check that all switch statements on `MergeResult` have exhaustive arms (no `_ =>`)
- Verify AOT publish output exists and runs
- Review guidelines doc for accuracy and completeness
- **This PR is conditional** — only created if PG-2 proves SDK + DU + AOT feasibility

## Future Work (Candidate Backlog)

Once the pattern is proven, the following types are candidates for opportunistic DU conversion:

| Priority | Type | Cases | Rationale |
|----------|------|-------|-----------|
| 1 | `SyncResult` | 5 | Most call sites among manual DUs; high value from exhaustiveness |
| 2 | `ActiveItemResult` | 4 | Used across commands and MCP — catches routing bugs |
| 3 | `StatusResult` | 3 | Clean 3-case type, low risk |
| 4 | `BranchLinkResult` | 4 | Self-contained in navigation service |
| 5 | `MatchResult` | 3 | Small, clean, well-tested |
| 6 | `WorkspaceDataChunk` | 5 | Rendering pipeline — exhaustiveness prevents dropped chunks |
| 7 | `BatchNode` | 3 | Batch execution graph — tree traversal exhaustiveness |
| 8 | `TemplateSegment` | 2 | Simplest conversion (2 cases) but MCP-internal |
| Deferred | `StateCategory` (enum) | 6 | Requires JSON serialization work, ordinal semantics, 22+ call sites |

## Execution Plan

### PR Group Table

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|-------------|--------------|------|
| PG-1 | Planning Artifacts | #2587 / T3 (docs) | none | deep |
| PG-2 | Research Deliverables (SDK Spike) | #2587 / T1, T2 | PG-1 | deep |
| PG-3 | DU PoC Conversion (conditional) | #2587 / T4, T5 | PG-2 | deep |

### Execution Order

**PG-1 → PG-2 → PG-3 (sequential, PG-3 conditional on PG-2 outcome)**

PG-1 is submitted first. It delivers the planning documents: the research plan with
MergeResult-first rationale (T3 research/inventory portion) and this candidate inventory,
establishing the approach before any code changes land.

PG-2 establishes the .NET 11 Preview SDK baseline (T1), validates DU + AOT compatibility
(T2), and delivers the feasibility report and migration playbook as committed docs. These
tasks are self-contained as a research spike: the codebase builds on .NET 11 (or the
blocker is documented), the polyfill exists, and the research docs are committed —
regardless of whether T4/T5 proceed.

PG-3 is opened only if PG-2 confirms SDK + DU + AOT feasibility. It converts
`MergeResult` to the native `union` keyword (T4) and validates tests plus documents
adoption guidelines (T5). If PG-2 determines the .NET 11 Preview SDK is unavailable or
DUs are not yet in the available preview, PG-3 is deferred and the issue closes with
the PG-2 research deliverables.

### Validation Strategy

**PG-1 validation:**
1. `dotnet build` succeeds for all 6 projects on .NET 11 with `TreatWarningsAsErrors=true`
2. `dotnet test` passes for all test projects
3. `dotnet publish -c Release` (AOT) succeeds for `Twig` and `Twig.Mcp`
4. `docs/research/du-feasibility-report.md` exists and covers all 9 manual DU types + 10 enums
5. `docs/research/du-migration-playbook.md` exists with before/after patterns and adoption criteria
6. If SDK is unavailable: report documents the blocker and build evidence; no build validation required

**PG-2 validation (conditional):**
1. `union MergeResult(NoConflict, AutoMergeable, HasConflicts)` compiles
2. All `MergeResult` switch statements have exhaustive arms — no `_ =>` catch-alls
3. `dotnet test` passes with updated `ConflictResolverTests.cs` assertions
4. `dotnet publish -c Release` (AOT) succeeds for `Twig` and `Twig.Mcp` post-conversion
5. `REVIEW_GUIDELINES.md` contains a "Discriminated Unions" section with candidate backlog

## References

- [A First Look at C# Unions in .NET 11](https://oksala.net/2026/04/18/a-first-look-at-c-unions-in-net-11/) — blog post with syntax examples and polyfill
- [C# Language Design Discussion #9663](https://github.com/dotnet/csharplang/discussions/9663) — union feature discussion
- [C# Discriminated Unions Proposal](https://github.com/dotnet/csharplang/issues/113) — original language proposal
- [Result Type Convention Plan](./result-type-convention.plan.md) — established the abstract record DU pattern
- Work Item #2587: Opportunistic DU preview adoption in Twig
