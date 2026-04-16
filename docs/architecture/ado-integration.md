# ADO Integration Layer

How twig communicates with Azure DevOps: REST client, authentication,
work-item operations, field enrichment, conflict resolution, and link management.

---

## 1. ADO REST Client

### AdoRestClient

`Twig.Infrastructure/Ado/AdoRestClient.cs` — sealed internal class implementing
`IAdoWorkItemService`. All ADO HTTP traffic flows through this single class.

**API version:** `7.1` (comments use `7.1-preview.4`).

**Org URL normalisation:** bare org names (e.g. `my-org`) are expanded to
`https://dev.azure.com/my-org`; absolute URLs pass through unchanged.

### HTTP plumbing

The shared `HttpClient` is built in `NetworkServiceModule.CreateHttpClient()`:

| Setting | Value |
|---------|-------|
| HTTP version | 2.0 with 1.1 fallback (`RequestVersionOrLower`) |
| Decompression | GZip + Brotli via `SocketsHttpHandler` |
| Auth header | Applied per-request by `AdoErrorHandler.ApplyAuthHeader()` |
| Patch content type | `application/json-patch+json` |

Every outbound call goes through `SendAsync()`, which:

1. Builds an `HttpRequestMessage` with method, URL and optional content.
2. Attaches the auth header (PAT → `Basic`, az CLI → `Bearer`).
3. Sets `If-Match` when an expected revision is supplied (optimistic concurrency).
4. Catches `HttpRequestException` / timeout → wraps as `AdoOfflineException`.
5. Delegates response validation to `AdoErrorHandler.ThrowOnErrorAsync()`.

### Batch processing

ADO caps batch fetches at **200 items**. `FetchBatchAsync` splits larger lists
into chunks of 200, fetches sequentially, and concatenates the results.

---

## 2. Authentication

Two providers implement `IAuthenticationProvider` (domain interface, single
method `GetAccessTokenAsync`). The active provider is selected at DI
registration time based on `TwigConfiguration.Auth.Method`.

```
if method == "pat"  →  PatAuthProvider
else                →  AzCliAuthProvider   (default)
```

### Azure CLI provider (`AzCliAuthProvider`)

The default flow uses the Azure CLI as a token broker:

```
az account get-access-token --resource 499b84ac-...798 --query accessToken -o tsv
```

**Three-tier cache:**

| Tier | Location | TTL |
|------|----------|-----|
| In-memory | Instance field | 50 min |
| Cross-process file | `~/.twig/.token-cache` | Until expiry tick |
| Azure CLI subprocess | `az account get-access-token` | On cache miss |

The file cache uses atomic write (tmp + rename) and `0600` permissions on
Unix. Stdout and stderr are read concurrently to avoid pipe-buffer deadlocks.

**Platform resolution:** On Windows the provider searches `PATH` for `az.cmd`;
on Unix it shells to `az` directly.

**Subprocess timeout:** 10 seconds.

### PAT provider (`PatAuthProvider`)

Token resolution order:

1. `$TWIG_PAT` environment variable.
2. `.twig/config` field `auth.pat`.

The token is formatted as HTTP Basic auth: `Basic base64(":PAT")`.

---

## 3. Work Item Operations

### IAdoWorkItemService interface

```csharp
public interface IAdoWorkItemService
{
    // Fetch
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<(WorkItem, IReadOnlyList<WorkItemLink>)> FetchWithLinksAsync(int id, …);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, …);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, …);

    // Mutate
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, …);
    Task<int> CreateAsync(WorkItem seed, …);
    Task AddCommentAsync(int id, string text, …);

    // Query
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, …);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, …);

    // Links
    Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, …);
    Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, …);
}
```

### Fetch operations

| Method | Description |
|--------|-------------|
| `FetchAsync` | Single item with `$expand=relations` |
| `FetchWithLinksAsync` | Single item + mapped non-hierarchy links |
| `FetchChildrenAsync` | WIQL for children → batch fetch |
| `FetchBatchAsync` | POST to `_apis/wit/workitemsbatch`, chunked at 200 |

### Patch / Create

`PatchAsync` sends a JSON Patch document to
`_apis/wit/workitems/{id}?api-version=7.1`. An `If-Match` header carries the
expected revision for optimistic concurrency. The returned revision int is
used to update the local cache.

`CreateAsync` POSTs to `_apis/wit/workitems/$...` and captures the server-
assigned ID from the response.

### WIQL queries

The client serialises an `AdoWiqlRequest { Query }` and POSTs to
`_apis/wit/wiql`. The response (`AdoWiqlResponse`) returns a flat list of
work-item ID references. An optional `$top` parameter limits result count.

### DTOs

All wire types live in `Twig.Infrastructure/Ado/Dtos/` and use
`[JsonPropertyName]` attributes with source-generated serialisation
(`TwigJsonContext`):

- `AdoWorkItemResponse` — id, rev, fields dict, relations list
- `AdoPatchOperation` — op, path, value, from
- `AdoWiqlResponse` — queryType, workItems list
- `AdoBatchWorkItemResponse` — wraps `List<AdoWorkItemResponse>`

---

## 4. Field Enrichment

### Field definitions

`IIterationService.GetFieldDefinitionsAsync()` calls
`GET /{project}/_apis/wit/fields?api-version=7.1` and maps each entry to a
domain `FieldDefinition(ReferenceName, DisplayName, DataType, IsReadOnly)`.
Results are cached as a lazy-initialised task and stored in
`IFieldDefinitionStore` (SQLite) for offline access.

### Field import filter

