---
goal: Tier 2 Nerd Font icon stub — config plumbing, IconSet class, and formatter wiring
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-15
owner: Twig CLI team
tags: [feature, cli, ux, icons, nerd-font, stub]
revision_notes: "Rev 1.1: Fixed surrogate pair math errors for nf-md-crown (U+F0531) and nf-md-bookmark (U+F00C0). Updated AOT claim for JsonStringEnumConverter<T> accuracy. Corrected stale dependency framing for FormatHint/FormatInfo. Clarified Font Awesome codepoint range. Updated risk table."
---

# Introduction

This document describes the solution design and implementation plan for adding Nerd Font icon support as a configurable opt-in feature in the Twig CLI. This is a **stub-only** plan — it establishes the configuration plumbing, icon map data structure, and formatter wiring, but does **not** integrate icons into every rendering path.

**Background prompt:** Plan: Tier 2 Nerd Font Icon Stub. SCOPE: This is a STUB-ONLY plan for future implementation. (1) Add a `display.icons` config key to TwigConfiguration (values: `unicode` default, `nerd` opt-in). (2) Create an `IconSet` class with two static dictionaries — UnicodeIcons (◆▪●✦□) and NerdFontIcons (Nerd Font glyphs for Epic/Feature/Story/Bug/Task). (3) Wire IconSet selection into HumanOutputFormatter based on config value. (4) Add `twig config display.icons unicode|nerd` command support. (5) This plan should be SMALL — just the config plumbing and icon map, not full rendering integration.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: Functional requirements use `FR-` prefix, non-functional requirements use `NFR-`, failure modes use `FM-`, and acceptance criteria use `AC-`. These enable traceability across sections.

---

## Executive Summary

The Twig CLI currently uses plain Unicode characters (e.g., `●` for active marker, `•` for dirty marker) in its `HumanOutputFormatter`. Users with Nerd Font–patched terminal fonts can display richer, more semantically meaningful glyphs — but the CLI has no mechanism to opt into them. This stub plan introduces: (1) a `display.icons` configuration key on `DisplayConfig` with values `"unicode"` (default) and `"nerd"`, (2) an `IconSet` class in the Domain layer providing two static dictionaries that map work item types to icon characters, (3) wiring in `HumanOutputFormatter` to select the correct `IconSet` based on the config value, and (4) `twig config display.icons unicode|nerd` read/write support. The scope is deliberately narrow — just the config plumbing and icon data, not full rendering integration across all formatter methods. Full rendering integration is deferred to a follow-up plan.

---

## Background

### Current system state

The Twig CLI is a .NET 9 AOT-compiled tool using `ConsoleAppFramework` for command routing. The architecture relevant to this plan:

- **`TwigConfiguration`** (`src/Twig.Infrastructure/Config/TwigConfiguration.cs`) — POCO loaded from `.twig/config` JSON. Contains `DisplayConfig Display` with `Hints: bool = true` and `TreeDepth: int = 3`. No icon-related configuration exists.
- **`DisplayConfig`** — nested POCO within `TwigConfiguration`. Serialized via `TwigJsonContext` (AOT source-gen JSON).
- **`TwigConfiguration.SetValue()`** — reflection-free switch on known dot-paths (e.g., `"display.hints"`, `"display.treedepth"`). Returns `bool` success.
- **`ConfigCommand`** (`src/Twig/Commands/ConfigCommand.cs`) — implements `twig config <key> [<value>]`. Uses `SetValue()` for writes, `GetValue()` switch for reads.
- **`HumanOutputFormatter`** (`src/Twig/Formatters/HumanOutputFormatter.cs`) — ANSI-colored output formatter. Uses hardcoded Unicode characters (`●` active marker, `•` dirty marker, `✓` success, `⚠` stale warning). No mechanism to swap these for alternative glyph sets.
- **`WorkItemType`** (`src/Twig.Domain/ValueObjects/WorkItemType.cs`) — `readonly record struct` with static instances (`Epic`, `Feature`, `UserStory`, `ProductBacklogItem`, `Bug`, `Task`, etc.) and a `string Value` property. The key used for icon map lookups.
- **`TwigJsonContext`** (`src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`) — source-generated `JsonSerializerContext` with `[JsonSerializable(typeof(DisplayConfig))]`. Any new properties on `DisplayConfig` are automatically included in serialization.

### Prior art — Tier 1 Unicode badges

The `twig-color-wiring.plan.md` (Rev 2) references "Tier 1 Unicode badges" and a "Nerd Font stub" as future work. The color-wiring plan established the type-color mapping in `HumanOutputFormatter.GetTypeColor()` and the state-color vocabulary. This plan is the natural successor — adding icon glyphs alongside the existing color treatment.

### Nerd Font ecosystem

