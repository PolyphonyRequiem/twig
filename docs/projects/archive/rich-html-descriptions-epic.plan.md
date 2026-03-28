# Plan: Support Rich HTML Descriptions for ADO Work Items

> **Date**: 2026-03-28
> **Status**: Draft (v3 — revised per v2 tech/readability review)
> **ADO Epic**: #1281
> **Supersedes**: `rich-html-descriptions.plan.md`, `html-description-rendering.plan.md`
> **Review scores (v2)**: Tech 91/100, Readability 88/100 — all feedback addressed below

---

## Executive Summary

Azure DevOps work item descriptions (`System.Description`) and history comments
(`System.History`) are HTML-typed, yet twig currently strips all HTML tags via a naive
`StripHtmlTags()` character-walker — destroying headings, lists, bold/italic, and leaving
HTML entities like `&amp;` un-decoded. On the input side, users must hand-craft HTML or
accept unstructured plain text in ADO's web UI.

This plan delivers end-to-end rich HTML description support:

1. **Read path** — A two-tier HTML rendering pipeline: `HtmlConverter` (plain-text
   extraction with entity decoding, shared across CLI/TUI/formatters) and
   `HtmlSpectreConverter` (rich terminal rendering with bold, italic, headings, lists,
   code blocks, and hyperlinks for the CLI detail view).

2. **Consolidation** — Eliminates duplicated `StripHtmlTags()` implementations across
   `SpectreRenderer` and `WorkItemFormView`, and decouples `FormatterHelpers` from
   `SpectreRenderer`.

The work is organized into 3 Epics (PR groups), totaling ~1100 LoC across 9 unique files
(5 source + 4 test), with 11 total file touches across epics (SpectreRenderer.cs and
RenderWorkItemTests.cs are modified in both Epic 2 and Epic 3).
Epic 1 is the foundation; Epic 2 follows, then Epic 3 follows Epic 2 to avoid merge
conflicts on `SpectreRenderer.cs`.

---

## Background

### Current State

- ADO's `System.Description` field has data type `"html"`. Content is HTML from ADO's
  WYSIWYG editor (e.g., `<p>Fix the <strong>auth</strong> bug</p><ul><li>Step 1</li></ul>`).
- ADO's `System.History` field is also HTML-typed — comment content from ADO's editor
  contains the same HTML formatting tags.
- **SpectreRenderer** (CLI): `TruncateField(description, 200)` calls `StripHtmlTags()`,
  then `Markup.Escape()`. All formatting is lost; output is a 200-char truncated plain string.
  The same `TruncateField()` is applied to `System.History` (L617) and `System.Tags` (L626).
- **WorkItemFormView** (TUI): Duplicates the same `StripHtmlTags()` method (~40 lines
  copy-pasted from SpectreRenderer, with comment "Duplicated from SpectreRenderer.StripHtmlTags()").
- **FormatterHelpers**: `FormatFieldValue()` (L32) and `FormatFieldValueForJson()` (L49)
  call `SpectreRenderer.StripHtmlTags()` for `html`-typed fields — tight cross-project
  coupling between formatter and renderer.
- **HTML entity gap**: No `HtmlDecode` / `WebUtility.HtmlDecode` usage anywhere.
  `&amp;`, `&lt;`, `&nbsp;`, `&#NNN;` pass through to terminal output un-decoded.
- **Write path**: `twig update System.Description "value"` sends the value as-is via
  `AdoRestClient.PatchAsync()`. HTML pass-through already works (sending `<h1>Hello</h1>`
  renders correctly in ADO web UI), but there is no Markdown→HTML conversion.
- **Field storage**: Descriptions are stored in `fields_json TEXT` column in SQLite as
  part of the serialized field dictionary. No separate column.

### All System.Description rendering paths (grep audit)

| File | Line | Usage | Notes |
|------|------|-------|-------|
| `SpectreRenderer.cs` | 605-611 | `RenderWorkItemAsync` detail view — `TruncateField(description, 200)` | Primary rich rendering target |
| `SpectreRenderer.cs` | 662-704 | `AddExtendedFieldRows` via `FormatterHelpers.FormatFieldValue()` — renders `html` data-type fields in status field grid | Affected via FormatterHelpers `html` branch |
| `HumanOutputFormatter.cs` | 1460, 1479 | `FormatFieldValue()` for non-live human output | Affected via FormatterHelpers `html` branch |
| `JsonOutputFormatter.cs` | 641 | `FormatFieldValueForJson()` for JSON output | Affected via FormatterHelpers `html` branch |
| `WorkItemFormView.cs` | 183 | TUI form — `StripHtmlTags(GetFieldExact(item, "System.Description"))` | Direct StripHtmlTags call |
| `SeedEditorFormat.cs` | 56 | Seed editor — raw field value written to editor buffer | Not HTML-processed (intentional: editor handles raw values) |
| `NewCommand.cs` | 96 | `seed.SetField("System.Description", description)` — write path | Not HTML-processed (write, not read) |
| `FieldImportFilter.cs` | 29 | Import allowlist — includes `System.Description` | Metadata only, no rendering |

