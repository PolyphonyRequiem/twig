# Twig Rendering Consolidation — Solution Design & Implementation Plan

> **Date**: 2026-03-16
> **Status**: DRAFT
> **Revision**: 4 (Addresses technical review feedback — score 89/100)

---

## Executive Summary

This plan consolidates the three independent icon/color/badge/state resolution chains in Twig (HumanOutputFormatter, SpectreTheme, and Twig.Tui) into a single, shared resolution pipeline. Today, `HumanOutputFormatter` is the authority — it uses `IconSet` with `iconId`-based resolution, `TypeColorResolver` with ADO true colors, `StateCategoryResolver` with process type data, and config-driven icon mode. `SpectreTheme` and `Twig.Tui` reimplement subsets of this logic with hardcoded values, producing visual inconsistencies (e.g., unicode icons in Spectre live mode vs. nerd font icons in `--no-live` sync mode). Additionally, the TUI manually constructs its DI graph, duplicating the CLI's service registrations. This plan introduces a shared `TwigServiceRegistration` class, makes `SpectreTheme` instance-based with injected resolvers, wires the TUI through shared DI, and fixes the TUI save/active-context mismatch.

---

## Background

### Current Architecture

The Twig system comprises four .NET projects:

| Project | Purpose | AOT |
|---------|---------|-----|
| `Twig.Domain` | Aggregates, value objects, services, interfaces | N/A (lib) |
| `Twig.Infrastructure` | SQLite persistence, ADO REST client, config | N/A (lib) |
| `Twig` (CLI) | ConsoleAppFramework CLI, formatters, Spectre rendering | ✅ AOT |
| `Twig.Tui` | Terminal.Gui v2 develop build (`2.0.0-develop.5185`) TUI navigator | ❌ Non-AOT |

> **Note**: Terminal.Gui is pinned to `2.0.0-develop.5185`, a nightly develop build — not an official beta release. The `2.0.0-beta.*` series predates the v2 instance-based API (`Terminal.Gui.App`, `Terminal.Gui.Input`, etc.) used here. API stability is not guaranteed between develop builds.

Rendering has three paths:
1. **Sync path** (`--no-live` or piped output): `HumanOutputFormatter` produces ANSI-escaped strings. This is the authority for icons, colors, badges, and state formatting.
2. **Async path** (live TTY): `SpectreRenderer` uses `SpectreTheme` (static class) for Spectre.Console markup. This reimplements badge/color/state logic with hardcoded values.
3. **TUI path**: `Twig.Tui/Program.cs` manually constructs `SqliteCacheStore`, repositories, and stores. `WorkItemNode.ToString()` uses a plain string format with no icon or color resolution.

### Context / Motivation

- Users see **different icons** depending on whether `--no-live` is used: the sync path renders nerd font glyphs (when configured) via `IconSet.GetIconByIconId`, while the Spectre live path always renders hardcoded unicode glyphs.
- Users see **different colors** depending on path: the sync path uses `TypeColorResolver` → `HexToAnsi` (24-bit true color from ADO appearance data), while the Spectre live path uses `DeterministicTypeColor` → hardcoded 6-entry ANSI-to-Spectre switch.
- The TUI has **no icon/color resolution at all** — `WorkItemNode.ToString()` renders plain text.
- The TUI **duplicates DI setup** from CLI `Program.cs`, creating maintenance burden and missing services (no `IProcessTypeStore`, no config-based path resolution, no `LegacyDbMigrator`).
- TUI save writes pending changes under the selected item's ID, but `twig save` only reads changes for the **active context** item, causing a "nothing to save" mismatch.

### Prior Art

- `docs/projects/twig-icon-cleanup.plan.md` — Introduced `IconSet` with `iconId` resolution
- `docs/projects/twig-true-color-badges.plan.md` — Introduced `TypeColorResolver` and `HexToAnsi`
- `docs/projects/twig-iux-tui.plan.md` — TUI architecture and binary separation
- `docs/projects/twig-state-category-cleanup.plan.md` — Unified `StateCategoryResolver`

---

## Problem Statement

1. **Visual inconsistency**: Three codepaths resolve icons, colors, and badges independently. `SpectreTheme.GetTypeBadge()` uses hardcoded unicode glyphs, ignoring `IconSet`, nerd font config, and `iconId` resolution. `SpectreTheme.GetSpectreColor()` maps only 6 ANSI escapes from `DeterministicTypeColor`, ignoring `TypeColorResolver` and ADO true colors. `SpectreTheme.GetStateStyle()` and `FormatState()` call `StateCategoryResolver.Resolve(state, null)`, ignoring process type state entries.

2. **DI duplication**: `Twig.Tui/Program.cs` (lines 16–54) manually constructs `SqliteCacheStore`, `SqliteWorkItemRepository`, `SqliteContextStore`, `SqlitePendingChangeStore`, `SqliteProcessTypeStore`, and `DynamicProcessConfigProvider` — mirroring CLI `Program.cs` (lines 30–68) without `LegacyDbMigrator`, without `TwigConfiguration.Display` config, and without auth/HTTP services.

3. **TUI save/context mismatch**: `WorkItemFormView.OnSave()` writes pending changes keyed by `_currentItem.Id`. `SaveCommand.ExecuteAsync()` reads `contextStore.GetActiveWorkItemIdAsync()` and only processes changes for that single ID. If the user navigates the TUI tree and edits a non-active item, `twig save` reports "Nothing to save."

4. **TUI has no badge rendering**: `WorkItemNode.ToString()` (line 182 of `TreeNavigatorView.cs`) returns plain `► #{Id} [{Type}] {Title} ({State})` with no icon glyphs or color.

---

## Goals and Non-Goals

### Goals

1. **G1**: All three rendering paths (sync formatter, Spectre live, TUI) produce identical icon glyphs for the same work item type and configuration.
2. **G2**: All three rendering paths use the same color resolution chain: user-override TypeColors → ADO appearance colors → deterministic fallback.
3. **G3**: State rendering respects process type state entries (when available) across all paths.
4. **G4**: CLI and TUI share a single service registration class, eliminating manual construction in TUI `Program.cs`.
5. **G5**: `twig save` processes all items with pending changes, not only the active context item.
6. **G6**: TUI `WorkItemNode` renders type badges using `IconSet`.
7. **G7**: All existing test assertions continue to pass.

### Non-Goals

- **NG1**: Making `Twig.Tui` AOT-compatible (Terminal.Gui v2 does not support AOT).
- **NG2**: Adding auth/HTTP services to the TUI (TUI operates offline against the local SQLite cache).
- **NG3**: Changing the `IOutputFormatter` interface or adding new formatter implementations.
- **NG4**: Modifying the `IAsyncRenderer` interface.
- **NG5**: Version bumps or changelog entries.
- **NG6**: Introducing a rendering abstraction layer that wraps both Spectre.Console and Terminal.Gui.

---

## Requirements

### Functional Requirements