[Nerd Fonts](https://www.nerdfonts.com/) patches developer-targeted fonts with 10,000+ glyphs from sets including Codicons (VS Code icons), Material Design Icons, Font Awesome, Octicons, and others. Relevant glyph sets for this plan:

| Set | Codepoint Range | Relevance |
|-----|----------------|-----------|
| Codicons (`nf-cod-*`) | U+EA60 – U+EC1E | VS Code icons: `bug`, `bookmark`, `rocket`, `checklist` |
| Material Design Icons (`nf-md-*`) | U+F0001 – U+F1AF0 | Largest set: `crown`, `feature-search`, `bug`, `checkbox-marked` |
| Font Awesome (`nf-fa-*`) | U+ED00 – U+EFCE, U+F000 – U+F2FF | General: `bug`, `bookmark`, `tasks` |

Nerd Font glyphs require the user's terminal font to be a Nerd Font-patched variant (e.g., "FiraCode Nerd Font", "JetBrainsMono Nerd Font"). If the font is not patched, Nerd Font codepoints render as missing-glyph boxes (□ or tofu). This is why the feature MUST be opt-in (`"nerd"`), not default.

---

## Problem Statement

1. **No icon differentiation by type**: `HumanOutputFormatter` uses `GetTypeColor()` to colorize work item types but provides no visual icon prefix. Users scanning output must read the type text to distinguish Epics from Bugs from Tasks.
2. **No glyph configuration**: There is no mechanism for users to opt into richer glyph sets (Nerd Fonts) or remain on safe Unicode defaults. The hardcoded `●`, `•`, `✓`, `⚠` characters cannot be swapped.
3. **No icon data structure**: No centralized mapping exists between `WorkItemType` and an icon glyph. Each place that could use an icon would need its own mapping — violating DRY.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Add `display.icons` config key to `DisplayConfig` with values `"unicode"` (default) and `"nerd"`.
2. **G-2**: Create an `IconSet` class with two static dictionaries mapping `WorkItemType.Value` strings to icon characters.
3. **G-3**: Wire `IconSet` selection into `HumanOutputFormatter` so the formatter has access to the selected icon set based on config.
4. **G-4**: Support `twig config display.icons unicode|nerd` for reading and writing the icon mode.
5. **G-5**: All existing tests pass; new tests cover the new config key, `IconSet`, and formatter wiring.

### Non-Goals

- **NG-1**: Full rendering integration — inserting icons into `FormatWorkItem`, `FormatTree`, `FormatWorkspace`, etc. This is a stub; rendering integration is deferred.
- **NG-2**: Custom/user-defined icon sets — only `"unicode"` and `"nerd"` are supported.
- **NG-3**: Auto-detection of Nerd Font availability — there is no reliable cross-platform way to detect this.
- **NG-4**: Icons in `JsonOutputFormatter` or `MinimalOutputFormatter` — icons are a human-output concern only.
- **NG-5**: Icon rendering for state categories (active, done, etc.) — this plan covers type icons only.

---

## Requirements

### Functional Requirements

- **FR-001**: `DisplayConfig` MUST have a `string Icons` property with a default value of `"unicode"`.
- **FR-002**: `TwigConfiguration.SetValue("display.icons", value)` MUST accept `"unicode"` and `"nerd"` (case-insensitive) and return `true`. MUST return `false` for any other value.
- **FR-003**: `ConfigCommand.GetValue("display.icons")` MUST return the current `Icons` value.
- **FR-004**: `IconSet` MUST be a `static class` in `Twig.Domain` with two public static `IReadOnlyDictionary<string, string>` properties: `UnicodeIcons` and `NerdFontIcons`.
- **FR-005**: `UnicodeIcons` MUST contain entries for at least: `Epic`, `Feature`, `User Story`, `Product Backlog Item`, `Bug`, `Task`, `Requirement`, `Impediment`, `Issue`, `Test Case`, `Change Request`, `Review`, `Risk`.
- **FR-006**: `NerdFontIcons` MUST contain entries for the same keys as `UnicodeIcons`, using Nerd Font glyph codepoints from the Codicons or Material Design Icons sets.
- **FR-007**: `IconSet` MUST expose a static `GetIcons(string mode)` method that returns `UnicodeIcons` for `"unicode"` and `NerdFontIcons` for `"nerd"`. Any unrecognized value MUST fall back to `UnicodeIcons`.
- **FR-008**: `HumanOutputFormatter` MUST accept `DisplayConfig` in its constructor and store the resolved `IReadOnlyDictionary<string, string>` icon map via `IconSet.GetIcons(displayConfig.Icons)`.
- **FR-009**: `HumanOutputFormatter` MUST expose a `GetTypeIcon(WorkItemType type)` method (internal or public) that looks up the icon string for a given type, falling back to a default icon if the type is not in the map.
- **FR-010**: `HumanOutputFormatter` DI registration in `Program.cs` MUST be updated to pass `DisplayConfig` to the formatter constructor.

### Non-Functional Requirements

- **NFR-001**: All additions MUST be AOT-safe. No reflection, no `System.Type` lookups, no dynamic code generation.
- **NFR-002**: `IconSet` dictionaries MUST be `static readonly` and initialized inline — no lazy initialization or locking.
- **NFR-003**: The `display.icons` config key MUST round-trip through JSON serialization (`SaveAsync` → `LoadAsync`) without data loss.
- **NFR-004**: `HumanOutputFormatter` constructor change MUST NOT break existing tests — tests that construct the formatter directly MUST be updated to pass a `DisplayConfig`.

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  .twig/config (JSON)                                            │
│    { "display": { "hints": true, "treeDepth": 3, "icons": "unicode" } }  │
└─────────────┬───────────────────────────────────────────────────┘
              │ LoadAsync
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  TwigConfiguration.Display (DisplayConfig)                      │
│    .Hints = true                                                │
│    .TreeDepth = 3                                               │
│    .Icons = "unicode"  ← NEW                                   │
└─────────────┬───────────────────────────────────────────────────┘
              │ injected via DI
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  HumanOutputFormatter(DisplayConfig displayConfig)  ← CHANGED  │
│    _icons = IconSet.GetIcons(displayConfig.Icons)               │
│    GetTypeIcon(WorkItemType) → string                           │
└─────────────────────────────────────────────────────────────────┘
              │ reads from
              ▼
┌─────────────────────────────────────────────────────────────────┐
│  IconSet (static class, Twig.Domain)                            │
│    UnicodeIcons: { "Epic" → "◆", "Feature" → "▪", ... }       │
│    NerdFontIcons: { "Epic" → "\uDB81\uDD31", ... }             │
│    GetIcons("unicode"|"nerd") → IReadOnlyDictionary             │
└─────────────────────────────────────────────────────────────────┘
```

### Key Components

#### `DisplayConfig` (updated)

```csharp
public sealed class DisplayConfig
{
    public bool Hints { get; set; } = true;
    public int TreeDepth { get; set; } = 3;
    public string Icons { get; set; } = "unicode";   // NEW
}
```

The `Icons` property defaults to `"unicode"`. Valid values: `"unicode"`, `"nerd"`. JSON serialization via `TwigJsonContext` picks this up automatically since `DisplayConfig` is already registered as `[JsonSerializable]`.

#### `TwigConfiguration.SetValue` (updated)

Add a new case to the `SetValue` switch:

```csharp
case "display.icons":
    var lower = value.ToLowerInvariant();
    if (lower is "unicode" or "nerd")
    {
        Display.Icons = lower;
        return true;
    }
    return false;
```

This enforces that only `"unicode"` and `"nerd"` are accepted — any other value returns `false`, which causes `ConfigCommand` to print an error message.

#### `ConfigCommand.GetValue` (updated)

Add a new case:

```csharp
"display.icons" => config.Display.Icons,
```

#### `IconSet` (new — Domain layer)

```csharp
// src/Twig.Domain/ValueObjects/IconSet.cs
namespace Twig.Domain.ValueObjects;

/// <summary>
/// Provides icon glyph mappings for work item types.
/// Two sets: Unicode (safe for all terminals) and Nerd Font (requires patched font).
/// </summary>
public static class IconSet
{
    public static IReadOnlyDictionary<string, string> UnicodeIcons { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"]                 = "◆",
            ["Feature"]              = "▪",
            ["User Story"]           = "●",
            ["Product Backlog Item"] = "●",
            ["Requirement"]          = "●",
            ["Bug"]                  = "✦",
            ["Task"]                 = "□",
            ["Impediment"]           = "✦",
            ["Risk"]                 = "✦",
            ["Issue"]                = "□",
            ["Test Case"]            = "□",
            ["Change Request"]       = "□",
            ["Review"]               = "□",
        };

    public static IReadOnlyDictionary<string, string> NerdFontIcons { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Codicons (nf-cod-*) — U+EA60–U+EC1E range in Nerd Fonts
            ["Epic"]                 = "\uDB81\uDD31",  // nf-md-crown (U+F0531)
            ["Feature"]              = "\uDB80\uDCC0",  // nf-md-bookmark (U+F00C0) — or nf-cod-rocket
            ["User Story"]           = "\uea67",         // nf-cod-account (U+EA67)
            ["Product Backlog Item"] = "\uea67",         // nf-cod-account (U+EA67)
            ["Requirement"]          = "\uea67",         // nf-cod-account (U+EA67)
            ["Bug"]                  = "\uea87",         // nf-cod-bug (U+EA87)
            ["Task"]                 = "\ueba2",         // nf-cod-checklist (U+EBA2)
            ["Impediment"]           = "\uea6c",         // nf-cod-alert (U+EA6C)
            ["Risk"]                 = "\uea6c",         // nf-cod-alert (U+EA6C)
            ["Issue"]                = "\ueba2",         // nf-cod-checklist (U+EBA2)
            ["Test Case"]            = "\ueb6e",         // nf-cod-beaker (U+EB6E)
            ["Change Request"]       = "\ueabd",         // nf-cod-diff (U+EABD)
            ["Review"]               = "\ueb66",         // nf-cod-eye (U+EB66)
        };

    private const string DefaultIcon = "·";

    /// <summary>
    /// Returns the icon dictionary for the given mode.
    /// Falls back to UnicodeIcons for unrecognized modes.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetIcons(string mode) =>
        string.Equals(mode, "nerd", StringComparison.OrdinalIgnoreCase)
            ? NerdFontIcons
            : UnicodeIcons;

    /// <summary>
    /// Looks up the icon for a work item type in the given icon dictionary.
    /// Returns <see cref="DefaultIcon"/> if the type is not mapped.
    /// </summary>
    public static string GetIcon(IReadOnlyDictionary<string, string> icons, string typeName) =>
        icons.TryGetValue(typeName, out var icon) ? icon : DefaultIcon;
}
```

**Placement rationale**: `IconSet` is placed in `Twig.Domain/ValueObjects/` alongside `WorkItemType`. It contains pure domain data (work item type → icon mapping) with no infrastructure dependencies. The Domain layer is the correct DDD home.

**Nerd Font glyph selection rationale**: Codicons (`nf-cod-*`) are preferred because they are the VS Code icon set — familiar to Azure DevOps users. Material Design Icons (`nf-md-*`) are used as fallback where Codicons lack a suitable glyph (e.g., `crown` for Epic). All selected codepoints are in the Nerd Fonts v3.x stable range.

#### `HumanOutputFormatter` (updated constructor)

```csharp
public sealed class HumanOutputFormatter : IOutputFormatter
{
    private readonly IReadOnlyDictionary<string, string> _icons;

    public HumanOutputFormatter() : this(new DisplayConfig()) { }

    public HumanOutputFormatter(DisplayConfig displayConfig)
    {
        _icons = IconSet.GetIcons(displayConfig.Icons);
    }

    // Existing methods unchanged...

    /// <summary>
    /// Returns the icon glyph for a work item type from the configured icon set.
    /// </summary>
    internal string GetTypeIcon(WorkItemType type) =>
        IconSet.GetIcon(_icons, type.Value);
}
```

The parameterless constructor is preserved for backward compatibility with existing tests. It delegates to the new constructor with default `DisplayConfig` (which uses `Icons = "unicode"`).

#### `Program.cs` DI update

```csharp
// Before (current):
services.AddSingleton<HumanOutputFormatter>();

// After:
services.AddSingleton<HumanOutputFormatter>(sp =>
    new HumanOutputFormatter(sp.GetRequiredService<TwigConfiguration>().Display));
```

### Data Flow

#### `twig config display.icons nerd` (write):

```
User: twig config display.icons nerd
  │
  ▼
ConsoleAppFramework → TwigCommands.Config(key:"display.icons", value:"nerd")
  │
  ▼
ConfigCommand.ExecuteAsync("display.icons", "nerd")
  │ config.SetValue("display.icons", "nerd")
  │   → Display.Icons = "nerd", returns true
  │ config.SaveAsync(paths.ConfigPath)
  │   → .twig/config written: { "display": { ..., "icons": "nerd" } }
  │ Console.WriteLine("Set display.icons = nerd")
  └─ return 0
```

#### `twig config display.icons` (read):

```
User: twig config display.icons
  │
  ▼
ConfigCommand.ExecuteAsync("display.icons", null)
  │ GetValue("display.icons") → "unicode" (or "nerd")
  │ Console.WriteLine("unicode")
  └─ return 0
```

#### Icon resolution at formatter construction:

```
Program.cs DI container
  │ sp.GetRequiredService<TwigConfiguration>().Display.Icons → "nerd"
  ▼
new HumanOutputFormatter(displayConfig)
  │ IconSet.GetIcons("nerd") → NerdFontIcons dictionary
  │ _icons = NerdFontIcons
  │
  │ (later, during rendering — deferred to follow-up plan)
  │ GetTypeIcon(WorkItemType.Bug) → "\uea87" (nf-cod-bug)
  └─
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **`IconSet` as static class with static dictionaries** | Icons are pure data — no state, no dependencies. Static avoids DI overhead and makes the data accessible from any layer without injection. `IReadOnlyDictionary` prevents mutation. |
| **`IconSet` in Domain layer (`Twig.Domain/ValueObjects/`)** | Icon mappings are domain concepts tied to `WorkItemType`. They don't depend on infrastructure (no I/O, no config loading). Domain is the correct DDD layer. |
| **`string Icons` on `DisplayConfig` (not an enum)** | AOT-safe JSON serialization with `System.Text.Json` source generators handles `string` natively. An enum would work with the generic `JsonStringEnumConverter<T>` (AOT-compatible in .NET 9) but adds source-gen ceremony for a two-value choice. A `string` with validation in `SetValue` is simpler and achieves the same safety. |
| **Parameterless constructor preserved on `HumanOutputFormatter`** | Existing tests (`new HumanOutputFormatter()`) and the current DI registration construct the formatter without arguments. Adding a parameterless constructor that delegates to the `DisplayConfig` overload preserves backward compatibility and minimizes test churn. |
| **`GetTypeIcon` as `internal` method** | Exposed for unit testing (via `InternalsVisibleTo`) but not part of the public API. Full rendering integration (deferred) will call this internally from `FormatWorkItem`, `FormatTree`, etc. |
| **`"unicode"` and `"nerd"` as lowercase string values** | Config keys and values in the Twig CLI are consistently lowercase (`"azcli"`, `"pat"`, etc.). The `SetValue` method normalizes to lowercase before storing. |
| **Validation in `SetValue` (not `DisplayConfig` setter)** | Consistent with existing pattern — `SetValue` validates `int.TryParse` for `StaleDays`, `bool.TryParse` for `Hints`. Adding enum-like validation for `Icons` follows the same pattern. |
| **Nerd Font glyphs from Codicons set** | Codicons are the VS Code icon set (U+EA60–U+EC1E in Nerd Fonts). VS Code is the most common editor for Azure DevOps users. These icons are semantically appropriate and visually clean at terminal font sizes. Material Design Icons (U+F0001+) are used only where Codicons lack a suitable glyph. |

---

## Alternatives Considered

### Alt-1: Enum for icon mode instead of string

Introduce `enum IconMode { Unicode, Nerd }` on `DisplayConfig`.

**Pros**: Type-safe; compiler prevents invalid values.
**Cons**: While the generic `JsonStringEnumConverter<T>` is AOT-compatible in .NET 9 with source-generated contexts, it still requires adding a `[JsonConverter]` attribute on the property and registering the converter — disproportionate ceremony for a two-value choice. Additionally, existing config value validation in `SetValue` uses string-based patterns (`int.TryParse`, `bool.TryParse`); an enum would break this consistency.
**Decision**: Rejected. `string` with validation in `SetValue` is simpler and consistent with existing config patterns.

### Alt-2: `IconSet` as a non-static DI-injectable service

Register `IconSet` as a singleton in DI, with the icon mode injected via constructor.

**Pros**: Standard DI pattern; testable via interface.
**Cons**: `IconSet` contains only static data (two dictionaries). A DI service is unnecessary overhead. The `GetIcons(string mode)` static method achieves the same result without DI plumbing. Additionally, `IconSet` is in the Domain layer, which has no DI container awareness.
**Decision**: Rejected. Static class is simpler and more appropriate for pure data.

### Alt-3: Icons as part of `IOutputFormatter` interface

Add `GetTypeIcon(WorkItemType type)` to `IOutputFormatter` and implement in all three formatters.

**Pros**: Polymorphic — each formatter could define its own icon set (or return empty string for JSON/Minimal).
**Cons**: Icons are a human-output concern. `JsonOutputFormatter` and `MinimalOutputFormatter` would return empty strings — dead implementations. The `IOutputFormatter` interface would grow for a concern that only one implementation uses.
**Decision**: Rejected. `GetTypeIcon` belongs on `HumanOutputFormatter` only.

---

## Dependencies

### Internal dependencies

| Dependency | Note |
|------------|------|
| `TwigConfiguration.DisplayConfig` | Extended with `Icons` property |
| `TwigConfiguration.SetValue()` | New case added for `display.icons` |
| `ConfigCommand.GetValue()` | New case added for `display.icons` |
| `HumanOutputFormatter` | Constructor extended to accept `DisplayConfig` |
| `WorkItemType` | Icon map keys reference `WorkItemType.Value` strings |
| `TwigJsonContext` | `DisplayConfig` already registered — no change needed |
| `Program.cs` DI | `HumanOutputFormatter` registration updated |

### External dependencies

None. All required libraries are already in the project. Nerd Font glyphs are encoded as Unicode codepoints in string literals — no font files or external packages are needed.

### Sequencing constraints

- **Dependency on `twig-color-wiring.plan.md`**: The color-wiring plan has already introduced `FormatHint()` (line 184) and `FormatInfo()` (line 189) on `HumanOutputFormatter`, and the `OutputFormatterFactory` DI registration (Program.cs line 88). This icon stub plan is **not blocked** by any remaining color-wiring work — the `HumanOutputFormatter` constructor change and `IconSet` class are independent of the color infrastructure already in place.

---

## Impact Analysis

### Components affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `DisplayConfig` | New property (`Icons`) | Low — additive, default value preserves behavior |
| `TwigConfiguration.SetValue()` | New switch case | Low — additive |
| `ConfigCommand.GetValue()` | New switch case | Low — additive |
| `IconSet` (new) | New static class | None — no existing code affected |
| `HumanOutputFormatter` | Constructor overload + new field | Medium — existing parameterless constructor preserved |
| `Program.cs` | DI registration update | Low — single line change |
| `TwigJsonContext` | No change needed | None — `DisplayConfig` already registered |

### Backward compatibility

- **CLI interface**: No new commands or flags. `twig config display.icons` is a new config key — existing keys are unchanged.
- **Config file**: Adding `"icons": "unicode"` to the `display` section. Existing config files without `"icons"` will use the default `"unicode"` value via `DisplayConfig` property initializer.
- **Output format**: No rendering changes — `GetTypeIcon` is available but not called from any rendering method in this stub plan.
- **Test compatibility**: `HumanOutputFormatter()` parameterless constructor is preserved. Existing tests are unaffected.

### AOT compatibility

- `IconSet` uses `static readonly Dictionary` with inline initialization — AOT-safe.
- `DisplayConfig.Icons` is a `string` property — no custom JSON converter needed.
- `SetValue` uses a `switch` on string — no reflection.
- DI registration uses explicit lambda — no reflection.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Nerd Font codepoints render as tofu on non-patched fonts | Medium | Low | Feature is opt-in (`"nerd"`); default is `"unicode"`. Config command error message could advise about Nerd Fonts in a follow-up. |
| Surrogate pair codepoints (U+F0531 for `nf-md-crown`, U+F00C0 for `nf-md-bookmark`) cause issues in some terminals | Low | Medium | Use BMP Codicon codepoints (U+EA60–U+EC1E) wherever possible. Only use SMP codepoints (surrogate pairs like `\uDB81\uDD31`) when no BMP alternative exists. Verify surrogate pair math: CP = 0x10000 + (H − 0xD800) × 0x400 + (L − 0xDC00). Test in Windows Terminal + iTerm2. |
| `DisplayConfig.Icons` default value changes config file format for existing users | Very Low | Low | Default is `"unicode"` — `JsonIgnoreCondition.WhenWritingNull` in `TwigJsonContext` does NOT skip non-null defaults. However, since `Icons` defaults to `"unicode"` (a non-null string), it will appear in serialized JSON. This is acceptable — it's a valid config key. |
| Future icon set additions (e.g., `"emoji"`) require changes | Low | Low | `IconSet.GetIcons()` and `SetValue` validation are both centralized switch/if expressions — easy to extend. |

---

## Open Questions

1. **Exact Nerd Font glyph choices**: The codepoints in this plan are based on Nerd Fonts v3.x Codicons and Material Design Icons ranges. The specific glyphs SHOULD be visually reviewed in a Nerd Font terminal before finalizing. The codepoints MAY be adjusted during implementation without a plan revision.

2. **Should `IconSet.GetIcon` return a `char` or `string`?**: Nerd Font glyphs above U+FFFF (e.g., Material Design Icons at U+F0001–U+F1AF0) require surrogate pairs in C# — they cannot be represented as a single `char`. `string` is the correct type. This is resolved in favor of `string`.

3. **Follow-up plan scope**: The rendering integration (inserting icons into `FormatWorkItem`, `FormatTree`, `FormatWorkspace`) is deferred. Should that be a separate plan document or an addendum to this one? Recommendation: separate plan.

---

## Implementation Phases

### Phase 1 — Config Plumbing

**Goal**: Add `display.icons` property to `DisplayConfig`; wire `SetValue`/`GetValue`; verify JSON round-trip.
**Exit criteria**: `twig config display.icons` returns `"unicode"`; `twig config display.icons nerd` sets and persists the value; invalid values are rejected.

### Phase 2 — IconSet Data Structure

**Goal**: Create `IconSet` static class with Unicode and Nerd Font dictionaries; add unit tests.
**Exit criteria**: `IconSet.GetIcons("unicode")` returns the Unicode dictionary; `IconSet.GetIcons("nerd")` returns the Nerd Font dictionary; all 13 work item types are mapped in both dictionaries.

### Phase 3 — Formatter Wiring

**Goal**: Update `HumanOutputFormatter` to accept `DisplayConfig`; resolve icons via `IconSet`; update DI registration; update existing tests.
**Exit criteria**: `HumanOutputFormatter(new DisplayConfig { Icons = "nerd" }).GetTypeIcon(WorkItemType.Bug)` returns the Nerd Font bug glyph; all existing formatter tests pass.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/IconSet.cs` | Static class with Unicode and Nerd Font icon dictionaries; `GetIcons(string mode)` and `GetIcon(dict, typeName)` methods |
| `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | Unit tests for `IconSet`: dictionary completeness, `GetIcons` mode selection, `GetIcon` lookup + fallback |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `string Icons { get; set; } = "unicode"` to `DisplayConfig`; add `"display.icons"` case to `SetValue()` with `"unicode"`/`"nerd"` validation |
| `src/Twig/Commands/ConfigCommand.cs` | Add `"display.icons"` case to `GetValue()` switch |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Add `DisplayConfig` constructor overload; add `_icons` field; add `GetTypeIcon(WorkItemType)` method; preserve parameterless constructor |
| `src/Twig/Program.cs` | Update `HumanOutputFormatter` DI registration to pass `DisplayConfig` |
| `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | Add tests for `SetValue("display.icons", ...)` and round-trip serialization with `Icons` property |
| `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | Add test for `display.icons` read and write |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Add tests for `GetTypeIcon` with unicode and nerd modes |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | No files are deleted |

---

## Implementation Plan

### EPIC-001: Config Plumbing — `display.icons` Key

**Goal**: Add `display.icons` configuration key to `DisplayConfig`, `TwigConfiguration.SetValue()`, and `ConfigCommand.GetValue()` with validation and tests.

**Prerequisites**: None.

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add `public string Icons { get; set; } = "unicode";` property to `DisplayConfig` class in `TwigConfiguration.cs` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-002 | IMPL | Add `"display.icons"` case to `TwigConfiguration.SetValue()` switch: normalize value to lowercase, accept only `"unicode"` or `"nerd"`, set `Display.Icons`, return `true`/`false` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | DONE |
| ITEM-003 | IMPL | Add `"display.icons" => config.Display.Icons` case to `ConfigCommand.GetValue()` switch | `src/Twig/Commands/ConfigCommand.cs` | DONE |
| ITEM-004 | TEST | Add `SetValue_KnownPath_DisplayIcons_Unicode` test: verify `SetValue("display.icons", "unicode")` returns `true` and `Display.Icons` is `"unicode"` | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-005 | TEST | Add `SetValue_KnownPath_DisplayIcons_Nerd` test: verify `SetValue("display.icons", "nerd")` returns `true` and `Display.Icons` is `"nerd"` | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-006 | TEST | Add `SetValue_KnownPath_DisplayIcons_Invalid_ReturnsFalse` test: verify `SetValue("display.icons", "emoji")` returns `false` and `Display.Icons` remains `"unicode"` | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-007 | TEST | Add `SetValue_KnownPath_DisplayIcons_CaseInsensitive` test: verify `SetValue("display.icons", "NERD")` returns `true` and `Display.Icons` is `"nerd"` | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-008 | TEST | Add `SaveAndLoad_RoundTrip_IncludesIcons` test: create config with `Display.Icons = "nerd"`, save, reload, verify `Icons` is `"nerd"` | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-009 | TEST | Add `LoadAsync_ReturnsDefaults_Icons_IsUnicode` assertion to existing `LoadAsync_ReturnsDefaults_WhenFileMissing` test | `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | DONE |
| ITEM-010 | TEST | Add `Config_Read_DisplayIcons` test to `ConfigCommandTests`: verify reading `display.icons` returns 0 | `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | DONE |
| ITEM-011 | TEST | Add `Config_Write_DisplayIcons_Nerd` test to `ConfigCommandTests`: verify writing `display.icons nerd` returns 0 and persists | `tests/Twig.Cli.Tests/Commands/ConfigCommandTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `new TwigConfiguration().Display.Icons` returns `"unicode"`
- [x] `config.SetValue("display.icons", "nerd")` returns `true` and sets `Display.Icons` to `"nerd"`
- [x] `config.SetValue("display.icons", "emoji")` returns `false`
- [x] Config JSON round-trip preserves `"icons": "nerd"` in the `display` section
- [x] `twig config display.icons` reads the value; `twig config display.icons nerd` writes it

---

### EPIC-002: IconSet Data Structure

**Goal**: Create the `IconSet` static class with Unicode and Nerd Font icon dictionaries, a mode-selector method, and a type-lookup method. Add comprehensive unit tests.

**Prerequisites**: None (independent of EPIC-001).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Create `IconSet` static class in `Twig.Domain/ValueObjects/IconSet.cs` with `UnicodeIcons` and `NerdFontIcons` static `IReadOnlyDictionary<string, string>` properties, `GetIcons(string mode)` method, and `GetIcon(IReadOnlyDictionary, string typeName)` method with `DefaultIcon = "·"` fallback | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE |
| ITEM-013 | TEST | Add `UnicodeIcons_ContainsAllKnownTypes` test: verify all 13 `WorkItemType` static instances have entries in `UnicodeIcons` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-014 | TEST | Add `NerdFontIcons_ContainsAllKnownTypes` test: verify all 13 types have entries in `NerdFontIcons` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-015 | TEST | Add `GetIcons_Unicode_ReturnsUnicodeIcons` test: verify `GetIcons("unicode")` returns `UnicodeIcons` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-016 | TEST | Add `GetIcons_Nerd_ReturnsNerdFontIcons` test: verify `GetIcons("nerd")` returns `NerdFontIcons` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-017 | TEST | Add `GetIcons_UnknownMode_FallsBackToUnicode` test: verify `GetIcons("emoji")` returns `UnicodeIcons` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-018 | TEST | Add `GetIcon_KnownType_ReturnsIcon` test: verify `GetIcon(UnicodeIcons, "Epic")` returns `"◆"` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-019 | TEST | Add `GetIcon_UnknownType_ReturnsDefaultIcon` test: verify `GetIcon(UnicodeIcons, "SomeNewType")` returns `"·"` | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |
| ITEM-020 | TEST | Add `GetIcon_CaseInsensitive` test: verify `GetIcon(UnicodeIcons, "epic")` returns `"◆"` (dictionary uses `OrdinalIgnoreCase`) | `tests/Twig.Domain.Tests/ValueObjects/IconSetTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `IconSet.UnicodeIcons` contains 13 entries (one per known `WorkItemType`)
- [x] `IconSet.NerdFontIcons` contains 13 entries with Nerd Font codepoints
- [x] `IconSet.GetIcons("unicode")` returns `UnicodeIcons`; `GetIcons("nerd")` returns `NerdFontIcons`
- [x] `IconSet.GetIcon(icons, "Unknown")` returns `"·"` (default fallback)
- [x] All lookups are case-insensitive

---

### EPIC-003: Formatter Wiring

**Goal**: Update `HumanOutputFormatter` to accept `DisplayConfig`, resolve the icon set, and expose `GetTypeIcon()`. Update DI registration. Update existing tests.

**Prerequisites**: EPIC-001 (DisplayConfig.Icons property) and EPIC-002 (IconSet class).

| Task ID | Type | Description | Files | Status |
|---------|------|-------------|-------|--------|
| ITEM-021 | IMPL | Add `private readonly IReadOnlyDictionary<string, string> _icons` field to `HumanOutputFormatter`; add `public HumanOutputFormatter(DisplayConfig displayConfig)` constructor that sets `_icons = IconSet.GetIcons(displayConfig.Icons)`; update existing parameterless constructor to delegate: `public HumanOutputFormatter() : this(new DisplayConfig()) { }` | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-022 | IMPL | Add `internal string GetTypeIcon(WorkItemType type) => IconSet.GetIcon(_icons, type.Value);` method to `HumanOutputFormatter` | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-023 | IMPL | Update `HumanOutputFormatter` DI registration in `Program.cs` to: `services.AddSingleton<HumanOutputFormatter>(sp => new HumanOutputFormatter(sp.GetRequiredService<TwigConfiguration>().Display))` | `src/Twig/Program.cs` | DONE |
| ITEM-024 | TEST | Add `GetTypeIcon_Unicode_ReturnsUnicodeGlyph` test: construct `HumanOutputFormatter(new DisplayConfig { Icons = "unicode" })`, call `GetTypeIcon(WorkItemType.Epic)`, verify returns `"◆"` | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-025 | TEST | Add `GetTypeIcon_Nerd_ReturnsNerdFontGlyph` test: construct `HumanOutputFormatter(new DisplayConfig { Icons = "nerd" })`, call `GetTypeIcon(WorkItemType.Bug)`, verify returns the Codicon bug glyph | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-026 | TEST | Add `GetTypeIcon_DefaultConfig_ReturnsUnicodeGlyph` test: construct `new HumanOutputFormatter()` (parameterless), call `GetTypeIcon(WorkItemType.Task)`, verify returns `"□"` | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-027 | TEST | Verify all existing `HumanOutputFormatterTests` pass unchanged (parameterless constructor preserved) | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |

**Acceptance Criteria**:
- [x] `new HumanOutputFormatter()` still works (backward compatible)
- [x] `new HumanOutputFormatter(new DisplayConfig { Icons = "nerd" }).GetTypeIcon(WorkItemType.Bug)` returns a Nerd Font glyph
- [x] `new HumanOutputFormatter(new DisplayConfig()).GetTypeIcon(WorkItemType.Epic)` returns `"◆"`
- [x] `Program.cs` DI registration passes `DisplayConfig` to `HumanOutputFormatter`
- [x] All existing `HumanOutputFormatterTests` pass without modification
- [x] `dotnet build` succeeds with no new warnings
- [x] `dotnet test` passes for all three test projects

---

## References

- [Nerd Fonts](https://www.nerdfonts.com/) — Iconic font aggregator with 10,000+ glyphs
- [Nerd Fonts Cheat Sheet](https://www.nerdfonts.com/cheat-sheet) — Searchable glyph reference
- [Codicons](https://contoso.github.io/vscode-codicons/dist/codicon.html) — VS Code icon font (source for `nf-cod-*` glyphs)
- [Nerd Fonts Glyph Sets and Code Points](https://github.com/ryanoasis/nerd-fonts/wiki/Glyph-Sets-and-Code-Points) — Unicode codepoint allocation
- `docs/projects/twig-color-wiring.plan.md` — Tier 1 color design language and formatter wiring plan (predecessor)
- `src/Twig.Domain/ValueObjects/WorkItemType.cs` — Work item type definitions (13 known types)
- `src/Twig.Infrastructure/Config/TwigConfiguration.cs` — Configuration POCO with `SetValue` pattern
- `src/Twig/Formatters/HumanOutputFormatter.cs` — Human-readable ANSI formatter (target for wiring)
