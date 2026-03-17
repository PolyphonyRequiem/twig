---
goal: True Color Type Rendering and Unicode Type Badges
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-15
completion_date: 2026-03-15
owner: Twig CLI team
tags: [feature, ux, color, ansi, formatting, true-color, unicode, badges]
revision_notes: "Rev 1.1 — Corrected FR-012 class target (DisplayConfig, not TwigConfiguration), added explicit OrdinalIgnoreCase dictionary rebuild in constructor code, fixed constant count (9 not 8), addressed FormatWorkspace context item badge gap, updated revision_notes."
---

# True Color Rendering and Unicode Type Badges

## Executive Summary

This plan introduces 24-bit true color ANSI rendering for work item type colors and Unicode glyph badges into the Twig CLI. Today, `HumanOutputFormatter.GetTypeColor` uses hardcoded 3/4-bit ANSI constants (Magenta for Epic, Cyan for Feature, etc.) that cannot represent the actual colors assigned to work item types in Azure DevOps. This plan delivers four capabilities: (1) a `HexToAnsi` utility that converts 6-digit hex color strings (e.g., `"CC293D"`) to 24-bit true color ANSI escape sequences (`\x1b[38;2;R;G;Bm`), (2) runtime type-color resolution in `HumanOutputFormatter.GetTypeColor` that reads hex colors from `TwigConfiguration.TypeColors` (populated by a separate ADO API fetch plan) with fallback to hardcoded defaults, (3) Tier 1 unicode type badges (◆ Epic, ▪ Feature, ● Story/PBI, ✦ Bug, □ Task) rendered inline in `FormatWorkItem` and `FormatTree` output, and (4) preservation of existing 3/4-bit ANSI for STATE colors (Proposed=dim/gray, InProgress=blue, Resolved/Completed=green, Removed=red). The result is a visually richer CLI that aligns type colors with the ADO web portal while remaining gracefully degraded when ADO color data is unavailable.

---

## Background

### Current system state

The Twig CLI is a .NET 9 AOT-compiled tool that formats terminal output via `IOutputFormatter` implementations. The relevant components:

- **`HumanOutputFormatter`** (`src/Twig/Formatters/HumanOutputFormatter.cs`) — renders ANSI-colored output with 9 private constants (`Reset`, `Bold`, `Red`, `Green`, `Yellow`, `Blue`, `Magenta`, `Cyan`, `Dim`). All are 3/4-bit SGR codes.
- **`GetTypeColor(WorkItemType type)`** (line 194–205) — a `private static` method that maps `type.Value.ToLowerInvariant()` to hardcoded ANSI constants via switch expression: `"epic"` → `Magenta`, `"feature"` → `Cyan`, `"user story"|"product backlog item"|"requirement"` → `Blue`, `"bug"|"impediment"|"risk"` → `Red`, everything else → `Reset`.
- **`GetStateColor(string state)`** (line 207–220) — maps state strings to 3/4-bit ANSI. This method is NOT changing in this plan.
- **`TwigConfiguration`** (`src/Twig.Infrastructure/Config/TwigConfiguration.cs`) — POCO with `Organization`, `Project`, `Auth`, `Defaults`, `Seed`, `Display` sections. Does NOT currently have a `TypeColors` dictionary. A separate plan will add `TypeColors: Dictionary<string, string>` populated via ADO's `_apis/wit/workitemtypes` API (which returns `"color": "CC293D"` per type).
- **`AdoWorkItemTypeResponse`** DTO (`src/Twig.Infrastructure/Ado/Dtos/AdoWorkItemTypeResponse.cs`) — currently only has `Name`, `Description`, `ReferenceName`. Missing `Color` and `Icon` fields that the ADO REST API v7.1 returns.
- **`TwigJsonContext`** (`src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`) — source-generated JSON context for AOT. All serializable types must be registered here.
- **No unicode badges exist** — work item types are displayed as plain text (e.g., `"Type: Epic"`). No glyph/icon prefix exists in any formatter output.

### ADO REST API color data

The ADO REST API `GET _apis/wit/workitemtypes?api-version=7.1` returns per-type metadata including:

```json
{
  "name": "Bug",
  "color": "CC293D",
  "icon": { "id": "icon_insect", "url": "..." }
}
```

The `color` field is a 6-digit hex string (no `#` prefix, no alpha). Default ADO colors for standard Agile types:

| Type | ADO Hex Color |
|------|---------------|
| Epic | `FF7B00` (orange) |
| Feature | `773B93` (purple) |
| User Story | `009CCC` (teal/blue) |
| Bug | `CC293D` (red) |
| Task | `F2CB1D` (yellow) |

These colors are customizable per project. The hex values above are defaults — actual values vary by project and process template.

### Prior art

The `twig-color-wiring.plan.md` established the ANSI color design language and wired `IOutputFormatter` into all commands. That plan used 3/4-bit ANSI for both state and type colors. This plan extends type colors to 24-bit true color while leaving state colors on 3/4-bit ANSI (as specified in the constraints).

---

## Problem Statement