`FieldImportFilter.ShouldImport(refName, fieldDef)` decides which ADO fields
are imported into a `WorkItem.Fields` dictionary:

| Rule | Action |
|------|--------|
| Core field (System.Id, Title, State, …) | Exclude — stored as first-class properties |
| No definition available | Import (safe fallback) |
| Display-worthy read-only (CreatedDate, Tags, …) | Import |
| Other read-only | Exclude |
| Importable data type (string, int, double, dateTime, html, plainText) | Import |
| Boolean | Exclude — cannot round-trip faithfully as string |

### Process configuration sync

`AdoIterationService.GetProcessConfigurationAsync()` fetches the project's
process configuration from
`GET /{project}/_apis/work/processconfiguration?api-version=7.1`, returning
backlog category mappings (task, requirement, portfolio, bug) with their
associated work-item types.

### Work-item type metadata

`GetWorkItemTypesWithStatesAsync()` fetches every work-item type and its
state definitions, including state category (`Proposed`, `InProgress`,
`Resolved`, `Completed`, `Removed`) and colour.

State ordering uses a stable sort: `OrderBy(CategoryRank).ThenBy(OriginalIndex)`.

### Template detection

Two-phase approach:

1. **API:** `GET /_apis/projects/{project}?includeCapabilities=true` → extract
   `ProcessTemplate.TemplateName`.
2. **Heuristic fallback:** Inspect fetched work-item type names:
   - "User Story" → Agile
   - "Product Backlog Item" → Scrum
   - "Requirement" → CMMI
   - Default → Basic

---

## 5. Conflict Resolution

### Error handler

`AdoErrorHandler.ThrowOnErrorAsync()` maps HTTP status codes to typed
exceptions:

| Status | Exception | Notes |
|--------|-----------|-------|
| 400 | `AdoBadRequestException` | Reads error message from body |
| 401 | `AdoAuthenticationException` | |
| 404 | `AdoNotFoundException` | Extracts work-item ID via regex |
| **412** | **`AdoConflictException`** | Parses server revision from body |
| 429 | `AdoRateLimitException` | Reads `Retry-After` header (default 10 s) |
| 5xx | `AdoServerException` | |

All inherit from `AdoException`. Network failures and timeouts become
`AdoOfflineException`.

### ConflictRetryHelper

`PatchWithRetryAsync(adoService, id, changes, expectedRevision, ct)`:

1. **Attempt 1:** Patch with expected revision.
2. **On 412:** Fetch the latest item to get the server's current revision.
3. **Attempt 2:** Patch again with the fresh revision.
4. **On second 412:** Exception propagates — no further retries.

This is a single-retry strategy; it handles the common case where another
actor (e.g. an ADO rule) bumped the revision between our fetch and patch.

### Revision parsing

`AdoConflictException` exposes `ServerRevision` (int), parsed from the error
body via regex `revision[:\s]+(\d+)` (case-insensitive). Returns 0 if
parsing fails.

---

## 6. Link Management

### Domain model

```csharp
public readonly record struct WorkItemLink(int SourceId, int TargetId, string LinkType);

public static class LinkTypes
{
    public const string Related     = "Related";
    public const string Predecessor = "Predecessor";
    public const string Successor   = "Successor";
}
```

`IWorkItemLinkRepository` provides `GetLinksAsync` / `SaveLinksAsync` backed
by SQLite.

### ADO relation type mapping

| ADO `rel` value | Domain `LinkType` |
|-----------------|-------------------|
| `System.LinkTypes.Hierarchy-Reverse` | Parent (stored as `WorkItem.ParentId`) |
| `System.LinkTypes.Related` | `Related` |
| `System.LinkTypes.Dependency-Forward` | `Successor` |
| `System.LinkTypes.Dependency-Reverse` | `Predecessor` |

Parent links are extracted by `AdoResponseMapper.ExtractParentId(relations)`,
which finds the hierarchy-reverse relation and parses the ID from the URL
suffix. Non-hierarchy links are extracted by `ExtractNonHierarchyLinks()`.

### Add link

`AddLinkAsync` sends a JSON Patch with `op: "add"`, `path: "/relations/-"`,
and a value containing the rel type and target URL:

```json
{
  "rel": "System.LinkTypes.Related",
  "url": "https://dev.azure.com/{org}/_apis/wit/workitems/{targetId}"
}
```

### Remove link

`RemoveLinkAsync` is a two-step operation:

1. **GET** the source item with `$expand=relations` to find the relation
   index and current revision.
2. **PATCH** with `op: "remove"`, `path: "/relations/{index}"`,
   and `If-Match: {rev}`.

The operation is idempotent — if the link doesn't exist, it returns silently.

---

## Iteration Service

`AdoIterationService : IIterationService` provides additional ADO metadata
queries beyond work items:

| Method | Endpoint |
|--------|----------|
| `GetCurrentIterationAsync` | `/{project}/{team}/_apis/work/teamsettings/iterations?$timeframe=current` |
| `GetTeamAreaPathsAsync` | `/{project}/{team}/_apis/work/teamsettings/teamfieldvalues` |
| `GetAuthenticatedUserDisplayNameAsync` | `https://app.vssps.visualstudio.com/_apis/profile/profiles/me` |
| `GetWorkItemTypesWithStatesAsync` | `/{project}/_apis/wit/workitemtypes` |
| `GetFieldDefinitionsAsync` | `/{project}/_apis/wit/fields` |
| `GetProcessConfigurationAsync` | `/{project}/_apis/work/processconfiguration` |

Results are lazily cached as tasks for the lifetime of the service instance
(singleton scope). Failures degrade gracefully — most methods return null or
empty lists rather than throwing.
