# Plan: Rich HTML Description Rendering for Terminal Display

> **Date**: 2026-03-28
> **Status**: Draft
> **ADO Epic**: #1281
> **Companion plan**: `rich-html-descriptions.plan.md` (covers the write/input side)

---

## Executive Summary

When twig displays ADO work item descriptions, it currently strips **all** HTML tags via a
simple character-walking `StripHtmlTags()` method, losing all formatting — headings become
plain text, bold/italic vanishes, lists flatten into run-on sentences, and HTML entities
like `&amp;` remain un-decoded. This plan introduces a two-tier HTML rendering pipeline:

1. **Plain-text extraction** (`HtmlConverter` in `Twig.Domain.Common`) — replaces the
   duplicated `StripHtmlTags` methods with a proper converter that decodes HTML entities,
   preserves paragraph/list structure as whitespace, and normalizes output. Used by
   formatters, JSON output, and the TUI.

2. **Rich terminal rendering** (`HtmlSpectreConverter` in `Twig\Rendering`) — converts
   HTML to Spectre.Console `IRenderable` objects with bold, italic, underline, headings,
   bullet lists, numbered lists, code blocks, and hyperlinks. Used by `RenderWorkItemAsync`
   to display full-fidelity descriptions in the CLI detail view.

Both converters are AOT-safe (no regex, no reflection), share a common tokenization
approach, and degrade gracefully on malformed input.

---

## Background

### Current State

- ADO's `System.Description` field has data type `"html"` — content is HTML from ADO's
  WYSIWYG editor (e.g., `<p>Fix the <strong>auth</strong> bug</p><ul><li>Step 1</li></ul>`)
- **SpectreRenderer** (CLI): `TruncateField(description, 200)` calls `StripHtmlTags()`,
  then `Markup.Escape()`. All formatting is lost; output is a 200-char truncated plain string.
- **WorkItemFormView** (TUI): Duplicates the same `StripHtmlTags()` method (noted as a
  TODO for consolidation to `Twig.Domain.Common`).
- **FormatterHelpers**: `FormatFieldValue()` calls `SpectreRenderer.StripHtmlTags()` for
  `html`-typed fields — tight coupling between formatter and renderer.
- **HTML entity gap**: No `HtmlDecode`/`WebUtility.HtmlDecode` usage anywhere.
  `&amp;`, `&lt;`, `&nbsp;`, `&#NNN;` pass through to terminal output un-decoded.
- **Code duplication**: `StripHtmlTags()` is copy-pasted between `SpectreRenderer.cs`
  (line 725) and `WorkItemFormView.cs` (line 310).

### What This Solves

| Before | After |
|--------|-------|
| `Fix the auth bug Step 1 Step 2` | **Fix the auth bug** • Step 1 • Step 2 |
| `Meeting &amp; notes` | `Meeting & notes` |
| All descriptions truncated to 200 chars | Detail view shows full rich description |
| No heading/list/code structure visible | Headings bold+underlined, lists bulleted, code dimmed |
| Duplicated `StripHtmlTags` in 2 projects | Single `HtmlConverter` in shared Domain.Common |

### Display Contexts

| Context | Current | Proposed |
|---------|---------|----------|
| `RenderWorkItemAsync` (detail view) | 200-char stripped text in grid cell | Full rich description in Panel below grid |
| `BuildStatusViewAsync` (status view) | 200-char stripped text in grid cell | Improved plain text with entity decoding (compact) |
| `HumanOutputFormatter` (non-TTY) | Stripped text via FormatterHelpers | Improved plain text with entity decoding |
| `JsonOutputFormatter` | Stripped text | Improved plain text with entity decoding |
| `WorkItemFormView` (TUI) | Stripped text in TextField | Improved plain text with entity decoding |
| Interactive tree preview | Not currently shown | Future: could add rich preview |

---

## Problem Statement

