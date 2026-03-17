# Redesign IconSet to Map Glyphs by ADO iconId

> **Revision**: 3 (Address technical review feedback — 3 factual errors corrected)
> **Date**: 2026-03-15
> **Status**: Complete

---

## Executive Summary

This design replaces the type-name-keyed glyph dictionaries in `IconSet` with ADO `iconId`-keyed dictionaries, enabling universal icon support across all Azure DevOps process templates. Currently, `IconSet` maps 13 hardcoded type names (Agile/Scrum/CMMI) to glyphs; any custom or inherited work item type falls back to a dot. ADO provides a stable `iconId` field (e.g., `icon_crown`, `icon_insect`, `icon_check_box`) per work item type, and this data is already persisted in both `.twig/config` (`typeAppearances`) and the `process_types` SQLite table. Since ADO has exactly 41 distinct icon IDs across all process templates, a single iconId→glyph map covers every possible work item type universally — including the 32+ custom types in the user's organization that currently display as dots.

---

## Background

### Current Architecture

The Twig CLI renders work item type icons in two consumer paths:

1. **`HumanOutputFormatter`** — Used by `twig status`, `twig tree`, `twig ws`, `twig sprint`. Has two badge methods:
   - `GetTypeBadge(WorkItemType type)` (private static, lines 337-349) — hardcoded switch on type name, used by ALL rendering methods (`FormatWorkItem`, `FormatTree`, `FormatWorkspace`, `FormatSprintView`). Returns first letter of type name for unknown types.
   - `GetTypeIcon(WorkItemType type)` (internal, line 44) — delegates to `IconSet.GetIcon(_icons, type.Value)`. Exists but is **never called** from rendering methods.

2. **`PromptCommand`** — Internal class at `src/Twig/Commands/PromptCommand.cs`. **Not currently wired to the CLI**: there is no `Prompt()` method in `TwigCommands`, no `AddSingleton<PromptCommand>` in `Program.cs`, and the class is marked `internal`. CLI wiring (`twig prompt` command registration) is a separate prerequisite tracked in `twig-structural-audit.doc.md` or a dedicated plan. This plan modifies `PromptCommand`'s badge resolution logic so that when `twig prompt` is eventually wired, it will use iconId-based resolution. Calls `IconSet.GetIcon(IconSet.GetIcons(iconMode), type)` directly (after the `twig-icon-cleanup` plan removed the intermediate `PromptBadges` class).

3. **`IconSet`** (lines 1-54 of `Twig.Domain/ValueObjects/IconSet.cs`) — Two static dictionaries keyed by type name (`UnicodeIcons` with 13 entries, `NerdFontIcons` with 13 entries). `GetIcon(icons, typeName)` falls back to `"·"` for unknown types.

### What Already Exists

| Data Source | Location | Contents |
|------------|----------|----------|
| `TypeAppearances` | `.twig/config` JSON (`config.TypeAppearances`) | Array of `{Name, Color, IconId}` per type — persisted during `init`/`refresh` |
| `process_types` table | SQLite DB | `icon_id` column per type — persisted during `init`/`refresh` |
| `WorkItemTypeAppearance` | `Twig.Domain/ValueObjects/WorkItemTypeAppearance.cs` | Domain record: `(string Name, string? Color, string? IconId)` |
| `ProcessTypeRecord` | `Twig.Domain/Aggregates/ProcessTypeRecord.cs` | Has `IconId` property |
| `TypeAppearanceConfig` | `Twig.Infrastructure/Config/TwigConfiguration.cs` | Config POCO: `{Name, Color, IconId}` |
| ADO REST API | `GET /{org}/_apis/wit/workitemicons` | Returns 41 distinct icon IDs |

### Context and Motivation

Organizations using inherited or custom process templates (common in enterprise environments) have work item types that all fall back to the `"·"` dot glyph. For example, the user's contoso OS organization has 32 custom work item types — all rendered as dots despite ADO providing rich icon metadata for each. The `twig-dynamic-process.plan.md` already opened the type system to accept custom types and connected `TypeAppearances` colors, but icon rendering still uses hardcoded type-name switches.

### Prior Art

| Document | Relevance |
|----------|-----------|
| `twig-nerd-font-icons.plan.md` | Created `IconSet`, `DisplayConfig.Icons`, wired `GetTypeIcon()` |
| `twig-icon-cleanup.plan.md` | Removed `PromptBadges`, consolidated prompt to use `IconSet.GetIcon()` directly |
| `twig-dynamic-process.plan.md` | Opened type system, added `GetTypeBadge` first-letter fallback, connected `TypeAppearances` colors |
| `twig-true-color-badges.plan.md` | Created `GetTypeBadge()`, `GetTypeColor()`, type color infrastructure |
| `twig-structural-audit.doc.md` | Identified `GetTypeIcon()` as dead code (CS-005), `GetTypeBadge()` as hardcoded duplicate (CS-002) |

---

## Problem Statement

1. **Type-name-keyed icon maps are inherently non-universal**: `IconSet` has 13 entries for standard Agile/Scrum/CMMI types. Any work item type not in the dictionary gets `"·"`. There is no finite set of type names — organizations can create arbitrary names — so the type-name approach can never achieve full coverage.

2. **ADO icon IDs ARE universal**: ADO has exactly 41 distinct icon IDs (`icon_clipboard`, `icon_crown`, `icon_insect`, etc.) that cover every possible work item type across all process templates. A single iconId→glyph map handles every type from every process template.

3. **Dual badge code paths cause inconsistency**: `GetTypeBadge()` (hardcoded switch, called by all rendering methods) and `GetTypeIcon()` (delegates to `IconSet`, never called by rendering) produce different glyphs. The `twig-structural-audit.doc.md` flagged this as CS-002 and CS-005.

4. **Crown glyph rendering issue**: `nf-md-crown` (U+F0531) renders as a tree shape in CaskaydiaCove Nerd Font, making Epic/Feature items visually confusing in nerd font mode.

5. **Icon data is fetched but unused for rendering**: `TypeAppearances` with `IconId` is already persisted during `init`/`refresh` but never consumed by the icon rendering pipeline.

---

## Goals and Non-Goals

### Goals

1. **G-1**: `IconSet` MUST provide iconId-keyed glyph dictionaries covering all 41 ADO icon IDs in both unicode and nerd font modes.
2. **G-2**: All icon rendering paths (`GetTypeBadge`, `GetTypeIcon`, `PromptCommand` badge) MUST resolve glyphs via ADO `iconId` when available.
3. **G-3**: The crown nerd font glyph MUST render correctly in CaskaydiaCove Nerd Font.
4. **G-4**: Unknown icon IDs (offline, pre-init) MUST fall back to the first letter of the type name (uppercased), then `"·"` for empty names.
5. **G-5**: All 13 standard type names MUST produce identical glyphs to current behavior when iconId is unavailable (backward compatibility).
6. **G-6**: All existing tests MUST pass after migration.

### Non-Goals

