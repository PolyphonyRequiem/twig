# Twig Test Resilience — Edge-Case & Failure-Mode Coverage Plan

> **Status:** IN PROGRESS — EPIC-001 DONE, EPIC-002 DONE, EPIC-003 next  
> **Date:** 2026-03-21 | **EPIC-001 Completed:** 2026-03-22 | **EPIC-002 Completed:** 2026-03-22
> **Scope:** Net-new tests targeting untested failure modes, race conditions, and edge cases  
> **Companion:** [twig-test-quality.plan.md](twig-test-quality.plan.md) (structural refactoring — independent prerequisite chain)

---

## Executive Summary

Twig's existing ~1,160+ passing tests cover happy paths well but leave critical failure modes untested. The `SelfUpdater` (binary replacement logic) has zero tests and a potential path-traversal security risk. `SyncWorkingSetAsync` fires unbounded `Task.WhenAll` with no throttling and zero concurrent test scenarios — a latent source of rate-limit failures and silent data races. ADO HTTP errors are mapped correctly but never retried. Config file corruption crashes the app on startup with an unhandled exception. This plan adds ~90–120 targeted tests across 4 EPICs, ordered by risk severity.

This plan is **independent** of the test quality plan. It adds new tests to cover risk; the quality plan restructures existing tests for maintainability. They can execute in parallel.

---

## Current State

| Risk Area | Production Code | Test Files | Gap |
|-----------|----------------|------------|-----|
| **SelfUpdater** | `SelfUpdater.cs` (~120 lines) | **0 tests** | Binary download, extraction, replacement, cleanup — all untested. Path traversal vulnerability. |
| **Concurrency** | `SyncCoordinator.cs` L95–97 (`Task.WhenAll`) | 200+ lines in `SyncCoordinatorTests` | **Zero concurrent scenarios.** All tests are sequential. |
| **Cache races** | `ProtectedCacheWriter.cs` L24–86 | `ProtectedCacheWriterTests` | Only sequential save/skip. No concurrent save-during-sync test. |
| **ADO HTTP resilience** | `AdoRestClient.cs` L190–215, `AdoErrorHandler.cs` | `AdoErrorHandlerTests` covers status codes | No retry for 429/500/503. No timeout escalation. No partial-batch failure. |
| **Git CLI edge cases** | `GitCliService.cs` L59–120 | `GitCliServiceTests` | No test for git-not-found, no-remote, merge-conflict state. |
| **Config I/O** | `TwigConfiguration.cs` L31–58 | `TwigConfigurationTests` | No test for malformed JSON, permission denied, disk full. |
| **Startup bootstrap** | `Program.cs` L50–52, L109–110 | **0 tests** | Sync-over-async config load; no error handling for corrupt config/DB. |
| **PR creation** | `AdoGitClient.cs` L71–95, `PrCommand.cs` L96–100 | `PrCommandTests` (happy path only) | Bare `Exception` catch — no tests for policy-blocked, auth-failure, branch-deleted. |
| **Process config** | `ProcessConfiguration.cs` L101–108, `StateTransitionService.cs` L28–31 | `ProcessConfigurationTests` | No test for empty config (all records malformed), unknown type transitions. |

---

## Strategy

### Principles

1. **Test the sad paths users actually hit.** ADO returning 429, git not being installed, config file corrupted after a crash — these are real support scenarios.
2. **Prove safety invariants hold under concurrency.** If `ProtectedCacheWriter` is supposed to prevent overwrite of dirty items during sync, there must be a test that exercises the race.
3. **Security first.** `SelfUpdater` handles downloaded archives and binary replacement. Path traversal, incomplete downloads, and permission failures must be validated.
4. **No simulated terminals.** All tests use standard xUnit + NSubstitute + Shouldly. Rendering/visual tests are out of scope.

---

## EPICs

### EPIC-001: SelfUpdater test coverage ✅ DONE

**Problem:** `SelfUpdater` has **zero unit tests**. It downloads archives from GitHub, extracts binaries, replaces the running executable, and cleans up. A bug here bricks the tool. A path-traversal vulnerability in archive extraction is a security risk.

**Production code:** `src/Twig.Infrastructure/GitHub/SelfUpdater.cs`

**Completed:** 2026-03-22 — 24 tests added. IFileSystem + IHttpDownloader interfaces extracted, SelfUpdater refactored with internal DI constructor (public HttpClient constructor preserved). Path traversal (both `../../` and leading-slash absolute-path variants) proven safe. Task 7 production fix: best-effort `.old` deletion with try/catch.

