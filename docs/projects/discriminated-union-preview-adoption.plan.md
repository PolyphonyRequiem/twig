# Opportunistic DU Preview Adoption in Twig

**Work Item:** #2587
**Type:** Issue
**Status:** Draft

> **⚠️ SUPERSEDED:** This document is a preliminary draft written before the research
> investigation was complete. It contains factual errors (see below) and proposes an approach
> that was explicitly rejected by the research. The authoritative plan is
> [`du-preview-adoption.plan.md`](du-preview-adoption.plan.md), which reflects the correct
> findings (DUs require .NET 11 / C# 15, and MergeResult — not StateCategory — is the
> recommended first conversion candidate). This document is retained for historical context
> only. Do not use it for implementation guidance.

---

## Executive Summary

> **⚠️ Factual correction:** The original summary below incorrectly stated that DUs are
> available in .NET 10. Research conclusively shows that the `union` keyword is a
> **C# 15 / .NET 11** feature — not .NET 10. See [`du-preview-adoption.plan.md`](du-preview-adoption.plan.md)
> for the authoritative executive summary. The StateCategory-first approach below was
> also explicitly evaluated and rejected in favor of MergeResult-first (lower risk, no
> serialization complexity, only 4 call sites vs 22+).

C# discriminated unions (DUs) are available as a preview feature in .NET 10. Twig already
employs a manual DU pattern extensively — 8 types use `abstract record` + `sealed record`
variants (ActiveItemResult, SyncResult, MatchResult, etc.) and 4 enums serve as simpler
discriminators (StateCategory, TransitionKind, etc.). This plan enables `LangVersion=preview`
in the solution, verifies AOT compatibility and build impact, then proves the native DU syntax
by converting StateCategory — the most cross-cutting discriminator — as the first adoption.
The investigation phase gates all code changes: if DU preview breaks AOT or introduces
unacceptable build regressions, we stop and document findings without shipping code changes.

## Background

### Current Architecture

Twig's domain layer uses two patterns to represent closed sets of alternatives:

**Pattern 1: Manual DU via abstract records** (8 types)

```csharp
public abstract record SyncResult
{
    private SyncResult() { }
    public sealed record UpToDate : SyncResult;
    public sealed record Updated(int ChangedCount) : SyncResult;
    public sealed record Failed(string Reason) : SyncResult;
    // ...
}
```

These require ~5 lines of boilerplate per type (abstract base, private ctor, sealed variants)
and rely on convention — the compiler does not enforce exhaustive matching. Several call sites
include a `default:` or `_ =>` arm that throws `UnreachableException`, which only fires at
runtime.

