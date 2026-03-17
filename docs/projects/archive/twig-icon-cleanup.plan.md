---
goal: Consolidate icon resolution to flow through IconSet — eliminate PromptBadges, remove legacy constructor, unify fallback defaults
version: 1.1
date_created: 2026-03-15
last_updated: 2026-03-15
owner: Twig CLI team
tags: [chore, cleanup, refactor, icons, nerd-font]
revision_notes: "Rev 1.1 — corrected test file count (1 file / 10 call sites, not 15+), fixed IconSet.DefaultIcon private access references, unified PromptBadges line range to 201-222, corrected ITEM-007 instance count to 10, clarified EPIC-002 prerequisite rationale."
---

# Twig Icon Cleanup — Solution Design & Implementation Plan

## Executive Summary

The Twig CLI has accumulated three separate icon resolution paths: (1) `IconSet` in `Twig.Domain` — the canonical source of truth with `GetIcons(mode)` and `GetIcon(icons, typeName)`, (2) `PromptBadges` in `PromptCommand.cs` — a static wrapper around `IconSet` that reimplements lookup with different fallback values (`"■"` and `"\uF059"` instead of `"·"`), and (3) a legacy `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor that hardcodes `IconSet.GetIcons("unicode")`, ignoring the user's `display.icons` configuration. This plan eliminates the duplication by: (a) removing `PromptBadges` and wiring `PromptCommand` directly to `IconSet`, (b) removing the legacy `HumanOutputFormatter(Dictionary?)` constructor and migrating all callers to the `DisplayConfig`-based constructor, and (c) ensuring every icon-consuming path in the codebase resolves through `IconSet.GetIcon()` with a single, consistent default glyph (`"·"`). The result is one clear path for icon resolution: `DisplayConfig.Icons → IconSet.GetIcons(mode) → IconSet.GetIcon(icons, typeName)`.

---

## Background

### Current system state

The Twig CLI is a .NET 9 AOT-compiled CLI tool. Icon rendering is used in five contexts:

1. **`HumanOutputFormatter.GetTypeIcon()`** — instance method that delegates to `IconSet.GetIcon(_icons, type.Value)` where `_icons` is set from `IconSet.GetIcons(displayConfig.Icons)` in the `DisplayConfig` constructor (line 50 of `HumanOutputFormatter.cs`). Used in `FormatWorkItem`, `FormatTree`, `FormatWorkspace`, and `FormatSprintView`. This is the correct flow.

2. **`PromptBadges.GetBadge()`** — static class in `PromptCommand.cs` (lines 201-222, inclusive of XML doc comment) that performs its own `IconSet.UnicodeIcons.TryGetValue` / `IconSet.NerdFontIcons.TryGetValue` lookups with **different fallback glyphs**: `"■"` for unicode unknown types, `"\uF059"` for nerd font unknown types. This diverges from `IconSet`'s private default which is `"·"`.

3. **`IconSet`** — canonical icon data in `Twig.Domain/ValueObjects/IconSet.cs` (lines 1-54). Two static dictionaries (`UnicodeIcons`, `NerdFontIcons`) with 13 known type mappings each. `GetIcons(mode)` returns the correct dictionary; `GetIcon(icons, typeName)` does the lookup with fallback `"·"`.

4. **Legacy constructor** — `HumanOutputFormatter(Dictionary<string, string>? typeColors)` (line 39-45) hardcodes `_icons = IconSet.GetIcons("unicode")` regardless of any configuration. This constructor exists for backward compatibility with tests written during the true-color-badges plan, before `display.icons` config existed.

5. **`PromptCommand.ReadPromptData()`** — reads `config.Display.Icons` to determine the mode string, then calls `PromptBadges.GetBadge(type, iconMode)` (line 88). The mode propagation is correct, but the resolution bypasses `IconSet.GetIcon()`.

### Context and motivation

The `twig-nerd-font-icons.plan.md` (Rev 1.1) established `IconSet` as the canonical icon registry and wired it into `HumanOutputFormatter` via the `DisplayConfig` constructor. However:

- The `PromptBadges` class was created later in the `twig-ohmyposh.plan.md` as a prompt-specific wrapper, introducing a second resolution path with different defaults.
- The legacy `HumanOutputFormatter(Dictionary?)` constructor was preserved from the earlier `twig-true-color-badges.plan.md` for test backward compatibility. It predates the `display.icons` config and always resolves to unicode mode.
- One test file (`HumanOutputFormatterTests.cs`) uses `new HumanOutputFormatter(typeColors)` or `new HumanOutputFormatter(typeColors: null)` across 10 call sites, which silently bypasses nerd font configuration. All other test files use the parameterless constructor and require no changes.

### Prior art in the codebase

| Document | Relevance |
|----------|-----------|
| `twig-nerd-font-icons.plan.md` | Created `IconSet`, `DisplayConfig.Icons`, wired `HumanOutputFormatter(DisplayConfig)` |
| `twig-true-color-badges.plan.md` | Created `HumanOutputFormatter(Dictionary<string, string>?)` constructor for type colors |
| `twig-ohmyposh.plan.md` | Created `PromptCommand`, `PromptBadges`, `PromptData` |

---

## Problem Statement

1. **Duplicate icon resolution paths**: `PromptBadges` reimplements `IconSet` lookup logic with different fallback values, violating DRY and creating a maintenance burden. Any future icon changes (e.g., adding a new work item type) must be made in two places.

2. **Inconsistent fallback glyphs**: `IconSet` uses `"·"` (middle dot) as its private default. `PromptBadges` uses `"■"` (black square) for unicode unknowns and `"\uF059"` for nerd font unknowns. A user with a custom work item type will see different glyphs in `twig status` vs `twig prompt`.

3. **Legacy constructor bypasses configuration**: `HumanOutputFormatter(Dictionary?)` always resolves to unicode icons. Tests using this constructor cannot verify nerd font behavior and silently produce unicode-only output regardless of any `DisplayConfig` settings.

4. **Constructor proliferation**: `HumanOutputFormatter` has three constructors — parameterless, `DisplayConfig`, and `Dictionary<string, string>?`. The `Dictionary` variant is only used in tests and creates confusion about which constructor to use.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Eliminate `PromptBadges` — `PromptCommand` MUST resolve icons via `IconSet.GetIcon()` directly.
2. **G-2**: Remove the `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor — all callers MUST use the `DisplayConfig` overload.
3. **G-3**: Ensure every icon-consuming path uses `IconSet.GetIcon()` with the canonical fallback glyph (`"·"`).
4. **G-4**: All existing tests pass after migration. Test assertions that depended on `PromptBadges`-specific fallbacks (`"■"`) MUST be updated to match `"·"`.
5. **G-5**: No behavioral change for users — icon output for all 13 known types is identical before and after.

