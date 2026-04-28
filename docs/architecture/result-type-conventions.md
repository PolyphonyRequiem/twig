# Result Type Conventions

> **Status**: Living document · **Last updated**: April 2026
> **Related**: [Domain Model Critique — Item 7](domain-model-critique.md#7-result-type-proliferation)

---

## Overview

Operations in the twig domain layer return result types to communicate outcomes.
This document establishes a **three-tier taxonomy** for choosing the right result
pattern, provides a **decision matrix** for new code, and catalogs
**anti-patterns** to avoid.

---

## Tier 1 — Discriminated Union

**Pattern**: `abstract record` with `private` constructor and `sealed record` subtypes.

**When to use**: The operation has **two or more distinct outcome paths** with
**different data shapes**. Each subtype carries exactly the data relevant to that
outcome — nothing more, nothing less.

### Template

```csharp
public abstract record OperationResult
{
    private OperationResult() { }

    public sealed record Success(/* success-specific data */) : OperationResult;
    public sealed record NotFound(int Id) : OperationResult;
    public sealed record Failed(string Reason) : OperationResult;
}
```

### Exhaustive matching

Always switch on the result and throw `UnreachableException` in the `default`
arm. This turns a forgotten case into a runtime crash rather than silent
data loss:

```csharp
var message = result switch
{
    OperationResult.Success s   => $"Done: {s.Id}",
    OperationResult.NotFound nf => $"Not found: {nf.Id}",
    OperationResult.Failed f    => $"Error: {f.Reason}",
    _ => throw new UnreachableException(
             $"Unhandled OperationResult: {result.GetType().Name}")
};
```

### Codebase examples

#### `ActiveItemResult`

**File**: `src/Twig.Domain/Services/Navigation/ActiveItemResult.cs`

```csharp
public abstract record ActiveItemResult
{
    private ActiveItemResult() { }

    public sealed record Found(WorkItem WorkItem) : ActiveItemResult;
    public sealed record NoContext : ActiveItemResult;
    public sealed record FetchedFromAdo(WorkItem WorkItem) : ActiveItemResult;
    public sealed record Unreachable(int Id, string Reason) : ActiveItemResult;
}
```

Four distinct outcomes — `Found` and `FetchedFromAdo` carry a `WorkItem`;
`Unreachable` carries an ID and reason; `NoContext` carries nothing. Consumers
pattern-match to extract data:

```csharp
// src/Twig.Mcp/Tools/ReadTools.cs
if (resolveResult is ActiveItemResult.NoContext)
    return McpResultBuilder.ToError("No active work item. Use twig_set first.");
if (resolveResult is ActiveItemResult.Unreachable u)
    return McpResultBuilder.ToError($"Work item #{u.Id} unreachable: {u.Reason}");
```

#### `SyncResult`

**File**: `src/Twig.Domain/Services/Sync/SyncResult.cs`

```csharp
public abstract record SyncResult
{
    private SyncResult() { }

    public sealed record UpToDate : SyncResult;
    public sealed record Updated(int ChangedCount) : SyncResult;
    public sealed record Failed(string Reason) : SyncResult;
    public sealed record Skipped(string Reason) : SyncResult;
    public sealed record PartiallyUpdated(
        int SavedCount,
        IReadOnlyList<SyncItemFailure> Failures) : SyncResult;
}
```

Five outcomes with varying data shapes. The renderer uses exhaustive matching
with `UnreachableException`:

```csharp
// src/Twig/Rendering/SpectreRenderer.cs (line 1484)
default:
    throw new UnreachableException(
        $"Unhandled SyncResult: {result.GetType().Name}");
```

### Rules

1. The `abstract record` constructor **must** be `private` — prevents external
   subtyping that would break exhaustive switches.
2. Every subtype **must** be `sealed record` — no further inheritance.
3. Subtypes carry only the data relevant to their case. Do **not** put shared
   properties on the base record.
4. Every `switch` expression or statement **must** include a `default` arm that
   throws `UnreachableException`.

---

## Tier 2 — `Result` / `Result<T>`

**Pattern**: `readonly record struct` with `IsSuccess`, `Value`, and `Error`
properties. Factory methods `Ok()` and `Fail()` enforce valid construction.

**When to use**: The operation either **succeeds with a value** (or void) or
**fails with an error message**. There are no additional outcome variants — just
pass/fail.

### Template

```csharp
// Already defined in src/Twig.Domain/Common/Result.cs — reuse, don't reinvent.
Result.Ok()                       // void success
Result.Ok(value)                  // success with value
Result.Fail("reason")             // void failure
Result.Fail<T>("reason")          // typed failure
```

### Codebase examples

#### `Result<T>` (type definition)

**File**: `src/Twig.Domain/Common/Result.cs`

```csharp
public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    private readonly T _value;
    public string Error { get; }

    private Result(bool isSuccess, T value, string error) { /* ... */ }

    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
              $"Cannot access Value on a failed result. Error: {Error}");

    public static Result<T> Ok(T value) => new(true, value, string.Empty);
    public static Result<T> Fail(string error) => new(false, default!, error);
}
```

#### `SeedFactory.Create()` — consumer example

**File**: `src/Twig.Domain/Services/Seed/SeedFactory.cs`

```csharp
public Result<WorkItem> Create(
    string title,
    WorkItem? parentContext,
    ProcessConfiguration processConfig,
    WorkItemType? typeOverride = null,
    string? assignedTo = null)
{
    if (string.IsNullOrWhiteSpace(title))
        return Result.Fail<WorkItem>("Seed title cannot be empty.");

    // ... validation ...

    return Result.Ok(seed);
}
```

Simple binary outcome: either a `WorkItem` is created, or a validation error
message is returned. No need for a discriminated union.

### Rules

1. **Do not** create new `Result`-like structs — use the existing
   `Result` / `Result<T>` from `Common/Result.cs`.
2. Always check `IsSuccess` before accessing `Value` — it throws on failure.
3. If you find yourself adding a third outcome (e.g., "not found" vs "error"),
   promote to Tier 1 (discriminated union).

---

## Tier 3 — Data Bag

**Pattern**: `sealed class` or `sealed record` with `init` properties and
optional computed summaries. No success/failure semantics.

**When to use**: The operation always "completes" (never fails in a way the
caller distinguishes) and returns **varying amounts of data**. The caller
inspects the data to decide what to display, not what to do differently.

### Template

```csharp
public sealed class OperationSummary
{
    public int ProcessedCount { get; init; }
    public int SkippedCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
```

### Codebase examples

#### `RefreshFetchResult`

**File**: `src/Twig.Domain/Services/Sync/RefreshOrchestrator.cs`

```csharp
public sealed class RefreshFetchResult
{
    public int ItemCount { get; init; }
    public int PhantomsCleansed { get; init; }
    public IReadOnlyList<RefreshConflict> Conflicts { get; init; } = [];
}
```

A single consumer reads the counters and conflict list for display. There is no
"failure" path — the orchestrator always produces a result.

#### `SeedPublishBatchResult`

**File**: `src/Twig.Domain/ValueObjects/SeedPublishBatchResult.cs`

```csharp
public sealed class SeedPublishBatchResult
{
    public IReadOnlyList<SeedPublishResult> Results { get; init; } = [];
    public IReadOnlyList<string> CycleErrors { get; init; } = [];

    public bool HasErrors =>
        CycleErrors.Count > 0 || Results.Any(r => r.Status == SeedPublishStatus.Error);
    public int CreatedCount =>
        Results.Count(r => r.Status == SeedPublishStatus.Created);
    public int SkippedCount =>
        Results.Count(r => r.Status == SeedPublishStatus.Skipped);
}
```

Aggregates nested results with computed summaries. Callers inspect `HasErrors`,
`CreatedCount`, etc. to build display output.

#### `QueryResult`

**File**: `src/Twig.Domain/ReadModels/QueryResult.cs`

```csharp
public sealed record QueryResult(
    IReadOnlyList<WorkItem> Items,
    bool IsTruncated,
    string Query = "all items");
```

A pure data carrier for WIQL query output — not an operation result at all. No
success/failure semantics, just a read model.

### Rules

1. Use `sealed class` with `init` properties when the bag has mutable-looking
   fields or collection defaults.
2. Use `sealed record` with positional parameters for small, immutable carriers
   (like `QueryResult`).
3. Default collections to `[]`, not `null`.
4. Computed properties (e.g., `HasErrors`, `CreatedCount`) are fine — they
   summarize the data, not encode outcome semantics.
5. If you find yourself adding `IsSuccess` or branching on an enum field,
   promote to Tier 1 or Tier 2.

---

## Decision Matrix

Use this flowchart when creating a new result type:

```
Does the operation have distinct outcome paths
with different data shapes?
  │
  ├── YES (2+ outcomes with different fields)
  │   └── Tier 1: Discriminated Union
  │       abstract record + sealed subtypes
  │
  └── NO
      │
      Can it fail with an error message?
        │
        ├── YES (binary pass/fail)
        │   └── Tier 2: Result / Result<T>
        │       Use existing Common/Result.cs
        │
        └── NO (always completes, returns data)
            └── Tier 3: Data Bag
                sealed class/record with init properties
```

### Quick reference

| Signal | Tier | Example |
|--------|------|---------|
| "It can be found, not found, or unreachable" | 1 — DU | `ActiveItemResult` |
| "It either succeeds with X or fails with a message" | 2 — `Result<T>` | `SeedFactory.Create()` |
| "It always returns counters/items/summaries" | 3 — Data bag | `RefreshFetchResult` |
| "I need an `IsSuccess` computed from an enum" | 1 — DU (refactor) | See anti-patterns |
| "I need nullable fields that are only set in some cases" | 1 — DU (refactor) | See anti-patterns |

---

## Anti-Patterns

### 1. Enum + class hybrid

```csharp
// ❌ Don't do this
public sealed record LinkResult
{
    public required LinkStatus Status { get; init; }  // enum
    public string ErrorMessage { get; init; } = "";   // only meaningful when Status == Failed
    public bool IsSuccess => Status is LinkStatus.Linked;
}
```

**Problem**: `ErrorMessage` exists on success instances (empty string),
`IsSuccess` is a computed property that papers over the real issue — the type
allows invalid combinations like `Status = Failed` with `ErrorMessage = ""`.

**Fix**: Promote to Tier 1. Each enum value becomes a sealed subtype that carries
only the data relevant to that case.

### 2. Nullable fields as state encoding

```csharp
// ❌ Don't do this
public sealed class Snapshot
{
    public bool HasContext { get; init; }
    public WorkItem? Item { get; init; }            // null when HasContext is false
    public int? UnreachableId { get; init; }        // null when not unreachable
    public string? UnreachableReason { get; init; } // null when not unreachable
    public bool IsSuccess => HasContext && Item is not null;
}
```

**Problem**: Nothing prevents `HasContext = false` with a non-null `Item`, or
`UnreachableId = 42` with `UnreachableReason = null`. The boolean and nullable
fields encode three distinct states (no-context / unreachable / success) in a
single flat class.

**Fix**: Promote to Tier 1. Each state becomes a sealed subtype:
- `NoContext` — no fields
- `Unreachable(int Id, string Reason)` — only the error data
- `Success(WorkItem Item, ...)` — only the success data

### 3. `IsSuccess` computed from unrelated fields

```csharp
// ❌ Don't do this
public sealed class ValidationResult
{
    public IReadOnlyList<string> Failures { get; init; } = [];
    public bool Passed => Failures.Count == 0;  // implicit encoding
}
```

**Problem**: `Passed` is derived from the absence of failures. If a caller adds a
`Warnings` list later, is `Passed` still correct? The success semantics are
implicit and fragile.

**Acceptable exception**: Tier 3 data bags that genuinely just summarize counters
(like `SeedPublishBatchResult.HasErrors`) are fine — they're display helpers, not
control-flow signals. The anti-pattern applies when `IsSuccess` drives branching
logic in callers.

---

## Adding a New Result Type — Checklist

1. Run through the [decision matrix](#decision-matrix) to pick a tier.
2. If Tier 1: follow the [template](#template) with `private` constructor,
   `sealed record` subtypes, and `UnreachableException` in all switches.
3. If Tier 2: use `Result` / `Result<T>` from `Common/Result.cs` — do not
   create a new struct.
4. If Tier 3: use `sealed class` with `init` properties; default collections
   to `[]`.
5. Add the type to `TwigJsonContext` if it participates in serialization.
6. Verify AOT compatibility — no reflection, no dynamic type loading.