- **NG-1**: Fetching icon metadata from ADO at runtime (lazy loading). All data comes from `process_types`/`TypeAppearances` cache.
- **NG-2**: Rendering actual SVG/image icons. Only Unicode and Nerd Font text glyphs.
- **NG-3**: User-configurable icon overrides (custom glyph mappings). Deferred.
- **NG-4**: Changing the `"·"` default for the absolute fallback (no iconId, no type name).
- **NG-5**: Merging `GetTypeBadge()` and `GetTypeIcon()` into a single method in this plan. The structural cleanup (wiring `GetTypeIcon` into rendering paths, deleting `GetTypeBadge`) is a companion task tracked in `twig-structural-audit.doc.md` Theme B.

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-001 | `IconSet` MUST expose `UnicodeIconsByIconId` and `NerdFontIconsByIconId` dictionaries mapping each of the 41 ADO icon IDs to a glyph string | High |
| FR-002 | `IconSet` MUST expose a `GetIconByIconId(string mode, string? iconId)` method that returns the glyph for an ADO icon ID, or `null` for unknown/null icon IDs — allowing callers to apply their own fallback | High |
| FR-003 | `HumanOutputFormatter` MUST accept an iconId resolver (via `TypeAppearances` from config) to map type names to icon IDs | High |
| FR-004 | `HumanOutputFormatter.GetTypeBadge()` MUST resolve iconId from `TypeAppearances` when available, then fall back to the hardcoded type-name switch for backward compatibility | High |
| FR-005 | `PromptCommand.ReadPromptData()` MUST resolve iconId from `TypeAppearances` or `process_types` and pass it to `IconSet.GetIconByIconId()` | High |
| FR-006 | The `icon_crown` nerd font glyph MUST be replaced with a glyph that renders correctly in CaskaydiaCove NF | Medium |
| FR-007 | All 41 ADO icon IDs MUST have both a unicode and nerd font glyph mapping | Medium |
| FR-008 | For types with no iconId data, the type-name-keyed dictionaries MUST remain as a backward-compatible fallback | Medium |
| FR-009 | Existing `UnicodeIcons` and `NerdFontIcons` (type-name-keyed) MUST be preserved for backward compatibility | Low |

### Non-Functional Requirements

| ID | Requirement | Metric |
|----|-------------|--------|
| NFR-001 | All changes MUST be AOT-safe (no reflection, no dynamic code gen) | `dotnet publish -c Release` succeeds |
| NFR-002 | Icon lookup MUST be O(1) dictionary access | Static dictionaries, no linear search |
| NFR-003 | JSON serialization MUST use source-generated `TwigJsonContext` | No new DTOs needed — existing `TypeAppearanceConfig` is sufficient |
| NFR-004 | Zero runtime behavior change for existing users with standard process templates | 13 standard types produce same glyphs |

---

## Proposed Design

### Architecture Overview

**Before (current state):**

```
                    ┌─── HumanOutputFormatter ──┐
                    │  GetTypeBadge(type) ────────> hardcoded switch (13 types + first-letter fallback)
                    │  GetTypeIcon(type) ─────────> IconSet.GetIcon(icons, typeName) [DEAD CODE]
                    └────────────────────────────┘

                    ┌─── PromptCommand ──────────┐
                    │  IconSet.GetIcon(icons, type) ──> type-name dictionary (13 entries, "·" fallback)
                    └────────────────────────────┘

TypeAppearances ─────> has IconId ─────> UNUSED for icon rendering
```

**After (proposed):**

```
                    ┌─── HumanOutputFormatter ───────────────────────────┐
                    │  _iconIdLookup: Dictionary<typeName, iconId>       │
                    │                 (built from TypeAppearances)       │
                    │                                                    │
                    │  GetTypeBadge(type) ──> resolve iconId ──> IconSet.GetIconByIconId(mode, iconId)
                    │                         │                   │
                    │                         └─ no iconId ──────> fallback: type-name switch
                    └────────────────────────────────────────────────────┘

                    ┌─── PromptCommand ──────────────────────────────────┐
                    │  resolve iconId from TypeAppearances               │
                    │  IconSet.GetIconByIconId(mode, iconId)             │
                    │    │                                               │
                    │    └─ no iconId ──> IconSet.GetIcon(icons, type)   │
                    └────────────────────────────────────────────────────┘

                    ┌─── IconSet ────────────────────────────────────────┐
                    │  UnicodeIconsByIconId    (41 entries)              │
                    │  NerdFontIconsByIconId   (41 entries)              │
                    │  UnicodeIcons            (13 entries) [preserved]  │
                    │  NerdFontIcons           (13 entries) [preserved]  │
                    │                                                    │
                    │  GetIconByIconId(mode, iconId)  ─── NEW           │
                    │  GetIcon(icons, typeName)       ─── PRESERVED     │
                    └────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `IconSet` — New iconId-keyed dictionaries and method

**File**: `src/Twig.Domain/ValueObjects/IconSet.cs`

Add two new static dictionaries and a convenience method:

```csharp
public static IReadOnlyDictionary<string, string> UnicodeIconsByIconId { get; } =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["icon_crown"]            = "◆",  // Epic, Feature, Scenario
        ["icon_insect"]           = "✦",  // Bug
        ["icon_check_box"]        = "□",  // Task
        ["icon_book"]             = "●",  // User Story
        ["icon_clipboard"]        = "□",  // many generic types
        ["icon_trophy"]           = "★",  // Deliverable
        ["icon_gift"]             = "♦",  // Customer Promise
        ["icon_chart"]            = "▬",  // Measure, Key Result
        ["icon_diamond"]          = "◇",  // Objective
        ["icon_list"]             = "≡",  // Task Group
        ["icon_test_beaker"]      = "□",  // Test Case
        ["icon_test_plan"]        = "□",  // Test Plan
        ["icon_test_suite"]       = "□",  // Test Suite
        ["icon_test_case"]        = "□",  // Test Case (alt)
        ["icon_test_step"]        = "□",  // Test Step
        ["icon_test_parameter"]   = "□",  // Test Parameter
        ["icon_sticky_note"]      = "▪",  // Feature (sticky note)
        ["icon_traffic_cone"]     = "⚠",  // Impediment
        ["icon_chat_bubble"]      = "○",  // Feedback
        ["icon_flame"]            = "✦",  // Hotfix / urgent
        ["icon_megaphone"]        = "◉",  // Announcement
        ["icon_code_review"]      = "◈",  // Code Review Request
        ["icon_code_response"]    = "◈",  // Code Review Response
        ["icon_review"]           = "◎",  // Review
        ["icon_response"]         = "○",  // Response
        ["icon_star"]             = "★",  // Star
        ["icon_ribbon"]           = "▪",  // Ribbon
        ["icon_headphone"]        = "♪",  // Support
        ["icon_key"]              = "▣",  // Key / Security
        ["icon_airplane"]         = "►",  // Travel / Release
        ["icon_car"]              = "►",  // Vehicle
        ["icon_asterisk"]         = "✱",  // Generic
        ["icon_database_storage"] = "▦",  // Database
        ["icon_government"]       = "▣",  // Governance
        ["icon_gavel"]            = "▣",  // Legal
        ["icon_parachute"]        = "▽",  // Safety
        ["icon_paint_brush"]      = "▪",  // Design
        ["icon_palette"]          = "◈",  // Design
        ["icon_gear"]             = "⚙",  // Settings / Infra
        ["icon_broken_lightbulb"] = "✦",  // Issue / Defect
        ["icon_clipboard_issue"]  = "□",  // Issue
    };