### Non-Goals

- **NG-1**: Adding new icon glyphs or work item types — out of scope.
- **NG-2**: Changing the `IconSet` API surface (it is already well-designed) — out of scope.
- **NG-3**: Making `MinimalOutputFormatter` or `JsonOutputFormatter` icon-aware — they intentionally do not use icons.
- **NG-4**: Adding icon support for the `●` (active marker), `•` (dirty marker), `✓` (success), or `⚠` (stale warning) glyphs — these are structural markers, not type icons.

---

## Requirements

### Functional Requirements

- **FR-001**: `PromptCommand` MUST resolve type badges via `IconSet.GetIcon(IconSet.GetIcons(iconMode), typeName)` instead of `PromptBadges.GetBadge()`.
- **FR-002**: The `PromptBadges` static class MUST be deleted.
- **FR-003**: The `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor MUST be removed.
- **FR-004**: All test files that construct `HumanOutputFormatter` with a `Dictionary<string, string>?` argument MUST be updated to use `new HumanOutputFormatter(new DisplayConfig { TypeColors = ... })`.
- **FR-005**: The `PromptBadgesTests` test class MUST be refactored to test `IconSet.GetIcon()` directly, or test `PromptCommand.ReadPromptData()` end-to-end. Any assertion on `"■"` as a fallback MUST be changed to `"·"` (the private default inside `IconSet`).
- **FR-006**: `PromptData.TypeBadge` field semantics are unchanged — it still holds the resolved glyph string.

### Non-Functional Requirements

- **NFR-001**: Zero runtime behavior change for the 13 known work item types in both unicode and nerd font modes.
- **NFR-002**: No new dependencies introduced.
- **NFR-003**: All existing tests pass (after migration of constructor calls and assertion updates).

---

## Proposed Design

### Architecture Overview

**Before (current state):**

```
DisplayConfig.Icons ─┬─> HumanOutputFormatter(DisplayConfig) ──> IconSet.GetIcons() ──> IconSet.GetIcon()  [fallback: "·"]
                     │
                     └─> PromptCommand ──> PromptBadges.GetBadge() ──> IconSet.UnicodeIcons / .NerdFontIcons  [fallback: "■" / "\uF059"]