### All System.History rendering paths (grep audit)

| File | Line | Usage | Notes |
|------|------|-------|-------|
| `SpectreRenderer.cs` | 613-620 | `RenderWorkItemAsync` — `TruncateField(history, 200)` (comment at L613, `if` block L614-620) | HTML-typed, same truncation as description |

### Architecture Context

```
Twig.Domain          → Aggregates, Services, ValueObjects (no external deps)
Twig.Infrastructure  → ADO REST, SQLite, Config, Auth (external deps)
Twig (CLI)           → Commands, Rendering (Spectre.Console), Formatters
Twig.Tui             → Terminal.Gui views (not AOT)
```

Key constraints (from `Twig.csproj` L7, L9, L12 unless noted):
- `PublishAot=true` (Twig.csproj L7), `TrimMode=full` (L9), `InvariantGlobalization=true` (L12)
- `JsonSerializerIsReflectionEnabledByDefault=false` (Twig.csproj L11, source-gen only)
- `TreatWarningsAsErrors=true` (Directory.Build.props L8)
- Target framework: `net10.0` (Directory.Build.props L4)

### What This Solves

| Before | After |
|--------|-------|
| `Fix the auth bug Step 1 Step 2` (run-on text) | **Fix the auth bug** • Step 1 • Step 2 |
| `Meeting &amp; notes` (un-decoded entities) | `Meeting & notes` |
| Description truncated to 200 chars (detail view) | Detail view shows full rich description |
| History shows `<p>Updated the &lt;config&gt;</p>` | History shows `Updated the <config>` (plain text, decoded) |
| No heading/list/code structure visible | Headings bold+underlined, lists bulleted, code dimmed |
| Duplicated `StripHtmlTags` in 2 projects | Single `HtmlConverter` in shared Domain.Common |

---

## Problem Statement

ADO work item descriptions and history comments contain structured HTML that conveys meaning
through headings, lists, emphasis, code blocks, and links. The current `StripHtmlTags()`
approach destroys this structure, producing unintelligible run-on text with un-decoded
entities. Users must open ADO's web UI to read properly formatted descriptions — defeating
the purpose of a CLI triage tool.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | Rich terminal rendering of HTML descriptions via Spectre.Console (bold, italic, headings, lists, code, links) |
| G2 | HTML entity decoding across all display paths (`&amp;` → `&`, `&lt;` → `<`, `&nbsp;` → ` `, numeric entities) — includes System.History |
| G3 | Consolidate duplicated `StripHtmlTags` into a single shared `HtmlConverter` in `Twig.Domain.Common` |
| G4 | Full (non-truncated) description display in work item detail view |
| G5 | AOT-safe implementation (no reflection, no trim warnings) |
| G6 | Graceful degradation on malformed HTML — never crash, fall back to plain text |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Full CSS/style attribute parsing (ADO inline styles ignored beyond tag semantics) |
| NG2 | Image rendering (terminal limitation; `<img>` alt text shown as placeholder) |
| NG3 | JavaScript or embedded content execution |
| NG4 | Pixel-perfect ADO web UI reproduction |
| NG5 | TUI rich rendering (Terminal.Gui has its own markup system; future scope) |
| NG6 | Embedded Mermaid diagram rendering (ADO limitation) |
| NG7 | `--format` on `twig edit` or `twig seed edit` (editor workflow handles raw field values) |
| NG8 | Sanitizing user-provided HTML for ADO (ADO handles its own sanitization) |
| NG9 | Rich rendering of System.History (truncated plain-text with entity decoding is sufficient for latest-comment preview) |

---

## Requirements

### Functional Requirements

