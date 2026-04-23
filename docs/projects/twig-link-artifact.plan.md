# twig link artifact: General Artifact Link Support

## Plan Metadata

| Field | Value |
|-------|-------|
| **Status** | Draft |
| **Work Item** | #2016 |
| **Type** | Issue |
| **Parent** | #2014 |
| **Revision** | 0 |
| **Revision Notes** | Initial draft. |

## Executive Summary

Add a general artifact link command (`twig link artifact <url> --name <name>`) and corresponding
MCP tool (`twig_link_artifact`) to create ArtifactLink and Hyperlink relations on ADO work items.
This extends the existing `IAdoWorkItemService` interface with `AddArtifactLinkAsync`, implements
duplicate link detection (ADO HTTP 409), and exposes the capability through both CLI and MCP surfaces.
The feature enables conductor workflows to link plan documents, commits, branches, and PR URLs to
work items programmatically.

## Background

### Current Architecture

Twig's link operations are split across two interfaces:

| Interface | Location | Methods | Available In |
|-----------|----------|---------|-------------|
| `IAdoWorkItemService` | `Twig.Domain` | `AddLinkAsync`, `RemoveLinkAsync` | CLI + MCP |
| `IAdoGitService` | `Twig.Domain` | `AddArtifactLinkAsync` | CLI only (optional) |

**`IAdoWorkItemService.AddLinkAsync`** creates work-item-to-work-item relations (parent/child/related/
predecessor/successor). Implemented by `AdoRestClient`, it uses JSON Patch to add a relation with a
target work item URL. No optimistic concurrency (no If-Match header).

**`IAdoGitService.AddArtifactLinkAsync`** creates artifact links (branch refs, commit refs) on work
items. Implemented by `AdoGitClient`, it uses JSON Patch with an If-Match header (optimistic
concurrency via revision). Currently used by `BranchCommand`, `CommitCommand`, and `PrCommand`.