- **FR-001**: `SpectreTheme.GetTypeBadge()` MUST delegate to `IconSet` using the same resolution chain as `HumanOutputFormatter.GetTypeBadge()`: iconId-based lookup first (from `TypeAppearanceConfig`), then icon-mode-based fallback (unicode/nerd), then hardcoded unicode fallback for unknown types.
- **FR-002**: `SpectreTheme` color resolution MUST use `TypeColorResolver.ResolveHex()` → hex-to-Spectre-Color conversion, falling back to `DeterministicTypeColor` only when no hex color is available.
- **FR-003**: `SpectreTheme` state resolution MUST accept `IReadOnlyList<StateEntry>?` and pass it to `StateCategoryResolver.Resolve()`.
- **FR-004**: A shared **`public`** `TwigServiceRegistration` class in `Twig.Infrastructure` MUST register all common services (SqliteCacheStore, repositories, stores, config, paths). The class MUST be `public` because `InternalsVisibleTo` in `Twig.Infrastructure.csproj` does NOT include `Twig.Tui`. `LegacyDbMigrator` invocation MUST remain in CLI `Program.cs` because `LegacyDbMigrator` is an `internal static class` in the CLI project (`src/Twig/LegacyDbMigrator.cs`) and cannot be referenced from `Twig.Infrastructure`.
- **FR-005**: CLI `Program.cs` MUST consume `TwigServiceRegistration` for common services, adding only CLI-specific services (formatters, commands, Spectre.Console, auth, HTTP).
- **FR-006**: TUI `Program.cs` MUST consume `TwigServiceRegistration` for common services, eliminating manual construction.
- **FR-007**: `SaveCommand.ExecuteAsync()` MUST iterate all work item IDs with pending changes (via `IPendingChangeStore.GetDirtyItemIdsAsync()`), not only the active context item.
- **FR-008**: TUI `WorkItemNode.ToString()` MUST render type badge glyphs from `IconSet`.

### Non-Functional Requirements

- **NFR-001**: `TwigServiceRegistration` MUST be AOT-compatible (no reflection, no `typeof()` generic constraints that break trimming).
- **NFR-002**: All existing tests MUST pass without modification to assertions. New tests may be added.
- **NFR-003**: `SpectreTheme` tests MUST verify visual parity with `HumanOutputFormatter` for the same inputs (same icon mode, same type appearances, same state entries).

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        Twig.Domain                              │
│  IconSet  ·  TypeColorResolver  ·  StateCategoryResolver        │
│  (unchanged — authoritative resolution logic)                   │
└─────────────────────────┬───────────────────────────────────────┘
                          │
┌─────────────────────────┴───────────────────────────────────────┐
│                    Twig.Infrastructure                           │
│  TwigServiceRegistration  (NEW)                                 │
│  SqliteCacheStore · Repositories · Config · TwigPaths            │
└────────┬────────────────────────────┬───────────────────────────┘
         │                            │
┌────────┴────────┐          ┌────────┴────────┐
│    Twig (CLI)   │          │   Twig.Tui      │
│                 │          │                 │
│ Program.cs      │          │ Program.cs      │
│  └ calls        │          │  └ calls        │
│    TwigService  │          │    TwigService   │
│    Registration │          │    Registration  │
│    .AddCore()   │          │    .AddCore()    │
│                 │          │                 │
│ SpectreTheme    │          │ WorkItemNode    │
│  (instance,     │          │  (uses IconSet)  │
│   injected      │          │                 │
│   resolvers)    │          │                 │
│                 │          │                 │
│ HumanOutput     │          │                 │
│  Formatter      │          │                 │
│  (unchanged)    │          │                 │
└─────────────────┘          └─────────────────┘
```

### Key Components

#### 1. `TwigServiceRegistration` (NEW — `Twig.Infrastructure`)

A **`public`** static class with extension method `AddTwigCoreServices(this IServiceCollection services)` that registers:

> **Access modifier — MUST be `public`**: `InternalsVisibleTo` in `Twig.Infrastructure.csproj` includes `Twig` (CLI) and `Twig.Infrastructure.Tests` but does **NOT** include `Twig.Tui`. If this class were `internal` (the C# default for non-nested classes when no modifier is specified), `Twig.Tui` would fail to compile with a visibility error. Declaring it `public` is consistent with all other Infrastructure types consumed by the TUI (e.g., `SqliteCacheStore`, `SqliteWorkItemRepository`, `SqlitePendingChangeStore` are all `public sealed class`).

- `TwigConfiguration` (loaded from `.twig/config`)
- `TwigPaths` (resolved from config org/project)
- `SqliteCacheStore` (lazy, descriptive error if not initialized)
- `IWorkItemRepository` → `SqliteWorkItemRepository`
- `IContextStore` → `SqliteContextStore`
- `IPendingChangeStore` → `SqlitePendingChangeStore`
- `IUnitOfWork` → `SqliteUnitOfWork`
- `IProcessTypeStore` → `SqliteProcessTypeStore`
- `IProcessConfigurationProvider` → `DynamicProcessConfigProvider`

**LegacyDbMigrator exclusion**: `LegacyDbMigrator` is an `internal static class` in `src/Twig/LegacyDbMigrator.cs` (the CLI project). It depends only on `TwigPaths` and `TwigConfiguration` (both in Infrastructure), but since the class itself lives in the CLI assembly, `TwigServiceRegistration` (in Infrastructure) cannot reference it. The `LegacyDbMigrator.MigrateIfNeeded()` invocation MUST remain in CLI `Program.cs`, called after `TwigConfiguration` and `TwigPaths` are resolved but before `SqliteCacheStore` is first accessed. TUI `Program.cs` does not need migration — the TUI only runs after `twig init` has already been executed, and migration is a one-time operation.

**AOT Considerations**: This class lives in `Twig.Infrastructure` which is NOT marked `IsAotCompatible`. `contoso.Extensions.DependencyInjection` is AOT-compatible in .NET 10 — the DI source generator handles generic registrations like `AddSingleton<TService, TImpl>()` at compile time, so they do not inherently require runtime `MakeGenericType`. However, factory-based registrations (`sp => new Foo(...)`) are used here as a deliberate practice: they give explicit control over construction order and parameter resolution, and they avoid relying on consuming projects (CLI AOT, TUI non-AOT) correctly configuring the DI source generator. This is a robustness choice, not a strict AOT necessity. The `InternalsVisibleTo` attribute is declared in `Twig.Infrastructure.csproj` pointing to `Twig` (not the other way around), which means the CLI can see Infrastructure internals. Infrastructure references Domain as a project dependency.

**Why in Infrastructure, not Domain**: The registration creates concrete `Sqlite*` types and reads `TwigConfiguration` from disk — both are infrastructure concerns. Domain stays free of DI and I/O.

#### 2. `SpectreTheme` Refactoring (MODIFIED — `Twig`)

Change from `internal static class` to `internal sealed class` with constructor-injected dependencies:

```csharp
internal sealed class SpectreTheme
{
    private readonly string _iconMode;
    private readonly Dictionary<string, string>? _typeIconIds;
    private readonly Dictionary<string, string>? _typeColors;
    private readonly Dictionary<string, string>? _appearanceColors;
    private readonly IReadOnlyList<StateEntry>? _stateEntries;