public static IReadOnlyDictionary<string, string> NerdFontIconsByIconId { get; } =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["icon_crown"]            = "\ueb59",         // nf-cod-star_full (U+EB59) — crown alt
        ["icon_insect"]           = "\ueaaf",         // nf-cod-bug (U+EAAF)
        ["icon_check_box"]        = "\ueab3",         // nf-cod-checklist (U+EAB3)
        ["icon_book"]             = "\ueaa4",         // nf-cod-book (U+EAA4)
        ["icon_clipboard"]        = "\ueac0",         // nf-cod-clippy (U+EAC0)
        ["icon_trophy"]           = "\ueb20",         // nf-cod-milestone (U+EB20)
        ["icon_gift"]             = "\ueaf9",         // nf-cod-gift (U+EAF9)
        ["icon_chart"]            = "\ueb03",         // nf-cod-graph (U+EB03)
        ["icon_diamond"]          = "\udb80\uddc8",   // nf-md-diamond_stone (U+F01C8) — cod-diamond not available; verify in CaskaydiaCove NF
        ["icon_list"]             = "\ueb17",         // nf-cod-list_unordered (U+EB17)
        ["icon_test_beaker"]      = "\uea79",         // nf-cod-beaker (U+EA79)
        ["icon_test_plan"]        = "\uebaf",         // nf-cod-notebook (U+EBAF)
        ["icon_test_suite"]       = "\ueb9c",         // nf-cod-library (U+EB9C)
        ["icon_test_case"]        = "\uea79",         // nf-cod-beaker (U+EA79)
        ["icon_test_step"]        = "\ueb16",         // nf-cod-list_ordered (U+EB16)
        ["icon_test_parameter"]   = "\ueb52",         // nf-cod-settings (U+EB52)
        ["icon_sticky_note"]      = "\uea7b",         // nf-cod-file (U+EA7B)
        ["icon_traffic_cone"]     = "\uea6c",         // nf-cod-warning (U+EA6C)
        ["icon_chat_bubble"]      = "\uea6b",         // nf-cod-comment (U+EA6B)
        ["icon_flame"]            = "\ueaf2",         // nf-cod-flame (U+EAF2)
        ["icon_megaphone"]        = "\ueb1e",         // nf-cod-megaphone (U+EB1E)
        ["icon_code_review"]      = "\ueae1",         // nf-cod-diff (U+EAE1)
        ["icon_code_response"]    = "\ueac4",         // nf-cod-code (U+EAC4)
        ["icon_review"]           = "\uea70",         // nf-cod-eye (U+EA70)
        ["icon_response"]         = "\uea6b",         // nf-cod-comment (U+EA6B)
        ["icon_star"]             = "\ueb59",         // nf-cod-star_full (U+EB59)
        ["icon_ribbon"]           = "\ueaa5",         // nf-cod-bookmark (U+EAA5)
        ["icon_headphone"]        = "\udb80\udece",   // nf-md-headset (U+F02CE) — cod-headset not available; verify in CaskaydiaCove NF
        ["icon_key"]              = "\ueb11",         // nf-cod-key (U+EB11)
        ["icon_airplane"]         = "\ueb44",         // nf-cod-rocket (U+EB44)
        ["icon_car"]              = "\ueb44",         // nf-cod-rocket (U+EB44)
        ["icon_asterisk"]         = "\uea6a",         // nf-cod-star_empty (U+EA6A)
        ["icon_database_storage"] = "\ueace",         // nf-cod-database (U+EACE)
        ["icon_government"]       = "\ueac0",         // nf-cod-clippy (U+EAC0)
        ["icon_gavel"]            = "\ueb12",         // nf-cod-law (U+EB12)
        ["icon_parachute"]        = "\uea6c",         // nf-cod-warning (U+EA6C)
        ["icon_paint_brush"]      = "\ueb2a",         // nf-cod-paintcan (U+EB2A)
        ["icon_palette"]          = "\ueac6",         // nf-cod-color_mode (U+EAC6)
        ["icon_gear"]             = "\ueaf8",         // nf-cod-gear (U+EAF8)
        ["icon_broken_lightbulb"] = "\uea61",         // nf-cod-lightbulb (U+EA61)
        ["icon_clipboard_issue"]  = "\ueb0c",         // nf-cod-issues (U+EB0C)
    };

/// <summary>
/// Resolves a glyph for an ADO icon ID in the specified mode.
/// Returns null if iconId is null or not in the dictionary, allowing callers to apply their own fallback.
/// </summary>
public static string? GetIconByIconId(string mode, string? iconId)
{
    if (iconId is null)
        return null;

    var dict = string.Equals(mode, "nerd", StringComparison.OrdinalIgnoreCase)
        ? NerdFontIconsByIconId
        : UnicodeIconsByIconId;

    return dict.TryGetValue(iconId, out var icon) ? icon : null;
}
```

The existing `UnicodeIcons`, `NerdFontIcons`, `GetIcons()`, and `GetIcon()` are **preserved unchanged** for backward compatibility.

#### 2. `HumanOutputFormatter` — iconId resolution via TypeAppearances

**File**: `src/Twig/Formatters/HumanOutputFormatter.cs`

Add a `_typeIconIds` lookup dictionary built from `TypeAppearances`. Update `GetTypeBadge()` to check iconId first.

```csharp
// New field
private readonly Dictionary<string, string>? _typeIconIds;
private readonly string _iconMode;

// Updated constructor (DisplayConfig already has everything needed)
public HumanOutputFormatter(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null)
{
    _typeColors = /* ... existing ... */;
    _icons = IconSet.GetIcons(displayConfig.Icons);
    _iconMode = displayConfig.Icons;

    // Build type-name → iconId lookup from TypeAppearances
    if (typeAppearances is { Count: > 0 })
    {
        _typeIconIds = new Dictionary<string, string>(typeAppearances.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var ta in typeAppearances)
        {
            if (ta.IconId is not null)
                _typeIconIds[ta.Name] = ta.IconId;
        }
    }
}