| # | Task | What to test | Status |
|---|------|-------------|--------|
| 1 | **Extract testable interface** — `SelfUpdater` currently calls `HttpClient`, `ZipFile`, `File.Move/Copy`, and `Process.GetCurrentProcess()` directly. Extract `IFileSystem` and `IHttpDownloader` interfaces (or use existing abstractions) so tests can inject fakes. | Testability refactor | ✅ DONE |
| 2 | **Happy path: download + extract + replace** — Mock HTTP response with valid zip/tar archive. Verify: archive extracted to temp dir, binary copied to process path, old binary renamed to `.old`, temp dir cleaned up. | `SelfUpdaterTests.cs` | ✅ DONE |
| 3 | **Path traversal defense** — Create a mock archive containing an entry with `../../etc/passwd` path. Verify: extraction throws or skips the malicious entry. **This is a security test.** | `SelfUpdaterTests.cs` | ✅ DONE |
| 4 | **Download failure** — Mock HTTP 404, 403, 503, timeout. Verify: descriptive exception thrown (not generic `InvalidOperationException`). | `SelfUpdaterTests.cs` | ✅ DONE |
| 5 | **Incomplete download** — Mock HTTP response that truncates mid-stream. Verify: extraction fails gracefully, temp files cleaned up, no partial binary left on disk. | `SelfUpdaterTests.cs` | ✅ DONE |
| 6 | **Permission denied on replace** — Mock file system that throws `UnauthorizedAccessException` on File.Move. Verify: descriptive error, no orphaned temp files, old binary not deleted. | `SelfUpdaterTests.cs` | ✅ DONE |
| 7 | **Windows file lock on old binary** — Mock file system where `.old` file exists and is locked. Verify: cleanup is best-effort (no crash), update still succeeds. | `SelfUpdaterTests.cs` | ✅ DONE |
| 8 | **CleanupOldBinary** — Verify: `.old` file deleted on next startup. Verify: no crash when `.old` doesn't exist. Verify: no crash when `ProcessPath` is null. | `SelfUpdaterTests.cs` | ✅ DONE |

**Outcome:** SelfUpdater has full error-path coverage. Path traversal vulnerability either fixed or proven safe. Binary update failures produce actionable errors.

---

### EPIC-002: Concurrency & cache race conditions ✅ DONE

**Problem:** `SyncWorkingSetAsync` fires unbounded `Task.WhenAll` across all stale items with no throttling. `ProtectedCacheWriter` is designed to prevent dirty-item overwrite during sync, but no test exercises the actual race. A concurrent `SaveAsync` during sync could silently lose data if the protection window has an off-by-one.

**Production code:** `SyncCoordinator.cs` L95–97, `ProtectedCacheWriter.cs` L24–86, `SqliteWorkItemRepository.cs` L160–181

**Completed:** 2026-03-22 — 6 tests added. Production fix: `SyncWorkingSetAsync` now handles partial fetch failures individually (was `Task.WhenAll` all-or-nothing). New `SyncResult.PartiallyUpdated` variant reports saved count + failures. `SpectreRenderer` updated to render partial results.

| # | Task | What to test | Status |
|---|------|-------------|--------|
| 1 | **Batch fetch partial failure** — 20 items stale, `FetchAsync` succeeds for 18, throws for 2. Verify: 18 saved, 2 reported as failed in `SyncResult`, no data loss. | `SyncCoordinatorTests.cs` | ✅ DONE |
| 2 | **ADO rate-limit during batch** — Mock `FetchAsync` to throw `AdoRateLimitException` for items 5+ (simulating 429 mid-batch). Verify: items 1–4 saved, rest reported as failed, no partial corruption. | `SyncCoordinatorTests.cs` | ✅ DONE |
| 3 | **Concurrent save-during-sync race** — Start `SyncWorkingSetAsync` → mock `FetchAsync` with artificial delay → while fetch is in flight, call `SaveAsync` on one of the items being synced → complete sync. Verify: `ProtectedCacheWriter` detects the dirty item and skips overwrite. The locally-saved version wins. | `ProtectedCacheWriterTests.cs` | ✅ DONE |
| 4 | **Concurrent dual-sync overlap** — Two `SyncWorkingSetAsync` calls with overlapping item sets {1–10} and {5–15}. Verify: no duplicate saves for items 5–10, final cache state is consistent. | `SyncCoordinatorTests.cs` | ✅ DONE |
| 5 | **SQLite busy timeout under contention** — Two threads: one doing `SaveBatchAsync` (large batch), one doing `GetByIdAsync` during the save. Verify: reader succeeds (WAL mode allows concurrent read), no `SqliteException`. | `SqliteWorkItemRepositoryTests.cs` | ✅ DONE |
| 6 | **Transaction rollback on partial batch save** — `SaveBatchAsync` with 10 items, mock failure on item 7. Verify: transaction rolled back, items 1–6 NOT persisted, exception surfaced to caller. | `SqliteWorkItemRepositoryTests.cs` | ✅ DONE |

