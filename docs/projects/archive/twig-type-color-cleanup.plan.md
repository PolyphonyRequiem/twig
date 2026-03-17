# Type Color Resolution Cleanup — Solution Design

> **Revision**: 2 (addresses technical review feedback)  
> **Date**: 2026-03-15

---

## Executive Summary

This design consolidates duplicate type-to-color resolution logic scattered across `HumanOutputFormatter.GetTypeColor()` and `PromptCommand.ResolveColor()`, eliminates the dual `TypeAppearances` / `Display.TypeColors` config storage that creates silent-overwrite hazards, and establishes a single resolution chain grounded in ADO-provided `ProcessTypeRecord.ColorHex` as the authoritative source. The result is a single `TypeColorResolver` utility that both consumers call, a config model where `Display.TypeColors` is a user-override-only section and `TypeAppearances` is the ADO cache, and removal of the hardcoded type→color switch from the formatter.

---

## Background

### Current Architecture

Type-to-color resolution currently lives in two independent code paths:

1. **`HumanOutputFormatter.GetTypeColor()`** (`src/Twig/Formatters/HumanOutputFormatter.cs:311-335`)
   - First checks `Display.TypeColors` dictionary for a hex value → converts via `HexToAnsi.ToForeground()`
   - Falls back to a hardcoded `switch` on `type.Value.ToLowerInvariant()` mapping known types to ANSI escapes — some to colors (Epic→`\x1b[35m` Magenta, Bug→`\x1b[31m` Red, Feature→`\x1b[36m` Cyan), others to `\x1b[0m` Reset (Task, Test Case, Change Request, Review, Issue)
   - Final fallback: `DeterministicColor()` — a private method that hashes the type name to pick one of 6 ANSI colors

2. **`PromptCommand.ResolveColor()`** (`src/Twig/Commands/PromptCommand.cs:144-155`)
   - Checks `Display.TypeColors` for a hex value
   - Falls back to `TypeAppearances` list (searching by name, case-insensitive)
   - Returns `null` if neither has a value (no deterministic fallback)

### Dual Config Storage

The config file (`/.twig/config`) stores color data in two places:

| Config Path | Type | Source | Set By |
|---|---|---|---|
| `typeAppearances` | `List<TypeAppearanceConfig>` with `Name`, `Color`, `IconId` | ADO `GetWorkItemTypeAppearancesAsync()` | `InitCommand` (line 131-144), `RefreshCommand` (line 129-136) |
| `display.typeColors` | `Dictionary<string, string>` mapping type name → hex color | Bridged from `TypeAppearances` | `InitCommand` (line 141-144), `RefreshCommand` (line 133-136) |

Both `InitCommand` and `RefreshCommand` contain identical bridging code:
```csharp
// ITEM-157: Bridge TypeAppearances → Display.TypeColors so HumanOutputFormatter uses ADO hex colors.
config.Display.TypeColors = typeAppearances
    .Where(a => !string.IsNullOrEmpty(a.Color))
    .ToDictionary(a => a.Name, a => a.Color, StringComparer.OrdinalIgnoreCase);
```

### ADO Data Source

`ProcessTypeRecord.ColorHex` (`src/Twig.Domain/Aggregates/ProcessTypeRecord.cs:23`) stores the hex color from ADO in the `process_types` SQLite table (persisted via `SqliteProcessTypeStore`). This is populated from `WorkItemTypeWithStates.Color` during init/refresh alongside the `TypeAppearances` config. This is a third copy of the same data.

### The Consistency Hazard

If a user runs `twig config display.typeColors.Epic "#FF0000"` to customize a color, the next `twig refresh` overwrites `Display.TypeColors` entirely from the fresh ADO appearances data. The user's customization is silently lost. There is no merge or user-override-wins logic.

---

## Problem Statement

1. **Duplicated resolution logic** — Two independent methods (`GetTypeColor`, `ResolveColor`) resolve type colors with different fallback chains, producing inconsistent behavior (formatter shows a deterministic-hash color for unknown types; prompt returns `null`).

2. **Hardcoded type→color switch** — `GetTypeColor()` has a 9-case switch mapping known type names to ANSI escapes (some to colors, some to Reset). This is redundant now that ADO-provided hex colors are available via `Display.TypeColors`, and it can produce wrong colors for projects that use non-standard color assignments.

3. **Dual storage with overwrite hazard** — `TypeAppearances` and `Display.TypeColors` contain the same color data in different shapes. `Display.TypeColors` is fully overwritten on every init/refresh, silently destroying any user customizations.

4. **Private deterministic hash** — `DeterministicColor()` is private to `HumanOutputFormatter` and unavailable to `PromptCommand`, which returns `null` for unknown types instead.

---

## Goals and Non-Goals

### Goals

- **G-1**: Single resolution chain — one method, one fallback order, used by both `HumanOutputFormatter` and `PromptCommand`
- **G-2**: Remove the hardcoded type→color switch from `HumanOutputFormatter`
- **G-3**: Eliminate the dual-storage consistency hazard — user overrides to type colors survive `twig refresh`
- **G-4**: Make the deterministic color hash available as a shared utility for unknown/unconfigured types
- **G-5**: Maintain AOT compatibility (no reflection, source-generated JSON)
- **G-6**: Handle missing ADO data gracefully (null colors, empty config, pre-init state)

