---
work_item_id: 2017
title: "twig update --append: Append to Field Values"
type: Issue
---

# `twig update --append`: Append to Field Values

| Field | Value |
|-------|-------|
| **Work Item** | #2017 |
| **Type** | Issue |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

This plan adds an `--append` flag to `twig update` (CLI) and the `twig_update` MCP tool,
enabling callers to append text to an existing field value instead of replacing it. The
primary use case is the conductor workflow, which needs to add plan document links and
branch references to `System.Description` without clobbering existing content from the
planning phase. The implementation reads the current field value from the freshly-fetched
remote work item, concatenates the new value with an HTML-aware separator, and writes the
combined result back via the existing ADO PATCH path. When `--format markdown` is combined
with `--append`, only the appended portion is converted to HTML before concatenation.

## Background

### Current Architecture

The `UpdateCommand` (CLI) and `MutationTools.Update` (MCP) follow a pull-then-push pattern:

1. **Resolve active item** — `ActiveItemResolver` finds the work item from context or explicit `--id`
2. **Fetch remote** — `adoService.FetchAsync(id)` pulls the latest from ADO (used for conflict detection + revision)
3. **Conflict resolution** (CLI only) — `ConflictResolutionFlow.ResolveAsync` compares local vs remote
4. **Compute effective value** — apply `--format markdown` conversion if specified
5. **Build `FieldChange`** — `new FieldChange(field, null, effectiveValue)` (note: `OldValue` is always `null`)
6. **PATCH to ADO** — `ConflictRetryHelper.PatchWithRetryAsync` sends the change
7. **Post-patch** — auto-push pending notes, resync cache, update prompt state

The `FieldChange` record feeds into `AdoResponseMapper.MapPatchDocument`, which always
emits `"op": "replace"` operations. The ADO REST API does not support an "append" operation
natively — append must be implemented client-side by reading the current value and
concatenating before sending a `"replace"`.

### Key Code Paths

The `WorkItem` aggregate stores fields in an internal `Dictionary<string, string?>` exposed
via `Fields` (public `IReadOnlyDictionary`). After `FetchAsync`, the remote work item
contains the latest field values from ADO, including `System.Description` as HTML.

`MarkdownConverter.ToHtml` (Markdig) converts Markdown → HTML. It returns `string.Empty`
for null/empty input. The existing `--format markdown` flag applies conversion before
the PATCH — the same pattern works for append mode (convert the appended portion only).

### Call-Site Audit

The append feature modifies the update path in two call sites. Both are self-contained
and share no cross-cutting abstractions with other commands:

| File | Method | Current Usage | Impact |
|------|--------|---------------|--------|
| `src/Twig/Commands/UpdateCommand.cs` | `ExecuteAsync` | Replaces field value; builds `FieldChange(field, null, effectiveValue)` | Add `--append` parameter; read existing value from `remote.Fields`; concatenate |
| `src/Twig.Mcp/Tools/MutationTools.cs` | `Update` | Same replace pattern via `FieldChange` | Add `append` boolean parameter; same read-then-concat logic |

No other callers are affected. `BatchCommand` has its own `set_field` path but does not
use `UpdateCommand` — `--append` is out of scope for batch mode.

## Problem Statement

Today, `twig update System.Description "..."` always replaces the entire field value. The
conductor workflow seeds work items with description content during planning, then needs to
append plan document links and branch references during implementation. This forces callers
to either manually read the existing value before updating, or accept that the description
will be overwritten. Neither option is acceptable for automated workflows.

## Goals and Non-Goals

### Goals

1. **Append mode for CLI** — `twig update <field> <value> --append` reads the current value and concatenates
2. **Append mode for MCP** — `twig_update` gains an `append` boolean parameter with the same behavior
3. **HTML-aware concatenation** — when appending to HTML fields (detected by presence of HTML tags), use `<br><br>` separator; otherwise use `\n\n`
4. **Markdown interop** — `--format markdown --append` converts the appended portion to HTML, then concatenates with existing HTML content
5. **Graceful empty handling** — when the existing value is null or empty, `--append` degrades to a simple set (no separator prepended)
6. **Comprehensive tests** — unit tests for both CLI and MCP paths covering append, append+markdown, empty field, and HTML detection

### Non-Goals

- **Batch command support** — `twig batch` does not support `--append` in this iteration
- **Prepend mode** — only append is supported; prepend is not requested
- **Separator customization** — the separator is fixed (HTML: `<br><br>`, plain: `\n\n`)
- **Field-type introspection from ADO** — we detect HTML by content inspection, not by querying ADO field metadata

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `twig update <field> <value> --append` appends `<value>` to the current field value |
| FR-2 | `twig update <field> <value> --append --format markdown` converts `<value>` to HTML, then appends to existing HTML |
| FR-3 | When the existing field value is null or empty, `--append` sets the field as if `--append` were not specified |
| FR-4 | The separator between existing and appended content is `<br><br>` for HTML content, `\n\n` for plain text |
| FR-5 | HTML detection uses a simple heuristic: existing value contains `<` and `>` |
| FR-6 | The `twig_update` MCP tool accepts an `append` boolean parameter with identical semantics |
| FR-7 | `--append` works with all value sources: inline, `--file`, and `--stdin` |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | No additional ADO API calls — the existing `FetchAsync` already provides the remote field values |
| NFR-2 | AOT-compatible — no reflection or dynamic types |
| NFR-3 | No new NuGet dependencies |