ADO work item descriptions contain structured HTML that conveys meaning through headings,
lists, emphasis, code blocks, and links. The current `StripHtmlTags()` approach destroys
this structure, producing unintelligible run-on text. Additionally, HTML entities are never
decoded. Users must open ADO's web UI to read properly formatted descriptions — defeating
the purpose of a CLI triage tool.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | Rich terminal rendering of HTML descriptions via Spectre.Console (bold, italic, headings, lists, code) |
| G2 | HTML entity decoding (`&amp;` → `&`, `&lt;` → `<`, `&nbsp;` → ` `, numeric entities) |
| G3 | Consolidate duplicated `StripHtmlTags` into a single shared `HtmlConverter` in Domain.Common |
| G4 | Full (non-truncated) description display in work item detail view |
| G5 | AOT-safe implementation (no regex, no reflection, no trim warnings) |
| G6 | Graceful degradation on malformed HTML — never crash, fall back to plain text |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Full CSS/style attribute parsing (ADO inline styles ignored beyond tag semantics) |
| NG2 | Image rendering (terminal limitation; `<img>` alt text shown as placeholder) |
| NG3 | JavaScript or embedded content execution |
| NG4 | Pixel-perfect ADO web UI reproduction |
| NG5 | Interactive HTML (forms, inputs) — display only |
| NG6 | TUI rich rendering (Terminal.Gui has its own markup system; out of scope) |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `<b>` / `<strong>` text renders as **bold** in terminal |
| FR-02 | `<i>` / `<em>` text renders as *italic* in terminal |
| FR-03 | `<u>` text renders as underlined in terminal |
| FR-04 | `<s>` / `<strike>` / `<del>` text renders as ~~strikethrough~~ in terminal |
| FR-05 | `<h1>`–`<h6>` render as bold (optionally underlined for h1/h2) with blank line separation |
| FR-06 | `<p>` content separated by blank lines |
| FR-07 | `<br>` / `<br/>` produce line breaks |
| FR-08 | `<ul><li>` renders as `  • item` with indentation |
| FR-09 | `<ol><li>` renders as `  1. item` with numbering |
| FR-10 | `<code>` (inline) renders as dimmed/highlighted text |
| FR-11 | `<pre>` / `<pre><code>` renders as indented block with dim styling |
| FR-12 | `<a href="url">text</a>` renders as `[link=url]text[/]` (Spectre hyperlink) |
| FR-13 | HTML entities (`&amp;`, `&lt;`, `&gt;`, `&nbsp;`, `&quot;`, `&#NNN;`, `&#xHH;`) decoded |
| FR-14 | Plain text input (no HTML) passes through unchanged |
| FR-15 | Malformed HTML degrades gracefully to plain text extraction |
| FR-16 | Nested lists render with increased indentation |
| FR-17 | `<table>` flattened to plain text: each row on its own line, cells separated by ` \| ` |
| FR-18 | `<img alt="text">` renders as `[image: text]` placeholder |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Zero trim/AOT warnings under `PublishAot=true` + `TreatWarningsAsErrors=true` |
| NFR-02 | No new NuGet dependencies (uses only .NET BCL + Spectre.Console) |
| NFR-03 | No regex usage (AOT trim safety) |
| NFR-04 | No reflection usage |
| NFR-05 | `HtmlConverter` in `Twig.Domain.Common` has zero Spectre.Console dependency |
| NFR-06 | Performance: convert a 10 KB HTML description in < 1 ms |
| NFR-07 | No new serializable types (no `TwigJsonContext` changes) |

---

## Proposed Design

### Architecture Overview

