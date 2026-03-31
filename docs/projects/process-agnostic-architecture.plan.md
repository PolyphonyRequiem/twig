# Plan: Process-Agnostic Architecture — Eliminate Hardcoded Process Assumptions

> **Date**: 2025-06-29  
> **Status**: Draft  
> **ADO Epic**: #1345  
> **Child Issues**: #1346, #1347, #1348, #1349, #1350  

---

## Executive Summary

Twig contains **14 identified hardcoded process assumptions** spanning state names, work item type names, process template names, and field reference names. These assumptions cause concrete bugs: the progress indicator shows 0/5 in Agile workspaces despite 4/5 children being done, hints suggest invalid state names, and custom process templates are misidentified.

This plan establishes **process agnosticism as a foundational principle** (codified in `.github/instructions/process-agnostic.instructions.md`) and systematically eliminates all violations across 5 prioritized Issues.

---

## Background

### How it works today

ADO process configuration is discovered at `twig init` time and stored locally in SQLite via `IProcessTypeStore`. `DynamicProcessConfigProvider` builds a `ProcessConfiguration` aggregate from these records, containing per-type `TypeConfig` with `StateEntries`, `AllowedChildTypes`, and `TransitionRules`.

Some code paths — notably `HumanOutputFormatter.CountChildProgress` and `FlowStartCommand` — correctly use this dynamic configuration. But other paths bypass it entirely, falling back to hardcoded switches that only cover well-known ADO process templates.

### The root cause

`StateCategoryResolver` has a dual pattern:
1. **Dynamic path**: `Resolve(state, entries)` looks up the state in the provided `StateEntry` list — accurate for any process
2. **Fallback path**: When `entries` is `null` or the state isn't found, `FallbackCategory(state)` uses a hardcoded switch covering ~12 state names from Basic/Agile/Scrum/CMMI

The problem: **multiple call sites pass `null` for entries**, forcing the fallback even when process config is available. The fallback is inherently incomplete — it cannot cover custom process templates or even all states in standard templates.

### The triggering bug

In a workspace using the Agile process template, `StatusCommand.ComputeChildProgress` passed `null` for entries. The Agile "Completed" state wasn't in the fallback map at the time, so all completed children resolved to `StateCategory.Unknown` and the progress bar showed **0/5**.

---

## Problem Statement