## Proposed Design

### Architecture Overview

The append feature is a thin layer inserted into the existing update data flow, between
value resolution (step 4) and `FieldChange` construction (step 5). No new services,
interfaces, or abstractions are needed.

```
┌──────────────────────────────────────────────────────────┐
│ UpdateCommand / MutationTools.Update                     │
│                                                          │
│  1. Resolve value (inline / file / stdin)                 │
│  2. Apply --format markdown (if set) → effectiveValue    │
│  3. ── NEW: if --append ──                                │
│     a. Read remote.Fields[field] (already fetched)       │
│     b. Compute separator (HTML or plain text)            │
│     c. Concatenate: existingValue + separator + newValue │
│     d. effectiveValue = combined                         │
│  4. Build FieldChange(field, null, effectiveValue)       │
│  5. PatchWithRetryAsync (unchanged)                      │
└──────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `FieldAppender` — Static Helper (`Twig.Infrastructure/Content/FieldAppender.cs`)

A small static class encapsulating the append logic, keeping `UpdateCommand` and
`MutationTools` focused on orchestration:

```csharp
internal static class FieldAppender
{
    /// <summary>
    /// Appends <paramref name="newValue"/> to <paramref name="existingValue"/>
    /// with an appropriate separator. Returns <paramref name="newValue"/> unchanged
    /// when <paramref name="existingValue"/> is null or whitespace-only.
    /// </summary>
    public static string Append(string? existingValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(existingValue))
            return newValue;

        var separator = LooksLikeHtml(existingValue) ? "<br><br>" : "\n\n";
        return string.Concat(existingValue, separator, newValue);
    }

    internal static bool LooksLikeHtml(string value)
        => value.Contains('<') && value.Contains('>');
}
```

**Design rationale**: Extracting to a static helper with `internal` visibility allows
thorough unit testing of the concatenation logic, HTML detection, and edge cases without
needing the full command orchestration. The helper lives in `Twig.Infrastructure/Content/`
alongside `MarkdownConverter` because both are content-transformation utilities.

#### 2. `UpdateCommand` Changes

Add `bool append = false` parameter to `ExecuteAsync`. After computing `effectiveValue`
(line 81) and after fetching `remote` (line 94), insert the append logic:

```csharp
if (append)
{
    remote.Fields.TryGetValue(field, out var existingValue);
    effectiveValue = FieldAppender.Append(existingValue, effectiveValue);
}
```

The critical ordering insight: `--format markdown` converts the appended portion *before*
concatenation with existing HTML. This is correct because `System.Description` values in
ADO are already HTML, and the appended markdown-converted HTML should be concatenated as
HTML, not as raw markdown.

#### 3. `MutationTools.Update` Changes

Add `bool append = false` parameter with MCP description. Same logic as CLI: after markdown
conversion and after fetching remote, call `FieldAppender.Append`.

#### 4. `Program.cs` Changes

Add the `--append` parameter to the `Update` method signature in the ConsoleAppFramework
command registration, threading it through to `UpdateCommand.ExecuteAsync`.

### Data Flow — Append Mode

```
Input: field="System.Description", value="## Plan\n[plan.md](link)", format="markdown", append=true

1. resolvedValue = "## Plan\n[plan.md](link)"
2. effectiveValue = MarkdownConverter.ToHtml(resolvedValue)
   → "<h2>Plan</h2>\n<p><a href=\"link\">plan.md</a></p>\n"
3. remote = await adoService.FetchAsync(id)
4. existingValue = remote.Fields["System.Description"]
   → "<p>This work item implements feature X.</p>"
5. FieldAppender.LooksLikeHtml(existingValue) → true → separator = "<br><br>"
6. effectiveValue = existingValue + "<br><br>" + effectiveValue
   → "<p>This work item implements feature X.</p><br><br><h2>Plan</h2>\n<p><a href=\"link\">plan.md</a></p>\n"
7. FieldChange("System.Description", null, effectiveValue) → PATCH to ADO
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Append logic location | Static helper `FieldAppender` | Testable in isolation; keeps command classes focused on orchestration |
| HTML detection | Content-based heuristic (`<` and `>`) | Simple, zero-cost, correct for ADO fields. Field metadata would require an extra API call or schema dependency |
| Separator | `<br><br>` for HTML, `\n\n` for text | Visual paragraph break in both rendering contexts |
| Markdown conversion order | Convert before append | The appended text joins existing HTML as HTML, not raw markdown |
| Source of existing value | `remote.Fields` (from `FetchAsync`) | Already fetched for conflict detection — zero additional API calls |
| Placement in code | After markdown conversion, after remote fetch | Both `effectiveValue` and `remote` must be available |