### Non-Goals

- **NG-1**: Changing how `TypeAppearances.IconId` is stored or resolved (icon handling is a separate concern, tracked in `twig-iconid-glyph-mapping.plan.md`)
- **NG-2**: Adding a `twig config display.typeColors.<type>` command (the existing `SetValue` method doesn't support nested dictionary keys; that's a separate feature)
- **NG-3**: Removing `ProcessTypeRecord.ColorHex` from the SQLite `process_types` table (it serves other purposes like icon-id-to-glyph mapping and is the authoritative ADO cache)
- **NG-4**: Changing the ANSI color rendering in `HexToAnsi` or adding 256-color fallback

---

## Requirements

### Functional Requirements

- **FR-1**: A new static `TypeColorResolver` class MUST provide a `ResolveHex(string typeName, Dictionary<string, string>? typeColors, Dictionary<string, string>? appearanceColors)` method returning a hex color string or `null`. Both dictionary parameters represent `typeName → hexColor` mappings. The `typeColors` parameter carries user overrides (`Display.TypeColors`); `appearanceColors` carries ADO-sourced colors pre-projected from `TypeAppearances` by the caller.
- **FR-2**: Resolution order MUST be: (1) `Display.TypeColors` entry → (2) `TypeAppearances` entry (via pre-projected `appearanceColors` dictionary) → (3) `null` (caller applies its own fallback).
- **FR-3**: A new static `DeterministicTypeColor.GetAnsiEscape(string typeName)` method MUST be available for callers that need a non-null ANSI color for display when no configured color exists.
- **FR-4**: `HumanOutputFormatter.GetTypeColor()` MUST be refactored to call `TypeColorResolver.ResolveHex()` then `HexToAnsi.ToForeground()`, falling back to `DeterministicTypeColor.GetAnsiEscape()` — with no hardcoded type→color switch.
- **FR-5**: `PromptCommand.ResolveColor()` MUST be refactored to call `TypeColorResolver.ResolveHex()`.
- **FR-6**: `InitCommand` and `RefreshCommand` MUST stop overwriting `Display.TypeColors`. Instead, they MUST write only to `TypeAppearances`. `Display.TypeColors` becomes a user-override-only section.
- **FR-7**: `TypeColorResolver.ResolveHex()` MUST perform case-insensitive type name matching. The resolver MUST internally create `OrdinalIgnoreCase` copies of any non-null dictionaries passed to it, so that case-insensitive matching is guaranteed regardless of caller convention. This prevents the gap where one caller wraps and another does not.

### Non-Functional Requirements

- **NFR-1**: All new code MUST be AOT-compatible — no `System.Reflection`, no runtime code generation.
- **NFR-2**: `PromptCommand` MUST NOT write to stderr (existing NFR-004 constraint).
- **NFR-3**: Resolution MUST be synchronous (no async I/O) — both callers operate in a hot path (formatter rendering, shell prompt evaluation).
- **NFR-4**: Existing tests that assert specific ANSI color codes for known types (e.g., `FormatWorkItem_ShowsTypeColor_ForEpic` asserts `\x1b[35m` Magenta) MUST be updated to reflect the new behavior.

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────┐
│       Config (JSON)         │
│  ┌────────────────────────┐ │
│  │ typeAppearances[]      │ │  ← ADO-sourced (init/refresh writes here)
│  │  {name, color, iconId} │ │
│  ├────────────────────────┤ │
│  │ display.typeColors {}  │ │  ← User overrides ONLY (never auto-written)
│  └────────────────────────┘ │
└───────────┬─────────────────┘
            │ both loaded into TwigConfiguration
            ▼
┌───────────────────────────────────────────┐
│   Callers pre-project TypeAppearances     │
│   to Dictionary<string,string> via:       │
│   .Where(a => !string.IsNullOrEmpty(      │
│     a.Color)).ToDictionary(a => a.Name,   │
│     a => a.Color)                         │
└───────────┬───────────────────────────────┘
            │
            ▼
┌──────────────────────────────────────┐
│        TypeColorResolver (static)    │
│  ResolveHex(name, typeColors,        │
│             appearanceColors)        │
│  Internally normalizes dictionaries  │
│  to OrdinalIgnoreCase                │
│  Priority: typeColors > appearances  │
└───────┬──────────────┬───────────────┘
        │              │
        ▼              ▼
┌──────────────┐  ┌──────────────────────┐
│ PromptCommand│  │ HumanOutputFormatter │
│ returns hex  │  │ hex→ANSI or          │
│ or null      │  │ DeterministicColor   │
└──────────────┘  └──────────────────────┘
```

### Key Components

#### 1. `TypeColorResolver` (new static class)

**Location**: `src/Twig.Domain/Services/TypeColorResolver.cs`

**Responsibility**: Single point of truth for resolving a type name to a hex color string. Accepts only primitive/BCL types (`string`, `Dictionary<string, string>?`) — no Infrastructure config types cross the layer boundary.

```csharp
namespace Twig.Domain.Services;