1. **Hardcoded type colors diverge from ADO**: `GetTypeColor` maps Epic→Magenta and Feature→Cyan using 3/4-bit ANSI, but ADO uses `FF7B00` (orange) for Epic and `773B93` (purple) for Feature. Users cannot visually correlate Twig output with their ADO portal.
2. **No mechanism to consume ADO color data**: Even when `TwigConfiguration.TypeColors` is populated by the ADO fetch plan, there is no code path to convert hex→ANSI and apply it at runtime.
3. **No visual type differentiation beyond color**: In tree views and work item lists, types are shown as plain text. A quick-scan unicode badge (like ◆ for Epic) would provide instant type recognition at a glance, complementing color.
4. **3/4-bit palette is insufficient**: The 8-color ANSI palette forces multiple unrelated types to share the same color (e.g., Bug shares Red with the "Removed" state color). 24-bit true color allows per-type unique colors without collision.

---

## Goals and Non-Goals

### Goals

- **G-1**: Create a `HexToAnsi` utility class that converts a 6-digit hex color string to a 24-bit true color ANSI foreground escape sequence (`\x1b[38;2;R;G;Bm`).
- **G-2**: Update `HumanOutputFormatter.GetTypeColor` to read from `TwigConfiguration.TypeColors` (a `Dictionary<string, string>` of type-name→hex-color) at runtime, convert via `HexToAnsi`, and fall back to hardcoded 3/4-bit defaults when no fetched color exists.
- **G-3**: Define a `TypeBadgeMap` in `HumanOutputFormatter` that maps work item type names to Tier 1 unicode glyphs: `◆` Epic, `▪` Feature, `●` Story/PBI, `✦` Bug, `□` Task.
- **G-4**: Wire unicode type badges into `FormatWorkItem` and `FormatTree` output, prefixed before the type name or work item title as appropriate.
- **G-5**: Preserve 3/4-bit ANSI for all STATE colors — no changes to `GetStateColor`.
- **G-6**: Maintain full AOT compatibility (no reflection, source-gen JSON only).
- **G-7**: All existing tests pass; new tests cover `HexToAnsi`, type-color resolution with/without config, and badge rendering.

### Non-Goals

- **NG-1**: Fetching type colors from the ADO API — that is a separate plan. This plan only consumes the `TypeColors` dictionary once it exists on `TwigConfiguration`.
- **NG-2**: Adding `Color` and `Icon` fields to `AdoWorkItemTypeResponse` DTO — that is part of the ADO fetch plan.
- **NG-3**: Nerd Font / extended icon support — deferred to a Tier 2 plan.
- **NG-4**: Changing state colors or state color implementation (they remain 3/4-bit ANSI).
- **NG-5**: True color for JSON or Minimal formatters — they do not emit ANSI codes.
- **NG-6**: Terminal capability detection (`COLORTERM`, `NO_COLOR`, `TERM`) — deferred.
- **NG-7**: Changing the `IOutputFormatter` interface — no new methods are added.

---

## Requirements

### Functional Requirements

- **FR-001**: `HexToAnsi.ToForeground(string hex)` MUST accept a 6-digit hex string (e.g., `"FF7B00"`, `"ff7b00"`, `"CC293D"`) and return `"\x1b[38;2;{R};{G};{B}m"` where R, G, B are the decimal values of the hex color components.
- **FR-002**: `HexToAnsi.ToForeground` MUST be case-insensitive (accept both `"ff7b00"` and `"FF7B00"`).
- **FR-003**: `HexToAnsi.ToForeground` MUST return `null` (or a designated fallback) for invalid input: null, empty, wrong length, non-hex characters.
- **FR-004**: `HumanOutputFormatter` MUST accept a `TwigConfiguration` (or its `TypeColors` dictionary) via constructor injection.
- **FR-005**: `GetTypeColor(WorkItemType type)` MUST first check `TwigConfiguration.TypeColors` for a matching key (case-insensitive lookup by `type.Value`). If found, convert the hex value via `HexToAnsi.ToForeground`. If conversion succeeds, return the true-color ANSI string. If not found or conversion fails, fall back to the existing hardcoded 3/4-bit switch expression.
- **FR-006**: The hardcoded fallback colors in `GetTypeColor` MUST remain: Epic→Magenta, Feature→Cyan, Story/PBI/Requirement→Blue, Bug/Impediment/Risk→Red, Task/TestCase/ChangeRequest/Review/Issue→Reset.
- **FR-007**: A `TypeBadgeMap` (private static dictionary or switch expression) MUST map type names to unicode glyphs: `"epic"`→`"◆"`, `"feature"`→`"▪"`, `"user story"`→`"●"`, `"product backlog item"`→`"●"`, `"requirement"`→`"●"`, `"bug"`→`"✦"`, `"task"`→`"□"`. Unknown types MUST map to `"■"` (filled square, generic fallback).
- **FR-008**: `FormatWorkItem` MUST render the type badge before the type name on the `Type:` line: `"  Type:      {typeColor}{badge} {item.Type}{Reset}"`.
- **FR-009**: `FormatTree` MUST render the type badge before each work item's ID in the tree: focused item line, child lines, and parent chain lines. The badge MUST use the type color.
- **FR-010**: `FormatWorkspace` MUST render the type badge before work items in sprint items and seeds sections. The active context item block (line 98 of `HumanOutputFormatter.cs`, which renders `{ws.ContextItem.Type} · {stateColor}{ws.ContextItem.State}{Reset}`) MUST also include the type badge and type color: `{typeColor}{badge} {ws.ContextItem.Type}{Reset} · {stateColor}{ws.ContextItem.State}{Reset}`. This ensures all three type-display locations in `FormatWorkspace` render badges consistently.
- **FR-011**: State colors (`GetStateColor`) MUST remain unchanged — 3/4-bit ANSI only.
- **FR-012**: `DisplayConfig` MUST be extended with a `TypeColors` property: `Dictionary<string, string>?` (nullable, defaults to null). When null or empty, all type color lookups fall through to hardcoded defaults.
- **FR-013**: `TypeColorsConfig` (or inline `Dictionary<string, string>`) MUST be registered in `TwigJsonContext` for AOT-compatible serialization.