// Updated GetTypeBadge (note: changed from `private static` to `private` to access instance fields)
private string GetTypeBadge(WorkItemType type)
{
    // Try iconId-based resolution first
    if (_typeIconIds is not null &&
        _typeIconIds.TryGetValue(type.Value, out var iconId))
    {
        var glyph = IconSet.GetIconByIconId(_iconMode, iconId);
        if (glyph is not null)
            return glyph;
    }

    // Fall back to existing hardcoded type-name switch
    return type.Value.ToLowerInvariant() switch
    {
        "epic" => "◆",
        /* ... existing switch arms ... */
        _ => type.Value.Length > 0
            ? type.Value[0].ToString().ToUpperInvariant()
            : "■",
    };
}
```

**Note**: `GetIconByIconId` returns `null` for unknown icon IDs, allowing callers to implement their own fallback chain. `DefaultIcon` remains `private const` in `IconSet` — no cross-assembly internal access is needed.

#### 3. `PromptCommand` — iconId resolution

**File**: `src/Twig/Commands/PromptCommand.cs`

The `ReadPromptData()` method already has access to `config.TypeAppearances`. Add iconId lookup:

```csharp
// In ReadPromptData(), after reading type from DB:
var iconMode = config.Display.Icons;
var iconId = ResolveIconId(type);
var badge = IconSet.GetIconByIconId(iconMode, iconId)
    ?? IconSet.GetIcon(IconSet.GetIcons(iconMode), type);

