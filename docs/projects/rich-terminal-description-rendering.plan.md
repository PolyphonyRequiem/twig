# Plan: Rich Terminal Description Rendering

> **Date**: 2026-03-31
> **Status**: ✅ Done
> **ADO Epic**: #1326

---

## Executive Summary

Enhance twig's terminal output to render ADO work item descriptions with rich
formatting instead of flat plain text. Currently, HTML descriptions from ADO are
stripped to plain text by `HtmlToPlainText` (in `BuildStatusViewAsync`) or
truncated to 200 characters via `TruncateField` (in `RenderWorkItemAsync`). This
loses all structure — headings, bold, italic, code, and list indentation disappear.

Two PRs deliver the feature:
1. **HTML-to-Spectre converter** — A new `HtmlToSpectreMarkup` method that converts
   HTML elements to Spectre.Console markup, wired into `BuildStatusViewAsync`
2. **Full-width description in `RenderWorkItemAsync`** — Restructure the work item
   detail view to render descriptions as full-width sections (matching
   `BuildStatusViewAsync`), using the new rich converter

Issue #1327 (increase `MaxDescriptionLines` from 15 to 30) is already complete.

---

## Background

### Current Architecture

The codebase has **two distinct HTML-to-text conversion paths** for descriptions:

| Path | Method | Location | Behavior |
|------|--------|----------|----------|
| `twig status` | `BuildStatusViewAsync` | `SpectreRenderer.cs` → description block after `AddExtendedFieldRows` | Full-width section below grid; `HtmlToPlainText()` → 30-line plain text; `Markup.Escape()` wrapping |
| `twig show` | `RenderWorkItemAsync` | `SpectreRenderer.cs` → `System.Description` block inside `Live` callback | 2-column grid row; `TruncateField()` → 200-char truncated plain text |
| `--output human` | `HumanOutputFormatter.RenderItem` | `HumanOutputFormatter.cs` → description section | ANSI-formatted section; `HtmlToPlainText()` → plain text with ANSI dim separators |

### Key Methods

**`FormatterHelpers.HtmlToPlainText(string? html)`** — `src/Twig/Formatters/FormatterHelpers.cs` (method `HtmlToPlainText`)
- 5-pass AOT-safe algorithm: insert block markers → strip tags → decode entities → normalize lines → truncate at 30 lines
- Uses only `StringBuilder` — no regex, no reflection
- Converts `<li>` to `• ` prefix, block elements to newlines
- All inline formatting (`<b>`, `<em>`, `<code>`) is **stripped completely**

**`SpectreRenderer.TruncateField(string value, int maxLength)`** — `src/Twig/Rendering/SpectreRenderer.cs` (method `TruncateField`)
- Calls `StripHtmlTags()` (simple tag stripping) then truncates to `maxLength` characters
- Used only in `RenderWorkItemAsync` for extended fields (Description, History, Tags)

**`SpectreRenderer.StripHtmlTags(string input)`** — `src/Twig/Rendering/SpectreRenderer.cs` (method `StripHtmlTags`)
- Regex-free character-by-character state machine
- Handles unclosed `<` gracefully (flushes as literal text)

### Call-Site Audit

#### HtmlToPlainText Call Sites

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `src/Twig/Formatters/HumanOutputFormatter.cs` | `RenderItem` | Plain text output with ANSI escapes | **None** — ANSI formatter continues to use `HtmlToPlainText` |
| `src/Twig/Rendering/SpectreRenderer.cs` | `BuildStatusViewAsync` | Full-width description section, wrapped in `Markup.Escape()` | **HIGH** — Replace with `HtmlToSpectreMarkup`, remove `Markup.Escape()` |

#### TruncateField Call Sites

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `src/Twig/Rendering/SpectreRenderer.cs` | `RenderWorkItemAsync` (Description block) | `TruncateField(description, 200)` in grid row | **HIGH** — Remove; replace with full-width rich section |
| `src/Twig/Rendering/SpectreRenderer.cs` | `RenderWorkItemAsync` (History block) | `TruncateField(history, 200)` in grid row | **None** — History stays in grid with current behavior |
| `src/Twig/Rendering/SpectreRenderer.cs` | `RenderWorkItemAsync` (Tags block) | `TruncateField(tags, 200)` in grid row | **None** — Tags stay in grid with current behavior |