### Non-Functional Requirements

- **NFR-001**: `HexToAnsi.ToForeground` MUST NOT allocate on the hot path beyond the returned string. Use `ReadOnlySpan<char>` parsing where possible.
- **NFR-002**: The change MUST NOT break AOT compatibility (`IsAotCompatible=true`, `PublishAot=true`, `JsonSerializerIsReflectionEnabledByDefault=false`).
- **NFR-003**: All existing `HumanOutputFormatterTests` MUST pass (some assertions may need updating for badge insertion).
- **NFR-004**: `HexToAnsi` MUST be a `static class` with pure static methods (no instance state) for simplicity and testability.
- **NFR-005**: The `TypeBadgeMap` MUST use only characters in Unicode BMP (Basic Multilingual Plane) — no emoji or surrogate pairs — to ensure rendering on all modern terminals.

---

## Proposed Design

### Architecture Overview

```
TwigConfiguration                    HexToAnsi (static utility)
  └─ TypeColors: Dict<string,string>?    └─ ToForeground("FF7B00") → "\x1b[38;2;255;123;0m"
       │                                      │
       │   injected via ctor                   │  called at runtime
       ▼                                       ▼
HumanOutputFormatter
  ├─ GetTypeColor(WorkItemType)  ─── config lookup → HexToAnsi → true-color ANSI
  │                                  fallback → hardcoded 3/4-bit ANSI
  ├─ GetTypeBadge(WorkItemType)  ─── static badge map → "◆", "▪", "●", "✦", "□"
  ├─ FormatWorkItem(...)         ─── uses badge + color
  ├─ FormatTree(...)             ─── uses badge + color
  └─ FormatWorkspace(...)        ─── uses badge + color

GetStateColor(string)  ─── UNCHANGED, 3/4-bit ANSI only
```

### Key Components

#### 1. `HexToAnsi` — Static Utility (`src/Twig/Formatters/HexToAnsi.cs`)

```csharp
namespace Twig.Formatters;

/// <summary>
/// Converts hex color strings to 24-bit true color ANSI escape sequences.
/// </summary>
internal static class HexToAnsi
{
    /// <summary>
    /// Converts a 6-digit hex color string to a 24-bit true color ANSI foreground escape sequence.
    /// Returns null for invalid input.
    /// </summary>
    internal static string? ToForeground(string? hex)
    {
        if (hex is null || hex.Length != 6)
            return null;

        if (!byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return null;

        return $"\x1b[38;2;{r};{g};{b}m";
    }
}
```

**Design rationale**: Pure static, zero-allocation parsing via `ReadOnlySpan<char>`. Returns `null` on failure rather than throwing — callers use `??` to fall back to 3/4-bit defaults. Internal visibility since only `HumanOutputFormatter` needs it. The `$""` interpolation for the return string is acceptable since this runs once per type per render call (not a tight loop).

#### 2. `TwigConfiguration.TypeColors` — Config Extension

Add a nullable dictionary property to `DisplayConfig`:

```csharp
public sealed class DisplayConfig
{
    public bool Hints { get; set; } = true;
    public int TreeDepth { get; set; } = 3;
    public Dictionary<string, string>? TypeColors { get; set; }  // NEW
}
```

This is placed on `DisplayConfig` (not at the root) because type colors are a display concern. The dictionary key is the work item type name (e.g., `"Epic"`, `"Bug"`), and the value is a 6-digit hex color string (e.g., `"FF7B00"`). When populated by the ADO fetch (separate plan), the config JSON would look like:

```json
{
  "display": {
    "hints": true,
    "treeDepth": 3,
    "typeColors": {
      "Epic": "FF7B00",
      "Feature": "773B93",
      "User Story": "009CCC",
      "Bug": "CC293D",
      "Task": "F2CB1D"
    }
  }
}
```

When null (the default), all lookups fall through to the hardcoded fallback in `GetTypeColor`.

#### 3. `HumanOutputFormatter` — Updated Constructor and Methods

The formatter transitions from a parameterless class to one that accepts `TwigConfiguration` (or just the `TypeColors` dictionary) via constructor:

```csharp
public sealed class HumanOutputFormatter : IOutputFormatter
{
    private readonly Dictionary<string, string>? _typeColors;

    public HumanOutputFormatter() : this(typeColors: null) { }

    public HumanOutputFormatter(Dictionary<string, string>? typeColors)
    {
        // Rebuild with OrdinalIgnoreCase to handle config keys in any casing
        // (e.g., "epic" from a normalizing JSON deserializer vs "Epic" from ADO API).
        _typeColors = typeColors is null
            ? null
            : new Dictionary<string, string>(typeColors, StringComparer.OrdinalIgnoreCase);
    }

    // ...existing code...

    private string GetTypeColor(WorkItemType type)
    {
        // Try fetched ADO color first
        if (_typeColors is not null &&
            _typeColors.TryGetValue(type.Value, out var hex))
        {
            var trueColor = HexToAnsi.ToForeground(hex);
            if (trueColor is not null)
                return trueColor;
        }

        // Fallback to hardcoded 3/4-bit ANSI
        return type.Value.ToLowerInvariant() switch
        {
            "epic" => Magenta,
            "feature" => Cyan,
            "user story" or "product backlog item" or "requirement" => Blue,
            "bug" or "impediment" or "risk" => Red,
            "task" or "test case" or "change request" or "review" or "issue" => Reset,
            _ => Reset,
        };
    }
}
```

**Key changes**:
- `GetTypeColor` is no longer `static` — it accesses instance field `_typeColors`.
- The parameterless constructor is preserved for backward compatibility with tests and DI registrations that don't supply type colors.

#### 4. `TypeBadgeMap` — Unicode Badge Resolution

A private static method on `HumanOutputFormatter`:

```csharp
private static string GetTypeBadge(WorkItemType type)
{
    return type.Value.ToLowerInvariant() switch
    {
        "epic" => "◆",
        "feature" => "▪",
        "user story" or "product backlog item" or "requirement" => "●",
        "bug" or "impediment" or "risk" => "✦",
        "task" or "test case" or "change request" or "review" or "issue" => "□",
        _ => "■",
    };
}
```

**Badge rationale**:

| Badge | Unicode | Codepoint | Type Family | Visual Weight |
|-------|---------|-----------|-------------|---------------|
| ◆ | Black Diamond | U+25C6 | Epic | Heavy — signals top-level container |
| ▪ | Black Small Square | U+25AA | Feature | Medium — distinct from diamond |
| ● | Black Circle | U+25CF | Story/PBI/Requirement | Medium — the "work unit" |
| ✦ | Black Four Pointed Star | U+2726 | Bug/Impediment/Risk | Attention-drawing — signals defect |
| □ | White Square | U+25A1 | Task/TestCase/etc. | Light — leaf-level work |
| ■ | Black Square | U+25A0 | Unknown/fallback | Neutral |

All glyphs are in Unicode BMP (U+0000–U+FFFF) and render correctly in Windows Terminal, iTerm2, GNOME Terminal, and all modern terminal emulators.

#### 5. Updated `FormatWorkItem` Output

```csharp
public string FormatWorkItem(WorkItem item, bool showDirty)
{
    var sb = new StringBuilder();
    var stateColor = GetStateColor(item.State);
    var dirty = showDirty && item.IsDirty ? $" {Yellow}•{Reset}" : "";

    sb.AppendLine($"{Bold}#{item.Id} {item.Title}{Reset}{dirty}");
    var typeColor = GetTypeColor(item.Type);
    var badge = GetTypeBadge(item.Type);
    sb.AppendLine($"  Type:      {typeColor}{badge} {item.Type}{Reset}");
    sb.AppendLine($"  State:     {stateColor}{item.State}{Reset}");
    sb.AppendLine($"  Assigned:  {item.AssignedTo ?? "(unassigned)"}");
    sb.AppendLine($"  Area:      {item.AreaPath}");
    sb.Append($"  Iteration: {item.IterationPath}");

    return sb.ToString();
}
```

Before: `Type:      \x1b[35mEpic\x1b[0m`
After:  `Type:      \x1b[38;2;255;123;0m◆ Epic\x1b[0m` (with ADO color) or `Type:      \x1b[35m◆ Epic\x1b[0m` (fallback)

#### 6. Updated `FormatTree` Output

Badges are added to:
- **Parent chain**: `{Dim}  {badge} {parent.Title} [{shorthand}]{Reset}`
- **Focused item**: `{Cyan}●{Reset} {typeColor}{badge}{Reset} {Bold}#{id} {title}{Reset} [...]`
- **Children**: `{connector}{activeMarker}{typeColor}{badge}{Reset} #{id} {title} [...]`

The type badge appears in the item's type color, immediately before the `#id` or title, creating a compact visual signal.

#### 7. Updated `FormatWorkspace` Output

Badges are added to the active context item, sprint items, and seeds:
- Active context item: `{typeColor}{badge} {ws.ContextItem.Type}{Reset} · {stateColor}{ws.ContextItem.State}{Reset}` (replacing the plain `{ws.ContextItem.Type}`)
- Sprint items: `{marker} {typeColor}{badge}{Reset} #{id} {title} [{stateColor}{state}{Reset}]{dirty}`
- Seeds: `{typeColor}{badge}{Reset} #{id} {title} ({type}){staleWarning}`

### Data Flow

**Type Color Resolution (with ADO colors)**:
```
TwigConfiguration.Display.TypeColors["Epic"] = "FF7B00"
  │
  ▼
HumanOutputFormatter.GetTypeColor(WorkItemType.Epic)
  │ _typeColors.TryGetValue("Epic", out "FF7B00") → true
  │ HexToAnsi.ToForeground("FF7B00") → "\x1b[38;2;255;123;0m"
  │
  └─ return "\x1b[38;2;255;123;0m"  (24-bit true color)
```