| ID | Requirement | Epic |
|----|-------------|------|
| FR-01 | `<b>` / `<strong>` text renders as **bold** in terminal | 2 |
| FR-02 | `<i>` / `<em>` text renders as *italic* in terminal | 2 |
| FR-03 | `<u>` text renders as underlined in terminal | 2 |
| FR-04 | `<s>` / `<strike>` / `<del>` text renders as ~~strikethrough~~ in terminal | 2 |
| FR-05 | `<h1>`–`<h6>` render as bold (h1/h2 underlined) with blank line separation | 2 |
| FR-06 | `<p>` content separated by blank lines | 2 |
| FR-07 | `<br>` / `<br/>` produce line breaks | 1, 2 |
| FR-08 | `<ul><li>` renders as `  • item` with indentation | 1, 2 |
| FR-09 | `<ol><li>` renders as `  1. item` with numbering | 1, 2 |
| FR-10 | `<code>` (inline) renders as dimmed/highlighted text | 2 |
| FR-11 | `<pre>` / `<pre><code>` renders as indented block with dim styling | 2 |
| FR-12 | `<a href="url">text</a>` renders as Spectre hyperlink | 2 |
| FR-13 | HTML entities decoded across all display paths (Description, History, any `html`-typed field) | 1, 3 |
| FR-14 | Plain text input (no HTML) passes through unchanged | 1 |
| FR-15 | Malformed HTML degrades gracefully to plain text extraction | 1, 2 |
| FR-16 | `<table>` rows rendered as plain text with ` \| ` cell separators | 2 |

### Non-Functional Requirements

| ID | Requirement | Rationale |
|----|-------------|-----------|
| NFR-01 | Zero trim/AOT warnings under `PublishAot=true` + `TreatWarningsAsErrors=true` | Build gate |
| NFR-02 | No new serializable types (no `TwigJsonContext` changes) | No JSON serialization involved |
| NFR-03 | No regex in custom tokenizer code | Complexity concern: the character-walking tokenizer is simpler and more debuggable than regex for nested HTML state tracking. Note: .NET 8+ `GeneratedRegex` is AOT-safe, so this is a design preference for maintainability, not an AOT limitation. |
| NFR-04 | No reflection usage | AOT constraint |
| NFR-05 | `HtmlConverter` in `Twig.Domain.Common` has zero Spectre.Console dependency | Domain layer purity |
| NFR-06 | Performance: convert a 10 KB HTML description in < 1 ms | User-perceptible latency budget |
| NFR-07 | Follows existing conventions: `sealed` classes, primary constructors, `TreatWarningsAsErrors` | Codebase consistency |

---

## Proposed Design

### Architecture Overview

```
                                          Read Path
                              ┌──────────────────────────────┐
                              │ HTML from ADO                │
                              │ (System.Description,          │
                              │  System.History, any "html")  │
                              └──────────┬───────────────────┘
                                          │
                              ┌──────────▼───────────────────┐
                              │ Twig.Domain.Common           │
                              │ ┌────────────────────────┐   │
                              │ │ HtmlConverter (static)  │   │
                              │ │  .ToPlainText(html)     │   │
                              │ │  (entity decode +       │   │
                              │ │   structural whitespace)│   │
                              │ └────────────────────────┘   │
                              └──────────┬───────────────────┘
                                          │
             ┌────────────────────────────┼────────────────────┐
    ┌────────▼──────────┐    ┌────────────▼───┐   ┌────────────▼──────────┐
    │ Twig (CLI)        │    │ Twig.Tui       │   │ Formatters            │
    │ Rendering/        │    │ Views/         │   │ FormatterHelpers      │
    │ HtmlSpectreConv.  │    │ ToPlainText()  │   │ ToPlainText()         │
    │ .ToRenderable()   │    └────────────────┘   └───────────────────────┘
    │                   │
    │ SpectreRenderer   │
    │  Description →    │
    │    rich Panel     │
    │  History/Tags →   │
    │    truncated      │
    │    plain text     │
    │    (entity decode)│
    └───────────────────┘
```

### Key Design Decisions