#### Markup.Escape() Usage (Description Path)

| File | Method / Location | Current Pattern | Change |
|------|-------------------|-----------------|--------|
| `SpectreRenderer.cs` | `BuildStatusViewAsync` → description section | `new Markup(Markup.Escape(plainText))` | Remove `Markup.Escape()` — `HtmlToSpectreMarkup` pre-escapes user content |
| `SpectreRenderer.cs` | `RenderWorkItemAsync` → Description grid row | `Markup.Escape(TruncateField(description, 200))` | Remove entirely — description moves out of grid |

#### Non-Impacted Call Sites

| File | Method | Reason |
|------|--------|--------|
| `src/Twig.Tui/Views/WorkItemFormView.cs` | `StripHtmlTags` | TUI is a separate app, not Spectre-based; uses Terminal.Gui. Not in scope. |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | `HtmlToPlainText` | ANSI text output, not Spectre. Continues using plain text. |

### Existing Tests

| File | Tests | Coverage |
|------|-------|----------|
| `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | 18 `HtmlToPlainText_*` tests | Null, empty, paragraphs, divs, br, headings, lists, entities, collapsing, truncation, mixed content, unclosed tags, case-insensitive |
| `tests/Twig.Cli.Tests/Rendering/BuildStatusViewDescriptionTests.cs` | 9 tests | Description present/absent, HTML stripping, whitespace, empty HTML, exclusion from extended fields (auto-detect + status entries), long descriptions, multi-paragraph |
| `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | 15 `RenderWorkItemAsync_*` tests, 3 `TruncateField_*` tests, 8 `StripHtmlTags_*` tests | Core fields, extended fields (desc/history/tags), dirty marker, null item, HTML stripping, whitespace, assignee, cancellation, long tags, short/long/HTML truncation, plain text, nested, self-closing, unclosed tags |

### Spectre Markup Conventions in Codebase

The codebase uses these Spectre markup styles consistently:
- `[bold]` — Headers, active items, emphasis
- `[dim]` — Loading placeholders, separators, secondary info
- `[italic]` — Minimal use (one instance: `[dim italic]`)
- `[blue]`, `[yellow]`, `[aqua]`, `[green]` — Semantic colors
- **All user content** wrapped with `Markup.Escape()` before insertion into markup strings

---

## Design Decisions

| ID | Decision | Rationale | Trade-off |
|----|----------|-----------|-----------|
| DD-01 | New `HtmlToSpectreMarkup` method alongside existing `HtmlToPlainText` | `HtmlToPlainText` is still needed by `HumanOutputFormatter` (ANSI output). Two separate methods avoids a mode/strategy parameter and keeps each method focused. | Minor code duplication in entity decoding and normalization (shared private helpers mitigate this). |
| DD-02 | Return `string` containing Spectre markup (not `IRenderable`) | Easier to test (string assertions), callers wrap in `new Markup(result)` — consistent with existing codebase patterns. User content is pre-escaped internally. | Callers must remember NOT to call `Markup.Escape()` on the result. XML doc comment documents this. |
| DD-03 | Single-pass state machine with text buffer flushing | AOT-safe (no regex, no reflection). Text is accumulated in a buffer, flushed with `Markup.Escape()` when a tag is encountered, then the tag's Spectre equivalent is emitted raw. Entity decoding happens during text accumulation. | Single-pass is more complex than multi-pass but handles the escaping order correctly (entities decoded → then Spectre-escaped). |
| DD-04 | Mapping: `<b>`/`<strong>` → `[bold]`, `<em>`/`<i>` → `[italic]`, `<code>` → `[dim]`, `<h1-6>` → `[bold]` + newlines | Matches codebase conventions. `[dim]` for code is consistent with how the codebase uses dim for secondary info. Headings as bold-with-newlines gives visual separation. | `<code>` as `[dim]` is imperfect (no monospace/background), but terminals don't support background highlight in markup. Alternative `[grey]` tested less readable. |
| DD-05 | Graceful degradation for malformed HTML | Unclosed `<` treated as literal text (matches `StripHtmlTags` behavior). Unmatched closing tags emit `[/]` which Spectre handles silently. Unmatched opening tags apply style to remaining content (acceptable). | Deeply malformed HTML with mismatched tags may produce unexpected styling, but content is never lost. |
| DD-06 | Reuse `NormalizeLines`, `TruncateLines`, `TryDecodeEntity`, `IsBreakElement` from existing code | These private helpers are general-purpose and work identically for both plain text and Spectre markup output. | None — pure code reuse. |
| DD-07 | `HumanOutputFormatter` unchanged | The ANSI output path uses escape codes (`\x1b[32m`), not Spectre markup. Spectre tags like `[bold]` would render as literal text. | ANSI output remains plain text only. Users who want rich descriptions use the default Spectre output. |
| DD-08 | History and Tags stay in grid rows (not full-width) | These fields are typically short (one line). Full-width rendering would waste vertical space. Description is the only field that benefits from full-width. | Inconsistency: Description is full-width but History is still truncated to 200 chars. Acceptable trade-off — History is the latest comment only. |

