# Process-Agnostic Architecture: Eliminate All Hardcoded Process Assumptions

**Epic:** #1345  
**Status:** 🔨 In Progress — 1/4 PR groups merged  
**Author:** Copilot (Principal Software Architect)  
**Revision:** 15  
**Child Issues:** #1346, #1348, #1349, #1350  
**Last Verified:** 2026-04-03T20:05Z — all call sites, DI registrations, and line numbers re-confirmed against codebase  
**Revision Notes:** Rev 15 — tech review (93/100) + readability review (87/100) feedback: fixed stale #1347 references in Executive Summary, added missing HintEngine.cs:85 "Closed" entry to audit tables (14-item count now consistent), corrected DTO convention to use [JsonPropertyName] per codebase standard, improved grep acceptance criteria for variable-based null, resolved Rev 14 revision history contradiction, moved Conventions to appendix, added Task IDs to PR Groups, enriched Open Questions with resolution pointers.

---

## Executive Summary

Twig contains 14 identified hardcoded process assumptions spanning state names, work item type names, process template heuristics, and field reference names. These assumptions cause concrete bugs: the `twig status` and `twig set` progress indicators show incorrect counts in Agile workspaces (e.g., 0/5 despite 4/5 children being done), hints suggest invalid state names like "Closed" to Basic-process users, and custom process templates are misidentified as "Basic." This plan systematically eliminates all violations across 4 prioritized Issues (#1346, #1348, #1349, #1350) by threading `IProcessConfigurationProvider` through affected components and replacing hardcoded fallbacks with dynamic process configuration lookups. The result is a twig CLI that works correctly with any ADO process template — Agile, Scrum, CMMI, Basic, or custom — without modification.

---

## Background

### Current Architecture

Twig's process configuration system has a well-designed dynamic path that already works correctly for most components:

1. **`IProcessConfigurationProvider`** → `DynamicProcessConfigProvider` loads `ProcessTypeRecord` entries from SQLite (populated during `twig init`/`twig refresh`) and builds an immutable `ProcessConfiguration` aggregate.
2. **`ProcessConfiguration`** contains a `TypeConfigs` dictionary keyed by `WorkItemType`, where each `TypeConfig` holds `StateEntries` (with `StateCategory` metadata), `AllowedChildTypes`, and `TransitionRules`.
3. **`StateCategoryResolver.Resolve(state, entries)`** has a clean two-tier design: when entries are provided, it does an exact case-insensitive lookup; when entries are `null`, it falls back to `FallbackCategory()` — a hardcoded switch covering 12 state names from Basic/Agile/Scrum/CMMI (Proposed ×3, InProgress ×5, Resolved ×1, Completed ×2, Removed ×1). **Important fall-through behavior:** when entries *are* provided but the state name is not found in them (e.g., a new ADO state added after the last `twig refresh`, or stale process config), the resolver still falls through to `FallbackCategory()`. This is intentional — it provides a best-effort classification even with stale config. Implementers should be aware that `FallbackCategory()` remains reachable in production even after all null-entry call sites are fixed.

The problem is that **4 call sites pass `null` for entries**, forcing the fallback even when process config is available via DI. Additionally, `AdoIterationService.DetectTemplateNameAsync()` uses a type-name heuristic that misidentifies custom templates, and `AdoResponseMapper.ParseWorkItemType()` silently maps unknown types to `Task`.

### Components Already Using Process Config Correctly

| Component | Pattern |
|-----------|---------|
| `BranchCommand` | Injects `IProcessConfigurationProvider`, looks up `TypeConfig.StateEntries` |
| `FlowStartCommand` | Same pattern — resolves state entries per work item type |
| `FlowTransitionService` | Injects provider via DI, uses entries for transition evaluation |
| `SpectreTheme` | Receives `stateEntries` at construction, passes to `Resolve()` |
| `HumanOutputFormatter` | Receives `stateEntries` at construction, passes to `Resolve()` |
| `PromptStateWriter` | Resolves entries from `IProcessTypeStore` at write time |
| `TreeNavigatorView` | Injects `IProcessConfigurationProvider`, uses `AllowedChildTypes` |

### Call-Site Audit: `StateCategoryResolver.Resolve()` — 17 Production Call Sites

> **Scope:** All 17 production call sites are listed below. An additional ~12 test call sites exist in `StateCategoryResolverTests.cs` and `HumanOutputFormatterTests.cs`; these are omitted for brevity since they test the resolver directly and are not impacted by the changes.

> **Filtered view:** 13 of the 17 call sites already pass non-null entries and require no changes. These belong to: SpectreTheme (×3), PromptStateWriter (×1), BranchCommand (×1), HumanOutputFormatter (×5), FlowStartCommand (×2), FlowTransitionService (×1). They are omitted for brevity — only the 4 affected call sites are shown below.

| # | File | Method | Passes null? | Impact |
|---|------|--------|:---:|--------|
| **14** | **`HintEngine.cs:66`** | **`GetHints()` — new state** | **Yes** | **H3: Wrong category for custom states** |
| **15** | **`HintEngine.cs:75`** | **`GetHints()` — sibling check** | **Yes** | **H3: Wrong sibling evaluation** |
| **16** | **`SetCommand.cs:264`** | **`ComputeChildProgress()`** | **Yes** | **H1: Wrong progress count** |
| **17** | **`StatusCommand.cs:333`** | **`ComputeChildProgress()`** | **Yes** | **H1: Wrong progress count** |

### Call-Site Audit: Hardcoded Type Assumptions

| # | File | Location | Hardcoded Value | Impact |
|---|------|----------|----------------|--------|
| 1 | `AdoResponseMapper.cs:340` | `ParseWorkItemType()` | `WorkItemType.Task` (null fallback) | H4: Custom types silently become Task |
| 2 | `AdoResponseMapper.cs:343` | `ParseWorkItemType()` | `WorkItemType.Task` (parse failure) | H4: Reachable for whitespace-only type names — `IsNullOrEmpty` vs. `IsNullOrWhiteSpace` guard gap |
| 3 | `TreeNavigatorView.cs:266` | `CanExpand()` | `"Task"` string literal | Medium: Custom leaf types always expand |
| 4 | `BranchNamingService.cs` | `DefaultTypeMap` | 10 known type mappings | Low: Unknown types use slugified raw name |
| 5 | `CommitMessageService.cs` | `DefaultTypeMap` | 10 known type mappings | Low: Unknown types use lowercased raw name |
| 6 | `IconSet.cs` | `ResolveTypeBadge()` | 13 known type names | Low: Unknown types use first-char fallback |

### Call-Site Audit: Template Detection

| # | File | Location | Issue |
|---|------|----------|-------|
| 1 | `AdoIterationService.cs:65-89` | `DetectTemplateNameAsync()` | H2: Heuristic based on type names; custom → "Basic" |
| 2 | `InitCommand.cs:162` | Calls `DetectTemplateNameAsync()` | Stores result in `config.ProcessTemplate` |
| 3 | `StatusFieldsConfig.cs:32` | `ProcessTemplateDefaults` | Uses template name for field curation — depends on accurate detection |

### Call-Site Audit: Hardcoded State Names

| # | File | Location | Hardcoded Value | Impact |
|---|------|----------|----------------|--------|
| 1 | `HintEngine.cs:85` | `GetHints()` — sibling-completion hint | `"Closed"` string literal in hint text | H3: Basic-process users see invalid state name "Closed" instead of "Done" |

> **Note:** This is separate from the null-entry issue at HintEngine.cs:66/75 (rows 14–15 in the Resolve() audit). The null-entry issue causes wrong *classification*; this hardcoded string causes a wrong *suggestion*. Both are fixed in #1350.

### Field Reference Name Audit Scope

> The process-agnostic instructions prohibit hardcoding field reference names. An audit of all `System.*` and `Microsoft.VSTS.*` references in production code (`src/`) identified ~142 occurrences across `AdoResponseMapper.cs`, `FieldImportFilter.cs`, `StatusFieldsConfig.cs`, and `SeedEditorFormat.cs`. These are **intentionally scoped out** of this Epic per the "configurable defaults" acceptable pattern in the [process-agnostic instructions](../../.github/instructions/process-agnostic.instructions.md) (lines 44–46): field reference names are used for ADO REST API field selection and response mapping (e.g., `System.State`, `System.Title`), which are protocol-level constants — not process-template-specific assumptions. They are analogous to column names in a database schema, not to state or type names that vary per process template. A future Epic could introduce a field-reference abstraction if ADO ever supports per-process field aliasing, but no such mechanism exists today.

---

## Problem Statement

1. **Wrong progress counts (H1):** `StatusCommand.ComputeChildProgress()` and `SetCommand.ComputeChildProgress()` pass `null` to `StateCategoryResolver.Resolve()`, forcing the hardcoded fallback. Custom state names (e.g., "Review", "Testing", "UAT") return `StateCategory.Unknown`, so items in those states are never counted as done — even when their process config marks them as `Completed`.

2. **Custom template misidentification (H2):** `DetectTemplateNameAsync()` infers template by checking for "User Story" (Agile), "Product Backlog Item" (Scrum), or "Requirement" (CMMI). Custom templates inheriting from Agile that add types but don't include "User Story" are misidentified as "Basic", causing incorrect status field defaults.

3. **Invalid state hints (H3):** `HintEngine` hardcodes `"twig state Closed"` in the sibling-completion hint. Basic-process users see "twig state Closed" when their valid state is "Done". The hint also uses null entries for `StateCategoryResolver.Resolve()`, so custom states are miscategorized.

4. **Silent type corruption (H4):** `AdoResponseMapper.ParseWorkItemType()` has two issues. First, the guard uses `string.IsNullOrEmpty()` while `WorkItemType.Parse()` uses `string.IsNullOrWhiteSpace()` — whitespace-only type names slip through the guard and hit the `Task` fallback. Second, the `!IsSuccess` fallback maps any unrecognized type to `Task`, masking custom type names. In practice, `Parse()` accepts any non-whitespace string so custom types are preserved, but the asymmetric validation and misleading fallback are correctness hazards.

---

## Goals and Non-Goals

### Goals

1. **Zero null-entry calls in production code:** Every `StateCategoryResolver.Resolve()` call passes actual `StateEntry[]` from process configuration, or explicitly handles the "no config available" case.
2. **Correct progress counts:** `ComputeChildProgress` in both `StatusCommand` and `SetCommand` correctly classifies custom states.
3. **Process-accurate hints:** `HintEngine` suggests valid state names from the active item's process config.
4. **Honest type parsing:** `AdoResponseMapper.ParseWorkItemType()` preserves unknown type names rather than silently converting to Task.
5. **API-based template detection:** `DetectTemplateNameAsync()` uses the ADO projects API (`capabilities.processTemplate.templateName`) when available, falling back to the heuristic only on API failure.
6. **Process-agnostic tree expansion:** `TreeNavigatorView.CanExpand()` fallback defaults to "has children" (expandable) rather than hardcoding `"Task"` as the sole leaf type.

### Non-Goals

1. **Removing `FallbackCategory()` entirely:** The fallback serves as a graceful degradation path for offline/pre-init scenarios. It stays; enforcement against reaching it in normal paths is via call-site fixes (#1346, #1350), not compile-time attributes.
2. **Removing `DefaultTypeMap` from `BranchNamingService`/`CommitMessageService`:** These maps are configurable starting points with adequate unknown-type fallbacks. The plan documents them as cosmetic and adds tests for unknown type degradation.
3. **Removing `IconSet.ResolveTypeBadge()` switch:** The switch is cosmetic (rendering) with a first-char fallback. Acceptable per process-agnostic instructions.
4. **Removing `ProcessTemplateDefaults` from `StatusFieldsConfig`:** These curated field sets improve UX for first-time setup. They use template name as a key (not type names), so they depend on *accurate* template detection (#1349) but don't need removal.
5. **Rewriting the process config infrastructure:** The existing `IProcessConfigurationProvider` → `ProcessConfiguration` → `TypeConfig` → `StateEntry` chain is well-designed. We thread it through more components, not replace it.
6. **Abstracting field reference names** (e.g., `System.State`, `Microsoft.VSTS.Scheduling.StoryPoints`)**:** These are ADO REST API protocol-level constants, not process-template-specific assumptions. Acceptable per the "configurable defaults" pattern in the [process-agnostic instructions](../../.github/instructions/process-agnostic.instructions.md) (lines 44–46). See §Background → *Field Reference Name Audit Scope* for full rationale.

---

## Requirements

### Functional

- **FR-01:** `ComputeChildProgress` must look up `TypeConfig.StateEntries` for each child's type and pass them to `StateCategoryResolver.Resolve()`.
- **FR-02:** When process config is unavailable (e.g., pre-init, offline), `ComputeChildProgress` must fall back gracefully to the existing heuristic (pass null) rather than throwing.
- **FR-03:** `HintEngine.GetHints("state", ...)` must resolve the completed-state name dynamically from the active item's `TypeConfig.StateEntries` instead of hardcoding "Closed".
- **FR-04:** `HintEngine` must pass `StateEntry[]` to `StateCategoryResolver.Resolve()` for both the new state and sibling state checks.
- **FR-05:** `AdoResponseMapper.ParseWorkItemType()` must align its guard to `string.IsNullOrWhiteSpace()` (matching `WorkItemType.Parse()`) and preserve the original type name for unknown types rather than mapping to `Task`.
- **FR-06:** `TreeNavigatorView.CanExpand()` fallback must default to `true` (expandable) for unknown types, not check against `"Task"`.
- **FR-07:** `DetectTemplateNameAsync()` must call `GET {org}/_apis/projects/{project}?includeCapabilities=true&api-version=7.1` and extract `capabilities.processTemplate.templateName`.
- **FR-08:** Template detection must fall back to the existing heuristic when the API call fails.

### Non-Functional

- **NFR-01:** All changes must be AOT-compatible (no reflection, all JSON via `TwigJsonContext`).
- **NFR-02:** `TreatWarningsAsErrors=true` must pass — no new warnings.
- **NFR-03:** Each Issue must include xUnit tests covering Basic (To Do/Doing/Done), Agile (New/Active/Resolved/Closed), and graceful degradation (null provider) using `ProcessConfigBuilder`.
- **NFR-04:** Telemetry must not emit process-specific data (state names, type names, template names).

---

## Proposed Design

### Architecture Overview

The design threads `IProcessConfigurationProvider` (already registered as a singleton) through 4 additional consumers, using a shared `SafeGetConfiguration()` extension to centralize the try/catch/null-check pattern:

```
IProcessConfigurationProvider (singleton, DI)
    │
    ├─ ProcessConfigExtensions.SafeGetConfiguration()  ← new shared helper
    │       Centralizes try/catch/null-check for graceful degradation
    │
    ├─ StatusCommand ──── ComputeChildProgress() uses TypeConfig.StateEntries
    ├─ SetCommand ──────── ComputeChildProgress() uses TypeConfig.StateEntries
    ├─ HintEngine ──────── GetHints() resolves completed state dynamically
    └─ AdoIterationService ── DetectTemplateNameAsync() calls Projects API
```

For `HintEngine`, the provider is injected as `IProcessConfigurationProvider?` (nullable) because hints are non-critical and must degrade gracefully. For `StatusCommand` and `SetCommand`, the provider is also nullable to maintain backward compatibility with tests and offline scenarios. All three consumers use `provider.SafeGetConfiguration()` (see §Key Component 0) to avoid duplicating the error-handling pattern.

> **Implementation notes:**
> - The `src/Twig.Domain/Extensions/` directory does not exist yet and must be created for `ProcessConfigExtensions.cs`.
> - ConsoleAppFramework's source-gen DI resolves nullable constructor parameters (`T?`) to `null` when no matching service is registered. Both `StatusCommand` and `SetCommand` already use this pattern successfully (e.g., `RenderingPipelineFactory?`, `ITelemetryClient?`), so adding `IProcessConfigurationProvider?` follows the established convention.
> - **Existing test compatibility:** The new `IProcessConfigurationProvider?` parameter must be added at the **end** of each primary constructor with `= null` default, after all existing nullable parameters. This ensures existing test constructor calls (which use positional arguments and omit trailing optional parameters) continue to compile without modification. Verified: `StatusCommandTests` passes 10 positional args (trailing 6 optional params omitted), `SetCommandTests` passes 7 positional args (trailing 7 optional params omitted), `HintEngineTests` passes only `DisplayConfig` — all continue to compile when a new trailing `= null` parameter is appended. Factory methods like `CreateCommandWithGit()` use named parameters for specific trailing args and are also unaffected.

### Key Components

#### 0. Shared Helper — SafeGetConfiguration Extension

**Motivation:** The try/catch/null-check pattern for `IProcessConfigurationProvider?` is needed in 3 consumers (StatusCommand, SetCommand, HintEngine). Per technical review feedback, centralizing this avoids copy-paste bugs and ensures consistent error handling.

**Proposed:** Add an extension method on `IProcessConfigurationProvider?` in a new `Twig.Domain.Extensions` namespace (new `src/Twig.Domain/Extensions/` directory). The namespace keeps cross-project extension helpers separate from domain service types:

```csharp
namespace Twig.Domain.Extensions;

internal static class ProcessConfigExtensions
{
    /// <summary>
    /// Safely retrieves the process configuration, returning null on failure.
    /// Centralizes the try/catch/null-check pattern for components that
    /// must degrade gracefully when process config is unavailable (pre-init, offline).
    /// </summary>
    internal static ProcessConfiguration? SafeGetConfiguration(
        this IProcessConfigurationProvider? provider)
    {
        if (provider is null) return null;
        try { return provider.GetConfiguration(); }
        catch { return null; }
    }
}
```

All consumers call `provider.SafeGetConfiguration()` and handle the `null` return as "no config available — use fallback."

#### 1. StatusCommand / SetCommand — ComputeChildProgress Fix

**Current:** `ComputeChildProgress` is a `private static` method that iterates children and calls `StateCategoryResolver.Resolve(child.State, null)`.

**Proposed:** Change from `static` to instance method. Accept `IProcessConfigurationProvider?` via the constructor. Use `SafeGetConfiguration()` to obtain config. For each child, look up `TypeConfig` by the child's `WorkItemType` and pass `StateEntries` to `Resolve()`. If provider is null or type not found, fall back to null entries.

```csharp
private (int Done, int Total)? ComputeChildProgress(IReadOnlyList<WorkItem> children)
{
    if (children.Count == 0) return null;

    var config = processConfigProvider.SafeGetConfiguration();

    var done = 0;
    foreach (var child in children)
    {
        IReadOnlyList<StateEntry>? entries = null;
        if (config is not null && config.TypeConfigs.TryGetValue(child.Type, out var tc))
            entries = tc.StateEntries;

        // Intentional: if child.State is not found in entries (e.g., state added
        // after last twig refresh), Resolve() falls through to FallbackCategory().
        var cat = StateCategoryResolver.Resolve(child.State, entries);
        if (cat is StateCategory.Resolved or StateCategory.Completed)
            done++;
    }
    return (done, children.Count);
}
```

#### 2. HintEngine — Dynamic State Suggestions

**Current:** Constructs hints with hardcoded `"twig state Closed"` and passes null entries.

**Proposed:** Accept `IProcessConfigurationProvider?` via constructor. In the `"state"` case, use `SafeGetConfiguration()`:
1. Look up the active item's `TypeConfig` to get `StateEntries`.
2. Pass entries to both `StateCategoryResolver.Resolve()` calls.
3. Find the completed-state name dynamically: scan `StateEntries` for the first entry with `StateCategory.Completed`.
4. Build the hint string using the actual state name.

```csharp
var config = processConfigProvider.SafeGetConfiguration();
TypeConfig? typeConfig = null;
if (config is not null && item is not null)
    config.TypeConfigs.TryGetValue(item.Type, out typeConfig);
var entries = typeConfig?.StateEntries;

var completedStateName = "Done"; // safe default — "Done" is valid in Basic, Scrum, and CMMI; "Closed" is only valid in Agile
if (typeConfig is not null)
{
    var completedEntry = typeConfig.StateEntries
        .FirstOrDefault(e => e.Category == StateCategory.Completed);
    if (completedEntry.Name is not null)
        completedStateName = completedEntry.Name;
}
hints.Add($"All sibling tasks complete. Consider: twig up then twig state {completedStateName}");
```

> **Implementer note:** `StateEntry` is a `readonly record struct`. `FirstOrDefault()` on `IReadOnlyList<StateEntry>` returns `default(StateEntry)` with `Name = null`, `Category = StateCategory.Unknown`, `Color = null`. The `completedEntry.Name is not null` guard above handles this correctly. Do not replace it with a `Category`-based check without accounting for the default struct value.

#### 3. AdoResponseMapper — Honest Type Parsing

**Current:** `ParseWorkItemType(null) → Task`, `ParseWorkItemType("CustomType") → Parse succeeds, returns custom type`. The guard uses `string.IsNullOrEmpty()` while `Parse()` uses `string.IsNullOrWhiteSpace()` — a whitespace-only input (e.g., `"  "`) passes the guard but fails `Parse()`, hitting the `Task` fallback. While ADO never sends whitespace-only type names, the asymmetric validation is a correctness hazard.

**Proposed:** Align the guard with `Parse()`'s validation by using `IsNullOrWhiteSpace`. The `IsNullOrWhiteSpace` guard makes the `!IsSuccess` branch unreachable — remove it rather than keeping dead code:

```csharp
private static WorkItemType ParseWorkItemType(string? typeName)
{
    if (string.IsNullOrWhiteSpace(typeName))
        return WorkItemType.Task; // ADO always provides type; this is defensive
    return WorkItemType.Parse(typeName).Value;
}
```

This preserves custom type names (e.g., "Deliverable" stays as "Deliverable" rather than becoming "Task") and eliminates the validation asymmetry between the guard and `Parse()`.

#### 4. TreeNavigatorView — Process-Config-First Expansion

**Current fallback:** `return model.WorkItem.Type.Value is not "Task"` — only "Task" is a leaf.

**Proposed fallback:** Default to expandable (`true`) for unknown types, since incorrectly showing an expand arrow for a leaf is a cosmetic issue, but incorrectly hiding children is data loss:

```csharp
// Fallback: default to expandable (showing an expand arrow for a leaf is harmless;
// hiding children of an unknown parent type is data loss)
return true;
```

#### 5. DetectTemplateNameAsync — ADO Projects API

**Current:** Type-name heuristic only.

**Proposed:** Call `GET {org}/_apis/projects/{project}?includeCapabilities=true&api-version=7.1` which returns `capabilities.processTemplate.templateName`. This requires:
1. Extending `AdoProjectResponse` DTO to include `capabilities` nested structure.
2. Adding a new private method to `AdoIterationService`.
3. Falling back to the heuristic on API failure.

```csharp
public async Task<string?> DetectTemplateNameAsync(CancellationToken ct = default)
{
    // Try API-based detection first
    try
    {
        var url = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}" +
                  $"?includeCapabilities=true&api-version={ApiVersion}";
        using var response = await SendAsync(url, ct);
        var project = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(ct),
            TwigJsonContext.Default.AdoProjectWithCapabilitiesResponse, ct);
        if (project?.Capabilities?.ProcessTemplate?.TemplateName is { Length: > 0 } name)
            return name;
    }
    catch { /* fall through to heuristic */ }

    // Fallback: heuristic based on type names
    return await DetectTemplateNameByHeuristicAsync(ct);
}
```

> **Implementer note — ADO JSON casing and DTO convention:** The ADO projects API returns camelCase properties (`capabilities`, `processTemplate`, `templateName`). While `TwigJsonContext`'s global `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` handles the PascalCase→camelCase mapping automatically, **every existing DTO in the codebase uses explicit `[JsonPropertyName]` attributes** (verified: `AdoProjectResponse`, `AdoWorkItemResponse`, `AdoIterationResponse`, `AdoProcessConfigurationResponse`, and all other DTOs in `src/Twig.Infrastructure/Ado/Dtos/`). The new `AdoProjectWithCapabilitiesResponse` DTO **must follow the same convention** with explicit `[JsonPropertyName("capabilities")]`, `[JsonPropertyName("processTemplate")]`, etc. for consistency and resilience against naming-policy changes. Verify with a round-trip test (T1349.2) to confirm correct deserialization.

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Nullable `IProcessConfigurationProvider?` for HintEngine, StatusCommand, SetCommand** | These components must work pre-init (no SQLite yet). Required injection was rejected because `GetConfiguration()` throws pre-init; null-object pattern was rejected because consumers must distinguish "config exists but type not found" from "no config at all" — an empty TypeConfigs dict would mask that distinction. Nullable injection with `SafeGetConfiguration()` makes the "not available" state explicit at the type level. |
| **Instance method (not static) for `ComputeChildProgress`** | Needs access to the DI-injected provider. Both StatusCommand and SetCommand already use primary constructors, so accessing the provider is natural. |
| **Default to expandable in TreeNavigatorView fallback** | Showing an expand arrow on a leaf is cosmetic; hiding children of an unknown parent is functional data loss. |
| **Keep `FallbackCategory()` without `[Obsolete]`** | Removing it would break offline/pre-init. `[Obsolete]` was evaluated and rejected: the method is intentionally reachable inside `Resolve()` when entries are provided but the state name is not found (stale config, new ADO state). Marking it `[Obsolete]` would trigger `TreatWarningsAsErrors` inside `Resolve()` itself. See §Alternatives Considered for full evaluation. The call-site audit + fixes in #1346 and #1350 are the enforcement mechanism. |
| **Create sibling `AdoProjectWithCapabilitiesResponse` DTO** | `AdoProjectResponse` is a minimal DTO (`id` + `name`) used by `AdoGitClient.GetProjectIdAsync()`. Extending it with nested capabilities would change its shape for all consumers and require broader test updates. A sibling type isolates the new fields to the single call site in `AdoIterationService`. |
| **Nullable entries parameter stays in `StateCategoryResolver.Resolve()` signature** | The method signature is correct — null entries trigger the fallback, which is the intended behavior for degradation scenarios. The fix is at the *call sites*, not the resolver. |
| **Shared `SafeGetConfiguration()` extension** | Centralizes the try/catch/null-check pattern duplicated across 3 consumers (StatusCommand, SetCommand, HintEngine). The extension is discoverable via IntelliSense on nullable `IProcessConfigurationProvider?` references. |

---

## Alternatives Considered

### Null-Object Pattern vs. Nullable Injection for IProcessConfigurationProvider

Three consumers (StatusCommand, SetCommand, HintEngine) need optional access to process configuration. Two approaches were evaluated:

**Option A: Null-object pattern** — Register an `EmptyProcessConfigProvider` returning a `ProcessConfiguration` with empty `TypeConfigs`.
- **Pro:** Consumers avoid null checks; simpler call-site code.
- **Con:** Empty `TypeConfigs` dictionary masks the distinction between "config exists but this type is absent" and "no config at all" — consumers cannot determine whether they are in a pre-init/offline scenario or encountering an unknown type.
- **Con:** Would silently fall through to `FallbackCategory()` without making the degradation explicit in the type system.

**Option B: Nullable injection with `SafeGetConfiguration()` extension** *(chosen)*
- **Pro:** Makes the "not available" state explicit at the type level (`null` vs. empty config).
- **Pro:** Follows established pattern in the codebase (`ITelemetryClient?`, `RenderingPipelineFactory?` in StatusCommand/SetCommand).
- **Pro:** `SafeGetConfiguration()` centralizes the try/catch/null-check, avoiding code duplication.
- **Con:** Requires null handling at each call site (mitigated by the shared extension).

**Decision:** Option B. The semantic distinction between "no provider" and "provider with missing type" is important for diagnostics and future logging. Nullable injection with a shared extension gives explicit degradation with minimal boilerplate.

### `[Obsolete]` Attribute on `FallbackCategory()`

To prevent future regressions (new call sites passing null entries), two enforcement approaches were evaluated:

**Option A: Mark `FallbackCategory()` as `[Obsolete]`** — Generate compile-time warnings on any direct call.
- **Pro:** Surfaces accidental usage during code review via compiler warning.
- **Con:** `FallbackCategory()` is called from *within* `Resolve()` itself — on the legitimate fall-through path when entries are provided but the state name is not found (stale config, new ADO state added post-refresh). `[Obsolete]` would generate a warning inside `Resolve()`.
- **Con:** `TreatWarningsAsErrors=true` means the `[Obsolete]` warning breaks the build unless suppressed with `#pragma`, defeating the purpose and adding noise.

**Option B: No attribute; enforce via call-site fixes and process** *(chosen)*
- **Pro:** `FallbackCategory()` remains available for its legitimate degradation purpose inside `Resolve()`.
- **Pro:** Call-site audit (§Background) + fixes in #1346 and #1350 eliminate all known null-entry paths.
- **Con:** No compile-time guard against *future* null-entry call sites (mitigated by code review, the [process-agnostic instructions](../../.github/instructions/process-agnostic.instructions.md), and the T1347.V verification grep).

**Decision:** Option B. The method serves a legitimate purpose as a fallback within `Resolve()`. Enforcement is via call-site audit, not compile-time attributes.

## Dependencies

### External
- **ADO REST API** `GET /_apis/projects/{project}?includeCapabilities=true&api-version=7.1` — returns `capabilities.processTemplate.templateName`. The projects endpoint is already partially consumed by `AdoGitClient.GetProjectIdAsync()`.

### Internal
- **`IProcessConfigurationProvider`** — already registered as singleton in `TwigServiceRegistration.cs`
- **`ProcessConfigBuilder`** in `Twig.TestKit` — already supports Basic/Agile/Scrum/CMMI factory methods
- **`TwigJsonContext`** — must add `[JsonSerializable]` for any new DTOs

### Sequencing

| Issue | Depends On | Rationale |
|-------|-----------|-----------|
| **#1346** (Progress fix) | — | Independent, highest priority. Creates `SafeGetConfiguration()` extension used by downstream Issues. |
| **#1348** (Type assumptions) | — | Independent. Can proceed in parallel with #1346. |
| **#1349** (API detection) | — | Independent, lowest priority. Can proceed in parallel. |
| **#1350** (HintEngine) | #1346 | Uses `SafeGetConfiguration()` extension created in T1346.1. Source file modifications are disjoint (HintEngine.cs, CommandServiceModule.cs vs. StatusCommand.cs, SetCommand.cs). Includes final null-entry verification grep. |

---

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|------------|------|
| `ProcessConfigExtensions` (new) | New shared extension method for graceful config access | Low — 10 LoC, pure utility |
| `StatusCommand` | Add DI parameter, change static → instance | Low — isolated method |
| `SetCommand` | Add DI parameter, change static → instance | Low — identical pattern |
| `HintEngine` | Add DI parameter, modify hint generation | Low — hints are non-critical |
| `CommandServiceModule` | Update `HintEngine` registration | Low — add provider parameter |
| `CommandRegistrationModule` | No change (StatusCommand/SetCommand auto-resolve) | None |
| `AdoResponseMapper` | Simplify `ParseWorkItemType` | Low — behavior unchanged for valid types |
| `TreeNavigatorView` | Change fallback default | Low — cosmetic |
| `AdoIterationService` | Add API call, restructure method | Medium — external API dependency |
| `AdoProjectWithCapabilitiesResponse` DTO (new) | New sibling DTO with capabilities properties | Low — additive, isolated to detection call site |
| `TwigJsonContext` | Add serializable attributes for new DTOs | Low — additive |

### Backward Compatibility
- All public method signatures change additively (new optional/nullable parameters)
- Existing tests continue to work with null providers (graceful degradation)
- No breaking changes to CLI command behavior — output improves

---

## Security Considerations

Issue #1349's new `GET /_apis/projects/{project}?includeCapabilities=true` call uses the existing PAT-based `HttpClient` (no new credentials, no PAT scope escalation — `vso.project` is already required by `twig init`). Failures fall through to the heuristic. Per NFR-04, the template name is not emitted in telemetry.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|:---:|:---:|------------|
| Process config unavailable at `ComputeChildProgress` call time | Low | Medium | Null-check with try/catch; falls back to existing heuristic |
| ADO projects API returns different structure than expected | Low | Low | Heuristic fallback preserved; API result is best-effort |
| `WorkItemType.Parse()` behavior changes in future | Low | Medium | Explicit null guard before calling Parse; test for custom types |
| Test coverage gaps for custom process templates | Medium | Medium | Add custom-template `[Theory]` data alongside standard templates |
| DI registration change breaks existing test setup | Low | Low | StatusCommand/SetCommand use auto-resolution; only HintEngine factory changes |
| Stale `ProcessConfiguration` after ADO state additions | Medium | Low | States added in ADO after the last `twig init`/`twig refresh` are absent from `TypeConfig.StateEntries`. `Resolve()` falls through to `FallbackCategory()` for these states — this is intentional graceful degradation (see §Background → Current Architecture). Running `twig refresh` picks up the new states. A future improvement could detect and warn on unknown states. |

---

## Open Questions

All design questions resolved. See §Alternatives Considered and §Design Decisions.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Extensions/ProcessConfigExtensions.cs` | `SafeGetConfiguration()` extension — shared try/catch/null-check helper |
| `tests/Twig.Cli.Tests/Commands/StatusCommandProgressTests.cs` | Focused tests for `ComputeChildProgress` with process config; includes `SafeGetConfiguration()` edge-case tests (null provider, throwing provider) |
| `tests/Twig.Cli.Tests/Commands/SetCommandProgressTests.cs` | Focused tests for `ComputeChildProgress` with process config |
| `tests/Twig.Cli.Tests/Hints/HintEngineProcessConfigTests.cs` | Tests for dynamic state hints |
| `src/Twig.Infrastructure/Ado/Dtos/AdoProjectCapabilitiesDto.cs` | Contains `AdoProjectWithCapabilitiesResponse` and nested capability DTOs |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/StatusCommand.cs` | Add `IProcessConfigurationProvider?`, change `ComputeChildProgress` to instance |
| `src/Twig/Commands/SetCommand.cs` | Add `IProcessConfigurationProvider?`, change `ComputeChildProgress` to instance |
| `src/Twig/Hints/HintEngine.cs` | Add `IProcessConfigurationProvider?`, dynamic state lookup, pass entries |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Update HintEngine factory to inject provider |
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Align guard to `IsNullOrWhiteSpace` in `ParseWorkItemType` to preserve unknown types |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Add API-based detection, restructure as fallback chain |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable]` for new DTOs |
| `src/Twig.Tui/Views/TreeNavigatorView.cs` | Change fallback from `is not "Task"` to `true` |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Add tests for unknown type preservation (custom types, whitespace guard) |
| `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` | Add tests for API-based detection |
| `tests/Twig.Domain.Tests/Services/StateCategoryResolverTests.cs` | Add `[Theory]` rows for Basic/Agile/Scrum/CMMI Resolve-with-entries (formerly T1347.2) |

---

## ADO Work Item Structure

### Epic #1345: Process-agnostic architecture

> **Ordering note:** Issues below are listed by **implementation priority** (not issue number): #1346 → #1350 → #1348 → #1349. This matches the dependency sequencing in §Dependencies and the PR Group execution order.

---

### Issue #1346: StatusCommand.ComputeChildProgress must use process config state entries

**Goal:** Fix the highest-severity bug — progress indicators showing wrong counts in non-Basic workspaces.

**Prerequisites:** None (first in sequence)

**Tasks:**

**T1346.1** _(M)_ — Create shared config extension and wire StatusCommand to process config.
- Create `ProcessConfigExtensions.SafeGetConfiguration()` extension in new `src/Twig.Domain/Extensions/` directory (directory does not exist yet — must be created)
- Add `IProcessConfigurationProvider? processConfigProvider = null` at the **end** of `StatusCommand` primary constructor (after existing optional params — existing test constructor calls omit trailing defaults and compile without modification)
- Convert `ComputeChildProgress` from `private static` to `private` instance method
- Use `SafeGetConfiguration()` to look up `TypeConfig.StateEntries` per child type
- **Files:** `src/Twig.Domain/Extensions/ProcessConfigExtensions.cs`, `src/Twig/Commands/StatusCommand.cs`
- **Implements:** FR-01, FR-02

**T1346.2** _(S)_ — Apply identical pattern to SetCommand.
- Add `IProcessConfigurationProvider? processConfigProvider = null` at end of constructor, convert `ComputeChildProgress` to instance method, use `SafeGetConfiguration()`
- **File:** `src/Twig/Commands/SetCommand.cs`
- **Implements:** FR-01, FR-02

**T1346.3** _(M)_ — Write progress, resolver, and extension tests across templates.
- StatusCommand progress tests: Basic (To Do/Doing/Done → correct counts), Agile (New/Active/Resolved/Closed → correct counts), custom state ("UAT"), null provider fallback
- SetCommand progress tests: same matrix as StatusCommand
- `StateCategoryResolver` smoke rows: add `[Theory]` data covering Basic "Done", Agile "Closed", Scrum "Done", CMMI "Resolved" with entries _(formerly T1347.2 — folded here)_
- **Direct `SafeGetConfiguration()` tests:** null provider returns null, provider that throws returns null
- **Files:** `tests/Twig.Cli.Tests/Commands/StatusCommandProgressTests.cs` (also covers `SafeGetConfiguration()` edge cases: null provider, throwing provider), `tests/Twig.Cli.Tests/Commands/SetCommandProgressTests.cs`, `tests/Twig.Domain.Tests/Services/StateCategoryResolverTests.cs`
- **Validates:** NFR-03

**Acceptance Criteria:**
- [ ] `ComputeChildProgress` passes `StateEntries` from process config to `Resolve()`
- [ ] Agile workspace with 4/5 children in "Closed" state shows `4/5` progress (not `0/5`)
- [ ] Basic workspace with 3/3 children in "Done" state shows `3/3` progress
- [ ] Null provider gracefully falls back to heuristic (no crash, no exception)
- [ ] All existing StatusCommand and SetCommand tests pass without modification

---

### Issue #1350: HintEngine must use process config for state suggestions

**Goal:** Replace hardcoded "Closed" hint with dynamic state name from process config; use actual state entries for category resolution.

**Prerequisites:** #1346 (uses `SafeGetConfiguration()` extension created in T1346.1; source file modifications are disjoint — dependency is on the shared extension method, not on the command files)

**Tasks:**

**T1350.1** _(M)_ — Wire HintEngine to process config for dynamic state resolution.
- Add `IProcessConfigurationProvider? processConfigProvider = null` as second constructor parameter to `HintEngine` (after `DisplayConfig` — existing test calls pass only `DisplayConfig` and compile without modification)
- Use `SafeGetConfiguration()` to look up active item's `TypeConfig`
- Pass `StateEntries` to both `StateCategoryResolver.Resolve()` calls (new state + sibling states)
- Replace hardcoded `"twig state Closed"` with dynamic completed-state name: scan `StateEntries` for first `StateCategory.Completed` entry, default to "Done"
- Update `CommandServiceModule` HintEngine factory to inject `IProcessConfigurationProvider` from DI container
- **Files:** `src/Twig/Hints/HintEngine.cs`, `src/Twig/DependencyInjection/CommandServiceModule.cs`
- **Implements:** FR-03, FR-04

**T1350.2** _(M)_ — Write hint engine process config tests.
- Basic hint says "Done", Agile hint says "Closed", Scrum hint says "Done"
- Null provider falls back to "Done" (safe default)
- Disabled hints still return empty
- **File:** `tests/Twig.Cli.Tests/Hints/HintEngineProcessConfigTests.cs`
- **Validates:** NFR-03

**Acceptance Criteria:**
- [ ] HintEngine passes `StateEntries` to `StateCategoryResolver.Resolve()` (no null in normal path)
- [ ] Basic-process users see `"twig state Done"` in sibling-completion hint
- [ ] Agile-process users see `"twig state Closed"` in sibling-completion hint
- [ ] Null provider uses "Done" as safe default (not "Closed")
- [ ] Existing HintEngine tests pass with updated constructor (backward compat via default param)
- [ ] Null-entry verification: `rg "Resolve\(" src/ --glob "*.cs"` piped through manual review confirms all `Resolve()` calls pass actual `StateEntry[]` or use `SafeGetConfiguration()`. Note: literal-null grep (`rg "Resolve\(.*null\)"`) is run as a quick check but may miss null passed via a variable — the manual review of all `Resolve()` call sites (17 total, per §Background audit) is the authoritative verification.
- [ ] `dotnet build --no-restore` produces zero warnings

---

### Issue #1348: Eliminate hardcoded work item type assumptions across codebase

**Goal:** Unknown work item types are preserved faithfully; tree expansion defaults to expandable; type maps degrade gracefully for unknown types.

**Prerequisites:** None (independent of state-related Issues)

**Tasks:**

**T1348.1** _(S)_ — Fix `ParseWorkItemType` guard and `CanExpand` fallback.
- In `AdoResponseMapper.cs`: align guard from `IsNullOrEmpty` to `IsNullOrWhiteSpace`; remove now-dead `!IsSuccess` fallback; call `.Value` directly
- In `TreeNavigatorView.cs`: change `CanExpand` fallback from `model.WorkItem.Type.Value is not "Task"` to `true`; add brief inline comment explaining rationale
- **Files:** `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs`, `src/Twig.Tui/Views/TreeNavigatorView.cs`
- **Implements:** FR-05, FR-06

**T1348.2** _(M)_ — Write type parsing tests for custom and edge-case inputs.
- Custom type names ("Deliverable", "Initiative", "Scenario") verifying name is preserved
- Whitespace-only input (`"  "`) returns `Task` (not throws)
- Null/empty returns `Task`
- **File:** `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` (existing file — add new test cases)
- **Validates:** NFR-03 (covers whitespace edge case from tech review)

**Acceptance Criteria:**
- [ ] Custom type "Deliverable" parsed from ADO response retains name "Deliverable" (not "Task")
- [ ] Whitespace-only type name (`"  "`) returns `WorkItemType.Task` (guard catches it before `Parse()`)
- [ ] TreeNavigatorView shows expand arrow for unknown types (data preservation over cosmetics)
- [ ] `ParseWorkItemType` tests verify custom type names ("Deliverable", "Initiative", "Scenario") are preserved

---

### Issue #1349: Replace DetectTemplateNameAsync heuristic with ADO process API

**Goal:** Use the authoritative ADO projects API to detect process template name, falling back to the heuristic only on API failure.

**Prerequisites:** None (independent, lowest priority — can be done last)

**Tasks:**

**T1349.1** _(M)_ — Create capabilities DTO and restructure template detection as API-first with heuristic fallback.
- Create `AdoProjectWithCapabilitiesResponse` with nested `Capabilities.ProcessTemplate.TemplateName` properties; register in `TwigJsonContext` with `[JsonSerializable]`. Use explicit `[JsonPropertyName]` attributes on all properties (matching the convention used by all existing DTOs — see §Proposed Design → Key Component 5 implementer note).
- Add private `DetectTemplateNameByApiAsync` calling `GET /_apis/projects/{project}?includeCapabilities=true&api-version=7.1`
- Extract existing logic to `DetectTemplateNameByHeuristicAsync`
- Wire public `DetectTemplateNameAsync` to try API first, fall back on any exception
- **Files:** `src/Twig.Infrastructure/Ado/Dtos/AdoProjectCapabilitiesDto.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`, `src/Twig.Infrastructure/Ado/AdoIterationService.cs`
- **Implements:** FR-07, FR-08, NFR-01

**T1349.2** _(M)_ — Write template detection API tests.
- API returns "MyCustomProcess" → returns it
- API fails with exception → falls back to heuristic
- API returns null/empty → falls back to heuristic
- **File:** `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceTests.cs` (existing file — add new test cases)
- **Validates:** NFR-03

**Acceptance Criteria:**
- [ ] `DetectTemplateNameAsync` calls projects API first
- [ ] Custom process template "MyCustomProcess" is correctly detected (not "Basic")
- [ ] API failure gracefully falls back to existing heuristic (no crash)
- [ ] All new DTOs registered in `TwigJsonContext` with `[JsonSerializable]`
- [ ] AOT-compatible (source-gen JSON, no reflection)

---

## PR Groups

### PR Group 1: Fix progress indicators (#1346)
**Type:** Deep  
**Tasks:** T1346.1, T1346.2, T1346.3  
**Scope:** StatusCommand + SetCommand `ComputeChildProgress` computation, shared `SafeGetConfiguration()` extension (with direct unit tests), `StateCategoryResolver` smoke tests.  
**Files:** ~9  
**Prerequisite:** None (first in sequence)  

### PR Group 2: Dynamic hints (#1350)
**Type:** Deep  
**Tasks:** T1350.1, T1350.2  
**Scope:** HintEngine + CommandServiceModule DI wiring. Includes final null-entry verification: manual review of all `Resolve()` call sites confirms no null entries remain (see #1350 acceptance criteria for details).  
**Files:** ~5  
**Prerequisite:** PR Group 1 — uses `SafeGetConfiguration()` extension created in T1346.1. File-level changes are disjoint (HintEngine.cs / CommandServiceModule.cs vs. StatusCommand.cs / SetCommand.cs), but the shared extension must exist before PR Group 2 can compile.

### PR Group 3: Type assumption cleanup (#1348)
**Type:** Wide  
**Tasks:** T1348.1, T1348.2  
**Scope:** AdoResponseMapper, TreeNavigatorView  
**Files:** ~5  
**Prerequisite:** None (independent — can proceed in parallel with PR Groups 1 and 2)  

### PR Group 4: API-based template detection (#1349)
**Type:** Deep  
**Tasks:** T1349.1, T1349.2  
**Scope:** AdoIterationService, new DTOs, TwigJsonContext  
**Files:** ~4  
**Prerequisite:** None (independent — can proceed in parallel with PR Groups 1 and 2)  

---

## Appendix: Conventions

> **Legend used throughout this document:**
>
> | Label | Meaning |
> |-------|---------|
> | **H1–H4** | Severity ranking. **H1** = highest impact (wrong data shown to users), **H4** = lowest (edge-case corruption). |
> | **S** / **M** / **V** (effort) | **S**mall (≤30 LoC, single file) / **M**edium (30–100 LoC, may span files) / **V**erification (no code changes — audit and close only). |
> | **Deep** / **Wide** (PR type) | Few files with complex logic / many files with mechanical changes. |

---

## References

- [Process-agnostic instructions](../../.github/instructions/process-agnostic.instructions.md)
- [ADO Projects API — Get](https://learn.microsoft.com/en-us/rest/api/azure-devops/core/projects/get?view=azure-devops-rest-7.1) — includes `capabilities.processTemplate.templateName`
- [StateCategory enum](../../src/Twig.Domain/Enums/StateCategory.cs)
- [ProcessConfiguration aggregate](../../src/Twig.Domain/Aggregates/ProcessConfiguration.cs)
- [ProcessConfigBuilder test utility](../../tests/Twig.TestKit/ProcessConfigBuilder.cs)

---

## Revision History

- **Rev 15 (2026-04-03):** Tech review (93/100) + readability review (87/100) feedback incorporated: (1) Fixed stale "5 prioritized Issues (#1346 → #1347 → …)" in Executive Summary → now "4 prioritized Issues (#1346, #1348, #1349, #1350)". (2) Added missing HintEngine.cs:85 "Closed" hardcode to new §Background → "Call-Site Audit: Hardcoded State Names" table — 14-item count now matches audit tables. (3) Corrected DTO convention: new `AdoProjectWithCapabilitiesResponse` must use explicit `[JsonPropertyName]` attributes per codebase standard (all existing DTOs use them). (4) Improved #1350 grep acceptance criteria to acknowledge variable-based null limitation; added manual all-call-site review as authoritative check. (5) Resolved Rev 14 revision history contradiction ("eliminated #1347" vs. "moved #1347") — cleaned to single consistent narrative. (6) Moved Conventions section to Appendix to improve document flow. (7) Added Task ID references (T1346.1, T1350.1, etc.) to all PR Groups. (8) Replaced terse "No open questions remain" with resolution-pointer table. (9) Clarified phantom-rows note in Resolve() audit table intro — now explicitly lists the 13 omitted components by name and count.
- **Rev 14 (2026-04-03):** Reducer review: eliminated #1347 (verification-only issue) — folded the 2-command grep/build check into #1350's acceptance criteria and PR Group 2 prerequisites, removing 1 ADO issue and 1 PR group. Reduced SafeGetConfiguration() test matrix from 4 cases to 2. Added Open Questions section with resolution note. Improved call-site audit table intro. Added implementation-priority ordering note to ADO section header.

