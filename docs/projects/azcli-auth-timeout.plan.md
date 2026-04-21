# AzCliAuthProvider Timeout Increase & Configurability

| Field | Value |
|---|---|
| **Work Item** | #1858 |
| **Type** | Issue |
| **Status** | Draft |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

The `AzCliAuthProvider` hardcodes a 10-second process timeout for `az account get-access-token`,
which is too aggressive for cold-start token refresh scenarios. When Azure CLI needs to refresh
an expired token or load components for the first time, the process commonly takes 15–30+ seconds,
causing spurious `AdoAuthenticationException` failures. This design raises the default timeout
to 30 seconds and introduces a `TWIG_AZ_TIMEOUT` environment variable override, following the
existing `TWIG_*` env-var pattern used for `TWIG_PAT`, `TWIG_TELEMETRY_ENDPOINT`, and `TWIG_DEBUG`.
The timeout constant is refactored to be injectable for testability.

## Background

### Current Architecture

The auth stack has three providers behind `IAuthenticationProvider`:

1. **`PatAuthProvider`** — reads a PAT from `$TWIG_PAT` or config; no process invocation.
2. **`MsalCacheTokenProvider`** — decorator that reads the MSAL token cache file
   (`~/.azure/msal_token_cache.json`) before falling back to its inner provider.
3. **`AzCliAuthProvider`** — shells out to `az account get-access-token` with a hardcoded
   10-second process timeout.

The production chain is `MsalCacheTokenProvider(AzCliAuthProvider)` in the CLI
(`NetworkServiceModule.cs:35-36`) and bare `AzCliAuthProvider` in the MCP server
(`Twig.Mcp/Program.cs:89`).

### Call-Site Audit

Every downstream consumer of `IAuthenticationProvider.GetAccessTokenAsync()` is affected
by the timeout because `AdoAuthenticationException` propagates unhandled through these callers:

| File | Method | Current Usage | Impact |
|------|--------|--------------|--------|
| `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs` | `GetAccessTokenAsync` (L105) | Falls back to `_inner.GetAccessTokenAsync(ct)` | Timeout exception propagates up through semaphore |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | `SendAsync` (L293) | `await _authProvider.GetAccessTokenAsync(ct)` | All REST operations fail on timeout |
| `src/Twig.Infrastructure/Ado/AdoIterationService.cs` | `SendAsync` (L340) | `await _authProvider.GetAccessTokenAsync(ct)` | Sprint/iteration lookup fails |
| `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | `SendAsync` (L168) | `await _authProvider.GetAccessTokenAsync(ct)` | Git/PR operations fail |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | `AddTwigNetworkServices` (L35-36) | Constructs `new MsalCacheTokenProvider(new AzCliAuthProvider())` | Factory site — no runtime impact |
| `src/Twig.Mcp/Program.cs` | `CreateAuthProvider` (L89) | Constructs bare `new AzCliAuthProvider()` | MCP server has NO MSAL cache layer — highest timeout risk |

**Key observation:** The MCP server (`Program.cs:89`) creates `AzCliAuthProvider` directly
without the `MsalCacheTokenProvider` wrapper, making it the most likely caller to hit the
cold-start timeout path.

### Prior Art

- `TWIG_PAT`: env-var for PAT auth — read by `PatAuthProvider`.
- `TWIG_TELEMETRY_ENDPOINT`: env-var for telemetry endpoint — read by `TelemetryClient`.
- `TWIG_DEBUG`: env-var for debug mode — read by `HookHandlerCommand`.
- All follow the `TWIG_` prefix convention and are read once at startup.

## Problem Statement

The `AzCliAuthProvider` enforces a 10-second timeout (`ProcessTimeout = TimeSpan.FromSeconds(10)`)
on the `az account get-access-token` subprocess. This is too aggressive for two scenarios:

1. **Cold-start token refresh** — when the MSAL token cache is empty or expired, Azure CLI
   must perform an OAuth2 token refresh against AAD/Entra. On first run, CI environments, or
   after long idle periods, this routinely takes 15–25 seconds.
2. **Azure CLI component loading** — `az` may auto-update or lazy-load its Python modules on
   first invocation, adding 5–15 seconds of startup overhead.

The 10-second timeout causes:
- `AdoAuthenticationException: Azure CLI timed out after 10s. Ensure 'az' is responsive.`
- No user-configurable escape hatch — the only workaround is to manually run `az account
  get-access-token` beforehand to warm the cache.
- MCP server is especially vulnerable because it skips the `MsalCacheTokenProvider` fast path.

## Goals and Non-Goals

### Goals

1. **Raise the default timeout** to 30 seconds to accommodate cold-start token refresh.
2. **Add `TWIG_AZ_TIMEOUT` environment variable** to let users override the timeout (in seconds).
3. **Make the timeout injectable** in the constructor for unit testing.
4. **Update the error message** on timeout to mention the `TWIG_AZ_TIMEOUT` override.
5. **Add unit tests** for the new timeout behavior (env var parsing, custom timeout, error message).

### Non-Goals

- **Config-file setting for timeout** — env var is sufficient; config-file would require
  plumbing through DI and is overkill for a rarely-used escape hatch.
- **Retry logic** — timeouts are a hard cutoff; retrying a slow `az` command won't help if
  it's genuinely unresponsive.
- **MCP server MSAL cache integration** — wrapping the MCP server's `AzCliAuthProvider` in
  `MsalCacheTokenProvider` is a separate improvement (orthogonal to timeout configuration).

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| FR-1 | Default process timeout for `az account get-access-token` is 30 seconds |
| FR-2 | `TWIG_AZ_TIMEOUT` env var overrides the timeout (integer seconds, ≥1) |
| FR-3 | Invalid `TWIG_AZ_TIMEOUT` values (non-numeric, ≤0) are silently ignored, falling back to default |
| FR-4 | Timeout error message includes `TWIG_AZ_TIMEOUT` guidance |
| FR-5 | Timeout value is injectable via constructor for test isolation |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NFR-1 | No new dependencies — uses `Environment.GetEnvironmentVariable` only |
| NFR-2 | AOT-compatible — no reflection |
| NFR-3 | Telemetry safe — timeout value is a generic number, safe to emit |
| NFR-4 | Backward-compatible — existing callers with no env var get 30s default (was 10s) |

## Proposed Design

### Architecture Overview

No new components or interfaces. The change is localized to `AzCliAuthProvider`:

```
┌─────────────────────────────────────────┐
│         AzCliAuthProvider               │
│  ┌───────────────────────────────────┐  │
│  │ ProcessTimeout (was: 10s static)  │  │
│  │ → Now: instance field, default 30s│  │
│  │ → Overridden by TWIG_AZ_TIMEOUT  │  │
│  └───────────────────────────────────┘  │
│  ┌───────────────────────────────────┐  │
│  │ ResolveTimeout() (new helper)     │  │
│  │ → Reads env var once at ctor time │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

