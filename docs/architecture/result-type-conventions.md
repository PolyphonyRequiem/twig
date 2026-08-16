# Result Type Conventions

> **Status**: Living document · **Last updated**: August 2026
> **Related**: [Domain Model Critique — Item 7](domain-model-critique.md#7-result-type-proliferation)

---

## Overview

Operations in the twig domain layer return result types to communicate outcomes.
This document establishes a **three-tier taxonomy** for choosing the right result
pattern, provides a **decision matrix** for new code, and catalogs
**anti-patterns** to avoid.

---

## Tier 1 — Discriminated Union

**Pattern**: the C# `union` keyword, listing **top-level sibling `sealed record`
case types**.

**When to use**: The operation has **two or more distinct outcome paths** with
**different data shapes**. Each case type carries exactly the data relevant to that
outcome — nothing more, nothing less.

### Template

```csharp
/// <summary>The operation succeeded.</summary>
public sealed record Succeeded(int Id);

/// <summary>The requested item does not exist.</summary>
public sealed record OperationNotFound(int Id);

/// <summary>The operation could not be completed.</summary>
public sealed record OperationFailed(string Reason);

/// <summary>
/// Discriminated union representing the outcome of the operation.
/// </summary>
public union OperationResult(Succeeded, OperationNotFound, OperationFailed);
```

Two structural points, both load-bearing:

- **The case types are declared at namespace scope, as siblings of the union** —
  not nested inside it. A case type is therefore referred to by its bare name
  (`OperationNotFound`), never as `OperationResult.NotFound`.
- **The `union` declaration lists the case types by name.** That list *is* the
  closed set; there is no base type to inherit from and no way to add a case from
  outside the declaration.

### The keyword replaces rules, it does not bend them

Earlier revisions of this document specified Tier 1 as an `abstract record` with a
`private` constructor and nested `sealed record` subtypes, and required those
properties by hand. The `union` keyword enforces the same three properties
**structurally**, so they are no longer rules a reviewer has to check:

| Former hand-written rule | How the keyword enforces it |
|---|---|
| The base constructor **must** be `private`, to prevent external subtyping | There is no base type to subtype. The case list in the declaration is closed by construction. |
| Every subtype **must** be `sealed record` | Case types are independent records that inherit from nothing, so there is no inheritance chain to seal against. |
| Do **not** put shared properties on the base record | There is no base record, so shared members are not expressible. |

The keyword is the **stronger** guarantee, not a deviation from the old rules.
Reviewers: a `union` that does not declare a private constructor is correct — the
absence is the point.

> **Both shapes are live in this repo, and that is expected.** The keyword is the
> convention for Tier 1, and 14 unions across 13 files use it. Older
> `abstract record` discriminated unions remain in
> `src/Twig.Domain/Services/Mutation/` (`PatchOutcome`, `NoteOutcome`,
> `StateTransitionOutcome`, `DeletePreparation`, `DeleteOutcome`, `DiscardOutcome`,
> `PinOutcome`, `FieldUpdateOutcome`, `BenchOutcome`), plus `FormLayoutResult`,
> `RenderNode` and `RenderValue`. They are correct as written and are **not**
> scheduled for conversion; do not treat one as a defect. Write **new** Tier 1
> types with the keyword.

### Naming: disambiguate case types at namespace scope

Because case types are top-level, their names share a namespace with every other
case type near them. Where a plain name would collide, **prefix it with its
union's subject**. The repo already follows this:

- `ActiveNoContext` (in `ActiveItemResult`) and `StatusNoContext` (in
  `StatusResult`) — both mean "no active work item context is configured", and
  a bare `NoContext` could only belong to one of them.
- `SyncFailed` (in `SyncResult`) and `LinkFailed` (in `BranchLinkResult`).
- `ActiveUnreachable` (in `ActiveItemResult`) and `StatusUnreachable` (in
  `StatusResult`).

Where no collision exists, the unqualified name is fine — `Found`,
`FetchedFromAdo`, `SingleMatch`, `NoMatch`, `UpToDate`, `Updated`.

### Exhaustive matching

Always switch on the result and throw `UnreachableException` in the `default`
arm. This turns a forgotten case into a runtime crash rather than silent
data loss.

🔴 **A `union` is a struct wrapper around its current case, so
`result.GetType().Name` returns the name of the UNION, not of the unhandled case.**
Use `result.Value?.GetType().Name`, which is the case type's name — otherwise the
diagnostic reads `Unhandled OperationResult: OperationResult` and names nothing:

```csharp
var message = result switch
{
    Succeeded s          => $"Done: {s.Id}",
    OperationNotFound nf => $"Not found: {nf.Id}",
    OperationFailed f    => $"Error: {f.Reason}",
    _ => throw new UnreachableException(
             $"Unhandled OperationResult: {result.Value?.GetType().Name}")
};
```

Note the case patterns are bare type names. Note also that the union's own name is
written as a **literal** in the message: that is what tells a reader which union
was being matched, and it matters for the reuse ruling below.

### Cross-union arm reuse — SANCTIONED, with a condition

**Ruling**: a union **may** reuse another union's case types rather than mirroring
them, **provided every `UnreachableException` over either union names its own
union in the message.**

The instance that raised the question is `ProcessDescriptionRenderResult`
(`src/Twig.Mcp/Tools/ProcessDescriptionRenderResult.cs`), which reuses the three
failure case types of `ProcessDescriptionResult`
(`src/Twig.Domain/Services/Process/ProcessDescriptionResult.cs`) — only the success
arm differs, because at the agent surface the answer is rendered bytes rather than
a model.

The reason to allow it: a parallel set of mirrored failure types would be free to
**drift** from the assembler's, which is exactly the duplication this design
already refuses for the document itself.

The cost, stated plainly and accepted: the two unions are **not disjoint**. A bare
`result is ProcessIdentityUnresolved` no longer tells a reader which union is in
hand.

The condition is what makes the reuse legible, and it is not optional:

> **Every `UnreachableException` over either union names its own union in the
> message.**

At a failure — the one moment a reader most needs to know which union was being
matched — the message supplies the half the type name cannot. Combined with
`.Value?.GetType().Name` above, the diagnostic carries both facts: which union, and
which case went unhandled. See `ProcessTools.DescribeFailure` and
`ProcessDescriptionCommand`, which spell it exactly that way.

Reuse case types **only** where the two unions genuinely describe the same
outcomes, as here. Two unions that merely happen to have a similarly-named failure
should declare their own.

### Codebase examples

#### `ActiveItemResult`

**File**: `src/Twig.Domain/Services/Navigation/ActiveItemResult.cs`

```csharp
/// <summary>Active item was found in local cache.</summary>
public sealed record Found(WorkItem WorkItem);

/// <summary>No active work item context is configured.</summary>
public sealed record ActiveNoContext;

/// <summary>Active item was fetched from Azure DevOps.</summary>
public sealed record FetchedFromAdo(WorkItem WorkItem);

/// <summary>Active item could not be reached.</summary>
public sealed record ActiveUnreachable(int Id, string Reason);

public union ActiveItemResult(Found, ActiveNoContext, FetchedFromAdo, ActiveUnreachable);
```

Four distinct outcomes — `Found` and `FetchedFromAdo` carry a `WorkItem`;
`ActiveUnreachable` carries an ID and reason; `ActiveNoContext` carries nothing.
Consumers pattern-match on the bare case names to extract data:

```csharp
// src/Twig.Mcp/Services/WorkItemResolver.cs
if (resolved is ActiveNoContext)
    return (null, await EnvelopeBuilder.ErrorAsync(
        McpErrorCode.NoContext, "No active work item. Pass an explicit id.", ctx, ct));
if (resolved is ActiveUnreachable u)
    return (null, await EnvelopeBuilder.ErrorAsync(
        McpErrorCode.ItemNotFound, $"Work item #{u.Id} unreachable: {u.Reason}", ctx, ct));
```

#### `SyncResult`

**File**: `src/Twig.Domain/Services/Sync/SyncResult.cs`

```csharp
/// <summary>All items are already current — nothing to sync.</summary>
public sealed record UpToDate;

/// <summary>Items were synced successfully.</summary>
public sealed record Updated(int ChangedCount);

/// <summary>Sync failed entirely.</summary>
public sealed record SyncFailed(string Reason);

/// <summary>Sync was skipped (e.g., no context).</summary>
public sealed record Skipped(string Reason);

/// <summary>Some items were saved successfully while others failed during fetch.</summary>
public sealed record PartiallyUpdated(int SavedCount, IReadOnlyList<SyncItemFailure> Failures);

/// <summary>The cache holds the item but it is older than the staleness window.</summary>
public sealed record Stale(DateTimeOffset? LastSyncedAt);

/// <summary>The item is not present in the local cache at all.</summary>
public sealed record NotCached(int Id);

public union SyncResult(UpToDate, Updated, SyncFailed, Skipped, PartiallyUpdated, Stale, NotCached);
```

Seven outcomes with varying data shapes. Note `SyncFailed` rather than a bare
`Failed`, per the naming convention above. `SpectreRenderer.RenderWithSyncAsync`
uses exhaustive matching with `UnreachableException`:

```csharp
// src/Twig/Rendering/SpectreRenderer.cs — RenderWithSyncAsync
default:
    throw new System.Diagnostics.UnreachableException(
        $"Unhandled SyncResult: {result.Value?.GetType().Name}");
```

### Rules

1. Declare Tier 1 types with the `union` keyword, listing the case types by name.
   Rules that former revisions of this document imposed by hand — private
   constructor, sealed subtypes, no shared base members — are enforced
   **structurally** by the keyword and need no restating in code.
2. Declare case types as **top-level sibling `sealed record`s**, not nested inside
   the union.
3. Case types carry only the data relevant to their case.
4. Prefix a case type's name with its union's subject where a bare name would
   collide at namespace scope (`ActiveNoContext` / `StatusNoContext`).
5. Every `switch` expression or statement **must** include a `default` arm that
   throws `UnreachableException`, and that message **must** name the union as a
   literal and identify the case via `result.Value?.GetType().Name`.
6. Reusing another union's case types is permitted where the outcomes are
   genuinely the same, subject to rule 5 — see the ruling above.

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
    public IReadOnlyList<string> PreFlightErrors { get; init; } = [];

    public bool HasErrors => CycleErrors.Count > 0 || PreFlightErrors.Count > 0
        || Results.Any(r => r.Status is SeedPublishStatus.Error or SeedPublishStatus.ValidationFailed);
    public int CreatedCount => Results.Count(r => r.Status == SeedPublishStatus.Created);
    public int SkippedCount => Results.Count(r => r.Status == SeedPublishStatus.Skipped);
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
  │       union keyword + top-level sibling records
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
2. If Tier 1: follow the [template](#template) — the `union` keyword with
   top-level sibling `sealed record` case types, disambiguating prefixes where
   names would collide, and `UnreachableException` in every switch's `default`
   arm naming the union and reporting `result.Value?.GetType().Name`.
3. If Tier 2: use `Result` / `Result<T>` from `Common/Result.cs` — do not
   create a new struct.
4. If Tier 3: use `sealed class` with `init` properties; default collections
   to `[]`.
5. Add the type to `TwigJsonContext` if it participates in serialization.
6. Verify AOT compatibility — no reflection, no dynamic type loading.
