# Plan: Rich HTML Descriptions for ADO Work Items

> **Date**: 2026-03-28
> **Status**: Draft (Rev 7 — addresses tech + readability review feedback)
> **ADO Epic**: #1281

---

## Executive Summary

Enable twig to push rich HTML content to Azure DevOps work-item descriptions and
add a Markdown-to-HTML conversion pipeline for ergonomic authoring. Today,
`twig update System.Description` sends the value as a raw string — since ADO's
`System.Description` field is natively HTML-typed, HTML pass-through already works.
The main deliverable is a **Markdig**-based conversion service exposed via a `--format`
flag on `twig update`. Plain-text descriptions continue to work unchanged.

Two PRs (Epics) deliver the feature:
1. **Markdig foundation** — library integration, converter class, AOT verification
2. **`--format` on `twig update`** — core command-level wiring and validation

---

## Background

### Current State

- `twig update System.Description "value"` creates a `FieldChange(field, null, value)`
  and sends it via `AdoRestClient.PatchAsync()` as a JSON Patch `replace` operation.
- The value is serialized as a JSON string via `JsonValue.Create(change.NewValue)` in
  `AdoResponseMapper.MapPatchDocument()`.
- ADO's `System.Description` field has data type `"html"` — **HTML pass-through already
  works**. Sending `<h1>Hello</h1>` renders correctly in the ADO web UI.
- `FieldDefinition.DataType` distinguishes `"html"` from `"plainText"` and `"string"`,
  but this metadata is not used during field updates.
- `FormatterHelpers.FormatFieldValue()` strips HTML via `SpectreRenderer.StripHtmlTags()`
  for terminal display, confirming the codebase already anticipates HTML content.
- `PendingNote` is a `readonly record struct` with a configurable `IsHtml` constructor
  parameter. No caller currently sets `IsHtml` to `true` — notes are always pushed as
  plain text.
- No Markdown processing library exists in the codebase.

### Test Framework

> **Important:** The test projects use **xUnit 2.9.3** with **Shouldly** assertions and
> **NSubstitute** mocking — not MSTest v4 as stated in project conventions. All tasks
> in this plan follow the actual codebase conventions: `[Fact]`/`[Theory]` attributes,
> Shouldly fluent assertions (`.ShouldBe()`, `.ShouldContain()`), and NSubstitute
> (`Substitute.For<T>()`, `Arg.Is<>()`, `.Received()`).

### What This Solves

Users (especially automated callers and AI agents) want structured content — headings,
lists, bold/italic, code blocks — without hand-crafting HTML:

```bash
# Before: manual HTML
twig update System.Description "<h2>Summary</h2><p>Fix the <strong>auth</strong> bug</p>"

# After: natural Markdown via twig update
twig update System.Description "## Summary\n\nFix the **auth** bug" --format markdown
```

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | Verify and document that HTML strings pass through to ADO correctly via `twig update` |
| G2 | Add `--format markdown` flag to `twig update` that converts Markdown → HTML via Markdig |
| G3 | Existing plain-text and HTML descriptions continue to work unchanged |
| G4 | Markdig integration is AOT-compatible (`PublishAot=true`, `TrimMode=full`) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Embedded Mermaid diagram rendering (ADO doesn't render Mermaid natively) |
| NG2 | Reverse conversion (HTML → Markdown) for display purposes |
| NG3 | Adding `--format` to `twig edit` or `twig seed edit` (editor workflow handles raw values) |
| NG4 | Sanitizing user-provided HTML (ADO handles its own sanitization) |
| NG5 | Stdin/pipe input (covered by shell substitution; `--file -` could be added later) |
| NG6 | `--file` flag for reading content from files (defer to follow-on) |
| NG7 | Field-type validation — `--format` does not check `FieldDefinition.DataType` (see DD-09) |
| NG8 | Adding `--format` to `twig new` or `twig note` (not in #1281 acceptance criteria; defer to follow-on) |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig update System.Description "<b>html</b>"` sends HTML unchanged (existing behavior) |
| FR-02 | `twig update System.Description "# Heading" --format markdown` converts to HTML before sending |
| FR-03 | Default behavior (no `--format` flag) is unchanged: value sent as-is |
| FR-04 | `--format` accepts `markdown` (case-insensitive per DD-08); any other value produces error `"Unknown format '{value}'. Supported formats: markdown"` with exit code 2 |
| FR-05 | Markdig pipeline uses advanced extensions (tables, task lists, auto-links) for GFM-like conversion |
| FR-06 | When `--format markdown` is used, the success message echoes the **original markdown input**, not the converted HTML. This applies to all output modes (human, JSON, compact, minimal). See *Design: Success Message Preservation* for implementation approach. |
| FR-07 | `MarkdownConverter.ToHtml()` returns `string.Empty` for null, empty, or whitespace-only input. Whitespace-only strings are not passed to Markdig (avoids producing `<p>   </p>` markup). |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | AOT compatible — Markdig must not produce trim/AOT warnings under `PublishAot=true` (verified against `src/Twig/Twig.csproj`) |
| NFR-02 | No new serializable types (no TwigJsonContext changes) |
| NFR-03 | Follows existing patterns (sealed class, primary constructor, DI via factory lambdas) |

---

## Proposed Design

### Architecture Overview

```
┌─ CLI Layer (Epic 2) ──────────────────┐      ┌─ Infrastructure Layer (Epic 1) ─┐
│                                       │      │                                 │
│  UpdateCommand                        │      │  MarkdownConverter              │
│    --format markdown ─────────────────┼─────►│    .ToHtml(markdown)            │
│    creates FieldChange(field, html)   │      │    (Markdig pipeline,           │
│                                       │      │     static cached)              │
└───────────────┬───────────────────────┘      └─────────────────────────────────┘
                │
                ▼
┌─ Infrastructure Layer (unchanged) ────┐
│                                       │
│  AdoRestClient                        │
│    .PatchAsync()                      │
│    FieldChange.NewValue = HTML string │
│                                       │
└───────────────────────────────────────┘
```

`UpdateCommand` converts its input via `MarkdownConverter.ToHtml()` before creating the
`FieldChange`. The REST client and patch pipeline remain format-agnostic — they receive
the already-converted value.

### Key Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Static utility class, not DI service | Stateless pure function wrapping Markdig; no mock injection needed |
| DD-02 | Markdig with `UseAdvancedExtensions()` | GFM-like behavior (tables, task lists, auto-links) that users expect |
| DD-03 | `--format` flag (not `--markdown` boolean) | Extensible — allows future formats without accumulating boolean flags |
| DD-04 | Conversion in CLI command, not AdoRestClient | REST client should be format-agnostic; format is a CLI concern |
| DD-05 | No `--format html` value needed | No-flag already sends as-is; explicit `html` adds no functionality |
| DD-06 | Place in new `Twig.Infrastructure/Content/` folder | Markdig is an external dependency → Infrastructure is the standard home. **The `Content` folder does not exist and must be created.** |
| DD-07 | `--format` (input) vs `--output` (display) naming | Existing commands use `--output` for display format (human/json/compact/minimal). `--format` controls input content conversion. XML doc comment on `TwigCommands.Update()` will document both clearly. |
| DD-08 | Case-insensitive format matching | `--format Markdown`, `--format MARKDOWN`, and `--format markdown` all resolve to the same conversion. Use `StringComparison.OrdinalIgnoreCase` for comparison. |
| DD-09 | No field-type restriction for `--format` | `--format` converts the value regardless of the target field's `DataType`. Applying `--format markdown` to a non-HTML field (e.g., `System.Title`) sends HTML markup as the value, which may render poorly in ADO. This is the caller's responsibility. A future enhancement could warn using `FieldDefinition.DataType` metadata, but the current design avoids the complexity of field metadata lookup during updates. |
| DD-10 | Whitespace-only input treated as empty | `string.IsNullOrWhiteSpace()` guard in `MarkdownConverter.ToHtml()` returns `string.Empty`. Prevents Markdig from producing semantically empty HTML like `<p>   </p>\n`. Consistent with the null/empty guard. |

### Conversion Pipeline Configuration

```csharp
private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()   // tables, task lists, auto-links, etc.
    .Build();
```

### Design: Success Message Preservation (FR-06)

The success message at `UpdateCommand.cs:66` uses the `value` parameter directly:
```csharp
Console.WriteLine(fmt.FormatSuccess($"#{local.Id} updated: {field} = '{value}'"));
```

When `--format markdown` is active, the implementation must store the converted HTML in
a separate local variable (e.g., `effectiveValue`) and use that for `FieldChange`, while
preserving the original `value` parameter for the success message:

```csharp
var effectiveValue = format is not null &&
    string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
        ? MarkdownConverter.ToHtml(value)
        : value;

var changes = new[] { new FieldChange(field, null, effectiveValue) };
// ... success message still references `value` (the original input)
```

This ensures all output modes behave correctly:
- **Human**: `#42 updated: System.Description = '## Summary'`
- **JSON**: `{"message": "#42 updated: System.Description = '## Summary'"}`
- **Compact / Minimal**: same — the formatter receives the original input, not HTML

`JsonOutputFormatter.FormatSuccess()` wraps the message in `{"message": "..."}` via
`Utf8JsonWriter` (see `JsonOutputFormatter.cs`). Since the message string itself
contains the original markdown, no special JSON-mode handling is needed.

### Command Flow: `twig update --format` (Epic 2)

```
twig update System.Description "## Summary\n\nFix the **auth** bug" --format markdown

1. Parse field="System.Description", value="## Summary\n...", format="markdown"
2. Validate format (case-insensitive via OrdinalIgnoreCase):
   "markdown" → proceed
   Unknown value → fmt.FormatError("Unknown format 'xyz'. Supported formats: markdown"),
                   return exit code 2
3. format == "markdown" → effectiveValue = MarkdownConverter.ToHtml(value)
   → "<h2>Summary</h2>\n<p>Fix the <strong>auth</strong> bug</p>\n"
4. Create FieldChange("System.Description", null, effectiveValue)
5. PatchAsync(id, changes, revision) → sends HTML to ADO
6. ADO renders the HTML natively in the web UI
7. Success message echoes original markdown input (FR-06):
   fmt.FormatSuccess($"#{id} updated: System.Description = '## Summary\n...'")
```


---

## Implementation Plan

### Epic 1: Markdig Integration and Markdown Converter (PR #1)

> **Classification:** Deep (4 files, focused library integration + pure-function design)
> **Estimated LoC:** ~180 (converter ~30, tests ~100, csproj/props ~50)
> **ADO Issue:** #1282
> **Predecessor**: None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | Add Markdig package to central version management | `Directory.Packages.props` | S |
| T2 | Add Markdig PackageReference to Infrastructure project | `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | S |
| T3 | Create MarkdownConverter static class | `src/Twig.Infrastructure/Content/MarkdownConverter.cs` (**new**) | M |
| T4 | Unit tests for MarkdownConverter | `tests/Twig.Infrastructure.Tests/Content/MarkdownConverterTests.cs` (**new**) | M |

#### T1: Add Markdig to Central Package Management

Add `Markdig` version **1.1.2** (latest stable as of 2026-03-28) to `Directory.Packages.props`.
Insert under a `<!-- Markdown -->` comment group, following the existing grouping pattern
(e.g., `<!-- Rendering -->`, `<!-- Testing -->`):
```xml
<!-- Markdown -->
<PackageVersion Include="Markdig" Version="1.1.2" />
```

#### T2: Add Markdig PackageReference

Add `<PackageReference Include="Markdig" />` to `src/Twig.Infrastructure/Twig.Infrastructure.csproj`
(version managed centrally).

#### T3: Create MarkdownConverter

Create the `Content/` folder in `Twig.Infrastructure` (does not exist) and add
`MarkdownConverter.cs`:

- **Class**: `internal static class MarkdownConverter`
- **Method**: `public static string ToHtml(string? markdown)`
- **Pipeline**: `static readonly MarkdownPipeline` with `UseAdvancedExtensions()`
- **Null/empty/whitespace guard**: `if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;`
  (covers null, empty, and whitespace-only inputs per FR-07 / DD-10)
- **Conversion**: `Markdig.Markdown.ToHtml(markdown, Pipeline)`

#### T4: Unit Tests for MarkdownConverter

Create `tests/Twig.Infrastructure.Tests/Content/MarkdownConverterTests.cs` using
xUnit `[Fact]`/`[Theory]` with Shouldly assertions:

| Test case | Input | Expected assertion |
|-----------|-------|--------------------|
| Heading | `"# Hello"` | Contains `<h1>Hello</h1>` |
| Bold/italic | `"**bold** _italic_"` | Contains `<strong>bold</strong>`, `<em>italic</em>` |
| Ordered list | `"1. A\n2. B"` | Contains `<ol>`, `<li>A</li>` |
| Unordered list | `"- A\n- B"` | Contains `<ul>`, `<li>A</li>` |
| Table | GFM pipe table | Contains `<table>`, `<th>`, `<td>` |
| Code block | Fenced ` ```csharp ``` ` | Contains `<code>` |
| Task list | `"- [x] Done"` | Contains `<input` and `checked` |
| Null input | `null` | Returns `string.Empty` |
| Empty input | `""` | Returns `string.Empty` |
| Whitespace-only | `"   "` | Returns `string.Empty` |

**Acceptance Criteria (Epic 1)**:
- `MarkdownConverter.ToHtml("# Hello\n\n**World**")` output contains `<h1>Hello</h1>` and `<strong>World</strong>`
- `MarkdownConverter.ToHtml("")` returns `string.Empty`
- `MarkdownConverter.ToHtml(null)` returns `string.Empty`
- `MarkdownConverter.ToHtml("   ")` returns `string.Empty`
- `dotnet publish src/Twig/Twig.csproj -c Release -r win-x64` succeeds with zero trim/AOT warnings
- All unit tests pass (`dotnet test`)

---

### Epic 2: `--format` Flag on `twig update` (PR #2)

> **Classification:** Deep (3 files, command-layer wiring with validation logic)
> **Estimated LoC:** ~200 (command changes ~40, routing ~10, tests ~150)
> **ADO Issue:** #1283
> **Predecessor**: Epic 1

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T5 | Add `format` parameter to `UpdateCommand.ExecuteAsync()` | `src/Twig/Commands/UpdateCommand.cs` | M |
| T6 | Wire `format` through `TwigCommands.Update()` routing | `src/Twig/Program.cs` | S |
| T7 | Unit tests for `--format` flag behavior | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | M |

#### T5: Add `format` Parameter to UpdateCommand

Add `string? format = null` parameter to `UpdateCommand.ExecuteAsync()` (after `outputFormat`,
before `CancellationToken`). Insert validation and conversion logic between the conflict
resolution block and the `FieldChange` creation:

1. **Validate format** (after line 51, before line 54):
   - `null` → no conversion, proceed with original `value`
   - `"markdown"` (case-insensitive via `StringComparison.OrdinalIgnoreCase`) → convert
   - Any other value → write error via `fmt.FormatError("Unknown format '{format}'. Supported formats: markdown")`
     to `Console.Error`, return exit code 2
2. **Convert if needed**: `var effectiveValue = MarkdownConverter.ToHtml(value)`
3. **Use `effectiveValue` for FieldChange** at current line 54:
   `new FieldChange(field, null, effectiveValue)`
4. **Preserve original `value`** in success message at current line 66 (unchanged)

Exit code 2 for invalid format follows the existing convention (missing field returns 2
at line 29; `NewCommand` returns 2 for missing title at line 44).

#### T6: Wire `format` Through TwigCommands.Update()

Add `string? format = null` parameter to `TwigCommands.Update()` in `Program.cs` (line ~453).
Pass through to `UpdateCommand.ExecuteAsync()`.

Add XML doc comment:
```csharp
/// <param name="format">Convert the input value before sending to ADO.
/// Supported: "markdown" (converts Markdown to HTML).
/// Distinct from --output, which controls display format.</param>
```

#### T7: Unit Tests for `--format` Flag

Add tests to `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` using xUnit `[Fact]`
with Shouldly assertions and NSubstitute mocking (matching existing patterns):

| Test | Assertion |
|------|-----------|
| **Format_Markdown_ConvertsValue** | `PatchAsync` receives `FieldChange` with value containing `<h1>` via `Arg.Is<>()` |
| **Format_Markdown_CaseInsensitive** | `--format MARKDOWN` produces same HTML conversion as `--format markdown` |
| **Format_Invalid_ReturnsExitCode2** | `ExecuteAsync(format: "xyz")` returns 2; stderr contains `"Unknown format 'xyz'. Supported formats: markdown"` |
| **Format_Null_PassesThroughUnchanged** | `ExecuteAsync(format: null)` sends original value to `PatchAsync` (regression guard) |
| **Format_Markdown_SuccessEchoesOriginalInput** | Capture stdout; assert it contains original markdown string, does not contain `<h1>` tags (FR-06) |
| **Format_Markdown_JsonOutput_EchoesOriginalInput** | `ExecuteAsync(format: "markdown", outputFormat: "json")`; stdout JSON `message` field contains original markdown, not HTML (FR-06 + `--output json` interaction) |

**Acceptance Criteria (Epic 2)**:
- `twig update System.Description "# Hello" --format markdown` — `PatchAsync` called with value containing `<h1>Hello</h1>`
- `twig update System.Description "# Hello" --format MARKDOWN` — same result (case-insensitive)
- `twig update System.Description "plain text"` continues to send `"plain text"` (no flag = no conversion)
- `twig update System.Description "<b>html</b>"` continues to send `<b>html</b>` as-is
- `twig update System.Description "text" --format xyz` returns exit code 2 with error containing `"Unknown format 'xyz'. Supported formats: markdown"`
- `twig update System.Description "# Hello" --format markdown` — success message echoes `# Hello` (the original markdown), not `<h1>Hello</h1>` (FR-06)
- `twig update System.Description "# Hello" --format markdown --output json` — JSON `message` field echoes original markdown (FR-06 in JSON mode)
- All existing `UpdateCommandTests` continue to pass
- `dotnet test` passes across all test projects

---

## Execution Order

```
Epic 1 (Markdig Foundation — #1282)
    │
    └──► Epic 2 (--format on twig update — #1283)
```

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Markdig produces trim/AOT warnings under `PublishAot=true` | Low | High | Markdig is widely used in AOT contexts. Verify with `dotnet publish src/Twig/Twig.csproj -c Release -r win-x64` early in T4. If warnings appear, evaluate `<SuppressTrimAnalysisWarnings>` scoped to Markdig only, or fall back to a minimal hand-rolled converter. |
| `--format` / `--output` naming proximity causes user confusion | Low | Medium | Semantically distinct: `--format` = input conversion, `--output` = display format. XML doc comment on `TwigCommands.Update()` will document both clearly. ConsoleAppFramework auto-generates `--help` from parameter names and XML docs. |
| Shell escaping of Markdown characters (`*`, `#`, etc.) | Medium | Medium | Standard shell concern — users must quote arguments. Success messages echo the original input value, not converted HTML (FR-06), which aids debugging when conversion produces unexpected results. |
| `--format markdown` applied to non-HTML fields (e.g., System.Title) | Low | Low | By design (DD-09), twig does not restrict `--format` to specific field types. Callers are responsible for applying it to appropriate fields. ADO may sanitize or reject unexpected HTML. Documented in `--help` text. |

---

## Revision History

| Rev | Date | Changes |
|-----|------|---------|
| 7 | 2026-03-28 | Addressed review feedback: (1) Fixed task numbering — Epic 2 now uses T5–T7, sequential from T4 with no gaps; (2) Added FR-07 and DD-10 for whitespace-only markdown input edge case; (3) Added *Design: `--format` + `--output` Interaction Matrix* section and new test case `Format_Markdown_JsonOutput_EchoesOriginalInput` in T7 to explicitly verify JSON output echoes original markdown; (4) Moved implementation details from FR-06 to new *Design: Success Message Preservation* section, keeping FR-06 as a clean requirement statement; (5) Broke T5–T7 task descriptions into separate sub-sections with structured details instead of dense table cells; (6) Fixed architecture diagram — AdoRestClient now drawn in its own labeled box with clear layer annotation. |
| 6 | 2026-03-28 | Addresses readability review feedback. |

---

## File Change Summary

| File | Change Type | Epic |
|------|-------------|------|
| `Directory.Packages.props` | Modified | 1 |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Modified | 1 |
| `src/Twig.Infrastructure/Content/MarkdownConverter.cs` | **New** (new folder) | 1 |
| `tests/Twig.Infrastructure.Tests/Content/MarkdownConverterTests.cs` | **New** (new folder) | 1 |
| `src/Twig/Commands/UpdateCommand.cs` | Modified | 2 |
| `src/Twig/Program.cs` | Modified | 2 |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Modified | 2 |

**Total**: 2 new files (+ 2 new folders), 5 modified files across 2 epics
