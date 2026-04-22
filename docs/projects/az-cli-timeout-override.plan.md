# AzCliAuthProvider Timeout Override

| Field | Value |
|---|---|
| **Work Item** | #1858 |
| **Type** | Issue |
| **Status** | In Progress |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

The `AzCliAuthProvider` uses a hard-coded 10-second timeout for `az account get-access-token`
process execution, which is too aggressive for cold-start scenarios where the Azure CLI
must refresh or acquire a new token. This plan introduces a configurable timeout via the
`TWIG_AZ_TIMEOUT` environment variable, converts the static `ProcessTimeout` field to an
instance field resolved at construction time, and improves the timeout error message to
include self-service guidance. The change is backward-compatible: the default remains 10s
unless the environment variable is set. Production code changes are already implemented;
remaining work is focused on completing test coverage for the new env-var-driven behavior.

## Background

### Current Architecture

`AzCliAuthProvider` is an `IAuthenticationProvider` implementation that shells out to
`az account get-access-token` to obtain an Azure DevOps access token. It features:

- **In-memory caching** with a 50-minute TTL
- **Cross-process file caching** at `~/.twig/.token-cache`
- **Decorator wrapping** via `MsalCacheTokenProvider` (reads MSAL cache first, falls back
  to `AzCliAuthProvider`)

The timeout was originally defined as a static field:

```csharp
private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
```

This has since been replaced with a `DefaultProcessTimeout` constant and a `_processTimeout`
instance field that is resolved at construction time via `ResolveTimeout()`.

### Call-Site Audit

| File | Method / Location | Current Usage | Impact |
|------|-------------------|---------------|--------|
| `NetworkServiceModule.cs:35` | `AddTwigNetworkServices` lambda | `new AzCliAuthProvider()` — parameterless ctor | Unchanged; timeout resolved internally via env var |
| `Program.cs:89` (Twig.Mcp) | `CreateAuthProvider` | `return new AzCliAuthProvider()` — parameterless ctor | Unchanged; timeout resolved internally via env var |
| `AzCliAuthProviderTests.cs` | 18 test methods | Mix of 3-param and 4-param internal ctors | Existing tests pass; some new tests still needed |

### Prior Art

The codebase already uses environment variables for configuration (e.g.,
`TWIG_TELEMETRY_ENDPOINT`). The pattern of reading an env var with a fallback default
is established. `int.TryParse` with `NumberStyles.None` is used in `TryReadFileCache`
for parsing cached tick values.

### Implementation Status

Production code changes are **complete** in `AzCliAuthProvider.cs`:
- `DefaultProcessTimeout` const (line 17)
- `_processTimeout` instance field (line 27)
- `ResolveTimeout()` static method (lines 82–93)
- 4-param constructor with `TimeSpan? processTimeout` (lines 70–76)
- Constructor chain rewired (lines 38–65)
- `CancelAfter(_processTimeout)` (line 144)
- Improved error message with `TWIG_AZ_TIMEOUT` guidance (line 180)

Existing new test coverage (4 tests):
- `Constructor_ExplicitTimeout_UsesProvidedValue` — verifies 4-param ctor accepts timeout
- `Constructor_NullTimeout_FallsBackToResolveTimeout` — verifies null triggers ResolveTimeout()
- `TimeoutErrorMessage_ContainsTWIG_AZ_TIMEOUT_Guidance` — verifies error message content
- `Constructor_3ParamCtor_StillChainsProperly` — backward compat regression test