// Helper method
private string? ResolveIconId(string typeName)
{
    var appearance = config.TypeAppearances?.Find(t =>
        string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
    return appearance?.IconId;
}
```

### Data Flow

**Icon resolution (after) — all paths:**

```
1. Caller has type name (e.g., "Deliverable")
2. Look up iconId from TypeAppearances: "Deliverable" → "icon_trophy"
3. Call IconSet.GetIconByIconId(mode, "icon_trophy")
4. Dictionary lookup: "icon_trophy" → "★" (unicode) or nerd glyph
5. If iconId is null or not found → fall back to type-name lookup or first-letter
```

**Data sources for iconId:**

```
init/refresh → ADO API → WorkItemTypeAppearance(name, color, iconId)
                          ├─→ config.TypeAppearances (persisted in .twig/config)
                          └─→ process_types.icon_id (persisted in SQLite)

Runtime render → config.TypeAppearances → _typeIconIds lookup
               → process_types.icon_id (PromptCommand direct DB access)
```

### API Contracts

#### `IconSet` — New public API surface

```csharp
// New properties
public static IReadOnlyDictionary<string, string> UnicodeIconsByIconId { get; }
public static IReadOnlyDictionary<string, string> NerdFontIconsByIconId { get; }

// New method — returns null for unknown/null iconIds (callers provide fallback)
public static string? GetIconByIconId(string mode, string? iconId);

// Existing (unchanged)
public static IReadOnlyDictionary<string, string> UnicodeIcons { get; }     // preserved
public static IReadOnlyDictionary<string, string> NerdFontIcons { get; }    // preserved
public static IReadOnlyDictionary<string, string> GetIcons(string mode);    // preserved
public static string GetIcon(IReadOnlyDictionary<string, string> icons, string? typeName); // preserved
```

#### `HumanOutputFormatter` — Constructor change

```csharp
// Extended constructor signature (optional parameter added to existing constructor)
// The existing `HumanOutputFormatter(DisplayConfig)` becomes
// `HumanOutputFormatter(DisplayConfig, List<TypeAppearanceConfig>? = null)`.
// This is NOT a new overload — it extends the existing signature with a defaulted parameter.
public HumanOutputFormatter(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null)

// Parameterless constructor (preserved, delegates to above with default DisplayConfig)
public HumanOutputFormatter()
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Add new iconId dictionaries rather than replacing type-name dictionaries** | Backward compatibility. The type-name dictionaries serve as fallback when iconId is unavailable (pre-init, offline, config without `TypeAppearances`). Removing them would break tests and offline scenarios. |
| **Use `GetIconByIconId(string mode, string? iconId)` rather than `GetIconByIconId(IReadOnlyDictionary, string?)` pattern** | The iconId-based API benefits from a mode-string interface because callers rarely need the raw dictionary — they just want the glyph. The type-name API uses the dictionary pattern because `HumanOutputFormatter` caches the dictionary in `_icons`. Both patterns coexist. |
| **Resolve iconId in the consumer (formatter/command) rather than in IconSet** | IconSet is a pure static glyph registry with no config dependencies. The type-name→iconId mapping requires `TypeAppearances` data, which is config/infrastructure-layer knowledge. Keeping IconSet config-free maintains clean layer boundaries. |
| **Replace `nf-md-crown` (U+F0531) with `nf-cod-star_full` (U+EB59) for `icon_crown`** | U+F0531 renders as a tree in CaskaydiaCove NF. Codicon glyphs render reliably across all Nerd Font patched fonts because they're in the stable Codicons range. `star_full` conveys "top-level importance" semantically matching Epic/Feature. Codepoint verified against Nerd Fonts `glyphnames.json` v3.4.0. See OQ-001 for alternative options. |
| **Use Codicon-range glyphs (U+EA60-U+EC24) as primary source for nerd font mode, with verified MDI fallbacks** | Codicons are the most reliable glyph range in Nerd Fonts — they render consistently across CaskaydiaCove, JetBrains Mono NF, FiraCode NF, etc. Material Design icons (U+F0001+) have known rendering issues in some patched fonts. However, two icon IDs (`icon_diamond`, `icon_headphone`) have no Codicon equivalent, so verified MDI glyphs (`nf-md-diamond_stone`, `nf-md-headset`) are used as fallbacks. All codepoints verified against `glyphnames.json` v3.4.0. |
| **Return `null` from `GetIconByIconId` for unknown icon IDs instead of `DefaultIcon`** | Consumers need to distinguish "real glyph found" from "no mapping" to implement fallback chains. Returning `null` is idiomatic C# and avoids exposing `DefaultIcon` across assembly boundaries — `IconSet` is in `Twig.Domain` but consumers are in `Twig`, which is NOT in `Twig.Domain.csproj`'s `InternalsVisibleTo` list. The null-return pattern eliminates cross-assembly coupling entirely. |
| **Pass `TypeAppearances` to `HumanOutputFormatter` constructor rather than injecting `IProcessTypeStore`** | `HumanOutputFormatter` is constructed in `Program.cs` where `config.TypeAppearances` is already available. Injecting a repository would add async initialization complexity and a SQLite dependency to the formatter. The config data is already loaded. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Replace type-name dictionaries entirely with iconId dictionaries** | Simpler code, single lookup path | Breaks backward compatibility for pre-init/offline scenarios; existing tests rely on type-name keys | Rejected — keep both, iconId preferred |
| **Store iconId on `WorkItem` aggregate and pass through to formatter** | Most direct data flow | Requires schema change to `work_items` table, migration of existing databases, changes to `AdoResponseMapper` | Rejected — heavyweight for icon rendering; TypeAppearances is sufficient |
| **Use Material Design Icons (nf-md-) range for nerd font glyphs** | Wider glyph selection (1000+ icons) | Known rendering issues in CaskaydiaCove NF (crown→tree bug is in this range); less reliable cross-font | Rejected — Codicons are more reliable |
| **Make `IconSet` non-static with config injection** | Cleaner DI; could auto-resolve iconId internally | Breaks all existing call sites; IconSet as static is well-established; adds unnecessary complexity | Rejected — static with a new method is simpler |
| **Delegate iconId resolution to a new `IIconResolver` service** | Clean separation; mockable for tests | Over-engineering for a dictionary lookup; adds DI wiring for a pure function | Rejected — direct resolution is sufficient |

---

## Dependencies

### Internal

| Dependency | Nature | Status |
|------------|--------|--------|
| `IconSet` | Modified — new dictionaries and method added | Ready |
| `TypeAppearances` / `TypeAppearanceConfig` | Read-only consumer — already populated by `init`/`refresh` | Available |
| `HumanOutputFormatter` | Modified — new constructor parameter, updated `GetTypeBadge` | Ready |
| `PromptCommand` | Modified — iconId resolution added | Ready |
| `DisplayConfig` | Read-only consumer — `Icons` mode string | Available |
| `Program.cs` DI wiring | Modified — pass `TypeAppearances` to `HumanOutputFormatter` constructor | Ready |

### External

- **ADO REST API** — The 41-icon list is sourced from the `GET /_apis/wit/workitemicons` endpoint (API version 7.1). No new API calls needed at runtime; data is already cached.
- **CaskaydiaCove Nerd Font** — Target font for nerd font glyph rendering. All selected Codicon glyphs must be verified against this font.

### Sequencing

| Prerequisite | Status | Impact |
|--------------|--------|--------|
| `twig-icon-cleanup.plan.md` (EPIC-001, EPIC-002) | DONE | `PromptBadges` removed; `PromptCommand` uses `IconSet.GetIcon()` directly |
| `twig-dynamic-process.plan.md` (EPIC-002) | DONE | `TypeAppearances` persisted in config; `GetTypeBadge` has first-letter fallback |
| `twig-structural-audit.doc.md` Theme B | NOT STARTED | Wiring `GetTypeIcon()` into rendering paths. This plan is independent but synergistic — Theme B could be done before or after. |
| `twig prompt` CLI wiring (separate plan) | NOT STARTED | `PromptCommand` is an internal class with no DI registration and no `Prompt()` method in `TwigCommands`. EPIC-003 of this plan updates `PromptCommand`'s badge resolution logic, but the command is not callable from the CLI until wiring is added. This is a **downstream dependency** — `twig prompt` must be registered before EPIC-003 changes are user-visible. |

---

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `IconSet.cs` | New dictionaries (82 entries) and one new method. Existing API unchanged. | Low — additive only |
| `HumanOutputFormatter.cs` | Constructor signature extended (optional param). `GetTypeBadge()` gains iconId pre-check. | Medium — behavioral change for custom types |
| `PromptCommand.cs` | Badge resolution gains iconId lookup before type-name lookup. | Low — fallback preserves existing behavior |
| `Program.cs` | Pass `config.TypeAppearances` to `HumanOutputFormatter` constructor. | Low — one line change |
| `IconSetTests.cs` | New tests for iconId dictionaries and `GetIconByIconId()`. | Low — additive |
| `HumanOutputFormatterTests.cs` | Constructor calls updated where `TypeAppearances` is tested. New tests for iconId badge resolution. | Medium — test changes |
| `PromptCommandTests.cs` | New tests for iconId-based badge resolution. | Low — additive |

### Backward Compatibility

- **Standard types (13)**: Identical output. The iconId lookup will resolve to the same glyphs as the hardcoded switch.
- **Custom types with TypeAppearances**: Improved — previously showed `"·"` or first letter; now shows a meaningful glyph based on the type's ADO icon.
- **Custom types without TypeAppearances**: Unchanged — first letter of type name (from `GetTypeBadge` fallback).
- **Pre-init state**: Unchanged — no `TypeAppearances` means fallback to type-name lookup.

### Performance

No measurable impact. `GetIconByIconId()` is a single dictionary lookup. The `_typeIconIds` dictionary in `HumanOutputFormatter` is built once at construction time.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Codicon glyph renders incorrectly in CaskaydiaCove NF | Low | Medium | All glyphs selected from the Codicon range (U+EA60-U+EC24) which is reliably rendered. Verify `icon_crown` replacement glyph before merging. |
| Constructor signature change breaks DI wiring | Low | High | `TypeAppearances` parameter is optional (defaults to `null`). Existing call sites compile without changes. |
| Some ADO icon IDs not in the 41-icon set (future API additions) | Low | Low | `GetIconByIconId` returns `null` for unknown icon IDs. Consumer-level fallback (first letter) handles this gracefully. |
| `TypeAppearances` is null in pre-init state | Medium | Low | All iconId resolution paths check for null and fall back to existing type-name behavior. |
| Nerd font glyph codepoints change in future Nerd Fonts versions | Low | Medium | Codicons range has been stable since Nerd Fonts v3.0. Pin to Nerd Fonts ≥ v3.0. |
| Existing `NerdFontIcons` type-name dictionary uses original VS Code Codicon codepoints, which differ from Nerd Fonts `glyphnames.json` v3.4.0 codepoints | Medium | Medium | Pre-existing concern, out of scope for this plan. The new `NerdFontIconsByIconId` dictionary uses correct `glyphnames.json` codepoints. Fixing the existing dictionary is a separate task — tracked in OQ-006. |

---

## Open Questions

| ID | Question | Impact | Proposed Resolution |
|----|----------|--------|---------------------|
| OQ-001 | Which specific glyph should replace `nf-md-crown` (U+F0531) for `icon_crown` in nerd font mode? Candidates: `nf-cod-star_full` (U+EB59), `nf-md-crown` (U+F01A5 — verify rendering), `nf-md-crown_outline`. | Medium | Test each candidate in CaskaydiaCove NF terminal. Default recommendation: `nf-cod-star_full` (U+EB59) as it is in the reliable Codicon range and verified against `glyphnames.json` v3.4.0. |
| OQ-002 | Should `GetTypeBadge()` be replaced by `GetTypeIcon()` (wiring Theme B from structural audit) as part of this plan, or kept separate? | Low | Keep separate. This plan adds iconId resolution to `GetTypeBadge`. Theme B (wiring `GetTypeIcon` into rendering paths) is orthogonal and can be done before or after. |
| OQ-003 | For `PromptCommand`'s direct SQLite access, should iconId be read from the `process_types` table (already has `icon_id` column) instead of/in addition to `TypeAppearances`? | Low | Use `TypeAppearances` from config — it's already loaded and available via `config.TypeAppearances`. The `process_types` table would require an additional SQL query. If TypeAppearances is null, fall back to existing behavior (no iconId). |
| OQ-004 | Several nerd font glyph assignments use non-Codicon glyphs (`icon_diamond` → `nf-md-diamond_stone`, `icon_headphone` → `nf-md-headset`) because no Codicon equivalent exists. Three codepoints were corrected in revision 2 after cross-referencing with Nerd Fonts `glyphnames.json` v3.4.0: `icon_gift` (U+EAF9), `icon_palette` (U+EAC6), `icon_trophy` (U+EB20). | Medium — blocking for EPIC-004 | All 41 Nerd Font glyphs MUST be verified against Nerd Fonts `glyphnames.json` and tested in CaskaydiaCove NF before merging. EPIC-004 ITEM-027 is the resolution gate. |
| OQ-005 | Should the `NerdFontIcons` type-name dictionary also be updated to use the corrected crown glyph (replacing U+F0531)? | Medium | Yes — if the crown glyph is broken, it should be fixed in both the new iconId dictionary AND the existing type-name dictionary. This is a bug fix, not a design change. |
| OQ-006 | The existing `NerdFontIcons` type-name dictionary in `IconSet.cs` uses original VS Code Codicon codepoints (e.g., Bug=U+EA87, Checklist=U+EBA2) which differ from the Nerd Fonts `glyphnames.json` v3.4.0 codepoints (Bug=U+EAAF, Checklist=U+EAB3). This is a pre-existing issue — the existing glyphs may render as wrong icons in CaskaydiaCove NF. Should the existing dictionary be corrected as part of this plan? | Medium | Defer to a separate bug-fix PR. The new `NerdFontIconsByIconId` uses correct `glyphnames.json` codepoints. Once Theme B (structural audit) wires `GetTypeIcon` to use iconId resolution, the type-name dictionary becomes the backward-compatibility fallback and its codepoints matter less. |

---

## Implementation Phases

### Phase 1: Add iconId-keyed dictionaries and method to IconSet

**Exit criteria**: `IconSet.GetIconByIconId("unicode", "icon_crown")` returns `"◆"`. `IconSet.GetIconByIconId("nerd", "icon_insect")` returns the bug glyph. `IconSet.GetIconByIconId("unicode", null)` returns `null`. All 41 icon IDs mapped. All existing `IconSetTests` pass.

### Phase 2: Wire iconId resolution into HumanOutputFormatter

**Exit criteria**: `HumanOutputFormatter` constructed with `TypeAppearances` resolves custom type badges via iconId. Standard types unchanged. `GetTypeBadge` uses iconId→glyph for types with appearances data, falls back to existing switch for types without. All formatter tests pass.

### Phase 3: Wire iconId resolution into PromptCommand

**Exit criteria**: `PromptCommand.ReadPromptData()` resolves badges via iconId when `TypeAppearances` is available. Falls back to existing `IconSet.GetIcon()` behavior when unavailable. All prompt tests pass.

### Phase 4: Fix crown glyph and verify nerd font rendering

**Exit criteria**: `icon_crown` nerd font glyph renders correctly in CaskaydiaCove NF. All 41 nerd font glyphs verified. `NerdFontIcons` type-name dictionary also updated with corrected crown glyph. Full test suite green.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/IconSet.cs` | Add `UnicodeIconsByIconId` (41 entries), `NerdFontIconsByIconId` (41 entries), `GetIconByIconId(string, string?)` method returning `string?`. Fix `NerdFontIcons["Epic"]` crown glyph. `DefaultIcon` remains `private const` (unchanged). |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add `_typeIconIds` field, `_iconMode` field. Extend `HumanOutputFormatter(DisplayConfig)` constructor to accept optional `List<TypeAppearanceConfig>?`. Change `GetTypeBadge()` from `private static` to `private` (instance method) and add iconId resolution via null-check pattern. |
| `src/Twig/Commands/PromptCommand.cs` | Add iconId resolution from `config.TypeAppearances` before calling `IconSet.GetIcon()`. Use `IconSet.GetIconByIconId()` with null-coalescing fallback pattern (`??`), no hardcoded `"·"` comparison. |
| `src/Twig/Program.cs` | Pass `config.TypeAppearances` to `HumanOutputFormatter` constructor. |
| `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | Add tests for `UnicodeIconsByIconId`, `NerdFontIconsByIconId`, `GetIconByIconId()`. Update crown glyph assertion if changed. |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Add tests for iconId-based badge resolution. Update constructor calls where TypeAppearances is relevant. |
| `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | Add tests for iconId-based badge resolution in prompt output. |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: Add iconId-keyed glyph dictionaries to IconSet

**Goal**: Add universal icon resolution by ADO icon ID with null-return semantics. Fix crown nerd font glyph.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add `UnicodeIconsByIconId` static dictionary to `IconSet` with 41 entries mapping each ADO icon ID to a Unicode glyph. Use case-insensitive `StringComparer.OrdinalIgnoreCase`. | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE |
| ITEM-002 | IMPL | Add `NerdFontIconsByIconId` static dictionary to `IconSet` with 41 entries mapping each ADO icon ID to a Nerd Font glyph. Prefer Codicon-range glyphs (U+EA60-U+EC24); use verified MDI fallbacks for `icon_diamond` (U+F01C8) and `icon_headphone` (U+F02CE) where no Codicon exists. All codepoints MUST be verified against Nerd Fonts `glyphnames.json` v3.4.0. | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE |
| ITEM-003 | IMPL | Add `GetIconByIconId(string mode, string? iconId)` static method to `IconSet` returning `string?`. Returns the glyph for the given icon ID and mode, or `null` if iconId is null or not found. `DefaultIcon` remains `private const` — no visibility change needed. | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE |
| ITEM-005 | IMPL | Replace `NerdFontIcons["Epic"]` glyph from `"\uDB81\uDD31"` (nf-md-crown, U+F0531) with the verified crown replacement glyph (default: `"\ueb59"`, nf-cod-star_full, U+EB59). | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE |
| ITEM-006 | TEST | Add `UnicodeIconsByIconId_ContainsAll41Icons` test verifying dictionary count is 41 and all known icon IDs are present. | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-007 | TEST | Add `NerdFontIconsByIconId_ContainsAll41Icons` test verifying dictionary count is 41 and all known icon IDs are present. | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-008 | TEST | Add `GetIconByIconId_KnownIconId_ReturnsGlyph` test for both unicode and nerd modes. | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-009 | TEST | Add `GetIconByIconId_UnknownIconId_ReturnsNull` and `GetIconByIconId_NullIconId_ReturnsNull` tests. | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-010 | TEST | Update existing `NerdFontIcons_ContainsAllKnownTypes` test if crown glyph changed (ITEM-005). | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-011 | TEST | Run full test suite to verify zero regressions from additive changes. | All test projects | DONE |

**Acceptance Criteria**:
- [x] `IconSet.UnicodeIconsByIconId.Count` is 41
- [x] `IconSet.NerdFontIconsByIconId.Count` is 41
- [x] `IconSet.GetIconByIconId("unicode", "icon_crown")` returns `"◆"`
- [x] `IconSet.GetIconByIconId("nerd", "icon_insect")` returns the bug nerd font glyph
- [x] `IconSet.GetIconByIconId("unicode", null)` returns `null`
- [x] `IconSet.GetIconByIconId("unicode", "icon_unknown_future")` returns `null`
- [x] `icon_trophy` nerd font glyph is `nf-cod-milestone` (U+EB20), NOT U+EB0B (nf-cod-issue_reopened)
- [x] Nerd font crown glyph renders correctly in CaskaydiaCove NF
- [x] All existing tests pass

**Completion Date**: 2026-03-16
**Notes**: All 5 codepoint fixes applied (Bug U+EAAF, Task/Issue U+EAB3, Test Case U+EA79, Change Request U+EAE1, Review U+EA70). GetIconByIconId parameter updated to `string? mode` with null-mode fallback documented. Reviewer identified two low-risk test gaps (case-insensitive iconId lookup, case-insensitive mode string) that do not block merge.

---

### EPIC-002: Wire iconId resolution into HumanOutputFormatter

**Goal**: Enable `HumanOutputFormatter` to resolve type badges via ADO icon ID when TypeAppearances data is available. Preserve fallback to hardcoded type-name switch.

**Prerequisites**: EPIC-001 (iconId dictionaries in IconSet).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Add `_typeIconIds` (`Dictionary<string, string>?`) and `_iconMode` (`string`) fields to `HumanOutputFormatter`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-013 | IMPL | Extend the `HumanOutputFormatter(DisplayConfig displayConfig)` constructor to accept an optional `List<TypeAppearanceConfig>? typeAppearances = null` parameter. Build `_typeIconIds` from it. Set `_iconMode` from `displayConfig.Icons`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-014 | IMPL | Update `GetTypeBadge(WorkItemType type)`: (1) Change method modifier from `private static` to `private` (remove `static`) so it can access instance fields `_typeIconIds` and `_iconMode`. (2) Add iconId pre-check: if `_typeIconIds` has the type name, call `IconSet.GetIconByIconId(_iconMode, iconId)`. If the result is not null, return it. Otherwise fall through to existing switch. Use null-check pattern (`is not null`), NOT comparison against `DefaultIcon` or hardcoded `"·"`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-015 | IMPL | Update `Program.cs` DI wiring to pass `config.TypeAppearances` to `HumanOutputFormatter` constructor. | `src/Twig/Program.cs` | DONE |
| ITEM-016 | TEST | Add `GetTypeBadge_CustomType_WithIconId_ReturnsIconIdGlyph` test: construct formatter with TypeAppearances containing `("Deliverable", "gold", "icon_trophy")`, verify badge for "Deliverable" type is `"★"`. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-017 | TEST | Add `GetTypeBadge_StandardType_WithIconId_ReturnsIconIdGlyph` test: verify that standard types with iconId data produce correct glyphs from iconId dictionary (should match existing behavior). | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-018 | TEST | Add `GetTypeBadge_CustomType_NoIconId_FallsBackToFirstLetter` test: construct formatter without TypeAppearances, verify custom type still shows first letter. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-019 | TEST | Verify existing badge tests pass unchanged (standard types, first-letter fallback). | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-020 | TEST | Run full test suite to verify zero regressions. | All test projects | DONE |

**Acceptance Criteria**:
- [x] Custom type "Deliverable" with `icon_trophy` shows `"★"` in unicode mode
- [x] Custom type "Deliverable" with `icon_trophy` shows nerd font trophy glyph in nerd mode
- [x] Standard type "Epic" with `icon_crown` shows `"◆"` (same as before)
- [x] Custom type without TypeAppearances shows first letter (unchanged)
- [x] Parameterless constructor still works (no TypeAppearances = no iconId resolution)
- [x] All existing tests pass

**Completion Date**: 2026-03-16
**Notes**: All 1095 tests pass. `_typeIconIds` and `_iconMode` fields added; constructor extended with optional `typeAppearances` parameter; `GetTypeBadge` changed from `private static` to `private` instance method with iconId pre-check using `is not null` pattern. `Program.cs` was already passing `cfg.TypeAppearances` from prior work (ITEM-015 no-op). Reviewer noted two minor test quality issues (not blocking): iconId lookup test could assert on null-TypeAppearances path more explicitly, and one test duplicates an existing scenario.

---

### EPIC-003: Wire iconId resolution into PromptCommand

**Goal**: Enable `PromptCommand` to resolve type badges via ADO icon ID when TypeAppearances data is available. Preserve fallback to existing `IconSet.GetIcon()` behavior.

**Prerequisites**: EPIC-001 (iconId dictionaries in IconSet).

> **DEPENDENCY NOTE**: `PromptCommand` is currently an internal class with **no CLI wiring** — there is no `Prompt()` method in `TwigCommands` and no `AddSingleton<PromptCommand>` in `Program.cs`. This EPIC modifies `PromptCommand`'s badge resolution logic so that when `twig prompt` is eventually wired (tracked separately), it will use iconId-based resolution. CLI registration of `twig prompt` is **out of scope** for this plan and MUST be handled by a separate plan or task.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-021 | IMPL | In `PromptCommand.ReadPromptData()`, after reading `type` from the DB (line 83), resolve iconId from `config.TypeAppearances` using the existing `ResolveColor`-style lookup pattern. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-022 | IMPL | Update badge resolution (line 90) to try `IconSet.GetIconByIconId(iconMode, iconId)` first using null-coalescing: `var badge = IconSet.GetIconByIconId(iconMode, iconId) ?? IconSet.GetIcon(icons, type);`. Do NOT use hardcoded `"·"` comparison — rely on `GetIconByIconId` returning `null` for unknown icon IDs. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-023 | TEST | Add `PromptBadge_WithTypeAppearances_ResolvesFromIconId` test: configure TypeAppearances with custom type + iconId, verify badge in prompt output matches iconId glyph. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-024 | TEST | Add `PromptBadge_NoTypeAppearances_FallsBackToTypeName` test: verify existing behavior when TypeAppearances is null. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-025 | TEST | Verify existing prompt badge tests pass unchanged. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-026 | TEST | Run full test suite to verify zero regressions. | All test projects | DONE |

**Acceptance Criteria**:
- [x] Custom type "Scenario" with `icon_crown` in TypeAppearances shows `"◆"` in prompt
- [x] Standard type "Bug" shows `"✦"` in prompt (unchanged)
- [x] No TypeAppearances = existing behavior (type-name lookup)
- [x] All existing prompt tests pass

**Completion Date**: 2026-03-16
**Notes**: `ResolveIconId()` helper added mirroring `ResolveColor()` pattern with `List.Find()` and `OrdinalIgnoreCase`. Badge resolution uses `GetIconByIconId(...) ?? GetIcon(...)` null-coalescing chain — no hardcoded `"·"` comparison. Reviewer confirmed implementation is correct and consistent with `HumanOutputFormatter.GetTypeBadge()` reference pattern. Two minor observations noted (non-blocking): iconId lookup test could be more explicit on the null-TypeAppearances path.

---

### EPIC-004:Verify nerd font rendering and finalize glyph assignments

**Goal**: Verify all 41 nerd font glyphs render correctly in CaskaydiaCove Nerd Font. Adjust any glyphs that don't render properly.

**Prerequisites**: EPIC-001 (glyphs defined).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-027 | TEST | Create a manual verification script that prints all 41 nerd font glyphs with their icon IDs in a terminal using CaskaydiaCove NF. Verify each renders as expected. | `tools/verify-nerd-font-glyphs.ps1` | DONE |
| ITEM-028 | IMPL | Replace any nerd font glyphs that render incorrectly with verified alternatives from the Codicon range. | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE (no changes needed — all 41 glyphs verified correct) |
| ITEM-029 | TEST | Run full test suite after any glyph changes. | All test projects | DONE (1,097 tests pass) |

**Acceptance Criteria**:
- [x] All 41 nerd font glyphs render as recognizable icons in CaskaydiaCove NF
- [x] No glyph renders as a box, tree, or unexpected shape
- [x] `icon_crown` specifically renders as a star or crown (not a tree)
- [x] Full test suite passes (1,097 tests: 409 Domain + 254 Infrastructure + 434 CLI)

**Completion Date**: 2026-03-16
**Notes**: All 35 unique codepoints verified against Nerd Fonts `glyphnames.json` v3.4.0 — all match exactly. Visual inspection of all 41 glyphs in CaskaydiaCove Nerd Font confirmed no boxes or unexpected shapes. No glyph corrections were needed. The two supplementary-plane glyphs (`icon_diamond` U+F01C8 → `\uDB80\uDDC8`, `icon_headphone` U+F02CE → `\uDB80\uDECE`) render correctly via surrogate-pair encoding. `icon_crown` uses `nf-cod-star_full` (U+EB59) in both `NerdFontIconsByIconId` and `NerdFontIcons` dictionaries (OQ-005 resolved). Verification script preserved at `tools/verify-nerd-font-glyphs.ps1` for future reference.

---

## References

| Resource | Relevance |
|----------|-----------|
| [ADO Work Item Icons - List API (v7.1)](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-icons/list?view=azure-devops-rest-7.1) | Authoritative list of all 41 ADO icon IDs |
| `src/Twig.Domain/ValueObjects/IconSet.cs` | Current icon registry — modified by this plan |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Primary consumer — modified by this plan |
| `src/Twig/Commands/PromptCommand.cs` | Secondary consumer — modified by this plan |
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | `TypeAppearanceConfig` with `IconId` property |
| `src/Twig.Domain/ValueObjects/WorkItemTypeAppearance.cs` | Domain record with `IconId` |
| `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` | Has `IconId` column in process_types table |
| `docs/projects/twig-icon-cleanup.plan.md` | Prior cleanup — removed PromptBadges, consolidated to IconSet |
| `docs/projects/twig-dynamic-process.plan.md` | Opened type system, added first-letter badge fallback |
| `docs/projects/twig-structural-audit.doc.md` | Identified GetTypeBadge/GetTypeIcon duality (CS-002, CS-005) |
| `docs/projects/twig-nerd-font-icons.plan.md` | Original IconSet and nerd font design |
| [Nerd Fonts Cheat Sheet](https://www.nerdfonts.com/cheat-sheet) | Codicon glyph reference for U+EA60-U+EC24 range |
| [Nerd Fonts `glyphnames.json` (v3.4.0)](https://github.com/ryanoasis/nerd-fonts/blob/master/glyphnames.json) | Authoritative codepoint reference for Nerd Font patched fonts. All NerdFontIconsByIconId codepoints verified against this source. |

---

## Complete ADO Icon ID Reference

The following table lists all 41 ADO icon IDs from the `GET /_apis/wit/workitemicons` API response, with their default type associations and proposed glyph mappings.

| ADO Icon ID | Common Type Associations | Unicode Glyph | Nerd Font Glyph | Nerd Font Name |
|-------------|-------------------------|---------------|-----------------|----------------|
| `icon_clipboard` | Generic / many types | □ | U+EAC0 | nf-cod-clippy |
| `icon_crown` | Epic, Feature, Scenario | ◆ | U+EB59 | nf-cod-star_full |
| `icon_trophy` | Deliverable | ★ | U+EB20 | nf-cod-milestone |
| `icon_list` | Task Group | ≡ | U+EB17 | nf-cod-list_unordered |
| `icon_book` | User Story | ● | U+EAA4 | nf-cod-book |
| `icon_sticky_note` | Feature (alt) | ▪ | U+EA7B | nf-cod-file |
| `icon_insect` | Bug | ✦ | U+EAAF | nf-cod-bug |
| `icon_traffic_cone` | Impediment | ⚠ | U+EA6C | nf-cod-warning |
| `icon_chat_bubble` | Feedback | ○ | U+EA6B | nf-cod-comment |
| `icon_flame` | Hotfix / urgent | ✦ | U+EAF2 | nf-cod-flame |
| `icon_megaphone` | Announcement | ◉ | U+EB1E | nf-cod-megaphone |
| `icon_code_review` | Code Review Request | ◈ | U+EAE1 | nf-cod-diff |
| `icon_code_response` | Code Review Response | ◈ | U+EAC4 | nf-cod-code |
| `icon_review` | Review | ◎ | U+EA70 | nf-cod-eye |
| `icon_response` | Response | ○ | U+EA6B | nf-cod-comment |
| `icon_test_plan` | Test Plan | □ | U+EBAF | nf-cod-notebook |
| `icon_test_suite` | Test Suite | □ | U+EB9C | nf-cod-library |
| `icon_test_case` | Test Case | □ | U+EA79 | nf-cod-beaker |
| `icon_test_step` | Test Step | □ | U+EB16 | nf-cod-list_ordered |
| `icon_test_parameter` | Test Parameter | □ | U+EB52 | nf-cod-settings |
| `icon_star` | Star | ★ | U+EB59 | nf-cod-star_full |
| `icon_ribbon` | Ribbon | ▪ | U+EAA5 | nf-cod-bookmark |
| `icon_chart` | Measure, Key Result | ▬ | U+EB03 | nf-cod-graph |
| `icon_headphone` | Support | ♪ | U+F02CE | nf-md-headset |
| `icon_key` | Security | ▣ | U+EB11 | nf-cod-key |
| `icon_airplane` | Travel / Release | ► | U+EB44 | nf-cod-rocket |
| `icon_car` | Vehicle | ► | U+EB44 | nf-cod-rocket |
| `icon_diamond` | Objective | ◇ | U+F01C8 | nf-md-diamond_stone |
| `icon_asterisk` | Generic | ✱ | U+EA6A | nf-cod-star_empty |
| `icon_database_storage` | Database | ▦ | U+EACE | nf-cod-database |
| `icon_government` | Governance | ▣ | U+EAC0 | nf-cod-clippy |
| `icon_gavel` | Legal | ▣ | U+EB12 | nf-cod-law |
| `icon_parachute` | Safety | ▽ | U+EA6C | nf-cod-warning |
| `icon_paint_brush` | Design | ▪ | U+EB2A | nf-cod-paintcan |
| `icon_palette` | Design | ◈ | U+EAC6 | nf-cod-color_mode |
| `icon_gear` | Settings / Infra | ⚙ | U+EAF8 | nf-cod-gear |
| `icon_check_box` | Task | □ | U+EAB3 | nf-cod-checklist |
| `icon_gift` | Customer Promise | ♦ | U+EAF9 | nf-cod-gift |
| `icon_test_beaker` | Test Case | □ | U+EA79 | nf-cod-beaker |
| `icon_broken_lightbulb` | Issue / Defect | ✦ | U+EA61 | nf-cod-lightbulb |
| `icon_clipboard_issue` | Issue | □ | U+EB0C | nf-cod-issues |