```
                    ┌──────────────────────────────────┐
                    │ HTML from ADO (System.Description)│
                    └──────────┬───────────────────────┘
                               │
                    ┌──────────▼───────────────────────┐
                    │ Twig.Domain.Common                │
                    │ ┌──────────────────────────────┐  │
                    │ │ HtmlConverter (static)        │  │
                    │ │  .ToPlainText(html) → string  │  │
                    │ │  .DecodeEntities(text) → str  │  │
                    │ │                               │  │
                    │ │ (shared tokenization logic)   │  │
                    │ └──────────────────────────────┘  │
                    └──────────┬───────────────────────┘
                               │
               ┌───────────────┼────────────────────┐
               │               │                    │
    ┌──────────▼──────┐  ┌─────▼──────────┐  ┌─────▼──────────────┐
    │ Twig (CLI)       │  │ Twig.Tui       │  │ Formatters         │
    │ Rendering/       │  │ Views/         │  │ FormatterHelpers   │
    │                  │  │                │  │                    │
    │ HtmlSpectre-     │  │ Uses           │  │ Uses               │
    │ Converter        │  │ ToPlainText()  │  │ ToPlainText()      │
    │ (static)         │  │                │  │                    │
    │                  │  │ (replaces      │  │ (replaces          │
    │ .ToRenderable()  │  │  StripHtml-    │  │  StripHtmlTags     │
    │ → IRenderable    │  │  Tags dup)     │  │  cross-project     │
    │                  │  │                │  │  coupling)          │
    │ .ToMarkup()      │  └────────────────┘  └────────────────────┘
    │ → string         │
    └──────────────────┘
```

### Key Design Decisions

| ID | Decision | Rationale | Alternative Considered |
|----|----------|-----------|----------------------|
| DD-01 | Custom character-walking tokenizer, no HTML parser library | AOT-safe, zero dependencies, ADO HTML is well-structured and limited in tag variety | HtmlAgilityPack — heavy, AOT compatibility uncertain, trim warnings likely |
| DD-02 | `HtmlConverter` in `Twig.Domain.Common` (plain text) | Shared by CLI, TUI, and formatters without Spectre dependency; consolidates duplicated `StripHtmlTags` | Keep in SpectreRenderer — perpetuates TUI duplication and cross-project coupling |
| DD-03 | `HtmlSpectreConverter` in `Twig\Rendering` (rich) | Spectre-specific; only CLI needs rich rendering; same project as SpectreRenderer | Domain layer — would add Spectre.Console dependency to Domain |
| DD-04 | Static utility classes, not DI services | Pure stateless functions; no side effects; no mocking needed (test inputs/outputs directly) | Interface + DI — over-engineered for pure functions |
| DD-05 | Produce `IRenderable` for block-level description display | Allows mixing `Markup`, `Table`, `Panel`, and `Rows` for proper block structure | Single markup string — can't embed Spectre `Table` in a markup string |
| DD-06 | Entity decoding via `System.Net.WebUtility.HtmlDecode` | BCL function in `System.Net.Primitives`; trim-safe, zero custom code to maintain, handles full entity set including numeric and hex forms | Custom lookup table — ~100 extra LoC with same capability; `InvariantGlobalization` only affects culture-sensitive operations, not character encoding |
| DD-07 | Full description in detail view, truncated in status view | Detail view is for inspection; status view is for overview/glancing | Always truncate — loses the primary value of rich rendering |
| DD-08 | Graceful fallback: on any parse error, return `Markup.Escape(plainText)` | Never crash the CLI due to unexpected HTML; plain text is always safe | Throw exception — would break commands on malformed input |

### HTML Tag → Spectre.Console Mapping

| HTML Element | Spectre Output | Block/Inline |
|-------------|----------------|-------------|
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
| `<tr>`, `<td>`, `<th>` | Table rows/cells | Block |
| `<img alt="text">` | `[dim][image: text][/]` | Inline |
| `<div>`, `<span>` | pass through content | Container |
| `<hr>` | Spectre `Rule()` | Block |

### Rendering Flow (Detail View)

```
RenderWorkItemAsync(item)
│
├─ Core fields grid (Type, State, Assigned, Area, Iteration)
│
├─ if (description is not null && not whitespace):
│   │
│   ├─ html = item.Fields["System.Description"]
│   ├─ renderable = HtmlSpectreConverter.ToRenderable(html)
│   ├─ descPanel = new Panel(renderable)
│   │     .Header("[dim]Description[/]")
│   │     .Border(BoxBorder.Rounded)
│   │     .Expand()
│   └─ grid is replaced by Rows(grid, descPanel)
│
├─ History, Tags (unchanged — truncated text in grid)
│
└─ Panel wrapping
```