Twig's correctness depends on which process template the user's ADO project uses. It works well for Basic (the developer's primary template) but silently produces wrong results for Agile, Scrum, CMMI, and custom templates. This violates the tool's value proposition of being a universal ADO work-item triage tool.

---

## Principle

**There shall be no hardcoded process information or assumptions in twig.**

Mapping between process-specific concepts and twig's internal model MUST only occur through the process configuration discovered at runtime from the ADO project scope. ADO's `StateCategory` enum (`Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`) is the acceptable process-agnostic abstraction layer — it's a platform-level concept, not process-specific.

Full principle codified in `.github/instructions/process-agnostic.instructions.md`.

---

## Audit Findings

### Severity HIGH

| # | File | Issue |
|---|------|-------|
| H1 | `StatusCommand.cs:325-339` | `ComputeChildProgress` passes `null` for state entries → wrong progress |
| H2 | `AdoIterationService.cs:65-89` | `DetectTemplateNameAsync` infers template from type names; custom → "Basic" |
| H3 | `HintEngine.cs:85` | Hardcodes `"twig state Closed"` without validating against process |
| H4 | `AdoResponseMapper.cs:337-343` | Unknown types silently become `Task` — data corruption |

### Severity MEDIUM

| # | File | Issue |
|---|------|-------|
| M1 | `TreeNavigatorView.cs:245-266` | Falls back to `"Task"` as leaf when process config unavailable |
| M2 | `FlowDoneCommand.cs:78-86` | Hardcodes `Resolved → Completed` fallback chain |
| M3 | `FlowStartCommand.cs:165-180` | Hardcodes `Proposed → InProgress` transition |
| M4 | `FlowCloseCommand.cs:100-108` | Hardcodes only `Completed` — no fallback |

### Severity LOW (configurable defaults)

| # | File | Issue |
|---|------|-------|
| L1 | `BranchNamingService.cs:19-28` | `DefaultTypeMap` with 10 hardcoded type→prefix mappings |
| L2 | `CommitMessageService.cs:16-26` | `DefaultTypeMap` with 10 hardcoded type→prefix mappings |
| L3 | `StatusFieldsConfig.cs:34-55` | `ProcessTemplateDefaults` for Agile/Scrum/CMMI only |
| L4 | `IconSet.cs:159-165` | `ResolveTypeBadge` switch on 13 type names (graceful fallback) |
| L5 | `WorkItemType.cs:10-35` | 13 static readonly well-known type constants |
| L6 | `AdoIterationService.cs:255-261` | `CategoryRank` hardcoded category sort order (ADO categories, may be OK) |

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | Fix the progress bar bug (#1346): `ComputeChildProgress` uses process config |
| G2 | Eliminate all `null` entries arguments to `StateCategoryResolver.Resolve()` in normal code paths (#1347) |
| G3 | Ensure unknown work item types never silently mutate to Task (#1348) |
| G4 | Replace template detection heuristic with ADO Process API (#1349) |
| G5 | Make HintEngine state suggestions dynamic (#1350) |
| G6 | Ensure `FallbackCategory` is only reachable in offline/degraded scenarios |
| G7 | Add multi-process test scenarios (at least Basic + Agile coverage) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Removing `FallbackCategory` entirely — it serves as a legitimate safety net for offline mode |
| NG2 | Replacing `WorkItemType` static constants — they're convenience values, not assumptions |
| NG3 | Making `DefaultTypeMap` in BranchNaming/CommitMessage fully dynamic — they're acceptable as configurable defaults |
| NG4 | Eliminating `CategoryRank` in `AdoIterationService` — these are ADO platform categories, not process-specific |

---

## Implementation Approach

### Issue #1346: StatusCommand.ComputeChildProgress (HIGH — do first)

**Changes:**
1. Add `IProcessConfigurationProvider` as optional parameter to `StatusCommand` constructor
2. Change `ComputeChildProgress` from `static` to instance method  
3. Look up `TypeConfig` for the child's work item type to obtain `StateEntries`
4. Pass entries to `StateCategoryResolver.Resolve()` instead of `null`
5. Fallback gracefully if process config is unavailable (use existing fallback, but log)

**DI registration:** Update `CommandServiceModule.cs` or `CommandRegistrationModule.cs` to pass `IProcessConfigurationProvider`.

**Tests:** 
- Verify correct progress for Basic states (To Do, Doing, Done)
- Verify correct progress for Agile states (New, Active, Resolved, Closed, Completed)
- Verify graceful fallback when process config is null

**Estimated complexity:** Low — follows existing pattern from `HumanOutputFormatter`

### Issue #1347: Eliminate hardcoded state name fallbacks (HIGH)

**Changes:**
1. Audit all `StateCategoryResolver.Resolve(_, null)` call sites
2. For each, thread `IReadOnlyList<StateEntry>` or `IProcessConfigurationProvider` through DI
3. Add `[Conditional("DEBUG")] static void WarnFallback()` to `FallbackCategory` to emit diagnostics
4. Consider adding `Obsolete` attribute with message directing callers to use entries

**Known null call sites from audit:**
- `StatusCommand.ComputeChildProgress` (covered by #1346)
- `HintEngine` state handling (covered by #1350)

**Tests:** Add `[Theory]` tests with state names from all 4 standard templates

### Issue #1348: Eliminate hardcoded work item type assumptions (MEDIUM)

**Changes:**
1. `AdoResponseMapper.ParseWorkItemType`: Return `WorkItemType.Parse(typeName)` with the original name, not `WorkItemType.Task`. If parse fails, create a `WorkItemType` from the raw string.
2. `TreeNavigatorView`: Ensure the process-config-first path always succeeds, remove the `"Task"` string literal fallback, default to "has children" rather than "is leaf" for unknown types
3. `IconSet.ResolveTypeBadge`: Keep the switch but document it as cosmetic fallback — the `_` case (first char) is adequate for unknown types
4. `BranchNamingService`/`CommitMessageService`: Keep `DefaultTypeMap` but add test for unknown type degradation

### Issue #1349: Replace DetectTemplateNameAsync (MEDIUM)

**Changes:**
1. Add method to `IAdoRestClient` (or `AdoIterationService`) to call `GET {org}/_apis/projects/{project}?api-version=7.1`
2. Response contains `capabilities.processTemplate.templateName`
3. Replace heuristic with API call; fall back to heuristic only when API fails
4. Cache the result per workspace

### Issue #1350: HintEngine process-aware (MEDIUM)

**Changes:**
1. Add `IProcessConfigurationProvider?` to `HintEngine` constructor  
2. In the `"state"` case, look up the active item's `TypeConfig` for actual state entries
3. Replace `"twig state Closed"` with dynamic completed-state name from entries
4. Pass entries to `StateCategoryResolver.Resolve()` instead of `null`

---

## Execution Order

```
#1346 (StatusCommand progress bar) ← Immediate bug fix, low risk
  ↓
#1347 (State name fallback audit) ← Foundation for all other fixes
  ↓
#1350 (HintEngine) ← Small scope, depends on same pattern as #1347
  ↓
#1348 (Type assumptions) ← Broader scope, lower urgency
  ↓
#1349 (Template detection API) ← Requires new API call, highest effort
```

---

## Testing Strategy

Each Issue must include tests covering at minimum:
- **Basic** process: To Do → Doing → Done
- **Agile** process: New → Active → Resolved → Closed
- **Graceful degradation**: Process config unavailable (null provider)

Use `ProcessConfigBuilder` from `Twig.TestKit` to construct test process configs.

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| DI changes break existing registrations | Low | High | Incremental changes; run full test suite after each Issue |
| `FallbackCategory` removal causes regressions in offline mode | Medium | Medium | Keep fallback; add diagnostic, don't remove |
| ADO Process API not available in all ADO versions | Low | Low | Keep heuristic as fallback behind API call |
| `ComputeChildProgress` becoming instance method breaks callers | Low | Low | Only called internally within StatusCommand |