    public SpectreTheme(
        DisplayConfig displayConfig,
        List<TypeAppearanceConfig>? typeAppearances = null,
        IReadOnlyList<StateEntry>? stateEntries = null)
    { ... }
}
```

> **Note — no `_icons` field**: Unlike `HumanOutputFormatter`, `SpectreTheme` has no `GetTypeIcon()` equivalent. Badge resolution delegates to `IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)` which uses `_iconMode` and `_typeIconIds` directly — it does not need the `_icons` dictionary (which maps type names to glyph characters via `IconSet.GetIcons()`). Including `_icons` would be dead code.

**`GetTypeBadge(WorkItemType type)`**: Delegates to `IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)` — a new shared method (see section 8 below) that encapsulates the full badge resolution chain: iconId-based lookup first (from `TypeAppearanceConfig`), then the hardcoded switch (known types → unicode glyphs, unknown → `type.Value[0].ToUpperInvariant()`, empty → "■"). This is the **same method** that `HumanOutputFormatter.GetTypeBadge()` and TUI `WorkItemTreeBuilder` delegate to, guaranteeing identical glyph output for the same type and configuration across all three rendering paths. This is NOT `IconSet.GetIcon()` — that method uses the `UnicodeIcons`/`NerdFontIcons` dictionaries with `DefaultIcon = '·'` for unknown types, which differs from badge semantics.

**`GetSpectreColor(WorkItemType type)`**: Calls `TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors)`. If a hex color is returned, converts it to the nearest `Spectre.Console.Color` using `HexToSpectreColor.Convert(hex)` (new helper). Falls back to `DeterministicTypeColor` → Spectre color name mapping only when no hex is available.

**`GetStateStyle(string state)` / `FormatState(string state)`**: Passes `_stateEntries` to `StateCategoryResolver.Resolve(state, _stateEntries)` instead of `null`.

#### 3. `HexToSpectreColor` (NEW — `Twig`)

A small static helper that converts a hex color string to `Spectre.Console.Color`:

```csharp
internal static class HexToSpectreColor
{
    internal static Color? FromHex(string? hex)
    {
        // Parse hex (strip # prefix, handle 8-char ARGB)
        // Return new Color(r, g, b)
    }

    internal static string ToMarkupColor(string? hex)
    {
        // Returns "#{RRGGBB}" for Spectre markup or deterministic fallback
    }
}
```

`Spectre.Console.Color` accepts RGB constructor `new Color(byte r, byte g, byte b)` — no nearest-match approximation needed. Spectre handles terminal capability downgrade internally.

#### 4. `SpectreRenderer` Wiring (MODIFIED — `Twig`)

`SpectreRenderer` currently takes only `IAnsiConsole`. It calls `SpectreTheme.FormatTypeBadge()` and `SpectreTheme.FormatState()` as static methods — both directly in its instance methods (`RenderWorkspaceAsync`, `RenderTreeAsync`, `RenderStatusAsync`, `RenderWorkItemAsync`) and in three `internal static` helper methods: `FormatParentNode()`, `FormatFocusedNode()`, and `BuildSpectreTree()`.

After refactoring `SpectreTheme` to instance-based:
- `SpectreRenderer` constructor adds `SpectreTheme theme` parameter, stored as `private readonly SpectreTheme _theme`.
- All static calls (`SpectreTheme.FormatTypeBadge(...)`) become instance calls (`_theme.FormatTypeBadge(...)`).
- **`FormatParentNode()`, `FormatFocusedNode()`, and `BuildSpectreTree()` MUST be converted from `internal static` to `internal` instance methods** — they need access to `_theme` to call `_theme.FormatTypeBadge()` and `_theme.FormatState()`. Since `BuildSpectreTree()` calls `FormatParentNode()` and `FormatFocusedNode()`, all three must be converted together.
- `CreateWorkspaceTable()`, `TruncateField()`, `StripHtmlTags()`, `BuildSelectionRenderable()`, and `ApplyFilter()` remain `static` — they have no `SpectreTheme` dependency.
- DI registration in `Program.cs` creates `SpectreTheme` from config and injects it.

#### 5. TUI DI Consolidation (MODIFIED — `Twig.Tui`)

Replace manual construction in `Twig.Tui/Program.cs` with `IServiceCollection` + `TwigServiceRegistration.AddTwigCoreServices()`. The TUI already references `Twig.Infrastructure`, so this is a project-dependency-free change.

```csharp
var services = new ServiceCollection();
services.AddTwigCoreServices();  // registers SqliteCacheStore, repos, stores, config, paths
var sp = services.BuildServiceProvider();
var workItemRepo = sp.GetRequiredService<IWorkItemRepository>();
// ... etc
```

This gives the TUI access to `TwigConfiguration` (and therefore `DisplayConfig.Icons`) and `IProcessTypeStore` without manual wiring.

#### 6. TUI Badge Rendering (MODIFIED — `Twig.Tui`)

`WorkItemNode` gains a `_badgeGlyph` field set at construction time:

```csharp
internal sealed class WorkItemNode
{
    public WorkItem WorkItem { get; }
    public bool IsActive { get; }
    private readonly string _badge;

    public WorkItemNode(WorkItem workItem, bool isActive = false, string? badge = null)
    {
        WorkItem = workItem;
        IsActive = isActive;
        _badge = badge ?? workItem.Type.Value[..1];
    }