| ID | Decision | Rationale | Alternative Considered |
|----|----------|-----------|----------------------|
| DD-01 | Custom character-walking tokenizer for read path (no HTML parser library) | Zero dependencies, ADO HTML is well-structured WYSIWYG output. ~300 LoC vs adding HtmlAgilityPack (uncertain AOT compat). | HtmlAgilityPack / AngleSharp — heavy, AOT compatibility uncertain, likely trim warnings |
| DD-02 | `HtmlConverter` in `Twig.Domain.Common` (plain text) | Shared by CLI, TUI, and formatters without Spectre dependency; consolidates duplicated `StripHtmlTags` | Keep in SpectreRenderer — perpetuates TUI duplication and cross-project coupling |
| DD-03 | `HtmlSpectreConverter` in `Twig\Rendering` (rich) | Spectre-specific; only CLI needs rich rendering; same project as SpectreRenderer | Domain layer — would add Spectre.Console dependency to Domain |
| DD-04 | Static utility classes, not DI services | Pure stateless functions; no side effects; testable via direct input/output | Interface + DI — over-engineered for pure functions |
| DD-05 | Produce `IRenderable` for block-level description display | Allows mixing `Markup`, `Panel`, and `Rows` for proper block structure. Compatible with `LiveDisplayContext.UpdateTarget()` — same pattern used extensively in `RenderWithSyncAsync` (L969-1024). | Single markup string — can't embed Spectre `Table` or `Panel` in a markup string |
| DD-06 | Entity decoding via `System.Net.WebUtility.HtmlDecode` | BCL class in `System.Net.Primitives` assembly, included in the `net10.0` shared framework by default — no additional PackageReference needed in `Twig.Domain.csproj`. Trim-safe, handles full entity set (named, decimal, hex). Verified: `Twig.Domain.csproj` has no PackageReferences today and `System.Net.WebUtility` is available via the implicit framework reference. | Custom lookup table — ~100 extra LoC with same capability |
| DD-07 | Full description in detail view, truncated in status view | Detail view (invoked by `twig status` when an active item is set, rendered via `RenderWorkItemAsync`) is for inspection; workspace views are for overview/glancing | Always truncate — loses the primary value of rich rendering |
| DD-08 | Graceful fallback: on any parse error, return `new Markup(Markup.Escape(HtmlConverter.ToPlainText(html)))` | Never crash the CLI due to unexpected HTML; plain text is always safe. Catch `Exception` (broad) because Spectre's `Markup` constructor can throw `InvalidOperationException` for malformed markup strings, and our own tokenizer could throw unexpected exceptions on exotic input. Re-throw `OperationCanceledException` to preserve cancellation semantics. | Throw exception — would break commands on malformed input |
| DD-09 | `TruncateField` retained in `SpectreRenderer` for History and Tags, refactored to call `HtmlConverter.ToPlainText()` | History (L617) and Tags (L626) still need truncation. Only the Description path switches to rich rendering. TruncateField stays as a thin wrapper: `HtmlConverter.ToPlainText(value).Trim()` → truncate. `StripHtmlTags()` is removed; `TruncateField()` is preserved. | Move TruncateField to HtmlConverter — but truncation is a display concern, not a domain concern. Tags aren't even HTML. |
| DD-10 | `Panel(Rows(coreGrid, descriptionPanel))` as Live target | The `LiveDisplayContext.UpdateTarget()` method accepts any `IRenderable`. The existing codebase already uses `Rows(...)` as a Live target extensively (L969, L977, L990, L1006, L1015, L1021). Wrapping `Rows(coreGrid, descriptionPanel)` inside the existing `BuildPanel()` Panel keeps the same outer structure — the only change is the Panel content goes from a single `Grid` to a `Rows(grid, descPanel)`. | Keep description as grid row — can't embed `Panel`-in-`Panel` or rich `IRenderable` in a grid cell string |

### HTML Tag → Spectre.Console Mapping

| HTML Element | Spectre Output | Type |
|-------------|----------------|------|
| `<b>`, `<strong>` | `[bold]...[/]` | Inline |
| `<i>`, `<em>` | `[italic]...[/]` | Inline |
| `<u>` | `[underline]...[/]` | Inline |
| `<s>`, `<strike>`, `<del>` | `[strikethrough]...[/]` | Inline |
| `<code>` (inline) | `[grey on grey15]...[/]` | Inline |
| `<a href="url">` | `[blue underline link=url]...[/]` | Inline |
| `<h1>`, `<h2>` | `[bold underline]TEXT[/]` + blank line | Block |
| `<h3>`–`<h6>` | `[bold]text[/]` + blank line | Block |
| `<p>` | content + blank line after | Block |
| `<br>`, `<br/>` | newline | Block |
| `<ul>` | container; resets list counter | Block |
| `<ol>` | container; initializes counter at 1 | Block |
| `<li>` (in `<ul>`) | `  • content` | Block |
| `<li>` (in `<ol>`) | `  N. content` (N incremented) | Block |
| `<pre>`, `<pre><code>` | `Panel` with `BoxBorder.Square`, dim text | Block |
| `<table>` | row per `<tr>`, cells joined with ` \| ` | Block |
| `<img alt="text">` | `[dim][image: text][/]` | Inline |
| `<div>`, `<span>` | pass through content | Container |
| `<style>`, `<script>` | content suppressed (tag and body discarded) | Void |