---

## Proposed Design

### Architecture

```
┌─ HTML Input (ADO System.Description) ─────────────────────────────┐
│  <div><h2>Summary</h2><p>Fix the <b>auth</b> bug</p>             │
│  <ul><li>Login works</li><li>Errors shown</li></ul></div>         │
└───────────────────────────┬───────────────────────────────────────┘
                            │
                            ▼
┌─ FormatterHelpers ────────────────────────────────────────────────┐
│                                                                   │
│  HtmlToSpectreMarkup(html)          HtmlToPlainText(html)         │
│  ├─ Single-pass state machine       ├─ 5-pass pipeline (unchanged)│
│  ├─ Text buffer → Markup.Escape()   ├─ Strip all tags             │
│  ├─ Tags → Spectre markup           ├─ Decode entities            │
│  ├─ Entities → decoded + escaped    ├─ Normalize + truncate       │
│  ├─ NormalizeLines (shared)         └─ Returns plain string       │
│  ├─ TruncateLines (shared)                                        │
│  └─ Returns Spectre markup string                                 │
│                                                                   │
└───────────────┬──────────────────────────────────┬───────────────┘
                │                                  │
                ▼                                  ▼
┌─ SpectreRenderer ──────────┐    ┌─ HumanOutputFormatter ─────────┐
│                            │    │                                 │
│  BuildStatusViewAsync      │    │  RenderItem                    │
│    new Markup(markup)      │    │    ANSI-formatted plain text   │
│                            │    │    (unchanged)                 │
│  RenderWorkItemAsync       │    └─────────────────────────────────┘
│    new Markup(markup)      │
│    (full-width section)    │
│                            │
└────────────────────────────┘
```

### Conversion Mapping

| HTML Element | Spectre Markup | Visual Effect |
|-------------|----------------|---------------|
| `<b>`, `<strong>` | `[bold]...[/]` | **Bold text** |
| `<em>`, `<i>` | `[italic]...[/]` | *Italic text* |
| `<code>` | `[dim]...[/]` | Dimmed text (code appearance) |
| `<h1>`–`<h6>` | `\n[bold]...[/]` | Bold with preceding blank line |
| `<li>` | `\n• ` | Bullet-prefixed line |
| `<br>`, `<br/>` | `\n` | Line break |
| `<p>`, `<div>`, `<pre>` | `\n` (before opening) | Block-level break |
| `<ul>`, `<ol>`, `<table>`, etc. | Stripped | No markup emitted |
| User text content | `Markup.Escape(text)` | Safe literal rendering |

### Escaping Strategy

User content is escaped to prevent Spectre markup injection:

```
Input:  <b>Use array[0] for &amp; operations</b>
                │                    │
                ▼                    ▼
        decode entity          Markup.Escape
        "&amp;" → "&"          "[0]" → "[[0]]"
        "&" → escaped          "&" → "&"
                │                    │
                └────────┬───────────┘
                         ▼
Output: [bold]Use array[[0]] for & operations[/]
```

The single-pass approach handles this correctly:
1. Text characters accumulate in a buffer
2. Entity references (`&amp;`) are decoded during accumulation
3. When a tag boundary is reached, the buffer is flushed through `Markup.Escape()`
4. The tag's Spectre equivalent is emitted raw (not escaped)

### RenderWorkItemAsync Refactoring