**Type Color Resolution (no ADO colors / fallback)**:
```
TwigConfiguration.Display.TypeColors = null
  │
  ▼
HumanOutputFormatter.GetTypeColor(WorkItemType.Epic)
  │ _typeColors is null → skip
  │ switch("epic") → Magenta ("\x1b[35m")
  │
  └─ return "\x1b[35m"  (3/4-bit fallback)
```

**Badge Resolution** (always static, independent of config):
```
HumanOutputFormatter.GetTypeBadge(WorkItemType.Epic)
  │ switch("epic") → "◆"
  └─ return "◆"
```

### DI Registration Changes

`Program.cs` must be updated to pass `TypeColors` to `HumanOutputFormatter`:

```csharp
services.AddSingleton<HumanOutputFormatter>(sp =>
{
    var cfg = sp.GetRequiredService<TwigConfiguration>();
    return new HumanOutputFormatter(cfg.Display.TypeColors);
});
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-001 | `HexToAnsi` as internal static class in `Twig.Formatters` namespace | Only `HumanOutputFormatter` needs hex→ANSI conversion. Keeping it in the same namespace/assembly avoids cross-project references. Static because it is pure and stateless. |
| DD-002 | `TypeColors` on `DisplayConfig` (not root `TwigConfiguration`) | Type colors are a display/rendering concern, parallel to `Hints` and `TreeDepth`. Placing them on `DisplayConfig` follows the existing section structure. |
| DD-003 | Nullable `Dictionary<string, string>?` (not empty dictionary default) | Null clearly signals "no ADO colors fetched yet" vs an empty dictionary. The null check in `GetTypeColor` is a single branch that short-circuits to fallback. |
| DD-004 | Parameterless constructor preserved on `HumanOutputFormatter` | Existing tests construct `new HumanOutputFormatter()` directly. The parameterless ctor chains to `this(typeColors: null)`, so all existing tests continue to work without modification (they get fallback colors). |
| DD-005 | `GetTypeColor` is instance method (not static) | Must access `_typeColors` instance field. The `static` modifier is removed. This is a minor breaking change in the method signature but the method is `private`, so no external callers are affected. |
| DD-005a | Constructor rebuilds `_typeColors` with `StringComparer.OrdinalIgnoreCase` | Config keys may arrive in any casing — title case from ADO API (`"Epic"`), lowercase from a normalizing JSON deserializer or hand-authored config (`"epic"`), etc. Rebuilding the dictionary in the constructor ensures all subsequent `TryGetValue` lookups are case-insensitive regardless of the source dictionary's comparer. The one-time cost of dictionary rebuild is negligible (typically ≤20 entries). |
| DD-006 | Badge glyphs are BMP-only Unicode (no emoji) | Emoji rendering is inconsistent across terminals and requires double-width character handling. BMP geometric shapes (◆, ▪, ●, ✦, □) are single-width and universally supported. |
| DD-007 | Fallback badge `■` for unknown types | Rather than showing no badge for unknown types, a generic filled square provides visual consistency. Users always see a badge prefix. |
| DD-008 | True color only for TYPE colors, 3/4-bit for STATE colors | States are a small fixed set (5 categories) well-served by the 8-color palette. Types can be customized per project with arbitrary hex colors that need 24-bit representation. This separation is explicit in the constraints. |
| DD-009 | `HexToAnsi.ToForeground` returns `null` on failure (not exception) | The caller (`GetTypeColor`) needs a simple fallback mechanism. `null` + `??` pattern is idiomatic C# and avoids try/catch overhead. Invalid hex in config is a data quality issue, not an exceptional condition. |

---

## Alternatives Considered

### Alt-1: Cache true-color ANSI strings in a dictionary at startup

Pre-compute all `HexToAnsi.ToForeground` conversions at `HumanOutputFormatter` construction time and store in a `Dictionary<string, string>` keyed by type name.

**Pros**: Zero per-render overhead; `GetTypeColor` becomes a single dictionary lookup.
**Cons**: Premature optimization — `HexToAnsi.ToForeground` is trivially fast (3 `byte.TryParse` calls + one string interpolation). The number of type lookups per render is ≤20. Adding a cache dictionary increases constructor complexity and memory for negligible gain.
**Decision**: Rejected for now. Can be added later if profiling shows need. However, this is a valid optimization path — the implementation SHOULD be designed so that caching can be added transparently inside `GetTypeColor` without changing callers.

### Alt-2: Put `TypeColors` on root `TwigConfiguration` instead of `DisplayConfig`

**Pros**: Simpler dotpath (`typeColors.Epic` instead of `display.typeColors.Epic`).
**Cons**: Breaks the existing config sectioning convention. `TypeColors` is a display concern and belongs alongside `Hints` and `TreeDepth`.
**Decision**: Rejected in favor of `DisplayConfig.TypeColors`.

### Alt-3: Use a dedicated `TypeColorConfig` class instead of `Dictionary<string, string>`

```csharp
public sealed class TypeColorConfig
{
    public string? Epic { get; set; }
    public string? Feature { get; set; }
    // ...
}
```

**Pros**: Strongly typed; IntelliSense support.
**Cons**: Not extensible to custom work item types. ADO allows custom types with custom colors. A `Dictionary<string, string>` naturally handles any type name. A fixed class would need updating every time a new type is encountered.
**Decision**: Rejected. Dictionary is the correct abstraction for an open-ended key set.

### Alt-4: Embed badge glyphs in `WorkItemType` value object

Add a `Badge` property to `WorkItemType` that returns the unicode glyph.

**Pros**: Badge is co-located with the type definition.
**Cons**: `WorkItemType` is in the Domain layer (`Twig.Domain.ValueObjects`) and should not contain presentation concerns. Badges are a human-formatter-specific rendering detail.
**Decision**: Rejected. Badges belong in `HumanOutputFormatter`.

---

## Dependencies

### Internal dependencies

| Dependency | Status | Note |
|------------|--------|------|
| `HumanOutputFormatter` | Existing | Modified — constructor, `GetTypeColor`, `FormatWorkItem`, `FormatTree`, `FormatWorkspace` |
| `DisplayConfig` | Existing | Extended — new `TypeColors` property |
| `TwigJsonContext` | Existing | May need update if `Dictionary<string, string>` is not already registered (it IS — line 19) |
| `OutputFormatterFactory` | Existing | No change needed — already references `HumanOutputFormatter` |
| `Program.cs` DI | Existing | Updated — `HumanOutputFormatter` registration passes `TypeColors` |

### External dependencies

None. All required capabilities are built into .NET 9 (`byte.TryParse`, `ReadOnlySpan<char>`, string interpolation).

### Sequencing constraints

- This plan can be implemented independently of the ADO type-color fetch plan. When `TypeColors` is null (the default), all type colors use the existing hardcoded fallbacks.
- The ADO fetch plan MUST populate `DisplayConfig.TypeColors` for true-color rendering to activate. Until then, this plan provides the consumption mechanism.
- This plan SHOULD be implemented after the `twig-color-wiring.plan.md` changes are complete (since that plan establishes the DI registration pattern for `HumanOutputFormatter`).

---

## Impact Analysis

### Components affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `HumanOutputFormatter` | Constructor change + method updates | Medium — test assertions must be updated for badge insertion |
| `DisplayConfig` | New property (additive) | Low |
| `Program.cs` | DI registration update | Low |
| `HexToAnsi` | New file | None |
| `HumanOutputFormatterTests` | Assertion updates | Medium — tests check for specific ANSI codes and text patterns |

### Backward compatibility

- **Config file**: Adding `typeColors` to `DisplayConfig` is backward compatible. Existing config files without the key will deserialize to `null` (the default). No migration needed.
- **Test output**: Tests that assert exact ANSI output (e.g., `result.ShouldContain("\x1b[35m")` for Epic) will continue to pass when `TypeColors` is null (fallback colors are the same). Tests that check for `"Type:"` line content will need updating to include the badge character.
- **CLI output**: Users will see badges in type output. This is a visual enhancement, not a breaking change. Scripts parsing `--output minimal` or `--output json` are unaffected (badges are human-formatter only).

### Performance

- `HexToAnsi.ToForeground`: ~3 `byte.TryParse` calls + 1 string interpolation. Negligible.
- `GetTypeBadge`: Single switch expression on a string. Negligible.
- `GetTypeColor`: One dictionary lookup + potential `HexToAnsi` call. Negligible.
- No hot-loop or per-character processing. Total overhead is unmeasurable.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Unicode badges don't render on some terminals | Low | Low | All chosen glyphs are BMP geometric shapes supported by every modern terminal. Worst case: terminal shows a replacement character (□). |
| `TwigConfiguration.TypeColors` key casing mismatch with `WorkItemType.Value` | Medium | Medium | Constructor rebuilds `_typeColors` with `StringComparer.OrdinalIgnoreCase` (see DD-005a). All `TryGetValue` lookups are guaranteed case-insensitive regardless of source dictionary comparer. |
| ADO hex colors include `#` prefix or alpha channel | Low | Low | `HexToAnsi.ToForeground` validates length=6 and returns null for invalid input. The ADO REST API v7.1 returns 6-digit hex without `#`. If a future API change adds `#`, the separate ADO fetch plan should strip it before storing. |
| Existing test assertions break due to badge insertion | High | Low | Known change. Tests will be updated in Epic 3 to expect badge characters. |