### Rendering Flow (Detail View)

The detail view is rendered by `RenderWorkItemAsync` (SpectreRenderer.cs L569-633), which
is invoked by the `status` command when an active item is set. This is a `Live()` context
with progressive field loading.

**Current flow** (L589-630):
```
Live(Loading...) → ctx.UpdateTarget(Panel(grid))
  grid.AddRow("Description:", TruncateField(desc, 200))  // L608
  grid.AddRow("History:", TruncateField(history, 200))    // L617
  grid.AddRow("Tags:", TruncateField(tags, 200))          // L626
  ctx.UpdateTarget(Panel(grid))  // after each row
```

**New flow** (Epic 2 T5):
```
Live(Loading...) → ctx.UpdateTarget(BuildPanel())
  // Description: rich Panel below core grid
  if (description is present):
    renderable = HtmlSpectreConverter.ToRenderable(description)
    descPanel = Panel(renderable).Header("[dim]Description[/]").Border(Rounded).Expand()
    ctx.UpdateTarget(BuildPanel(descPanel))  // Panel wraps Rows(grid, descPanel)
    ctx.Refresh()

  // History: truncated plain text WITH entity decoding (via TruncateField → HtmlConverter.ToPlainText)
  if (history is present):
    grid.AddRow("[dim]History:[/]", Markup.Escape(TruncateField(history, 200)))
    ctx.UpdateTarget(BuildPanel(descPanel))
    ctx.Refresh()

  // Tags: truncated plain text (not HTML, no entity decoding needed)
  if (tags is present):
    grid.AddRow("[dim]Tags:[/]", Markup.Escape(TruncateField(tags, 200)))
    ctx.UpdateTarget(BuildPanel(descPanel))
    ctx.Refresh()
```

The `BuildPanel()` local function (currently L592-595) is modified to accept an optional
`IRenderable? descriptionPanel` parameter:
```csharp
Panel BuildPanel(IRenderable? descriptionContent = null)
{
    IRenderable content = descriptionContent is not null
        ? new Rows(grid, descriptionContent)
        : grid;
    return new Panel(content)
        .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
        .Border(BoxBorder.Rounded)
        .Expand();
}
```

This preserves the single `Panel` as the `LiveDisplayContext` target — the outer Panel
structure is unchanged. Only the Panel's *content* changes from a bare `Grid` to
`Rows(grid, descPanel)`. This is the same pattern used throughout SpectreRenderer (e.g.,
L1529: `Panel(new Rows(rows))`, L1644: `Panel(new Rows(rows))`).

---

## Implementation Plan

### Epic 1: HTML Processing Foundation in Domain.Common

**Classification**: Deep (few files, complex parsing logic)
**Estimated LoC**: ~380 new, ~0 modified = **~380 LoC**
**Predecessor**: None
**PR scope**: 2 new files

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | Create `HtmlConverter` static class with `ToPlainText(string? html)` method. Implements character-by-character HTML tokenization, tag-aware whitespace normalization, block element separation (paragraphs/headings → blank lines; list items → bullets; `<br>` → newlines), and HTML entity decoding via `System.Net.WebUtility.HtmlDecode`. Handles unclosed tags gracefully. Suppresses `<style>` and `<script>` tag content. Returns empty string for null/whitespace input. | `src/Twig.Domain/Common/HtmlConverter.cs` (new) | L |
| T2 | Comprehensive unit tests for `HtmlConverter.ToPlainText`. Tests must specifically demonstrate behavioral differences from the old `StripHtmlTags()` to justify the new implementation — see acceptance criteria below. Categories: plain text passthrough, basic tag stripping, paragraph separation with blank lines (old: run-on), heading + blank lines (old: no separation), ordered/unordered list rendering with bullets (old: no bullets), nested lists, `<br>` → newline (old: `<br>` removed silently), inline code, `<pre>` block preservation, entity decoding (named: `&amp;`/`&lt;`/`&nbsp;`, decimal `&#8212;`, hex `&#x2014;` — old: passed through literally), unclosed tags, empty/null input, whitespace-only HTML, `<img>` alt text, deeply nested tags, mixed content, `<style>`/`<script>` suppression. | `tests/Twig.Domain.Tests/Common/HtmlConverterTests.cs` (new) | L |

**Acceptance Criteria** (each demonstrates behavior old `StripHtmlTags` cannot produce):