    public override string ToString()
    {
        var marker = IsActive ? "► " : "  ";
        return $"{marker}{_badge} #{WorkItem.Id} {WorkItem.Title} ({WorkItem.State})";
    }
}
```

`WorkItemTreeBuilder` resolves the badge via `IconSet.ResolveTypeBadge(iconMode, child.Type.Value, typeIconIds)` when constructing child nodes, using the icon mode and type-icon-ID dictionary obtained from `DisplayConfig` and `TypeAppearanceConfig` via DI. This is the same shared resolution method used by `HumanOutputFormatter.GetTypeBadge()` and `SpectreTheme.GetTypeBadge()`, guaranteeing visual parity across all three paths.

#### 7. SaveCommand Fix (MODIFIED — `Twig`)

`SaveCommand.ExecuteAsync()` changes from:
1. Get active context ID → get changes for that ID → push

To:
1. Get all dirty item IDs via `IPendingChangeStore.GetDirtyItemIdsAsync()`
2. For each dirty ID, fetch changes and push to ADO
3. Report results per item

This makes `twig save` work regardless of which item was edited in the TUI.

#### 8. `IconSet.ResolveTypeBadge()` Extraction (NEW method — `Twig.Domain`)

A new static method on `IconSet` that encapsulates the full badge resolution chain used by `HumanOutputFormatter.GetTypeBadge()`. This eliminates the design gap where `GetTypeBadge()` (iconId → hardcoded switch) and `GetIcon()` / `GetTypeIcon()` (type-name dictionary lookup) produce different glyphs for the same type:

```csharp
/// <summary>
/// Resolves the badge glyph for a work item type using the full resolution chain:
/// 1. iconId lookup via TypeAppearanceConfig → GetIconByIconId(mode, iconId)
/// 2. Hardcoded switch: known types → unicode glyphs, unknown → type[0], empty → "■"
///
/// This is the SINGLE authoritative badge resolution method. All consumers
/// (HumanOutputFormatter, SpectreTheme, TUI WorkItemTreeBuilder) MUST delegate here.
/// Do NOT use GetIcon() for badge resolution — that method uses the UnicodeIcons/
/// NerdFontIcons dictionaries with DefaultIcon='·' for unknown types, which differs
/// from badge semantics (type.Value[0] for unknown types).
/// </summary>
public static string ResolveTypeBadge(
    string iconMode,
    string typeName,
    Dictionary<string, string>? typeIconIds)
{
    if (typeIconIds is not null
        && typeIconIds.TryGetValue(typeName, out var iconId))
    {
        var glyph = GetIconByIconId(iconMode, iconId);
        if (glyph is not null)
            return glyph;
    }

    return typeName.ToLowerInvariant() switch
    {
        "epic" => "◆",
        "feature" => "▪",
        "user story" or "product backlog item" or "requirement" => "●",
        "bug" or "impediment" or "risk" => "✦",
        "task" or "test case" or "change request" or "review" or "issue" => "□",
        _ => typeName.Length > 0
            ? typeName[0].ToString().ToUpperInvariant()
            : "■",
    };
}
```

**Why this resolves the G1 design gap**: Prior to this change, three different resolution approaches existed:
- `HumanOutputFormatter.GetTypeBadge()`: iconId lookup → hardcoded unicode switch (returns `type.Value[0]` for unknown types)
- `SpectreTheme.GetTypeBadge()` (current): hardcoded unicode switch only (no iconId lookup at all)
- ITEM-019 (prior plan): `IconSet.GetIcon(NerdFontIcons, type)` — type-name dictionary lookup (returns `'·'` for unknown types)

In nerd font mode with a standard type like "User Story" and **no explicit iconId configured**:
- `GetIcon(NerdFontIcons, 'User Story')` → `"\uea67"` (nerd font glyph from NerdFontIcons dictionary)
- `GetTypeBadge()` hardcoded switch → `"●"` (unicode, no mode awareness)

These are **different glyphs** for the same type and configuration. `ResolveTypeBadge()` eliminates this by providing a single method that all three consumers call. The behavior matches `HumanOutputFormatter.GetTypeBadge()` exactly: iconId first, then the hardcoded switch. In nerd font mode without iconId, the switch still returns unicode glyphs — this is the existing `HumanOutputFormatter` behavior and is correct (nerd font mode only affects badge rendering when iconId is configured or when using `GetTypeIcon()`).

**What `GetIcon()` / `GetTypeIcon()` is for**: The `GetIcon(icons, typeName)` method (used by `HumanOutputFormatter.GetTypeIcon()`) serves a different purpose — it renders the inline type icon glyph in the tree prefix (the `◆ ▪ ● ✦ □` markers before the work item title), using the mode-specific icon dictionaries. `GetTypeBadge()` renders the badge in Spectre markup-wrapped contexts (table cells, tree node labels). Both can coexist — they serve different formatting needs with different fallback semantics.

### Data Flow — Badge Resolution (After)

```
User types: twig tree
  │
  ├─ TTY detected, --no-live not set → Spectre live path
  │   SpectreRenderer._theme.FormatTypeBadge(type)
  │     → SpectreTheme.GetTypeBadge(type)
  │       → IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)
  │         → _typeIconIds lookup → IconSet.GetIconByIconId(_iconMode, iconId)
  │         → fallback: hardcoded switch (same as HumanOutputFormatter.GetTypeBadge)
  │           known types → unicode glyphs; unknown → type.Value[0].ToUpperInvariant()
  │     → SpectreTheme.GetSpectreColor(type)
  │       → TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors)
  │       → HexToSpectreColor.ToMarkupColor(hex)
  │       → fallback: DeterministicTypeColor → Spectre color name
  │
  ├─ Piped or --no-live → Sync path
  │   HumanOutputFormatter.GetTypeBadge(type)
  │     → IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)
  │       (same shared method as Spectre path)
  │   HumanOutputFormatter.GetTypeColor(type)
  │     → TypeColorResolver.ResolveHex(...) → HexToAnsi.ToForeground(hex)
  │     → fallback: DeterministicTypeColor.GetAnsiEscape(type.Value)
  │
  ├─ TUI path
  │   WorkItemTreeBuilder.GetChildren()
  │     → IconSet.ResolveTypeBadge(iconMode, child.Type.Value, typeIconIds)
  │       (same shared method as Spectre and sync paths)
  │     → badge string passed to WorkItemNode constructor
  │
  └─ IDENTICAL glyph for same type + same config across ALL THREE paths

