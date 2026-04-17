# MCP Error Handling & ADO HTML Response Guard

> **Status**: 🔨 In Progress
> **Work Item**: [#1752](https://dev.azure.com/dangreen-msft/Twig/_workitems/edit/1752) — MCP swallows twig_state errors; ADO HTML response misparsed as JSON on sync/flush
> **Type**: Issue
> **Revision**: 0 (initial draft)

---

## Executive Summary

Two related reliability bugs in twig 0.42.3 create a poor debugging experience for MCP callers and a catastrophic failure mode when ADO returns non-JSON responses. Bug 1: `twig_state` and `twig_update` MCP tools let `AdoException` subtypes propagate unhandled to the MCP framework, which returns a generic error with no diagnostic detail. Bug 2: When ADO returns an HTML page (auth challenge, rate-limit interstitial, corporate proxy, or error page) instead of JSON, the HTTP client passes it through to `System.Text.Json`, which fails with a cryptic `'<' is an invalid start of a value` error — and if this happens during a flush of pending changes, the only recovery is manual SQLite surgery.

This plan adds four capabilities: (1) an `AdoUnexpectedResponseException` that catches non-JSON 2xx responses at the HTTP layer, (2) Content-Type validation in `AdoErrorHandler.ThrowOnErrorAsync`, (3) structured `AdoException` error handling in the MCP mutation tools, and (4) a new `twig_discard` MCP tool to clear poisoned pending changes without database surgery. All changes are covered by unit tests.

## Background

### Current Architecture

The ADO HTTP pipeline has three client classes that share a common error-handling path:

| Client | File | Responsibility |
|--------|------|----------------|
| `AdoRestClient` | `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Work item CRUD, WIQL, comments, links |
| `AdoIterationService` | `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | Iterations, teams, process config, field definitions |
| `AdoGitClient` | `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | Pull request queries |

All three call `AdoErrorHandler.ThrowOnErrorAsync(response, url, ct)` after `HttpClient.SendAsync()`. This method:
1. Returns immediately if `response.IsSuccessStatusCode` (2xx)
2. Maps non-2xx status codes to typed `AdoException` subclasses

**The gap**: Step 1 trusts that all 2xx responses contain JSON. It does not inspect `Content-Type`. When ADO (or an intermediate proxy/auth gateway) returns HTML with a 200 OK status, the response flows into `JsonSerializer.Deserialize()`, which throws `JsonException` with the cryptic `'<' is an invalid start of a value` message. This is not caught by any `AdoException` handler because `JsonException` is not in that hierarchy.

### Exception Hierarchy (current)

```
AdoException : Exception
├── AdoOfflineException (sealed)     — network unreachable
├── AdoBadRequestException (sealed)  — HTTP 400
├── AdoAuthenticationException (sealed) — HTTP 401
├── AdoNotFoundException (sealed)    — HTTP 404
├── AdoConflictException (sealed)    — HTTP 412
├── AdoRateLimitException (sealed)   — HTTP 429
└── AdoServerException (sealed)      — HTTP 5xx
```

After this change, a new sibling is added:
```
├── AdoUnexpectedResponseException (sealed) — 2xx with non-JSON Content-Type
```

### Call-Site Audit: `ThrowOnErrorAsync`

| File | Method | Line | Current Usage | Impact |
|------|--------|------|---------------|--------|
| `AdoRestClient.cs` | `SendAsync` | 316 | Validates response, returns on success | Gains Content-Type guard — protects all work item CRUD |
| `AdoIterationService.cs` | `SendAsync` | 344 | Validates response, returns on success | Gains Content-Type guard — protects iteration/process queries |
| `AdoGitClient.cs` | `SendAsync` | 186 | Validates response, returns on success | Gains Content-Type guard — protects PR queries |

All three callers use the identical pattern: `await ThrowOnErrorAsync(response, url, ct); return response;`. The change to `ThrowOnErrorAsync` automatically protects all three clients.

### Call-Site Audit: `AdoException` catch in MCP Tools

| File | Method | Line | Current Catch | Impact |
|------|--------|------|---------------|--------|
| `MutationTools.cs` | `State()` | 70–86 | No `AdoException` catch on fetch/patch; resync has generic `catch (Exception)` | Must wrap fetch+patch in `catch (AdoException)` |
| `MutationTools.cs` | `Update()` | 120–129 | `catch (AdoConflictException)` only on patch | Must widen to `catch (AdoException)` and include fetch |
| `MutationTools.cs` | `State()` | 74 | `AutoPushNotesHelper` not wrapped | Must add best-effort catch |
| `MutationTools.cs` | `Update()` | 131 | `AutoPushNotesHelper` not wrapped | Must add best-effort catch |
| `MutationTools.cs` | `State()` | 88 | `promptStateWriter` not wrapped | Must add best-effort catch |
| `MutationTools.cs` | `Update()` | 136 | `promptStateWriter` not wrapped | Must add best-effort catch |

### Pending Change Recovery (current)

When a flush operation fails mid-stream (e.g., `JsonException` from HTML response), the pending change row remains in `pending_changes` SQLite table, and `is_dirty` stays set on the work item. Every subsequent twig command that calls `FlushAllAsync` re-attempts the poisoned change and fails again. The only current recovery is:

1. Open `.twig/{org}/{project}/twig.db` with a SQLite client
2. `DELETE FROM pending_changes WHERE work_item_id = ?`
3. `UPDATE work_items SET is_dirty = 0 WHERE id = ?`

The new `twig_discard` tool automates this via `IPendingChangeStore.ClearChangesAsync()` and `IWorkItemRepository.ClearDirtyFlagAsync()`.

## Problem Statement

1. **Error swallowing in MCP tools**: When `adoService.FetchAsync()` or `ConflictRetryHelper.PatchWithRetryAsync()` throws any `AdoException` other than `AdoConflictException` (in `Update`) or no catch at all (in `State`), the exception propagates to the MCP framework, which returns a generic `"An error occurred invoking 'twig_state'"` message with no diagnostic information. Callers (AI agents, VS Code Copilot) cannot determine whether the issue is auth failure, rate limiting, server error, or HTML interception.

2. **HTML response misparse**: ADO occasionally returns HTML instead of JSON for 2xx responses — common scenarios include corporate proxy auth challenges, session expiry redirects, and Azure Front Door error pages. The current pipeline does not validate `Content-Type` on success responses, causing `JsonException` deep in deserialization code. This manifests as the cryptic `'<' is an invalid start` error.

3. **No recovery path for poisoned pending changes**: When the above failure occurs during a pending change flush, the user is stuck in a loop where every command re-tries the failed change. The only escape is manual SQLite surgery — unacceptable for a CLI tool.

## Goals and Non-Goals

### Goals

1. Add `AdoUnexpectedResponseException` to the exception hierarchy for non-JSON 2xx responses
2. Add Content-Type validation to `ThrowOnErrorAsync` that detects HTML/text responses and throws a descriptive error before JSON deserialization is attempted
3. Surface structured `AdoException` errors from `twig_state` and `twig_update` MCP tools with `IsError=true` and the exception message
4. Add `twig_discard` MCP tool to clear poisoned pending changes for a specific work item
5. Wrap post-mutation side-effects (`AutoPushNotesHelper`, `promptStateWriter`) in best-effort catches so they never fail the primary operation
6. Cover all changes with unit tests

### Non-Goals

- **Retry logic for HTML responses**: We detect and report — we do not auto-retry with re-authentication
- **Automatic pending change recovery**: `twig_discard` is manual/explicit — no auto-discard on failure
- **CLI command error handling**: Only MCP tools are in scope; CLI commands already have their own error handling
- **`twig_note` error handling changes**: `Note()` already has a comprehensive try/catch that stages locally on failure
- **`twig_sync` error handling changes**: `Sync()` already uses `McpPendingChangeFlusher.FlushAllAsync()` which returns structured failure summaries

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | `AdoUnexpectedResponseException` sealed class with `StatusCode`, `ContentType`, `RequestUrl`, `BodySnippet` properties |
| FR-2 | `ThrowOnErrorAsync` checks `Content-Type` after the `IsSuccessStatusCode` early return; throws `AdoUnexpectedResponseException` for non-JSON media types |
| FR-3 | Missing `Content-Type` header passes through (e.g., 204 No Content) |
| FR-4 | Empty body with non-JSON `Content-Type` passes through |
| FR-5 | `twig_state` catches `AdoException` on fetch+patch and returns `IsError=true` with the exception message |
| FR-6 | `twig_update` catches `AdoException` (widened from `AdoConflictException`) on fetch+patch and returns `IsError=true` with the exception message |
| FR-7 | Post-mutation operations (notes push, prompt state write) wrapped in best-effort catches in both `State()` and `Update()` |
| FR-8 | `twig_discard` tool accepts optional `id` parameter, defaults to active work item, clears pending changes and dirty flag |
| FR-9 | `twig_discard` returns change summary (notes and field edits discarded) |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF-1 | All new types are `sealed` (AOT compatibility) |
| NF-2 | No new NuGet dependencies |
| NF-3 | No reflection-based code (AOT-safe) |
| NF-4 | Compiles with `TreatWarningsAsErrors=true` |
| NF-5 | Body snippet in `AdoUnexpectedResponseException` truncated to 500 chars max (prevent memory bloat from large HTML pages) |

## Proposed Design

### Architecture Overview

The change touches two layers:

```
┌─────────────────────────────────────────────────────┐
│  MCP Layer (Twig.Mcp)                               │
│  ┌─────────────────────────────────────────────────┐│
│  │ MutationTools                                   ││
│  │  State()  — add catch(AdoException)             ││
│  │  Update() — widen catch to AdoException          ││
│  │  Discard() — NEW tool                            ││
│  └─────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────┐
│  Infrastructure Layer (Twig.Infrastructure)         │
│  ┌─────────────────────────────────────────────────┐│
│  │ AdoErrorHandler.ThrowOnErrorAsync()             ││
│  │  + Content-Type validation after 2xx check      ││
│  └─────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────┐│
│  │ AdoExceptions.cs                                ││
│  │  + AdoUnexpectedResponseException (sealed)      ││
│  └─────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────┘
```

### Key Components

#### 1. `AdoUnexpectedResponseException` (new)

```csharp
/// <summary>
/// Thrown when ADO returns a 2xx response with a non-JSON Content-Type.
/// Common causes: auth challenge page, corporate proxy, Azure Front Door error.
/// </summary>
public sealed class AdoUnexpectedResponseException : AdoException
{
    public int StatusCode { get; }
    public string ContentType { get; }
    public string RequestUrl { get; }
    public string BodySnippet { get; }

    public AdoUnexpectedResponseException(int statusCode, string contentType, string requestUrl, string bodySnippet)
        : base($"ADO returned non-JSON response (HTTP {statusCode}, Content-Type: {contentType}). URL: {requestUrl}. Body: {bodySnippet}")
    {
        StatusCode = statusCode;
        ContentType = contentType;
        RequestUrl = requestUrl;
        BodySnippet = bodySnippet;
    }
}
```

**Design decisions:**
- Inherits `AdoException` so existing `catch (AdoException)` blocks catch it
- All properties are `string`/`int` — no `HttpStatusCode` enum to avoid System.Net dependency in domain consumers
- Body snippet is passed in pre-truncated by the caller (ThrowOnErrorAsync truncates to 500 chars)

#### 2. Content-Type Validation in `ThrowOnErrorAsync`

The validation is inserted immediately after the existing `IsSuccessStatusCode` early return:

```csharp
if (response.IsSuccessStatusCode)
{
    // Guard: non-JSON Content-Type on success responses
    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (mediaType is not null
        && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            var snippet = body.Length > 500 ? body[..500] : body;
            throw new AdoUnexpectedResponseException(
                (int)response.StatusCode, mediaType, requestUrl, snippet);
        }
    }
    return;
}
```

**Guard chain:**
1. `mediaType is not null` — allows responses with no Content-Type (e.g., 204 No Content, certain batch responses)
2. `!mediaType.Contains("json")` — passes `application/json`, `application/json; charset=utf-8`, and any future JSON subtypes
3. `!string.IsNullOrWhiteSpace(body)` — allows empty bodies with mismatched Content-Type (harmless edge case)

#### 3. MCP Error Handling in `State()` and `Update()`

**`State()` — before:**
```csharp
var remote = await adoService.FetchAsync(item.Id, ct);
var changes = new[] { new FieldChange("System.State", item.State, newState) };
await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);