public static class TypeColorResolver
{
    /// <summary>
    /// Resolves a hex color for the given type name.
    /// Priority: typeColors (user overrides) > appearanceColors (ADO data).
    /// Returns null if no color is configured.
    /// Performs case-insensitive matching on type names by internally
    /// normalizing dictionaries to OrdinalIgnoreCase.
    /// </summary>
    public static string? ResolveHex(
        string typeName,
        Dictionary<string, string>? typeColors,
        Dictionary<string, string>? appearanceColors)
    {
        if (typeColors is not null)
        {
            var lookup = typeColors.Comparer == StringComparer.OrdinalIgnoreCase
                ? typeColors
                : new Dictionary<string, string>(typeColors, StringComparer.OrdinalIgnoreCase);
            if (lookup.TryGetValue(typeName, out var hex))
                return hex;
        }

        if (appearanceColors is not null)
        {
            var lookup = appearanceColors.Comparer == StringComparer.OrdinalIgnoreCase
                ? appearanceColors
                : new Dictionary<string, string>(appearanceColors, StringComparer.OrdinalIgnoreCase);
            if (lookup.TryGetValue(typeName, out var hex))
                return hex;
        }

        return null;
    }
}
```

**Design note**: The resolver takes `Dictionary<string, string>?` for both parameters rather than `TwigConfiguration` or `List<TypeAppearanceConfig>?`. This keeps the Domain layer free of Infrastructure config types (`TypeAppearanceConfig` is defined in `Twig.Infrastructure.Config` at `TwigConfiguration.cs:156`). Callers pre-project `TypeAppearances` to a flat dictionary via `typeAppearances?.Where(a => !string.IsNullOrEmpty(a.Color)).ToDictionary(a => a.Name, a => a.Color)`. The resolver internally normalizes dictionaries to `OrdinalIgnoreCase` to guarantee FR-7 regardless of caller convention.

#### 2. `DeterministicTypeColor` (new static class, extracted from HumanOutputFormatter)

**Location**: `src/Twig.Domain/Services/DeterministicTypeColor.cs`

**Responsibility**: Provides a deterministic ANSI 3-bit color escape for unknown/unconfigured type names.

```csharp
namespace Twig.Domain.Services;

public static class DeterministicTypeColor
{
    private static readonly string[] AnsiColors =
    [
        "\x1b[35m", // Magenta
        "\x1b[36m", // Cyan
        "\x1b[34m", // Blue
        "\x1b[33m", // Yellow
        "\x1b[32m", // Green
        "\x1b[31m", // Red
    ];

    /// <summary>
    /// Returns a deterministic 3-bit ANSI color escape for the given type name.
    /// Uses a simple hash to assign a stable color per type name.
    /// </summary>
    public static string GetAnsiEscape(string typeName)
    {
        var hash = 0;
        foreach (var c in typeName)
            hash = hash * 31 + c;

        return AnsiColors[(hash & 0x7FFFFFFF) % AnsiColors.Length];
    }
}
```

#### 3. Refactored `HumanOutputFormatter.GetTypeColor()`

The method changes from a 3-tier approach (typeColors → hardcoded switch → deterministic hash) to:

```csharp
private string GetTypeColor(WorkItemType type)
{
    var hex = TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors);
    if (hex is not null)
    {
        var trueColor = HexToAnsi.ToForeground(hex);
        if (trueColor is not null)
            return trueColor;
    }

    return DeterministicTypeColor.GetAnsiEscape(type.Value);
}
```

The constructor will also accept `TypeAppearances` from config and pre-project to a flat dictionary:

```csharp
public HumanOutputFormatter(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null)
{
    _typeColors = displayConfig.TypeColors;
    _appearanceColors = typeAppearances?
        .Where(a => !string.IsNullOrEmpty(a.Color))
        .ToDictionary(a => a.Name, a => a.Color);
    _icons = IconSet.GetIcons(displayConfig.Icons);
}
```

Note: The constructor no longer wraps `_typeColors` in an `OrdinalIgnoreCase` dictionary — `TypeColorResolver.ResolveHex()` handles case normalization internally (FR-7).

#### 4. Refactored `PromptCommand.ResolveColor()`

```csharp
private string? ResolveColor(string type)
{
    var appearanceColors = config.TypeAppearances?
        .Where(a => !string.IsNullOrEmpty(a.Color))
        .ToDictionary(a => a.Name, a => a.Color);
    return TypeColorResolver.ResolveHex(type, config.Display.TypeColors, appearanceColors);
}
```

The caller pre-projects `TypeAppearances` to a flat `Dictionary<string, string>` before passing to the resolver. The resolver normalizes both dictionaries to `OrdinalIgnoreCase` internally, so there is no case-sensitivity gap regardless of what `System.Text.Json` deserializes.

#### 5. Config Storage Changes (InitCommand + RefreshCommand)

**Remove** the `Display.TypeColors` bridging code from both commands. `TypeAppearances` continues to be written by init/refresh (it's the ADO cache). `Display.TypeColors` is no longer auto-populated — it becomes a user-only override section.

**Before** (in both InitCommand and RefreshCommand):
```csharp
// ITEM-157: Bridge TypeAppearances → Display.TypeColors so HumanOutputFormatter uses ADO hex colors.
config.Display.TypeColors = typeAppearances
    .Where(a => !string.IsNullOrEmpty(a.Color))
    .ToDictionary(a => a.Name, a => a.Color, StringComparer.OrdinalIgnoreCase);
