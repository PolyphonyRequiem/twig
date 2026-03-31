# Increase MaxDescriptionLines from 15 to 30

**Work Item:** AB#1327
**Type:** Issue
**Status:** 🔨 In Progress
**Author:** Copilot
**Date:** 2026-03-31

---

## Executive Summary

Increase the `MaxDescriptionLines` constant in `FormatterHelpers.cs` from 15 to 30. This
doubles the number of description lines shown in `twig status` output before truncation,
allowing longer work item descriptions to display more fully in the terminal. The change
is a single constant update plus test adjustments — no architectural changes required.

## Background & Current Architecture

### Current Behavior

`FormatterHelpers.HtmlToPlainText()` converts ADO HTML descriptions to plain text for
terminal display. After stripping tags, decoding entities, and normalizing lines, it
truncates output at `MaxDescriptionLines` (currently 15) and appends a
`(+N more lines)` indicator.

### Relevant Files

| File | Role |
|------|------|
| `src/Twig/Formatters/FormatterHelpers.cs` | Contains `MaxDescriptionLines` constant and `TruncateLines()` logic |
| `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | Unit tests for `HtmlToPlainText` truncation behavior |
| `tests/Twig.Cli.Tests/Rendering/BuildStatusViewDescriptionTests.cs` | Integration tests for description rendering in status view |

## Implementation Plan

### Tasks

| Task | File | Description | Est. LoC |
|------|------|-------------|----------|
| T1 | `src/Twig/Formatters/FormatterHelpers.cs` | Change `private const int MaxDescriptionLines = 15;` → `30` | 1 |
| T2 | `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | In `HtmlToPlainText_TruncatesAtMaxLines`: change loop from 20→35 paragraphs, update assertions (`+5 more lines`, `Line 30`, `ShouldNotContain("Line 31")`) | ~8 |
| T3 | `tests/Twig.Cli.Tests/Formatters/FormatterHelpersTests.cs` | In `HtmlToPlainText_ExactlyMaxLines_NoTruncation`: change loop from 15→30 paragraphs, update assertion to check `Line 30` | ~4 |
| T4 | `tests/Twig.Cli.Tests/Rendering/BuildStatusViewDescriptionTests.cs` | In `BuildStatusViewAsync_LongDescription_TruncatedWithIndicator`: change loop from 20→35 paragraphs, update comment referencing MaxDescriptionLines | ~4 |

### Acceptance Criteria

- [ ] `MaxDescriptionLines` is 30 in `FormatterHelpers.cs`
- [ ] `HtmlToPlainText` with ≤30 lines returns all lines (no truncation marker)
- [ ] `HtmlToPlainText` with >30 lines returns first 30 + `(+N more lines)`
- [ ] All existing tests pass with updated thresholds
- [ ] `dotnet test` passes with zero failures
- [ ] No other behavior changes

---