await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);
// resync block...
await promptStateWriter.WritePromptStateAsync();
```

**`State()` — after:**
```csharp
Domain.Aggregates.WorkItem remote;
try
{
    remote = await adoService.FetchAsync(item.Id, ct);
    var changes = new[] { new FieldChange("System.State", item.State, newState) };
    await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);
}
catch (AdoException ex)
{
    return McpResultBuilder.ToError(ex.Message);
}

// Best-effort: push notes
try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService); }
catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

// Resync cache — best-effort, non-fatal (existing)
// ...

// Best-effort: prompt state
try { await promptStateWriter.WritePromptStateAsync(); }
catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
```

**`Update()` — same pattern:** Widen `catch (AdoConflictException)` to `catch (AdoException)`, include `FetchAsync` in the try block, wrap post-mutation ops in best-effort catches.

#### 4. `twig_discard` MCP Tool

New method on `MutationTools`:

```csharp
[McpServerTool(Name = "twig_discard"), Description("Discard pending changes for a work item")]
public async Task<CallToolResult> Discard(
    [Description("Work item ID to discard changes for (defaults to active item)")] int? id = null,
    CancellationToken ct = default)
```

**Flow:**
1. Resolve target: `id` if provided, else active item via `activeItemResolver`
2. Validate item exists in cache via `workItemRepo.GetByIdAsync`
3. Get change summary via `pendingChangeStore.GetChangeSummaryAsync`
4. If no changes → return success with "no pending changes" message
5. Clear changes via `pendingChangeStore.ClearChangesAsync`
6. Clear dirty flag via `workItemRepo.ClearDirtyFlagAsync`
7. Update prompt state (best-effort)
8. Return structured result with discarded counts

### Data Flow

```
ADO HTTP Response (200 OK, text/html)
  → HttpClient.SendAsync()
  → AdoErrorHandler.ThrowOnErrorAsync()
    → IsSuccessStatusCode? Yes
    → Content-Type = "text/html"? Yes
    → Body non-empty? Yes
    → throw AdoUnexpectedResponseException(200, "text/html", url, snippet)
  → [propagates to caller]
  → MutationTools.State() catch (AdoException)
  → McpResultBuilder.ToError(ex.Message)
  → MCP response: { isError: true, content: "ADO returned non-JSON response..." }
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| Content-Type check uses `Contains("json")` not exact match | Handles `application/json`, `application/json; charset=utf-8`, `application/hal+json`, etc. |
| Empty body with wrong Content-Type passes through | Pragmatic: no actual data to misparse, and some ADO endpoints return empty 200s with text/plain |
| `twig_discard` is per-item, no `--all` flag | Prevents accidental bulk discard; users should be intentional about which item to recover |
| Best-effort wrapping uses `catch (Exception ex) when (ex is not OperationCanceledException)` | Matches existing pattern in `Note()` and resync block; cancellation must always propagate |
| `AdoUnexpectedResponseException` takes `int statusCode` not `HttpStatusCode` enum | Avoids coupling domain/MCP consumers to `System.Net`; consistent with `AdoServerException` pattern |

