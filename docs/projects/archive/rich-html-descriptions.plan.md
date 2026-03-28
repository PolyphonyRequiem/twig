# Plan: Rich HTML Descriptions for ADO Work Items

> **Date**: 2026-03-28
> **Status**: Draft
> **ADO Epic**: #1281

---

## Executive Summary

Enable `twig update System.Description` to push rich HTML content to Azure DevOps, and add an optional `--format markdown` flag that converts Markdown input to HTML before sending. Today, description values are sent as plain strings — but since ADO's `System.Description` field is natively HTML-typed, HTML strings already render correctly when sent verbatim. The main deliverable is a Markdown-to-HTML conversion pipeline using **Markdig**, exposed via a `--format` flag on `twig update` and `twig new --description`. Plain-text descriptions continue to work unchanged.

---

## Background

### Current State

- `twig update System.Description "value"` creates a `FieldChange(field, null, value)` and sends it as a JSON Patch operation via `AdoRestClient.PatchAsync()`.
- The value is serialized as a JSON string inside `AdoPatchOperation.Value` via `JsonValue.Create(change.NewValue)`.
- ADO's `System.Description` field has data type `"html"` — it accepts and renders HTML content natively.
- **HTML pass-through already works**: Sending `<h1>Hello</h1><p>World</p>` via `twig update` renders correctly in the ADO web UI because the value is transmitted as-is.
- No Markdown-to-HTML conversion exists anywhere in the codebase.
- The `FormatterHelpers.FormatFieldValue()` method strips HTML tags for display using `SpectreRenderer.StripHtmlTags()`, confirming the codebase already anticipates HTML content in fields.
- `twig new --description` sets the description via `seed.SetField("System.Description", description)` — same raw-string pipeline.

### What This Solves

Users writing descriptions via twig (especially automated callers and AI agents) want structured content — headings, lists, bold/italic, code blocks — without hand-crafting HTML. A `--format markdown` flag provides this ergonomically:

```bash
# Before: manual HTML
twig update System.Description "<h2>Summary</h2><p>Fix the <strong>auth</strong> bug</p>"

# After: natural Markdown
twig update System.Description "## Summary\n\nFix the **auth** bug" --format markdown
```

---

## Problem Statement

While ADO accepts HTML in `System.Description`, twig provides no way to convert input from a more natural format (Markdown) to HTML. Users must either write raw HTML or accept plain-text descriptions that lack structure in the ADO web UI.