Note: GetTypeBadge()/ResolveTypeBadge() and GetTypeIcon()/GetIcon() are different:
- ResolveTypeBadge(): iconId lookup → hardcoded switch (type.Value[0] for unknown)
- GetTypeIcon()/GetIcon(): type-name dictionary lookup → '·' for unknown
All three rendering paths now use ResolveTypeBadge() for badge glyphs.
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Extract `IconSet.ResolveTypeBadge()` to Domain** | The badge resolution chain (iconId lookup → hardcoded switch) was duplicated in `HumanOutputFormatter.GetTypeBadge()` and `SpectreTheme.GetTypeBadge()`, and ITEM-019 (TUI) originally used `IconSet.GetIcon()` which has different semantics (type-name dictionary lookup, `'·'` for unknowns). Extracting to a single shared method in Domain eliminates the design gap that would have caused nerd font mode inconsistencies (G1). Domain is the correct layer because `IconSet` already lives there and the method has no I/O or framework dependencies. |
| **SpectreTheme as instance class, not interface** | SpectreTheme is a Twig-specific Spectre.Console formatting helper, not a pluggable abstraction. Making it an interface would add unnecessary indirection. Tests can construct it directly with known config values. |
| **TwigServiceRegistration in Infrastructure, not a new project** | Adding a new project would require solution/build changes. Infrastructure already contains config and persistence — DI registration is a natural fit. Both CLI and TUI already reference Infrastructure. |
| **HexToSpectreColor uses Spectre.Console Color(r,g,b)** | Spectre.Console handles terminal capability detection internally (true color → 256 color → 16 color downgrade). No manual nearest-match needed. |
| **SaveCommand iterates all dirty IDs** | This is simpler and more correct than updating active context in TUI on save. Users may edit multiple items in the TUI and expect a single `twig save` to push all changes. |
| **Badge glyph passed to WorkItemNode at construction** | Avoids giving WorkItemNode a dependency on IconSet/config. The TreeBuilder already has access to config via DI and creates nodes — it can resolve the badge and pass it as a string. |
| **SpectreTheme registered via DI, not passed directly** | Allows SpectreRenderer to receive SpectreTheme via constructor injection, matching the existing DI pattern for all other services. |
| **SpectreTheme DI factory wraps `GetAllAsync()` in try-catch** | The factory calls `IProcessTypeStore.GetAllAsync()` which transitively depends on `SqliteCacheStore`. If resolved before `twig init` completes (database doesn't exist), `SqliteCacheStore` throws `InvalidOperationException`. Wrapping in try-catch and returning an empty list is consistent with the `IReadOnlyList<StateEntry>? stateEntries = null` nullable parameter contract on the `SpectreTheme` constructor. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Create shared `IRenderingTheme` interface consumed by all three paths | Maximum abstraction, testable | Over-engineering for 3 consumers; ANSI escapes (formatter) and Spectre markup (renderer) are fundamentally different output formats | Rejected — resolution logic is shared, but output encoding differs by design |
| Move SpectreTheme to Twig.Domain | Would allow TUI to use it | Domain should not depend on Spectre.Console; breaks layering | Rejected |
| Make TUI reference Twig (CLI) project for DI | Shares everything including commands | Circular dependency; TUI would pull in AOT constraints, ConsoleAppFramework, CLI commands | Rejected |
| Create Twig.Shared project for DI registration | Clean separation | Another project to maintain; over-engineering for a registration helper | Rejected — put in Infrastructure instead |
| Fix TUI save by updating active context in TUI | Simpler SaveCommand | Users may edit multiple items; active context semantics become confusing | Rejected — iterate all dirty IDs instead |

---

## Dependencies

### External Dependencies
- `Spectre.Console` — `Color(byte, byte, byte)` constructor (available in all supported versions)
- `contoso.Extensions.DependencyInjection` — `IServiceCollection` extension method pattern (AOT-compatible)
- `Terminal.Gui` v2 — No changes required to Terminal.Gui itself

### Internal Dependencies
- `IconSet` — modified to add `ResolveTypeBadge()` method; existing methods `GetIcon()`, `GetIconByIconId()` unchanged
- `TypeColorResolver`, `StateCategoryResolver`, `DeterministicTypeColor` — unchanged, consumed as-is
- `TwigConfiguration`, `DisplayConfig`, `TypeAppearanceConfig` — unchanged, read at startup
- `HexToAnsi` — unchanged, used only by sync formatter path

### Sequencing Constraints
- EPIC-001 (TwigServiceRegistration) must complete before EPIC-002 (SpectreTheme) and EPIC-003 (TUI DI).
- Within EPIC-002: ITEM-006B (ResolveTypeBadge in Domain) must complete before ITEM-006C (HumanOutputFormatter refactor), ITEM-009 (SpectreTheme refactor), and EPIC-003/ITEM-019 (TUI badge rendering).
- EPIC-002 (SpectreTheme + ResolveTypeBadge) must complete before EPIC-003 (TUI badge rendering uses `ResolveTypeBadge`).
- EPIC-004 (SaveCommand) is independent and can proceed in parallel with EPIC-002/003.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `Twig.Infrastructure` | New `TwigServiceRegistration.cs` file added |
| `Twig/Program.cs` | Refactored to call `TwigServiceRegistration.AddTwigCoreServices()`, removing ~30 lines of manual registration |
| `Twig/Rendering/SpectreTheme.cs` | Converted from static to instance class, all methods updated |
| `Twig/Rendering/SpectreRenderer.cs` | Constructor gains `SpectreTheme` parameter, static calls → instance calls |
| `Twig/Commands/SaveCommand.cs` | `ExecuteAsync` iterates all dirty IDs |
| `Twig.Tui/Program.cs` | Replaced manual construction with DI |
| `Twig.Tui/Views/TreeNavigatorView.cs` | `WorkItemNode` gains badge parameter; `WorkItemTreeBuilder` resolves badges |

### Backward Compatibility
- No public API changes (all modified types are `internal` or `sealed`).
- Config file format unchanged.
- CLI command behavior unchanged (same flags, same output for same inputs).
- `twig save` behavior changes: now processes ALL dirty items, not just active context. This is a bug fix, not a breaking change.

### Performance Implications
- `SpectreTheme` instantiation adds negligible cost (dictionary construction from config — same as `HumanOutputFormatter` constructor).
- `HexToSpectreColor` conversion is O(1) per type (parse hex + construct Color).
- `SaveCommand` now calls `GetDirtyItemIdsAsync()` which is a simple SQLite `SELECT DISTINCT work_item_id FROM pending_changes` — negligible.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| SpectreTheme refactoring breaks existing Spectre rendering tests | Medium | Medium | Run full test suite after each change; `RenderWorkItemTests` and pipeline tests exist |
| TwigServiceRegistration introduces AOT warnings in CLI build | Low | High | Use only factory-based `AddSingleton(sp => ...)` registrations; run `dotnet publish -c Release` AOT build to verify |
| SaveCommand multi-item iteration introduces partial push failures | Low | Medium | Each item is pushed independently; failures for one item don't block others; existing error handling per-item |
| Terminal.Gui TreeView rendering affected by longer badge strings (nerd fonts) | Low | Low | Nerd font glyphs are single-width characters; test visually |

---

## Open Questions

1. **~~State entries for SpectreTheme~~** *(RESOLVED)*: `SpectreTheme` receives `IReadOnlyList<StateEntry>?` at construction time (static for the session), matching how the formatter would receive them. ITEM-013 now explicitly calls `IProcessTypeStore.GetAllAsync()` at startup to collect state entries and pass them to the `SpectreTheme` constructor. Note: `HumanOutputFormatter.GetStateColor()` currently also passes `null` to `StateCategoryResolver.Resolve()` (line 468 of `HumanOutputFormatter.cs`). This is a pre-existing gap — both paths fall back to `FallbackCategory()`. Wiring state entries into `SpectreTheme` gives it _better_ state resolution than the formatter. A follow-up task could wire state entries into `HumanOutputFormatter` as well, but that is out of scope for this plan.

2. **TUI color rendering**: Terminal.Gui v2 has limited color support compared to Spectre.Console. Should `WorkItemNode.ToString()` attempt color markup, or is badge glyph sufficient for the initial consolidation? Recommendation: badge glyph only for now; Terminal.Gui color is out of scope (NG6).

3. **SaveCommand UX for multi-item save**: When saving multiple dirty items, should the command prompt for confirmation per item, or push all silently? Recommendation: push all silently with per-item status reporting (consistent with current single-item behavior).

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/TwigServiceRegistration.cs` | Shared DI registration for core services |
| `src/Twig/Rendering/HexToSpectreColor.cs` | Hex color → Spectre.Console Color converter |
| `tests/Twig.Cli.Tests/Rendering/SpectreThemeTests.cs` | Parity tests: SpectreTheme vs HumanOutputFormatter |
| `tests/Twig.Cli.Tests/Rendering/HexToSpectreColorTests.cs` | Unit tests for hex → Spectre color conversion |
| `tests/Twig.Domain.Tests/ValueObjects/ResolveTypeBadgeTests.cs` | Unit tests for `IconSet.ResolveTypeBadge()` parity |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/ValueObjects/IconSet.cs` | Add `ResolveTypeBadge()` static method — shared badge resolution chain |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | `GetTypeBadge()` delegates to `IconSet.ResolveTypeBadge()` (removes duplicated switch) |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Add `contoso.Extensions.DependencyInjection` package reference |
| `src/Twig/Rendering/SpectreTheme.cs` | Convert from static to instance class; `GetTypeBadge()` delegates to `IconSet.ResolveTypeBadge()` |
| `src/Twig/Rendering/SpectreRenderer.cs` | Add `SpectreTheme` constructor parameter; instance method calls |
| `src/Twig/Program.cs` | Replace manual core service registration with `AddTwigCoreServices()`; register `SpectreTheme` via DI with try-catch around `GetAllAsync()` |
| `src/Twig/Commands/SaveCommand.cs` | Iterate all dirty item IDs instead of active context only |
| `src/Twig.Tui/Program.cs` | Replace manual construction with DI via `TwigServiceRegistration` |
| `src/Twig.Tui/Twig.Tui.csproj` | Add `contoso.Extensions.DependencyInjection` package reference |
| `src/Twig.Tui/Views/TreeNavigatorView.cs` | `WorkItemNode` gains badge; `WorkItemTreeBuilder` resolves badges via `IconSet.ResolveTypeBadge()` |
| `tests/Twig.Cli.Tests/Rendering/RenderingPipelineFactoryTests.cs` | Update `SpectreRenderer` construction to include `SpectreTheme` |
| `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | Update `SpectreRenderer` construction to include `SpectreTheme` |
| `tests/Twig.Tui.Tests/TreeNavigatorViewTests.cs` | Update `WorkItemNode` construction to include badge |
| `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` | Add multi-dirty-item save tests |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: Shared DI Registration

**Goal**: Extract common service registration from CLI `Program.cs` into `TwigServiceRegistration` in `Twig.Infrastructure`. Verify both CLI and TUI can consume it.

**Prerequisites**: None

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Create **`public static class TwigServiceRegistration`** with `AddTwigCoreServices(this IServiceCollection services)` extension method in `Twig.Infrastructure`. The class MUST be declared `public` (not `internal`) because `InternalsVisibleTo` in `Twig.Infrastructure.csproj` does NOT include `Twig.Tui` — an `internal` class would cause a compilation error in the TUI project. The method registers `TwigConfiguration`, `TwigPaths`, `SqliteCacheStore` (lazy), `IWorkItemRepository`, `IContextStore`, `IPendingChangeStore`, `IUnitOfWork`, `IProcessTypeStore`, `IProcessConfigurationProvider`. Uses factory-based `AddSingleton(sp => ...)` pattern for AOT compatibility. **Does NOT include `LegacyDbMigrator`** — that class is `internal static` in the CLI project and cannot be referenced from Infrastructure. CLI `Program.cs` must continue to call `LegacyDbMigrator.MigrateIfNeeded()` directly after consuming `AddTwigCoreServices()`. | `src/Twig.Infrastructure/TwigServiceRegistration.cs` | DONE |
| ITEM-002 | IMPL | Add `contoso.Extensions.DependencyInjection` package reference to `Twig.Infrastructure.csproj` | `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | DONE |
| ITEM-003 | IMPL | Refactor CLI `Program.cs` to call `services.AddTwigCoreServices()` and remove the 12 manual registration lines for core services. Keep CLI-specific registrations (auth, HTTP, formatters, commands, Spectre.Console, hints). **Keep `LegacyDbMigrator.MigrateIfNeeded()` call in `Program.cs`** immediately after `TwigConfiguration` and `TwigPaths` are resolved (before DI container build). | `src/Twig/Program.cs` | DONE |
| ITEM-004 | TEST | Verify full CLI test suite passes after refactor (`dotnet test` for `Twig.Cli.Tests`) | — | DONE |
| ITEM-005 | TEST | Verify AOT publish succeeds without new trim/AOT warnings (`dotnet publish src/Twig/Twig.csproj -c Release`) | — | DONE |

**Acceptance Criteria**:
- [x] `TwigServiceRegistration` is declared `public static class` (not `internal`) — required for `Twig.Tui` visibility
- [x] `TwigServiceRegistration.AddTwigCoreServices()` exists and registers all listed services (excluding `LegacyDbMigrator`)
- [x] CLI `Program.cs` calls `AddTwigCoreServices()` and has no duplicate manual registrations
- [x] CLI `Program.cs` still calls `LegacyDbMigrator.MigrateIfNeeded()` directly (not via shared registration)
- [x] All `Twig.Cli.Tests` pass
- [x] AOT publish produces no new warnings

---

### EPIC-002:Badge Resolution Extraction & SpectreTheme Consolidation

**Goal**: Extract the badge resolution chain into `IconSet.ResolveTypeBadge()` (Domain), convert `SpectreTheme` from static to instance-based, and inject icon/color/state resolution dependencies so all consumers use the same chain.

**Prerequisites**: EPIC-001 (for DI registration of SpectreTheme) — DONE ✅

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-006 | IMPL | Create `HexToSpectreColor` static helper in `Twig/Rendering/` that converts hex color strings (6-char RGB, 8-char ARGB, optional `#` prefix) to Spectre.Console `Color` objects and Spectre markup color strings (`#{RRGGBB}`). Reuse parsing logic pattern from `HexToAnsi`. | `src/Twig/Rendering/HexToSpectreColor.cs` | DONE ✅ |
| ITEM-007 | TEST | Unit tests for `HexToSpectreColor`: valid 6-char, valid 8-char ARGB, `#` prefix, invalid input returns null, null input returns null. | `tests/Twig.Cli.Tests/Rendering/HexToSpectreColorTests.cs` | DONE ✅ |
| ITEM-006B | IMPL | Add `IconSet.ResolveTypeBadge(string iconMode, string typeName, Dictionary<string, string>? typeIconIds)` static method to `Twig.Domain/ValueObjects/IconSet.cs`. This method encapsulates the full badge resolution chain that is currently duplicated in `HumanOutputFormatter.GetTypeBadge()` (lines 440–461) and `SpectreTheme.GetTypeBadge()` (lines 54–67): (1) if `typeIconIds` contains an entry for `typeName`, call `GetIconByIconId(iconMode, iconId)` — if non-null, return it; (2) fall through to the hardcoded switch (epic→◆, feature→▪, etc., unknown→`typeName[0].ToUpperInvariant()`, empty→"■"). The switch is **mode-agnostic** — it always returns unicode glyphs. Nerd font glyphs are only returned via the iconId path in step 1. This is intentional and matches existing `HumanOutputFormatter` behavior. | `src/Twig.Domain/ValueObjects/IconSet.cs` | DONE ✅ |
| ITEM-006C | IMPL | Refactor `HumanOutputFormatter.GetTypeBadge()` to delegate to `IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)`, removing the duplicated switch. Verify that `GetTypeIcon()` (which uses `IconSet.GetIcon(_icons, type.Value)`) remains unchanged — it serves a different purpose (inline icon glyph with `'·'` default for unknowns). | `src/Twig/Formatters/HumanOutputFormatter.cs` | DONE ✅ |
| ITEM-006D | TEST | Create `ResolveTypeBadgeTests` in Domain test project. Test cases: (a) all 13 known types return correct unicode glyph (no iconId), (b) unknown type returns `typeName[0].ToUpperInvariant()`, (c) empty type returns "■", (d) with iconId + unicode mode returns unicode-by-iconId glyph, (e) with iconId + nerd mode returns nerd-font-by-iconId glyph, (f) with iconId that has no dictionary entry falls through to hardcoded switch, (g) verify parity with `HumanOutputFormatter.GetTypeBadge()` for all known types by constructing a formatter and comparing outputs. | `tests/Twig.Domain.Tests/ValueObjects/ResolveTypeBadgeTests.cs` | DONE ✅ |
| ITEM-008 | IMPL | Convert `SpectreTheme` from `internal static class` to `internal sealed class`. Add constructor accepting `DisplayConfig displayConfig`, `List<TypeAppearanceConfig>? typeAppearances`, `IReadOnlyList<StateEntry>? stateEntries`. Constructor initializes `_iconMode`, `_typeIconIds`, `_typeColors`, `_appearanceColors`, `_stateEntries` — mirroring `HumanOutputFormatter` constructor fields except `_icons` (not needed — `SpectreTheme` has no `GetTypeIcon()` equivalent; badge resolution uses `_iconMode` and `_typeIconIds` via `ResolveTypeBadge()`). | `src/Twig/Rendering/SpectreTheme.cs` | DONE ✅ |
| ITEM-009 | IMPL | Rewrite `SpectreTheme.GetTypeBadge()` to delegate to `IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds)`. Remove the hardcoded switch from `SpectreTheme` — it is now in `IconSet.ResolveTypeBadge()`. This guarantees that `SpectreTheme.GetTypeBadge()`, `HumanOutputFormatter.GetTypeBadge()`, and TUI `WorkItemTreeBuilder` all produce identical output for the same type and configuration. | `src/Twig/Rendering/SpectreTheme.cs` | DONE ✅ |
| ITEM-010 | IMPL | Rewrite `SpectreTheme.GetSpectreColor()` to call `TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors)`, convert via `HexToSpectreColor.ToMarkupColor()`, fall back to `DeterministicTypeColor` → Spectre color name only when hex is null. | `src/Twig/Rendering/SpectreTheme.cs` | DONE ✅ |
| ITEM-011 | IMPL | Update `SpectreTheme.GetStateStyle()` and `FormatState()` to pass `_stateEntries` to `StateCategoryResolver.Resolve(state, _stateEntries)`. | `src/Twig/Rendering/SpectreTheme.cs` | DONE ✅ |
| ITEM-012 | IMPL | Add `SpectreTheme` constructor parameter to `SpectreRenderer`. Store as `private readonly SpectreTheme _theme`. Change all static `SpectreTheme.X()` calls to instance `_theme.X()` calls throughout the class — this includes direct calls in `RenderWorkspaceAsync()`, `RenderTreeAsync()`, `RenderStatusAsync()`, and `RenderWorkItemAsync()`. **CRITICAL: Three `internal static` helper methods — `FormatParentNode()`, `FormatFocusedNode()`, and `BuildSpectreTree()` — also call `SpectreTheme.FormatTypeBadge()` and `SpectreTheme.FormatState()`.** After `SpectreTheme` becomes an instance class, these static calls will fail to compile. These three methods MUST be converted from `internal static` to `internal` instance methods so they can access `_theme`. `BuildSpectreTree()` calls `FormatParentNode()` and `FormatFocusedNode()`, so all three must be converted together. The existing tests (`RenderWorkItemTests`, `RenderingPipelineFactoryTests`) do NOT call these helpers directly, so the test impact is minimal — only ITEM-014 updates (constructor changes) are needed. Keep `CreateWorkspaceTable()` as static (it has no dependency on config or `SpectreTheme` instance state). Also keep `TruncateField()`, `StripHtmlTags()`, `BuildSelectionRenderable()`, and `ApplyFilter()` as static — they have no `SpectreTheme` dependency. | `src/Twig/Rendering/SpectreRenderer.cs` | DONE ✅ |
| ITEM-013 | IMPL | Register `SpectreTheme` in CLI `Program.cs` DI: resolve `TwigConfiguration` for display config and type appearances, resolve `IProcessTypeStore` and call `GetAllAsync()` to collect all `ProcessTypeRecord` objects, then **flatten to a single list via `records.SelectMany(r => r.States).ToList()`** to produce `IReadOnlyList<StateEntry>`. Construct `new SpectreTheme(cfg.Display, cfg.TypeAppearances, stateEntries)`. Register as a factory-based singleton. The `GetAllAsync()` call is awaited inside a `Task.Run(...).GetAwaiter().GetResult()` wrapper (same pattern as config loading in `Program.cs`). **The `GetAllAsync()` call MUST be wrapped in a try-catch that returns an empty list on failure** — specifically, if `SqliteCacheStore` is uninitialized (e.g., `twig init` code path resolves rendering services before the database exists), `SqliteCacheStore` throws `InvalidOperationException("Twig workspace not initialized")`. The try-catch must catch this and pass `stateEntries: null` to the `SpectreTheme` constructor, which is consistent with its `IReadOnlyList<StateEntry>? stateEntries = null` nullable parameter contract and causes `StateCategoryResolver.Resolve()` to fall back to `FallbackCategory()`. Update `SpectreRenderer` registration to inject `SpectreTheme`. | `src/Twig/Program.cs` | DONE ✅ |
| ITEM-014 | TEST | Update `RenderWorkItemTests` and `RenderingPipelineFactoryTests` to construct `SpectreRenderer` with a default `SpectreTheme(new DisplayConfig())`. | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs`, `tests/Twig.Cli.Tests/Rendering/RenderingPipelineFactoryTests.cs` | DONE ✅ |
| ITEM-015 | TEST | Create `SpectreThemeTests` verifying: (a) `GetTypeBadge` returns same glyph as `IconSet.ResolveTypeBadge` (and transitively `HumanOutputFormatter.GetTypeBadge`) for all known types in unicode mode, (b) `GetTypeBadge` returns nerd font glyphs when mode is "nerd" and `TypeAppearanceConfig` has iconId, (c) `FormatTypeBadge` includes Spectre markup color, (d) `FormatState` returns correct color for each `StateCategory`, (e) with iconId-bearing `TypeAppearanceConfig`, badge matches `HumanOutputFormatter.GetTypeBadge`, (f) for unknown/custom type names, both return `type.Value[0].ToUpperInvariant()`, (g) with `stateEntries: null`, state resolution falls back to `FallbackCategory()`. | `tests/Twig.Cli.Tests/Rendering/SpectreThemeTests.cs` | DONE ✅ |
| ITEM-016 | TEST | Verify full test suite passes (`dotnet test`) | — | DONE ✅ |

**Acceptance Criteria**:
- [x] `IconSet.ResolveTypeBadge()` exists and encapsulates the full badge resolution chain (iconId → hardcoded switch)
- [x] `HumanOutputFormatter.GetTypeBadge()` delegates to `IconSet.ResolveTypeBadge()` — no duplicated switch
- [x] `SpectreTheme.GetTypeBadge()` delegates to `IconSet.ResolveTypeBadge()` — identical output for same config
- [x] `SpectreTheme` is instance-based with injected config (no `_icons` field — not needed)
- [x] `SpectreRenderer.FormatParentNode()`, `FormatFocusedNode()`, `BuildSpectreTree()` converted from `static` to instance methods
- [x] `GetSpectreColor` uses `TypeColorResolver` → hex → Spectre Color
- [x] State resolution passes state entries (from `IProcessTypeStore.GetAllAsync()` with try-catch fallback) to `StateCategoryResolver`
- [x] ITEM-013 DI factory handles `GetAllAsync()` failure gracefully (returns null/empty, does not throw)
- [x] All existing tests pass; new parity tests and `ResolveTypeBadgeTests` pass

---

### EPIC-003: TUI DI & Badge Rendering

**Goal**: Replace manual DI in TUI `Program.cs` with shared registration; give `WorkItemNode` badge rendering from `IconSet`.

**Prerequisites**: EPIC-001 (TwigServiceRegistration) — DONE ✅, EPIC-002 (pattern established) — DONE ✅

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-017 | IMPL | Add `contoso.Extensions.DependencyInjection` package reference to `Twig.Tui.csproj`. | `src/Twig.Tui/Twig.Tui.csproj` | DONE ✅ |
| ITEM-018 | IMPL | Refactor `Twig.Tui/Program.cs`: replace manual `SqliteCacheStore`/repo/store construction (lines 16–54) with `ServiceCollection` + `AddTwigCoreServices()` + `BuildServiceProvider()`. Resolve `IWorkItemRepository`, `IContextStore`, `IPendingChangeStore`, `IProcessConfigurationProvider`, `TwigConfiguration` from the provider. Keep the workspace-not-initialized guard before building the provider. | `src/Twig.Tui/Program.cs` | DONE ✅ |
| ITEM-019 | IMPL | Add `string iconMode` and `Dictionary<string, string>? typeIconIds` parameters to `WorkItemTreeBuilder` constructor (or receive them from `TreeNavigatorView`). In `GetChildren()`, resolve badge via `IconSet.ResolveTypeBadge(iconMode, child.Type.Value, typeIconIds)` and pass to `WorkItemNode`. Also resolve badge for root node construction in `TreeNavigatorView.LoadRootAsync()`. **This uses the same `ResolveTypeBadge()` method as `HumanOutputFormatter` and `SpectreTheme`**, guaranteeing G1 (identical glyphs for same type and config). Do NOT use `IconSet.GetIcon()` — that method has different semantics (type-name dictionary lookup, returns `'·'` for unknown types). | `src/Twig.Tui/Views/TreeNavigatorView.cs` | DONE ✅ |
| ITEM-020 | IMPL | Update `WorkItemNode` constructor to accept optional `string? badge` parameter. Use `_badge` in `ToString()`. Default to first character of type name if not provided (backward compat). | `src/Twig.Tui/Views/TreeNavigatorView.cs` | DONE ✅ |
| ITEM-021 | IMPL | In TUI `Program.cs`, resolve `TwigConfiguration` from DI, extract `config.Display.Icons` (icon mode string) and build `typeIconIds` dictionary from `config.TypeAppearances` (same logic as `HumanOutputFormatter` constructor lines 46–48: `typeAppearances?.Where(a => a.IconId is not null).ToDictionary(a => a.Name, a => a.IconId!, StringComparer.OrdinalIgnoreCase)`). Pass both `iconMode` and `typeIconIds` to `TreeNavigatorView` constructor (which passes them to `WorkItemTreeBuilder`). | `src/Twig.Tui/Program.cs`, `src/Twig.Tui/Views/TreeNavigatorView.cs` | DONE ✅ |
| ITEM-022 | TEST | Update `TreeNavigatorViewTests` to pass icon mode and typeIconIds to constructors. Verify `WorkItemNode.ToString()` includes badge glyph matching `IconSet.ResolveTypeBadge()` output. | `tests/Twig.Tui.Tests/TreeNavigatorViewTests.cs` | DONE ✅ |
| ITEM-023 | TEST | Verify full test suite passes (`dotnet test`) | — | DONE ✅ |

**Acceptance Criteria**:
- [x] TUI `Program.cs` uses `TwigServiceRegistration` — no manual `SqliteCacheStore` construction
- [x] `WorkItemNode.ToString()` renders badges via `IconSet.ResolveTypeBadge()` — same method as formatter and SpectreTheme
- [x] For same type + config, TUI badge glyph matches `HumanOutputFormatter.GetTypeBadge()` output (G1 verified)
- [x] All existing TUI tests pass; badge assertions added

---

### EPIC-004: SaveCommand Multi-Item Fix

**Goal**: Make `twig save` push pending changes for ALL dirty items, not only the active context item.

**Prerequisites**: None (independent of EPIC-001/002/003)

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-024 | IMPL | Refactor `SaveCommand.ExecuteAsync()`: after getting `activeId`, call `pendingChangeStore.GetDirtyItemIdsAsync()` to get all IDs with pending changes. If the list is empty, print "Nothing to save" and return. For each dirty ID, fetch the work item, get its pending changes, run conflict resolution, push field changes and notes, clear pending changes, and refresh cache. Report per-item results. | `src/Twig/Commands/SaveCommand.cs` | DONE |
| ITEM-025 | TEST | Add test: save with changes on non-active item pushes successfully. Add test: save with changes on multiple items pushes all. Verify existing single-item save tests still pass. | `tests/Twig.Cli.Tests/Commands/EditSaveCommandTests.cs` (existing file — extend, do NOT create a new `SaveCommandTests.cs`) | DONE |
| ITEM-026 | TEST | Verify full test suite passes (`dotnet test`) | — | DONE |

**Acceptance Criteria**:
- [x] `twig save` iterates all dirty IDs via `GetDirtyItemIdsAsync()`
- [x] Changes saved by TUI for a non-active item are pushed by `twig save`
- [x] Multiple dirty items are all pushed in a single `twig save` invocation
- [x] Existing save tests pass

---

## References

- `src/Twig/Formatters/HumanOutputFormatter.cs` — Authority for icon/color/badge resolution
- `src/Twig/Rendering/SpectreTheme.cs` — Current static Spectre theme with hardcoded values
- `src/Twig/Rendering/SpectreRenderer.cs` — Spectre.Console async renderer
- `src/Twig.Tui/Program.cs` — TUI entry point with manual DI
- `src/Twig.Domain/ValueObjects/IconSet.cs` — Icon glyph dictionaries and resolution methods
- `src/Twig.Domain/Services/TypeColorResolver.cs` — Hex color resolution chain
- `src/Twig.Domain/Services/StateCategoryResolver.cs` — State category classification
- `src/Twig.Domain/Services/DeterministicTypeColor.cs` — Fallback ANSI color assignment
- `src/Twig/Formatters/HexToAnsi.cs` — Hex to ANSI escape conversion
- `docs/projects/twig-iux-tui.plan.md` — TUI architecture documentation
- Spectre.Console Color API: `new Color(byte r, byte g, byte b)` constructor