### Rendering Flow (Status View — Compact)

```
BuildStatusViewAsync(item)
│
├─ Core fields grid
│
├─ AddExtendedFieldRows(grid, item, ...)
│   │
│   ├─ For "html" typed fields:
│   │   └─ FormatterHelpers.FormatFieldValue(value, "html", maxWidth: 60)
│   │       └─ HtmlConverter.ToPlainText(value)  ← NEW (replaces StripHtmlTags)
│   │           └─ Truncate(result, 60)
│   │
│   └─ For other fields: existing behavior
│
└─ Panel wrapping
```

---

## Implementation Plan

### Epic 1: HTML Processing Foundation in Domain.Common

**Classification**: Deep (few files, complex parsing logic)
**Estimated LoC**: ~350 new, ~20 modified
**Predecessor**: None
**PR scope**: 2 new files, 1 modified

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | Create `HtmlConverter` static class with `ToPlainText(string html)` method. Implements character-by-character HTML tokenization, tag-aware whitespace normalization, block element separation (paragraphs, headings → blank lines; list items → bullets; `<br>` → newlines), and HTML entity decoding via `System.Net.WebUtility.HtmlDecode`. Handles unclosed tags gracefully (same defensive logic as existing `StripHtmlTags`). | `src/Twig.Domain/Common/HtmlConverter.cs` (new) | L |
| T2 | Create comprehensive unit tests for `HtmlConverter.ToPlainText`: plain text passthrough, basic tag stripping, paragraph separation, heading + blank lines, ordered/unordered list rendering, nested lists, `<br>` handling, inline code, `<pre>` blocks, entity decoding (named: `&amp;` / `&lt;` / `&nbsp;`, decimal `&#8212;`, hex `&#x2014;`), unclosed tags, empty input, whitespace-only HTML, `<img>` alt text, deeply nested tags, mixed content. | `tests/Twig.Domain.Tests/Common/HtmlConverterTests.cs` (new) | L |

**Acceptance Criteria**:
- `HtmlConverter.ToPlainText("<p>Fix the <strong>auth</strong> bug</p>")` → `"Fix the auth bug"`
- `HtmlConverter.ToPlainText("<ul><li>Step 1</li><li>Step 2</li></ul>")` → `"  • Step 1\n  • Step 2"`
- `HtmlConverter.ToPlainText("<h2>Summary</h2><p>Details here</p>")` → `"Summary\n\nDetails here"`
- `HtmlConverter.ToPlainText("Meeting &amp; notes &#8212; draft")` → `"Meeting & notes — draft"`
- `HtmlConverter.ToPlainText("plain text no html")` → `"plain text no html"`
- `HtmlConverter.ToPlainText("<div><p>unclosed")` → `"unclosed"` (graceful fallback)
- All new tests pass, `dotnet build` succeeds with zero warnings

---

### Epic 2: Spectre.Console Rich HTML Rendering