---

## Open Questions

| # | Question | Impact | Owner |
|---|----------|--------|-------|
| OQ-1 | Should `FormatTree` show the type badge in the parent chain (dimmed ancestors)? The current plan includes it, but it adds visual noise to a dimmed line. | Visual fidelity of tree output | Twig CLI team |
| OQ-2 | Should badges be configurable (e.g., `display.badges = false` to disable)? | Scope | Twig CLI team |
| OQ-3 | When `TypeColors` is populated, should the `FormatWorkItem` output show the hex code (for debugging) or just use the color? | Debug UX | Twig CLI team |
| OQ-4 | Should the badge appear in `FormatWorkspace` seed lines, which already show the type in parentheses — e.g., `□ #-1 My Task (Task)` — or is that redundant? | Visual consistency | Twig CLI team |

---

## Implementation Phases

### Phase 1: Core Utility and Config (Epic 1)
**Exit criteria**: `HexToAnsi` passes all unit tests. `DisplayConfig.TypeColors` is addable to config JSON and deserializes correctly. No existing tests broken.

### Phase 2: Formatter Integration (Epic 2)
**Exit criteria**: `HumanOutputFormatter` reads `TypeColors`, applies true-color ANSI, falls back to 3/4-bit defaults. Badges render in `FormatWorkItem`, `FormatTree`, `FormatWorkspace`. All formatter tests pass (updated for badges).