**Pattern 2: Enums** (4 types)

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]
public enum StateCategory
{
    Proposed = 0, InProgress = 1, Resolved = 2,
    Completed = 3, Removed = 4, Unknown = 5
}
```

Enums support exhaustive switch warnings but cannot carry per-variant data. StateCategory
in particular is a core type referenced across 20+ source files and 25+ test files.

### Existing DU-like Types Inventory

| Type | Location | Variants | Serialized? |
|------|----------|----------|-------------|
| `ActiveItemResult` | `Domain/Services/Navigation/` | Found, NoContext, FetchedFromAdo, Unreachable | No |
| `SyncResult` | `Domain/Services/Sync/` | UpToDate, Updated, Failed, Skipped, PartiallyUpdated | No |
| `MatchResult` | `Domain/Services/Navigation/` | SingleMatch, MultipleMatches, NoMatch | No |
| `WorkspaceDataChunk` | `Twig/Rendering/` | ContextLoaded, SprintItemsLoaded, SeedsLoaded, RefreshStarted, RefreshCompleted | No |
| `StatusResult` | `Domain/Services/Workspace/` | NoContext, Unreachable, Success | No |
| `MergeResult` | `Domain/Services/Sync/` | NoConflict, AutoMergeable, HasConflicts | No |
| `BranchLinkResult` | `Domain/ValueObjects/` | Linked, AlreadyLinked, GitContextUnavailable, Failed | No |
| `BatchNode` | `Twig.Mcp/Services/Batch/` | StepNode, SequenceNode, ParallelNode | No |

### Enum Types Inventory

| Type | Location | Values | Serialized? |
|------|----------|--------|-------------|
| `StateCategory` | `Domain/Enums/` | 6 values | Yes — `JsonStringEnumConverter`, registered in `TwigJsonContext` |
| `TransitionKind` | `Domain/Enums/` | 3 values (None, Forward, Cut) | No |
| `TrackingMode` | `Domain/Enums/` | 2 values (Single, Tree) | Yes — registered in `TwigJsonContext` |
| `TrackingCleanupPolicy` | `Domain/Enums/` | 3 values | Yes — registered in `TwigJsonContext` |
| `SeedPublishStatus` | `Domain/ValueObjects/` | 5 values | No |
| `ParentPropagationOutcome` | `Domain/Services/Navigation/` | 5 values | No |

### StateCategory Call-Site Audit

StateCategory is the recommended first DU candidate but has significant cross-cutting impact:

| File | Usage | Impact |
|------|-------|--------|
| `Twig.Domain/Enums/StateCategory.cs` | Definition + JsonConverter attribute | Rewritten entirely |
| `Twig.Domain/ValueObjects/StateEntry.cs` | Field: `StateCategory Category` | Type reference changes |
| `Twig.Domain/Services/Process/StateCategoryResolver.cs` | Returns StateCategory values in switch expressions | Return types change |
| `Twig.Domain/Aggregates/ProcessConfiguration.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/Extensions/ProcessConfigExtensions.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/ValueObjects/StateResolver.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/Services/Navigation/ParentStatePropagationService.cs` | Compares StateCategory values | Comparison semantics |
| `Twig.Domain/Services/Navigation/DescendantVerificationService.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/Services/Sync/TrackingService.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/Services/Process/ProcessTypeSyncService.cs` | Uses StateCategory | Type reference changes |
| `Twig.Domain/Interfaces/ITrackingService.cs` | Uses StateCategory | Type reference changes |
| `Twig/Rendering/SpectreRenderer.cs` | 2+ switch statements on StateCategory | Switch arms update |
| `Twig/Rendering/SpectreTheme.cs` | ResolveCategory returns StateCategory | Return type changes |
| `Twig/Formatters/HumanOutputFormatter.cs` | Switch on StateCategory | Switch arms update |
| `Twig/Formatters/JsonOutputFormatter.cs` | Uses StateCategory | Type reference changes |
| `Twig/Formatters/JsonCompactOutputFormatter.cs` | Uses StateCategory | Type reference changes |
| `Twig/Formatters/MinimalOutputFormatter.cs` | Uses StateCategory | Type reference changes |
| `Twig/Formatters/IdsOutputFormatter.cs` | Uses StateCategory | Type reference changes |
| `Twig/Commands/StateCommand.cs` | Uses StateCategory | Type reference changes |
| `Twig/Commands/ProcessCommand.cs` | Uses StateCategory | Type reference changes |
| `Twig/Hints/HintEngine.cs` | Uses StateCategory | Type reference changes |
| `Twig.Mcp/Services/McpResultBuilder.cs` | Uses StateCategory | Type reference changes |
| `Twig.Mcp/Tools/MutationTools.cs` | Uses StateCategory | Type reference changes |
| `Twig.Infrastructure/Config/PromptStateWriter.cs` | Uses StateCategory | Type reference changes |
| `Twig.Infrastructure/Serialization/TwigJsonContext.cs` | `[JsonSerializable(typeof(StateCategory))]` | Serialization registration |
| 25+ test files | Assert on StateCategory values | Test assertions update |

### Build Configuration

- **SDK:** .NET 10.0.104 (`global.json` with `rollForward: latestMinor`)
- **LangVersion:** `latest` (in `Directory.Build.props`) — needs to change to `preview`
- **AOT:** `PublishAot=true` in Twig.csproj and Twig.Mcp.csproj
- **Trimming:** `PublishTrimmed=true`, `TrimMode=full`
- **JSON:** `JsonSerializerIsReflectionEnabledByDefault=false` — source-generated serialization only
- **Warnings:** `TreatWarningsAsErrors=true`

## Problem Statement

Twig's manual DU pattern (`abstract record` + `sealed record` variants) works but has
three limitations:

1. **No compiler-enforced exhaustiveness** — Switch expressions on DU types require a
   `_ =>` discard arm or risk warnings, but the compiler cannot prove all cases are handled.
   Missing variants are caught only at runtime via `UnreachableException`.

2. **Boilerplate overhead** — Each DU type requires ~5 lines of structural boilerplate
   (abstract declaration, private constructor, sealed modifiers). This discourages
   developers from creating new DU types for small discriminated sets.

3. **Enum limitations** — Types like StateCategory are enums because they don't carry
   per-variant data, but enums can be cast to arbitrary integers, compared as numbers,
   and don't participate in pattern matching with the same safety guarantees as DUs.

## Goals and Non-Goals

### Goals

1. **Enable preview language features** — Set `LangVersion=preview` without breaking
   existing builds, AOT publish, or tests
2. **Verify DU + AOT compatibility** — Prove that native DU types work with
   `PublishAot=true` and `TrimMode=full`
3. **Quantify build impact** — Measure build time and binary size delta from
   LangVersion change and DU adoption
4. **Prove the pattern** — Convert at least one type (StateCategory recommended) to
   native DU syntax, demonstrating compile-time exhaustiveness
5. **Establish adoption guidelines** — Document when and how to use DUs vs enums vs
   records for future development

### Non-Goals

- **Big-bang migration** — Not converting all 8 existing DU-like types in this issue
- **New DU types** — Not introducing new discriminated types; only converting existing ones
- **Runtime behavior changes** — DU adoption must be a pure refactor with identical runtime behavior
- **Twig.Tui changes** — Twig.Tui is not AOT-compatible and is out of scope

## Requirements

### Functional

- FR-1: All existing tests pass without modification to test logic (assertion types may change)
- FR-2: `dotnet publish` with AOT succeeds for both `Twig.csproj` and `Twig.Mcp.csproj`
- FR-3: StateCategory values serialize/deserialize identically in JSON output
- FR-4: Existing switch expressions compile without `_ =>` discard arms (exhaustiveness)

### Non-Functional

- NFR-1: Build time increase ≤ 10% (measured on clean build)
- NFR-2: AOT binary size increase ≤ 5%
- NFR-3: No new runtime allocations in hot paths from enum→DU conversion

## Proposed Design

### Architecture Overview

This is a language-level refactoring, not an architectural change. The design has two phases:

**Phase A: Investigation (gates Phase B)**
- Enable `LangVersion=preview`
- Verify DU syntax is available in the installed .NET 10 SDK
- Build and AOT-publish to confirm no regressions
- Measure baseline metrics (build time, binary size)
- Create a minimal DU type in a test project to verify AOT compatibility

**Phase B: Adoption (only if Phase A passes all checks)**
- Convert StateCategory from enum to native DU
- Update all call sites (20+ source files, 25+ test files)
- Verify serialization compatibility
- Remove `_ =>` / `default:` discard arms where exhaustiveness is now guaranteed

### Key Components

#### 1. Directory.Build.props Change

```xml
<!-- Before -->
<LangVersion>latest</LangVersion>

