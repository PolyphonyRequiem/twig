# MCP Response Envelope Schema

This document defines the standard JSON response envelope used by all `twig-mcp` MCP tools.
Every tool response follows one of two shapes: **success** or **error**.

## Success Envelope

All successful tool responses return a JSON object with these fields:

```json
{
  "success": true,
  "data": { ... },
  "context": {
    "activeItemId": 1234,
    "workspace": "org/project",
    "cacheAge": "PT2M30S"
  },
  "hints": ["item has 3 pending changes — consider twig_sync"]
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `success` | `boolean` | ✅ | Always `true` for success responses. |
| `data` | `object` | ✅ | Tool-specific payload. Shape varies per tool but is always a JSON object. |
| `context` | `object` | ✅ | Contextual metadata populated automatically from workspace state. |
| `hints` | `string[]` | ✅ | Actionable suggestions for the calling agent. Empty array `[]` when no hints apply or when `verbose` is `false`. |

### Context Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `activeItemId` | `integer \| null` | ✅ | The currently active work item ID, or `null` if no item is set. |
| `workspace` | `string` | ✅ | Workspace key in `"org/project"` format, or `""` if unknown. |
| `cacheAge` | `string` | ✅ | ISO 8601 duration since the active item was last synced (e.g. `"PT2M30S"`), or `""` when no sync timestamp is available. |

## Error Envelope

All error responses return a JSON object with `success: false`:

```json
{
  "success": false,
  "error": {
    "code": "ITEM_NOT_FOUND",
    "message": "Work item 9999 does not exist in the local cache.",
    "details": { "id": "9999" }
  },
  "context": {
    "activeItemId": 10,
    "workspace": "org/project",
    "cacheAge": ""
  }
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `success` | `boolean` | ✅ | Always `false` for error responses. |
| `error` | `object` | ✅ | Structured error information. |
| `context` | `object` | ❌ | Present when workspace state is available at the point of failure. Omitted when the error occurs before workspace resolution (e.g. workspace not found). |

### Error Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `code` | `string` | ✅ | Machine-readable error code (UPPER_SNAKE_CASE). See [Error Codes](#error-codes) below. |
| `message` | `string` | ✅ | Human-readable error description. |
| `details` | `object` | ✅ | Additional key-value details about the error. Empty object `{}` when no additional details apply. All values are strings. |

## Error Codes

All error codes are defined as string constants in `McpErrorCode` (`src/Twig.Mcp/Services/McpErrorCode.cs`).
Codes follow UPPER_SNAKE_CASE convention.

| Code | Description |
|------|-------------|
| `ITEM_NOT_FOUND` | Work item not found in cache or ADO. |
| `INVALID_INPUT` | Caller provided invalid input (missing field, bad format, etc.). |
| `NO_CONTEXT` | No active work item set — caller must use `twig_set` first. |
| `ADO_UNREACHABLE` | ADO API is unreachable (network error, auth failure, timeout). |
| `ADO_VALIDATION_FAILED` | ADO rejected the request because one or more field values failed validation. |
| `CACHE_STALE` | The local cache is stale and a sync is required. |
| `INVALID_STATE_TRANSITION` | The requested state transition is not valid. |
| `WORKSPACE_NOT_FOUND` | The workspace could not be resolved (ambiguous, unknown, etc.). |
| `INTERNAL_ERROR` | An unexpected internal error occurred. |
| `PERMISSION_DENIED` | The caller lacks permission for the requested operation. |
| `CONFIRMATION_REQUIRED` | The operation requires confirmation that was not provided. |

## Hints

Hints are contextual suggestions included in the `hints` array on success responses.
They are only populated when `verbose=true` is passed to the tool — keeping the default
response lightweight for batch/automated scenarios.

Currently generated hints:

| Condition | Example Hint |
|-----------|-------------|
| Active item has pending changes | `"item has 3 pending changes — consider twig_sync"` |
| Other dirty items in workspace | `"2 other dirty items in workspace — consider twig_sync"` |
| Unpublished seeds exist | `"3 unpublished seeds"` |

## Type Mapping (C# → JSON)

The envelope types are defined in `src/Twig.Mcp/Services/` and registered for AOT-compatible
source-generated serialization in `McpJsonContext` (`src/Twig.Mcp/Serialization/McpJsonContext.cs`).

| C# Type | JSON Role | File |
|---------|-----------|------|
| `McpSuccessEnvelope` | Success response wrapper | `McpSuccessEnvelope.cs` |
| `McpErrorEnvelope` | Error response wrapper | `McpErrorEnvelope.cs` |
| `McpContext` | Context block | `McpContext.cs` |
| `McpError` | Error details block | `McpError.cs` |
| `McpErrorCode` | Error code constants | `McpErrorCode.cs` |
| `EnvelopeBuilder` | Factory for building envelopes | `EnvelopeBuilder.cs` |
| `McpHintProvider` | Hint generation logic | `McpHintProvider.cs` |

### Serialization Configuration

- **Naming policy:** `camelCase` (via `JsonKnownNamingPolicy.CamelCase`)
- **Null handling:** Null values are omitted (via `JsonIgnoreCondition.WhenWritingNull`)
- **Source generation:** All types annotated with `[JsonSerializable]` in `McpJsonContext`
- **No reflection:** Compatible with `PublishAot=true` and `JsonSerializerIsReflectionEnabledByDefault=false`

## Design Principles

1. **One parser for all tools** — Agents can use a single response parser for all 8+ MCP tools.
2. **Machine-readable errors** — Structured error codes enable programmatic error handling without string matching.
3. **No Spectre markup** — MCP responses contain zero Spectre Console markup characters. Markup is reserved for human-facing CLI rendering.
4. **Backwards-compatible data** — The `data` field preserves the same structure each tool returned before the envelope was introduced.
5. **Lightweight by default** — Hints are only computed when `verbose=true`, keeping default responses fast.

## Tools Using This Envelope

All MCP tools return responses wrapped in this envelope:

| Tool | Description |
|------|-------------|
| `twig_set` | Set the active work item by ID or title pattern |
| `twig_status` | Show the active work item status and pending changes |
| `twig_tree` | Render the focused work item tree |
| `twig_workspace` | Show the full workspace dashboard |
| `twig_state` | Change the state of the active work item |
| `twig_update` | Update a field on the active work item |
| `twig_note` | Add a comment/note to the active work item |
| `twig_sync` | Flush pending local changes to ADO then refresh |
