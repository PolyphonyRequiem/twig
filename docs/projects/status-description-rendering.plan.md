# Solution Design: Full-Width Multi-Row Description Rendering in `twig status`

> **ADO Issue**: #1310
> **Date**: 2026-03-30
> **Status**: ✅ Done
> **Classification**: Deep (few files, moderate complexity in HTML conversion logic)
> **Estimated LoC**: ~280 (145 production + 135 tests)

---

## 1. Executive Summary

The description field in `twig status` is currently truncated to 60 characters and
squeezed into the second column of a two-column key-value Grid. This makes it nearly
useless for descriptions of any meaningful length. This plan upgrades description
rendering to support multiple rows at **true full panel width**, converting ADO's HTML
content to readable plain text with preserved paragraph structure.

The description is rendered as a separate renderable below the key-value Grid (via
Spectre's `Rows` and `Rule` widgets), not inside a Grid cell, ensuring zero width is
lost to the label column. Two rendering paths are updated:

1. **Spectre.Console async path** (`BuildStatusViewAsync` in `SpectreRenderer.cs`)
2. **HumanOutputFormatter sync path** (`FormatWorkItem` in `HumanOutputFormatter.cs`)

The `RenderWorkItemAsync` path (used by `twig set`) is explicitly out of scope.
One PR delivers the complete feature.

---

## 2. Background & Current Architecture

### Rendering Paths

The `twig status` command has two rendering paths:

| Path | Entry Point | File | Used When |
|------|------------|------|-----------|
| **Spectre async** | `BuildStatusViewAsync` (line 439) | `src/Twig/Rendering/SpectreRenderer.cs` | TTY output, human format |
| **Human sync** | `FormatWorkItem` (line 64) | `src/Twig/Formatters/HumanOutputFormatter.cs` | Piped output, `--no-live`, non-default format |
| **Set command** *(out of scope)* | `RenderWorkItemAsync` (line 569) | `src/Twig/Rendering/SpectreRenderer.cs` | `twig set` display |

### Current Description Flow

Both in-scope paths route description through extended field methods:

- **Spectre path**: `BuildStatusViewAsync` → `AddExtendedFieldRows(itemGrid, item, ...)` (line 468)
  → `FormatterHelpers.FormatFieldValue(value, dataType, maxWidth: 60)` (lines 675, 697)
- **Human path**: `FormatWorkItem` → `GetExtendedFields(item, defLookup, ...)` (line 90)
  → `FormatterHelpers.FormatFieldValue(value, dataType, maxWidth: 60)` (lines 1460, 1479)

Both converge at `FormatterHelpers.FormatFieldValue` (line 25 of `FormatterHelpers.cs`):
```csharp
"html" => Truncate(Rendering.SpectreRenderer.StripHtmlTags(value), maxWidth)
```

This strips all HTML tags via a simple state machine (`StripHtmlTags`, line 725 of
`SpectreRenderer.cs`) and truncates to 60 characters. Structural information (paragraphs,
list items) is completely lost.

### What's Wrong

| Problem | Root Cause |
|---------|------------|
| Description ≤60 chars | `FormatFieldValue` called with `maxWidth: 60` in 4 call sites |
| Single row only | Grid cell gets a single-line string with no wrapping |
| Doesn't use full width | Value column shares space with label column (~15-25 chars), yielding ~95-100 usable chars on 120-char terminal instead of ~117 |
| HTML structure lost | `StripHtmlTags` removes all tags without inserting line breaks |
| Paragraphs run together | `<p>First</p><p>Second</p>` → `"FirstSecond"` |

### ADO Description Format

ADO stores `System.Description` as HTML (`dataType: "html"`). Typical content:
```html
<div>
  <p>Implement the new caching layer for work item queries.</p>
  <p>Acceptance criteria:</p>
  <ul>
    <li>Cache TTL configurable via twig.json</li>
    <li>Cache invalidated on refresh</li>
  </ul>
</div>
```

**Current rendering**: `Implement the new caching layer for work item queries.Accep…`

**Desired rendering** (full panel width, `Rule` separator below Grid):
```
╭──────────────────────────── #42 My Task ─────────────────────────────────╮
│  Type:      📋 Task                                                      │
│  State:     [Active]                                                     │
│  Assigned:  Daniel Green                                                 │
│  Priority:  2                                                            │
│  ── Description ───────────────────────────────────────────────────────  │
│  Implement the new caching layer for work item queries.                   │
│                                                                          │
│  Acceptance criteria:                                                    │
│  • Cache TTL configurable via twig.json                                  │
│  • Cache invalidated on refresh                                          │
╰──────────────────────────────────────────────────────────────────────────╯
```

---

## 3. Design Decisions

### D-1: HTML-to-Plain-Text with Structure Preservation

**Decision**: Add `HtmlToPlainText(string html, int maxLines = 15)` to
`FormatterHelpers.cs`. Two-pass approach: (1) insert structural markers before block
elements, (2) strip remaining tags and decode entities.

**Rules**:
- `<p>`, `<div>`, `<br>`, `<br/>` → newline
- `<li>` → newline + `• ` prefix
- `<h1>`–`<h6>` → newline (content rendered as plain text)
- Consecutive blank lines collapsed to single blank line
- Leading/trailing whitespace trimmed per line
- Named HTML entities decoded: `&amp;` → `&`, `&lt;` → `<`, `&gt;` → `>`, `&quot;` → `"`, `&nbsp;` → space
- Truncate at `maxLines` lines (default 15) with `(+N more lines)` suffix

**Rationale**: Placed in `FormatterHelpers.cs` because both `SpectreRenderer` and
`HumanOutputFormatter` already depend on it. A full HTML parser (HtmlAgilityPack,
AngleSharp) is overkill for CLI output and would introduce an AOT-incompatible dependency.
The existing `StripHtmlTags` is NOT modified — `HtmlToPlainText` is a new method with
zero regression risk to existing callers.

**Trade-off**: Loses bold/italic/heading formatting. Acceptable for a CLI status view.

**Note**: `WorkItemFormView.cs` (Twig.Tui) has an intentional duplicate of `StripHtmlTags`
due to circular dependency constraints. That duplicate is unaffected — Twig.Tui does not
use `FormatterHelpers`.

### D-2: Description as Full-Width `Rows` Section Below Grid

**Decision**: Render `System.Description` as a separate renderable below the key-value
Grid using `new Rows(itemGrid, rule, descMarkup)` as Panel content. Uses Spectre's `Rule`
widget with `.LeftJustified().RuleStyle("dim")` as the visual separator.

**Rationale**: Adding description inside the two-column Grid (`itemGrid.AddRow()`) would
constrain width to `panelWidth - labelColumnWidth` (~95-100 chars on 120-char terminal).
Using `Rows` gives the description the **full panel inner width** (~117 chars).

Both `Rows` and `Rule` are proven patterns in the codebase:
- `Rows` as Panel content: lines 1529, 1644 of `SpectreRenderer.cs`
- `Rule` separators: lines 1102, 1519, 1621 of `SpectreRenderer.cs`

### D-3: Line Limit with Configurable Truncation

**Decision**: Cap at 15 lines with `(+N more lines)` indicator. `MaxDescriptionLines = 15`
is a private constant — no parameter, as there is only one realistic value.

**Rationale**: Very long descriptions shouldn't dominate the status view. 15 lines
provides substantial context. Users can view full content on the ADO web UI.

### D-4: Skip Description from Extended Fields — All 4 Code Paths

**Decision**: Exclude `System.Description` unconditionally from both `AddExtendedFieldRows`
and `GetExtendedFields`, regardless of StatusFieldsConfig settings. The dedicated
description section always takes priority.

**Implementation**: 4 insertion points across 2 methods × 2 branches each:

| # | File | Method | Branch | Insertion Point |
|---|------|--------|--------|-----------------|
| 1 | `SpectreRenderer.cs` | `AddExtendedFieldRows` | `statusFieldEntries` path (line 662) | After `if (!entry.IsIncluded) continue;` |
| 2 | `SpectreRenderer.cs` | `AddExtendedFieldRows` | Auto-detection path (line 683) | After `if (CoreFields.Contains(kvp.Key)) continue;` |
| 3 | `HumanOutputFormatter.cs` | `GetExtendedFields` | `statusFieldEntries` path (line 1447) | After `if (!entry.IsIncluded) continue;` |
| 4 | `HumanOutputFormatter.cs` | `GetExtendedFields` | Auto-detection path (line 1468) | After `if (CoreFieldPrefixes.Contains(kvp.Key)) continue;` |

### D-5: Scope Boundaries

Explicitly **out of scope**:
- `RenderWorkItemAsync` (`twig set` path) — separate future issue
- `System.History` rendering — not in #1310
- `<table>` rendering — rare in ADO descriptions, tags stripped
- `<a href>` link preservation — users can view on ADO web UI

---

## 4. Implementation Plan

### Epic 1: Full-Width Description Rendering

> **PR Group**: Single PR
> **Classification**: Deep (4 production files, moderate complexity in HTML conversion)
> **Estimated LoC**: ~280 new/modified lines (145 production + 135 tests)
> **Files touched**: 6
> **Successor**: None (single epic)

#### Task 1.1 — Add `HtmlToPlainText` method to FormatterHelpers

**File**: `src/Twig/Formatters/FormatterHelpers.cs` (currently 138 lines)
**Effort**: ~75-85 LoC new

Add `internal static string HtmlToPlainText(string html)` that:
1. Returns empty string for null/whitespace input
2. Inserts newlines before block-level opening tags (`<p>`, `<div>`, `<br>`, `<br/>`, `<h1>`–`<h6>`)
3. Converts `<li>` to `\n• `
4. Strips all remaining HTML tags (state-machine approach consistent with `StripHtmlTags`)
5. Decodes named HTML entities: `&amp;`, `&lt;`, `&gt;`, `&quot;`, `&nbsp;`
6. Collapses consecutive blank lines to single blank line
7. Trims each line; trims leading/trailing blank lines from result
8. Truncates at `MaxDescriptionLines` (= 15) with `(+N more lines)` suffix
9. AOT-safe: `StringBuilder` operations only

#### Task 1.2 — Render description below Grid in `BuildStatusViewAsync`

**File**: `src/Twig/Rendering/SpectreRenderer.cs`
**Effort**: ~25 LoC modified

**Part A — Panel content composition** (lines 540-545):

Replace direct Panel wrapping of `itemGrid` with conditional `Rows` composition:
```csharp
IRenderable panelContent = itemGrid;
if (item.Fields.TryGetValue("System.Description", out var rawDescription)
    && !string.IsNullOrWhiteSpace(rawDescription))
{
    var plainText = Formatters.FormatterHelpers.HtmlToPlainText(rawDescription);
    if (!string.IsNullOrWhiteSpace(plainText))
    {
        var rule = new Rule("[dim]Description[/]").LeftJustified().RuleStyle("dim");
        var descMarkup = new Markup(Markup.Escape(plainText));
        panelContent = new Rows(itemGrid, rule, descMarkup);
    }
}
var itemPanel = new Panel(panelContent)
    .Header($"[bold]#{item.Id} {Markup.Escape(item.Title)}[/]{dirty}")
    .Border(BoxBorder.Rounded)
    .Expand();
```

**Part B — Skip description in `AddExtendedFieldRows`** (line 647):

Insert `System.Description` skip in both branches:
- Branch 1 (line 664, statusFieldEntries): `if (string.Equals(entry.ReferenceName, "System.Description", StringComparison.OrdinalIgnoreCase)) continue;`
- Branch 2 (line 689, auto-detection): `if (string.Equals(kvp.Key, "System.Description", StringComparison.OrdinalIgnoreCase)) continue;`

#### Task 1.3 — Update `HumanOutputFormatter` sync path

**File**: `src/Twig/Formatters/HumanOutputFormatter.cs`
**Effort**: ~35 LoC modified

**Part A — Dedicated description section in `FormatWorkItem`** (after line 107):

Insert a description section after the extended fields block, before the progress bar:
```csharp
if (item.Fields.TryGetValue("System.Description", out var rawDescription)
    && !string.IsNullOrWhiteSpace(rawDescription))
{
    var plainText = FormatterHelpers.HtmlToPlainText(rawDescription);
    if (!string.IsNullOrWhiteSpace(plainText))
    {
        sb.AppendLine();
        sb.AppendLine($"  {Dim}── Description ──────────────────{Reset}");
        foreach (var line in plainText.Split('\n'))
            sb.AppendLine($"  {line}");
    }
}
```

Uses existing ANSI constants (`Dim`, `Reset`) and matches the `── Extended ──` separator
pattern at line 94.

**Part B — Skip description in `GetExtendedFields`** (line 1441):

Insert `System.Description` skip in both branches:
- Branch 1 (line 1449, statusFieldEntries): `if (string.Equals(entry.ReferenceName, "System.Description", StringComparison.OrdinalIgnoreCase)) continue;`
- Branch 2 (line 1472, auto-detection): `if (string.Equals(kvp.Key, "System.Description", StringComparison.OrdinalIgnoreCase)) continue;`

#### Task 1.4 — Unit and integration tests

**Files**:
- `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` (existing, 222 lines)
- `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` (existing, 329 lines)
- `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` (existing)

**Effort**: ~135 LoC new

**Unit tests for `HtmlToPlainText`** (in `FormatterHelpersTests.cs`):

| Test | Input | Expected |
|------|-------|----------|
| `HtmlToPlainText_PlainText_PassesThrough` | `"Hello world"` | `"Hello world"` |
| `HtmlToPlainText_ParagraphTags_ProduceLineBreaks` | `"<p>A</p><p>B</p>"` | Contains `"A"` and `"B"` separated by newline |
| `HtmlToPlainText_BrTag_ProducesLineBreak` | `"A<br>B"` / `"A<br/>B"` | `"A\nB"` |
| `HtmlToPlainText_ListItems_BulletPrefix` | `"<ul><li>X</li><li>Y</li></ul>"` | Contains `"• X"` and `"• Y"` |
| `HtmlToPlainText_NestedHtml_FullyStripped` | `"<div><b>Bold</b></div>"` | `"Bold"` |
| `HtmlToPlainText_HtmlEntities_Decoded` | `"&amp; &lt; &gt; &quot; &nbsp;"` | `"& < > \" "` |
| `HtmlToPlainText_ConsecutiveBlankLines_Collapsed` | Multiple empty `<p></p>` | Single blank line max |
| `HtmlToPlainText_LineLimitRespected` | 20-line HTML (no args) | 15 lines + `"(+5 more lines)"` |
| `HtmlToPlainText_EmptyInput_ReturnsEmpty` | `""` | `""` |
| `HtmlToPlainText_WhitespaceOnly_ReturnsEmpty` | `"   "` | `""` |

**Integration tests for `BuildStatusViewAsync`** (in `RenderWorkItemTests.cs`):

Helper methods needed:
```csharp
private static Func<Task<WorkItem?>> ItemFunc(WorkItem item)
    => () => Task.FromResult<WorkItem?>(item);
private static Func<Task<IReadOnlyList<PendingChangeRecord>>> NoPendingChanges()
    => () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>());
```

| Test | Assertion |
|------|-----------|
| `BuildStatusViewAsync_Description_RendersFullWidthSection` | Output contains `"Description"` and `"Cache layer"` |
| `BuildStatusViewAsync_NoDescription_NoDescriptionSection` | Output does not contain `"Description"` separator |
| `BuildStatusViewAsync_LongDescription_TruncatedWithIndicator` | Output contains `"(+"` truncation marker |
| `BuildStatusViewAsync_DescriptionExcludedFromExtendedGrid` | Description appears once (no truncated duplicate) |
| `BuildStatusViewAsync_MultiParagraph_PreservesStructure` | Both `"A"` and `"B"` present |

**Integration tests for `FormatWorkItem`** (in `HumanOutputFormatterTests.cs`):

| Test | Assertion |
|------|-----------|
| `FormatWorkItem_Description_RendersDedicatedSection` | Contains `"── Description"` and `"Cache layer"` |
| `FormatWorkItem_NoDescription_NoDescriptionSection` | Does not contain `"── Description"` |
| `FormatWorkItem_MultilineDescription_IndentedWith2Spaces` | Contains `"  First"` and `"  Second"` |
| `FormatWorkItem_DescriptionExcludedFromExtendedFields` | `"── Description"` appears once |

### Acceptance Criteria — Epic 1

| # | Criterion | Verification |
|---|-----------|-------------|
| AC-1 | Description renders as multi-line text at full panel width (not constrained by Grid label column) | Manual + integration test |
| AC-2 | HTML paragraph breaks preserved as blank lines | Unit test (`ParagraphTags_ProduceLineBreaks`) |
| AC-3 | HTML list items render with `•` bullet prefix | Unit test (`ListItems_BulletPrefix`) |
| AC-4 | Named HTML entities decoded (`&amp;`, `&lt;`, `&gt;`, `&quot;`, `&nbsp;`) | Unit test (`HtmlEntities_Decoded`) |
| AC-5 | Description limited to 15 lines with `(+N more lines)` overflow | Unit test (`LineLimitRespected`) |
| AC-6 | Description section absent when field is empty/missing | Unit + integration tests |
| AC-7 | Description excluded from extended fields even when starred — all 4 code paths | Integration tests (`ExcludedFromExtendedGrid/Fields`) |
| AC-8 | Sync path renders description with 2-space indent and ANSI separator | Integration tests in `HumanOutputFormatterTests.cs` |
| AC-9 | All existing tests pass (no regressions) | CI |
| AC-10 | AOT build succeeds | CI |

---

## 5. Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| HTML entity decoding misses edge cases | Low | Low | Named set covers virtually all ADO content. Unrecognized entities pass through as-is. |
| Description wrapping at narrow widths | Low | Low | Spectre `Markup` handles word-wrap inside `Panel.Expand()`. `Rule` adapts to width. |
| Very long descriptions slow rendering | Low | Low | 15-line cap prevents excessive output. `HtmlToPlainText` is O(n). |
| Starred description causes duplicate | Medium | Low | `System.Description` skip in all 4 code paths. Explicit test coverage. |
| `Rows`/`Rule` composition breaks Panel layout | Low | Low | Both are used as Panel content in codebase (lines 1529, 1644). Pattern is proven. |
| Missing a skip insertion point | Medium | Low | All 4 points enumerated in D-4 with exact locations. Tests verify no duplicates. |
| `TestConsole` strips markup, assertion mismatches | Low | Low | Tests assert on plain text content, not Spectre markup syntax. |

---

## 6. Open Questions

*None identified.* All design decisions are fully resolved and grounded in verified
codebase patterns. The existing draft plan (Rev 3) addressed all feedback from
tech/readability review.

---

## 7. File Change Summary

| File | Change Type | LoC |
|------|-------------|-----|
| `src/Twig/Formatters/FormatterHelpers.cs` | Add `HtmlToPlainText` method | ~80 |
| `src/Twig/Rendering/SpectreRenderer.cs` | Modify `BuildStatusViewAsync` + `AddExtendedFieldRows` | ~25 |
| `src/Twig/Formatters/HumanOutputFormatter.cs` | Modify `FormatWorkItem` + `GetExtendedFields` | ~35 |
| `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | Add `HtmlToPlainText` unit tests | ~45 |
| `tests/Twig.Cli.Tests/Rendering/RenderWorkItemTests.cs` | Add `BuildStatusViewAsync` integration tests | ~55 |
| `tests/Twig.Cli.Tests/Formatters/HumanOutputFormatterTests.cs` | Add `FormatWorkItem` integration tests | ~35 |
| **Total** | | **~280** |

---

## 8. Completion

> **Completed**: 2026-03-30
> **PR**: Full-Width Description Rendering (merged as PR #6)
> **ADO Issue**: #1310 — transitioned to Done

### Summary

All tasks in Epic 1 are complete. The `twig status` command now renders
`System.Description` as a full-width, multi-line section below the key-value Grid,
using Spectre's `Rows` and `Rule` composition. HTML content from ADO is converted
to structured plain text via `FormatterHelpers.HtmlToPlainText`, preserving paragraph
breaks, list bullets, and entity decoding. Both the Spectre async path and
HumanOutputFormatter sync path were updated, with description excluded from extended
fields across all 4 code paths. A post-merge holistic simplification pass cleaned up
`NormalizeLines` in `FormatterHelpers`. All acceptance criteria (AC-1 through AC-10)
verified, all tests pass, AOT build succeeds.