**Before:**
```
╭─ #42 Fix the auth bug ──────────────────────╮
│ Type:        🐛 Bug                          │
│ State:       Active                          │
│ Assigned:    Daniel Green                    │
│ Area:        Project                         │
│ Iteration:   Project\Sprint 1               │
│ Description: As a developer, I need to fi…  │  ← 200-char truncated, no formatting
│ History:     Fixed the login flow yester…    │
│ Tags:        security, auth                  │
╰──────────────────────────────────────────────╯
```

**After:**
```
╭─ #42 Fix the auth bug ──────────────────────╮
│ Type:        🐛 Bug                          │
│ State:       Active                          │
│ Assigned:    Daniel Green                    │
│ Area:        Project                         │
│ Iteration:   Project\Sprint 1               │
│ History:     Fixed the login flow yester…    │
│ Tags:        security, auth                  │
│                                              │
│ ── Description ───────────────────────────── │
│ As a developer, I need to fix the auth bug   │  ← Full-width, up to 30 lines
│ that prevents users from logging in.         │
│                                              │
│ Acceptance Criteria:                         │  ← Bold heading
│ • Login works with valid credentials         │  ← Bulleted list
│ • Invalid credentials show error message     │
╰──────────────────────────────────────────────╯
```

---

## Implementation Plan

### Epic 1: HTML to Spectre Markup Converter — Issue #1328

> **Classification:** Deep (3 files, complex parsing logic + comprehensive tests)
> **Estimated LoC:** ~350
> **ADO Issue:** #1328
> **Predecessor:** #1327 (Done)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| E1-T1 | Create `HtmlToSpectreMarkup` method | `src/Twig/Formatters/FormatterHelpers.cs` | L |
| E1-T2 | Unit tests for `HtmlToSpectreMarkup` | `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | L |
| E1-T3 | Wire into `BuildStatusViewAsync` + verify existing tests pass | `src/Twig/Rendering/SpectreRenderer.cs` | S |

#### E1-T1: Create `HtmlToSpectreMarkup` Method

Add a new `internal static string HtmlToSpectreMarkup(string? html)` method to
`FormatterHelpers.cs`. This method converts HTML to a string containing Spectre.Console
markup tags, with all user text content pre-escaped via `Markup.Escape()`.

**Algorithm — single-pass state machine with text buffer flushing:**

1. Guard: `string.IsNullOrWhiteSpace(html)` → return `string.Empty`
2. Initialize `StringBuilder result` (Spectre markup output) and `StringBuilder textBuffer`
   (accumulates user text between tags)
3. Scan character by character:
   - **`<` encountered**: Flush `textBuffer` through `Markup.Escape()` → `result`.
     Read to next `>` to extract tag name (strip attributes at first space).
     Call `EmitSpectreTag()` to append the Spectre markup equivalent.
     If no `>` found (unclosed `<`), append remaining text to `textBuffer` as literal.
   - **`&` encountered**: Attempt entity decode via existing `TryDecodeEntity()`.
     Decoded character appended to `textBuffer`. If decode fails, append `&` literal.
   - **Other character**: Append to `textBuffer`.
4. Flush remaining `textBuffer` through `Markup.Escape()` → `result`
5. Pass result through existing `NormalizeLines()` and `TruncateLines()`

**Helper method — `EmitSpectreTag(StringBuilder result, ReadOnlySpan<char> tagName, bool isClosing)`:**

```
if b/strong:     append isClosing ? "[/]" : "[bold]"
if em/i:         append isClosing ? "[/]" : "[italic]"
if code:         append isClosing ? "[/]" : "[dim]"
if h1-h6:       append isClosing ? "[/]" : "\n[bold]"
if li:           if !isClosing append "\n• "
if br/br/:       if !isClosing append "\n"
if p/div/pre:    if !isClosing append "\n"
else:            strip silently
```

#### E1-T2: Unit Tests for `HtmlToSpectreMarkup`

Add tests to existing `FormatterHelpersTests.cs` in a new `// ── HtmlToSpectreMarkup` section:

| Test | Input | Expected |
|------|-------|----------|
| `HtmlToSpectreMarkup_NullInput_ReturnsEmpty` | `null` | `string.Empty` |
| `HtmlToSpectreMarkup_EmptyString_ReturnsEmpty` | `""` | `string.Empty` |
| `HtmlToSpectreMarkup_PlainText_PassesThrough` | `"Hello world"` | `"Hello world"` |
| `HtmlToSpectreMarkup_BoldTag_RendersBold` | `"<b>bold</b>"` | Contains `[bold]bold[/]` |
| `HtmlToSpectreMarkup_StrongTag_RendersBold` | `"<strong>text</strong>"` | Contains `[bold]text[/]` |
| `HtmlToSpectreMarkup_EmTag_RendersItalic` | `"<em>italic</em>"` | Contains `[italic]italic[/]` |
| `HtmlToSpectreMarkup_ITag_RendersItalic` | `"<i>text</i>"` | Contains `[italic]text[/]` |
| `HtmlToSpectreMarkup_CodeTag_RendersDim` | `"<code>x++</code>"` | Contains `[dim]x++[/]` |
| `HtmlToSpectreMarkup_HeadingTag_RendersBoldWithNewline` | `"<h2>Title</h2>"` | Contains `[bold]Title[/]` |
| `HtmlToSpectreMarkup_ListItems_GetBulletPrefix` | `"<ul><li>A</li><li>B</li></ul>"` | Contains `• A` and `• B` |
| `HtmlToSpectreMarkup_BrTag_InsertsNewline` | `"Line1<br>Line2"` | Lines separated by `\n` |
| `HtmlToSpectreMarkup_ParagraphTags_InsertNewlines` | `"<p>First</p><p>Second</p>"` | Multi-line output |
| `HtmlToSpectreMarkup_NestedFormatting_PreservesAll` | `"<b><i>text</i></b>"` | Contains `[bold][italic]text[/][/]` |
| `HtmlToSpectreMarkup_UserBrackets_Escaped` | `"<p>array[0]</p>"` | Contains `array[[0]]` |
| `HtmlToSpectreMarkup_HtmlEntities_DecodedAndEscaped` | `"A &amp; B"` | Contains `A & B` |
| `HtmlToSpectreMarkup_UnknownTags_StrippedCleanly` | `"<span class='x'>text</span>"` | `"text"` (no tags) |
| `HtmlToSpectreMarkup_UnclosedTag_TreatsAsLiteral` | `"Price < 100"` | Contains `Price < 100` (possibly bracket-escaped) |
| `HtmlToSpectreMarkup_CaseInsensitiveTags` | `"<B>bold</B>"` | Contains `[bold]bold[/]` |
| `HtmlToSpectreMarkup_TruncatesAtMaxLines` | 35 `<p>` paragraphs | Contains `(+5 more lines)`, line 31+ absent |
| `HtmlToSpectreMarkup_MixedContent_RealWorldAdoHtml` | Full ADO-style HTML with headings, bold, lists, entities | Contains `[bold]`, `• `, entity values; `new Markup(result)` does not throw (round-trip guard) |
| `HtmlToSpectreMarkup_InputContainsSpectreMarkupSyntax_BracketsEscaped` | `"<p>Use [bold] and [dim] in config</p>"` | Literal `[[bold]]` and `[[dim]]` in output — `Markup.Escape` converts `[` → `[[` so user content is never interpreted as Spectre tags |

#### E1-T3: Wire into `BuildStatusViewAsync`

In `SpectreRenderer.cs`, in the `BuildStatusViewAsync` method's description block (where
`item.Fields.TryGetValue("System.Description", ...)` is checked), replace `HtmlToPlainText`
\+ `Markup.Escape` with `HtmlToSpectreMarkup`:

```csharp
// Before:
var plainText = Formatters.FormatterHelpers.HtmlToPlainText(rawDescription);
if (!string.IsNullOrWhiteSpace(plainText))
    panelContent = new Rows(itemGrid,
        new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim"),
        new Markup(Markup.Escape(plainText)));

// After:
var descriptionMarkup = Formatters.FormatterHelpers.HtmlToSpectreMarkup(rawDescription);
if (!string.IsNullOrWhiteSpace(descriptionMarkup))
    panelContent = new Rows(itemGrid,
        new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim"),
        new Markup(descriptionMarkup));
```

Key change: remove `Markup.Escape()` wrapping — `HtmlToSpectreMarkup` pre-escapes user
content internally. Verify `BuildStatusViewDescriptionTests` still pass — most assertions
use `ShouldContain` on text content (not markup structure) and remain valid unchanged.

