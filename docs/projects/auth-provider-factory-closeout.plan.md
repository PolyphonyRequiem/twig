# AuthProviderFactory — Centralize Auth Provider Creation

| Field | Value |
|---|---|
| **Work Item** | #1842 |
| **Parent Epic** | #1673 — Read MSAL token cache directly instead of shelling out to az CLI |
| **Type** | Issue (Closeout) |
| **Status** | ✅ Done |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

---

## Executive Summary

Auth provider creation logic is duplicated across two entry points (`NetworkServiceModule.cs`
for the CLI and `Twig.Mcp/Program.cs` for the MCP server) with an inconsistency: the CLI wraps
`AzCliAuthProvider` in `MsalCacheTokenProvider` for cache-first token resolution, but the MCP
server uses a bare `AzCliAuthProvider`, missing the MSAL cache optimization entirely. This design
introduces an `AuthProviderFactory` internal static class that centralizes the creation logic,
fixes the MCP gap, and provides a single testable surface for auth provider construction.

## Background

### Current Architecture

The auth stack implements `IAuthenticationProvider` (defined in `Twig.Domain.Interfaces`) with
three concrete providers in `Twig.Infrastructure.Auth`:

1. **`PatAuthProvider`** — reads a PAT from `$TWIG_PAT` env var or config file; returns
   `Basic base64(:PAT)` header values. No external process invocation.
2. **`AzCliAuthProvider`** — shells out to `az account get-access-token`; maintains both
   in-memory (50-min TTL) and cross-process file cache (`~/.twig/.token-cache`).
3. **`MsalCacheTokenProvider`** — decorator that reads Azure CLI's MSAL token cache file
   (`~/.azure/msal_token_cache.json`) before falling back to its inner provider. Saves
   ~100–300ms of process-creation overhead when a valid cached token exists.

The intended production chain for `azcli` auth is:
```
MsalCacheTokenProvider → AzCliAuthProvider
```

### Call-Site Audit — Auth Provider Construction

| File | Method/Location | Current Logic | Issue |
|------|----------------|---------------|-------|
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | `AddTwigNetworkServices` (L30-37) | DI factory: `"pat"` → `PatAuthProvider()`, else → `new MsalCacheTokenProvider(new AzCliAuthProvider())` | ✅ Correct chain |
| `src/Twig.Mcp/Program.cs` | `CreateAuthProvider()` (L78-90) | Static local: loops workspace configs for `"pat"` → `PatAuthProvider()`, else → bare `new AzCliAuthProvider()` | ⚠️ Missing `MsalCacheTokenProvider` wrapper |

### What Changed (Epic #1673)

Epic #1673 introduced `MsalCacheTokenProvider` and wired it into `NetworkServiceModule.cs`.
However, `Twig.Mcp/Program.cs` was created/modified separately and was never updated to use
the MSAL cache decorator, leaving the MCP server with ~100–300ms slower auth on every cold
invocation when a valid MSAL-cached token exists.

## Problem Statement

1. **Duplication**: Auth provider construction logic is duplicated across two files with
   no shared abstraction, violating DRY and increasing the risk of future drift.
2. **Inconsistency**: The MCP server's `CreateAuthProvider()` does not wrap `AzCliAuthProvider`
   in `MsalCacheTokenProvider`, so MCP tools always shell out to `az` even when a valid
   cached MSAL token is available.
3. **Testability**: Neither call site is independently testable — the CLI's logic is buried
   inside a DI factory lambda, and the MCP server's is a static local function.

## Goals and Non-Goals

### Goals

- **G-1**: Create a single `AuthProviderFactory` class that encapsulates auth provider
  construction logic, eliminating duplication.
- **G-2**: Fix the MCP server to use `MsalCacheTokenProvider` wrapping `AzCliAuthProvider`,
  matching the CLI's behavior.
- **G-3**: Add comprehensive unit tests for the factory covering both `pat` and `azcli`
  methods, default behavior, and case-insensitivity.
- **G-4**: Both existing call sites (`NetworkServiceModule.cs` and `Twig.Mcp/Program.cs`)
  delegate to the factory.

### Non-Goals

- **NG-1**: Changing the auth provider implementations themselves (PAT, AzCli, MSAL).
- **NG-2**: Adding new auth methods (e.g., device code flow, managed identity).
- **NG-3**: Modifying the `AuthConfig` model or config file format.
- **NG-4**: Changing `Twig.Tui/Program.cs` — it uses only local services and has no
  auth provider construction.

## Requirements

### Functional