HumanOutputFormatter(Dictionary?) ──────> IconSet.GetIcons("unicode")  [always unicode, ignores config]
```

**After (proposed):**

```
DisplayConfig.Icons ─┬─> HumanOutputFormatter(DisplayConfig) ──> IconSet.GetIcons() ──> IconSet.GetIcon()  [fallback: "·"]
                     │
                     └─> PromptCommand ──────────────────────> IconSet.GetIcons() ──> IconSet.GetIcon()  [fallback: "·"]
```

All icon resolution flows through a single path: `IconSet.GetIcons(mode) → IconSet.GetIcon(icons, typeName)` with consistent `"·"` fallback.

### Key Components

#### 1. `IconSet` (unchanged)

File: `src/Twig.Domain/ValueObjects/IconSet.cs`

No changes. Already provides the correct API:
- `GetIcons(string mode)` — returns the icon dictionary for the given mode
- `GetIcon(IReadOnlyDictionary<string, string> icons, string? typeName)` — performs lookup with `DefaultIcon` fallback

#### 2. `PromptCommand` (modified)

File: `src/Twig/Commands/PromptCommand.cs`

Changes:
- Remove `PromptBadges` static class (lines 201-222, inclusive of XML doc comment)
- Replace `PromptBadges.GetBadge(type, iconMode)` call (line 88) with `IconSet.GetIcon(IconSet.GetIcons(iconMode), type)`

#### 3. `HumanOutputFormatter` (modified)

File: `src/Twig/Formatters/HumanOutputFormatter.cs`

Changes:
- Remove the `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor (lines 39-45)
- Two constructors remain: parameterless (delegates to `DisplayConfig`) and `DisplayConfig`-based

#### 4. Test files (modified)

One test file — `HumanOutputFormatterTests.cs` — uses the `Dictionary<string, string>?` constructor across 10 call sites (lines 461, 480, 499, 585, 614, 652, 800, 878, 899, 920). Each must be updated to use:
```csharp
new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors })
```
or for null:
```csharp
new HumanOutputFormatter(new DisplayConfig())
// or just: new HumanOutputFormatter()
```
All other test files (CommandFormatterWiringTests, ConfigCommandTests, ConflictUxTests, EditSaveCommandTests, etc.) use the parameterless constructor and require zero changes.

### Data Flow

**`twig prompt` icon resolution (after):**

1. `PromptCommand.ReadPromptData()` reads `config.Display.Icons` → `iconMode` (e.g., `"unicode"` or `"nerd"`)
2. Calls `IconSet.GetIcons(iconMode)` → returns `IReadOnlyDictionary<string, string>`
3. Calls `IconSet.GetIcon(icons, type)` → returns the glyph or `"·"` for unknown types
4. Stores result in `PromptData.TypeBadge`

**`twig status` / `twig tree` / `twig ws` icon resolution (unchanged):**

1. `HumanOutputFormatter` constructed with `DisplayConfig` containing `Icons` mode
2. `_icons` field set to `IconSet.GetIcons(displayConfig.Icons)`
3. `GetTypeIcon(type)` calls `IconSet.GetIcon(_icons, type.Value)` → returns glyph or `"·"`

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Delete `PromptBadges` entirely rather than making it delegate to `IconSet.GetIcon()`** | `PromptBadges` adds no value over calling `IconSet.GetIcon()` directly. A delegation wrapper would be pure indirection. The call site in `ReadPromptData` is a single line change. |
| **Remove `Dictionary<string, string>?` constructor rather than deprecating** | The constructor is `public` but internal to the assembly (no external consumers). Deprecation via `[Obsolete]` adds noise; removing it forces a clean migration. All callers are in test code and easily updated. |
| **Use `new DisplayConfig { TypeColors = ... }` in tests rather than a test helper** | Keeps tests explicit about what they configure. A helper would hide important config details. The migration is mechanical. |
| **Unify fallback to `"·"` for both prompt and formatter** | Users should see the same glyph for an unknown type regardless of which command they run. The `"·"` middle dot is already the private default inside `IconSet`. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| **Keep `PromptBadges` but make it delegate to `IconSet.GetIcon()`** | Less code churn in `PromptCommand` | Still maintains a redundant class; the delegation is pure indirection | Rejected — direct call is simpler |
| **Deprecate `Dictionary?` constructor with `[Obsolete]` instead of removing** | Less immediate churn; can remove later | Leaves dead code path; tests silently bypass icon config; two-step process | Rejected — clean break is better for a small codebase |
| **Create a shared `BadgeResolver` service injected via DI** | Enables mockability; single resolution point | Over-engineering for a static lookup; `IconSet` is already the right abstraction; adds DI complexity | Rejected — `IconSet` is sufficient |