**Acceptance Criteria (Epic 1):**
- `HtmlToSpectreMarkup("<b>text</b>")` returns string containing `[bold]text[/]`
- `HtmlToSpectreMarkup("<em>text</em>")` returns string containing `[italic]text[/]`
- `HtmlToSpectreMarkup("<code>x</code>")` returns string containing `[dim]x[/]`
- `HtmlToSpectreMarkup("<h2>Title</h2>")` returns string containing `[bold]Title[/]`
- `HtmlToSpectreMarkup("<li>Item</li>")` returns string containing `• Item`
- `HtmlToSpectreMarkup("<span>text</span>")` returns `"text"` (unknown tags stripped)
- `HtmlToSpectreMarkup("<p>array[0]</p>")` escapes brackets for Spectre safety
- `HtmlToSpectreMarkup(null)` and `HtmlToSpectreMarkup("")` return `string.Empty`
- All existing `HtmlToPlainText` tests continue to pass (unchanged method)
- All existing `BuildStatusViewDescriptionTests` continue to pass
- `BuildStatusViewAsync` uses `HtmlToSpectreMarkup` instead of `HtmlToPlainText`
- `dotnet test` passes across all test projects
- `dotnet build src/Twig/Twig.csproj` succeeds with zero warnings (TreatWarningsAsErrors)

---

### Epic 2: Full-Width Description in RenderWorkItemAsync — Issue #1329