- **FR-1**: `AuthProviderFactory.Create(string authMethod)` returns `PatAuthProvider`
  when `authMethod` is `"pat"` (case-insensitive).
- **FR-2**: `AuthProviderFactory.Create(string authMethod)` returns
  `MsalCacheTokenProvider(AzCliAuthProvider)` for `"azcli"` or any non-`"pat"` value.
- **FR-3**: `NetworkServiceModule.AddTwigNetworkServices` delegates to
  `AuthProviderFactory.Create()`.
- **FR-4**: `Twig.Mcp/Program.cs` delegates to `AuthProviderFactory.Create()`.

### Non-Functional

- **NFR-1**: No new dependencies — the factory uses only existing types.
- **NFR-2**: AOT-compatible — no reflection or dynamic type loading.
- **NFR-3**: All existing auth tests continue to pass unchanged.

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  AuthProviderFactory (internal static class)                │
│  ┌───────────────────────────────────────────────────────┐  │
│  │ Create(string authMethod)                             │  │
│  │   "pat"   → PatAuthProvider                           │  │
│  │   default → MsalCacheTokenProvider(AzCliAuthProvider) │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         ▲                              ▲
         │                              │
  NetworkServiceModule           Twig.Mcp/Program.cs
  (DI factory lambda)            (CreateAuthProvider)
```

The factory is a thin, static creation method that encapsulates the decision tree.
Both call sites pass `config.Auth.Method` (or equivalent) and receive a fully-composed
`IAuthenticationProvider`.

### Key Components

#### `AuthProviderFactory` (new)

- **Location**: `src/Twig.Infrastructure/Auth/AuthProviderFactory.cs`
- **Visibility**: `internal static class` (same assembly as all auth providers)
- **Method**: `static IAuthenticationProvider Create(string authMethod)`
- **Logic**:
  - `"pat"` (case-insensitive) → `new PatAuthProvider()`
  - Default → `new MsalCacheTokenProvider(new AzCliAuthProvider())`
- **Rationale**: Static because it has no state and all provider constructors are
  parameterless (default configs). Internal because all callers are within the
  Infrastructure assembly or `InternalsVisibleTo` projects.

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Static class vs. injectable factory | Static class | No state, no dependencies to inject. Factory pattern only applies when construction has varying inputs, but auth method is the only discriminator and comes from config. |
| Location in `Twig.Infrastructure.Auth` | Same namespace as providers | Follows existing convention where all auth types live together. Factory needs access to `internal` provider constructors. |
| Single `Create()` method vs. separate methods | Single method with switch | Matches the existing two-branch decision (`pat` vs default). Adding more branches later just extends the switch. |
| MCP server `CreateAuthProvider` replacement | Full replacement | The existing static local function becomes a one-liner calling the factory, or is inlined entirely. |

## Dependencies

### External Dependencies
None — all types already exist in `Twig.Infrastructure`.

### Internal Dependencies
- `PatAuthProvider`, `AzCliAuthProvider`, `MsalCacheTokenProvider` — all in
  `Twig.Infrastructure.Auth`
- `IAuthenticationProvider` — in `Twig.Domain.Interfaces`

### Sequencing Constraints
- Task #1935 (create factory) must complete before Task #1936 (update call sites)
- Task #1937 (tests) can be developed in parallel with #1935 but must run after it compiles

## Security Considerations

The factory does not introduce any new security surface. It merely centralizes existing
provider construction. The same auth methods (PAT, AzCli+MSAL) remain available with
identical security characteristics. No credentials are handled by the factory itself.

## Open Questions

| # | Question | Severity | Resolution |
|---|----------|----------|------------|
| 1 | Should the factory accept `configPat` (the PAT value from config) so `PatAuthProvider` can be constructed with the config-provided PAT directly? Currently `PatAuthProvider()` reads env var and config internally. | Low | No — `PatAuthProvider`'s default constructor already handles this. The factory just selects which provider to create; it doesn't need to forward config values. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Auth/AuthProviderFactory.cs` | Static factory class for auth provider creation |
| `tests/Twig.Infrastructure.Tests/Auth/AuthProviderFactoryTests.cs` | Unit tests for the factory |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Replace inline auth creation lambda (L30-37) with `AuthProviderFactory.Create(cfg.Auth.Method)` |
| `src/Twig.Mcp/Program.cs` | Replace `CreateAuthProvider()` static local function (L78-90) with call to `AuthProviderFactory.Create()` |

## ADO Work Item Structure

Issue #1842 already has three child Tasks. The plan maps to them directly.