---

## Dependencies

### Internal

- **`IconSet`** — no changes needed; already has the correct API.
- **`DisplayConfig`** — no changes needed; already has `Icons` and `TypeColors` properties.
- **Test infrastructure** — tests using the legacy constructor must be updated in the same PR.

### Sequencing

No external dependencies. This is a pure refactoring task that can proceed immediately. The only prerequisite is that the current test suite passes (baseline validation).

---

## Impact Analysis

### Components affected

| Component | Impact |
|-----------|--------|
| `PromptCommand.cs` | `PromptBadges` deleted; one line changed in `ReadPromptData` |
| `HumanOutputFormatter.cs` | One constructor removed (7 lines) |
| `PromptCommandTests.cs` | `PromptBadgesTests` class updated; fallback assertion `"■"` → `"·"` |
| `HumanOutputFormatterTests.cs` | 10 call sites updated: `new HumanOutputFormatter(typeColors)` → `new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors })` |

### Backward compatibility

- **CLI behavior**: No change for users. All 13 known work item types produce identical output.
- **Fallback for unknown types**: Changes from `"■"` to `"·"` in `twig prompt` output. This affects only custom/unknown work item types, which are rare. The `"·"` glyph is more consistent with the rest of the CLI.
- **Test API**: The `Dictionary?` constructor is removed. This is an internal-only breaking change; no external consumers exist.

### Performance

No impact — `IconSet.GetIcon()` is a dictionary lookup. Replacing `PromptBadges.GetBadge()` with a direct `IconSet.GetIcon()` call eliminates one level of indirection.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Test assertion on `"■"` fallback is load-bearing for a downstream integration | Low | Low | Only `PromptBadgesTests` asserts `"■"`; grep confirms no other references. The `"·"` replacement matches the canonical `IconSet` default. |
| Missed constructor call site causes compile error | Low | Low | The C# compiler will catch any remaining `Dictionary?` constructor calls at build time. This is a compile-time guarantee. |
| Test update introduces typo in `DisplayConfig` property name | Low | Low | `DisplayConfig.TypeColors` is the only property; the compiler catches misspellings. |

---

## Open Questions

None — the design is straightforward and all information needed is available in the codebase.

---

## Implementation Phases

### Phase 1: Remove `PromptBadges` and wire `PromptCommand` to `IconSet`

**Exit criteria**: `PromptBadges` class is deleted. `PromptCommand.ReadPromptData()` calls `IconSet.GetIcon()` directly. All prompt tests pass with updated assertions.

### Phase 2: Remove legacy `HumanOutputFormatter(Dictionary?)` constructor and migrate tests