```

**After**: These 4 lines (comment + 3-line LINQ expression) are deleted.

### Data Flow

#### Init/Refresh Flow (write path)

```
ADO API → GetWorkItemTypeAppearancesAsync()
  → config.TypeAppearances = [{ Name, Color, IconId }, ...]
  → config.SaveAsync()          // writes typeAppearances to JSON
  // display.typeColors is NOT touched — preserves user overrides
```

#### Color Resolution Flow (read path)

```
Caller: GetTypeColor("Epic") or ResolveColor("Epic")
  → Caller pre-projects typeAppearances to Dictionary<string,string> (name → color)
  → TypeColorResolver.ResolveHex("Epic", display.typeColors, appearanceColors)
     - Internally normalizes both dictionaries to OrdinalIgnoreCase
     1. typeColors["Epic"] → "FF00FF" (if user override exists)
     2. appearanceColors["Epic"] → "FF7B00" (if ADO data exists)
     3. null (no data)
  → (HumanOutputFormatter only) HexToAnsi.ToForeground(hex) or DeterministicTypeColor.GetAnsiEscape(name)
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Place `TypeColorResolver` in `Twig.Domain/Services/` (not Formatters, not Infrastructure) | Both `PromptCommand` (CLI layer) and `HumanOutputFormatter` (Formatters) need it. Domain.Services is the shared layer accessible to both. `Twig.Domain.csproj` has zero project references — the resolver must have no external type dependencies. |
| DD-2 | Keep `TypeAppearances` in config as the ADO color cache; make `Display.TypeColors` user-override-only | `TypeAppearances` carries `IconId` alongside `Color`, which is needed for icon-glyph mapping. Dropping it would lose icon data. Making `Display.TypeColors` the override layer gives users a clean separation: their customizations survive refresh. |
| DD-3 | Accept `Dictionary<string, string>?` parameters in `TypeColorResolver` — not `List<TypeAppearanceConfig>?` | `TypeAppearanceConfig` is defined in `Twig.Infrastructure.Config` (`TwigConfiguration.cs:156`). `Twig.Domain` has no project references and `Twig.Infrastructure` already depends on `Twig.Domain` — accepting `TypeAppearanceConfig` would create a circular dependency that prevents compilation. Using flat dictionaries keeps the Domain layer free of Infrastructure config types. Callers pre-project `TypeAppearances` via `typeAppearances?.Where(a => !string.IsNullOrEmpty(a.Color)).ToDictionary(a => a.Name, a => a.Color)`. |
| DD-4 | Remove the hardcoded type→color switch entirely (not keep as second fallback) | With ADO colors available via `TypeAppearances` after init, the hardcoded switch is redundant and can produce wrong colors for non-standard projects. Pre-init (no config), the deterministic hash provides adequate visual differentiation. |
| DD-5 | Extract `DeterministicColor` to `Twig.Domain/Services/` as a shared static | It needs to be callable from both `HumanOutputFormatter` (ANSI rendering) and potentially from `PromptCommand` in the future. A Domain service is the right home. It has no external type dependencies. |
| DD-6 | Pass `typeAppearances` to `HumanOutputFormatter` constructor rather than injecting `IProcessTypeStore` | Per existing decision in `twig-iconid-glyph-mapping.plan.md` (DD): the formatter is constructed in `Program.cs` where `config.TypeAppearances` is already loaded. Injecting a repository would add async initialization complexity and a SQLite dependency to the formatter. |
| DD-7 | `TypeColorResolver.ResolveHex()` internally normalizes dictionaries to `OrdinalIgnoreCase` | FR-7 mandates case-insensitive matching. Relying on caller convention is error-prone — one caller (`PromptCommand`) passes the raw `System.Text.Json`-deserialized dictionary which uses default `StringComparer.Ordinal`. The resolver checks `dictionary.Comparer` first and only creates a copy if the comparer is not already `OrdinalIgnoreCase`, minimizing unnecessary allocations. |

---

## Alternatives Considered

### Dual Storage Resolution Strategy

| Alternative | Pros | Cons | Decision |
|---|---|---|---|
| **Derive `Display.TypeColors` at read time** via a computed property on `TwigConfiguration` that merges `TypeAppearances` colors as defaults under user-specified `TypeColors` | No dual storage, fully transparent | Requires custom JSON serialization to avoid writing the computed dictionary to disk; `TwigConfiguration` is a POCO with source-gen serialization — adding computed properties risks AOT issues; `TypeAppearances` is nullable and the computed property would need defensive null checks on every access | Rejected — too much serialization complexity for the POCO model |
| **Drop `TypeAppearances` entirely, store only `Display.TypeColors`** | Simplest config, single source | Loses `IconId` data needed for nerd-font icon mapping; would require a separate config section for icons or forcing all icon data into `ProcessTypeRecord` only | Rejected — breaks icon-glyph mapping feature |
| **Merge user overrides into `TypeAppearances` during refresh** (mark entries as user-modified) | Single list, preserves overrides | Complicates the `TypeAppearanceConfig` model (needs a `isUserOverride` flag); makes refresh logic more complex; unclear what happens when ADO renames a type | Rejected — over-engineering for the current need |
| **Chosen: `TypeAppearances` = ADO cache, `Display.TypeColors` = user overrides** | Clean separation of concerns; minimal code change; `TypeColorResolver` priority gives user overrides precedence | Two config sections remain (but with distinct purposes and no overwrite hazard) | **Selected** |