| Input | HtmlConverter.ToPlainText Output | Old StripHtmlTags Output | Why Different |
|-------|----------------------------------|-------------------------|---------------|
| `"<p>Para 1</p><p>Para 2</p>"` | `"Para 1\n\nPara 2"` | `"Para 1Para 2"` | Paragraph separation |
| `"<ul><li>A</li><li>B</li></ul>"` | `"  • A\n  • B"` | `"AB"` | List bullet rendering |
| `"Line 1<br/>Line 2"` | `"Line 1\nLine 2"` | `"Line 1Line 2"` | `<br>` → newline |
| `"Meeting &amp; notes &#8212; draft"` | `"Meeting & notes — draft"` | `"Meeting &amp; notes &#8212; draft"` | Entity decoding |
| `"<h2>Title</h2><p>Body</p>"` | `"Title\n\nBody"` | `"TitleBody"` | Heading separation |
| `"Price: 5 &lt; 10 &amp; valid"` | `"Price: 5 < 10 & valid"` | `"Price: 5 &lt; 10 &amp; valid"` | Named entity decoding |
| `"<style>.red{color:red}</style>Visible"` | `"Visible"` | `".red{color:red}Visible"` | Style content suppression |
| `"plain text no html"` | `"plain text no html"` | `"plain text no html"` | Passthrough (same) |
| `"<div><p>unclosed"` | `"unclosed"` (graceful) | `"unclosed"` | Graceful (same) |

- All new tests pass; `dotnet build` succeeds with zero warnings
- Performance test validates 10 KB HTML conversion completes within 50 ms (NFR-06 guard)
- No changes to `Twig.Domain.csproj` required (`System.Net.WebUtility` is available via implicit framework reference in `net10.0`)

---

### Epic 2: Spectre.Console Rich HTML Rendering

**Classification**: Deep (few files, complex rendering logic)
**Estimated LoC**: ~480 new, ~50 modified = **~530 LoC**
**Predecessor**: Epic 1
**Successor**: Epic 3 (Epic 3 must follow Epic 2 to avoid merge conflicts on SpectreRenderer.cs)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T3 | Create `HtmlSpectreConverter` static class with `ToRenderable(string? html)` returning `IRenderable`. Implements the HTML tag → Spectre mapping table. Inline elements produce markup annotations (`[bold]`, `[italic]`, etc.). Block elements produce separate `IRenderable` items composed via `Rows`. `<pre>` blocks produce `Panel` with `BoxBorder.Square`. `<table>` produces plain-text rows with ` \| ` cell separators. Wraps entire conversion in try/catch: catch `OperationCanceledException` and re-throw; catch `Exception` (broad — covers `InvalidOperationException` from Spectre `Markup` constructor on malformed markup, plus any unexpected tokenizer failures) and fall back to `new Markup(Markup.Escape(HtmlConverter.ToPlainText(html)))`. Returns `Text.Empty` for null/whitespace input. | `src/Twig/Rendering/HtmlSpectreConverter.cs` (new) | XL |
| T4 | Comprehensive tests for `HtmlSpectreConverter` using `TestConsole` to extract rendered output. Test cases: bold/italic/underline markup output, heading rendering, paragraph separation, list bullet rendering, numbered lists, nested lists, code inline, code block in Panel, hyperlink markup, table plain-text rendering, entity decoding in markup, mixed block+inline, `<hr>` as Rule, `<img>` alt text, plain text passthrough, malformed HTML fallback (verify no exception + plain text returned), empty/null input. | `tests/Twig.Cli.Tests/Rendering/HtmlSpectreConverterTests.cs` (new) | XL |
| T5 | Update `SpectreRenderer.RenderWorkItemAsync()` (L569-633) to use `HtmlSpectreConverter.ToRenderable()` for the description field. Changes: (1) Modify `BuildPanel()` local function (L592-595) to accept optional `IRenderable? descriptionContent` parameter — when non-null, Panel content becomes `Rows(grid, descriptionContent)` instead of bare `grid`. (2) Replace description grid row (L604-611) with rich Panel: `var descPanel = new Panel(HtmlSpectreConverter.ToRenderable(description)).Header("[dim]Description[/]").Border(BoxBorder.Rounded).Expand()`, then `ctx.UpdateTarget(BuildPanel(descPanel))`. (3) History (L613-620) and Tags (L622-629) remain as truncated grid rows but now call `TruncateField` which internally uses `HtmlConverter.ToPlainText()` (entity decoding gained). (4) Refactor `TruncateField` (L711-718) to call `HtmlConverter.ToPlainText()` instead of `StripHtmlTags()`. **Note**: This is the riskiest change — explicitly verify via test that `Panel(Rows(grid, descPanel))` works correctly as a `LiveDisplayContext.UpdateTarget()` target. | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T6 | Update existing `RenderWorkItemTests` to verify: (a) HTML description renders with rich formatting (not truncated), (b) description renders in a Panel with "Description" header, (c) entities decoded in rendered output, (d) History still renders as truncated plain text with entity decoding, (e) existing tests still pass. Add dedicated test: `RenderWorkItemAsync_RichDescriptionInLiveContext_PanelRendersCorrectly` — verifies `Panel(Rows(grid, descPanel))` produces valid output via `TestConsole`. **TestConsole + Live() compatibility**: Existing tests (e.g., `RenderWorkItemAsync_CoreFields_RenderedImmediately`) already exercise the `_console.Live().StartAsync()` path through `TestConsole` — `TestConsole` implements `IAnsiConsole` and supports `Live()` regions (the extension method works on any `IAnsiConsole`). The T6 Live context test follows the same proven pattern. | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | M |