**Exit criteria**: The `Dictionary<string, string>?` constructor is removed. All 10 call sites in `HumanOutputFormatterTests.cs` compile and pass using the `DisplayConfig` constructor. Full test suite green.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/PromptCommand.cs` | Delete `PromptBadges` class (lines 201-222, inclusive of XML doc comment); replace `PromptBadges.GetBadge(type, iconMode)` with `IconSet.GetIcon(IconSet.GetIcons(iconMode), type)` on line 88 |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Remove `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor (lines 39-45) |
| `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | Update `PromptBadgesTests` class: remove tests that assert `PromptBadges.GetBadge()`, replace with `IconSet.GetIcon()` tests; change `"■"` assertion to `"·"` |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Update 10 call sites: `new HumanOutputFormatter(typeColors)` → `new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors })` and `new HumanOutputFormatter(typeColors: null)` → `new HumanOutputFormatter()` |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none — `PromptBadges` is an inner class in `PromptCommand.cs`, not a separate file) | |

---

## Implementation Plan

### EPIC-001: Remove `PromptBadges` and consolidate prompt icon resolution

**Goal**: Eliminate the `PromptBadges` wrapper class and wire `PromptCommand` directly to `IconSet.GetIcon()`. Unify the fallback glyph to `"·"`.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | In `PromptCommand.cs`, replace `var badge = PromptBadges.GetBadge(type, iconMode);` (line 88) with `var icons = IconSet.GetIcons(iconMode); var badge = IconSet.GetIcon(icons, type);`. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-002 | IMPL | Delete the `PromptBadges` static class (lines 201-222 of `PromptCommand.cs`, inclusive of the XML doc comment starting at line 201), including the `GetBadge`, `GetUnicodeBadge`, and `GetNerdBadge` methods. | `src/Twig/Commands/PromptCommand.cs` | DONE |
| ITEM-003 | TEST | In `PromptCommandTests.cs`, refactor the `PromptBadgesTests` class: (a) Remove all `PromptBadges.GetBadge()` call sites; (b) Replace with equivalent `IconSet.GetIcon()` assertions; (c) Change the `"CustomType"` fallback assertion from `"■"` to `"·"` (the private default inside `IconSet`). Rename class to something like `IconSetPromptIntegrationTests` or inline into `PromptCommandTests`. | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-004 | TEST | In `PromptCommandTests.PlainFormat_CorrectOutput`, verify the output still contains `"◆"` for Epic type (unchanged behavior). | `tests/Twig.Cli.Tests/Commands/PromptCommandTests.cs` | DONE |
| ITEM-005 | TEST | Run full test suite (`dotnet test`) to verify zero regressions. | All test projects | DONE |

**Acceptance Criteria**:
- [x] `PromptBadges` class no longer exists in the codebase
- [x] `PromptCommand.ReadPromptData()` calls `IconSet.GetIcon()` directly
- [x] All 13 known types produce identical badges in both unicode and nerd modes
- [x] Unknown type fallback is `"·"` (not `"■"`)
- [x] All prompt-related tests pass

---

### EPIC-002: Remove legacy `HumanOutputFormatter(Dictionary?)` constructor and migrate tests

**Goal**: Remove the constructor that bypasses `DisplayConfig` and hardcodes unicode mode. Migrate all test callers to the `DisplayConfig`-based constructor.

**Prerequisites**: None (EPIC-001 and EPIC-002 touch independent code paths — `PromptCommand.cs` vs `HumanOutputFormatter.cs` — and can be implemented in either order or in parallel). Sequencing EPIC-001 first is preferred for reviewability: smaller PR first, then the test migration PR.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-006 | IMPL | Remove the `HumanOutputFormatter(Dictionary<string, string>? typeColors)` constructor (lines 39-45 of `HumanOutputFormatter.cs`). | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE |
| ITEM-007 | TEST | In `HumanOutputFormatterTests.cs`, update all 10 instances of the `Dictionary<string, string>?` constructor (lines 461, 480, 499, 585, 614, 652, 800, 878, 899, 920). Change `new HumanOutputFormatter(new Dictionary<string, string> { ... })` to `new HumanOutputFormatter(new DisplayConfig { TypeColors = new Dictionary<string, string> { ... } })`. Change `new HumanOutputFormatter(typeColors: null)` to `new HumanOutputFormatter()` or `new HumanOutputFormatter(new DisplayConfig())`. Affected tests include `FormatWorkItem_UsesTrueColor_WhenTypeColorsConfigured`, `FormatWorkItem_FallsBackTo3BitColor_WhenNoTypeColors`, `FormatWorkItem_UsesTrueColor_CaseInsensitiveKey`, `Integration_FormatWorkItem_WithTypeColors_ShowsTrueColorAndBadge`, and others. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-008 | TEST | Verify that tests using `new HumanOutputFormatter()` (parameterless) are unaffected — this constructor delegates to `DisplayConfig` and remains. | `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | DONE |
| ITEM-009 | TEST | Build the solution (`dotnet build`) to verify no compile errors from remaining `Dictionary?` constructor references across all test files. The compiler will catch any missed call sites. | All projects | DONE |
| ITEM-010 | TEST | Run full test suite (`dotnet test`) to verify zero regressions. | All test projects | DONE |

**Acceptance Criteria**:
- [x] `HumanOutputFormatter` has exactly two constructors: parameterless and `DisplayConfig`
- [x] No test file references the `Dictionary<string, string>?` constructor
- [x] All tests pass
- [x] `dotnet build` succeeds with zero warnings related to this change

---

## References

| Resource | Relevance |
|----------|-----------|
| `src/Twig.Domain/ValueObjects/IconSet.cs` | Canonical icon registry — unchanged by this plan |
| `src/Twig/Commands/PromptCommand.cs` (lines 201-222) | `PromptBadges` class to be deleted (inclusive of XML doc comment) |
| `src/Twig/Formatters/HumanOutputFormatter.cs` (lines 39-45) | Legacy constructor to be removed |
| `docs/projects/twig-nerd-font-icons.plan.md` | Original `IconSet` and `DisplayConfig.Icons` design |
| `docs/projects/twig-ohmyposh.plan.md` | `PromptCommand` and `PromptBadges` design |
| `docs/projects/twig-true-color-badges.plan.md` | Origin of the `Dictionary?` constructor |