---

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G1 | Verify and document that HTML strings pass through to ADO correctly via `twig update` |
| G2 | Add `--format markdown` flag to `twig update` that converts Markdown to HTML via Markdig |
| G3 | Add `--format markdown` flag to `twig new --description` for the same conversion |
| G4 | Existing plain-text and HTML descriptions continue to work unchanged (backward compatible) |
| G5 | Markdig integration is AOT-compatible (`PublishAot=true`, `TrimMode=full`) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG1 | Embedded Mermaid diagram rendering (ADO doesn't render Mermaid natively; this is an ADO limitation, not a twig concern) |
| NG2 | Reading description content from files via `--file` flag (achievable via shell substitution: `$(cat file.md)` or `(Get-Content file.md -Raw)`) |
| NG3 | Reverse conversion (HTML to Markdown) for display purposes |
| NG4 | Adding `--format` to `twig edit` or `twig seed edit` (editor workflow already handles raw field values) |
| NG5 | Sanitizing user-provided HTML (ADO handles its own HTML sanitization) |

---

## Requirements

### Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-01 | `twig update System.Description "<b>html</b>"` sends the HTML value to ADO unchanged (existing behavior, verified) |
| FR-02 | `twig update System.Description "# Heading" --format markdown` converts Markdown to HTML before sending |
| FR-03 | `twig new --description "**bold**" --format markdown` converts the description to HTML before seed creation |
| FR-04 | Default behavior (no `--format` flag) is unchanged: value sent as-is |
| FR-05 | The `--format` flag accepts `markdown`; any other value produces a clear error |
| FR-06 | Markdig pipeline uses advanced extensions (tables, task lists, auto-links, pipe tables) for GFM-like conversion |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-01 | Native AOT compatible — Markdig must not produce trim/AOT warnings under `PublishAot=true` + `TreatWarningsAsErrors=true` |
| NFR-02 | No new serializable types (no changes to `TwigJsonContext`) |
| NFR-03 | Follows existing command pattern (sealed class, primary constructor, DI) |
| NFR-04 | Binary size increase from Markdig ≤ 500 KB (Markdig is ~300 KB) |

---

## Proposed Design

### Architecture Overview

```
CLI Layer                          Infrastructure Layer
┌─────────────────────┐           ┌─────────────────────────┐
│ UpdateCommand        │           │ MarkdownConverter       │
│  --format markdown ──┼──────────►│  .ToHtml(markdown)     │
│                      │           │  (Markdig pipeline)     │
│ NewCommand           │           │                         │
│  --format markdown ──┼──────────►│  static, cached pipeline│
└─────────────────────┘           └─────────────────────────┘
         │
         ▼
┌─────────────────────┐
│ AdoRestClient        │
│  .PatchAsync()       │  ← FieldChange.NewValue now contains HTML
│  (unchanged)         │
└─────────────────────┘
```

### Key Design Decision: Where Does Conversion Live?

The Markdown→HTML conversion is placed in `Twig.Infrastructure.Content.MarkdownConverter` as a **static utility class**:

- Wraps the third-party Markdig library (external dependency → Infrastructure)
- Statically caches the `MarkdownPipeline` for reuse (CLI is single-command, but pipeline build is non-trivial)
- Accessible from the CLI project via existing `InternalsVisibleTo` (or made `public`)
- No interface needed — this is a pure stateless function with no side effects

### Conversion Pipeline Configuration

```csharp
private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()   // tables, task lists, auto-links, etc.
    .Build();
```

`UseAdvancedExtensions()` enables: pipe tables, grid tables, extra emphasis (strikethrough, subscript, superscript), definition lists, footnotes, auto-identifiers, auto-links, task lists, and more. This provides GFM-like behavior that users expect.

### Command Flow (Updated)

```
twig update System.Description "## Summary\n\nFix the **auth** bug" --format markdown

1. Parse field="System.Description", value="## Summary\n\nFix...", format="markdown"
2. format == "markdown" → value = MarkdownConverter.ToHtml(value)
   → "<h2>Summary</h2>\n<p>Fix the <strong>auth</strong> bug</p>\n"
3. Create FieldChange("System.Description", null, htmlValue)
4. PatchAsync(id, changes, revision) → sends HTML to ADO
5. ADO renders the HTML natively in the web UI
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-01 | Static utility class, not DI service | Stateless pure function; no need for mock injection in command tests (mock the value transformation at the string level instead) |
| DD-02 | Markdig with `UseAdvancedExtensions()` | Provides GFM-like behavior (tables, task lists, auto-links) that users expect from Markdown |
| DD-03 | `--format` flag (not `--markdown` boolean) | Extensible pattern — allows future formats (e.g., `--format rst`) without accumulating boolean flags |
| DD-04 | Conversion in CLI command, not in `AdoRestClient` | The REST client should be format-agnostic; format conversion is a CLI presentation concern |
| DD-05 | No `--format html` value needed | Sending raw HTML already works with no flag; adding an explicit `html` format value adds no functionality |
| DD-06 | Place in Twig.Infrastructure, not Twig | Markdig is an external dependency; Infrastructure is the standard home for third-party library wrappers |

---

## Implementation Plan

### Epic 1: Markdig Integration and Markdown Converter (PR #1)

**Classification**: Deep (3 files changed, focused conversion logic + AOT verification)
**Estimated LoC**: ~150 new, ~30 modified
**Predecessor**: None

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T1 | Add `Markdig` package version to central package management | `Directory.Packages.props` | S |
| T2 | Add `Markdig` PackageReference to Infrastructure project | `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | S |
| T3 | Create `MarkdownConverter` static class with `ToHtml(string)` method. Cache `MarkdownPipeline` as static readonly. Use `UseAdvancedExtensions()`. | `src/Twig.Infrastructure/Content/MarkdownConverter.cs` (new) | M |
| T4 | Unit tests for `MarkdownConverter`: headings, bold/italic, lists, tables, code blocks, empty input, null-safety | `tests/Twig.Infrastructure.Tests/Content/MarkdownConverterTests.cs` (new) | M |
| T5 | AOT verification: run `dotnet publish` with `PublishAot=true` and confirm no trim warnings from Markdig | Build verification | M |

**Acceptance Criteria**:
- `MarkdownConverter.ToHtml("# Hello\n\n**World**")` returns `<h1>Hello</h1>\n<p><strong>World</strong></p>\n`
- `dotnet publish -c Release` succeeds with zero trim/AOT warnings
- All unit tests pass

---

### Epic 2: `--format` Flag on `twig update` and `twig new` (PR #2)

**Classification**: Deep (5 files changed, command plumbing + validation)
**Estimated LoC**: ~200 new, ~50 modified
**Predecessor**: Epic 1

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T6 | Add `string? format = null` parameter to `UpdateCommand.ExecuteAsync()`. When `format` is `"markdown"`, convert value via `MarkdownConverter.ToHtml()` before creating `FieldChange`. Return error for unrecognized values. | `src/Twig/Commands/UpdateCommand.cs` | M |
| T7 | Update `TwigCommands.Update()` route in Program.cs to pass `format` parameter through | `src/Twig/Program.cs` | S |
| T8 | Add `string? format = null` parameter to `NewCommand.ExecuteAsync()`. When `format` is `"markdown"`, convert `description` value before calling `seed.SetField()`. Return error for unrecognized values. | `src/Twig/Commands/NewCommand.cs` | M |
| T9 | Update `TwigCommands.New()` route in Program.cs to pass `format` parameter through | `src/Twig/Program.cs` | S |
| T10 | Unit tests for `UpdateCommand` with `--format markdown`: verify converted HTML is passed to PatchAsync | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | M |
| T11 | Unit tests for `UpdateCommand` with invalid `--format`: verify error return | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | S |
| T12 | Unit tests for `NewCommand` with `--format markdown` on description | `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | M |
| T13 | Update help text in Program.cs to document the `--format` flag | `src/Twig/Program.cs` | S |

**Acceptance Criteria**:
- `twig update System.Description "# Hello" --format markdown` sends `<h1>Hello</h1>` (HTML) to ADO
- `twig update System.Description "plain text"` continues to send plain text (no format flag = no conversion)
- `twig update System.Description "<b>html</b>"` continues to send HTML as-is (no format flag)
- `twig update System.Description "text" --format unknown` returns error code 2 with clear message
- `twig new --title "Test" --type Epic --description "**bold**" --format markdown` creates item with HTML description
- All existing tests continue to pass
- `dotnet test` passes across all test projects
---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Markdig produces trim/AOT warnings under `PublishAot=true` | Low | High | Markdig's AGENTS.md explicitly states AOT/trimmer-friendly guidelines. Verify with `dotnet publish` early (T5). If warnings appear, evaluate `<SuppressTrimAnalysisWarnings>` for Markdig only or consider minimal hand-rolled converter as fallback. |
| Markdig increases binary size significantly | Very Low | Low | Markdig is ~300 KB; AOT binary is already ~20+ MB. Marginal impact. |
| `--format` flag name conflicts with future flags | Low | Low | `--format` is intuitive and not used by any existing command. Other output format flags use `--output`. |
| Users expect Mermaid diagrams to render in ADO | Medium | Low | ADO does not natively render Mermaid. Out of scope per NG1. |
| Shell escaping of Markdown characters (*, #, etc.) causes issues | Medium | Medium | This is a standard shell concern, not a twig concern. Users must quote arguments properly. Document examples in help text showing proper quoting. |

---

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--format` accept `html` as an explicit pass-through value? | Low | Currently unnecessary since no-flag already sends as-is. Could add later for explicitness. |
| 2 | Should stdin/pipe input be supported for multi-line descriptions? | Low | Shell substitution (`$(cat file.md)`) covers this use case without code changes. Can revisit if user demand arises. |
| 3 | Should Markdig's output be sanitized before sending to ADO? | Low | ADO performs its own HTML sanitization. Double-sanitizing could break valid HTML. Trust ADO's sanitization. |

---

## File Change Summary

| File | Change Type | Epic |
|------|-------------|------|
| `Directory.Packages.props` | Modified (add Markdig version) | 1 |
| `src/Twig.Infrastructure/Twig.Infrastructure.csproj` | Modified (add PackageReference) | 1 |
| `src/Twig.Infrastructure/Content/MarkdownConverter.cs` | **New** | 1 |
| `tests/Twig.Infrastructure.Tests/Content/MarkdownConverterTests.cs` | **New** | 1 |
| `src/Twig/Commands/UpdateCommand.cs` | Modified (add format param) | 2 |
| `src/Twig/Commands/NewCommand.cs` | Modified (add format param) | 2 |
| `src/Twig/Program.cs` | Modified (route format param) | 2 |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Modified (add format tests) | 2 |
| `tests/Twig.Cli.Tests/Commands/NewCommandTests.cs` | Modified (add format tests) | 2 |

**Total**: 3 new files, 5 modified files (~280 LoC new + ~60 LoC modified ≈ 340 LoC total)