### TypeColorResolver Layer Placement & Signature (Circular Dependency Resolution)

Three options were evaluated to resolve the circular dependency caused by `TypeAppearanceConfig` living in `Twig.Infrastructure.Config` while `TypeColorResolver` needs to be accessible from `Twig.Domain`:

| Alternative | Pros | Cons | Decision |
|---|---|---|---|
| **(a) Move `TypeAppearanceConfig` to `Twig.Domain`** | Conceptually valid (it's a pure POCO); enables `TypeColorResolver` to accept the list directly; no projection needed in callers | Requires updating `TwigJsonContext` registration in `Twig.Infrastructure` (source-gen); moves a config-layer type into the domain, muddying the boundary; other config types stay in Infrastructure creating inconsistency | Rejected — crosses established layering convention |
| **(b) Change `ResolveHex` to accept `Dictionary<string, string>?` for appearance colors** | Truly achieves DD-3 (no Infrastructure types in Domain); callers pre-project with a one-liner LINQ; `Twig.Domain.Tests` compiles without any Infrastructure reference; signature is maximally generic | Callers must remember to project; slight overhead of one-liner LINQ per call site (negligible for 5-15 items) | **Selected** — cleanest layer boundary |
| **(c) Place `TypeColorResolver` in `Twig.Infrastructure/Services/`** | No circular dependency; can accept `TypeAppearanceConfig` directly | `HumanOutputFormatter` in `Twig` (CLI) would need to reference `Twig.Infrastructure` (already does via csproj); but `Twig.Domain` consumers couldn't use the resolver; tests move to `Twig.Infrastructure.Tests` adding coupling | Rejected — limits future reuse from Domain layer |

---

## Dependencies

### Internal Dependencies

- **`HexToAnsi`** (`src/Twig/Formatters/HexToAnsi.cs`): Used by `HumanOutputFormatter` to convert resolved hex → ANSI escape. No changes needed.
- **`IconSet`** (`src/Twig.Domain/ValueObjects/IconSet.cs`): Not affected — icon resolution is independent of color resolution.
- **`TwigJsonContext`** (`src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`): No changes needed — `TypeAppearanceConfig` and `DisplayConfig` serialization attributes already exist.

### Sequencing Constraints

- None. This work is independent of the dynamic convergence epic (`twig-dynamic-convergence.plan.md`) and the icon-glyph mapping work (`twig-iconid-glyph-mapping.plan.md`). It can proceed in parallel.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|---|---|
| `HumanOutputFormatter` | Constructor signature changes (additive — new optional parameter). `GetTypeColor()` internal logic changes. `DeterministicColor()` private method removed. Constructor no longer wraps `_typeColors` in `OrdinalIgnoreCase` (resolver handles it). |
| `PromptCommand` | `ResolveColor()` body changes to pre-project `TypeAppearances` to a flat dictionary and delegate to `TypeColorResolver`. |
| `InitCommand` | 4 lines removed (comment + 3-line `Display.TypeColors` bridging LINQ expression, lines 141-144). |
| `RefreshCommand` | 4 lines removed (comment + 3-line `Display.TypeColors` bridging LINQ expression, lines 133-136). |
| `Program.cs` | `HumanOutputFormatter` construction updated to pass `config.TypeAppearances`. |
| Test files | Multiple test updates for changed behavior (see below). |

### Backward Compatibility

- **Config file**: Existing `display.typeColors` entries in config will continue to work as user overrides. Existing `typeAppearances` entries continue to work as ADO cache. No migration needed.
- **After next refresh**: `display.typeColors` will no longer be auto-populated. Users who relied on the auto-bridged `display.typeColors` (without their own overrides) will see colors resolve from `typeAppearances` instead — same visual result, different resolution path.
- **Pre-init state**: Before `twig init`, both `TypeAppearances` and `Display.TypeColors` are null. `DeterministicTypeColor` provides the fallback. This is a behavior change for `HumanOutputFormatter`: pre-init, known types like "Epic" currently get hardcoded Magenta; after this change, they'll get a deterministic-hash color. This is acceptable since pre-init is a transient state.

### Performance Implications

- `TypeColorResolver.ResolveHex()` performs up to two dictionary lookups. When a dictionary's comparer is already `OrdinalIgnoreCase`, no copy is made. When it is not (e.g., raw `System.Text.Json` deserialized dictionaries), a one-time `O(n)` copy is created. For `HumanOutputFormatter`, the pre-projected `_appearanceColors` is constructed once in the constructor; for `PromptCommand`, projection runs once per `ReadPromptData()` call (typically 5-15 items). This is negligible.
- No new I/O or allocations on the hot path beyond the dictionary normalization.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Existing tests assert specific 3-bit ANSI colors for known types (e.g., Epic→Magenta) that will change after removing the hardcoded switch | High | Medium | Update all affected tests to assert true-color from `TypeAppearances` when configured, or deterministic-hash color when not. Tests for the "no config" path will need new expected values. |
| Users who never ran `twig init` (edge case) see different colors for known types | Low | Low | `DeterministicTypeColor` still produces a color — just a potentially different one than the old hardcoded mapping. This is a cosmetic-only change in an uncommon scenario. |
| Config files written by older Twig versions have `display.typeColors` but no `typeAppearances` | Low | Low | `TypeColorResolver` checks `typeColors` first, so existing config files work unchanged. |

---

## Open Questions

1. **Should `Display.TypeColors` be writable via `twig config`?** Currently `SetValue()` doesn't support `display.typeColors.<type>` paths. Adding support would complement this cleanup by giving users an explicit way to set overrides. This is listed as a non-goal for this work but could be a fast follow-up.

2. **Should `TypeColorResolver` also consult `ProcessTypeRecord.ColorHex` from SQLite?** This would make the resolver authoritative even without config, but adds a SQLite dependency to the resolution path. Current design defers this — config is the faster path and is always populated after init.

---

## Implementation Phases

### Phase 1: Extract shared utilities (E-2, E-4 partial)
Create `TypeColorResolver` and `DeterministicTypeColor` in `Twig.Domain/Services/`. Write unit tests.

**Exit criteria**: Both classes exist, are tested, and compile with the rest of the solution.

### Phase 2: Refactor consumers (E-1, E-4)
Update `HumanOutputFormatter` and `PromptCommand` to use the new shared utilities. Update `Program.cs` DI wiring. Remove hardcoded switch.

**Exit criteria**: Both consumers delegate to `TypeColorResolver`. No hardcoded type→color switch remains. All existing tests updated and passing.

### Phase 3: Resolve dual storage (E-3)
Remove the `Display.TypeColors` bridging code from `InitCommand` and `RefreshCommand`. Update affected tests.

**Exit criteria**: Init/refresh no longer write to `Display.TypeColors`. Existing config files continue to work. All tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|---|---|
| `src/Twig.Domain/Services/TypeColorResolver.cs` | Shared hex color resolution: typeColors → appearanceColors → null. Accepts only `Dictionary<string, string>?` parameters (no Infrastructure types). |
| `src/Twig.Domain/Services/DeterministicTypeColor.cs` | Deterministic ANSI color hash for unconfigured types |
| `tests/Twig.Domain.Tests/Services/TypeColorResolverTests.cs` | Unit tests for TypeColorResolver |
| `tests/Twig.Domain.Tests/Services/DeterministicTypeColorTests.cs` | Unit tests for DeterministicTypeColor |

### Modified Files

| File Path | Changes |
|---|---|
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add `_appearanceColors` field (`Dictionary<string, string>?`); update constructor to accept `List<TypeAppearanceConfig>?` and pre-project to flat dictionary; refactor `GetTypeColor()` to use `TypeColorResolver` + `DeterministicTypeColor`; delete `DeterministicColor()` private method and hardcoded switch; remove `OrdinalIgnoreCase` wrapping from constructor (resolver handles it) |
| `src/Twig/Commands/PromptCommand.cs` | Refactor `ResolveColor()` to pre-project `config.TypeAppearances` to a flat dictionary and delegate to `TypeColorResolver.ResolveHex()` |
| `src/Twig/Commands/InitCommand.cs` | Remove 4-line `Display.TypeColors` bridging block: comment + 3-line LINQ expression (lines 141-144) |
| `src/Twig/Commands/RefreshCommand.cs` | Remove 4-line `Display.TypeColors` bridging block: comment + 3-line LINQ expression (lines 133-136) |
| `src/Twig/Program.cs` | Update `HumanOutputFormatter` construction to pass `config.TypeAppearances` |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Update tests asserting hardcoded ANSI escapes for known types; update tests for `DeterministicColor` behavior; update integration tests |
| `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | Verify `ResolveColor` still works with both `typeColors` and `typeAppearances` |
| `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | Remove assertion that `Display.TypeColors` is populated by refresh (lines 172-176) |
| `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | Update test `Init_PersistsTypeAppearances_InConfig` if it checks `Display.TypeColors` bridging |

### Deleted Files

| File Path | Reason |
|---|---|
| (none) | |

---

## Implementation Plan

### EPIC-1: Extract Shared Utilities

**Goal**: Create the `TypeColorResolver` and `DeterministicTypeColor` shared services in `Twig.Domain/Services/`.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-001 | IMPL | Create `TypeColorResolver` static class in `Twig.Domain/Services/` with `ResolveHex(string typeName, Dictionary<string, string>? typeColors, Dictionary<string, string>? appearanceColors)` method. Both dictionary parameters are `typeName → hexColor` maps. Resolution order: typeColors → appearanceColors → null. The resolver MUST internally normalize each non-null dictionary to `OrdinalIgnoreCase` by checking `dictionary.Comparer`: if already `OrdinalIgnoreCase`, use as-is; otherwise create a new `Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase)` copy. This guarantees FR-7 regardless of caller convention. No Infrastructure types (`TypeAppearanceConfig`, `TwigConfiguration`, etc.) in the signature — `Twig.Domain.csproj` has zero project references. | `src/Twig.Domain/Services/TypeColorResolver.cs` | DONE |
| ITEM-002 | IMPL | Extract `DeterministicTypeColor` static class from `HumanOutputFormatter.DeterministicColor()`. Move the hash algorithm and 6-color ANSI array to `Twig.Domain/Services/DeterministicTypeColor.cs` with a public `GetAnsiEscape(string typeName)` method. Keep the identical hash logic: `hash * 31 + c`, `(hash & 0x7FFFFFFF) % 6`, same color order. | `src/Twig.Domain/Services/DeterministicTypeColor.cs` | DONE |
| ITEM-003 | TEST | Create `TypeColorResolverTests` covering: (a) returns hex from typeColors when present, (b) falls back to appearanceColors when typeColors missing, (c) typeColors wins over appearanceColors, (d) returns null when both null/empty, (e) case-insensitive type name matching with default-comparer dictionaries (verifies internal normalization), (f) case-insensitive matching with pre-wrapped `OrdinalIgnoreCase` dictionaries (verifies no double-wrapping). All test inputs use plain `Dictionary<string, string>` — no `TypeAppearanceConfig` references needed. Tests compile under `Twig.Domain.Tests` which only references `Twig.Domain`. | `tests/Twig.Domain.Tests/Services/TypeColorResolverTests.cs` | DONE |
| ITEM-004 | TEST | Create `DeterministicTypeColorTests` covering: (a) returns valid ANSI escape, (b) same input → same output (deterministic), (c) different inputs → covers multiple colors, (d) empty string doesn't throw. | `tests/Twig.Domain.Tests/Services/DeterministicTypeColorTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `TypeColorResolver.ResolveHex()` returns correct hex following the priority chain
- [x] `TypeColorResolver.ResolveHex()` performs case-insensitive matching even when passed default-comparer dictionaries
- [x] `DeterministicTypeColor.GetAnsiEscape()` produces identical output to the old `HumanOutputFormatter.DeterministicColor()` for the same input (verified by pinning tests for "Bug"→Green, "Epic"→Cyan, "Task"→Cyan)
- [x] All new tests pass (16/16)
- [x] Solution builds with no warnings — no circular dependency between `Twig.Domain` and `Twig.Infrastructure`