**Acceptance Criteria**:
- Work item detail view (rendered by `RenderWorkItemAsync`, invoked via `twig status` when an active item is set) shows rich description with bold headings, bulleted lists, italic text in a bordered Panel
- Description Panel renders below core fields grid, inside the outer work-item Panel
- History field still renders as truncated plain text in the core grid, but with HTML entities decoded (e.g., `&amp;` → `&`)
- Malformed HTML gracefully falls back to plain text (no crash, no raw HTML shown)
- All HTML entities decoded in rendered output
- Dedicated test verifies `Panel(Rows(grid, innerPanel))` is compatible with `LiveDisplayContext.UpdateTarget()` (via `TestConsole`)
- All existing `RenderWorkItemTests` continue to pass
- `dotnet build` succeeds with zero warnings

---

### Epic 3: Codebase Consolidation and Cleanup

**Classification**: Wide (many files, mechanical replacements)
**Estimated LoC**: ~30 new, ~160 modified/removed = **~190 LoC**
**Predecessor**: Epic 2 (sequenced after Epic 2 to avoid merge conflicts on SpectreRenderer.cs — both epics modify the same file region L604-764)

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T7 | Replace all `StripHtmlTags()` usages: (1) Update `FormatterHelpers.FormatFieldValue()` (L32) and `FormatFieldValueForJson()` (L49) to call `HtmlConverter.ToPlainText()` instead of `Rendering.SpectreRenderer.StripHtmlTags()` — this decouples `FormatterHelpers` from `SpectreRenderer` and adds entity decoding to all formatter paths. (2) Remove the now-unused `StripHtmlTags()` method (L725-764) from `SpectreRenderer`. Note: `TruncateField` is retained (already refactored in Epic 2 T5 to use `HtmlConverter.ToPlainText()`). **Contingency**: Before removing `StripHtmlTags()`, run a final project-wide grep for `StripHtmlTags` to catch any usages added between Epic 1 merge and Epic 3 implementation. If unexpected usages are found, update them to `HtmlConverter.ToPlainText()` and document in the PR. | `src/Twig/Rendering/SpectreRenderer.cs`, `src/Twig/Formatters/FormatterHelpers.cs` | M |
| T8 | Replace duplicated `StripHtmlTags()` in `WorkItemFormView` (L305-345) with `HtmlConverter.ToPlainText()`. Update the call site (L183) and remove the ~40-line duplicate method. | `src/Twig.Tui/Views/WorkItemFormView.cs` | S |
| T9 | Update affected tests: (1) Remove `StripHtmlTags` unit tests from `RenderWorkItemTests.cs` (L197-244) — these are now covered by `HtmlConverterTests`. (2) Update `WorkItemFormViewTests` to verify HTML stripping still works via `HtmlConverter`. (3) Run `dotnet test` across all test projects to verify no regressions. | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs`, `tests/Twig.Tui.Tests/WorkItemFormViewTests.cs` | M |

**Acceptance Criteria**:
- Zero references to `StripHtmlTags` remain anywhere in the codebase (verified by grep)
- `StripHtmlTags()` method removed from both `SpectreRenderer` and `WorkItemFormView`
- `TruncateField()` retained in `SpectreRenderer`, now calls `HtmlConverter.ToPlainText()`
- `FormatterHelpers` no longer references `Rendering.SpectreRenderer` (coupling removed)
- HTML entities decoded consistently across all display paths (CLI detail, CLI status grid, Human formatter, JSON formatter, TUI)
- All existing tests pass; `dotnet build` succeeds with zero warnings across all projects

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Custom HTML tokenizer doesn't handle ADO's specific HTML quirks | Medium | Medium | T2/T4 include tests with real ADO HTML samples. Fallback to plain text on any parse error (DD-08). Collect sample HTML from actual ADO descriptions during development. |
| Spectre.Console markup injection from user HTML content | Low | High | All text content is escaped via `Markup.Escape()` before embedding in markup. Only structurally-derived markup (bold, italic tags) produces Spectre annotations. Link URLs sanitized. |
| Terminal compatibility — some terminals don't support hyperlinks, strikethrough, or italic | Medium | Low | Spectre.Console features degrade gracefully. Hyperlinks show as plain text in unsupported terminals. No functional impact. |
| `InvariantGlobalization=true` affects entity decoding of non-ASCII characters | Low | Low | `WebUtility.HtmlDecode` maps to `char` values directly. `InvariantGlobalization` affects culture-sensitive operations, not character encoding. Verify with tests for `&#8212;`, `&#8217;`. |
| Breaking change to `FormatterHelpers.FormatFieldValueForJson` output format | Low | Medium | JSON consumers may depend on exact string format. Entity decoding is objectively correct (e.g., `&amp;` → `&`). Document as improvement. |
| `Panel(Rows(grid, descPanel))` incompatible with LiveDisplayContext | Low | High | The codebase already uses `Rows(...)` as Live targets in 14 places (L969, L977, L980, L990, L993, L1006, L1009, L1015, L1021, L1024 in `RenderWithSyncAsync`; L297, L545 in tree/status views). Existing tests already exercise `TestConsole` + `Live()` successfully (e.g., `RenderWorkItemAsync_CoreFields_RenderedImmediately` passes through the full `_console.Live().StartAsync()` path). Epic 2 T6 includes both a Live-context integration test and a direct `IRenderable` composition test. If incompatible, fallback: render description as a separate `_console.Write()` call after the Live block completes. |
| Unexpected `StripHtmlTags` usages appear between Epic 1 and Epic 3 | Low | Low | Epic 3 T7 includes a mandatory pre-removal grep for `StripHtmlTags` across the entire codebase. Any new usages are updated before removal. |