<!-- After -->
<LangVersion>preview</LangVersion>
```

This is a single-line change that affects all projects in the solution. The `preview` setting
enables all C# preview features, including discriminated unions.

#### 2. StateCategory DU Conversion

The exact syntax depends on what is available in the .NET 10 preview. The investigation
task will determine the concrete syntax. Possible forms:

**If `union` keyword is available:**
```csharp
// Hypothetical — actual syntax TBD during investigation
[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]  // may need custom converter
public union StateCategory
{
    Proposed,
    InProgress,
    Resolved,
    Completed,
    Removed,
    Unknown
}
```

**If closed hierarchies with exhaustiveness are the mechanism:**
```csharp
// Uses existing abstract record pattern but with compiler exhaustiveness
public abstract record StateCategory
{
    private StateCategory() { }
    public sealed record Proposed : StateCategory;
    public sealed record InProgress : StateCategory;
    public sealed record Resolved : StateCategory;
    public sealed record Completed : StateCategory;
    public sealed record Removed : StateCategory;
    public sealed record Unknown : StateCategory;
}
```

#### 3. Serialization Adapter

StateCategory is currently serialized as a JSON string enum (`"Proposed"`, `"InProgress"`, etc.)
via `JsonStringEnumConverter<StateCategory>`. If the DU type is no longer an enum, we need
a custom `JsonConverter` that preserves wire-format compatibility:

```csharp
// Sketch — actual implementation depends on DU syntax
public sealed class StateCategoryJsonConverter : JsonConverter<StateCategory>
{
    public override StateCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "Proposed" => new StateCategory.Proposed(),
            "InProgress" => new StateCategory.InProgress(),
            // ...
        };
    }
    // ...
}
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Investigation gates adoption | Yes | DU preview may not be AOT-compatible; we don't want to ship broken builds |
| StateCategory as first candidate | Yes (per work item) | Most cross-cutting type, proves the pattern at scale |
| Preserve JSON wire format | Required | Breaking serialization would break `twig workspace --format json` and MCP tools |
| Keep `Unknown` variant | Yes | Fallback for unrecognized ADO state strings; removing it would break process-agnostic design |
| Don't convert existing abstract record DUs yet | Correct | They already work; converting is mechanical and can happen opportunistically |