**Outcome:** Concurrency invariants are proven: dirty items survive sync, partial failures don't corrupt cache, SQLite WAL mode handles contention.

---

### EPIC-003: Error resilience paths

**Problem:** ADO HTTP errors are correctly mapped to typed exceptions but never retried — a transient 429 or 503 fails immediately. Git CLI errors are caught but edge states (detached HEAD, no remote, merge conflict) are not tested. Config file corruption crashes the app with an unhandled `JsonException`.

**Production code:** `AdoRestClient.cs`, `AdoErrorHandler.cs`, `GitCliService.cs`, `TwigConfiguration.cs`

| # | Task | What to test |
|---|------|-------------|
| 1 | **ADO 429 with Retry-After** — Mock `SendAsync` returning 429 with `Retry-After: 2`. Verify: `AdoRateLimitException` contains the retry-after value. (Retry *policy* is out of scope for this plan — but the exception must carry enough info for a future retry policy to use.) | `AdoErrorHandlerTests.cs` |
| 2 | **ADO 503 Service Unavailable** — Mock `SendAsync` returning 503. Verify: `AdoServerException` thrown with status code. Verify no silent swallow. | `AdoErrorHandlerTests.cs` |
| 3 | **ADO batch fetch with 200-item limit** — Construct a WIQL result with 201 item IDs. Verify: `GetWorkItemsBatchAsync` pages correctly (or rejects gracefully), no silent truncation. | `AdoWorkItemServiceTests.cs` |
| 4 | **Git not installed** — Mock `Process.Start` throwing `Win32Exception` (code 2). Verify: `GitOperationException` with message "git binary not found" (or similar). | `GitCliServiceTests.cs` |
| 5 | **Git no remote configured** — Mock `git remote get-url origin` returning non-zero exit code. Verify: caller receives descriptive error, not generic "git operation failed". | `GitCliServiceTests.cs` |
| 6 | **Git detached HEAD handling** — Mock `git branch --show-current` returning empty string. Verify: `GetCurrentBranchAsync` returns `null` or a sentinel (not "HEAD" raw string that confuses callers). | `GitCliServiceTests.cs` |
| 7 | **Config malformed JSON** — Write `{ "org": "test", bad json` to config path. Call `LoadAsync`. Verify: descriptive exception (not raw `JsonException`), or graceful fallback to defaults with warning. | `TwigConfigurationTests.cs` |
| 8 | **Config file permission denied** — Mock file system (or use read-only temp file) where config path exists but is unreadable. Verify: descriptive exception, not raw `UnauthorizedAccessException`. | `TwigConfigurationTests.cs` |
| 9 | **PR creation: auth failure** — Mock `AdoGitClient.CreatePullRequestAsync` throwing `AdoAuthenticationException`. Verify: `PrCommand` prints actionable message ("check your PAT") not generic "Failed to create pull request". | `PrCommandTests.cs` |
| 10 | **PR creation: merge policy blocked** — Mock `AdoGitClient` throwing `AdoBadRequestException` with merge-policy details. Verify: command output includes the policy name/reason. | `PrCommandTests.cs` |

**Outcome:** Every ADO/Git/Config error path that can happen in production has a test proving the error message is actionable. Future retry policies have typed exceptions with all necessary metadata.

---

### EPIC-004: Configuration & startup robustness

**Problem:** `Program.cs` performs sync-over-async config loading and SQLite state pre-computation during DI startup. A corrupt config file or database causes an unhandled crash. `ProcessConfiguration.FromRecords` silently drops malformed records — an empty config breaks all state transitions with no diagnostic. `StateCommand` calls `GetConfiguration()` without a try-catch.

**Production code:** `Program.cs` L50–52 + L109–110, `ProcessConfiguration.cs` L101–108, `StateTransitionService.cs` L22–56, `StateCommand.cs` L48