## Dependencies

### External
- No new NuGet packages required

### Internal
- `AdoUnexpectedResponseException` must be defined before `AdoErrorHandler` can reference it (same file, no real sequencing issue)
- `AdoErrorHandler` changes must be in place before MCP error handling tests can verify the full pipeline

### Sequencing
- Issues #1768 → #1769 → #1770 (exception → handler → handler tests) must be sequential
- Issues #1771, #1772 (MCP error handling) can proceed after #1768 is complete
- Issue #1773 (discard tool) is independent of the error handling chain
- Test issues (#1774, #1775, #1776) depend on their corresponding implementation issues

## Impact Analysis

### Components Affected

| Component | Change Type | Risk |
|-----------|-------------|------|
| `AdoExceptions.cs` | Add class | None — additive |
| `AdoErrorHandler.cs` | Add validation after existing check | Low — new code path only fires on non-JSON 2xx |
| `MutationTools.cs` | Widen catches, add tool | Medium — changes error behavior for all ADO failures |
| `AdoErrorHandlerTests.cs` | Add tests, update helpers | None — test-only |
| `MutationToolsStateTests.cs` | Add tests | None — test-only |
| `MutationToolsUpdateTests.cs` | Add tests | None — test-only |
| `MutationToolsDiscardTests.cs` | New file | None — test-only |

### Backward Compatibility

- **Breaking change?** No. `AdoUnexpectedResponseException` inherits `AdoException`, so any code catching `AdoException` already handles it.
- **Behavioral change:** Previously, HTML responses on 2xx caused `JsonException` downstream. Now they cause `AdoUnexpectedResponseException` at the HTTP layer. This is a *better* error, not a regression.
- **MCP tool behavior change:** `twig_state` and `twig_update` now return `IsError=true` with structured messages for ADO failures instead of propagating generic MCP errors. This is the desired fix.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Content-Type check rejects legitimate non-JSON success responses | Low | Medium | Guard chain: null Content-Type passes, empty body passes, only non-empty non-JSON throws |
| Widening catch to `AdoException` masks bugs that should propagate | Low | Low | All caught exceptions are surfaced via `McpResultBuilder.ToError(ex.Message)` — nothing is silently swallowed |
| `twig_discard` clears changes user didn't intend to discard | Low | Medium | Per-item scoped (no `--all`), returns change summary before clearing |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should `twig_discard` require a confirmation parameter (like `force`)? | Low | Current design discards immediately. Per-item scope limits blast radius. Can add later if needed. |
| 2 | Should `McpResultBuilder` gain a `FormatDiscard()` method or is inline JSON sufficient? | Low | Inline `BuildJson()` is consistent with how `Note()` returns ad-hoc JSON. Can refactor later. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `tests/Twig.Mcp.Tests/Tools/MutationToolsDiscardTests.cs` | Tests for `twig_discard` MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Ado/Exceptions/AdoExceptions.cs` | Add `AdoUnexpectedResponseException` sealed class |
| `src/Twig.Infrastructure/Ado/AdoErrorHandler.cs` | Add Content-Type validation after 2xx check |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Add `AdoException` catch to `State()` and `Update()`, add `Discard()` tool |
| `tests/Twig.Infrastructure.Tests/Ado/AdoErrorHandlerTests.cs` | Add Content-Type tests, update `CreateResponse` helper |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsStateTests.cs` | Add `AdoException` surfacing tests |
| `tests/Twig.Mcp.Tests/Tools/MutationToolsUpdateTests.cs` | Add `AdoException` surfacing tests |

## ADO Work Item Structure

All Issues already exist under parent Issue #1752. Tasks are defined below for each Issue.

---

### Issue #1768: Add AdoUnexpectedResponseException sealed class

**Goal:** Add a new exception type to the ADO exception hierarchy for non-JSON 2xx responses.

**Prerequisites:** None

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1768.1 | Add `AdoUnexpectedResponseException` sealed class inheriting `AdoException` with properties: `StatusCode` (int), `ContentType` (string), `RequestUrl` (string), `BodySnippet` (string) | `src/Twig.Infrastructure/Ado/Exceptions/AdoExceptions.cs` | ~15 LoC |
| T-1768.2 | Constructor builds descriptive message: `ADO returned non-JSON response (HTTP {statusCode}, Content-Type: {contentType}). URL: {requestUrl}. Body: {bodySnippet}` | Same file | Included above |

**Acceptance Criteria:**
- [ ] Class is `sealed`, inherits `AdoException`
- [ ] All four properties are publicly readable
- [ ] Compiles with `TreatWarningsAsErrors=true`

---

### Issue #1769: Add Content-Type validation to ThrowOnErrorAsync

**Goal:** Detect non-JSON 2xx responses at the HTTP layer before JSON deserialization is attempted.

**Prerequisites:** #1768 (needs `AdoUnexpectedResponseException`)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1769.1 | After `IsSuccessStatusCode` early-return, check `response.Content.Headers.ContentType?.MediaType` | `src/Twig.Infrastructure/Ado/AdoErrorHandler.cs` | ~12 LoC |
| T-1769.2 | Guard: `mediaType is not null` — allow responses with no Content-Type through | Same file | Included above |
| T-1769.3 | Guard: `!string.IsNullOrWhiteSpace(body)` — empty bodies are harmless | Same file | Included above |
| T-1769.4 | Throw `AdoUnexpectedResponseException(statusCode, mediaType, requestUrl, snippet)` where snippet ≤ 500 chars | Same file | Included above |

**Acceptance Criteria:**
- [ ] HTML responses (200 OK, text/html) throw `AdoUnexpectedResponseException`
- [ ] `application/json` and `application/json; charset=utf-8` pass through
- [ ] Missing Content-Type passes through
- [ ] Empty body with non-JSON Content-Type passes through

---

### Issue #1770: Add Content-Type validation tests and update existing 2xx tests

**Goal:** Test the Content-Type validation logic and update existing test helpers for realism.

**Prerequisites:** #1769 (needs Content-Type validation in place)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1770.1 | Add test: HTML response (200 OK, text/html) → throws `AdoUnexpectedResponseException` with correct properties | `tests/Twig.Infrastructure.Tests/Ado/AdoErrorHandlerTests.cs` | ~15 LoC |
| T-1770.2 | Add test: `text/plain` non-empty body → throws `AdoUnexpectedResponseException` | Same file | ~10 LoC |
| T-1770.3 | Add test: missing Content-Type → does not throw | Same file | ~8 LoC |
| T-1770.4 | Add test: empty body with `text/html` Content-Type → does not throw | Same file | ~8 LoC |
| T-1770.5 | Add test: `application/json; charset=utf-8` → does not throw | Same file | ~8 LoC |
| T-1770.6 | Add test: body snippet truncated to 500 chars | Same file | ~12 LoC |
| T-1770.7 | Update `CreateResponse` helper to accept optional `mediaType` parameter; set `application/json` for existing 2xx tests | Same file | ~10 LoC |

**Acceptance Criteria:**
- [ ] All new tests pass
- [ ] All existing `AdoErrorHandlerTests` continue to pass
- [ ] `CreateResponse` helper updated for realism

---

### Issue #1771: Add AdoException catch to twig_state and best-effort wrapping

**Goal:** Surface structured ADO errors from `twig_state` instead of letting them propagate as generic MCP errors.

**Prerequisites:** #1768 (needs `AdoUnexpectedResponseException` to exist for end-to-end test scenarios)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1771.1 | Wrap `adoService.FetchAsync` and `ConflictRetryHelper.PatchWithRetryAsync` in `try/catch (AdoException ex)` → `return McpResultBuilder.ToError(ex.Message)` | `src/Twig.Mcp/Tools/MutationTools.cs` | ~8 LoC |
| T-1771.2 | Wrap `AutoPushNotesHelper.PushAndClearAsync` in best-effort `catch (Exception ex) when (ex is not OperationCanceledException)` | Same file | ~3 LoC |
| T-1771.3 | Wrap `promptStateWriter.WritePromptStateAsync()` in best-effort catch | Same file | ~3 LoC |

**Acceptance Criteria:**
- [ ] `twig_state` returns `IsError=true` with exception message for all `AdoException` types
- [ ] Best-effort operations (notes push, prompt state) never fail the tool
- [ ] Existing validation logic (empty state name, no context, etc.) unchanged

---

### Issue #1772: Add AdoException catch to twig_update and best-effort wrapping

**Goal:** Widen error handling in `twig_update` from `AdoConflictException` to `AdoException`.

**Prerequisites:** #1768

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1772.1 | Replace `catch (AdoConflictException)` with `catch (AdoException ex)` → `return McpResultBuilder.ToError(ex.Message)` | `src/Twig.Mcp/Tools/MutationTools.cs` | ~4 LoC |
| T-1772.2 | Include `FetchAsync` (pre-patch) in the same try block | Same file | ~2 LoC (move line) |
| T-1772.3 | Wrap `AutoPushNotesHelper.PushAndClearAsync` in best-effort catch | Same file | ~3 LoC |
| T-1772.4 | Wrap resync (`adoService.FetchAsync` + `workItemRepo.SaveAsync`) in best-effort catch | Same file | ~5 LoC |
| T-1772.5 | Wrap `promptStateWriter.WritePromptStateAsync()` in best-effort catch | Same file | ~3 LoC |

**Acceptance Criteria:**
- [ ] `twig_update` returns structured error for all `AdoException` types
- [ ] Existing conflict retry logic (via `ConflictRetryHelper`) remains intact
- [ ] Post-mutation failures never fail the tool

---

### Issue #1773: Add twig_discard MCP tool

**Goal:** Provide a clean way to discard poisoned pending changes without SQLite surgery.

**Prerequisites:** None (independent of error handling chain)

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1773.1 | Add `Discard()` method with `[McpServerTool(Name = "twig_discard")]` attribute | `src/Twig.Mcp/Tools/MutationTools.cs` | ~5 LoC |
| T-1773.2 | Resolve target ID: use `id` if provided, else resolve active item via `activeItemResolver` | Same file | ~10 LoC |
| T-1773.3 | Validate item exists via `workItemRepo.GetByIdAsync` | Same file | ~5 LoC |
| T-1773.4 | Get change summary via `pendingChangeStore.GetChangeSummaryAsync`, return early if no changes | Same file | ~5 LoC |
| T-1773.5 | Clear changes via `pendingChangeStore.ClearChangesAsync` and dirty flag via `workItemRepo.ClearDirtyFlagAsync` | Same file | ~5 LoC |
| T-1773.6 | Update prompt state (best-effort) and return structured result with discarded counts | Same file | ~15 LoC |

**Acceptance Criteria:**
- [ ] Tool accepts optional `id` parameter; defaults to active work item
- [ ] Returns clear success/error messages with discarded counts
- [ ] No `--all` flag (per-item scoped)

---

### Issue #1774: Add twig_state error handling tests

**Goal:** Verify `AdoException` surfacing in `twig_state`.

**Prerequisites:** #1771

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1774.1 | Add test: `FetchAsync` throws `AdoAuthenticationException` → returns `IsError=true` with message | `tests/Twig.Mcp.Tests/Tools/MutationToolsStateTests.cs` | ~15 LoC |
| T-1774.2 | Add test: `PatchWithRetryAsync` throws `AdoUnexpectedResponseException` → returns `IsError=true` | Same file | ~15 LoC |
| T-1774.3 | Add test: `AutoPushNotesHelper` throws → does not fail `twig_state` (best-effort) | Same file | ~15 LoC |
| T-1774.4 | Add test: `promptStateWriter` throws → does not fail `twig_state` (best-effort) | Same file | ~15 LoC |

**Acceptance Criteria:**
- [ ] All tests pass
- [ ] Tests follow existing `MutationToolsStateTests` pattern

---

### Issue #1775: Add twig_update error handling tests

**Goal:** Verify `AdoException` surfacing in `twig_update`.

**Prerequisites:** #1772

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1775.1 | Add test: `FetchAsync` throws `AdoAuthenticationException` → returns `IsError=true` | `tests/Twig.Mcp.Tests/Tools/MutationToolsUpdateTests.cs` | ~15 LoC |
| T-1775.2 | Add test: `PatchWithRetryAsync` throws `AdoUnexpectedResponseException` → returns `IsError=true` | Same file | ~15 LoC |
| T-1775.3 | Add test: `AdoConflictException` still handled → returns error (regression guard) | Same file | ~10 LoC |
| T-1775.4 | Add test: post-mutation best-effort operations throw → does not fail `twig_update` | Same file | ~15 LoC |

**Acceptance Criteria:**
- [ ] All tests pass
- [ ] Existing `Update_AdoConflictException_ReturnsError` test still passes (regression guard)

---

### Issue #1776: Add twig_discard tests

**Goal:** Comprehensive unit tests for the new `twig_discard` MCP tool.

**Prerequisites:** #1773

**Tasks:**

| Task | Description | Files | Effort |
|------|-------------|-------|--------|
| T-1776.1 | Add test: discard with no pending changes → returns success with "no pending changes" message | `tests/Twig.Mcp.Tests/Tools/MutationToolsDiscardTests.cs` | ~15 LoC |
| T-1776.2 | Add test: discard with pending changes → calls `ClearChangesAsync`, `ClearDirtyFlagAsync`, returns discarded counts | Same file | ~20 LoC |
| T-1776.3 | Add test: discard by explicit ID → resolves by ID, not active item | Same file | ~20 LoC |
| T-1776.4 | Add test: explicit ID not found in cache → returns error | Same file | ~15 LoC |
| T-1776.5 | Add test: no active item and no ID → returns error | Same file | ~10 LoC |
| T-1776.6 | Add test: `ClearChangesAsync` throws → returns structured error | Same file | ~15 LoC |

**Acceptance Criteria:**
- [ ] All tests pass
- [ ] New file: `tests/Twig.Mcp.Tests/Tools/MutationToolsDiscardTests.cs`

## PR Groups

### PG-1: Infrastructure — Exception & Content-Type Guard

**Issues:** #1768, #1769, #1770
**Type:** Deep (few files, focused logic changes)
**Estimated LoC:** ~120

| Task | File | LoC |
|------|------|-----|
| T-1768.1–2 | `AdoExceptions.cs` | ~15 |
| T-1769.1–4 | `AdoErrorHandler.cs` | ~12 |
| T-1770.1–7 | `AdoErrorHandlerTests.cs` | ~90 |

**Files touched:** 3
**Successor:** PG-2 (MCP changes depend on exception type existing)

---

### PG-2: MCP — Error Handling & Discard Tool

**Issues:** #1771, #1772, #1773, #1774, #1775, #1776
**Type:** Deep (one production file + three test files)
**Estimated LoC:** ~330

| Task | File | LoC |
|------|------|-----|
| T-1771.1–3 | `MutationTools.cs` | ~14 |
| T-1772.1–5 | `MutationTools.cs` | ~17 |
| T-1773.1–6 | `MutationTools.cs` | ~45 |
| T-1774.1–4 | `MutationToolsStateTests.cs` | ~60 |
| T-1775.1–4 | `MutationToolsUpdateTests.cs` | ~55 |
| T-1776.1–6 | `MutationToolsDiscardTests.cs` | ~120 |

**Files touched:** 4 (1 production + 3 test)
**Predecessor:** PG-1

---

**Execution order:** PG-1 → PG-2

## References

- [ADO REST API — Work Items](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items)
- Existing architecture doc: `docs/architecture/ado-integration.md`
- MCP SDK: [ModelContextProtocol NuGet](https://www.nuget.org/packages/ModelContextProtocol)
