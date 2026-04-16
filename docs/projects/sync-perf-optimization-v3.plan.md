# Sync Performance Optimization v3

**Epic:** #1611 — Sync Performance Optimization
**Status:** In Progress (3 of 5 Issues completed; #1614 in progress — 3 of 6 tasks done)
**Revision:** Rev 10

---

## Executive Summary

Together these changes reduce median command latency by avoiding unnecessary ADO
round-trips on read-heavy commands and eliminating ~100–300ms of process-creation overhead
on every authenticated call. This plan covers the remaining two work streams under
Epic #1611 to reduce Azure DevOps API round-trips and improve Twig CLI responsiveness.
Issues #1612 (batch fetch), #1616 (HTTP transport), and #1613 (parallel refresh +
delegated orchestrator) are all complete. The remaining work is: (1) completing the tiered
cache TTL migration (#1614) — the `SyncCoordinatorFactory` class, its tests,
`DisplayConfig.CacheStaleMinutesReadOnly`, and the DI registrations in both CLI and MCP
are already implemented; what remains is migrating 10 command/service consumers from direct
`SyncCoordinator` injection to `SyncCoordinatorFactory` tier selection and updating 34 test
files — and (2) reading the MSAL token cache file directly to eliminate per-command `az`
process spawn overhead (#1673).

## Background

### Current Architecture

Twig's sync layer is built around `SyncCoordinator`, a sealed Domain-layer class that
checks per-item staleness via `LastSyncedAt` timestamps and fetches stale items from ADO.
It accepts an `int cacheStaleMinutes` primitive at construction time (DD-13) to avoid a
Domain→Infrastructure dependency. Today, a **single** `SyncCoordinator` singleton is
registered at the CLI layer (`CommandServiceModule.cs`) and MCP layer (`Twig.Mcp/Program.cs`)
with a uniform TTL sourced from `DisplayConfig.CacheStaleMinutes` (default: 5 minutes).

This means read-only commands like `twig status`, `twig tree`, and `twig show` trigger
the same staleness checks and ADO fetches as mutating commands like `twig set` and
`twig link` — even though the user running `twig status` three times in a row would be
well-served by cached data.

Authentication is handled by `IAuthenticationProvider` with two implementations:
`AzCliAuthProvider` (default) shells out to `az account get-access-token` via
`Process.Start()`, and `PatAuthProvider` uses a static PAT from environment/config.
`AzCliAuthProvider` implements a **3-tier token cache**: (1) **in-memory** — a private
`_cachedToken`/`_tokenExpiry` pair with a 50-minute TTL, checked first on every call;
(2) **cross-process file cache** at `~/.twig/.token-cache` — read via `TryReadFileCache()`
when the in-memory cache misses, enabling token reuse across concurrent twig CLI
invocations without lock contention; (3) **az CLI spawn** — shells out to
`az account get-access-token` only when both caches miss, costing ~100–300ms of
process-creation overhead. Azure CLI internally uses MSAL and persists its token cache at
`~/.azure/msal_token_cache.json`. `MsalCacheTokenProvider` (this plan) inserts as a
decorator **before all three tiers**, reading the MSAL cache directly to skip the entire
`AzCliAuthProvider` chain when a valid ADO-scoped token exists.

Issue #1613 (parallel refresh + delegated orchestrator) is complete.

### Prior Art

- **sync-perf-optimization.plan.md** — Original plan covering batch fetch (#1612) and HTTP
  transport (#1616), both completed.
- **sync-perf-optimization-v2.plan.md** — Extended plan adding parallel refresh (#1613) and
  tiered cache (#1614), partially completed.
- **DD-8** — Per-item `LastSyncedAt` staleness design decision.
- **DD-13** — Primitive injection pattern for `cacheStaleMinutes`.
- **DD-15** — `SyncChildrenAsync` always fetches unconditionally.

### Call-Site Audit: SyncCoordinator

Every consumer of `SyncCoordinator` must be migrated to `SyncCoordinatorFactory` for
tiered cache TTL (#1614). The table below inventories all call sites:

| # | File | Class/Method | Current Usage | TTL Tier |
|---|------|-------------|---------------|----------|
| 1 | `src/Twig/Commands/StatusCommand.cs` | `StatusCommand` (ctor) | `SyncWorkingSetAsync`, `SyncLinksAsync` | **ReadOnly** |
| 2 | `src/Twig/Commands/TreeCommand.cs` | `TreeCommand` (ctor) | `SyncWorkingSetAsync`, `SyncLinksAsync` | **ReadOnly** |
| 3 | `src/Twig/Commands/ShowCommand.cs` | `ShowCommand` (ctor) | `SyncItemSetAsync` | **ReadOnly** |
| 4 | `src/Twig/Commands/SetCommand.cs` | `SetCommand` (ctor) | `SyncItemSetAsync`, `SyncLinksAsync` | **ReadWrite** |
| 5 | `src/Twig/Commands/LinkCommand.cs` | `LinkCommand` (ctor) | `SyncLinksAsync` | **ReadWrite** |
| 6 | `src/Twig.Domain/Services/StatusOrchestrator.cs` | `StatusOrchestrator` (ctor) | `SyncWorkingSetAsync` | **ReadOnly** |
| 7 | `src/Twig.Domain/Services/RefreshOrchestrator.cs` | `RefreshOrchestrator` (ctor) | `SyncWorkingSetAsync` | **ReadWrite** ¹ |
| 8 | `src/Twig.Domain/Services/ContextChangeService.cs` | `ContextChangeService` (ctor) | `SyncChildrenAsync`, `SyncLinksAsync` | **ReadWrite** |
| 9 | `src/Twig.Mcp/Tools/ReadTools.cs` | `ReadTools` (ctor) | `SyncLinksAsync` | **ReadOnly** |
| 10 | `src/Twig.Mcp/Tools/MutationTools.cs` | `MutationTools` (ctor) | `SyncItemSetAsync` | **ReadWrite** |
| 11 | `src/Twig/DependencyInjection/CommandServiceModule.cs` | DI factory | Singleton registration | **Both** |
| 12 | `src/Twig.Mcp/Program.cs` | DI factory (SyncCoordinator) | Singleton registration | **Both** |
| 13 | `src/Twig.Mcp/Program.cs` | ContextChangeService DI lambda | `sp.GetRequiredService<SyncCoordinator>()` passed to ctor | **ReadWrite** |
> **Note:** `ContextTools.cs` does NOT directly consume `SyncCoordinator` — it accesses
> sync functionality indirectly through `StatusOrchestrator` and `ContextChangeService`
> (rows 6 and 8). No code change is required in `ContextTools.cs` for the factory migration.

**Test files referencing SyncCoordinator:** 34 files across `Twig.Cli.Tests`, `Twig.Domain.Tests`,
and `Twig.Mcp.Tests`. This includes 4 base classes (`RefreshCommandTestBase`,
`ContextToolsTestBase`, `MutationToolsTestBase`, `ReadToolsTestBase`) that centralize
`SyncCoordinator` setup — updating these base classes covers their transitive consumers.
Full list in Task #1664.

> ¹ `RefreshOrchestrator` uses `SyncCoordinator.SyncWorkingSetAsync` only *after*
> `FetchItemsAsync` has fetched and saved fresh data from ADO. At that point, the
> `LastSyncedAt` timestamps are current, so the staleness check is effectively a no-op
> regardless of TTL tier. ReadWrite is assigned as the conservative classification since
> `RefreshOrchestrator` is part of a mutating command pipeline — the TTL has no practical
> effect in this specific call path.

> **Row 13 note:** The MCP `ContextChangeService` DI lambda (lines 69–74 of
> `Twig.Mcp/Program.cs`) calls `sp.GetRequiredService<SyncCoordinator>()` and passes
> the resolved instance to the `ContextChangeService` constructor. This resolves to
> `factory.ReadWrite` via the backward-compat `SyncCoordinator` registration (line 67).
> For the full tiered migration, this lambda should resolve `SyncCoordinatorFactory`
> directly and pass `.ReadWrite` explicitly. This site is distinct from row 12 (which
> covers the `SyncCoordinatorFactory` registration itself) and from row 8 (which covers
> the `ContextChangeService` class constructor in the Domain layer).

**Transitive consumer analysis:** Files like `CacheFirstReadCommandTests.cs`,
`FlowStartCommand_ContextChangeTests.cs`, and `NewCommand_ContextChangeTests.cs` consume
`SyncCoordinator` through base test classes or command constructors.
`ContextToolsSetTests.cs` references `SyncCoordinator` only transitively via
`ContextToolsTestBase` — it requires no direct code change. All 34 files with direct
references are accounted for in the migration scope; no additional transitive consumers
require direct changes.

## Problem Statement

Two performance bottlenecks remain after the batch-fetch, HTTP transport, and parallel
refresh optimizations:

1. **Uniform cache TTL wastes bandwidth on read-heavy workflows.** Users running
   `twig status` or `twig tree` repeatedly (e.g., in shell prompts, terminal tabs, or
   rapid context-switching) trigger ADO API calls every 5 minutes per item. Read-only
   commands don't mutate state, so a 15-minute TTL is acceptable and would eliminate
   ~67% of redundant sync traffic in typical usage patterns.

2. **Process spawn overhead on every CLI invocation.** The `AzCliAuthProvider` shells out
   to `az account get-access-token` on the first API call of each process, adding
   ~100–300ms of latency. The Azure CLI's MSAL token cache at `~/.azure/msal_token_cache.json`
   contains the same token and can be read directly — eliminating the process spawn entirely
   for the common case where a valid cached token exists.

## Goals and Non-Goals

### Goals

- **G-1:** Read-only commands (`status`, `tree`, `show`) use a 15-minute cache TTL;
  mutating commands (`set`, `link`, `refresh`, `sync`) use a 5-minute TTL.
- **G-2:** `SyncCoordinatorFactory` provides `.ReadOnly` and `.ReadWrite` accessors
  so each command explicitly declares its staleness tolerance.
- **G-3:** First-call token acquisition avoids process spawn when a valid MSAL cache
  entry exists, reducing per-command latency by ~100–300ms.
- **G-4:** All changes maintain AOT compatibility (`PublishAot=true`, `TrimMode=full`,
  source-generated JSON only).

### Non-Goals

- **NG-1:** Interactive MSAL authentication flows (e.g., device code, browser login).
  Twig continues to rely on `az login` for initial authentication.
- **NG-2:** Token refresh via MSAL library. If the cached token is expired, we fall back
  to `az` CLI — no `Microsoft.Identity.Client` dependency is added.
- **NG-3:** Configurable per-command TTL overrides. Two tiers (read-only / read-write) are
  sufficient; per-command granularity is over-engineering.
- **NG-4:** Separate cache TTL tiers for MCP server. The MCP server reuses the same
  two-tier factory as CLI — no MCP-specific TTL configuration is needed.
- **NG-5:** Further parallelizing individual `FetchAsync` calls within
  `FetchStaleAndSaveAsync` — these are already parallelized via `Task.WhenAll`
  (line 137 of `SyncCoordinator.cs`). `SyncChildrenAsync` is a separate public method
  and is not called within `FetchStaleAndSaveAsync`.

## Requirements

### Functional

- **FR-1:** `SyncCoordinatorFactory` reads the read-only TTL from
  `DisplayConfig.CacheStaleMinutesReadOnly` (default: 15 minutes), which already exists at
  `TwigConfiguration.cs:337` and has round-trip serialization tests. This value is
  user-configurable via the twig config file, consistent with how
  `DisplayConfig.CacheStaleMinutes` (default: 5 minutes) controls the read-write tier.
- **FR-2:** `SyncCoordinatorFactory` holds two `SyncCoordinator` instances constructed
  with different `cacheStaleMinutes` values.
- **FR-3:** Read-only commands inject `SyncCoordinatorFactory` and use `.ReadOnly`.
  Mutating commands use `.ReadWrite`.
- **FR-4:** `MsalCacheTokenProvider` wraps `AzCliAuthProvider` as a decorator. On
  `GetAccessTokenAsync`, it performs the following steps in order:
  - **(a) In-memory TTL cache (50 minutes):** Checks a private cached token before any
    I/O. The 50-minute TTL matches `AzCliAuthProvider`'s `TokenTtl`.
  - **(b) MSAL cache file read:** Reads `~/.azure/msal_token_cache.json` and filters
    for a valid ADO-scoped token (`target` containing
    `499b84ac-1321-427f-aa17-267ca6975798`).
  - **(c) Expiry validation with 5-minute buffer:** Accepts the token only if
    `ExpiresOn > now + 5 minutes`. The buffer accounts for clock skew between the
    local system and Azure AD's token server, plus network latency between token
    validation and API call arrival — without it, a token expiring during an in-flight
    request would cause a 401 retry.
  - **(d) Testable file-read abstraction:** File I/O is abstracted via an injectable
    `Func<string, CancellationToken, Task<string?>>`, mirroring `AzCliAuthProvider`'s
    `Func<ProcessStartInfo, Process?>` pattern.
  - **(e) Fallback chain:** On miss, expiry, or any error, delegates to the inner
    `AzCliAuthProvider`. Callers never see the decorator — the optimization is transparent.

### Non-Functional

- **NFR-1:** No new NuGet dependencies. MSAL cache is parsed with `System.Text.Json`
  source-generated serialization (added to `TwigJsonContext`).
- **NFR-2:** MSAL cache read failures (missing file, malformed JSON, expired tokens)
  silently fall back to `AzCliAuthProvider` — never surface errors to the user.
- **NFR-3:** All existing tests pass after migration. New tests cover factory wiring,
  tiered TTL behavior, and MSAL cache parsing.
- **NFR-4:** `TreatWarningsAsErrors` remains satisfied — no new warnings introduced.
- **NFR-5:** `MsalCacheTokenProvider` uses a 50-minute in-memory TTL for cached tokens,
  matching `AzCliAuthProvider`'s `TokenTtl` value.
- **NFR-6:** `MsalCacheTokenProvider.GetAccessTokenAsync` is serialized with a
  `SemaphoreSlim(1, 1)` to prevent concurrent file reads and cache races in the MCP server.

### Requirement → Task Traceability

| Requirement | Implementing Tasks | Notes |
|-------------|-------------------|-------|
| FR-1 (factory reads `CacheStaleMinutesReadOnly`) | #1659, #1660, #1661 | All Done — property, factory class, and DI registration |
| FR-2 (factory holds two `SyncCoordinator` instances) | #1660 | Done — constructor creates ReadOnly + ReadWrite |
| FR-3 (read-only commands use `.ReadOnly`; mutating use `.ReadWrite`) | #1662, #1663, #1664 | TO DO — consumer migration + test updates |
| FR-4 (`MsalCacheTokenProvider` decorator chain) | #1673-T1, #1673-T2 | TO DO — implementation + tests |

## Proposed Design

### Architecture Overview

```
┌───────────────────────────────────────────────────────────────────┐
│                      CLI / MCP Entry Points                       │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐          │
│  │ status │ │ tree   │ │ show   │ │ set    │ │ link   │          │
│  │ (RO)   │ │ (RO)   │ │ (RO)   │ │ (RW)   │ │ (RW)   │          │
│  └───┬────┘ └───┬────┘ └───┬────┘ └───┬────┘ └───┬────┘          │
│      │          │          │          │          │                │
│      └──────────┴──────┬───┴──────────┴──────────┘                │
│                        │                                          │
│             ┌──────────▼──────────┐                               │
│             │ SyncCoordinatorFactory │                             │
│             │  .ReadOnly (15 min)  │                              │
│             │  .ReadWrite (5 min)  │                              │
│             └──────────┬──────────┘                               │
│                        │                                          │
│             ┌──────────▼──────────┐                               │
│             │  SyncCoordinator    │                               │
│             │  (unchanged API)    │                               │
│             └──────────┬──────────┘                               │
│                        │                                          │
│       ┌────────────────┼────────────────┐                         │
│  ┌────▼───────────┐ ┌──▼────────┐ ┌─────▼───────────┐            │
│  │ IAdoWorkItem   │ │Protected  │ │ IPendingChange  │            │
│  │ Service        │ │CacheWriter│ │ Store           │            │
│  └────────────────┘ └───────────┘ └─────────────────┘            │
│                                                                   │
│  ┌──────────────────────────────────────────────────┐             │
│  │           Authentication Chain                    │             │
│  │  MsalCacheTokenProvider → AzCliAuthProvider       │             │
│  │  (read file)               (spawn az process)     │             │
│  └──────────────────────────────────────────────────┘             │
└───────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. SyncCoordinatorFactory (implemented — `src/Twig.Domain/Services/SyncCoordinatorFactory.cs`)

A sealed class that constructs two `SyncCoordinator` instances internally using shared
dependencies and different TTL values. The read-only TTL is sourced from
`DisplayConfig.CacheStaleMinutesReadOnly` (default: 15 minutes) and the read-write TTL from
`DisplayConfig.CacheStaleMinutes` (default: 5 minutes) — both are user-configurable via
the twig config file (DD-25):

```csharp
public sealed class SyncCoordinatorFactory
{
    public SyncCoordinatorFactory(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository? linkRepo,
        int readOnlyStaleMinutes,
        int readWriteStaleMinutes)
    {
        if (readOnlyStaleMinutes < readWriteStaleMinutes)
            readOnlyStaleMinutes = readWriteStaleMinutes;

        ReadOnly = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readOnlyStaleMinutes);
        ReadWrite = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readWriteStaleMinutes);
    }

    public SyncCoordinator ReadOnly { get; }
    public SyncCoordinator ReadWrite { get; }
}
```

The constructor clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes` to prevent display
commands from being more aggressive than mutating commands. Registered as a singleton in
DI. Commands inject the factory and select the appropriate tier. The `SyncCoordinator`
class itself is unchanged — the factory is purely a DI-level concern.

#### 2. MsalCacheTokenProvider (new)

A decorator around `IAuthenticationProvider` that intercepts `GetAccessTokenAsync`.
Following the `AzCliAuthProvider` testability pattern, the constructor accepts injectable
dependencies:

```csharp
internal sealed class MsalCacheTokenProvider(
    IAuthenticationProvider inner,
    string? cacheFilePath = null,
    Func<string, CancellationToken, Task<string?>>? fileReader = null,
    Func<DateTimeOffset>? clock = null) : IAuthenticationProvider
```

- `inner` — fallback provider (typically `AzCliAuthProvider`)
- `cacheFilePath` — defaults to `~/.azure/msal_token_cache.json`
- `fileReader` — defaults to `FileStream(FileShare.ReadWrite)` + `StreamReader`; injectable for test isolation
- `clock` — defaults to `DateTimeOffset.UtcNow`; injectable for deterministic expiry tests

**Thread safety:** `GetAccessTokenAsync` is serialized with a `SemaphoreSlim(1, 1)` to
prevent concurrent file reads and torn in-memory cache updates. The existing
`AzCliAuthProvider` documents its `_cachedToken`/`_tokenExpiry` fields as "intentionally
not thread-safe" and "designed for single-threaded CLI usage." While the CLI is
single-threaded, the MCP server processes tool calls on thread pool threads, making
concurrent `GetAccessTokenAsync` invocations possible. The `SemaphoreSlim` in the
decorator serializes access to both the decorator's own cache AND the inner
`AzCliAuthProvider` call, preventing data races on either layer. The semaphore is
acquired before the in-memory cache check and released after the token is cached or
the fallback completes.

Flow:

```
GetAccessTokenAsync()
  ├─ Check in-memory cache → hit → return raw token
  ├─ Read ~/.azure/msal_token_cache.json
  │   ├─ Parse AccessToken entries
  │   ├─ Filter by target containing ADO resource ID
  │   ├─ Check expires_on > now
  │   └─ Valid → cache in-memory, return raw secret string
  └─ Miss/expired/error → delegate to inner (AzCliAuthProvider)
```

> **Important:** `MsalCacheTokenProvider` must return the **raw token string** (e.g.,
> `"eyJ..."`) — NOT prefixed with `"Bearer "`. The calling infrastructure
> (`AdoErrorHandler.ApplyAuthHeader`) adds the `Authorization: Bearer` header
> automatically for any token that doesn't start with `"Basic "`. Returning
> `"Bearer eyJ..."` would produce a double-prefix `Authorization: Bearer Bearer eyJ...`,
> breaking all API calls. This matches `AzCliAuthProvider`, which also returns raw tokens.

The MSAL cache JSON format is:
```json
{
  "AccessToken": {
    "<key>": {
      "secret": "eyJ...",
      "target": "499b84ac-1321-427f-aa17-267ca6975798/.default",
      "expires_on": "1700000000"
    }
  }
}
```

DTOs (`MsalTokenCache`, `MsalAccessTokenEntry`) are added to `TwigJsonContext` for
AOT-safe deserialization. No `Microsoft.Identity.Client` dependency is needed.

> **Critical: `TwigJsonContext` camelCase naming policy.** The MSAL cache file uses
> PascalCase `"AccessToken"` at the top level, which conflicts with `TwigJsonContext`'s
> `CamelCase` naming policy. DD-23 mandates `[JsonPropertyName("AccessToken")]` on the
> DTO property to override the policy. See DD-23 in Design Decisions for full rationale.

#### 3. DI Registration Changes

In `CommandServiceModule.AddTwigCommandServices()` (already implemented):

```csharp
// DD-13 + #1614: SyncCoordinatorFactory holds ReadOnly (longer TTL) and ReadWrite (shorter TTL)
// tiers. Accepts int primitives to avoid Domain → Infrastructure circular reference.
services.AddSingleton<SyncCoordinatorFactory>(sp => new SyncCoordinatorFactory(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IWorkItemLinkRepository>(),
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutesReadOnly,
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

// Backward compat — direct SyncCoordinator consumers resolve to factory.ReadWrite
services.AddSingleton(sp => sp.GetRequiredService<SyncCoordinatorFactory>().ReadWrite);
```

The backward-compat `SyncCoordinator` registration allows existing consumers (e.g.,
`RefreshOrchestrator`, `StatusOrchestrator`, `ContextChangeService`) to continue resolving
`SyncCoordinator` directly, mapping to `factory.ReadWrite`. This acts as a transitional
bridge during the consumer migration (Tasks #1662/#1663). Once all consumers inject
`SyncCoordinatorFactory` directly and select their tier, the backward-compat registration
can optionally be removed. Identical factory registration in `Twig.Mcp/Program.cs` (lines
53–64), except MCP uses `CacheStaleMinutes` for both tiers (agent-driven tools need fresh
data, no read-only distinction).

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-16 | Factory over keyed services | .NET keyed services require `[FromKeyedServices]` attributes which are incompatible with ConsoleAppFramework source-gen. A simple factory class is AOT-safe and explicit. |
| DD-17 | No MSAL library dependency | Parsing the JSON cache file with System.Text.Json avoids a heavy dependency (~2MB), keeps AOT trim clean, and avoids MSAL's runtime reflection usage. |
| DD-18 | Decorator pattern for MSAL cache | Wrapping `AzCliAuthProvider` in `MsalCacheTokenProvider` preserves the existing fallback chain and makes the optimization invisible to callers. |
| DD-20 | Injectable `Func<>` for file-read in MsalCacheTokenProvider | Mirrors `AzCliAuthProvider`'s `Func<ProcessStartInfo, Process?>` testability pattern. Avoids filesystem coupling in tests; enables deterministic simulation of missing/corrupt files. |
| DD-21 | Raw token return (not "Bearer"-prefixed) | `MsalCacheTokenProvider.GetAccessTokenAsync` returns the raw secret string, matching `AzCliAuthProvider`. `AdoErrorHandler.ApplyAuthHeader` adds the `Bearer` scheme. Returning a prefixed token would cause a double-prefix `Authorization: Bearer Bearer ...` header. |
| DD-22 | `SemaphoreSlim` serialization in `MsalCacheTokenProvider` | MCP server invokes auth from thread pool threads. Without serialization, concurrent `GetAccessTokenAsync` calls race on the in-memory cache and file reads. `SemaphoreSlim(1,1)` is the lightest async-compatible lock; the semaphore wraps both the decorator's cache check and the inner provider fallback to prevent concurrent `az` process spawns. The CLI is single-threaded, so this adds negligible overhead there. |
| DD-23 | Explicit `[JsonPropertyName("AccessToken")]` on `MsalTokenCache` DTO | `TwigJsonContext` uses `CamelCase` naming policy. The MSAL cache file uses PascalCase `"AccessToken"` at the top level. Without `[JsonPropertyName]`, the source-generated deserializer looks for `"accessToken"` and silently produces `null`, defeating the optimization. Inner properties (`secret`, `target`) match camelCase naturally. |
| DD-24 | 50-minute in-memory TTL alignment with `AzCliAuthProvider` | The decorator's TTL must not exceed the inner provider's `TokenTtl` (50 minutes). If the decorator cached longer, it could serve a token that `AzCliAuthProvider` has already discarded, creating inconsistent behavior on fallback paths where the inner provider returns a different (newer) token. Matching TTLs ensures cache coherence across the decorator chain. |
| DD-25 | Factory TTL values sourced from `DisplayConfig` at DI registration time | `CacheStaleMinutesReadOnly` (default: 15) and `CacheStaleMinutes` (default: 5) are both user-configurable via the twig config file. The factory clamps ReadOnly ≥ ReadWrite at construction time (line 23 of `SyncCoordinatorFactory.cs`) to prevent display commands from being more aggressive than mutating commands. This makes TTL thresholds adjustable without code changes, consistent with how other `DisplayConfig` settings (e.g., `TreeDepth`, `Hints`) are user-configurable. |

> **Note:** DD-19 was assigned to a design decision in the v2 plan (sync-perf-optimization-v2.plan.md)
> that was superseded during the parallel-refresh refactoring. The numbering gap is intentional to
> maintain traceability across plan revisions.

### Alternatives Considered

**DD-16: Factory vs. Keyed Services for tiered SyncCoordinator injection**

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **SyncCoordinatorFactory** (chosen) | AOT-safe; no attributes; explicit tier selection in code; compatible with ConsoleAppFramework source-gen | Extra factory class; consumers must know which tier to use | **Selected** — AOT compatibility and source-gen framework constraints make this the only viable option |
| .NET Keyed Services (`[FromKeyedServices("ReadOnly")]`) | Native DI pattern; no custom factory class; framework-supported | Requires `[FromKeyedServices]` attribute on constructor parameters, which is incompatible with ConsoleAppFramework's source-generated command binding. ConsoleAppFramework resolves parameters via its own source-gen pipeline and does not recognize `[FromKeyedServices]`. | Rejected |
| Two separate `SyncCoordinator` registrations via marker interfaces | Type-safe; no factory class | Requires 2 new interfaces (`IReadOnlySyncCoordinator`, `IReadWriteSyncCoordinator`) wrapping the same concrete type — adds interface surface without value. `SyncCoordinator` is sealed and not abstracted behind an interface by design (DD-13). | Rejected |

**DD-17: Direct MSAL cache file parsing vs. MSAL library dependency**

| Approach | Pros | Cons | Verdict |
|----------|------|------|---------|
| **Direct JSON parsing** (chosen) | Zero new dependencies; ~50 lines of code; AOT-compatible via `TwigJsonContext`; no reflection | Coupled to MSAL cache file format (mitigated by silent fallback) | **Selected** — simplicity, AOT safety, and zero-dependency alignment with project constraints |
| `Microsoft.Identity.Client` library | Official API; handles format changes; supports token refresh | ~2MB dependency; extensive reflection usage incompatible with `TrimMode=full`; requires `PublicClientApplication` builder pattern that pulls in interactive auth flows (violates NG-1); MSAL's `ITokenCache` serialization API requires callback registration that doesn't map to "just read the file" | Rejected |
| `Microsoft.Identity.Client.Extensions.Msal` | Cross-platform cache access; handles encryption | Additional ~500KB dependency on top of MSAL core; same AOT/trim issues; designed for apps that own the cache, not for read-only access to another app's cache | Rejected |

## Dependencies

### Internal
- Issues #1612, #1616, and #1613 are all **Done** — they laid the groundwork for this plan.
- Issue #1614 and #1673 are independent of each other and can proceed in parallel.

### External
- The MSAL cache file format (`~/.azure/msal_token_cache.json`) is an Azure CLI
  implementation detail, not a public contract. Twig must degrade gracefully if the
  format changes or the file is absent. The decorator-with-fallback pattern ensures this.

### Sequencing Constraints

| Step | Tasks | Depends On | Status | Notes |
|------|-------|------------|--------|-------|
| 1a | #1659 (DisplayConfig) | — | **Done** | `CacheStaleMinutesReadOnly` property already exists |
| 1b | #1660 (SyncCoordinatorFactory) | — | **Done** | Class + tests implemented and passing |
| 2 | #1661 (DI registration) | #1659, #1660 | **Done** | Factory + backward-compat registered in CLI + MCP |
| 3a | #1662 (read-only migration) | #1661 | TO DO | Can run in parallel with #1663 |
| 3b | #1663 (mutating migration) | #1661 | TO DO | Can run in parallel with #1662 |
| 4 | #1664 (test migration) | #1662, #1663 | TO DO | Requires all commands migrated first |
| — | #1673 (MSAL cache) | — | TO DO | Fully independent, any time |

## Impact Analysis

### Blast Radius

This change touches **3 source projects**, **3 test projects**, and **~47 files total**:

| Area | Files Changed | Risk Level | Notes |
|------|--------------|------------|-------|
| DI registration (CLI) | `CommandServiceModule.cs` | Medium | Central wiring file; incorrect factory builds break all commands |
| DI registration (MCP) | `Twig.Mcp/Program.cs` | Medium | Parallel change to CLI; must stay in sync. Includes `ContextChangeService` DI lambda (row 13) |
| Domain services | 3 files (`StatusOrchestrator`, `RefreshOrchestrator`, `ContextChangeService`) | Low | Constructor signature change only; business logic unchanged |
| CLI commands | 5 files (`Status`, `Tree`, `Show`, `Set`, `Link`) | Low | Mechanical: `SyncCoordinator` → `SyncCoordinatorFactory` + tier selection |
| MCP tools | 2 files (`ReadTools`, `MutationTools`) | Low | Same mechanical change as CLI commands |
| Auth infrastructure | 2 new files (`MsalCacheTokenProvider`, tests) | Medium | New decorator in auth chain; errors fall back silently |
| JSON serialization | `TwigJsonContext.cs` | Low | Additive: new `[JsonSerializable]` attribute |
| Test files | 34 files across 3 test projects | Low | Mechanical factory substitution; 4 base classes cover ~15 transitive consumers |

### Backward Compatibility

- **Wire-compatible:** No changes to SQLite schema, CLI argument surface, or MCP tool
  signatures. Users see no behavioral difference except faster response times.
- **DI-transitional:** A backward-compat `SyncCoordinator` singleton registration
  currently maps to `factory.ReadWrite`, allowing unmigrated consumers to continue
  resolving `SyncCoordinator` directly. Once Tasks #1662/#1663 migrate all consumers to
  inject `SyncCoordinatorFactory` directly, the backward-compat registration can be
  removed. Any out-of-tree code that still resolves `SyncCoordinator` would then fail at
  runtime — this is intentional.
- **Auth-compatible:** `MsalCacheTokenProvider` is invisible to callers. The
  `IAuthenticationProvider` contract is unchanged. PAT auth users are completely unaffected.

### Performance Implications

- **Read-only commands:** ~67% reduction in ADO API calls in typical usage patterns
  (15-min TTL vs 5-min means 3x fewer sync cycles for `status`/`tree`/`show`).
- **All commands (az CLI auth):** ~100–300ms latency reduction on first API call per
  process when a valid MSAL cache entry exists (eliminates `az` process spawn).
- **Thread serialization overhead:** The `SemaphoreSlim` in `MsalCacheTokenProvider`
  adds <1μs per call in the uncontended case (CLI). Under MCP contention, concurrent
  callers wait for the token acquisition to complete, which is correct behavior.

## Security Considerations

`MsalCacheTokenProvider` is read-only: it never writes to the MSAL cache, never initiates
auth flows, and never calls Azure AD endpoints. Token in-memory retention and file read
permissions match `AzCliAuthProvider`'s existing threat model. Telemetry emits only
`msal_cache_hit: true` (boolean) — no token content or paths.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| MSAL cache format changes across `az` versions | Low | Medium | Decorator falls back silently to `AzCliAuthProvider`. Format is stable since MSAL 4.x. |
| Read-only 15-min TTL shows stale data confusing users | Low | Low | `CacheAgeFormatter` already displays "⚡ 2m ago" indicators. Stale hint at 15 min is still surfaced. |
| Large test file migration introduces regressions | Medium | Medium | Mechanical change (find-replace `SyncCoordinator` → `SyncCoordinatorFactory`). Each test file compiled and run individually. |
| MSAL cache file locked by `az` CLI during reads | Low | Low | Default `fileReader` uses `FileStream(FileShare.ReadWrite)`. JSON parse failure falls back to az CLI. |
| `TwigJsonContext` naming policy breaks MSAL DTO deserialization | Medium | High | DD-23 mandates `[JsonPropertyName("AccessToken")]`. Test #1673-T2 explicitly verifies PascalCase key deserialization. CI catches via assertion on non-null `AccessToken` dictionary. |
| MCP concurrent auth race condition | Low | Medium | DD-22 mandates `SemaphoreSlim(1,1)` serialization. Test verifies concurrent access pattern. |

## Files Affected

### New Files
| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs` | Reads MSAL cache, decorates AzCliAuthProvider |
| `tests/Twig.Infrastructure.Tests/Auth/MsalCacheTokenProviderTests.cs` | Tests for MSAL cache token provider |

### Modified Files
| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinatorFactory.cs` | Already implemented — no changes needed |
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Already implemented — factory + backward-compat registered; DI lambdas for `StatusOrchestrator`, `RefreshOrchestrator`, `ContextChangeService` still resolve `SyncCoordinator` via backward-compat and will be updated in #1662/#1663 |
| `src/Twig.Mcp/Program.cs` | Already implemented — factory + backward-compat registered; DI lambdas for domain services still resolve `SyncCoordinator` via backward-compat and will be updated in #1662/#1663 |
| `src/Twig/Commands/StatusCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/Commands/TreeCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/Commands/ShowCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig/Commands/SetCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig/Commands/LinkCommand.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Domain/Services/StatusOrchestrator.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig.Domain/Services/RefreshOrchestrator.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Domain/Services/ContextChangeService.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Mcp/Tools/ReadTools.cs` | Inject `SyncCoordinatorFactory`, use `.ReadOnly` |
| `src/Twig.Mcp/Tools/MutationTools.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Wrap `AzCliAuthProvider` in `MsalCacheTokenProvider` |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `MsalTokenCache` serialization |
| 34 test files (see Task #1664 table) | Replace `SyncCoordinator` with `SyncCoordinatorFactory` in test setup |

### Deleted Files

None.

## ADO Work Item Structure

### Issue #1614: Tiered cache TTL for read-only vs read-write commands
**Status:** Doing (3 of 6 tasks done — infrastructure complete; consumer migration remaining)
**Goal:** Read-only commands use 15-min TTL; mutating commands use 5-min TTL via
`SyncCoordinatorFactory`.
**Prerequisites:** None (independent of #1613)

> **Progress note:** Tasks #1659–#1661 established the factory infrastructure (property,
> class, DI registration). Acceptance criteria below track end-to-end outcomes that require
> the remaining consumer migration tasks (#1662–#1664) to be completed.

| Task ID | Description | Files | Status |
|---------|-------------|-------|--------|
| #1659 | Add `CacheStaleMinutesReadOnly` property to `DisplayConfig` | `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | **DONE** |
| #1660 | Implement `SyncCoordinatorFactory` class and tests | `src/Twig.Domain/Services/SyncCoordinatorFactory.cs`, `tests/Twig.Domain.Tests/Services/SyncCoordinatorFactoryTests.cs` | **DONE** |
| #1661 | Register `SyncCoordinatorFactory` in DI (CLI + MCP) with backward-compat `SyncCoordinator` | `src/Twig/DependencyInjection/CommandServiceModule.cs`, `src/Twig.Mcp/Program.cs` | **DONE** |
| #1662 | Migrate read-only commands to `factory.ReadOnly` | `StatusCommand.cs`, `TreeCommand.cs`, `ShowCommand.cs`, `StatusOrchestrator.cs`, `ReadTools.cs` (MCP); `StatusOrchestrator` DI lambdas in `CommandServiceModule.cs` and `Twig.Mcp/Program.cs` | TO DO |
| #1663 | Migrate mutating commands to `factory.ReadWrite` | `SetCommand.cs`, `LinkCommand.cs`, `RefreshOrchestrator.cs`, `ContextChangeService.cs`, `MutationTools.cs` (MCP); `RefreshOrchestrator` and `ContextChangeService` DI lambdas in `CommandServiceModule.cs` and `Twig.Mcp/Program.cs` | TO DO |
| #1664 | Update test files for factory injection | 34 test files across `Twig.Cli.Tests`, `Twig.Domain.Tests`, `Twig.Mcp.Tests` | TO DO |

**Task #1659 Details:**
- `DisplayConfig.CacheStaleMinutesReadOnly` already exists at `TwigConfiguration.cs:337`
  with default value `15`. This property is user-configurable via the twig config file and
  has round-trip serialization coverage via the existing `TwigConfiguration` tests. No
  further work needed.

**Task #1660 Details:**
- `SyncCoordinatorFactory` already exists at `src/Twig.Domain/Services/SyncCoordinatorFactory.cs`
  with 6 passing tests in `SyncCoordinatorFactoryTests.cs`. The class is sealed, accepts
  shared dependencies (`IWorkItemRepository`, `IAdoWorkItemService`, `ProtectedCacheWriter`,
  `IPendingChangeStore`, `IWorkItemLinkRepository?`) plus two `int` TTL parameters
  (`readOnlyStaleMinutes`, `readWriteStaleMinutes`), and constructs two internal
  `SyncCoordinator` instances. The constructor clamps `readOnlyStaleMinutes ≥ readWriteStaleMinutes`
  to prevent display commands from being more aggressive than mutating commands. No
  further work needed — see DD-25 for the TTL sourcing rationale.

**Task #1661 Details:**
- Already implemented in both `CommandServiceModule.AddTwigCommandServices()` and
  `Twig.Mcp/Program.cs`.
- CLI registration (lines 47–57 of `CommandServiceModule.cs`): constructs
  `SyncCoordinatorFactory` with `CacheStaleMinutesReadOnly` and `CacheStaleMinutes`
  from `TwigConfiguration.Display`. Backward-compat `SyncCoordinator` singleton (line 57)
  resolves to `factory.ReadWrite`.
- MCP registration (lines 53–67 of `Twig.Mcp/Program.cs`): constructs
  `SyncCoordinatorFactory` with `CacheStaleMinutes` for both tiers (agent-driven tools
  need fresh data, no read-only distinction). Same backward-compat pattern.
- `CommandServiceModuleTests.cs` already has tests for factory resolution, backward-compat
  mapping, and TTL configuration. No further work needed — see DD-25.

**Task #1662 Details (read-only commands):**
- `StatusCommand`: change `SyncCoordinator syncCoordinator` → `SyncCoordinatorFactory syncFactory`,
  replace all `syncCoordinator.` with `syncFactory.ReadOnly.`.
- `TreeCommand`: same pattern.
- `ShowCommand`: same pattern.
- `StatusOrchestrator`: same pattern (uses `.ReadOnly`).
- `ReadTools` (MCP): same pattern.
- `CommandServiceModule.cs`: update the `StatusOrchestrator` DI lambda (the
  `services.AddSingleton<StatusOrchestrator>(sp => ...)` block) to resolve
  `SyncCoordinatorFactory` instead of `SyncCoordinator` and pass it to the constructor.
- `Twig.Mcp/Program.cs`: same DI lambda update for the `StatusOrchestrator` registration
  (`builder.Services.AddSingleton(sp => new StatusOrchestrator(...))` block).

**Task #1663 Details (mutating commands):**
- `SetCommand`: change `SyncCoordinator syncCoordinator` → `SyncCoordinatorFactory syncFactory`,
  replace all `syncCoordinator.` with `syncFactory.ReadWrite.`.
- `LinkCommand`: same pattern.
- `RefreshOrchestrator`: same pattern (uses `.ReadWrite`).
- `ContextChangeService`: same pattern (uses `.ReadWrite`).
- `MutationTools` (MCP): same pattern.
- `CommandServiceModule.cs`: update the `RefreshOrchestrator` DI lambda
  (`services.AddSingleton<RefreshOrchestrator>(sp => ...)` block) and the
  `ContextChangeService` DI lambda (`services.AddSingleton<ContextChangeService>(sp => ...)`
  block) to resolve `SyncCoordinatorFactory` instead of `SyncCoordinator`.
- `Twig.Mcp/Program.cs`: same DI lambda update for the `RefreshOrchestrator` registration
  (`builder.Services.AddSingleton(sp => new RefreshOrchestrator(...))` block) and the
  `ContextChangeService` registration (`builder.Services.AddSingleton(sp => new
  ContextChangeService(...))` block, lines 69–74) — both resolve `SyncCoordinator` today
  via the backward-compat registration and should be migrated to resolve
  `SyncCoordinatorFactory` directly (see call-site audit row 13).

**Task #1664 Details (test updates):**
- All 34 test files that construct or reference `SyncCoordinator` for command tests must:
  - Create a `SyncCoordinatorFactory` wrapping the test dependencies.
  - Pass the factory instead of the coordinator to the command/service under test.
- Pattern:
  ```csharp
  var factory = new SyncCoordinatorFactory(
      _workItemRepo, _adoService, _protectedWriter, _pendingStore, _linkRepo,
      readOnlyStaleMinutes: 5, readWriteStaleMinutes: 5);
  ```
  (both tiers use the same TTL in most tests — only TTL-specific tests need different values).
- 4 base classes (`RefreshCommandTestBase`, `ContextToolsTestBase`, `MutationToolsTestBase`,
  `ReadToolsTestBase`) centralize coordinator setup — updating these covers their transitive
  consumer tests automatically.
- Tests that specifically verify TTL behavior should construct factories with different
  `readOnlyStaleMinutes` and `readWriteStaleMinutes` values.

**Full test file list (34 files):**

| # | Directory | File |
|---|-----------|------|
| 1 | `Twig.Cli.Tests/Commands` | `CacheFirstReadCommandTests.cs` |
| 2 | | `CommandFormatterWiringTests.cs` |
| 3 | | `FlowStartCommand_ContextChangeTests.cs` |
| 4 | | `LinkCommandTests.cs` |
| 5 | | `NavigationCommandsInteractiveTests.cs` |
| 6 | | `NewCommand_ContextChangeTests.cs` |
| 7 | | `NextPrevCommandTests.cs` |
| 8 | | `OfflineModeTests.cs` |
| 9 | | `PromptStateIntegrationTests.cs` |
| 10 | | `RefreshCommandTestBase.cs` *(base class)* |
| 11 | | `SetCommand_ContextChangeTests.cs` |
| 12 | | `SetCommandDisambiguationTests.cs` |
| 13 | | `SetCommandTests.cs` |
| 14 | | `ShowCommand_CacheAwareTests.cs` |
| 15 | | `ShowCommandTests.cs` |
| 16 | | `StatusCommand_CacheAwareTests.cs` |
| 17 | | `StatusCommandTests.cs` |
| 18 | | `TreeCommand_CacheAwareTests.cs` |
| 19 | | `TreeCommandAsyncTests.cs` |
| 20 | | `TreeCommandLinkTests.cs` |
| 21 | | `TreeCommandTests.cs` |
| 22 | | `TreeNavCommandTests.cs` |
| 23 | | `WorkingSetCommandTests.cs` |
| 24 | `Twig.Cli.Tests/DependencyInjection` | `CommandServiceModuleTests.cs` |
| 25 | `Twig.Cli.Tests/Rendering` | `CacheRefreshTests.cs` |
| 26 | `Twig.Domain.Tests/Services` | `ContextChangeServiceTests.cs` |
| 27 | | `ProtectedCacheWriterTests.cs` |
| 28 | | `RefreshOrchestratorTests.cs` |
| 29 | | `StatusOrchestratorTests.cs` |
| 30 | | `SyncCoordinatorTests.cs` |
| 31 | `Twig.Mcp.Tests` | `ProgramBootstrapTests.cs` |
| 32 | `Twig.Mcp.Tests/Tools` | `ContextToolsTestBase.cs` *(base class)* |
| 33 | | `MutationToolsTestBase.cs` *(base class)* |
| 34 | | `ReadToolsTestBase.cs` *(base class)* |

> **Note:** `ContextToolsSetTests.cs` is excluded from the direct migration count — it
> references `SyncCoordinator` only transitively via `ContextToolsTestBase.cs` (row 32).
> Updating the base class automatically covers `ContextToolsSetTests.cs`.
>
> **Note:** `SyncCoordinatorFactoryTests.cs` is excluded from the 34-file migration list
> because it already constructs `SyncCoordinatorFactory` directly — it tests the factory
> itself, not a consumer that needs to be migrated from `SyncCoordinator` to the factory.
> Its 3 references to `SyncCoordinator` are within `SyncCoordinatorFactory` property
> accesses (`.ReadOnly`, `.ReadWrite`), not direct `SyncCoordinator` injection.

**Acceptance Criteria:**
- [ ] Read-only commands use 15-min staleness threshold
- [ ] Mutating commands use 5-min staleness threshold
- [x] SyncCoordinatorFactory is registered in both CLI and MCP DI
- [ ] All 34 test files compile and pass with factory injection
- [ ] Backward-compat `SyncCoordinator` registration removed after all consumers migrated

---

### Issue #1673: Read MSAL token cache directly
**Status:** To Do
**Goal:** Eliminate per-command `az` process spawn by reading the MSAL token cache file
directly when a valid ADO-scoped token exists.
**Prerequisites:** None (fully independent)

| Task ID | Description | Files | Status |
|---------|-------------|-------|--------|
| #1673-T1 | Implement `MsalCacheTokenProvider` decorator (DTOs + logic), register DTOs in `TwigJsonContext`, and wire decorator into NetworkServiceModule DI | `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs` (new), `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs`, `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | TO DO |
| #1673-T2 | Write comprehensive tests | `tests/Twig.Infrastructure.Tests/Auth/MsalCacheTokenProviderTests.cs` (new) | TO DO |

> **Note:** Tasks #1673-T1 and #1673-T2 use local suffixes because they have not yet
> been created as standalone ADO work items. They will be seeded as Tasks under Issue #1673
> during implementation kickoff, at which point this table will be updated with real ADO IDs.

**Task #1673-T1 Details:**

This task bundles DTOs, JSON registration, business logic, and DI wiring as a single
atomic unit — none has value without the others, and all four modify only 3 files.

- New sealed class `MsalCacheTokenProvider : IAuthenticationProvider` in `Auth/MsalCacheTokenProvider.cs`.
- DTOs in the same file:
  ```csharp
  internal sealed class MsalTokenCache
  {
      [JsonPropertyName("AccessToken")]
      public Dictionary<string, MsalAccessTokenEntry>? AccessToken { get; set; }
  }

  internal sealed class MsalAccessTokenEntry
  {
      public string? Secret { get; set; }
      public string? Target { get; set; }
      [JsonPropertyName("expires_on")]
      public string? ExpiresOn { get; set; }
  }
  ```
  > **Visibility:** Both `MsalTokenCache` and `MsalAccessTokenEntry` are `internal sealed` —
  > they are Infrastructure implementation details and must not be `public`. This is safe
  > because `TwigJsonContext` (which references them via `[JsonSerializable]`) is also
  > `internal` within the same assembly. The `[JsonPropertyName("AccessToken")]` attribute
  > on `MsalTokenCache.AccessToken` is required per DD-23 to override the camelCase naming
  > policy. `ExpiresOn` uses `[JsonPropertyName("expires_on")]` for the snake_case key.
- Add `[JsonSerializable(typeof(MsalTokenCache))]` to `TwigJsonContext`.
- Constructor: `(IAuthenticationProvider inner, string? cacheFilePath = null, Func<string, CancellationToken, Task<string?>>? fileReader = null, Func<DateTimeOffset>? clock = null)`.
- Private `SemaphoreSlim _semaphore = new(1, 1)` field for thread-safe token acquisition (DD-22).
- Default cache path: `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), ".azure", "msal_token_cache.json")`.
- Default file reader: reads via `new FileStream(path, FileMode.Open, FileAccess.Read,
  FileShare.ReadWrite)` + `StreamReader` (not `File.ReadAllTextAsync`, which does not
  support `FileShare` parameters). This avoids locking conflicts when `az` CLI writes
  the cache concurrently. Injectable via `Func<string, CancellationToken, Task<string?>>`
  for test isolation, mirroring `AzCliAuthProvider`'s `Func<ProcessStartInfo, Process?>`
  pattern.
- `GetAccessTokenAsync`:
  1. Acquire `_semaphore` (serializes concurrent MCP calls — DD-22).
  2. Check in-memory cache (50-min TTL matching AzCliAuthProvider — NFR-5).
  3. Read + parse MSAL cache file.
  4. Filter `AccessToken` entries where `Target` contains `499b84ac-1321-427f-aa17-267ca6975798`.
  5. Parse `ExpiresOn` from Unix epoch string to `DateTimeOffset` via
     `long.TryParse(entry.ExpiresOn, out var epoch)` →
     `DateTimeOffset.FromUnixTimeSeconds(epoch)`. If parsing fails, skip the entry
     (treat as expired). From all entries with parsed expiry > `now + 5 minutes`,
     **select the entry with the maximum expiry** (`OrderByDescending(e => parsedExpiry)
     .FirstOrDefault()`). This tie-breaking ensures the longest-lived token is used when
     multiple valid ADO-scoped entries exist (e.g., tokens for different tenants or
     accounts that both match the ADO resource scope). The
     **5-minute expiry buffer** accounts for two factors: (a) **clock skew** between
     the local system and Azure AD's token server — the token's `ExpiresOn` is set by
     Azure AD, but the comparison uses the local clock, which may lag by 1–3 minutes;
     (b) **network latency** — a token that expires in 4 minutes might be used in a
     request that takes 1–2 minutes to reach the server, arriving after expiry and
     triggering a 401. The 5-minute buffer ensures the token is valid for the duration
     of at least one API call round-trip. This matches the MSAL library's own
     `DefaultAccessTokenExpirationBuffer` of 5 minutes.
  6. Valid → cache in-memory, return raw secret string (DD-21).
  7. Any failure → delegate to `_inner.GetAccessTokenAsync(ct)`.
  8. Release `_semaphore` in `finally` block.
- All exceptions caught and swallowed (delegate to fallback).
- In `NetworkServiceModule`, change the azcli branch to wrap `AzCliAuthProvider`:
  ```csharp
  if (string.Equals(cfg.Auth.Method, "pat", StringComparison.OrdinalIgnoreCase))
      return new PatAuthProvider();
  var azCli = new AzCliAuthProvider();
  return new MsalCacheTokenProvider(azCli);
  ```
- No config changes needed — optimization is transparent within `azcli` auth method.

**Task #1673-T2 Details:**
- Test valid token in cache → returns raw token string without spawning process.
- Test expired token → falls back to inner provider.
- Test token within 5-minute expiry buffer → falls back to inner provider.
- Test missing cache file → falls back to inner provider.
- Test malformed JSON → falls back to inner provider.
- Test no ADO-scoped token → falls back to inner provider.
- Test multiple tokens → selects ADO-scoped entry with maximum expiry.
- Test multiple valid ADO-scoped tokens with different expiries → selects max-expiry entry
  (tie-breaking verification: provide 2 entries both with `Target` containing the ADO resource
  ID and expiry > now + 5 min; assert the returned token is the one with the later expiry).
- Test in-memory cache → second call skips file read.
- Test concurrent calls → `SemaphoreSlim` serializes access (verify via timing or mock assertions).
- Test `AccessToken` deserialization with PascalCase key (verifies DD-23 `[JsonPropertyName]`).
- Use injectable `fileReader`, `clock`, and `cacheFilePath` for deterministic testing.
  The `fileReader` func enables tests to provide cache content without touching the
  filesystem, and to simulate read failures by returning `null` or throwing.

**Acceptance Criteria:**
- [ ] Valid MSAL cache token is returned without process spawn
- [ ] Expired/missing/malformed cache silently falls back to `az` CLI
- [ ] Token within 5-minute expiry buffer treated as expired (falls back)
- [ ] In-memory cache (50-min TTL) prevents repeated file reads
- [ ] Concurrent `GetAccessTokenAsync` calls serialized by `SemaphoreSlim`
- [ ] Multiple valid ADO-scoped tokens resolved by selecting max-expiry entry
- [ ] `MsalTokenCache.AccessToken` has `[JsonPropertyName("AccessToken")]` for camelCase policy
- [ ] No `Microsoft.Identity.Client` dependency added
- [ ] DTOs registered in `TwigJsonContext` for AOT compatibility
- [ ] `PatAuthProvider` path is completely unaffected

## PR Groups

PR groups cluster tasks for reviewable PRs, sized for reviewability (≤2000 LoC, ≤50 files).
These are independent of the ADO work-item hierarchy.

| PR Group | Tasks | Type | Est. Files | Est. LoC | Description |
|----------|-------|------|-----------|----------|-------------|
| PG-1 | #1662, #1663, #1664 | **wide** | ~49 | ~800 | **SyncCoordinatorFactory migration.** Migrates all 10 command/service consumers to `SyncCoordinatorFactory` tier selection (`.ReadOnly` / `.ReadWrite`), updates DI lambdas in `CommandServiceModule.cs` and `Twig.Mcp/Program.cs`, and updates all 34 test files in the same branch. 4 base classes cover ~15 transitive test consumers. Removes backward-compat `SyncCoordinator` registration after migration is verified. |
| PG-2 | #1673-T1, #1673-T2 | **deep** | ~4 | ~450 | **MSAL token cache provider.** Implements `MsalCacheTokenProvider` decorator, DTOs, `TwigJsonContext` registration, DI wiring, and comprehensive test suite. Fully independent of PG-1 — can be developed and reviewed in parallel. |

**Execution order:**
- PG-1 and PG-2 can proceed in parallel (no dependencies between them).

## References

- Azure CLI MSAL token cache: `~/.azure/msal_token_cache.json` (format stable since MSAL 4.x)
- ADO OAuth resource ID: `499b84ac-1321-427f-aa17-267ca6975798`
- Azure CLI client ID: `04b07795-8ddb-461a-bbee-02f9e1bf7b46`
- [sync-perf-optimization.plan.md](./sync-perf-optimization.plan.md) — v1 plan (batch fetch + HTTP transport)
- [sync-perf-optimization-v2.plan.md](./sync-perf-optimization-v2.plan.md) — v2 plan (parallel refresh + tiered cache)

