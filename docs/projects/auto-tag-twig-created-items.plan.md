# Auto-Tag All Twig-Created Work Items with 'twig' Tag

| Field | Value |
|-------|-------|
| **Status** | ✅ Done |
| **Work Item** | #1890 |
| **Author** | Copilot |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

All work items created by twig in Azure DevOps should automatically receive the
`twig` tag, making twig-managed items easily discoverable through ADO queries,
board filters, and audit trails. This design proposes injecting the tag at the
single funnel point where all creation payloads are built —
`AdoResponseMapper.MapSeedToCreatePayload()` — ensuring every creation path
(CLI `twig new`, MCP `twig_new`/`twig_find_or_create`, and seed publish) tags
items without any caller-side changes. The change is surgical: one method
modified, one helper added, and focused test coverage.

## Background

### Current Architecture

Twig creates work items in ADO through two logical flows that converge at a single
infrastructure method:

1. **Direct creation** (`twig new`, MCP `twig_new`, MCP `twig_find_or_create`):
   builds a seed `WorkItem` via `SeedFactory`, then calls
   `IAdoWorkItemService.CreateAsync(seed)`.

2. **Seed publish** (`twig seed publish`, `twig seed publish --all`): seeds are
   created locally first (`twig seed new`, `twig seed chain`), then published
   via `SeedPublishOrchestrator.PublishAsync()` which calls
   `IAdoWorkItemService.CreateAsync(seed)`.

Both flows arrive at `AdoRestClient.CreateAsync()`, which delegates payload
construction to `AdoResponseMapper.MapSeedToCreatePayload()`. This static method
builds a `List<AdoPatchOperation>` containing JSON Patch operations for Title,
AreaPath, IterationPath, parent relation, and all non-excluded seed fields.

### Call-Site Audit

All callers that result in ADO work item creation:

| File | Method | Usage | Impact |
|------|--------|-------|--------|
| `src/Twig/Commands/NewCommand.cs` | `ExecuteAsync` | Calls `adoService.CreateAsync(seed)` after `SeedFactory.CreateUnparented()` | Auto-tagged via mapper |
| `src/Twig.Domain/Services/SeedPublishOrchestrator.cs` | `PublishAsync` | Calls `_adoService.CreateAsync(seed)` for each seed | Auto-tagged via mapper |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `New` (unparented path) | Calls `ctx.AdoService.CreateAsync(seed)` | Auto-tagged via mapper |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `CreateParentedAsync` | Calls `ctx.AdoService.CreateAsync(seed)` | Auto-tagged via mapper |
| `src/Twig.Mcp/Tools/CreationTools.cs` | `FindOrCreate` | Delegates to `New()` | Auto-tagged via mapper |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | `CreateAsync` | Calls `MapSeedToCreatePayload()` → REST POST | **Change point** |

All paths funnel through `AdoRestClient.CreateAsync()` → `AdoResponseMapper.MapSeedToCreatePayload()`.
No caller-side changes needed.

### Local-Only Seed Creation (Not Affected)

| File | Method | Notes |
|------|--------|-------|
| `src/Twig/Commands/SeedNewCommand.cs` | `ExecuteAsync` | Saves locally only; tag applied when published |
| `src/Twig/Commands/SeedChainCommand.cs` | `ExecuteAsync` | Saves locally only; tag applied when published |

These create local seeds that are never sent to ADO until `twig seed publish`,
at which point they flow through `MapSeedToCreatePayload()` and receive the tag.

### Tag Handling in ADO

`System.Tags` is a semicolon-separated plainText field. ADO accepts it as a
standard field operation in JSON Patch documents:
```json
{ "op": "add", "path": "/fields/System.Tags", "value": "twig" }
```