### Key Components

#### 1. Timeout Resolution (`AzCliAuthProvider`)

The static `ProcessTimeout` field becomes an instance `readonly TimeSpan _processTimeout`.
A new static helper `ResolveTimeout()` reads `TWIG_AZ_TIMEOUT` at construction time:

```csharp
private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromSeconds(30);

private static TimeSpan ResolveTimeout()
{
    var envValue = Environment.GetEnvironmentVariable("TWIG_AZ_TIMEOUT");
    if (envValue is not null
        && int.TryParse(envValue, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 1)
    {
        return TimeSpan.FromSeconds(seconds);
    }
    return DefaultProcessTimeout;
}
```

#### 2. Constructor Changes

The existing four-constructor overload chain gains a `TimeSpan? processTimeout` parameter
on the internal test constructors. The production constructor calls `ResolveTimeout()`:

```csharp
// Production constructor
public AzCliAuthProvider()
    : this(psi => Process.Start(psi), () => DateTimeOffset.UtcNow,
           DefaultCachePath, ResolveTimeout())
{ }

// Full test constructor
internal AzCliAuthProvider(
    Func<ProcessStartInfo, Process?> processStarter,
    Func<DateTimeOffset> clock,
    string cachePath,
    TimeSpan? processTimeout = null)
{
    _processStarter = processStarter;
    _clock = clock;
    _cachePath = cachePath;
    _processTimeout = processTimeout ?? ResolveTimeout();
}
```

#### 3. Error Message Update

The timeout error message changes from:
```
Azure CLI timed out after 10s. Ensure 'az' is responsive.
```
To:
```
Azure CLI timed out after 30s. Ensure 'az' is responsive.
Set TWIG_AZ_TIMEOUT=<seconds> to adjust the timeout.
```

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| 30s default (not 60s) | Balances cold-start coverage with reasonable UX for genuine hangs. Most cold starts resolve in 15–25s. |
| Env var (not config file) | Follows existing `TWIG_*` pattern. Timeout is a machine-level concern, not a workspace concern. |
| Read at constructor time | Single read, no per-call overhead. Provider is singleton. |
| Silent fallback on invalid env var | Consistent with `TWIG_TELEMETRY_ENDPOINT` behavior — invalid values are no-ops. |
| Instance field (not static) | Enables test isolation without ambient state leaking between tests. |