**Missing tests** (per child Issue specifications):
- `Constructor_WithExplicit30sTimeout_ProducesCorrectInstance` (#1878)
- `GetAccessTokenAsync_CustomTimeout_UsesInjectedValue` (#1876)
- `ResolveTimeout_InvalidEnvVar_FallsBackToDefault` (#1880)
- `ResolveTimeout_ValidEnvVar_ReturnsOverride` (#1879)

## Problem Statement

When `az account get-access-token` is invoked for the first time on a machine (cold start),
or after a token cache eviction, the Azure CLI may take 15–30 seconds to complete — well
beyond the current 10-second hard limit. This causes:

1. **Spurious `AdoAuthenticationException`** — users see `"Azure CLI timed out after 10s"`
   even though `az` would have succeeded given more time.
2. **No self-service path** — the error message provides no actionable override.
3. **Cannot adapt to environment** — CI/CD runners, VMs with cold credential caches, and
   networks with high latency all need longer timeouts, but the value is baked in.

## Goals and Non-Goals

### Goals

1. Make the `az` process timeout configurable via `TWIG_AZ_TIMEOUT` (integer seconds).
2. Keep the default timeout at 10 seconds for existing users.
3. Improve the timeout error message to mention `TWIG_AZ_TIMEOUT` for self-diagnosis.
4. Maintain full backward compatibility — no breaking changes to APIs.
5. Support testability — inject custom timeout without env var side effects.

### Non-Goals

- Changing the default timeout value (stays at 10s).
- Adding retry logic for timed-out `az` invocations.
- Surfacing the timeout in `twig.json` configuration.
- Adding a global timeout configuration system.

## Requirements

### Functional Requirements

- **FR-1:** `TWIG_AZ_TIMEOUT` env var controls the `az` process timeout in seconds.
- **FR-2:** Invalid values (non-numeric, zero, negative) silently fall back to 10s default.
- **FR-3:** Timeout error message includes `TWIG_AZ_TIMEOUT` guidance text.
- **FR-4:** Constructor chain supports injecting a custom `TimeSpan?` for testing.

### Non-Functional Requirements

- **NFR-1:** Env var read once at construction time, not per-call.
- **NFR-2:** Zero allocation overhead on the hot path (cached token return).
- **NFR-3:** AOT-compatible — no reflection, no dynamic dispatch.
- **NFR-4:** All existing tests pass without modification.

## Proposed Design

### Architecture Overview

The change is localized to `AzCliAuthProvider` and its test file. No new types, interfaces,
or files are introduced. The architecture remains:

```
NetworkServiceModule / MCP Program
  └─► MsalCacheTokenProvider (decorator)
       └─► AzCliAuthProvider  ← timeout override here
            └─► az account get-access-token (OS process)
```

### Key Components

#### 1. `DefaultProcessTimeout` Constant + `_processTimeout` Instance Field

```csharp
private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromSeconds(10);
private readonly TimeSpan _processTimeout;
```

#### 2. `ResolveTimeout()` Static Helper

Reads `TWIG_AZ_TIMEOUT`, parses as positive integer, returns `TimeSpan` or default.

```csharp
private static TimeSpan ResolveTimeout()
{
    var value = Environment.GetEnvironmentVariable("TWIG_AZ_TIMEOUT");
    if (value is not null
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0)
        return TimeSpan.FromSeconds(seconds);
    return DefaultProcessTimeout;
}
```

#### 3. Constructor Chain (5 overloads)

All chain to the fully-parameterized 4-param constructor. The existing 3-param overload
(`processStarter, clock, cachePath`) is preserved, passing `null` as `processTimeout`.

#### 4. Improved Error Message

```csharp
$"Azure CLI timed out after {_processTimeout.TotalSeconds}s. Set TWIG_AZ_TIMEOUT=30 to increase the timeout."
```

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | Env var read at construction, not per-call | Avoids repeated lookups; value shouldn't change mid-process |
| DD-2 | `TimeSpan?` parameter for tests | Simpler than env var injection; tests pass concrete values |
| DD-3 | Preserve 3-param constructor | Existing test call sites compile unchanged |
| DD-4 | Silent fallback on invalid values | Matches `TWIG_TELEMETRY_ENDPOINT` precedent |
| DD-5 | `NumberStyles.None` for parsing | Rejects hex/whitespace/signs; matches existing patterns |

## Dependencies

- **External:** None (`Environment.GetEnvironmentVariable` is BCL).
- **Internal:** Self-contained within `AzCliAuthProvider`.
- **Sequencing:** Independent of other work.

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Should there be an upper bound on `TWIG_AZ_TIMEOUT` (e.g., 300s)? | Low | Any positive integer is accepted. Users setting extreme values are opting in. |

## Files Affected

### New Files

_None._

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | `DefaultProcessTimeout` const, `_processTimeout` field, `ResolveTimeout()`, 4-param ctor, constructor chain rewiring, `CancelAfter`/error message update. **Status: Complete.** |
| `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | Add remaining tests for 30s timeout, custom timeout enforcement, env var override, env var fallback. **Status: In Progress.** |

## ADO Work Item Structure

This is an **Issue** (#1858). Ten child Issues exist; Tasks are defined under each.

---

### Issue #1886 — Add DefaultProcessTimeout const, _processTimeout instance field, and ResolveTimeout() env var helper

**Goal:** Replace the static `ProcessTimeout` field with a `DefaultProcessTimeout` constant,
an instance-level `_processTimeout` field, and a `ResolveTimeout()` helper that reads the
`TWIG_AZ_TIMEOUT` environment variable.

**Prerequisites:** None (foundational change).

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1886-1 | Rename `ProcessTimeout` to `DefaultProcessTimeout` (keep as `static readonly TimeSpan`) | `AzCliAuthProvider.cs` | ~2 LoC | DONE |
| T-1886-2 | Add `private readonly TimeSpan _processTimeout` instance field | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1886-3 | Implement `private static TimeSpan ResolveTimeout()` — read `TWIG_AZ_TIMEOUT` env var, parse with `int.TryParse` / `NumberStyles.None`, return `TimeSpan.FromSeconds` for valid positive ints, else `DefaultProcessTimeout` | `AzCliAuthProvider.cs` | ~12 LoC | DONE |

**Acceptance Criteria:**
- [x] `DefaultProcessTimeout` is a static readonly field set to 10 seconds
- [x] `_processTimeout` instance field exists
- [x] `ResolveTimeout()` reads `TWIG_AZ_TIMEOUT` and parses correctly
- [x] Invalid/missing env var falls back to `DefaultProcessTimeout`

---

### Issue #1870 — Add private static ResolveTimeout() method

**Goal:** Implement the `ResolveTimeout()` method that reads `TWIG_AZ_TIMEOUT` with
positive-integer parsing and `DefaultProcessTimeout` fallback.

**Prerequisites:** Issue #1886 (provides `DefaultProcessTimeout`).

**Note:** This Issue's scope is a subset of Issue #1886. The implementation was completed
as part of #1886. Tasks here confirm the method meets specifications.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1870-1 | Verify `ResolveTimeout()` exists and reads `TWIG_AZ_TIMEOUT` | `AzCliAuthProvider.cs` | ~0 LoC (verify) | DONE |
| T-1870-2 | Verify `NumberStyles.None` rejects hex, whitespace, negative signs | `AzCliAuthProvider.cs` | ~0 LoC (verify) | DONE |

**Acceptance Criteria:**
- [x] `ResolveTimeout()` is `private static`
- [x] Uses `int.TryParse` with `NumberStyles.None` and `CultureInfo.InvariantCulture`
- [x] Returns `DefaultProcessTimeout` for null, empty, non-numeric, zero, or negative values

---

### Issue #1873 — Add 4-param internal constructor accepting TimeSpan? processTimeout

**Goal:** Add the fully-parameterized constructor that accepts an optional `TimeSpan?`
for the process timeout, resolving to `ResolveTimeout()` when null.

**Prerequisites:** Issue #1886 (provides `_processTimeout` and `ResolveTimeout()`).

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1873-1 | Add `internal AzCliAuthProvider(Func<ProcessStartInfo, Process?>, Func<DateTimeOffset>, string, TimeSpan?)` constructor | `AzCliAuthProvider.cs` | ~8 LoC | DONE |
| T-1873-2 | Set `_processTimeout = processTimeout ?? ResolveTimeout()` in constructor body | `AzCliAuthProvider.cs` | ~1 LoC | DONE |

**Acceptance Criteria:**
- [x] 4-param constructor exists with `TimeSpan?` as the 4th parameter
- [x] Null timeout triggers `ResolveTimeout()` fallback
- [x] Explicit timeout is stored directly in `_processTimeout`

---

### Issue #1887 — Rewire constructor chain + update CancelAfter

**Goal:** Rewire all existing constructors to chain through the 4-param constructor and
update `GetAccessTokenAsync` to use `_processTimeout` in `CancelAfter` and the error message.

**Prerequisites:** Issue #1873 (provides 4-param constructor).

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1887-1 | Update parameterless ctor to chain: `this(psi => Process.Start(psi), () => DateTimeOffset.UtcNow, DefaultCachePath, null)` | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1887-2 | Update 1-param ctor (`processStarter`) to chain with `null` timeout | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1887-3 | Update 2-param ctor (`processStarter, clock`) to chain with `null` timeout | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1887-4 | Update 3-param ctor (`processStarter, clock, cachePath`) to chain with `null` timeout | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1887-5 | Replace `CancelAfter(ProcessTimeout)` with `CancelAfter(_processTimeout)` | `AzCliAuthProvider.cs` | ~1 LoC | DONE |
| T-1887-6 | Update timeout error message to: `$"Azure CLI timed out after {_processTimeout.TotalSeconds}s. Set TWIG_AZ_TIMEOUT=30 to increase the timeout."` | `AzCliAuthProvider.cs` | ~2 LoC | DONE |

**Acceptance Criteria:**
- [x] All 5 constructors chain through the 4-param constructor
- [x] `CancelAfter` uses `_processTimeout` (not static field)
- [x] Error message includes `TWIG_AZ_TIMEOUT=30` guidance
- [x] Existing 3-param constructor signature preserved (backward compat)

---

### Issue #1878 — Add test Constructor_WithExplicit30sTimeout_ProducesCorrectInstance

**Goal:** Verify the 4-param constructor correctly stores a 30-second explicit timeout.

**Prerequisites:** Issue #1873.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1878-1 | Write `Constructor_WithExplicit30sTimeout_ProducesCorrectInstance` test: construct with `TimeSpan.FromSeconds(30)`, invoke `GetAccessTokenAsync` with fast fake process, assert success | `AzCliAuthProviderTests.cs` | ~12 LoC | TO DO |
| T-1878-2 | Verify test doesn't conflict with existing `Constructor_ExplicitTimeout_UsesProvidedValue` | `AzCliAuthProviderTests.cs` | ~0 LoC | TO DO |

**Acceptance Criteria:**
- [ ] Test constructs with explicit 30s `TimeSpan` and asserts correct behavior
- [ ] Test is independent of environment variables

---

### Issue #1876 — Add test GetAccessTokenAsync_CustomTimeout_UsesInjectedValue

**Goal:** Verify a custom timeout injected via constructor is enforced during execution.

**Prerequisites:** Issue #1887.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1876-1 | Write test: construct with `TimeSpan.FromMilliseconds(1)`, use `CreateSlowProcess()`, assert `AdoAuthenticationException` thrown | `AzCliAuthProviderTests.cs` | ~15 LoC | TO DO |

**Acceptance Criteria:**
- [ ] Test uses tiny custom timeout (1ms) to force timeout
- [ ] Test asserts `AdoAuthenticationException` is thrown

---

### Issue #1877 — Add test GetAccessTokenAsync_Timeout_ErrorMessageIncludesEnvVarGuidance

**Goal:** Verify timeout error message includes `TWIG_AZ_TIMEOUT` guidance.

**Prerequisites:** Issue #1887.

**Note:** Covered by existing test `TimeoutErrorMessage_ContainsTWIG_AZ_TIMEOUT_Guidance`.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1877-1 | Verify existing test covers this requirement | `AzCliAuthProviderTests.cs` | ~0 LoC | DONE |

**Acceptance Criteria:**
- [x] Test asserts error message contains `"TWIG_AZ_TIMEOUT=30"`

---

### Issue #1880 — Add test ResolveTimeout_InvalidEnvVar_FallsBackToDefault

**Goal:** Verify invalid `TWIG_AZ_TIMEOUT` values fall back to 10s default.

**Prerequisites:** Issue #1886.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1880-1 | Write `[Theory]` test with `[InlineData("abc")]`, `[InlineData("0")]`, `[InlineData("-5")]`, `[InlineData("")]`: set env var, construct provider without explicit timeout, use fast fake process, assert success (proving 10s default, not 0s). Clean up env var in `finally`. | `AzCliAuthProviderTests.cs` | ~20 LoC | TO DO |

**Acceptance Criteria:**
- [ ] Covers "abc", "0", "-5", "" as invalid values
- [ ] Provider falls back to 10s default for all
- [ ] Env var cleaned up after each test

---

### Issue #1879 — Add test ResolveTimeout_ValidEnvVar_ReturnsOverride

**Goal:** Verify valid `TWIG_AZ_TIMEOUT` value is parsed and used.

**Prerequisites:** Issue #1886.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1879-1 | Write test: set `TWIG_AZ_TIMEOUT=1`, construct without explicit timeout, use `CreateSlowProcess()`, assert `AdoAuthenticationException` thrown within ~1s. Clean up env var in `finally`. | `AzCliAuthProviderTests.cs` | ~18 LoC | TO DO |

**Acceptance Criteria:**
- [ ] Env var override is applied (verified by triggering timeout)
- [ ] Env var cleaned up after test

---

### Issue #1861 — Add test: custom timeout via constructor parameter

**Goal:** Verify custom timeout injection works end-to-end.

**Prerequisites:** Issue #1873.

**Note:** Partially covered by existing `Constructor_ExplicitTimeout_UsesProvidedValue`.

**Tasks:**

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1861-1 | Verify existing test covers constructor acceptance | `AzCliAuthProviderTests.cs` | ~0 LoC | DONE |
| T-1861-2 | Write `CustomTimeout_ViaConstructor_EnforcedOnSlowProcess`: 1ms timeout, slow process, assert timeout fires | `AzCliAuthProviderTests.cs` | ~15 LoC | TO DO |

**Acceptance Criteria:**
- [ ] Custom timeout is accepted by constructor
- [ ] Custom timeout is enforced during process execution

## PR Groups

All changes touch only 2 files and total ~80 LoC of new test code (production code is
complete). Splitting into multiple PRs would create unreviewed gaps. Single PR group.

| PR Group | Issues | Classification | Est. Files | Est. LoC | Description | Successors |
|----------|--------|----------------|------------|----------|-------------|------------|
| PG-1 | #1878, #1876, #1877, #1880, #1879, #1861 | **deep** | 1 | ~80 | Add remaining test methods for timeout override behavior: 30s constructor, custom timeout enforcement, env var valid/invalid cases. Production code (Issues #1886, #1870, #1873, #1887) is already complete. | _(none)_ |

**Execution order within PG-1:**
1. T-1878-1 (30s constructor test) — no dependencies
2. T-1876-1 (custom timeout enforcement) — no dependencies
3. T-1880-1 (invalid env var fallback) — no dependencies
4. T-1879-1 (valid env var override) — no dependencies
5. T-1861-2 (constructor enforcement) — may deduplicate with T-1876-1

All test tasks are independent and can be implemented in any order.