> **Classification:** Deep (2 files, structural refactoring within progressive rendering)
> **Estimated LoC:** ~200
> **ADO Issue:** #1329
> **Predecessor:** Epic 1 (#1328)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| E2-T1 | Refactor `RenderWorkItemAsync` description rendering | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| E2-T2 | Tests for refactored `RenderWorkItemAsync` | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | M |

#### E2-T1: Refactor `RenderWorkItemAsync` Description Rendering

Restructure the `RenderWorkItemAsync` method in `SpectreRenderer.cs` to render descriptions
as a full-width section below the core fields grid, matching the pattern established in
`BuildStatusViewAsync`.

> **Note:** `BuildPanel()` is an **existing local function** inside `RenderWorkItemAsync`
> (defined at the top of the `Live` callback). It currently returns `new Panel(grid)` —
> we modify it to compose `grid + description section` when a description is present.

**Changes:**

1. **Add a section-tracking variable** above the `Live` block:
   ```csharp
   IRenderable? descriptionSection = null;
   ```

2. **Modify the existing `BuildPanel()` local function** to compose grid + description section:
   ```csharp
   // Current (before):
   Panel BuildPanel() => new Panel(grid)
       .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
       .Border(BoxBorder.Rounded)
       .Expand();

   // Proposed (after):
   Panel BuildPanel()
   {
       IRenderable content = descriptionSection is not null
           ? new Rows(grid,
               new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim"),
               descriptionSection)
           : grid;
       return new Panel(content)
           .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
           .Border(BoxBorder.Rounded)
           .Expand();
   }
   ```

3. **Replace the Description grid row** with full-width section:
   ```csharp
   // Before:
   grid.AddRow("[dim]Description:[/]", Markup.Escape(TruncateField(description, 200)));

   // After:
   var descMarkup = Formatters.FormatterHelpers.HtmlToSpectreMarkup(description);
   if (!string.IsNullOrWhiteSpace(descMarkup))
       descriptionSection = new Markup(descMarkup);
   ```

4. **Reorder extended fields**: Move History and Tags processing BEFORE description,
   so they remain in the grid above the full-width description section. The description
   section is appended by `BuildPanel()` below the grid via `Rows`, not as a grid row.

   ```csharp
   // ── Current ordering (before) ────────────────────────────────────
   // Extended field: Description
   if (item.Fields.TryGetValue("System.Description", out var description)
       && !string.IsNullOrWhiteSpace(description))
   {
       grid.AddRow("[dim]Description:[/]", Markup.Escape(TruncateField(description, 200)));
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }

   // Extended field: History (latest comment)
   if (item.Fields.TryGetValue("System.History", out var history)
       && !string.IsNullOrWhiteSpace(history))
   {
       grid.AddRow("[dim]History:[/]", Markup.Escape(TruncateField(history, 200)));
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }

   // Extended field: Tags
   if (item.Fields.TryGetValue("System.Tags", out var tags)
       && !string.IsNullOrWhiteSpace(tags))
   {
       grid.AddRow("[dim]Tags:[/]", Markup.Escape(TruncateField(tags, 200)));
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }

   // ── Proposed ordering (after) ────────────────────────────────────
   // Extended field: History (latest comment) — stays in grid
   if (item.Fields.TryGetValue("System.History", out var history)
       && !string.IsNullOrWhiteSpace(history))
   {
       grid.AddRow("[dim]History:[/]", Markup.Escape(TruncateField(history, 200)));
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }

   // Extended field: Tags — stays in grid
   if (item.Fields.TryGetValue("System.Tags", out var tags)
       && !string.IsNullOrWhiteSpace(tags))
   {
       grid.AddRow("[dim]Tags:[/]", Markup.Escape(TruncateField(tags, 200)));
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }

   // Extended field: Description — full-width section below grid
   if (item.Fields.TryGetValue("System.Description", out var description)
       && !string.IsNullOrWhiteSpace(description))
   {
       var descMarkup = Formatters.FormatterHelpers.HtmlToSpectreMarkup(description);
       if (!string.IsNullOrWhiteSpace(descMarkup))
           descriptionSection = new Markup(descMarkup);
       ctx.UpdateTarget(BuildPanel());
       ctx.Refresh();
   }
   ```

5. **Null/empty guard**: If description is null, empty, or produces empty markup,
   `descriptionSection` stays null and no Rule/section is rendered.

#### E2-T2: Tests for Refactored `RenderWorkItemAsync`

**Existing tests impacted by E2-T1 reordering** — The table below identifies every test in
`RenderWorkItemTests.cs` that asserts on description-related behavior and specifies the
required change:

| Test Method | Current Assertion | Impact | Required Change |
|-------------|-------------------|--------|-----------------|
| `RenderWorkItemAsync_Description_RenderedWhenPresent` | `ShouldContain("Description:")` (colon = grid-row label) | **MUST UPDATE** | Change to `ShouldContain("Description")` (no colon — now a Rule separator title). Keep `ShouldContain("detailed description")` unchanged. |
| `RenderWorkItemAsync_HtmlDescription_StrippedCleanly` | `ShouldContain("Hello world")`, `ShouldNotContain("<div>")`, `ShouldNotContain("<b>")` | **PASSES AS-IS** | No change needed. Text content assertions are label-independent. `<b>` becomes `[bold]` in markup, which Spectre's TestConsole consumes — neither HTML tags nor Spectre tags appear in rendered output. |
| `RenderWorkItemAsync_NoExtendedFields_OnlyCoreFieldsShown` | `ShouldNotContain("Description:")` | **SHOULD UPDATE** | Change to `ShouldNotContain("Description")` (without colon) for robustness — the Rule title `"Description"` would otherwise pass the old assertion vacuously. |
| `RenderWorkItemAsync_WhitespaceOnlyDescription_NotShown` | `ShouldNotContain("Description:")` | **SHOULD UPDATE** | Change to `ShouldNotContain("Description")` (without colon) for same reason as above. |
| `RenderWorkItemAsync_History_RenderedWhenPresent` | `ShouldContain("History:")` and text | **PASSES AS-IS** | No change. History stays in grid as a labeled row. |
| `RenderWorkItemAsync_Tags_RenderedWhenPresent` | `ShouldContain("Tags:")` and text | **PASSES AS-IS** | No change. Tags stay in grid as a labeled row. |
| `RenderWorkItemAsync_CoreFields_RenderedImmediately` | `ShouldContain("#42")`, title, state | **PASSES AS-IS** | No change. Core fields assertions are content-based, not position-dependent. |
| `RenderWorkItemAsync_LongTags_Truncated` | `ShouldContain("Tags:")`, `ShouldContain("…")` | **PASSES AS-IS** | No change. Tags still use `TruncateField`. |
| `RenderWorkItemAsync_CancelledToken_ThrowsOperationCanceledException` | Throws on pre-cancelled token | **PASSES AS-IS** | No change. Cancellation check happens before extended fields. |
| `TruncateField_*` (3 tests) | Direct method unit tests | **PASSES AS-IS** | No change. `TruncateField` is still used for History and Tags. |
| `StripHtmlTags_*` (8 tests) | Direct method unit tests | **PASSES AS-IS** | No change. `StripHtmlTags` is still used internally by `TruncateField`. |

**New tests to add** in `RenderWorkItemTests.cs`:

| Test | Assertion |
|------|-----------|
| `RenderWorkItemAsync_Description_RenderedFullWidth` | Output contains "Description" rule separator and full description text (not truncated to 200 chars); a 250-char description appears in full |
| `RenderWorkItemAsync_HtmlDescription_RichFormatting` | HTML with `<b>`, `<em>`, `<code>` produces rendered text content (no raw HTML tags); validates end-to-end Spectre rendering path |
| `RenderWorkItemAsync_NoDescription_NoDescriptionSection` | Output does not contain "Description" when description field is absent |
| `RenderWorkItemAsync_EmptyDescription_NoDescriptionSection` | Output does not contain "Description" when description is whitespace-only |
| `RenderWorkItemAsync_HistoryAndTags_StillRendered` | When all three extended fields present, History and Tags appear with their `":"` labels and description appears with Rule separator |
| `RenderWorkItemAsync_LongDescription_TruncatedWithIndicator` | 35-paragraph HTML description shows `"(+"` and `"more lines)"` truncation marker |

**Acceptance Criteria (Epic 2):**
- Description renders full-width below the core fields grid in `twig show` output
- HTML is converted to Spectre markup (bold, italic, dim code, bullet lists)
- Description is no longer truncated to 200 characters (uses 30-line limit from `HtmlToSpectreMarkup`)
- Null/empty descriptions produce no section (no empty Rule separator)
- History and Tags fields still render correctly in grid rows
- Progressive rendering (Live context) still works — core fields appear first
- All existing passing tests continue to pass
- `dotnet test` passes across all test projects
- `dotnet build src/Twig/Twig.csproj` succeeds with zero warnings

---

## Execution Order

```
#1327 (MaxDescriptionLines 15 → 30)  ← Done
    │
    └──► Epic 1 (#1328 — HtmlToSpectreMarkup converter + BuildStatusViewAsync wiring)
              │
              └──► Epic 2 (#1329 — RenderWorkItemAsync full-width section)
```

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Spectre markup injection via malformed user content | Low | High | `Markup.Escape()` applied to all user text in the text buffer flush. Unit tests verify bracket escaping. Dedicated test `HtmlToSpectreMarkup_InputContainsSpectreMarkupSyntax_BracketsEscaped` guards against `[bold]`-like content in user text. |
| Invalid Spectre markup output | Low | High | Round-trip integration test `HtmlToSpectreMarkup_ValidOutput_SpectreMarkupParsesWithoutError` verifies that `new Markup(result)` does not throw for real-world ADO HTML input. |
| Progressive rendering regression in `RenderWorkItemAsync` | Medium | Medium | The existing `BuildPanel()` local function is modified — not replaced — to conditionally compose `Rows(grid, Rule, Markup)`. All 26 existing `RenderWorkItemTests` must pass. New tests verify the full-width layout. |
| Spectre `[/]` handling of unmatched closing tags | Low | Low | Spectre silently ignores extra `[/]` closers. Unmatched opening tags apply style to remaining content — acceptable per DD-05. |
| `NormalizeLines` trimming removes intentional indentation | Medium | Low | `NormalizeLines` calls `raw.Trim()` on each line, which strips leading whitespace. This is acceptable for the current flat-list design (`• ` prefix requires no indentation). **If nested list indentation is added in the future** (open question #2), `NormalizeLines` would need a variant that preserves leading whitespace or the indentation would need to be encoded as non-whitespace characters (e.g., Unicode box-drawing). This is explicitly out of scope for this plan. |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `<a href="...">` links show the URL in the terminal output (e.g., `text (url)`)? | Low | Current design strips `<a>` tags, showing only the link text. URL display could be added later without breaking changes. |
| 2 | Should nested `<ul>` lists have increased indentation (e.g., `  • ` vs `    • `)? | Low | Current design treats all `<li>` at the same level. Nesting awareness would require a depth counter. Defer to follow-on. |
| 3 | Should `<table>` content be rendered as a Spectre `Table` widget? | Low | Tables are rare in ADO descriptions. Current design strips `<table>` tags, showing cell content as flat text. A future enhancement could detect tables and render them with `Spectre.Console.Table`. |