---

### EPIC-2: Refactor Consumers ✅ DONE

**Goal**: Update `HumanOutputFormatter` and `PromptCommand` to use the shared utilities. Remove the hardcoded type→color switch.

**Prerequisites**: EPIC-1.

**Completed**: 2026-03-16. EPIC-2 delivered: `HumanOutputFormatter` refactored to accept `List<TypeAppearanceConfig>?` and pre-project to `_appearanceColors`; `GetTypeColor()` now delegates to `TypeColorResolver.ResolveHex()` → `HexToAnsi.ToForeground()` → `DeterministicTypeColor.GetAnsiEscape()`, removing the hardcoded switch and private `DeterministicColor()`. `PromptCommand.ResolveColor()` similarly delegates to `TypeColorResolver.ResolveHex()`. `Program.cs` updated to pass `cfg.TypeAppearances`. Review fixes applied: `_typeColors` normalized to `OrdinalIgnoreCase` at construction time via copy-constructor overload; `_appearanceColors` uses `StringComparer.OrdinalIgnoreCase` in `ToDictionary()` — both use the `StringComparer.OrdinalIgnoreCase` singleton to satisfy the comparer-identity check in `TypeColorResolver`. New test `FormatWorkItem_UsesTrueColor_WhenTypeAppearancesProvided` added asserting `\x1b[38;2;255;0;255m` for `#FF00FF` Epic appearance.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-005 | IMPL | Add `private readonly Dictionary<string, string>? _appearanceColors;` field to `HumanOutputFormatter`. Update constructor `HumanOutputFormatter(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null)` to accept the list and pre-project to a flat dictionary: `_appearanceColors = typeAppearances?.Where(a => !string.IsNullOrEmpty(a.Color)).ToDictionary(a => a.Name, a => a.Color);`. Store `_typeColors = displayConfig.TypeColors` directly (no longer wrap in `OrdinalIgnoreCase` — the resolver handles normalization). Keep the existing parameterless constructor calling `this(new DisplayConfig())`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-006 | IMPL | Refactor `GetTypeColor()` to: (1) call `TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors)`, (2) if non-null, pass to `HexToAnsi.ToForeground()`, (3) if that returns non-null, return it, (4) otherwise return `DeterministicTypeColor.GetAnsiEscape(type.Value)`. Delete the hardcoded `switch` block and the private `DeterministicColor()` method. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-007 | IMPL | Refactor `PromptCommand.ResolveColor()` to pre-project `config.TypeAppearances` and delegate: `var appearanceColors = config.TypeAppearances?.Where(a => !string.IsNullOrEmpty(a.Color)).ToDictionary(a => a.Name, a => a.Color); return TypeColorResolver.ResolveHex(type, config.Display.TypeColors, appearanceColors);`. This ensures no `TypeAppearanceConfig` reference leaks to the Domain layer and the resolver handles case normalization. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-008 | IMPL | Update `Program.cs` HumanOutputFormatter registration to pass `config.TypeAppearances`: `new HumanOutputFormatter(cfg.Display, cfg.TypeAppearances)`. | `src/Twig/Program.cs` | DONE |
| ITEM-009 | TEST | Update `HumanOutputFormatterTests` tests that assert hardcoded ANSI escapes (e.g., `FormatWorkItem_ShowsTypeColor_ForEpic` asserts `\x1b[35m`). For tests where no `DisplayConfig.TypeColors` is configured and no `TypeAppearances` is provided, the expected color changes from the hardcoded value to `DeterministicTypeColor.GetAnsiEscape()` output. Calculate the expected deterministic color for each type and update assertions. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-010 | TEST | Update `FormatWorkItem_ShowsTypeColor_ForTask_UsesReset` — Task with no config will now get a deterministic hash color instead of `\x1b[0m` Reset. Update assertion to expect `DeterministicTypeColor.GetAnsiEscape("Task")`. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-011 | TEST | Update `FormatWorkItem_FallsBackTo3BitColor_WhenNoTypeColors` — previously asserted `\x1b[35m` (Magenta) for Epic. With no typeAppearances either, it will get `DeterministicTypeColor.GetAnsiEscape("Epic")`. Update assertion. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-012 | TEST | Verify `PromptCommandTests.JsonColor_FallsBackToTypeAppearances` still passes (it should — `ResolveColor` still checks `TypeAppearances` via pre-projection). Verify `JsonColor_FromTypeColors` still passes (typeColors still checked first). | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] No hardcoded type→color switch in `HumanOutputFormatter`
- [x] No private `DeterministicColor()` method in `HumanOutputFormatter`
- [x] Both `GetTypeColor()` and `ResolveColor()` use `TypeColorResolver.ResolveHex()`
- [x] All existing and updated tests pass
- [x] Solution builds and all formatter tests pass