## Dependencies

- **External:** None — only uses `System.Environment.GetEnvironmentVariable`.
- **Internal:** No new dependencies between projects.
- **Sequencing:** None — this is a self-contained change.

## Impact Analysis

- **Backward compatibility:** The default changes from 10s to 30s. Users who previously
  relied on the 10s timeout to detect a hung `az` CLI will wait longer. This is intentional
  and the fix for the reported issue. Users needing faster failure can set `TWIG_AZ_TIMEOUT=10`.
- **Performance:** No measurable impact on the success path (in-memory or file cache hit).
  The timeout only applies when shelling out to `az`, which is already an expensive operation.
- **MCP server:** Benefits directly — bare `AzCliAuthProvider` now gets 30s by default.

## Open Questions

| # | Question | Severity | Status |
|---|----------|----------|--------|
| 1 | Should the MCP server wrap `AzCliAuthProvider` in `MsalCacheTokenProvider` to reduce cold-start frequency? | Low | Deferred — separate work item |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| *(none)* | |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | Replace static `ProcessTimeout` with instance `_processTimeout`; add `ResolveTimeout()` helper; update constructor chain to accept optional timeout; update error message |
| `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | Add tests for custom timeout, env var override, invalid env var fallback, updated error message |

## ADO Work Item Structure

**Parent Issue:** #1858 — AzCliAuthProvider 10s timeout too aggressive for cold-start token refresh

### Task 1: Raise default timeout and add env-var override

- **Goal:** Refactor `AzCliAuthProvider` to use a configurable timeout with 30s default
- **Prerequisites:** None
- **Tasks:**

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T1.1 | Replace `ProcessTimeout` static field with instance `_processTimeout`; add `ResolveTimeout()` static helper; update constructor chain to thread timeout through | `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | ~60 LoC changed | TO DO |
| T1.2 | Update timeout error message to include `TWIG_AZ_TIMEOUT` guidance | `src/Twig.Infrastructure/Auth/AzCliAuthProvider.cs` | ~5 LoC changed | TO DO |

- **Acceptance Criteria:**
  - [ ] Default timeout is 30 seconds
  - [ ] `TWIG_AZ_TIMEOUT=45` sets timeout to 45 seconds
  - [ ] Invalid `TWIG_AZ_TIMEOUT` values fall back to 30s
  - [ ] Error message on timeout mentions `TWIG_AZ_TIMEOUT`
  - [ ] All existing tests pass without modification

### Task 2: Add unit tests for timeout configurability

- **Goal:** Verify timeout resolution logic and error message content
- **Prerequisites:** Task 1
- **Tasks:**

| Task ID | Description | Files | Effort Estimate | Status |
|---------|-------------|-------|-----------------|--------|
| T2.1 | Add test: custom timeout via constructor parameter | `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | ~20 LoC | TO DO |
| T2.2 | Add test: error message on timeout includes `TWIG_AZ_TIMEOUT` hint | `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | ~15 LoC | TO DO |
| T2.3 | Add test: default timeout is 30 seconds (constructor produces correct instance) | `tests/Twig.Infrastructure.Tests/Auth/AzCliAuthProviderTests.cs` | ~10 LoC | TO DO |

- **Acceptance Criteria:**
  - [ ] Test confirms custom timeout is respected
  - [ ] Test confirms error message includes `TWIG_AZ_TIMEOUT`
  - [ ] Test confirms 30s default
  - [ ] All tests pass (including existing ones)

## PR Groups

| PR Group | Tasks | Type | Est. LoC | Est. Files | Successor |
|----------|-------|------|----------|------------|-----------|
| PG-1 | T1.1, T1.2, T2.1, T2.2, T2.3 | Deep | ~150 | 2 | *(none)* |

**PG-1: Raise default timeout and add TWIG_AZ_TIMEOUT override**
- **Classification:** Deep — few files, focused behavioral change with test coverage
- **Scope:** Single source file + single test file
- **Rationale:** All tasks are tightly coupled and should be reviewed atomically.
  Splitting into separate PRs would create unnecessary churn on the same 2 files.

