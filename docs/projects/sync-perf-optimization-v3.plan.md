# Sync Performance Optimization v3

**Epic:** #1611 — Sync Performance Optimization
**Status:** In Progress (3 of 5 Issues completed)
**Revision:** Rev 4 — Correcting auth token return format, TTL precision, alternatives section, structural fixes

---

## Executive Summary

This plan covers the remaining two work streams under Epic #1611 to reduce Azure DevOps
API round-trips and improve Twig CLI responsiveness. Issues #1612 (batch fetch), #1616
(HTTP transport), and #1613 (parallel refresh + delegated orchestrator) are all complete.
The remaining work is: (1) introducing tiered cache TTL via a `SyncCoordinatorFactory` so
read-only commands tolerate 15-minute staleness while mutating commands keep a 5-minute
threshold (#1614), and (2) reading the MSAL token cache file directly to eliminate
per-command `az` process spawn overhead (#1673). Together these changes reduce median
command latency by avoiding unnecessary ADO round-trips on read-heavy commands and
eliminating ~100–300ms of process-creation overhead on every authenticated call.

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
The `AzCliAuthProvider` has a 50-minute in-memory TTL cache, but the first invocation
per process always spawns `az.cmd`/`az` — costing ~100–300ms of process-creation overhead.
Azure CLI internally uses MSAL and persists its token cache at `~/.azure/msal_token_cache.json`,
which can be read directly to skip the process spawn entirely.

The `RefreshOrchestrator` now parallelizes active-item and child fetches via `Task.WhenAll`
and `RefreshCommand` delegates fetch/save/conflict logic to the orchestrator. Post-refresh
metadata syncs (process types + field definitions) already run concurrently. Test coverage
for the parallel/delegated patterns is in place — `FetchItems_ActiveNotInBatch_FiresFetchAndChildrenConcurrently`
verifies concurrent fetch, `FetchItems_ActiveItemInBatch_SkipsDuplicateFetch` verifies the
sequential optimization, and `Refresh_BothMetadataSyncsAreCalled` (with resilience variants)
verifies concurrent metadata sync. Issue #1613 is complete.

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
| 11 | `src/Twig.Mcp/Tools/ContextTools.cs` | `ContextTools` (ctor) | `SyncItemSetAsync` | **ReadWrite** |
| 12 | `src/Twig/DependencyInjection/CommandServiceModule.cs` | DI factory | Singleton registration | **Both** |
| 13 | `src/Twig.Mcp/Program.cs` | DI factory | Singleton registration | **Both** |

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

**Transitive consumer analysis:** Files like `CacheFirstReadCommandTests.cs`,
`FlowStartCommand_ContextChangeTests.cs`, and `NewCommand_ContextChangeTests.cs` consume
`SyncCoordinator` through base test classes or command constructors. All 34 files are
accounted for in the migration scope; no additional transitive consumers require direct changes.

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
- **NG-4:** Separate cache TTL tiers for MCP server. The MCP server reuses the same two-tier factory as CLI — no MCP-specific TTL configuration is needed.
- **NG-5:** Parallelizing `SyncChildrenAsync` calls within `FetchStaleAndSaveAsync` — these
  are already parallelized via `Task.WhenAll`.

## Requirements

### Functional

- **FR-1:** `SyncCoordinatorFactory` defines `internal const int ReadOnlyCacheStaleMinutes = 15`
  for the read-only tier. No changes to `DisplayConfig` — the 15-minute value is not
  user-configurable, so it doesn't belong on the config class.
- **FR-2:** `SyncCoordinatorFactory` holds two `SyncCoordinator` instances constructed
  with different `cacheStaleMinutes` values.
- **FR-3:** Read-only commands inject `SyncCoordinatorFactory` and use `.ReadOnly`.
  Mutating commands use `.ReadWrite`.
- **FR-4:** `MsalCacheTokenProvider` wraps `AzCliAuthProvider` as a decorator. On
  `GetAccessTokenAsync`, it reads `~/.azure/msal_token_cache.json`, filters for a
  valid ADO-scoped token, and returns it directly. On miss/expiry, delegates to inner.
  File-read is abstracted via injectable `Func<string, CancellationToken, Task<string?>>`
  for testability (mirroring `AzCliAuthProvider`'s `Func<ProcessStartInfo, Process?>` pattern).

### Non-Functional

- **NFR-1:** No new NuGet dependencies. MSAL cache is parsed with `System.Text.Json`
  source-generated serialization (added to `TwigJsonContext`).
- **NFR-2:** MSAL cache read failures (missing file, malformed JSON, expired tokens)
  silently fall back to `AzCliAuthProvider` — never surface errors to the user.
- **NFR-3:** All existing tests pass after migration. New tests cover factory wiring,
  tiered TTL behavior, and MSAL cache parsing.
- **NFR-4:** `TreatWarningsAsErrors` remains satisfied — no new warnings introduced.

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    CLI / MCP Entry Points                │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌─────────┐ │
│  │ status   │  │ tree     │  │ show     │  │ set     │ │
│  │ (RO)     │  │ (RO)     │  │ (RO)     │  │ (RW)   │ │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  └───┬─────┘ │
│       │              │              │             │       │
│       └──────────────┴──────┬───────┴─────────────┘       │
│                             │                             │
│                  ┌──────────▼──────────┐                  │
│                  │ SyncCoordinatorFactory │                │
│                  │  .ReadOnly (15 min)  │                │
│                  │  .ReadWrite (5 min)  │                │
│                  └──────────┬──────────┘                  │
│                             │                             │
│                  ┌──────────▼──────────┐                  │
│                  │  SyncCoordinator    │                  │
│                  │  (unchanged API)    │                  │
│                  └──────────┬──────────┘                  │
│                             │                             │
│            ┌────────────────┼────────────────┐            │
│  ┌─────────▼──────┐  ┌─────▼─────┐  ┌──────▼──────────┐ │
│  │ IAdoWorkItem   │  │Protected  │  │ IPendingChange  │ │
│  │ Service        │  │CacheWriter│  │ Store           │ │
│  └────────────────┘  └───────────┘  └─────────────────┘ │
│                                                          │
│  ┌──────────────────────────────────────────────────┐    │
│  │           Authentication Chain                    │    │
│  │  MsalCacheTokenProvider → AzCliAuthProvider       │    │
│  │  (read file)               (spawn az process)     │    │
│  └──────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. SyncCoordinatorFactory (new)

A simple sealed class holding two pre-built `SyncCoordinator` instances with an
`internal const` for the read-only TTL. `DisplayConfig.CacheStaleMinutes` (default: 5)
remains the sole user-configurable TTL — the 15-minute value is not user-configurable,
so it lives on the factory, not the config class:

```csharp
public sealed class SyncCoordinatorFactory(
    SyncCoordinator readOnly,
    SyncCoordinator readWrite)
{
    internal const int ReadOnlyCacheStaleMinutes = 15;

    public SyncCoordinator ReadOnly { get; } = readOnly;
    public SyncCoordinator ReadWrite { get; } = readWrite;
}
```

Registered as a singleton in DI. Commands inject the factory and select the appropriate
tier. The `SyncCoordinator` class itself is unchanged — the factory is purely a DI-level
concern. The constant is `internal` (not `private`) because DI registration code in
`CommandServiceModule` (`twig` assembly) and `Twig.Mcp/Program.cs` references
`SyncCoordinatorFactory.ReadOnlyCacheStaleMinutes` — both assemblies have
`InternalsVisibleTo` access to `Twig.Domain`.

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

#### 3. DI Registration Changes

In `CommandServiceModule.AddTwigCommandServices()`:

```csharp
// Build both coordinators with different TTLs
services.AddSingleton<SyncCoordinatorFactory>(sp =>
{
    var config = sp.GetRequiredService<TwigConfiguration>();
    SyncCoordinator Build(int ttl) => new SyncCoordinator(
        sp.GetRequiredService<IWorkItemRepository>(),
        sp.GetRequiredService<IAdoWorkItemService>(),
        sp.GetRequiredService<ProtectedCacheWriter>(),
        sp.GetRequiredService<IPendingChangeStore>(),
        sp.GetRequiredService<IWorkItemLinkRepository>(),
        ttl);
    return new SyncCoordinatorFactory(
        readOnly:  Build(SyncCoordinatorFactory.ReadOnlyCacheStaleMinutes),
        readWrite: Build(config.Display.CacheStaleMinutes));
});
```

The standalone `SyncCoordinator` registration is removed. All consumers migrate to
`SyncCoordinatorFactory`. Identical change in `Twig.Mcp/Program.cs`.

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-16 | Factory over keyed services | .NET keyed services require `[FromKeyedServices]` attributes which are incompatible with ConsoleAppFramework source-gen. A simple factory class is AOT-safe and explicit. |
| DD-17 | No MSAL library dependency | Parsing the JSON cache file with System.Text.Json avoids a heavy dependency (~2MB), keeps AOT trim clean, and avoids MSAL's runtime reflection usage. |
| DD-18 | Decorator pattern for MSAL cache | Wrapping `AzCliAuthProvider` in `MsalCacheTokenProvider` preserves the existing fallback chain and makes the optimization invisible to callers. |
| DD-19 | Two tiers, not per-command | Two tiers (read-only/read-write) cover the essential use case without over-engineering. If a command is read-only, 15-min staleness is acceptable. |
| DD-20 | Injectable `Func<>` for file-read in MsalCacheTokenProvider | Mirrors `AzCliAuthProvider`'s `Func<ProcessStartInfo, Process?>` testability pattern. Avoids filesystem coupling in tests; enables deterministic simulation of missing/corrupt files. |
| DD-21 | Raw token return (not "Bearer"-prefixed) | `MsalCacheTokenProvider.GetAccessTokenAsync` returns the raw secret string, matching `AzCliAuthProvider`. `AdoErrorHandler.ApplyAuthHeader` adds the `Bearer` scheme. Returning a prefixed token would cause a double-prefix `Authorization: Bearer Bearer ...` header. |

## Alternatives Considered

### DD-16: Factory vs .NET Keyed Services

**Option A: Keyed services (rejected)**
Register two `SyncCoordinator` instances keyed by `"ReadOnly"` / `"ReadWrite"` using
.NET 8's `AddKeyedSingleton<T>`. Consumers inject via `[FromKeyedServices("ReadOnly")]`.

*Pros:* Standard .NET DI pattern; no new factory class needed.
*Cons:* `[FromKeyedServices]` requires attribute injection, which **is not supported by
ConsoleAppFramework's source-generated constructors**. ConsoleAppFramework resolves
constructor parameters via generated code that doesn't process `[FromKeyedServices]`
attributes. This is a hard blocker for AOT. Additionally, keyed services have no compile-time
safety — a typo in the string key (`"ReadOnyl"`) silently resolves to null at runtime.

**Option B: Factory class (selected)**
A simple `SyncCoordinatorFactory` with `.ReadOnly` and `.ReadWrite` properties.

*Pros:* AOT-safe (no attributes), compile-time type safety, explicit intent at each call
site, works with all DI frameworks including ConsoleAppFramework source-gen.
*Cons:* One additional class; consumers reference the factory instead of the coordinator
directly.

**Verdict:** Option B is the only viable approach given the ConsoleAppFramework constraint.

### DD-17: MSAL Cache Direct Read vs Microsoft.Identity.Client

**Option A: Add `Microsoft.Identity.Client` NuGet package (rejected)**
Use the official MSAL library's `PublicClientApplication` to acquire tokens from the
shared token cache.

*Pros:* Official API; handles token refresh, cache serialization, and expiry automatically.
*Cons:* Adds ~2MB dependency; MSAL uses runtime reflection for serialization, which
**conflicts with `PublishAot=true` and `TrimMode=full`**. MSAL's `DefaultCacheAccessor`
uses `System.Reflection` to discover token cache extensions. Trim warnings are
un-suppressable without `[DynamicDependency]` chains. Additionally, MSAL's
`AcquireTokenSilent` flow may trigger interactive prompts or device code flows, which
conflict with Twig's non-interactive CLI model.

**Option B: Read MSAL cache JSON directly (selected)**
Parse `~/.azure/msal_token_cache.json` with `System.Text.Json` source-generated DTOs.
Fall back to `AzCliAuthProvider` on any failure.

*Pros:* Zero new dependencies; AOT-safe via `TwigJsonContext`; fail-safe decorator pattern;
~200 LoC implementation.
*Cons:* Depends on an undocumented file format. The MSAL cache schema has been stable since
MSAL 4.x (2019), but format changes would cause fallback to `az` CLI rather than failure.

**Verdict:** Option B is the pragmatic choice for an AOT-compiled CLI. The fallback chain
ensures zero risk from format changes.

### DD-19: Two Tiers vs Per-Command TTL

**Option A: Per-command TTL configuration (rejected)**
Allow each command to specify its own `cacheStaleMinutes` value.

*Pros:* Maximum flexibility; individual commands can tune their staleness tolerance.
*Cons:* Over-engineering — the fundamental distinction is "does this command mutate ADO
state?" with only two answers. Adding 10+ TTL values creates configuration sprawl and
makes behavior harder to reason about. No user request for this granularity.

**Option B: Two tiers — ReadOnly (15 min) / ReadWrite (5 min) (selected)**
All commands fall into one of two categories based on whether they mutate ADO state.

*Pros:* Simple mental model; covers the primary use case (read-heavy workflows); two
configurable values instead of N.
*Cons:* A command like `refresh` uses ReadWrite even though its staleness check is
effectively moot (items were just fetched). This is a conservative classification.

**Verdict:** Two tiers provide the right cost/benefit ratio. Per-command TTL is deferred
as NG-3.

## Dependencies

### Internal
- Issues #1612, #1616, and #1613 are all **Done** — they laid the groundwork for this plan.
- Issue #1614 and #1673 are independent of each other and can proceed in parallel.

### External
- The MSAL cache file format (`~/.azure/msal_token_cache.json`) is an Azure CLI
  implementation detail, not a public contract. Twig must degrade gracefully if the
  format changes or the file is absent. The decorator-with-fallback pattern ensures this.

### Sequencing Constraints

| Step | Tasks | Depends On | Notes |
|------|-------|------------|-------|
| 1 | #1660 (SyncCoordinatorFactory) | — | Defines class + ReadOnlyCacheStaleMinutes constant |
| 2 | #1661 (DI registration) | #1660 | Registers factory in DI container |
| 3a | #1662 (read-only migration) | #1661 | Can run in parallel with #1663 |
| 3b | #1663 (mutating migration) | #1661 | Can run in parallel with #1662 |
| 4 | #1664 (test migration) | #1662, #1663 | Requires all commands migrated first |
| — | #1673 (MSAL cache) | — | Fully independent, any time |

## Security Considerations

### MSAL Token Cache File Access

`MsalCacheTokenProvider` reads bearer tokens directly from the MSAL cache file on disk.
This has security implications:

- **File permissions:** The cache file at `~/.azure/msal_token_cache.json` is owned by the
  user and protected by OS-level file permissions. Twig reads it with the same permissions
  as `az` CLI — no privilege escalation occurs.
- **Token in memory:** The bearer token string is held in an in-memory field (`_cachedToken`)
  for up to 50 minutes. This matches the existing behavior of `AzCliAuthProvider`, which
  also caches the token in memory. No new in-memory exposure is introduced.
- **No token persistence:** Twig never writes tokens to disk, logs, telemetry, or any
  external system. The token is used only for `Authorization: Bearer` headers on ADO API
  calls via the existing `HttpClient` pipeline.
- **Fallback isolation:** If the MSAL cache file is unreadable (permissions, corruption,
  format change), the decorator swallows the exception and delegates to the inner provider.
  No partial token data is exposed.
- **No new attack surface:** The cache file is already readable by the current user process.
  Twig adds no new file access patterns — it reads the same file that `az` CLI writes.
  The optimization merely avoids spawning a subprocess to read it indirectly.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| MSAL cache format changes across `az` versions | Low | Medium | Decorator falls back silently to `AzCliAuthProvider`. Format is stable since MSAL 4.x. |
| Read-only 15-min TTL shows stale data confusing users | Low | Low | `CacheAgeFormatter` already displays "⚡ 2m ago" indicators. Stale hint at 15 min is still surfaced. |
| Large test file migration introduces regressions | Medium | Medium | Mechanical change (find-replace `SyncCoordinator` → `SyncCoordinatorFactory`). Each test file compiled and run individually. |
| MSAL cache file locked by `az` CLI during reads | Low | Low | Default `fileReader` uses `FileStream(FileShare.ReadWrite)`. JSON parse failure falls back to az CLI. |

## Resolved Questions

All design questions have been resolved through codebase analysis and design review:
- **ReadOnlyCacheStaleMinutes location** → `internal const` in `SyncCoordinatorFactory`, not on `DisplayConfig` (the 15-min value is not user-configurable)
- **MsalCacheTokenProvider testability** → Injectable `Func<>` for file-read, mirroring `AzCliAuthProvider`'s pattern (DD-20)
- **File reading API** → `FileStream(FileShare.ReadWrite)` + `StreamReader` (not `File.ReadAllTextAsync`, which doesn't support `FileShare` parameters)
- **Token return format** → Raw secret string, not "Bearer"-prefixed. `AdoErrorHandler.ApplyAuthHeader` adds the scheme (DD-21)
- **Task #1658 scope** → Verified complete in codebase audit, removed from plan
- **Test file count** → Audited at 34 files across 3 test projects
- **#1673 task IDs** → Local suffixes (T1–T2) pending ADO seeding

## Open Questions

No blocking open questions at this revision.

## Files Affected

### New Files
| File Path | Purpose |
|-----------|---------|
| `src/Twig.Domain/Services/SyncCoordinatorFactory.cs` | Holds ReadOnly and ReadWrite SyncCoordinator instances |
| `src/Twig.Infrastructure/Auth/MsalCacheTokenProvider.cs` | Reads MSAL cache, decorates AzCliAuthProvider |
| `tests/Twig.Infrastructure.Tests/Auth/MsalCacheTokenProviderTests.cs` | Tests for MSAL cache token provider |

### Modified Files
| File Path | Changes |
|-----------|---------|
| `src/Twig/DependencyInjection/CommandServiceModule.cs` | Replace `SyncCoordinator` singleton with `SyncCoordinatorFactory` |
| `src/Twig.Mcp/Program.cs` | Replace `SyncCoordinator` singleton with `SyncCoordinatorFactory` |
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
| `src/Twig.Mcp/Tools/ContextTools.cs` | Inject `SyncCoordinatorFactory`, use `.ReadWrite` |
| `src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs` | Wrap `AzCliAuthProvider` in `MsalCacheTokenProvider` |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `MsalTokenCache` serialization |
| 34 test files (see Task #1664 table) | Replace `SyncCoordinator` with `SyncCoordinatorFactory` in test setup |

## ADO Work Item Structure

### Issue #1613: Parallelize network calls and deduplicate refresh logic
**Status:** Done
**Summary:** Implementation and test coverage are complete. `RefreshOrchestrator` parallelizes
active-item and child fetches via `Task.WhenAll`. `RefreshCommand` delegates fetch/save/conflict
logic to the orchestrator. Tests verify parallel fetch, sequential optimization (active in batch),
conflict detection, delegation, and concurrent metadata sync with resilience. Task #1658 was
the last remaining item and has been verified as complete in codebase audit.

---

### Issue #1614: Tiered cache TTL for read-only vs read-write commands
**Status:** To Do
**Goal:** Read-only commands use 15-min TTL; mutating commands use 5-min TTL via
`SyncCoordinatorFactory`.
**Prerequisites:** None (independent of #1613)

| Task ID | Description | Files | Status |
|---------|-------------|-------|--------|
| #1660 | Create `SyncCoordinatorFactory` with `ReadOnlyCacheStaleMinutes` constant | `src/Twig.Domain/Services/SyncCoordinatorFactory.cs` (new) | TO DO |
| #1661 | Update DI registration to use factory | `src/Twig/DependencyInjection/CommandServiceModule.cs`, `src/Twig.Mcp/Program.cs` | TO DO |
| #1662 | Migrate read-only commands to `factory.ReadOnly` | `StatusCommand.cs`, `TreeCommand.cs`, `ShowCommand.cs`, `StatusOrchestrator.cs`, `ReadTools.cs` (MCP) | TO DO |
| #1663 | Migrate mutating commands to `factory.ReadWrite` | `SetCommand.cs`, `LinkCommand.cs`, `RefreshOrchestrator.cs`, `ContextChangeService.cs`, `MutationTools.cs`, `ContextTools.cs` (MCP) | TO DO |
| #1664 | Update test files for factory injection | 34 test files across `Twig.Cli.Tests`, `Twig.Domain.Tests`, `Twig.Mcp.Tests` | TO DO |

**Task #1660 Details:**
- New file `src/Twig.Domain/Services/SyncCoordinatorFactory.cs`.
- Sealed class with primary constructor accepting two `SyncCoordinator` instances.
- Defines `internal const int ReadOnlyCacheStaleMinutes = 15;` (used by DI registration).
- Properties: `ReadOnly`, `ReadWrite` (both `SyncCoordinator`).
- No logic — purely a holder for DI.

**Task #1661 Details:**
- In `CommandServiceModule.AddTwigCommandServices()`:
  - Remove standalone `SyncCoordinator` singleton registration.
  - Add `SyncCoordinatorFactory` singleton registration that builds two coordinators
    with `SyncCoordinatorFactory.ReadOnlyCacheStaleMinutes` (ReadOnly) and
    `CacheStaleMinutes` from config (ReadWrite).
- In `Twig.Mcp/Program.cs`: identical change.

**Task #1662 Details (read-only commands):**
- `StatusCommand`: change `SyncCoordinator syncCoordinator` → `SyncCoordinatorFactory syncFactory`,
  replace all `syncCoordinator.` with `syncFactory.ReadOnly.`.
- `TreeCommand`: same pattern.
- `ShowCommand`: same pattern.
- `StatusOrchestrator`: same pattern (uses `.ReadOnly`).
- `ReadTools` (MCP): same pattern.

**Task #1663 Details (mutating commands):**
- `SetCommand`: change `SyncCoordinator syncCoordinator` → `SyncCoordinatorFactory syncFactory`,
  replace all `syncCoordinator.` with `syncFactory.ReadWrite.`.
- `LinkCommand`: same pattern.
- `RefreshOrchestrator`: same pattern (uses `.ReadWrite`).
- `ContextChangeService`: same pattern (uses `.ReadWrite`).
- `MutationTools` (MCP): same pattern.
- `ContextTools` (MCP): same pattern.

**Task #1664 Details (test updates):**
- All 34 test files that construct `SyncCoordinator` for command tests must:
  - Create a `SyncCoordinatorFactory` wrapping the test `SyncCoordinator`.
  - Pass the factory instead of the coordinator to the command/service under test.
- Pattern: `var factory = new SyncCoordinatorFactory(syncCoordinator, syncCoordinator);`
  (both tiers use the same mock in most tests).
- 4 base classes (`RefreshCommandTestBase`, `ContextToolsTestBase`, `MutationToolsTestBase`,
  `ReadToolsTestBase`) centralize coordinator setup — updating these covers their transitive
  consumer tests automatically.
- Tests that specifically verify TTL behavior should construct separate coordinators.

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
| 24 | `Twig.Cli.Tests/Rendering` | `CacheRefreshTests.cs` |
| 25 | `Twig.Domain.Tests/Services` | `ContextChangeServiceTests.cs` |
| 26 | | `ProtectedCacheWriterTests.cs` |
| 27 | | `RefreshOrchestratorTests.cs` |
| 28 | | `StatusOrchestratorTests.cs` |
| 29 | | `SyncCoordinatorTests.cs` |
| 30 | `Twig.Mcp.Tests` | `ProgramBootstrapTests.cs` |
| 31 | `Twig.Mcp.Tests/Tools` | `ContextToolsSetTests.cs` |
| 32 | | `ContextToolsTestBase.cs` *(base class)* |
| 33 | | `MutationToolsTestBase.cs` *(base class)* |
| 34 | | `ReadToolsTestBase.cs` *(base class)* |

**Acceptance Criteria:**
- [ ] Read-only commands use 15-min staleness threshold
- [ ] Mutating commands use 5-min staleness threshold
- [ ] SyncCoordinatorFactory is registered in both CLI and MCP DI
- [ ] All 34 test files compile and pass with factory injection
- [ ] No standalone SyncCoordinator registration remains in DI

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
- New sealed class `MsalCacheTokenProvider : IAuthenticationProvider` in `Auth/MsalCacheTokenProvider.cs`.
- DTOs in the same file:
  ```csharp
  internal sealed class MsalTokenCache
  {
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
- Add `[JsonSerializable(typeof(MsalTokenCache))]` to `TwigJsonContext`.
- Constructor: `(IAuthenticationProvider inner, string? cacheFilePath = null, Func<string, CancellationToken, Task<string?>>? fileReader = null, Func<DateTimeOffset>? clock = null)`.
- Default cache path: `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), ".azure", "msal_token_cache.json")`.
- Default file reader: reads via `new FileStream(path, FileMode.Open, FileAccess.Read,
  FileShare.ReadWrite)` + `StreamReader` (not `File.ReadAllTextAsync`, which does not
  support `FileShare` parameters). This avoids locking conflicts when `az` CLI writes
  the cache concurrently. Injectable via `Func<string, CancellationToken, Task<string?>>`
  for test isolation, mirroring `AzCliAuthProvider`'s `Func<ProcessStartInfo, Process?>`
  pattern.
- `GetAccessTokenAsync`:
  1. Check in-memory cache (50-min TTL matching AzCliAuthProvider).
  2. Read + parse MSAL cache file.
  3. Filter `AccessToken` entries where `Target` contains `499b84ac-1321-427f-aa17-267ca6975798`.
  4. Find entry with `ExpiresOn` > `now + 5 minutes` (buffer for clock skew).
  5. Valid → cache in-memory, return `Bearer {secret}`.
  6. Any failure → delegate to `_inner.GetAccessTokenAsync(ct)`.
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
- Test valid token in cache → returns Bearer token without spawning process.
- Test expired token → falls back to inner provider.
- Test missing cache file → falls back to inner provider.
- Test malformed JSON → falls back to inner provider.
- Test no ADO-scoped token → falls back to inner provider.
- Test multiple tokens → selects ADO-scoped entry.
- Test in-memory cache → second call skips file read.
- Use injectable `fileReader`, `clock`, and `cacheFilePath` for deterministic testing.
  The `fileReader` func enables tests to provide cache content without touching the
  filesystem, and to simulate read failures by returning `null` or throwing.

**Acceptance Criteria:**
- [ ] Valid MSAL cache token is returned without process spawn
- [ ] Expired/missing/malformed cache silently falls back to `az` CLI
- [ ] In-memory cache prevents repeated file reads
- [ ] No `Microsoft.Identity.Client` dependency added
- [ ] DTOs registered in `TwigJsonContext` for AOT compatibility
- [ ] `PatAuthProvider` path is completely unaffected

## PR Groups

### PG-1: Tiered cache core infrastructure (#1660, #1661)
**Type:** Deep (few files, architectural change)
**Tasks:** #1660, #1661
**Estimated LoC:** ~150
**Successor:** PG-2
**Files:** 2 source files (SyncCoordinatorFactory, CommandServiceModule + MCP Program)

### PG-2: Command migration to SyncCoordinatorFactory (#1662, #1663)
**Type:** Wide (many files, mechanical change)
**Tasks:** #1662, #1663
**Estimated LoC:** ~300
**Predecessor:** PG-1
**Successor:** PG-3
**Files:** 11 source files (5 commands, 3 orchestrators/services, 3 MCP tools)

### PG-3: Test migration to SyncCoordinatorFactory (#1664)
**Type:** Wide (many files, mechanical change)
**Tasks:** #1664
**Estimated LoC:** ~800
**Predecessor:** PG-2
**Files:** 34 test files

### PG-4: MSAL token cache optimization (#1673)
**Type:** Deep (few files, complex logic)
**Tasks:** #1673-T1, #1673-T2
**Estimated LoC:** ~350
**Successor:** None (independent of PG-1–PG-3)
**Files:** 3 source files + 1 test file

## References

- Azure CLI MSAL token cache: `~/.azure/msal_token_cache.json` (format stable since MSAL 4.x)
- ADO OAuth resource ID: `499b84ac-1321-427f-aa17-267ca6975798`
- Azure CLI client ID: `04b07795-8ddb-461a-bbee-02f9e1bf7b46`
- [sync-perf-optimization.plan.md](./sync-perf-optimization.plan.md) — v1 plan (batch fetch + HTTP transport)
- [sync-perf-optimization-v2.plan.md](./sync-perf-optimization-v2.plan.md) — v2 plan (parallel refresh + tiered cache)