## Dependencies

### External Dependencies
- None new. Existing: Markdig (markdown conversion), ModelContextProtocol.Server (MCP tools)

### Internal Dependencies
- `FieldAppender` depends on: nothing (pure string manipulation)
- `UpdateCommand` already depends on: `IAdoWorkItemService.FetchAsync` (provides `remote.Fields`)
- `MutationTools.Update` already depends on: same

### Sequencing Constraints
- `FieldAppender` must be created before CLI/MCP changes (it's used by both)
- CLI and MCP changes can proceed in parallel after `FieldAppender` exists
- Tests should be written alongside each component

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `--append` + `--format markdown` produce `<hr>` separator instead of `<br><br>` for visual distinction? | Low | `<br><br>` matches the plain-text `\n\n` semantics. A horizontal rule would be more visually distinct but adds opinionation. Starting with `<br><br>` and adjusting based on feedback is safe. |
| 2 | Should we guard against double-appending the same content? | Low | Out of scope for v1. Callers (conductor workflow) are responsible for idempotency. Adding dedup logic would require content hashing and adds complexity with minimal benefit. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Content/FieldAppender.cs` | Static helper for HTML-aware field value concatenation |
| `tests/Twig.Infrastructure.Tests/Content/FieldAppenderTests.cs` | Unit tests for `FieldAppender` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/UpdateCommand.cs` | Add `bool append` parameter; call `FieldAppender.Append` when set |
| `src/Twig/Program.cs` | Add `--append` parameter to `Update` command registration |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add `bool append` parameter to `Update` tool; call `FieldAppender.Append` |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | Add tests for `--append` with/without `--format markdown`, empty field |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsUpdateTests.cs` | Add tests for `append: true` with/without `format: "markdown"`, empty field |

## ADO Work Item Structure

### Issue #2021: Implement --append flag for twig update

**Goal**: Add `--append` flag to CLI and MCP `twig_update` with HTML-aware concatenation,
markdown interop, and comprehensive tests.

**Prerequisites**: None (standalone issue)

#### Tasks

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| T1 | Create `FieldAppender` static helper with `Append()` and `LooksLikeHtml()` methods | `src/Twig.Infrastructure/Content/FieldAppender.cs` | ~25 LoC |
| T2 | Add unit tests for `FieldAppender`: append to HTML, append to plain text, empty/null existing, HTML detection edge cases | `tests/Twig.Infrastructure.Tests/Content/FieldAppenderTests.cs` | ~80 LoC |
| T3 | Add `--append` flag to `UpdateCommand.ExecuteAsync` and `Program.cs` command registration | `src/Twig/Commands/UpdateCommand.cs`, `src/Twig/Program.cs` | ~15 LoC |
| T4 | Add CLI unit tests for `--append`: basic append, append+markdown, empty field, success message | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | ~100 LoC |
| T5 | Add `append` boolean parameter to `MutationTools.Update` MCP tool | `src/Twig.Mcp/Tools/MutationTools.cs` | ~15 LoC |
| T6 | Add MCP unit tests for `append`: basic append, append+markdown, empty field | `tests/Twig.Mcp.Tests/Tools/MutationToolsUpdateTests.cs` | ~100 LoC |

**Acceptance Criteria**
- [ ] `twig update System.Description "new text" --append` appends to existing value
- [ ] `twig update System.Description "# Heading" --append --format markdown` converts markdown then appends as HTML
- [ ] Appending to null/empty field behaves like a normal set (no leading separator)
- [ ] HTML fields get `<br><br>` separator; plain text fields get `\n\n`
- [ ] `twig_update` MCP tool accepts `append: true` with identical behavior
- [ ] All new tests pass
- [ ] Build succeeds with `TreatWarningsAsErrors=true`

## PR Groups

### PG-1: Core implementation and tests

**Type**: Deep (few files, moderate complexity)
**Scope**: All tasks (T1–T6)
**Estimated LoC**: ~335

| Task | Issue | Files |
|------|-------|-------|
| T1 | #2021 | `src/Twig.Infrastructure/Content/FieldAppender.cs` |
| T2 | #2021 | `tests/Twig.Infrastructure.Tests/Content/FieldAppenderTests.cs` |
| T3 | #2021 | `src/Twig/Commands/UpdateCommand.cs`, `src/Twig/Program.cs` |
| T4 | #2021 | `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` |
| T5 | #2021 | `src/Twig.Mcp/Tools/MutationTools.cs` |
| T6 | #2021 | `tests/Twig.Mcp.Tests/Tools/MutationToolsUpdateTests.cs` |

**Rationale**: This is a small, cohesive feature (~335 LoC, 7 files) that fits comfortably
in a single PR. The helper, CLI, and MCP changes are tightly coupled and should be reviewed
together to verify consistent behavior. Splitting into multiple PRs would create artificial
dependency ordering without improving reviewability.

**Execution order**: T1 → T2 → T3 → T4 → T5 → T6 (sequential within the PR)