The field is already imported during fetch (it's in `FieldImportFilter.DisplayWorthyReadOnlyRefs`)
and is NOT in `CreatePayloadExcludedFields`, meaning if a seed has `System.Tags`
set in its fields dictionary, it will be included in the payload.

## Problem Statement

Currently, twig-created work items are indistinguishable from manually-created
items in ADO. There is no automated way to:
- Filter ADO backlogs to show only twig-managed items
- Audit which items were created by twig vs. other tools
- Build WIQL queries scoped to twig-originated work items

## Goals and Non-Goals

### Goals

1. Every work item created by twig receives the `twig` tag automatically
2. The tag is applied at the infrastructure level — no caller changes required
3. If a seed already has user-specified tags, `twig` is merged (not replacing)
4. The tag is not duplicated if already present
5. Test coverage validates tag injection across creation scenarios

### Non-Goals

- Making the tag name configurable (hardcoded `"twig"` for now)
- Retroactively tagging existing twig-created items
- Tagging items that twig modifies but did not create (e.g., `twig update`)
- Adding tags to locally-stored seeds before publish

## Requirements

### Functional

1. `MapSeedToCreatePayload()` MUST include a `/fields/System.Tags` operation
   with value containing `twig` in every generated payload.
2. When the seed already has `System.Tags` in its `Fields` dictionary (user-set
   tags), the output MUST merge `twig` with existing tags using ADO's
   semicolon-separated format (e.g., `"frontend; twig"`).
3. If the seed's existing tags already contain `twig` (case-insensitive), no
   duplicate MUST be added.
4. The tag operation MUST appear exactly once in the payload (no duplicate
   `/fields/System.Tags` entries).

### Non-Functional

1. No additional HTTP calls or latency introduced.
2. No changes to the `IAdoWorkItemService` interface.
3. AOT/trim-safe — no reflection.

## Proposed Design

### Architecture Overview

The change is localized to the infrastructure layer's anti-corruption mapping:

```
  [Callers]                       [Infrastructure]
  ─────────                       ────────────────
  NewCommand ──┐
  CreationTools ──┤                AdoRestClient.CreateAsync()
  SeedPublishOrchestrator ──┘           │
                                        ▼
                              AdoResponseMapper.MapSeedToCreatePayload()
                                        │
                                        ▼
                              ┌─────────────────────────┐
                              │ 1. Title, Area, Iter ops │
                              │ 2. Parent relation op    │
                              │ 3. Seed fields loop      │
                              │ 4. ★ InjectTwigTag()     │◄── NEW
                              └─────────────────────────┘
                                        │
                                        ▼
                              List<AdoPatchOperation> → JSON → HTTP POST
```

### Key Components

#### 1. Tag Injection in `MapSeedToCreatePayload` (Modified)

After the existing fields loop, add tag injection logic:

```csharp
// After the seed.Fields loop, ensure 'twig' tag is present
InjectTwigTag(operations);
```

#### 2. `InjectTwigTag` Helper Method (New — private static)

A private static method in `AdoResponseMapper` that:
1. Searches `operations` for an existing `/fields/System.Tags` operation
2. If found: extracts the current value, merges `twig` tag, updates in place
3. If not found: adds a new operation with value `"twig"`

```csharp
private static void InjectTwigTag(List<AdoPatchOperation> operations)
{
    const string tagPath = "/fields/System.Tags";
    const string twigTag = "twig";

    var existingIndex = operations.FindIndex(op =>
        string.Equals(op.Path, tagPath, StringComparison.OrdinalIgnoreCase));

    if (existingIndex >= 0)
    {
        var current = operations[existingIndex].Value?.GetValue<string>() ?? "";
        operations[existingIndex].Value = JsonValue.Create(MergeTwigTag(current, twigTag));
    }
    else
    {
        operations.Add(new AdoPatchOperation
        {
            Op = "add",
            Path = tagPath,
            Value = JsonValue.Create(twigTag),
        });
    }
}
```

#### 3. `MergeTwigTag` Helper Method (New — internal static for testability)

Merges the `twig` tag into an existing semicolon-separated tag string:

```csharp
internal static string MergeTwigTag(string existingTags, string tag)
{
    if (string.IsNullOrWhiteSpace(existingTags))
        return tag;

    var tags = existingTags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    foreach (var t in tags)
    {
        if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            return existingTags; // already present
    }

    return $"{existingTags}; {tag}";
}
```

### Data Flow

1. Caller builds a seed `WorkItem` (with or without existing `System.Tags`)
2. `AdoRestClient.CreateAsync()` calls `MapSeedToCreatePayload(seed, orgUrl, parentId)`
3. `MapSeedToCreatePayload` builds patch operations:
   - Title, AreaPath, IterationPath (explicit fields)
   - Parent relation link (if parentId provided)
   - All non-excluded seed fields (loop — may include `System.Tags` if user-set)
4. **NEW**: `InjectTwigTag(operations)` runs post-loop:
   - If `/fields/System.Tags` already exists → merges `twig` into value
   - If not → adds new operation with value `"twig"`
5. Serialized to JSON → sent as HTTP POST

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Inject in `MapSeedToCreatePayload` (not `CreateAsync` or `SeedFactory`) | Single funnel point; all creation paths covered; keeps domain layer clean |
| In-place mutation of operations list | Avoids allocating a new list; the method already builds and returns the list |
| `internal static` for `MergeTwigTag` | Allows direct unit testing without HTTP mocking |
| Case-insensitive duplicate check | ADO tags are case-insensitive; prevents "twig" + "Twig" |
| Hardcoded `"twig"` constant | Matches the issue spec; configurability is a non-goal |

## Dependencies

- **No new external dependencies** — uses existing `System.Text.Json.Nodes` already imported
- **No new internal dependencies** — change is self-contained in `AdoResponseMapper`
- **No sequencing constraints** — can be implemented immediately

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | All changes are modifications to existing files |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | Add `InjectTwigTag()` call at end of `MapSeedToCreatePayload()`; add `InjectTwigTag` and `MergeTwigTag` helper methods |
| `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | Add tests for tag injection: no-existing-tags, merge-with-existing, no-duplicate, case-insensitive-dedup |

## ADO Work Item Structure

This is an Issue (#1890) — defining Tasks directly under it.

### Issue: Auto-tag all twig-created work items with 'twig' tag (#1890)

**Goal**: Every work item created by twig in ADO automatically receives the `twig` tag.

**Prerequisites**: None.

#### Tasks

| Task ID | Description | Files | Effort Estimate |
|---------|-------------|-------|-----------------|
| T1 | Add `MergeTwigTag` helper and `InjectTwigTag` to `AdoResponseMapper`; call from `MapSeedToCreatePayload` | `src/Twig.Infrastructure/Ado/AdoResponseMapper.cs` | ~30 LoC |
| T2 | Add unit tests for tag injection: baseline, merge, dedup, case-insensitive | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | ~80 LoC |
| T3 | Update existing `MapSeedToCreatePayload` tests that assert exact operation counts | `tests/Twig.Infrastructure.Tests/Ado/AdoResponseMapperTests.cs` | ~10 LoC |

**Acceptance Criteria**:
- [x] `MapSeedToCreatePayload` always produces a `/fields/System.Tags` op containing `twig`
- [x] Existing user-set tags are preserved and merged (semicolon-separated)
- [x] `twig` tag is not duplicated if already present
- [x] All existing tests pass
- [x] New tests cover: no tags → "twig"; existing tags → merged; already has "twig" → no dup

## PR Groups

| PG | Tasks | Type | Est. LoC | Est. Files | Description |
|----|-------|------|----------|------------|-------------|
| PG-1 | T1, T2, T3 | Deep | ~120 | 2 | Implementation + tests for auto-tagging in `AdoResponseMapper` |

**Execution order**: Single PR group — no dependencies.

**Classification**: **Deep** — small file count (2 files), focused logic change with
careful edge-case handling (tag merging, deduplication).

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should the tag name be configurable via `twig.yml`? | Low | The issue explicitly says `twig`; configurability can be added later if needed |
| 2 | Should existing twig-created items be retroactively tagged? | Low | Explicitly a non-goal per this design; could be a separate utility command |

## Completion

**Completed**: 2026-04-22

All three tasks were implemented in a single PR group (PG-1, PR #78):
- **T1 (AB#1915)**: Added `MergeTwigTag` internal static helper and `InjectTwigTag` private static method to `AdoResponseMapper`; called from `MapSeedToCreatePayload` post-loop.
- **T2 (AB#1916)**: Added comprehensive unit tests covering baseline tag injection, merge with existing tags, deduplication, and case-insensitive dedup.
- **T3 (AB#1917)**: Updated existing `MapSeedToCreatePayload` tests to account for the new `System.Tags` operation in patch payloads.

All acceptance criteria met. PR #78 merged to main.