### Task #1935: Create AuthProviderFactory internal static class

**Goal**: Create the `AuthProviderFactory` class that centralizes auth provider construction.

**Prerequisites**: None

**Tasks**:

| Sub-Task | Description | Files | Effort |
|----------|-------------|-------|--------|
| 1935-A | Create `AuthProviderFactory.cs` with `Create(string authMethod)` method | `src/Twig.Infrastructure/Auth/AuthProviderFactory.cs` | ~20 LoC |
| 1935-B | Verify the class compiles and is AOT-compatible (no reflection) | Build verification | ~5 min |

**Acceptance Criteria**:
- [ ] `AuthProviderFactory.Create("pat")` returns a `PatAuthProvider`
- [ ] `AuthProviderFactory.Create("azcli")` returns a `MsalCacheTokenProvider` wrapping `AzCliAuthProvider`
- [ ] Class is `internal static` with no constructor parameters
- [ ] Project builds with `TreatWarningsAsErrors=true`

---

### Task #1936: Update Program.cs to use AuthProviderFactory

**Goal**: Replace duplicated auth creation logic in both entry points with factory calls.

**Prerequisites**: Task #1935

**Tasks**:

| Sub-Task | Description | Files | Effort |
|----------|-------------|-------|--------|
| 1936-A | Replace DI lambda in `NetworkServiceModule.AddTwigNetworkServices` with `AuthProviderFactory.Create()` | `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | ~5 LoC delta |
| 1936-B | Replace `CreateAuthProvider()` in MCP `Program.cs` with `AuthProviderFactory.Create()` | `src/Twig.Mcp/Program.cs` | ~15 LoC delta |
| 1936-C | Verify both projects build and existing tests pass | Build + test | ~10 min |

**Acceptance Criteria**:
- [ ] `NetworkServiceModule.cs` delegates to `AuthProviderFactory.Create()`
- [ ] `Twig.Mcp/Program.cs` no longer contains `CreateAuthProvider()` static local function
- [ ] MCP server now wraps `AzCliAuthProvider` in `MsalCacheTokenProvider` (bug fix)
- [ ] All existing tests pass

---

### Task #1937: Add AuthProviderFactory unit tests

**Goal**: Add unit tests covering all factory creation paths.

**Prerequisites**: Task #1935

**Tasks**:

| Sub-Task | Description | Files | Effort |
|----------|-------------|-------|--------|
| 1937-A | Create test class with tests for `"pat"`, `"azcli"`, case-insensitivity, and default behavior | `tests/Twig.Infrastructure.Tests/Auth/AuthProviderFactoryTests.cs` | ~60 LoC |
| 1937-B | Verify all tests pass | Test execution | ~5 min |

**Acceptance Criteria**:
- [ ] Test: `Create("pat")` returns `PatAuthProvider`
- [ ] Test: `Create("azcli")` returns `MsalCacheTokenProvider`
- [ ] Test: `Create("PAT")` returns `PatAuthProvider` (case-insensitive)
- [ ] Test: `Create("AZCLI")` returns `MsalCacheTokenProvider` (case-insensitive)
- [ ] Test: Default/unknown method returns `MsalCacheTokenProvider` (safe fallback)
- [ ] All tests pass with `dotnet test`

## PR Groups

### PG-1: AuthProviderFactory + Call-Site Updates + Tests

| Property | Value |
|----------|-------|
| **Type** | Deep |
| **Work Items** | #1935, #1936, #1937 |
| **Estimated LoC** | ~100 |
| **Estimated Files** | 4 (1 new factory, 1 new test file, 2 modified) |
| **Predecessor** | None |

**Rationale**: Small, self-contained change. All three tasks are tightly coupled — the
factory, its call sites, and its tests form a single reviewable unit. Splitting into
multiple PRs would add overhead with no reviewability benefit.

**Review Focus**:
- Factory logic matches existing CLI behavior (except for the intentional MCP MSAL fix)
- MCP server now correctly uses `MsalCacheTokenProvider` wrapper
- Tests cover all decision branches
- No behavioral regression in existing auth tests

## References

- Epic #1673: Read MSAL token cache directly instead of shelling out to az CLI
- `MsalCacheTokenProvider`: `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs`
- Auth call-site audit: `docs/projects/azcli-auth-timeout.plan.md`
**Acceptance Criteria**:
- [ ] `AuthProviderFactory.Create("pat")` returns a `PatAuthProvider`
- [ ] `AuthProviderFactory.Create("azcli")` returns a `MsalCacheTokenProvider` wrapping `AzCliAuthProvider`
- [ ] Class is `internal static` with no constructor parameters
- [ ] Project builds with `TreatWarningsAsErrors=true`

### Task #1936: Update Program.cs to use AuthProviderFactory

**Goal**: Replace duplicated auth creation logic in both entry points with factory calls.

**Prerequisites**: Task #1935 (factory must exist)

**Tasks**:

| Sub-Task | Description | Files | Effort |
|----------|-------------|-------|--------|
| 1936-A | Replace DI lambda in `NetworkServiceModule.AddTwigNetworkServices` with `AuthProviderFactory.Create()` | `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | ~5 LoC delta |
| 1936-B | Replace `CreateAuthProvider()` in MCP `Program.cs` with `AuthProviderFactory.Create()` | `src/Twig.Mcp/Program.cs` | ~15 LoC delta |
| 1936-C | Verify both projects build and existing tests pass | Build + test | ~10 min |