---

### EPIC-3: Eliminate Dual Storage Bridging ✅ DONE

**Goal**: Remove the `Display.TypeColors` auto-bridging from `InitCommand` and `RefreshCommand`, making `Display.TypeColors` a user-override-only config section.

**Prerequisites**: EPIC-2 (consumers must already resolve from `TypeAppearances` as fallback).

**Completed**: 2026-03-16. EPIC-3 delivered: bridging blocks removed from both `InitCommand.ExecuteAsync()` and `RefreshCommand.ExecuteAsync()`; `Display.TypeColors` is now user-override-only. `Refresh_UpdatesTypeAppearances` assertion block for `Display.TypeColors` removed (test still validates `TypeAppearances` population). New regression test `Refresh_DoesNotOverwriteDisplayTypeColors` verifies custom colors survive refresh unchanged for both ADO-known types and entirely custom types.

| Task | Type | Description | Files | Status |
|---|---|---|---|---|
| ITEM-013 | IMPL | In `InitCommand.ExecuteAsync()`, delete the 4-line block (lines 141-144) that bridges `TypeAppearances → Display.TypeColors`. This includes the comment `// ITEM-157: Bridge TypeAppearances → Display.TypeColors...` followed by the 3-line LINQ expression below it. | `src/Twig/Commands/InitCommand.cs` | DONE |
| ITEM-014 | IMPL | In `RefreshCommand.ExecuteAsync()`, delete the 4-line block (lines 133-136) that bridges `TypeAppearances → Display.TypeColors`. This includes the comment `// ITEM-157: Bridge TypeAppearances → Display.TypeColors...` followed by the 3-line LINQ expression below it. | `src/Twig/Commands/RefreshCommand.cs` | DONE |
| ITEM-015 | TEST | In `RefreshCommandTests.Refresh_UpdatesTypeAppearances()`, remove the assertion block that checks `_config.Display.TypeColors` is populated (lines 172-176). The test should still verify `TypeAppearances` is populated correctly. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | DONE |
| ITEM-016 | TEST | In `InitCommandTests`, verify that `Init_PersistsTypeAppearances_InConfig` does not depend on `Display.TypeColors` being present in the serialized output. If it asserts `typeColors` in the JSON, update or remove that assertion. | `tests/Twig.Cli.Tests/Commands/InitCommandTests.cs` | DONE |
| ITEM-017 | TEST | Add a new test: `Refresh_DoesNotOverwriteDisplayTypeColors` — set `Display.TypeColors` to a custom value before refresh, run refresh, assert `Display.TypeColors` is unchanged. | `tests/Twig.Cli.Tests/Commands/RefreshCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `InitCommand` no longer writes to `Display.TypeColors`
- [x] `RefreshCommand` no longer writes to `Display.TypeColors`
- [x] User-specified `Display.TypeColors` entries survive a refresh cycle
- [x] All tests pass
- [x] Config file after init contains `typeAppearances` but `display.typeColors` is absent (unless user-set)

---

## References

- `docs/projects/twig-ado-type-colors.plan.md` — Original ADO type colors integration plan
- `docs/projects/twig-true-color-badges.plan.md` — True-color badge support plan
- `docs/projects/twig-iconid-glyph-mapping.plan.md` — Icon ID to glyph mapping (related, parallel work)
- `docs/projects/twig-structural-audit.doc.md` — Structural audit documenting dual storage concern
- `src/Twig/Formatters/HexToAnsi.cs` — Hex-to-ANSI conversion utility
- `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` — ADO process type record with `ColorHex`