### Serialization Risk Analysis

StateCategory is the only DU candidate that is serialized. Key concerns:

1. **TwigJsonContext registration** — `[JsonSerializable(typeof(StateCategory))]` must be updated
   if the type changes from enum to class/record
2. **StateEntry dependency** — `StateEntry` is a `readonly record struct` with a `StateCategory Category`
   field. If StateCategory becomes a reference type, `StateEntry` can no longer be a struct
   (or it can, but with different copy semantics)
3. **JsonStringEnumConverter** — Only works with enums. A DU-based StateCategory needs a
   custom converter or a different serialization strategy
4. **AOT source generation** — Custom converters must be AOT-compatible (no reflection)

**Mitigation:** The investigation task specifically tests serialization round-tripping before
any source file changes are committed.

## Alternatives Considered

### Alternative 1: Skip StateCategory, start with a non-serialized DU type

**Pros:** Lower risk (MatchResult has 3 variants, no serialization, small call-site surface).
**Cons:** Doesn't prove the hardest case. StateCategory is the type most likely to surface
compatibility issues, making it the best investigation target.
**Decision:** Start with StateCategory per work item guidance, but fall back to MatchResult
if serialization proves too complex.

### Alternative 2: Use `#pragma warning disable` instead of DU adoption

**Pros:** Zero code changes beyond the pragma.
**Cons:** Doesn't address the problem — we still lack exhaustiveness guarantees.
**Decision:** Rejected. The goal is to improve type safety, not suppress warnings.

### Alternative 3: Wait for DU to exit preview

**Pros:** No preview-feature risk.
**Cons:** Could be 1-2 release cycles away. Twig already uses `net10.0` and preview SDK.
**Decision:** Adopt now behind `LangVersion=preview` since the project already targets
the latest framework and SDK.

## Dependencies

### External
- .NET 10 SDK ≥ 10.0.100 with DU preview support (currently using 10.0.104)
- No new NuGet packages required

### Internal
- `Directory.Build.props` change affects all projects in the solution
- StateCategory change touches Domain, Infrastructure, CLI, MCP, and TUI projects

### Sequencing
- Investigation tasks (1-3) must complete before adoption tasks (4-6)
- Investigation findings may cause scope adjustment or cancellation of adoption tasks

## Impact Analysis

### Components Affected

| Component | Impact | Severity |
|-----------|--------|----------|
| Twig.Domain | StateCategory definition + all consumers | High |
| Twig.Infrastructure | TwigJsonContext, PromptStateWriter | Medium |
| Twig (CLI) | Renderers, formatters, commands, hints | High |
| Twig.Mcp | McpResultBuilder, MutationTools | Low |
| Twig.Tui | TreeNavigatorView (uses StateCategory) | Low |
| All test projects | Assertions on StateCategory values | Medium |

### Backward Compatibility

- **JSON output:** Must remain identical (wire-format preserved)
- **CLI behavior:** No user-visible changes
- **SQLite schema:** StateCategory is not stored in SQLite; no migration needed

### Performance

- **Build time:** Preview language features may increase compilation time; measured in investigation
- **Binary size:** DU types may generate more IL than enums; measured in investigation
- **Runtime:** Switching from value-type enum to reference-type DU could introduce allocations;
  profiled in investigation

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| DU preview not available in current SDK | Medium | High | Investigation task verifies before any code changes; fall back to documenting findings |
| DU types break AOT publish | Medium | High | Investigation task includes AOT publish test; abort adoption if it fails |
| StateCategory serialization breaks | Medium | High | Custom JsonConverter with round-trip tests; wire-format compatibility verified |
| Build time regression > 10% | Low | Medium | Measured in investigation; revert LangVersion if unacceptable |
| Preview features introduce compiler bugs | Low | Medium | TreatWarningsAsErrors catches issues early; revert if unstable |
| StateEntry struct semantics change | Medium | Medium | If StateCategory becomes reference type, assess StateEntry impact; may keep as struct with nullable field |