**Acceptance Criteria**:
- [ ] `NetworkServiceModule.cs` no longer directly constructs `PatAuthProvider`, `AzCliAuthProvider`, or `MsalCacheTokenProvider`
- [ ] `Twig.Mcp/Program.cs` no longer contains `CreateAuthProvider()` static local function
- [ ] MCP server now wraps `AzCliAuthProvider` in `MsalCacheTokenProvider` (bug fix)
- [ ] All existing tests pass

### Task #1937: Add AuthProviderFactory unit tests

**Goal**: Add unit tests covering all factory creation paths.

**Prerequisites**: Task #1935 (factory must exist)

**Tasks**:

| Sub-Task | Description | Files | Effort |
|----------|-------------|-------|--------|
| 1937-A | Create test class with tests for `"pat"` method, `"azcli"` method, case-insensitivity, and default behavior | `tests/Twig.Infrastructure.Tests/Auth/AuthProviderFactoryTests.cs` | ~60 LoC |
| 1937-B | Verify all tests pass | Test execution | ~5 min |

**Acceptance Criteria**:
- [ ] Test: `Create("pat")` returns `PatAuthProvider`
- [ ] Test: `Create("azcli")` returns `MsalCacheTokenProvider`
- [ ] Test: `Create("PAT")` returns `PatAuthProvider` (case-insensitive)
- [ ] Test: `Create("AZCLI")` returns `MsalCacheTokenProvider` (case-insensitive)
- [ ] Test: Default/unknown method returns `MsalCacheTokenProvider` (safe fallback)
- [ ] All tests pass with `dotnet test`

## PR Groups

### PG-1: AuthProviderFactory + Call-Site Updates + Tests

| Property | Value |
|----------|-------|
| **Type** | Deep |
| **Tasks** | #1935, #1936, #1937 |
| **Estimated LoC** | ~100 |
| **Estimated Files** | 4 (1 new factory, 1 new test, 2 modified) |
| **Predecessor** | None |

**Rationale**: This is a small, self-contained change. All three tasks are tightly coupled —
the factory, its call sites, and its tests form a single reviewable unit. Splitting into
multiple PRs would create a dependency chain with no reviewability benefit.

**Review Focus**:
- Factory logic matches existing behavior exactly (except for the MCP MSAL fix)
- MCP server now correctly wraps with `MsalCacheTokenProvider`
- Tests cover all branches
- No behavioral regression in existing auth tests

## References

- Epic #1673: Read MSAL token cache directly instead of shelling out to az CLI
- `MsalCacheTokenProvider` design: `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs`
- Auth call-site audit in `azcli-auth-timeout.plan.md` (related prior work)
| **Type** | Deep |
| **Work Items** | #1935, #1936, #1937 |
| **Estimated LoC** | ~100 |
| **Estimated Files** | 4 (1 new factory, 1 new test file, 2 modified) |
| **Predecessor** | None |

**Rationale**: Small, self-contained change. All three tasks are tightly coupled — the
factory, its call sites, and its tests form a single reviewable unit. Splitting into
multiple PRs would add overhead with no reviewability benefit.

**Review Focus**:
- Factory logic matches existing CLI behavior (except for the intentional MCP MSAL fix)
- MCP server now correctly uses `MsalCacheTokenProvider` wrapper
- Tests cover all decision branches
- No behavioral regression in existing auth tests

## References

- Epic #1673: Read MSAL token cache directly instead of shelling out to az CLI
- `MsalCacheTokenProvider`: `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs`
- Auth call-site audit: `docs/projects/azcli-auth-timeout.plan.md`