| # | Task | What to test |
|---|------|-------------|
| 1 | **ProcessConfiguration.FromRecords: all records malformed** — Pass records where every `TypeName` is null/empty. Verify: returns empty `ProcessConfiguration` (not null, not crash). Verify: callers handle empty config gracefully. | `ProcessConfigurationTests.cs` |
| 2 | **ProcessConfiguration.FromRecords: unknown work item type** — Pass a record with `TypeName = "CustomWorkItemType"`. Verify: either included or explicitly skipped — behavior is documented and tested. | `ProcessConfigurationTests.cs` |
| 3 | **StateTransitionService: unknown type** — Call `Evaluate` with a `WorkItemType` not in the config. Verify: returns `TransitionResult { IsAllowed = false }` with a reason distinguishing "type not configured" from "transition blocked". | `StateTransitionServiceTests.cs` |
| 4 | **StateTransitionService: unknown state** — Call `Evaluate` with valid type but `fromState = "NonexistentState"`. Verify: returns not-allowed with descriptive reason. | `StateTransitionServiceTests.cs` |
| 5 | **StateCategoryResolver: custom ADO process state** — Call `Resolve` with `state = "CustomPhase"` not in any entry list and not in the hardcoded fallback map. Verify: returns `StateCategory.Unknown`. Document this as intended behavior. | `StateCategoryResolverTests.cs` |
| 6 | **LegacyDbMigrator: WAL/SHM corruption** — Stage a legacy DB with corrupt WAL file. Call `MigrateIfNeeded`. Verify: migration succeeds (WAL moved along with DB) or fails gracefully (warning printed, app continues). | `LegacyDbMigratorTests.cs` |
| 7 | **SqliteCacheStore: schema mismatch recovery** — Open a DB with an old schema version. Verify: `SchemaWasRebuilt` flag is set, tables recreated, no data from old schema leaks. | `SqliteCacheStoreTests.cs` |
| 8 | **Startup: corrupt config → clear error** — Create a temp config file with invalid JSON. Simulate the `TwigConfiguration.LoadAsync` call from `Program.cs`. Verify: user-facing error message (not raw stack trace). *(This may require a thin wrapper around the bootstrap path for testability.)* | `BootstrapTests.cs` or `TwigConfigurationTests.cs` |

**Outcome:** Every configuration edge case — empty process config, unknown states, corrupt files, schema drift — has a test proving the app degrades gracefully with actionable diagnostics.

---

## Implementation Sequence

```
EPIC-001: SelfUpdater test coverage (8 tasks)
  ├── Task 1: Extract testable interface (testability refactor)
  ├── Task 2: Happy path download + extract + replace
  ├── Task 3: Path traversal defense (SECURITY)
  ├── Task 4: Download failure (404/403/503/timeout)
  ├── Task 5: Incomplete download
  ├── Task 6: Permission denied on replace
  ├── Task 7: Windows file lock on old binary
  └── Task 8: CleanupOldBinary

EPIC-002: Concurrency & cache race conditions (6 tasks)
  ├── Task 1: Batch fetch partial failure
  ├── Task 2: ADO rate-limit during batch
  ├── Task 3: Concurrent save-during-sync race
  ├── Task 4: Concurrent dual-sync overlap
  ├── Task 5: SQLite busy timeout under contention
  └── Task 6: Transaction rollback on partial batch save

EPIC-003: Error resilience paths (10 tasks)
  ├── Task 1: ADO 429 with Retry-After
  ├── Task 2: ADO 503 Service Unavailable
  ├── Task 3: ADO batch fetch with 200-item limit
  ├── Task 4: Git not installed
  ├── Task 5: Git no remote configured
  ├── Task 6: Git detached HEAD handling
  ├── Task 7: Config malformed JSON
  ├── Task 8: Config file permission denied
  ├── Task 9: PR creation: auth failure
  └── Task 10: PR creation: merge policy blocked

EPIC-004: Configuration & startup robustness (8 tasks)
  ├── Task 1: ProcessConfiguration: all records malformed
  ├── Task 2: ProcessConfiguration: unknown work item type
  ├── Task 3: StateTransitionService: unknown type
  ├── Task 4: StateTransitionService: unknown state
  ├── Task 5: StateCategoryResolver: custom ADO process state
  ├── Task 6: LegacyDbMigrator: WAL/SHM corruption
  ├── Task 7: SqliteCacheStore: schema mismatch recovery
  └── Task 8: Startup: corrupt config → clear error

No prerequisite chain — all EPICs are independent.
Recommended priority: EPIC-001 (security) → EPIC-002 (data loss) → EPIC-003 (error UX) → EPIC-004 (edge cases).
```

---

## What This Plan Does NOT Change

- **Production code behavior** — This plan adds tests, not features. Exception: EPIC-001 Task 1 extracts an interface for testability, and Task 3 may require a path-traversal fix if the vulnerability is confirmed.
- **Retry policies** — Testing that exceptions carry enough info for a future retry policy, but not implementing retry logic.
- **Visual/rendering tests** — No simulated terminals, no screenshot comparison, no NerdFont rendering validation.
- **Test framework** — xUnit + Shouldly + NSubstitute. No new dependencies.
- **Happy paths** — Existing happy-path tests are unchanged. This plan covers only sad paths and edge cases.

---

## Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| SelfUpdater test count | 0 | 7+ |
| Concurrent scenario tests | 0 | 6+ |
| Error-path tests (ADO/Git/Config) | ~12 | 30+ |
| Production files with zero tests | ~74 (43%) | ≤60 (35%) |
| Path traversal vulnerability | Unvalidated | Tested (fixed if vulnerable) |

---