**Classification**: Deep (few files, complex rendering logic)
**Estimated LoC**: ~450 new, ~80 modified
**Predecessor**: Epic 1

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T3 | Create `HtmlSpectreConverter` static class with `ToRenderable(string html)` returning `IRenderable`. Implements the HTML tag → Spectre mapping table defined in this plan. Inline elements produce markup annotations (`[bold]`, `[italic]`, etc.). Block elements produce separate `IRenderable` items composed via `Rows`. `<pre>` blocks produce `Panel` with `BoxBorder.Square`. `<table>` produces plain-text rows with ` \| ` cell separators. Wraps entire conversion in try/catch; on any error, falls back to `Markup.Escape(HtmlConverter.ToPlainText(html))`. | `src/Twig/Rendering/HtmlSpectreConverter.cs` (new) | XL |
| T4 | Comprehensive tests for `HtmlSpectreConverter`: bold/italic/underline markup output, heading rendering, paragraph separation, list bullet rendering, numbered list rendering, nested lists, code inline, code block in Panel, hyperlink markup, table plain-text rendering, entity decoding in markup, mixed block+inline, `<hr>` as Rule, `<img>` alt text, plain text passthrough, malformed HTML fallback, empty input. Uses `TestConsole` to verify rendered output. | `tests/Twig.Cli.Tests/Rendering/HtmlSpectreConverterTests.cs` (new) | XL |
| T5 | Update `SpectreRenderer.RenderWorkItemAsync()` to use `HtmlSpectreConverter.ToRenderable()` for the description field. Replace the current `grid.AddRow("[dim]Description:[/]", Markup.Escape(TruncateField(description, 200)))` with a description Panel rendered below the core fields grid using `Rows(coreGrid, descriptionPanel)`. Always render the description as a Panel (no inline/panel branching). | `src/Twig/Rendering/SpectreRenderer.cs` | M |
| T6 | Update existing `RenderWorkItemTests` to verify: (a) HTML description renders with bold markup, (b) description renders in Panel, (c) entities decoded in rendered output, (d) existing tests still pass. Add new test methods. | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | M |

**Acceptance Criteria**:
- `twig set 42` on an item with HTML description shows bold headings, bulleted lists, italic text
- Rich description renders in a bordered Panel below core fields in the detail view
- Malformed HTML gracefully falls back to plain text (no crash, no raw HTML shown)
- All HTML entities decoded in rendered output
- All existing `RenderWorkItemTests` continue to pass

---

### Epic 3: Codebase Consolidation and Cleanup

**Classification**: Wide (many files, mechanical replacements)
**Estimated LoC**: ~30 new, ~130 modified/removed
**Predecessor**: Epic 1

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T7 | Consolidate all `StripHtmlTags()` usages: (1) replace calls in `SpectreRenderer.TruncateField()` with `HtmlConverter.ToPlainText()`; (2) update `FormatterHelpers.FormatFieldValue()` and `FormatFieldValueForJson()` to call `HtmlConverter.ToPlainText()` instead of `SpectreRenderer.StripHtmlTags()`; (3) remove the now-unused `StripHtmlTags()` method from `SpectreRenderer`. This decouples `FormatterHelpers` from `SpectreRenderer`. | `src/Twig/Rendering/SpectreRenderer.cs`, `src/Twig/Formatters/FormatterHelpers.cs` | M |
| T8 | Replace duplicated `StripHtmlTags()` in `WorkItemFormView` with `HtmlConverter.ToPlainText()`. Remove the ~40-line duplicate method. | `src/Twig.Tui/Views/WorkItemFormView.cs` | S |
| T9 | Update affected tests and verify no regressions: redirect `StripHtmlTags` test references in `RenderWorkItemTests` to `HtmlConverter.ToPlainText()`; update `WorkItemFormViewTests` to verify HTML stripping still works; run `dotnet test` across all test projects. | `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs`, `tests/Twig.Tui.Tests/WorkItemFormViewTests.cs` | M |

**Acceptance Criteria**:
- Zero references to `SpectreRenderer.StripHtmlTags` remain outside SpectreRenderer
- `FormatterHelpers` no longer depends on `SpectreRenderer` (coupling removed)
- `WorkItemFormView` no longer contains a duplicate `StripHtmlTags` method
- HTML entities decoded consistently across all display paths (CLI, TUI, JSON, Human)
- All existing tests pass with no modifications to expected behavior
- `dotnet build` succeeds with zero warnings across all projects
- `dotnet test` passes across all test projects

---

## Successor Links (Execution Order)

```
Epic 1: HTML Processing Foundation
   │
   ├──► Epic 2: Spectre.Console Rich Rendering  (depends on Epic 1)
   │
   └──► Epic 3: Codebase Consolidation           (depends on Epic 1, parallel with Epic 2)
```