### Phase 3: DI Wiring and Integration Testing (Epic 3)
**Exit criteria**: `Program.cs` passes `TypeColors` to `HumanOutputFormatter`. Integration tests verify end-to-end badge+color rendering. All CI tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Formatters/HexToAnsi.cs` | Static utility: hex color string → 24-bit true color ANSI escape sequence |
| `tests/Twig.Cli.Tests/Formatters/HexToAnsiTests.cs` | Unit tests for `HexToAnsi.ToForeground` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `TypeColors` property to `DisplayConfig` |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add constructor accepting `Dictionary<string, string>?`, change `GetTypeColor` from static→instance with config lookup + fallback, add `GetTypeBadge` static method, update `FormatWorkItem`/`FormatTree`/`FormatWorkspace` to include badges |
| `src/Twig/Program.cs` | Update `HumanOutputFormatter` DI registration to pass `TypeColors` |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Update assertions for badge characters in output, add tests for true-color rendering with config |

### Deleted Files

None.

---

## Implementation Plan

### EPIC-001: HexToAnsi Utility and Config Extension

**Goal**: Create the `HexToAnsi` static utility class and extend `DisplayConfig` with the `TypeColors` property.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create `HexToAnsi` static class in `src/Twig/Formatters/HexToAnsi.cs` with `ToForeground(string? hex)` method. Parse 6-digit hex string to R, G, B bytes using `byte.TryParse` with `NumberStyles.HexNumber`. Return `$"\x1b[38;2;{r};{g};{b}m"` on success, `null` on failure. | `src/Twig/Formatters/HexToAnsi.cs` | DONE |
| ITEM-002 | TEST | Create `tests/Twig.Cli.Tests/Formatters/HexToAnsiTests.cs`. Test cases: valid 6-digit hex (uppercase, lowercase, mixed case), returns correct ANSI string; invalid input (null, empty, 3-digit, 7-digit, non-hex chars) returns null; boundary values (`"000000"` → `\x1b[38;2;0;0;0m`, `"FFFFFF"` → `\x1b[38;2;255;255;255m`); known ADO colors (`"CC293D"` → `\x1b[38;2;204;41;61m`). | `tests/Twig.Cli.Tests/Formatters/HexToAnsiTests.cs` | DONE |
| ITEM-003 | IMPL | Add `public Dictionary<string, string>? TypeColors { get; set; }` property to `DisplayConfig` class in `TwigConfiguration.cs`. Ensure default is `null`. | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-004 | IMPL | Verify `Dictionary<string, string>` is registered in `TwigJsonContext.cs` for AOT serialization. It is already registered on line 19 (`[JsonSerializable(typeof(Dictionary<string, string>))]`). Confirm no additional registration is needed. If `Dictionary<string, string>?` needs separate handling, add it. | `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | DONE |

**Acceptance Criteria**:
- [x] `HexToAnsi.ToForeground("FF7B00")` returns `"\x1b[38;2;255;123;0m"`
- [x] `HexToAnsi.ToForeground("cc293d")` returns `"\x1b[38;2;204;41;61m"` (case-insensitive)
- [x] `HexToAnsi.ToForeground(null)` returns `null`
- [x] `HexToAnsi.ToForeground("XYZ")` returns `null`
- [x] `DisplayConfig.TypeColors` defaults to `null`
- [x] Config JSON with `"typeColors": {"Epic": "FF7B00"}` deserializes correctly
- [x] All existing tests pass

### EPIC-002:HumanOutputFormatter True Color and Badges

**Goal**: Update `HumanOutputFormatter` to consume `TypeColors` for true-color rendering and add unicode type badges to all output methods.

**Prerequisites**: EPIC-001 (HexToAnsi and DisplayConfig.TypeColors must exist).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | Add constructor to `HumanOutputFormatter` that accepts `Dictionary<string, string>? typeColors`. Rebuild the dictionary with `StringComparer.OrdinalIgnoreCase` for case-insensitive lookup: `_typeColors = typeColors is null ? null : new Dictionary<string, string>(typeColors, StringComparer.OrdinalIgnoreCase)`. Store as `private readonly` field `_typeColors`. Preserve parameterless constructor via `this(typeColors: null)` chaining. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-006 | IMPL | Change `GetTypeColor` from `private static` to `private` (instance method). Add config-first lookup: check `_typeColors` for `type.Value` key, convert via `HexToAnsi.ToForeground`, return true-color string if non-null. Fall back to existing switch expression. Case-insensitive lookup is guaranteed by the constructor, which rebuilds the dictionary with `StringComparer.OrdinalIgnoreCase` (see DD-005a). No additional case handling is needed in `GetTypeColor` itself. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-007 | IMPL | Add `private static string GetTypeBadge(WorkItemType type)` method with switch expression mapping: `"epic"`→`"◆"`, `"feature"`→`"▪"`, `"user story"|"product backlog item"|"requirement"`→`"●"`, `"bug"|"impediment"|"risk"`→`"✦"`, `"task"|"test case"|"change request"|"review"|"issue"`→`"□"`, `_`→`"■"`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-008 | IMPL | Update `FormatWorkItem`: Change the `Type:` line from `$"  Type:      {typeColor}{item.Type}{Reset}"` to `$"  Type:      {typeColor}{badge} {item.Type}{Reset}"` where `badge = GetTypeBadge(item.Type)`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-009 | IMPL | Update `FormatTree`: Add type badge to parent chain lines (`{Dim}  {badge} {parent.Title} [{shorthand}]{Reset}`), focused item line (add `{typeColor}{badge}{Reset}` before `#{id}`), and child lines (add `{typeColor}{badge}{Reset}` before `#{id}`). Ensure badge uses type color, not state color. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-010 | IMPL | Update `FormatWorkspace`: (a) In the active context item block (line 98), change `$"          {ws.ContextItem.Type} · {stateColor}{ws.ContextItem.State}{Reset}"` to `$"          {typeColor}{badge} {ws.ContextItem.Type}{Reset} · {stateColor}{ws.ContextItem.State}{Reset}"` where `typeColor = GetTypeColor(ws.ContextItem.Type)` and `badge = GetTypeBadge(ws.ContextItem.Type)`. (b) Add type badge before work items in sprint items section. (c) Add type badge before work items in seeds section. Sprint items: `{typeColor}{badge}{Reset} #{id} {title}`. Seeds: `{typeColor}{badge}{Reset} #{id} {title} ({type})`. | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-011 | TEST | Update `HumanOutputFormatterTests`: Update `FormatWorkItem_ShowsTypeColor_ForEpic` to also check for `"◆"` badge. Add new test `FormatWorkItem_ShowsBadge_ForBug` verifying `"✦"` appears. Add test `FormatWorkItem_UsesTrueColor_WhenTypeColorsConfigured` that constructs formatter with `TypeColors = { ["Epic"] = "FF7B00" }` and asserts output contains `"\x1b[38;2;255;123;0m"`. Add test `FormatWorkItem_FallsBackTo3BitColor_WhenNoTypeColors` verifying `"\x1b[35m"` (Magenta) for Epic with null `TypeColors`. Add test `FormatWorkItem_UsesTrueColor_CaseInsensitiveKey` that constructs formatter with lowercase key `{ ["epic"] = "FF7B00" }` and verifies true-color output for `WorkItemType.Epic` (Value = `"Epic"`). | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-012 | TEST | Add `FormatTree_ShowsTypeBadges` test: build a tree with Epic focus and Task children, verify `"◆"` appears for Epic and `"□"` for Task children. Add `FormatWorkspace_ShowsTypeBadges` test: build workspace with Bug sprint item, verify `"✦"` appears. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `FormatWorkItem` output includes type badge before type name (e.g., `"◆ Epic"`)
- [x] `FormatTree` output includes type badges for focus, children, and parent chain
- [x] `FormatWorkspace` output includes type badges for active context item, sprint items, and seeds
- [x] With `TypeColors = { ["Epic"] = "FF7B00" }`, output contains `\x1b[38;2;255;123;0m`
- [x] With `TypeColors = null`, output contains fallback `\x1b[35m` for Epic
- [x] State colors remain unchanged (Blue for Active, Green for Done, etc.)
- [x] All existing tests pass (with assertion updates for badges)

### EPIC-003: DI Wiring and Integration

**Goal**: Wire the updated `HumanOutputFormatter` into the DI container with `TypeColors` from config.

**Prerequisites**: EPIC-002 (formatter must accept TypeColors in constructor).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-013 | IMPL | Update `Program.cs` `HumanOutputFormatter` DI registration from `services.AddSingleton<HumanOutputFormatter>()` to `services.AddSingleton<HumanOutputFormatter>(sp => { var cfg = sp.GetRequiredService<TwigConfiguration>(); return new HumanOutputFormatter(cfg.Display.TypeColors); })`. | `src/Twig/Program.cs` | DONE |
| ITEM-014 | TEST | Add integration-level test (or verify existing test coverage) that constructs `HumanOutputFormatter` with a populated `TypeColors` dictionary and renders a work item, verifying both true-color ANSI and badge appear in output. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-015 | TEST | Run full test suite (`dotnet test`) and verify all tests pass. Fix any assertion failures caused by badge insertion in existing tests (e.g., tests that check exact output strings). | All test files | DONE |

**Acceptance Criteria**:
- [x] `HumanOutputFormatter` is registered in DI with `TypeColors` from config
- [x] `OutputFormatterFactory.GetFormatter("human")` returns a formatter with TypeColors-aware `GetTypeColor`
- [x] `dotnet test` passes with zero failures
- [x] `dotnet build` succeeds with zero warnings related to this change

---

## References

- [ADO REST API: Work Item Types - List (v7.1)](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/list?view=azure-devops-rest-7.1) — API returning `color` field per work item type
- [ANSI Escape Codes — 24-bit color](https://en.wikipedia.org/wiki/ANSI_escape_code#24-bit) — `ESC[38;2;r;g;bm` for foreground true color
- [Unicode Geometric Shapes block (U+25A0–U+25FF)](https://www.unicode.org/charts/PDF/U25A0.pdf) — source of badge glyphs
- `docs/projects/twig-color-wiring.plan.md` — prior plan establishing ANSI color design language and formatter wiring
- `src/Twig/Formatters/HumanOutputFormatter.cs` — current implementation with hardcoded 3/4-bit type colors
- `src/Twig.Infrastructure/Config/TwigConfiguration.cs` — current configuration POCO