**Key gap:** `IAdoGitService` is **not available in the MCP context** (`WorkspaceContext` doesn't
expose it, and `WorkspaceContextFactory` doesn't construct it). This means the MCP server cannot
create artifact links today. Adding the capability requires either exposing `IAdoGitService` in MCP
or adding artifact link support to `IAdoWorkItemService`.

### CLI Link Commands

The `link` command group currently supports three subcommands:

| Command | Handler | Operation |
|---------|---------|-----------|
| `link parent <id>` | `LinkCommand.ParentAsync` | Set parent via Hierarchy-Reverse |
| `link unparent` | `LinkCommand.UnparentAsync` | Remove parent link |
| `link reparent <id>` | `LinkCommand.ReparentAsync` | Swap parent atomically |

All three operate on hierarchy links only. No subcommand exists for artifact or hyperlink relations.

### MCP Link Tool

`twig_link` in `CreationTools.cs` creates work-item-to-work-item links using `LinkTypeMapper`
to resolve friendly names (parent, child, related, predecessor, successor) to ADO relation types.
It does not support artifact links.

### ADO Error Handling

`AdoErrorHandler.ThrowOnErrorAsync` handles HTTP status codes and throws typed exceptions:

| Status Code | Exception | Notes |
|-------------|-----------|-------|
| 400 | `AdoBadRequestException` | Bad request body |
| 401 | `AdoAuthenticationException` | Auth failure |
| 404 | `AdoNotFoundException` | Work item not found |
| 409 | *(not handled)* | Falls through to generic `AdoException` |
| 412 | `AdoConflictException` | Optimistic concurrency conflict |
| 429 | `AdoRateLimitException` | Rate limited |
| 5xx | `AdoServerException` | Server errors |

HTTP 409 (Conflict) is not explicitly handled — it falls into the default branch and throws
a generic `AdoException("Unexpected HTTP 409: ...")`. ADO returns 409 when adding a duplicate
artifact link to a work item. This needs explicit handling for graceful "already linked"
reporting.

### Call-Site Audit: IAdoWorkItemService

Since this design adds a method to `IAdoWorkItemService`, all implementors must be updated:

| Implementor | File | Impact |
|-------------|------|--------|
| `AdoRestClient` | `Ado/AdoRestClient.cs` | Add `AddArtifactLinkAsync` implementation |
| NSubstitute mocks | Various test files | Auto-handled — NSubstitute returns defaults for new methods |

### Call-Site Audit: AdoErrorHandler.ThrowOnErrorAsync

Adding 409 handling affects all code paths that trigger ADO REST calls:

| Caller | File | Current 409 Behavior | New Behavior |
|--------|------|---------------------|--------------|
| `AdoRestClient.SendAsync` | `AdoRestClient.cs` | Throws generic `AdoException` | Throws `AdoDuplicateRelationException` |
| `AdoGitClient.SendAsync` | `AdoGitClient.cs` | Throws generic `AdoException` | Throws `AdoDuplicateRelationException` |

The existing callers that use `AddArtifactLinkAsync` on `AdoGitClient` (BranchCommand,
CommitCommand, PrCommand) all catch broad `Exception` with best-effort semantics — the new
exception type will be caught by their existing handlers. No behavioral change.

## Problem Statement

The conductor workflow needs to link plan document URLs, specific commits, and PR URLs to ADO
work items. Today, artifact linking is only possible through `IAdoGitService.AddArtifactLinkAsync`,
which is:

1. **Unavailable in MCP** — `WorkspaceContext` doesn't expose `IAdoGitService`, so the MCP server
   cannot create artifact links
2. **Unavailable without git configuration** — `IAdoGitService` requires a resolved git project and
   repository, which may not be configured in all workspaces
3. **Not exposed as a standalone CLI command** — artifact linking is embedded in `BranchCommand`,
   `CommitCommand`, and `PrCommand` as a side-effect, not as a composable operation
4. **No duplicate link handling** — HTTP 409 from ADO is not handled, causing confusing error messages
   when re-linking an already-linked artifact

## Goals and Non-Goals

### Goals

- **G1**: Add `twig link artifact <url> --name <name>` CLI command supporting active item and `--id` override
- **G2**: Add `twig_link_artifact` MCP tool with identical capabilities
- **G3**: Auto-detect relation type: `ArtifactLink` for `vstfs:///` URIs, `Hyperlink` for HTTP URLs
- **G4**: Handle duplicate links gracefully — return success with "already linked" message on ADO 409
- **G5**: Add `AddArtifactLinkAsync` to `IAdoWorkItemService` so both CLI and MCP can use it without `IAdoGitService`

### Non-Goals

- **NG1**: Migrating existing `BranchCommand`/`CommitCommand`/`PrCommand` off `IAdoGitService.AddArtifactLinkAsync`
  — they work fine and have different concurrency semantics (caller-managed revision)
- **NG2**: Adding `IAdoGitService` to the MCP `WorkspaceContext` — artifact links don't require git configuration
- **NG3**: Removing artifact links (unlink) — this can be added later if needed
- **NG4**: Listing artifact links — the `twig tree` command already shows links

## Requirements

### Functional

| ID | Requirement |
|----|------------|
| F1 | `twig link artifact <url>` adds an artifact/hyperlink relation to the active work item |
| F2 | `--name <name>` sets the link display name (defaults to "Link") |
| F3 | `--id <id>` targets a specific work item instead of the active item |
| F4 | Auto-detects relation type: `ArtifactLink` for `vstfs:///`, `Hyperlink` for `http(s)://` |
| F5 | Returns success with informational message when link already exists (HTTP 409) |
| F6 | `twig_link_artifact` MCP tool provides equivalent functionality |
| F7 | Supports all output formats: human, json, json-compact, minimal |

### Non-Functional

| ID | Requirement |
|----|------------|
| NF1 | AOT-compatible — no reflection, all JSON via `TwigJsonContext` |
| NF2 | `TreatWarningsAsErrors` — no warnings |
| NF3 | Process-agnostic — no hardcoded process template assumptions |

## Proposed Design

### Architecture Overview

```
CLI                              MCP
 │                                │
 ▼                                ▼
[Program.cs]                  [CreationTools.cs]
 "link artifact"               twig_link_artifact
 │                                │
 ▼                                ▼
[ArtifactLinkCommand]        [WorkspaceContext]
 │                                │
 ├──▶ ActiveItemResolver         ├──▶ ActiveItemResolver
 │    (or --id override)         │    (workItemId param)
 │                                │
 └──▶ IAdoWorkItemService  ◀────┘
      .AddArtifactLinkAsync()
       │
       ▼
      [AdoRestClient]
       │
       ▼
      ADO REST API
      PATCH /_apis/wit/workitems/{id}
```

### Key Components

#### 1. `IAdoWorkItemService.AddArtifactLinkAsync` (Domain Layer)

New method on the existing interface:

```csharp
Task<bool> AddArtifactLinkAsync(
    int workItemId, string url, string? name = null, CancellationToken ct = default);
```

Returns `true` if the link was created, `false` if it already existed (duplicate detection).
The implementation auto-fetches the latest revision for the If-Match header.

#### 2. `AdoRestClient.AddArtifactLinkAsync` (Infrastructure Layer)

Implementation in the existing ADO REST client:

1. Determine relation type: `"ArtifactLink"` if URL starts with `vstfs:///`, else `"Hyperlink"`
2. GET the work item to obtain current revision (for If-Match)
3. PATCH `/relations/-` with the artifact link relation
4. Catch `AdoDuplicateRelationException` → return `false`

Reuses the existing `AdoArtifactLinkRelation` and `AdoArtifactLinkAttributes` DTOs.
For Hyperlink, sets `attributes.comment` instead of `attributes.name`.

#### 3. `AdoDuplicateRelationException` (Infrastructure Layer)

New exception type for HTTP 409 Conflict, added to `AdoErrorHandler`:

```csharp
case HttpStatusCode.Conflict:
    var conflictMsg = await TryReadErrorMessageAsync(response, ct);
    throw new AdoDuplicateRelationException(conflictMsg);
```

#### 4. `ArtifactLinkCommand` (CLI Layer)

New command class (separate from `LinkCommand` which handles hierarchy operations):

```csharp
public sealed class ArtifactLinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    OutputFormatterFactory formatterFactory)
```

Methods:
- `ExecuteAsync(string url, string? name, int? id, string outputFormat, CancellationToken ct)`

Flow:
1. Resolve work item (active or `--id`)
2. Call `adoService.AddArtifactLinkAsync(itemId, url, name, ct)`
3. Output success (with "already linked" variant if duplicate)

#### 5. `twig_link_artifact` MCP Tool

New method on `CreationTools`:

```csharp
[McpServerTool(Name = "twig_link_artifact")]
public async Task<CallToolResult> LinkArtifact(
    int workItemId, string url, string? name = null, string? workspace = null, CancellationToken ct = default)
```

Flow mirrors the CLI command, using `McpResultBuilder.FormatArtifactLinked`.

### Data Flow — Link Creation

```
User: twig link artifact https://dev.azure.com/.../file.md --name "Plan doc"
  │
  ▼
ArtifactLinkCommand.ExecuteAsync
  │
  ├─ ActiveItemResolver.GetActiveItemAsync()  ──▶  WorkItem (id=42, rev=7)
  │
  ├─ adoService.AddArtifactLinkAsync(42, url, "Plan doc")
  │    │
  │    ├─ Detect scheme: https:// → rel = "Hyperlink"
  │    ├─ GET /workitems/42 → revision 7
  │    ├─ PATCH /workitems/42 with relation, If-Match: 7
  │    │    ├─ 200 OK → return true
  │    │    └─ 409 Conflict → return false (duplicate)
  │    │
  │    └─ return bool
  │
  └─ Output: "Linked https://... to #42" or "Already linked to #42"
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Where to add artifact link method | `IAdoWorkItemService` | Available in both CLI and MCP. Artifact links are work item operations, not git operations. |
| Revision management | Auto-fetch inside implementation | Callers don't need to manage revision. Slight overhead (extra GET) but simpler API. |
| Duplicate detection | Return `bool` (not exception) | Cleaner API — callers don't need try/catch for an expected scenario. Exception still available for other consumers. |
| Separate command class | `ArtifactLinkCommand` (not extending `LinkCommand`) | `LinkCommand` manages hierarchy links. Artifact links are conceptually different (URL target, not work item target). |
| MCP tool naming | `twig_link_artifact` (new tool, not extending `twig_link`) | Different parameter shape (URL + name vs sourceId + targetId + linkType). |
| URL scheme detection | Auto-detect `vstfs:///` vs `http(s)://` | User doesn't need to know ADO internals. Maps to correct relation type automatically. |
| Hyperlink attributes | `attributes.comment` for `--name` value | ADO convention: ArtifactLink uses `name`, Hyperlink uses `comment`. |

## Dependencies

### External
- ADO REST API 7.1 — PATCH work items with relation operations
- ADO `ArtifactLink` and `Hyperlink` relation types

### Internal
- `IAdoWorkItemService` interface — adding new method
- `AdoErrorHandler` — adding 409 handling
- `AdoArtifactLinkRelation` DTO — adding nullable `Comment` property to `AdoArtifactLinkAttributes`
- `TwigJsonContext` — no new types needed (existing DTOs reused)

### Sequencing
- No prerequisites — all dependencies are in the current codebase

## Impact Analysis

### Backward Compatibility
- `IAdoWorkItemService` gains a new method — `AdoRestClient` (sole implementor) must be updated
- `AdoArtifactLinkAttributes` gains nullable `Comment` property — backward compatible (null omitted via `JsonIgnoreCondition.WhenWritingNull`)
- `AdoErrorHandler` gains 409 case — previously threw generic `AdoException`, now throws `AdoDuplicateRelationException` (subclass of `AdoException`). Existing catch blocks still work.

### Performance
- One additional GET request per artifact link (to fetch revision) — acceptable for a user-initiated operation
- No impact on existing operations

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 1 | Does ADO accept `http://` URLs as `ArtifactLink` relation type, or only `vstfs:///`? | Low | Design auto-detects and uses `Hyperlink` for HTTP URLs. If ADO accepts both, auto-detection is still correct behavior. |
| 2 | Does ADO return 409 or 400 for duplicate artifact links? | Low | Implementation handles both: 409 via `AdoDuplicateRelationException`, 400 via message inspection in the command handler. |

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig/Commands/ArtifactLinkCommand.cs` | CLI command for `twig link artifact` |
| `tests/Twig.Cli.Tests/Commands/ArtifactLinkCommandTests.cs` | Unit tests for CLI command |
| `tests/Twig.Mcp.Tests/Tools/CreationToolsLinkArtifactTests.cs` | Unit tests for MCP tool |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Domain/Interfaces/IAdoWorkItemService.cs` | Add `AddArtifactLinkAsync` method |
| `src/Twig.Infrastructure/Ado/AdoRestClient.cs` | Implement `AddArtifactLinkAsync` |
| `src/Twig.Infrastructure/Ado/AdoErrorHandler.cs` | Add HTTP 409 → `AdoDuplicateRelationException` |
| `src/Twig.Infrastructure/Ado/Exceptions/AdoExceptions.cs` | Add `AdoDuplicateRelationException` class |
| `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestDtos.cs` | Add nullable `Comment` property to `AdoArtifactLinkAttributes` |
| `src/Twig/Program.cs` | Add `[Command("link artifact")]` method |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Register `ArtifactLinkCommand` |
| `src/Twig.Mcp/Tools/CreationTools.cs` | Add `LinkArtifact` MCP tool method |
| `src/Twig.Mcp/Services/McpResultBuilder.cs` | Add `FormatArtifactLinked` method |

## ADO Work Item Structure

### Issue #2016: twig link artifact: General artifact link support

**Goal:** Add general artifact link (branch refs, commit refs, arbitrary URLs) support to
the active work item via both CLI and MCP.

**Prerequisites:** None

#### Tasks

| Task | ID | Description | Files | Status |
|------|----|-------------|-------|--------|
| T1: Service layer + duplicate detection | #2020 (existing) | Add `AddArtifactLinkAsync` to `IAdoWorkItemService`, implement in `AdoRestClient`. Add `AdoDuplicateRelationException` and 409 handling to `AdoErrorHandler`. Add `Comment` property to `AdoArtifactLinkAttributes`. | `IAdoWorkItemService.cs`, `AdoRestClient.cs`, `AdoErrorHandler.cs`, `AdoExceptions.cs`, `AdoPullRequestDtos.cs` | TO DO |
| T2: CLI command | (new) | Create `ArtifactLinkCommand` class with `--name` and `--id` flags. Register in DI and add `[Command("link artifact")]` to Program.cs. Update help text. | `ArtifactLinkCommand.cs`, `CommandRegistrationModule.cs`, `Program.cs` | TO DO |
| T3: MCP tool | (new) | Add `twig_link_artifact` tool to `CreationTools.cs`. Add `FormatArtifactLinked` to `McpResultBuilder`. | `CreationTools.cs`, `McpResultBuilder.cs` | TO DO |
| T4: Unit tests | (new) | CLI command tests (happy path, duplicate, errors, all formats). MCP tool tests (happy path, duplicate, validation, errors). | `ArtifactLinkCommandTests.cs`, `CreationToolsLinkArtifactTests.cs` | TO DO |

#### Acceptance Criteria

- [ ] `twig link artifact <url> --name <name>` creates an artifact/hyperlink relation on the active work item
- [ ] `--id <id>` targets a specific work item instead of the active item
- [ ] Auto-detects `ArtifactLink` (vstfs://) vs `Hyperlink` (http/https) relation type
- [ ] Duplicate links return success with informational "already linked" message
- [ ] `twig_link_artifact` MCP tool provides equivalent functionality
- [ ] All output formats supported (human, json, json-compact, minimal)
- [ ] Unit tests cover happy path, duplicate detection, validation errors, and ADO failures

## PR Groups

### PG-1: Artifact Link Support (full feature)

| Field | Value |
|-------|-------|
| **Type** | Deep |
| **Tasks** | T1, T2, T3, T4 (all tasks from Issue #2016) |
| **Estimated LoC** | ~500 |
| **Estimated Files** | ~12 |
| **Predecessors** | None |

**Rationale:** The entire feature is cohesive and small enough for a single reviewable PR.
All four tasks are tightly coupled — the service layer change (T1) enables both surfaces
(T2 CLI, T3 MCP), and tests (T4) validate all layers. Splitting would create incomplete,
non-reviewable intermediate states.

**Review focus:** Verify 409 duplicate detection works correctly, auto-detection logic for
relation type, and that the new `IAdoWorkItemService` method signature is clean.