---

## Open Questions

| ID | Question | Severity | Context |
|----|----------|----------|---------|
| OQ-1 | **Markdown-to-HTML write-path feature**: Work item #1281 acceptance criterion #2 requests a `--format markdown` flag on `twig update` that converts markdown to HTML before sending to ADO. This plan covers the read path (rendering HTML in terminal) and consolidation, but does not implement the markdown-to-HTML write-path conversion. Adding this feature requires: (a) a markdown parser library (e.g., Markdig — AOT compatibility must be verified, or a lightweight custom converter), (b) a `--format` parameter on `UpdateCommand.ExecuteAsync` and the `Update()` routing in `TwigCommands`, (c) tests for the conversion pipeline. Should this be added as a 4th Epic in this plan, or deferred to a separate plan? | **Moderate** | The write-path HTML pass-through already works (sending `<h1>Hello</h1>` via `twig update` renders correctly in ADO). The `--format markdown` flag is described as "optionally support" in the work item description, suggesting it could be a follow-on. However, it is explicitly listed as acceptance criterion #2. |
| OQ-2 | **Markdig AOT compatibility**: If OQ-1 is resolved by adding markdown-to-HTML support, the leading .NET markdown library is [Markdig](https://github.com/xoofx/markdig). It uses reflection for its extension pipeline. Under `PublishAot=true` + `TrimMode=full`, Markdig may produce trim warnings or runtime failures. Alternatives: (a) verify Markdig works under AOT with targeted `rd.xml` annotations, (b) use a minimal custom markdown-to-HTML converter for headings/lists/bold/italic (limited subset), (c) shell out to an external tool like `pandoc`. | **Low** | Only relevant if OQ-1 is resolved as "include in this plan." Markdig's core pipeline (no extensions) may be AOT-safe — needs verification. |
| OQ-3 | **`--format` flag scope**: If markdown-to-HTML is added, should `--format markdown` also apply to `twig new --description`, `twig seed edit`, and `twig note`? Or only `twig update System.Description`? | **Low** | The work item mentions `twig update` specifically. The plan already lists NG7 (no `--format` on `twig edit`/`twig seed edit`). |