Epic 2 and Epic 3 can be developed in parallel after Epic 1 merges, since:
- Epic 2 adds *new* rendering (doesn't modify existing StripHtmlTags callers)
- Epic 3 *replaces* existing StripHtmlTags callers (doesn't touch new rendering)

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Custom HTML parser doesn't handle ADO's specific HTML quirks | Medium | Medium | T2/T7 include tests with real ADO HTML samples. Fallback to plain text on any parse error (DD-08). Collect sample HTML from actual ADO descriptions during development. |
| Spectre.Console markup injection from user HTML content | Low | High | All text content is escaped via `Markup.Escape()` before embedding in markup. Only structurally-derived markup (bold, italic tags) produces Spectre annotations. Link URLs are sanitized to prevent markup injection. |
| Terminal compatibility — some terminals don't support hyperlinks, strikethrough, or italic | Medium | Low | These are Spectre.Console features that degrade gracefully. Hyperlinks show as plain text in unsupported terminals. Italic/strikethrough may render as normal text. No functional impact. |
| Large HTML descriptions cause slow rendering or terminal flooding | Low | Medium | Consider a configurable max-render-length with a "… (truncated)" suffix. Detail view is explicitly for inspection, so some length is expected. |
| `InvariantGlobalization=true` affects entity decoding of non-ASCII characters | Low | Low | Entity decoder maps to `char` values directly. `InvariantGlobalization` affects culture-sensitive operations (collation, formatting), not character encoding. Verify with tests for `&#8212;` (em dash), `&#8217;` (right quote), etc. |
| `TruncateField` behavior change from improved plain text extraction | Medium | Low | T15 regression tests verify identical output for existing test cases. The improved version may produce slightly different whitespace, which is acceptable and intentional. |
| Breaking change to `FormatterHelpers.FormatFieldValueForJson` output format | Low | Medium | JSON consumers may depend on exact string format. Entity decoding is objectively correct (e.g., `&amp;` → `&`). Document as an improvement, not a breaking change. |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the rich description Panel have a maximum height/line limit in the detail view? | Low | Long descriptions will scroll naturally in the terminal. Could add a `--full` / `--brief` flag later if needed. Start without a limit. |
| 2 | Should `<table>` elements be rendered as Spectre `Table` or as aligned plain text? | Low | Spectre `Table` provides the best output. Tables in ADO descriptions are rare. Implement Spectre `Table` but fall back to plain text if table structure is malformed. |
| 3 | Should the interactive tree preview pane also use rich rendering? | Low | Out of scope for this plan. The preview pane currently doesn't show descriptions. Can be added in a follow-up. |
| 4 | ~~Should `System.Net.WebUtility.HtmlDecode` be used instead of a custom entity decoder?~~ | Resolved | Use `WebUtility.HtmlDecode`. It is in `System.Net.Primitives`, trim-safe, and eliminates ~100 LoC of custom lookup code. |
| 5 | Should `<style>` and `<script>` tag content be explicitly suppressed? | Low | ADO's HTML sanitization prevents scripts. Adding explicit suppression is a small defensive measure worth including in the tokenizer. |

---

## File Change Summary

| File | Change Type | Epic |
|------|-------------|------|
| `src/Twig.Domain/Common/HtmlConverter.cs` | **New** | 1 |
| `tests/Twig.Domain.Tests/Common/HtmlConverterTests.cs` | **New** | 1 |
| `src/Twig/Rendering/HtmlSpectreConverter.cs` | **New** | 2 |
| `tests/Twig.Cli.Tests/Rendering/HtmlSpectreConverterTests.cs` | **New** | 2 |
| `src/Twig/Rendering/SpectreRenderer.cs` | Modified | 2, 3 |
| `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | Modified | 2, 3 |
| `src/Twig/Formatters/FormatterHelpers.cs` | Modified | 3 |
| `src/Twig.Tui/Views/WorkItemFormView.cs` | Modified | 3 |
| `tests/Twig.Tui.Tests/WorkItemFormViewTests.cs` | Modified | 3 |

**Total**: 4 new files, 5 modified files
**Estimated LoC**: ~800 new + ~230 modified ≈ **1030 LoC total**