## Open Questions

1. **[Critical] Is `union` syntax available in .NET 10.0.104 preview?** — The C# DU feature
   has been in proposal for several years. The investigation task must verify that
   `LangVersion=preview` actually enables a usable DU syntax in the installed SDK. If not,
   the project scope reduces to documenting findings and establishing patterns for when
   DU does ship.

2. **[Major] How do DU types interact with System.Text.Json source generation?** — Twig uses
   `JsonSerializerIsReflectionEnabledByDefault=false` with `TwigJsonContext`. If DU types
   require reflection-based serialization, they cannot be used for serialized types under AOT.

3. **[Major] Does AOT trimming preserve DU type metadata?** — `TrimMode=full` aggressively
   removes unreferenced code. DU pattern matching may rely on type metadata that trimming
   could remove. The investigation task must verify with an actual `dotnet publish -r win-x64`.

4. **[Moderate] What is the allocation profile of DU vs enum?** — StateCategory is used in
   tight loops (sprint item rendering, hierarchy building). If DU variants are heap-allocated
   reference types instead of stack-allocated value types, this could measurably impact
   performance. Investigation should include a basic allocation comparison.

5. **[Low] Should we adopt DU for the existing abstract-record types?** — The 8 existing
   manual DU types already work. Converting them to native syntax would reduce boilerplate
   but is mechanical. This is deferred to opportunistic adoption in future PRs.

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tests/Twig.Domain.Tests/Spikes/DuAotCompatibilityTests.cs` | AOT compatibility spike tests for DU types |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `Directory.Build.props` | Change `LangVersion` from `latest` to `preview` |
| `src/Twig.Domain/Enums/StateCategory.cs` | Convert from enum to DU (if investigation passes) |
| `src/Twig.Domain/ValueObjects/StateEntry.cs` | Update StateCategory type reference |
| `src/Twig.Domain/Services/Process/StateCategoryResolver.cs` | Update return expressions for DU syntax |
| `src/Twig.Domain/Aggregates/ProcessConfiguration.cs` | Update StateCategory references |
| `src/Twig.Domain/Extensions/ProcessConfigExtensions.cs` | Update StateCategory references |
| `src/Twig.Domain/ValueObjects/StateResolver.cs` | Update StateCategory references |
| `src/Twig.Domain/Services/Navigation/ParentStatePropagationService.cs` | Update StateCategory comparisons |
| `src/Twig.Domain/Services/Navigation/DescendantVerificationService.cs` | Update StateCategory references |
| `src/Twig.Domain/Services/Sync/TrackingService.cs` | Update StateCategory references |
| `src/Twig.Domain/Services/Process/ProcessTypeSyncService.cs` | Update StateCategory references |
| `src/Twig.Domain/Interfaces/ITrackingService.cs` | Update StateCategory references |
| `src/Twig/Rendering/SpectreRenderer.cs` | Update switch statements on StateCategory |
| `src/Twig/Rendering/SpectreTheme.cs` | Update StateCategory returns |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Update switch on StateCategory |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Update StateCategory references |
| `src/Twig/Formatters/JsonCompactOutputFormatter.cs` | Update StateCategory references |
| `src/Twig/Formatters/MinimalOutputFormatter.cs` | Update StateCategory references |
| `src/Twig/Formatters/IdsOutputFormatter.cs` | Update StateCategory references |
| `src/Twig/Commands/StateCommand.cs` | Update StateCategory references |
| `src/Twig/Commands/ProcessCommand.cs` | Update StateCategory references |
| `src/Twig/Hints/HintEngine.cs` | Update StateCategory references |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Update StateCategory references |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Update StateCategory references |
| `src/Twig.Infrastructure/Config/PromptStateWriter.cs` | Update StateCategory references |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Update `[JsonSerializable]` registration |
| 25+ test files (see call-site audit) | Update test assertions and StateCategory references |

## ADO Work Item Structure

### Issue: #2587 — Opportunistic DU Preview Adoption in Twig

**Goal:** Enable C# discriminated union preview features in Twig's .NET 10 build, verify
AOT compatibility, and prove the pattern with at least one DU adoption (StateCategory).

**Prerequisites:** None (this is a standalone investigation + refactoring issue)

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | **Baseline measurement**: Record clean build time, AOT publish time, and binary sizes for Twig.csproj and Twig.Mcp.csproj. Run full test suite and record pass count. | None (measurement only) | Small |
| T2 | **Enable LangVersion=preview**: Change `Directory.Build.props` LangVersion from `latest` to `preview`. Verify full solution builds with zero errors/warnings. Run test suite. Record build time delta. | `Directory.Build.props` | Small |
| T3 | **DU syntax availability spike**: Attempt to define a DU type using native C# syntax in a test project. Determine the exact syntax available (union keyword, closed hierarchy exhaustiveness, etc.). If no DU syntax is available, document findings and stop. | `tests/Twig.Domain.Tests/Spikes/DuAotCompatibilityTests.cs` | Small |
| T4 | **AOT compatibility verification**: If T3 succeeds, create a DU type in the Twig.Domain project, add it to TwigJsonContext if serializable, and run `dotnet publish -c Release -r win-x64` for both AOT projects. Compare binary size to baseline. | Spike files in `src/Twig.Domain/` | Medium |
| T5 | **StateCategory DU conversion**: Convert StateCategory from enum to native DU syntax. Update StateCategoryResolver, StateEntry, and all call sites. Implement custom JsonConverter if needed. Remove `_ =>` / `default:` discard arms where exhaustiveness is now guaranteed. | See Modified Files table | Large |
| T6 | **Test updates and verification**: Update all 25+ test files that reference StateCategory. Verify full test suite passes. Run AOT publish. Confirm JSON output wire-format compatibility. | All test files referencing StateCategory | Medium |

**Acceptance Criteria:**

- [ ] `LangVersion=preview` set in `Directory.Build.props` without build errors
- [ ] Investigation findings documented (DU syntax availability, AOT compat, build metrics)
- [ ] At least one DU type compiles and passes AOT publish (T3/T4)
- [ ] StateCategory converted to DU with all call sites updated (T5, contingent on T3/T4)
- [ ] Full test suite passes with zero regressions
- [ ] JSON serialization of StateCategory values is wire-format identical
- [ ] AOT binary size delta documented and within ≤5% threshold

### Decision Gates

- **After T3:** If DU syntax is not available in the current SDK, Tasks T4-T6 are cancelled.
  The issue closes with LangVersion=preview enabled (T2) and investigation findings documented.
- **After T4:** If AOT publish fails with DU types, Task T5-T6 are cancelled. The issue
  closes with investigation findings and a recommendation to revisit when AOT support ships.

## PR Groups

### PG-1: Investigation & Preview Enablement (deep)

**Scope:** Tasks T1, T2, T3, T4
**Classification:** Deep — few files, complex investigation
**Estimated LoC:** ~150-300
**Estimated Files:** ~5-8
**Successor:** PG-2

**Contents:**
- `Directory.Build.props` change (LangVersion)
- DU spike test file
- AOT verification results (documented in PR description)
- Build metric comparison table (in PR description)

**Rationale:** This PR is the investigation gate. It must merge before PG-2 begins.
If investigation reveals DU is not viable, this PR still ships with LangVersion=preview
enabled (which has value independent of DU) and the investigation findings documented.

### PG-2: StateCategory DU Adoption (wide)

**Scope:** Tasks T5, T6
**Classification:** Wide — many files, mechanical changes
**Estimated LoC:** ~400-800
**Estimated Files:** ~40-50
**Successor:** None

**Contents:**
- StateCategory type conversion
- Custom JsonConverter (if needed)
- All call-site updates (20+ source files)
- All test updates (25+ test files)
- TwigJsonContext registration update

**Rationale:** This is a large but mechanical change. Every call site gets the same
transformation (enum member → DU variant). The PR is wide but low-complexity per file.
Review can focus on the StateCategory definition and JsonConverter; call-site changes
are mechanical.

**Precondition:** PG-1 merged and investigation confirms DU + AOT viability.

## References

- [C# Discriminated Unions Proposal](https://github.com/dotnet/csharplang/issues/113)
- [.NET 10 Preview Features](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)
- Existing Twig DU pattern: `src/Twig.Domain/Services/Sync/SyncResult.cs`
- Existing Twig DU pattern: `src/Twig.Domain/Services/Navigation/ActiveItemResult.cs`


